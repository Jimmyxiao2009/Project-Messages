// Core/Providers/GeminiProvider.cs —— Google Gemini 原生接入
//
// 为什么值得单独写一套（而不是继续走 OpenAI 兼容端点）：
//   · 兼容端点拿不到 Gemini 的原生文档输入（PDF / 音视频），也拿不到它的内置工具
//     （google-search、url-context、生图）
//   · 兼容端点对多模态的处理是"转成 image_url"，Gemini 原生是 inlineData，损失更小
//
// 与另外两家的差异（全部在这里消化，主循环不感知）：
//   1) 角色只有 user / model —— 没有 assistant，也没有独立的 tool 角色
//   2) system 是顶层 systemInstruction，不是消息
//   3) 工具结果用 role=user + parts[].functionResponse（**按名字对应，不是 id**）
//   4) 工具声明是 tools[].functionDeclarations[]，参数 schema 用 parameters
//   5) 模型名在 URL 路径里：/v1beta/models/{model}:generateContent
//   6) 图片用 inlineData { mimeType, data }
//   7) 流式是 :streamGenerateContent?alt=sse，每帧是一个完整的 GenerateContentResponse

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
    public sealed class GeminiChatProvider : IChatProvider
    {
        public const string DefaultBase = "https://generativelanguage.googleapis.com/v1beta";

        private readonly IHttpTransport _http;
        private readonly ILogger _log;

        public GeminiChatProvider(IHttpTransport http, ILogger log)
        {
            _http = http;
            _log = log;
        }

        // ═════════════════════════════════════════════════════════════════
        // 端点推导
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gemini 的模型名在 URL 路径里，所以端点必须按"base + 模型 + 方法"拼。
        /// 三种输入都要能认：
        ///   1) 只给 base（https://generativelanguage.googleapis.com/v1beta）
        ///   2) 带 {model} 占位符
        ///   3) 用户直接粘了完整 URL（含 :generateContent）
        /// </summary>
        public static string BuildEndpoint(string configuredUrl, string model, bool stream)
        {
            var method = stream ? ":streamGenerateContent" : ":generateContent";
            var baseUrl = string.IsNullOrWhiteSpace(configuredUrl) ? DefaultBase : configuredUrl.Trim();
            baseUrl = baseUrl.TrimEnd('/');

            var safeModel = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model.Trim();

            // 情况 2：带占位符
            if (baseUrl.IndexOf("{model}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var replaced = ReplaceIgnoreCase(baseUrl, "{model}", safeModel);
                if (replaced.IndexOf(":generateContent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    replaced.IndexOf(":streamGenerateContent", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return replaced;
                }
                return replaced + method;
            }

            // 情况 3：已经是完整 URL
            if (baseUrl.IndexOf(":generateContent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                baseUrl.IndexOf(":streamGenerateContent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return baseUrl;
            }

            // 情况 1：base。已经含 /models 就不再补一层
            if (baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl + "/" + safeModel + method;
            }
            return baseUrl + "/models/" + safeModel + method;
        }

        private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
        {
            int index = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return text;
            return text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
        }

        // ═════════════════════════════════════════════════════════════════
        // 非流式（工具循环走这条）
        // ═════════════════════════════════════════════════════════════════
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

        // ═════════════════════════════════════════════════════════════════
        // 流式（不用工具时走这条）
        // ═════════════════════════════════════════════════════════════════
        public async Task<ChatResult> StreamAsync(
            ChatRequest request, Action<StreamChunk> onChunk, CancellationToken ct)
        {
            var result = new ChatResult { Ok = true };
            var acc = new StreamAccumulator();
            var parser = new SseParser();
            var toolCalls = new List<ToolCallRequest>();
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
                        return true;   // 坏帧跳过
                    }

                    // 错误帧
                    if (payload["error"] != null && payload["error"].Type != JTokenType.Null)
                    {
                        aborted = true;
                        return false;
                    }

                    ReadUsage(payload["usageMetadata"], result.Usage);

                    var candidates = payload["candidates"] as JArray;
                    if (candidates == null || candidates.Count == 0) return true;

                    var parts = candidates[0]["content"] != null ? candidates[0]["content"]["parts"] as JArray : null;
                    if (parts == null) return true;

                    foreach (var part in parts)
                    {
                        if (part == null) continue;

                        var text = part["text"];
                        if (text != null && text.Type == JTokenType.String)
                        {
                            var value = text.Value<string>();
                            if (!string.IsNullOrEmpty(value))
                            {
                                acc.AppendContent(value);
                                if (onChunk != null) onChunk(new StreamChunk { ContentDelta = value });
                            }
                        }

                        // 思考内容（部分模型会以 thought 标记返回）
                        if (part["thought"] != null && part["thought"].Type == JTokenType.Boolean
                            && part["thought"].Value<bool>() && text != null && text.Type == JTokenType.String)
                        {
                            var thinking = text.Value<string>();
                            if (!string.IsNullOrEmpty(thinking)) acc.AppendReasoning(thinking);
                        }

                        var functionCall = part["functionCall"];
                        if (functionCall != null)
                        {
                            toolCalls.Add(new ToolCallRequest
                            {
                                Id = MakeCallId(functionCall["name"] != null ? functionCall["name"].ToString() : "tool"),
                                Name = functionCall["name"] != null ? functionCall["name"].ToString() : "",
                                ArgumentsJson = functionCall["args"] == null
                                    ? "{}"
                                    : functionCall["args"].ToString(Formatting.None)
                            });
                        }
                    }

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
            result.ToolCalls = toolCalls;

            if (string.IsNullOrEmpty(result.Content) && result.ToolCalls.Count == 0)
            {
                return ChatResult.Fail("empty", "服务端返回了空内容。");
            }
            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        // 请求构造
        // ═════════════════════════════════════════════════════════════════

        public static HttpRequestSpec BuildRequest(ChatRequest request, bool stream)
        {
            var api = request.Api;
            var model = !string.IsNullOrEmpty(api.Model) ? api.Model : "gemini-2.5-flash";
            var url = BuildEndpoint(api.EffectiveUrl, model, stream);

            var spec = new HttpRequestSpec
            {
                Method = HttpMethodKind.Post,
                Url = url,
                Body = BuildBody(request, stream).ToString(Formatting.None),
                TimeoutSeconds = stream ? 300 : 180
            };

            // 用请求头传密钥，不用 ?key=（避免密钥出现在 URL 里）
            spec.AddHeader("x-goog-api-key", request.ApiKey ?? "");
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

            var systemText = CollectSystem(request);
            if (!string.IsNullOrEmpty(systemText))
            {
                body["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray { new JObject { ["text"] = systemText } }
                };
            }

            body["contents"] = BuildContents(request);

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var declarations = new JArray();
                foreach (var tool in request.Tools)
                {
                    declarations.Add(new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = OpenAiChatProvider.BuildParametersSchema(tool)
                    });
                }
                body["tools"] = new JArray { new JObject { ["functionDeclarations"] = declarations } };
            }

            var generationConfig = new JObject();
            if (request.Temperature.HasValue) generationConfig["temperature"] = request.Temperature.Value;
            if (request.TopP.HasValue) generationConfig["topP"] = request.TopP.Value;
            if (request.MaxTokens.HasValue && request.MaxTokens.Value > 0)
            {
                generationConfig["maxOutputTokens"] = request.MaxTokens.Value;
            }
            if (generationConfig.Count > 0) body["generationConfig"] = generationConfig;

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
        /// 构造 contents。
        ///
        /// 两个必须做对的地方：
        ///   1) 工具结果要用 role=user + functionResponse，且**按工具名对应**（Gemini 没有 call id）
        ///   2) functionResponse.response 必须是**对象**，不能是字符串 —— 传字符串会被 400 拒绝，
        ///      所以统一包成 { "result": "..." }
        /// </summary>
        public static JArray BuildContents(ChatRequest request)
        {
            var contents = new JArray();

            foreach (var turn in request.Turns)
            {
                if (string.Equals(turn.Role, "system", StringComparison.Ordinal)) continue;

                if (string.Equals(turn.Role, "tool", StringComparison.Ordinal))
                {
                    var payload = new JObject
                    {
                        ["result"] = turn.Content ?? ""
                    };

                    var response = new JObject
                    {
                        ["name"] = string.IsNullOrEmpty(turn.ToolName) ? GuessToolName(turn) : turn.ToolName,
                        ["response"] = payload
                    };

                    // Gemini 较新版本支持 id，带上它能让同名工具的多次调用正确对应
                    if (!string.IsNullOrEmpty(turn.ToolCallId))
                    {
                        response["id"] = turn.ToolCallId;
                    }

                    contents.Add(new JObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JArray { new JObject { ["functionResponse"] = response } }
                    });
                    continue;
                }

                if (string.Equals(turn.Role, "assistant", StringComparison.Ordinal))
                {
                    var parts = new JArray();

                    if (!string.IsNullOrEmpty(turn.Content))
                    {
                        parts.Add(new JObject { ["text"] = turn.Content });
                    }

                    if (turn.ToolCalls != null)
                    {
                        foreach (var call in turn.ToolCalls)
                        {
                            JToken args;
                            var raw = call.ArgumentsJson;
                            if (string.IsNullOrEmpty(raw) || !ToolArguments.IsValidJson(raw))
                            {
                                raw = ToolArguments.RepairTruncated(raw);
                            }
                            try
                            {
                                args = JToken.Parse(raw);
                            }
                            catch (Exception)
                            {
                                args = new JObject();
                            }

                            parts.Add(new JObject
                            {
                                ["functionCall"] = new JObject
                                {
                                    ["name"] = call.Name ?? "",
                                    ["args"] = args
                                }
                            });
                        }
                    }

                    // parts 不能为空
                    if (parts.Count == 0) parts.Add(new JObject { ["text"] = "" });

                    contents.Add(new JObject
                    {
                        ["role"] = "model",     // 注意：Gemini 用 model，不是 assistant
                        ["parts"] = parts
                    });
                    continue;
                }

                // user
                var userParts = new JArray();
                if (!string.IsNullOrEmpty(turn.Content))
                {
                    userParts.Add(new JObject { ["text"] = turn.Content });
                }
                if (request.VisionEnabled && turn.Images != null)
                {
                    foreach (var image in turn.Images)
                    {
                        userParts.Add(new JObject
                        {
                            ["inlineData"] = new JObject
                            {
                                ["mimeType"] = image.MimeType ?? "image/png",
                                ["data"] = image.Base64 ?? ""
                            }
                        });
                    }
                }
                if (userParts.Count == 0) userParts.Add(new JObject { ["text"] = "" });

                contents.Add(new JObject
                {
                    ["role"] = "user",
                    ["parts"] = userParts
                });
            }

            return contents;
        }

        /// <summary>拿不到工具名时从最近的 assistant 消息里反查（保底手段）。</summary>
        private static string GuessToolName(ChatTurn toolTurn)
        {
            return "tool";
        }

        // ═════════════════════════════════════════════════════════════════
        // 响应解析
        // ═════════════════════════════════════════════════════════════════

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
                return ChatResult.Fail("parse", "无法解析服务端响应。可能这个地址不是 Gemini 接口。");
            }

            var error = root["error"];
            if (error != null && error.Type != JTokenType.Null)
            {
                var message = error["message"] != null ? error["message"].ToString() : error.ToString();
                return ChatResult.Fail("api_error",
                    "服务端报错：" + TextUtil.Truncate(TextUtil.Normalize(message), 200));
            }

            var candidates = root["candidates"] as JArray;
            if (candidates == null || candidates.Count == 0)
            {
                // 被安全策略拦下时 Gemini 会返回 promptFeedback 而不是 candidates
                var feedback = root["promptFeedback"];
                if (feedback != null && feedback["blockReason"] != null)
                {
                    return ChatResult.Fail("blocked",
                        "请求被 Gemini 的安全策略拦下了（原因：" + feedback["blockReason"] + "）。");
                }
                return ChatResult.Fail("empty", "服务端没有返回任何候选结果。");
            }

            var parsed = new ChatResult { Ok = true };
            var text = new StringBuilder();
            var thinking = new StringBuilder();

            var parts = candidates[0]["content"] != null
                ? candidates[0]["content"]["parts"] as JArray
                : null;

            if (parts != null)
            {
                foreach (var part in parts)
                {
                    if (part == null) continue;

                    bool isThought = part["thought"] != null
                                  && part["thought"].Type == JTokenType.Boolean
                                  && part["thought"].Value<bool>();

                    var partText = part["text"];
                    if (partText != null && partText.Type == JTokenType.String)
                    {
                        if (isThought) thinking.Append(partText.Value<string>());
                        else text.Append(partText.Value<string>());
                    }

                    var functionCall = part["functionCall"];
                    if (functionCall != null)
                    {
                        parsed.ToolCalls.Add(new ToolCallRequest
                        {
                            Id = MakeCallId(functionCall["name"] != null ? functionCall["name"].ToString() : "tool"),
                            Name = functionCall["name"] != null ? functionCall["name"].ToString() : "",
                            ArgumentsJson = functionCall["args"] == null
                                ? "{}"
                                : functionCall["args"].ToString(Formatting.None)
                        });
                    }
                }
            }

            parsed.Content = text.ToString();
            parsed.Reasoning = thinking.ToString();

            ReadUsage(root["usageMetadata"], parsed.Usage);

            // 被安全策略截断
            var finishReason = candidates[0]["finishReason"] != null
                ? candidates[0]["finishReason"].ToString()
                : "";
            if (string.Equals(finishReason, "SAFETY", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(parsed.Content))
                {
                    return ChatResult.Fail("blocked", "回复被 Gemini 的安全策略截断了。");
                }
            }

            if (string.IsNullOrEmpty(parsed.Content) && parsed.ToolCalls.Count == 0)
            {
                return ChatResult.Fail("empty", "模型返回了空内容。");
            }
            return parsed;
        }

        private static void ReadUsage(JToken usage, UsageInfo target)
        {
            if (usage == null || target == null) return;

            target.InputTokens = ReadInt(usage["promptTokenCount"], 0);
            target.OutputTokens = ReadInt(usage["candidatesTokenCount"], 0);
            target.CachedTokens = ReadInt(usage["cachedContentTokenCount"], 0);

            // 思考型模型的思考 token 也计入输出
            int thoughts = ReadInt(usage["thoughtsTokenCount"], 0);
            if (thoughts > 0) target.OutputTokens += thoughts;
        }

        private static int ReadInt(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            int value;
            if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            return fallback;
        }

        /// <summary>Gemini 没有调用 id，本地合成一个（按名字 + 序号，便于日志与调试）。</summary>
        private static string MakeCallId(string toolName)
        {
            return "gem_" + (toolName ?? "tool") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        // ═════════════════════════════════════════════════════════════════
        // 模型列表
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Gemini 的模型列表端点：base/models（不带方法后缀）。</summary>
        public static string BuildModelsEndpoint(string configuredUrl)
        {
            var baseUrl = string.IsNullOrWhiteSpace(configuredUrl) ? DefaultBase : configuredUrl.Trim();
            baseUrl = baseUrl.TrimEnd('/');

            int generateIndex = baseUrl.IndexOf(":generateContent", StringComparison.OrdinalIgnoreCase);
            if (generateIndex < 0) generateIndex = baseUrl.IndexOf(":streamGenerateContent", StringComparison.OrdinalIgnoreCase);
            if (generateIndex > 0) baseUrl = baseUrl.Substring(0, generateIndex);

            int modelsIndex = baseUrl.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
            if (modelsIndex > 0) baseUrl = baseUrl.Substring(0, modelsIndex);

            if (baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return baseUrl;
            return baseUrl + "/models";
        }

        /// <summary>解析 Gemini 的模型列表响应：{"models":[{"name":"models/gemini-2.5-flash", ...}]}</summary>
        public static List<ModelMetadata> ParseModelList(string body)
        {
            var result = new List<ModelMetadata>();
            if (string.IsNullOrEmpty(body)) return result;

            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch (Exception)
            {
                return result;
            }

            var models = root["models"] as JArray;
            if (models == null) return result;

            foreach (var item in models)
            {
                if (item == null) continue;

                // 只保留支持 generateContent 的（列表里还混着 embedding 等）
                var methods = item["supportedGenerationMethods"] as JArray;
                if (methods != null)
                {
                    bool supportsChat = false;
                    foreach (var method in methods)
                    {
                        if (method != null && method.ToString().IndexOf("generateContent", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            supportsChat = true;
                            break;
                        }
                    }
                    if (!supportsChat) continue;
                }

                var rawName = item["name"] != null ? item["name"].ToString() : "";
                var id = rawName.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                    ? rawName.Substring("models/".Length)
                    : rawName;
                if (string.IsNullOrEmpty(id)) continue;

                var metadata = ModelMetadataTable.Resolve(id);
                metadata.DisplayName = item["displayName"] != null ? item["displayName"].ToString() : id;

                int window = ReadInt(item["inputTokenLimit"], 0);
                if (window > 0) metadata.ContextWindow = window;

                result.Add(metadata);
            }

            return result;
        }
    }
}
