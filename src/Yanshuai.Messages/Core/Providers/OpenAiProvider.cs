// Core/Providers/OpenAiProvider.cs —— OpenAI 兼容接口
//
// 覆盖：OpenAI 本体、DeepSeek、Groq、Moonshot、GLM、Qwen、OpenRouter、本地 llama.cpp 等
// 所有走 /chat/completions 的服务。
//
// 两个容易踩的点：
//   1) 图片走 content 数组（[{type:"text"},{type:"image_url"}]），纯文本时用字符串，
//      不要总是发数组 —— 有些兼容端点不支持数组形式。
//   2) reasoning_content 默认不回传。OpenAI 官方接口没有这个字段，回传会被部分网关 400 拒绝；
//      需要它的中转通过 ChatRequest.SendReasoningBack 打开。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public sealed class OpenAiChatProvider : IChatProvider
    {
        private readonly IHttpTransport _http;
        private readonly ILogger _log;

        public OpenAiChatProvider(IHttpTransport http, ILogger log)
        {
            _http = http;
            _log = log;
        }

        // ── 非流式（工具循环走这条） ──────────────────────────────────────
        public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            HttpRequestSpec spec;
            try
            {
                spec = BuildRequest(request, false);
            }
            catch (Exception)
            {
                return ChatResult.Fail("build", "请求构造失败：模型档案里的自定义字段不是合法 JSON。");
            }

            HttpResponseSpec response;
            try
            {
                response = await ProviderHttp.SendWithRetryAsync(_http, spec, _log, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProviderNetworkException ex)
            {
                return ChatResult.Fail("network", "网络请求失败（" + ex.Reason + "）。检查网络或接口地址。");
            }

            if (!response.IsSuccess)
            {
                return ProviderErrors.FromStatus(response.StatusCode, response.Body);
            }

            return ParseResponse(response.Body);
        }

        // ── 流式（不用工具时走这条） ──────────────────────────────────────
        public async Task<ChatResult> StreamAsync(
            ChatRequest request, Action<StreamChunk> onChunk, CancellationToken ct)
        {
            var result = new ChatResult { Ok = true };
            var acc = new StreamAccumulator();
            var parser = new SseParser();
            var toolCalls = new Dictionary<int, ToolCallAccum>( );

            HttpRequestSpec spec;
            try
            {
                spec = BuildRequest(request, true);
            }
            catch (Exception)
            {
                return ChatResult.Fail("build", "请求构造失败：模型档案里的自定义字段不是合法 JSON。");
            }

            bool aborted = false;
            try
            {
                await _http.StreamLinesAsync(spec, delegate (string line)
                {
                    var evt = parser.Feed(line);
                    if (evt == null) return true;
                    if (evt.IsDone) return false;
                    if (string.IsNullOrEmpty(evt.Data)) return true;

                    JObject payload;
                    try
                    {
                        payload = JObject.Parse(evt.Data);
                    }
                    catch (Exception)
                    {
                        return true;   // 坏帧跳过，不中断整条流
                    }

                    // 错误帧：有些服务端会在流里返回 {"error":{...}}
                    var error = payload["error"];
                    if (error != null && error.Type != JTokenType.Null)
                    {
                        aborted = true;
                        return false;
                    }

                    var usage = payload["usage"];
                    if (usage != null && usage.Type != JTokenType.Null) ReadUsage(usage, result.Usage);

                    var choices = payload["choices"] as JArray;
                    if (choices == null || choices.Count == 0) return true;

                    var delta = choices[0]["delta"];
                    if (delta == null) return true;

                    var reasoning = FirstString(delta["reasoning_content"], delta["reasoning"]);
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        acc.AppendReasoning(reasoning);
                        if (onChunk != null)
                        {
                            onChunk(new StreamChunk { ReasoningDelta = reasoning });
                        }
                    }

                    var content = delta["content"];
                    if (content != null && content.Type == JTokenType.String)
                    {
                        var text = content.Value<string>();
                        if (!string.IsNullOrEmpty(text))
                        {
                            acc.AppendContent(text);
                            if (onChunk != null) onChunk(new StreamChunk { ContentDelta = text });
                        }
                    }

                    // 流式下若服务端仍给了 tool_calls 增量，攒起来（用于未来支持流式工具）
                    var calls = delta["tool_calls"] as JArray;
                    if (calls != null)
                    {
                        foreach (var c in calls) AccumulateToolCall(toolCalls, c);
                    }

                    return true;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户打断：把已经收到的内容保留下来
                result.Content = acc.Content;
                result.Reasoning = acc.Reasoning;
                result.Ok = true;
                return result;
            }
            catch (ProviderStreamException ex)
            {
                return ProviderErrors.FromStatus(ex.StatusCode, ex.BodyPreview);
            }
            catch (ProviderNetworkException ex)
            {
                return ChatResult.Fail("network", "网络请求失败（" + ex.Reason + "）。");
            }

            if (aborted)
            {
                return ChatResult.Fail("stream_error", "服务端在流中返回了错误。");
            }

            result.Content = acc.Content;
            result.Reasoning = acc.Reasoning;
            result.ToolCalls = FlushToolCalls(toolCalls);

            if (string.IsNullOrEmpty(result.Content) && result.ToolCalls.Count == 0)
            {
                result.Ok = false;
                result.ErrorCode = "empty";
                result.ErrorMessage = "服务端返回了空内容。";
            }
            return result;
        }

        // ── 请求构造 ─────────────────────────────────────────────────────

        public static HttpRequestSpec BuildRequest(ChatRequest request, bool stream)
        {
            var api = request.Api;
            var body = BuildBody(request, stream);

            var spec = new HttpRequestSpec
            {
                Method = HttpMethodKind.Post,
                Url = api.EffectiveUrl,
                Body = body.ToString(Formatting.None),
                TimeoutSeconds = stream ? 300 : 180
            };
            if (!string.IsNullOrEmpty(request.ApiKey)) spec.AddHeader("Authorization", "Bearer " + request.ApiKey);
            spec.AddHeader("Content-Type", "application/json");

            // 档案里的自定义请求头（中转站常用）
            if (api != null && api.CustomHeaders != null)
            {
                foreach (var kv in api.CustomHeaders)
                {
                    spec.AddHeader(kv.Key, kv.Value);
                }
            }
            return spec;
        }

        public static JObject BuildBody(ChatRequest request, bool stream)
        {
            var api = request.Api;
            var body = new JObject();
            body["model"] = string.IsNullOrEmpty(api.Model) ? "gpt-4o-mini" : api.Model;
            body["messages"] = BuildMessages(request);
            body["stream"] = stream;

            if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
            if (request.TopP.HasValue) body["top_p"] = request.TopP.Value;
            if (request.MaxTokens.HasValue && request.MaxTokens.Value > 0) body["max_tokens"] = request.MaxTokens.Value;

            // 推理强度：只在用户显式选了 low/medium/high 时才发。
            // auto 不发 —— 部分中转不认识这个字段，带了会被 400 拒绝。
            if (!string.IsNullOrEmpty(request.ReasoningEffort)
                && !string.Equals(request.ReasoningEffort, "auto", StringComparison.OrdinalIgnoreCase))
            {
                body["reasoning_effort"] = request.ReasoningEffort;
            }

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = new JArray();
                foreach (var t in request.Tools)
                {
                    tools.Add(new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["parameters"] = BuildParametersSchema(t)
                        }
                    });
                }
                body["tools"] = tools;
                body["tool_choice"] = "auto";
            }

            // 档案里的自定义请求体字段（覆盖式）
            if (api != null && api.CustomBody != null)
            {
                foreach (var kv in api.CustomBody)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    body[kv.Key] = ConvertValue(kv.Value);
                }
            }
            if (request.ExtraBody != null)
            {
                foreach (var kv in request.ExtraBody)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    body[kv.Key] = ConvertValue(kv.Value);
                }
            }

            // The selected model is authoritative even if an older custom body
            // still contains the provider's previous default model.
            body["model"] = string.IsNullOrEmpty(api.Model) ? "gpt-4o-mini" : api.Model;
            return body;
        }

        private static JToken ConvertValue(object value)
        {
            if (value == null) return JValue.CreateNull();
            var token = value as JToken;
            if (token != null) return token;
            return JToken.FromObject(value);
        }

        public static JArray BuildMessages(ChatRequest request)
        {
            var messages = new JArray();

            foreach (var turn in request.Turns)
            {
                if (string.Equals(turn.Role, "tool", StringComparison.Ordinal))
                {
                    messages.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = turn.ToolCallId ?? "",
                        ["content"] = turn.Content ?? ""
                    });
                    continue;
                }

                if (string.Equals(turn.Role, "assistant", StringComparison.Ordinal))
                {
                    var msg = new JObject();
                    msg["role"] = "assistant";
                    msg["content"] = turn.Content ?? "";

                    if (turn.ToolCalls != null && turn.ToolCalls.Count > 0)
                    {
                        var calls = new JArray();
                        foreach (var call in turn.ToolCalls)
                        {
                            calls.Add(new JObject
                            {
                                ["id"] = string.IsNullOrEmpty(call.Id) ? MakeCallId() : call.Id,
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = call.Name ?? "",
                                    ["arguments"] = string.IsNullOrEmpty(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                                }
                            });
                        }
                        msg["tool_calls"] = calls;
                    }

                    if (request.SendReasoningBack && !string.IsNullOrEmpty(turn.Reasoning))
                    {
                        msg["reasoning_content"] = turn.Reasoning;
                    }

                    messages.Add(msg);
                    continue;
                }

                // system / user
                var isUser = string.Equals(turn.Role, "user", StringComparison.Ordinal);
                bool hasImages = request.VisionEnabled && turn.Images != null && turn.Images.Count > 0;

                if (hasImages)
                {
                    var parts = new JArray();
                    if (!string.IsNullOrEmpty(turn.Content))
                    {
                        parts.Add(new JObject { ["type"] = "text", ["text"] = turn.Content });
                    }
                    foreach (var img in turn.Images)
                    {
                        parts.Add(new JObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JObject
                            {
                                ["url"] = "data:" + (img.MimeType ?? "image/png") + ";base64," + (img.Base64 ?? "")
                            }
                        });
                    }
                    messages.Add(new JObject
                    {
                        ["role"] = isUser ? "user" : "system",
                        ["content"] = parts
                    });
                }
                else
                {
                    messages.Add(new JObject
                    {
                        ["role"] = isUser ? "user" : "system",
                        ["content"] = turn.Content ?? ""
                    });
                }
            }

            return messages;
        }

        public static JObject BuildParametersSchema(ToolSchema tool)
        {
            var properties = new JObject();
            var required = new JArray();

            foreach (var p in tool.Parameters)
            {
                var prop = new JObject
                {
                    ["type"] = string.IsNullOrEmpty(p.Type) ? "string" : p.Type
                };
                if (!string.IsNullOrEmpty(p.Description)) prop["description"] = p.Description;
                if (p.Enum != null && p.Enum.Count > 0)
                {
                    var arr = new JArray();
                    foreach (var v in p.Enum) arr.Add(v);
                    prop["enum"] = arr;
                }
                properties[p.Name] = prop;
                if (p.Required) required.Add(p.Name);
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }

        // ── 响应解析 ─────────────────────────────────────────────────────

        public static ChatResult ParseResponse(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return ChatResult.Fail("empty", "服务端返回了空响应。");
            }

            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch (Exception)
            {
                return ChatResult.Fail("parse", "无法解析服务端响应。可能这个地址不是 OpenAI 兼容接口。");
            }

            var error = root["error"];
            if (error != null && error.Type != JTokenType.Null)
            {
                var message = error["message"] != null ? error["message"].ToString() : error.ToString();
                var result = ChatResult.Fail("api_error", "服务端报错：" + TextUtil.Truncate(TextUtil.Normalize(message), 200));
                return result;
            }

            var choices = root["choices"] as JArray;
            if (choices == null || choices.Count == 0)
            {
                return ChatResult.Fail("empty", "服务端没有返回任何候选结果。");
            }

            var message_ = choices[0]["message"];
            if (message_ == null)
            {
                return ChatResult.Fail("parse", "响应结构不完整（缺少 message）。");
            }

            var parsed = new ChatResult { Ok = true };
            parsed.Content = message_["content"] != null && message_["content"].Type == JTokenType.String
                ? message_["content"].Value<string>()
                : "";
            parsed.Reasoning = FirstString(message_["reasoning_content"], message_["reasoning"]) ?? "";

            var calls = message_["tool_calls"] as JArray;
            if (calls != null)
            {
                foreach (var c in calls)
                {
                    var fn = c["function"];
                    if (fn == null) continue;
                    parsed.ToolCalls.Add(new ToolCallRequest
                    {
                        Id = c["id"] != null ? c["id"].ToString() : MakeCallId(),
                        Name = fn["name"] != null ? fn["name"].ToString() : "",
                        ArgumentsJson = fn["arguments"] != null ? fn["arguments"].ToString() : "{}"
                    });
                }
            }

            var usage = root["usage"];
            if (usage != null && usage.Type != JTokenType.Null) ReadUsage(usage, parsed.Usage);

            if (string.IsNullOrEmpty(parsed.Content) && parsed.ToolCalls.Count == 0)
            {
                return ChatResult.Fail("empty", "模型返回了空内容。");
            }
            return parsed;
        }

        private static void ReadUsage(JToken usage, UsageInfo target)
        {
            if (usage == null || target == null) return;

            int input = ReadInt(usage["prompt_tokens"], ReadInt(usage["input_tokens"], 0));
            int output = ReadInt(usage["completion_tokens"], ReadInt(usage["output_tokens"], 0));
            target.InputTokens = input;
            target.OutputTokens = output;

            var details = usage["prompt_tokens_details"];
            if (details != null)
            {
                target.CachedTokens = ReadInt(details["cached_tokens"], 0);
            }
            if (target.CachedTokens == 0)
            {
                target.CachedTokens = ReadInt(usage["cache_read_input_tokens"], 0);
            }
        }

        private static int ReadInt(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            int v;
            if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static string FirstString(JToken a, JToken b)
        {
            if (a != null && a.Type == JTokenType.String) return a.Value<string>();
            if (b != null && b.Type == JTokenType.String) return b.Value<string>();
            return null;
        }

        private static string MakeCallId()
        {
            return "call_" + Guid.NewGuid().ToString("N").Substring(0, 20);
        }

        // ── 流式工具调用增量（为将来"流式 + 工具"预留） ────────────────────

        private sealed class ToolCallAccum
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new StringBuilder();
        }

        private static void AccumulateToolCall(Dictionary<int, ToolCallAccum> map, JToken call)
        {
            int index = 0;
            if (call["index"] != null)
            {
                int.TryParse(call["index"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
            }

            ToolCallAccum accum;
            if (!map.TryGetValue(index, out accum))
            {
                accum = new ToolCallAccum();
                map[index] = accum;
            }

            if (call["id"] != null && call["id"].Type == JTokenType.String) accum.Id = call["id"].Value<string>();
            var fn = call["function"];
            if (fn != null)
            {
                if (fn["name"] != null && fn["name"].Type == JTokenType.String && accum.Name.Length == 0)
                {
                    accum.Name = fn["name"].Value<string>();
                }
                if (fn["arguments"] != null && fn["arguments"].Type == JTokenType.String)
                {
                    accum.Args.Append(fn["arguments"].Value<string>());
                }
            }
        }

        private static List<ToolCallRequest> FlushToolCalls(Dictionary<int, ToolCallAccum> map)
        {
            var list = new List<ToolCallRequest>();
            var keys = new List<int>(map.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var accum = map[key];
                if (string.IsNullOrEmpty(accum.Name)) continue;
                list.Add(new ToolCallRequest
                {
                    Id = string.IsNullOrEmpty(accum.Id) ? MakeCallId() : accum.Id,
                    Name = accum.Name,
                    ArgumentsJson = accum.Args.Length == 0 ? "{}" : accum.Args.ToString()
                });
            }
            return list;
        }
    }
}
