// Core/Providers/AnthropicProvider.cs —— Anthropic 原生 Messages API
//
// 与 OpenAI 形态的差异，全部在这里消化：
//   1) system 不是消息，是顶层字段。
//   2) 消息只有 user / assistant 两种角色。工具结果不是独立消息，而是塞进
//      user 消息里的 tool_result 块 —— 而且**同一轮的多个工具结果必须合并在同一条
//      user 消息里**，拆开发会被 400 拒绝。
//   3) 鉴权用 x-api-key，并必须带 anthropic-version。
//   4) max_tokens 是必填项，不填直接报错。
//   5) 工具参数用 input_schema 而不是 parameters；参数是 JSON 对象不是字符串，
//      回填时要把对象序列化成字符串给上层。

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
    public sealed class AnthropicChatProvider : IChatProvider
    {
        public const string ApiVersion = "2023-06-01";
        public const int DefaultMaxTokens = 8192;

        private readonly IHttpTransport _http;
        private readonly ILogger _log;

        public AnthropicChatProvider(IHttpTransport http, ILogger log)
        {
            _http = http;
            _log = log;
        }

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
                return ChatResult.Fail("network", "网络请求失败（" + ex.Reason + "）。");
            }

            if (!response.IsSuccess)
            {
                return ProviderErrors.FromStatus(response.StatusCode, response.Body);
            }

            return ParseResponse(response.Body);
        }

        public async Task<ChatResult> StreamAsync(
            ChatRequest request, Action<StreamChunk> onChunk, CancellationToken ct)
        {
            var result = new ChatResult { Ok = true };
            var acc = new StreamAccumulator();
            var parser = new SseParser();
            var toolBuffers = new Dictionary<int, ToolUseBuffer>();
            bool aborted = false;

            HttpRequestSpec spec;
            try
            {
                spec = BuildRequest(request, true);
            }
            catch (Exception)
            {
                return ChatResult.Fail("build", "请求构造失败。");
            }

            try
            {
                await _http.StreamLinesAsync(spec, delegate (string line)
                {
                    var evt = parser.Feed(line);
                    if (evt == null) return true;
                    if (string.IsNullOrEmpty(evt.Data)) return true;

                    JObject payload;
                    try
                    {
                        payload = JObject.Parse(evt.Data);
                    }
                    catch (Exception)
                    {
                        return true;
                    }

                    var type = payload["type"] != null ? payload["type"].ToString() : "";

                    if (type == "error")
                    {
                        aborted = true;
                        return false;
                    }

                    if (type == "message_start")
                    {
                        var usage = payload["message"] != null ? payload["message"]["usage"] : null;
                        if (usage != null) ReadUsage(usage, result.Usage);
                        return true;
                    }

                    if (type == "content_block_start")
                    {
                        int index = ReadIndex(payload["index"]);
                        var block = payload["content_block"];
                        if (block != null && block["type"] != null &&
                            string.Equals(block["type"].ToString(), "tool_use", StringComparison.Ordinal))
                        {
                            toolBuffers[index] = new ToolUseBuffer
                            {
                                Id = block["id"] != null ? block["id"].ToString() : "",
                                Name = block["name"] != null ? block["name"].ToString() : ""
                            };
                        }
                        return true;
                    }

                    if (type == "content_block_delta")
                    {
                        int index = ReadIndex(payload["index"]);
                        var delta = payload["delta"];
                        if (delta == null) return true;
                        var deltaType = delta["type"] != null ? delta["type"].ToString() : "";

                        if (string.Equals(deltaType, "text_delta", StringComparison.Ordinal))
                        {
                            var text = delta["text"] != null ? delta["text"].ToString() : "";
                            if (text.Length > 0)
                            {
                                acc.AppendContent(text);
                                if (onChunk != null) onChunk(new StreamChunk { ContentDelta = text });
                            }
                        }
                        else if (string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
                        {
                            var thinking = delta["thinking"] != null ? delta["thinking"].ToString() : "";
                            if (thinking.Length > 0)
                            {
                                acc.AppendReasoning(thinking);
                                if (onChunk != null) onChunk(new StreamChunk { ReasoningDelta = thinking });
                            }
                        }
                        else if (string.Equals(deltaType, "input_json_delta", StringComparison.Ordinal))
                        {
                            ToolUseBuffer buffer;
                            if (toolBuffers.TryGetValue(index, out buffer))
                            {
                                var partial = delta["partial_json"] != null ? delta["partial_json"].ToString() : "";
                                buffer.Json.Append(partial);
                            }
                        }
                        return true;
                    }

                    if (type == "message_delta")
                    {
                        var usage = payload["usage"];
                        if (usage != null) ReadUsage(usage, result.Usage, true);
                        return true;
                    }

                    if (type == "message_stop") return false;

                    return true;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
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

            if (aborted) return ChatResult.Fail("stream_error", "服务端在流中返回了错误。");

            result.Content = acc.Content;
            result.Reasoning = acc.Reasoning;

            var keys = new List<int>(toolBuffers.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var buffer = toolBuffers[key];
                if (string.IsNullOrEmpty(buffer.Name)) continue;
                result.ToolCalls.Add(new ToolCallRequest
                {
                    Id = buffer.Id,
                    Name = buffer.Name,
                    ArgumentsJson = buffer.Json.Length == 0 ? "{}" : ToolArguments.RepairTruncated(buffer.Json.ToString())
                });
            }

            if (string.IsNullOrEmpty(result.Content) && result.ToolCalls.Count == 0)
            {
                return ChatResult.Fail("empty", "服务端返回了空内容。");
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
            spec.AddHeader("x-api-key", request.ApiKey ?? "");
            spec.AddHeader("anthropic-version", ApiVersion);
            spec.AddHeader("Content-Type", "application/json");

            if (api != null && api.CustomHeaders != null)
            {
                foreach (var kv in api.CustomHeaders) spec.AddHeader(kv.Key, kv.Value);
            }
            return spec;
        }

        public static JObject BuildBody(ChatRequest request, bool stream)
        {
            var api = request.Api;
            var body = new JObject();
            body["model"] = string.IsNullOrEmpty(api.Model) ? "claude-sonnet-4-20250514" : api.Model;

            // max_tokens 必填
            int maxTokens = request.MaxTokens.HasValue && request.MaxTokens.Value > 0
                ? request.MaxTokens.Value
                : DefaultMaxTokens;
            body["max_tokens"] = maxTokens;

            var systemText = CollectSystem(request);
            if (!string.IsNullOrEmpty(systemText)) body["system"] = systemText;

            body["messages"] = BuildMessages(request);
            if (stream) body["stream"] = true;

            if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
            if (request.TopP.HasValue) body["top_p"] = request.TopP.Value;

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = new JArray();
                foreach (var t in request.Tools)
                {
                    tools.Add(new JObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["input_schema"] = OpenAiChatProvider.BuildParametersSchema(t)
                    });
                }
                body["tools"] = tools;
            }

            if (api != null && api.CustomBody != null)
            {
                foreach (var kv in api.CustomBody)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    body[kv.Key] = kv.Value == null ? JValue.CreateNull() : JToken.FromObject(kv.Value);
                }
            }
            if (request.ExtraBody != null)
            {
                foreach (var kv in request.ExtraBody)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    body[kv.Key] = kv.Value == null ? JValue.CreateNull() : JToken.FromObject(kv.Value);
                }
            }

            body["model"] = string.IsNullOrEmpty(api.Model) ? "claude-sonnet-4-20250514" : api.Model;
            return body;
        }

        private static string CollectSystem(ChatRequest request)
        {
            var sb = new StringBuilder();
            foreach (var turn in request.Turns)
            {
                if (!string.Equals(turn.Role, "system", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(turn.Content)) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(turn.Content);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 构造 messages。核心难点是工具结果的合并：
        /// Anthropic 要求 tool_result 块紧跟对应的 tool_use，且同一轮的多个结果
        /// 必须在同一条 user 消息里。所以这里把连续的 tool turn 攒起来一起发。
        /// </summary>
        public static JArray BuildMessages(ChatRequest request)
        {
            var messages = new JArray();
            var pendingToolResults = new JArray();

            Action flushToolResults = delegate ()
            {
                if (pendingToolResults.Count == 0) return;
                messages.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = pendingToolResults.DeepClone()
                });
                pendingToolResults = new JArray();
            };

            foreach (var turn in request.Turns)
            {
                if (string.Equals(turn.Role, "system", StringComparison.Ordinal)) continue;

                if (string.Equals(turn.Role, "tool", StringComparison.Ordinal))
                {
                    var block = new JObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = turn.ToolCallId ?? "",
                        ["content"] = turn.Content ?? ""
                    };
                    pendingToolResults.Add(block);
                    continue;
                }

                flushToolResults();

                if (string.Equals(turn.Role, "assistant", StringComparison.Ordinal))
                {
                    var blocks = new JArray();
                    if (!string.IsNullOrEmpty(turn.Content))
                    {
                        blocks.Add(new JObject { ["type"] = "text", ["text"] = turn.Content });
                    }
                    if (turn.ToolCalls != null)
                    {
                        foreach (var call in turn.ToolCalls)
                        {
                            JToken input;
                            var args = call.ArgumentsJson;
                            if (string.IsNullOrEmpty(args) || !ToolArguments.IsValidJson(args))
                            {
                                args = ToolArguments.RepairTruncated(args);
                            }
                            try
                            {
                                input = JToken.Parse(args);
                            }
                            catch (Exception)
                            {
                                input = new JObject();
                            }

                            blocks.Add(new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = string.IsNullOrEmpty(call.Id) ? ("toolu_" + Guid.NewGuid().ToString("N").Substring(0, 20)) : call.Id,
                                ["name"] = call.Name ?? "",
                                ["input"] = input
                            });
                        }
                    }

                    // 内容全空会让 Anthropic 报错，补一个空文本块
                    if (blocks.Count == 0) blocks.Add(new JObject { ["type"] = "text", ["text"] = "" });

                    messages.Add(new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = blocks
                    });
                    continue;
                }

                // user
                var userBlocks = new JArray();
                if (!string.IsNullOrEmpty(turn.Content))
                {
                    userBlocks.Add(new JObject { ["type"] = "text", ["text"] = turn.Content });
                }
                if (request.VisionEnabled && turn.Images != null)
                {
                    foreach (var img in turn.Images)
                    {
                        userBlocks.Add(new JObject
                        {
                            ["type"] = "image",
                            ["source"] = new JObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = img.MimeType ?? "image/png",
                                ["data"] = img.Base64 ?? ""
                            }
                        });
                    }
                }
                if (userBlocks.Count == 0) userBlocks.Add(new JObject { ["type"] = "text", ["text"] = "" });

                messages.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = userBlocks
                });
            }

            flushToolResults();
            return messages;
        }

        // ── 响应解析 ─────────────────────────────────────────────────────

        public static ChatResult ParseResponse(string body)
        {
            if (string.IsNullOrEmpty(body)) return ChatResult.Fail("empty", "服务端返回了空响应。");

            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch (Exception)
            {
                return ChatResult.Fail("parse", "无法解析服务端响应。可能这个地址不是 Anthropic Messages 接口。");
            }

            if (root["type"] != null && string.Equals(root["type"].ToString(), "error", StringComparison.Ordinal))
            {
                var err = root["error"];
                var message = err != null && err["message"] != null ? err["message"].ToString() : root.ToString();
                return ChatResult.Fail("api_error", "服务端报错：" + TextUtil.Truncate(TextUtil.Normalize(message), 200));
            }

            var content = root["content"] as JArray;
            if (content == null) return ChatResult.Fail("parse", "响应结构不完整（缺少 content）。");

            var result = new ChatResult { Ok = true };
            var text = new StringBuilder();
            var thinking = new StringBuilder();

            foreach (var block in content)
            {
                var type = block["type"] != null ? block["type"].ToString() : "";
                if (string.Equals(type, "text", StringComparison.Ordinal))
                {
                    if (block["text"] != null) text.Append(block["text"].ToString());
                }
                else if (string.Equals(type, "thinking", StringComparison.Ordinal))
                {
                    if (block["thinking"] != null) thinking.Append(block["thinking"].ToString());
                }
                else if (string.Equals(type, "tool_use", StringComparison.Ordinal))
                {
                    result.ToolCalls.Add(new ToolCallRequest
                    {
                        Id = block["id"] != null ? block["id"].ToString() : "",
                        Name = block["name"] != null ? block["name"].ToString() : "",
                        ArgumentsJson = block["input"] == null ? "{}" : block["input"].ToString(Formatting.None)
                    });
                }
            }

            result.Content = text.ToString();
            result.Reasoning = thinking.ToString();

            var usage = root["usage"];
            if (usage != null) ReadUsage(usage, result.Usage);

            if (string.IsNullOrEmpty(result.Content) && result.ToolCalls.Count == 0)
            {
                return ChatResult.Fail("empty", "模型返回了空内容。");
            }
            return result;
        }

        private static void ReadUsage(JToken usage, UsageInfo target, bool isDelta = false)
        {
            if (usage == null || target == null) return;

            if (usage["input_tokens"] != null)
            {
                int v = ReadIndex(usage["input_tokens"]);
                // message_delta 里的 input_tokens 往往是增量语义，做累加更安全
                target.InputTokens = isDelta ? target.InputTokens + v : v;
            }
            if (usage["output_tokens"] != null)
            {
                int v = ReadIndex(usage["output_tokens"]);
                target.OutputTokens = isDelta ? target.OutputTokens + v : v;
            }
            if (usage["cache_read_input_tokens"] != null) target.CachedTokens = ReadIndex(usage["cache_read_input_tokens"]);
        }

        private static int ReadIndex(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            int v;
            if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private sealed class ToolUseBuffer
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Json = new StringBuilder();
        }
    }
}
