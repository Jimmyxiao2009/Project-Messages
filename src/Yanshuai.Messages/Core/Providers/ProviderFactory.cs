// Core/Providers/ProviderFactory.cs —— provider 选择与模型目录
//
// 上层永远不写 "if provider == claude"，一律经这里拿 IChatProvider。
// ModelCatalog 负责两件设置页要用的事：拉模型列表、测试连通性。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public static class ProviderFactory
    {
        public static IChatProvider Create(ApiProfile api, IHttpTransport http, ILogger log)
        {
            if (api != null)
            {
                if (api.IsGemini) return new GeminiChatProvider(http, log);
                if (api.IsAnthropic) return new AnthropicChatProvider(http, log);
            }
            return new OpenAiChatProvider(http, log);
        }
    }

    public sealed class ModelListResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";

        /// <summary>模型列表 + 自动补齐的元数据（服务端给的字段优先，缺失的按内置表推断）。</summary>
        public List<ModelMetadata> Entries { get; private set; }

        public ModelListResult()
        {
            Entries = new List<ModelMetadata>();
        }

        public List<string> Models
        {
            get
            {
                var list = new List<string>();
                foreach (var entry in Entries) list.Add(entry.ModelId);
                return list;
            }
        }

        /// <summary>有多少个模型的元数据至少部分已知。界面用它显示"自动识别了 N / M 个"。</summary>
        public int EnrichedCount
        {
            get
            {
                int count = 0;
                foreach (var entry in Entries)
                {
                    if (!entry.IsFullyUnknown) count++;
                }
                return count;
            }
        }
    }

    public static class ModelCatalog
    {
        /// <summary>
        /// 从对话端点推模型列表端点：
        /// https://api.deepseek.com/v1/chat/completions → https://api.deepseek.com/v1/models
        /// https://api.anthropic.com/v1/messages        → https://api.anthropic.com/v1/models
        /// </summary>
        public static string DeriveModelsUrl(string chatUrl)
        {
            return ProviderEndpoints.ModelsUrl(chatUrl);
        }

        public static async Task<ModelListResult> FetchAsync(
            IHttpTransport http, ApiProfile api, string apiKey, ILogger log, CancellationToken ct)
        {
            var result = new ModelListResult();
            if (http == null || api == null) { result.Error = "缺少配置"; return result; }

            // Gemini 的模型列表形态与 OpenAI 完全不同，单独走一条
            if (api.IsGemini) return await FetchGeminiAsync(http, api, apiKey, log, ct).ConfigureAwait(false);

            var url = !string.IsNullOrWhiteSpace(api.ModelsUrl) ? api.ModelsUrl : DeriveModelsUrl(api.EffectiveUrl);
            if (string.IsNullOrEmpty(url)) { result.Error = "接口地址为空"; return result; }

            var spec = new HttpRequestSpec { Method = HttpMethodKind.Get, Url = url, TimeoutSeconds = 30 };
            AddAuth(spec, api, apiKey);

            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var cursors = new HashSet<string>(StringComparer.Ordinal);
                for (int page = 0; page < 25; page++)
                {
                var response = await http.SendAsync(spec, ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    result.Error = "服务端返回 " + response.StatusCode.ToString(CultureInfo.InvariantCulture) +
                                   "，可能这个端点不提供模型列表。";
                    return result;
                }

                // OpenAI 形态 {"data":[{"id":...}]}；也有服务端直接给数组或 {"models":[...]}
                var token = JToken.Parse(response.Body);
                var rootObject = token as JObject;

                JArray array;
                if (rootObject != null)
                {
                    array = rootObject["data"] as JArray;
                    if (array == null) array = rootObject["models"] as JArray;
                }
                else
                {
                    array = token as JArray;
                }

                if (array == null)
                {
                    result.Error = "响应里没有找到模型列表。";
                    return result;
                }

                foreach (var item in array)
                {
                    var metadata = ParseServerEntry(item);
                    if (metadata == null || string.IsNullOrEmpty(metadata.ModelId)) continue;
                    if (!seen.Add(metadata.ModelId)) continue;
                    result.Entries.Add(metadata);
                }
                string cursor = rootObject != null ? JsonStr(rootObject["next_cursor"]) : null;
                string cursorName = "cursor";
                if (rootObject != null && rootObject["has_more"]?.Type == JTokenType.Boolean && rootObject["has_more"].Value<bool>())
                {
                    cursor = JsonStr(rootObject["last_id"]); cursorName = api.IsAnthropic ? "after_id" : "after";
                    if (string.IsNullOrEmpty(cursor)) { result.Error = "分页响应缺少游标，旧模型列表已保留。"; return result; }
                }
                if (string.IsNullOrEmpty(cursor)) break;
                if (!cursors.Add(cursor) || page == 24) { result.Error = "模型列表分页未完成，旧模型列表已保留。"; return result; }
                var builder = new UriBuilder(url);
                builder.Query = builder.Query.TrimStart('?') + (builder.Query.Length > 1 ? "&" : "") + cursorName + "=" + Uri.EscapeDataString(cursor);
                spec.Url = builder.Uri.AbsoluteUri;
                }

                // 能力已知的排前面，未知的沉底
                result.Entries.Sort(delegate (ModelMetadata a, ModelMetadata b)
                {
                    bool aUnknown = a.IsFullyUnknown;
                    bool bUnknown = b.IsFullyUnknown;
                    if (aUnknown != bUnknown) return aUnknown ? 1 : -1;
                    return string.Compare(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase);
                });

                result.Ok = result.Entries.Count > 0;
                if (!result.Ok) result.Error = "模型列表是空的。";
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                result.Error = "获取失败，检查网络或接口地址。";
                return result;
            }
        }

        /// <summary>Gemini 的模型列表：GET {base}/models，形态与 OpenAI 不同。</summary>
        private static async Task<ModelListResult> FetchGeminiAsync(
            IHttpTransport http, ApiProfile api, string apiKey, ILogger log, CancellationToken ct)
        {
            var result = new ModelListResult();

            var url = !string.IsNullOrWhiteSpace(api.ModelsUrl) ? api.ModelsUrl : GeminiChatProvider.BuildModelsEndpoint(api.EffectiveUrl);
            var spec = new HttpRequestSpec { Method = HttpMethodKind.Get, Url = url, TimeoutSeconds = 30 };
            AddAuth(spec, api, apiKey);

            try
            {
                var response = await http.SendAsync(spec, ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    result.Error = "服务端返回 " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + "。";
                    return result;
                }

                result.Entries.AddRange(GeminiChatProvider.ParseModelList(response.Body));

                result.Entries.Sort(delegate (ModelMetadata a, ModelMetadata b)
                {
                    bool aUnknown = a.IsFullyUnknown;
                    bool bUnknown = b.IsFullyUnknown;
                    if (aUnknown != bUnknown) return aUnknown ? 1 : -1;
                    return string.Compare(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase);
                });

                result.Ok = result.Entries.Count > 0;
                if (!result.Ok) result.Error = "模型列表是空的。";
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                result.Error = "获取失败，检查网络或接口地址。";
                return result;
            }
        }

        /// <summary>
        /// 把一个服务端返回的模型条目解析成元数据，缺失字段用内置表兜底。
        ///
        /// 各家给的字段差别极大：
        ///   OpenRouter ：id / name / context_length / pricing / architecture / supported_parameters（最全）
        ///   Anthropic  ：id / display_name / created_at
        ///   OpenAI     ：id / created（几乎什么信息都没有，全靠内置表）
        /// 所以这里的原则是"服务端给什么就用什么，其余按内置表推断"，并如实记录来源。
        /// </summary>
        public static ModelMetadata ParseServerEntry(JToken item)
        {
            if (item == null) return null;

            // 有的服务端直接给字符串数组：{"data":["gpt-4o","gpt-4"]}。
            // 在 JValue 上访问子节点会抛异常（Cannot access child value on JValue），
            // 所以字符串形态必须单独走一条路，否则整份列表解析会直接失败。
            if (item.Type != JTokenType.Object)
            {
                var text = item.Type == JTokenType.String ? item.Value<string>() : null;
                if (string.IsNullOrEmpty(text)) return null;

                var solo = ModelMetadataTable.Resolve(text);
                solo.DisplayName = text;
                return solo;
            }

            string id;
            string displayName = null;

            id = JsonStr(item["id"]);
            displayName = JsonStr(item["name"]);
            if (string.IsNullOrEmpty(displayName)) displayName = JsonStr(item["display_name"]);

            if (string.IsNullOrEmpty(id)) return null;

            // 先用内置表打底，服务端有值就覆盖，这样不会有"服务端给了窗口但没给模态 → 模态变未知"的倒退
            var builtin = ModelMetadataTable.Lookup(id);
            var metadata = builtin != null ? builtin.Clone() : new ModelMetadata();
            metadata.ModelId = id;
            metadata.DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;

            bool fromServer = false;
            var origin = new List<string>();

            int window = FirstInt(item, "context_length", "context_window", "max_context_length", "max_input_tokens", "input_token_limit", "inputTokenLimit");
            if (window > 0)
            {
                metadata.ContextWindow = window;
                fromServer = true;
                origin.Add("窗口");
            }
            int output = FirstInt(item, "max_output_tokens", "max_completion_tokens", "output_token_limit", "outputTokenLimit");
            if (output == 0 && item["top_provider"] is JObject) output = FirstInt(item["top_provider"], "max_completion_tokens");
            if (output > 0) { metadata.MaxOutputTokens = output; fromServer = true; origin.Add("输出上限"); }

            var pricing = item["pricing"];
            if (pricing is JObject)
            {
                double prompt;
                double completion;
                bool hasPrompt = TryDouble(pricing["prompt"], out prompt);
                bool hasCompletion = TryDouble(pricing["completion"], out completion);
                if (hasPrompt || hasCompletion)
                {
                    metadata.PriceTier = PriceTierOf(prompt, completion);
                    fromServer = true;
                    origin.Add("价格");
                }
            }

            var vision = DetectVisionFromServer(item);
            if (vision.HasValue)
            {
                metadata.Vision = vision;
                fromServer = true;
                origin.Add("模态");
            }

            var parameters = item["supported_parameters"] as JArray;
            if (parameters != null)
            {
                bool reasoning = false;
                bool tools = false;
                foreach (var parameter in parameters)
                {
                    var name = parameter != null ? parameter.ToString().ToLowerInvariant() : "";
                    if (name.IndexOf("reasoning", StringComparison.Ordinal) >= 0) reasoning = true;
                    if (name.IndexOf("tool", StringComparison.Ordinal) >= 0) tools = true;
                }
                metadata.Reasoning = reasoning;
                metadata.Tools = tools;
                metadata.SupportedParameters = Strings(parameters);
                fromServer = true;
                origin.Add("能力");
            }
            var capabilities = item["capabilities"] as JObject;
            var explicitVision = FirstBool(item, "supports_vision", "vision") ?? FirstBool(capabilities, "vision", "image_input");
            var explicitTools = FirstBool(item, "supports_tools", "supports_function_calling", "tool_calling") ?? FirstBool(capabilities, "tools", "function_calling", "tool_calling");
            var explicitReasoning = FirstBool(item, "supports_reasoning", "reasoning") ?? FirstBool(capabilities, "reasoning", "thinking");
            if (explicitVision.HasValue) metadata.Vision = explicitVision;
            if (explicitTools.HasValue) metadata.Tools = explicitTools;
            if (explicitReasoning.HasValue) metadata.Reasoning = explicitReasoning;
            if (explicitVision.HasValue || explicitTools.HasValue || explicitReasoning.HasValue) { fromServer = true; origin.Add("显式能力"); }
            var architecture = item["architecture"] as JObject;
            metadata.InputModalities = Strings(item["input_modalities"] as JArray ?? architecture?["input_modalities"] as JArray);
            metadata.OutputModalities = Strings(item["output_modalities"] as JArray ?? architecture?["output_modalities"] as JArray);

            if (fromServer)
            {
                metadata.Source = MetadataSource.Server;
                metadata.Origin = "服务端：" + string.Join("/", origin.ToArray());
            }
            else if (builtin != null)
            {
                metadata.Source = MetadataSource.BuiltIn;
            }
            else
            {
                metadata.Source = MetadataSource.Unknown;
                metadata.Origin = "未知";
            }

            return metadata;
        }

        private static List<string> Strings(JArray array)
        {
            var result = new List<string>();
            if (array != null) foreach (var token in array) if (token.Type == JTokenType.String) result.Add(token.Value<string>());
            return result;
        }
        private static bool? FirstBool(JToken item, params string[] keys)
        {
            if (!(item is JObject)) return null;
            foreach (var key in keys) {
                var token = item[key];
                if (token == null) continue;
                bool value;
                if (bool.TryParse(token.ToString(), out value)) return value;
            }
            return null;
        }

        private static string JsonStr(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }

        private static int FirstInt(JToken item, params string[] keys)
        {
            if (item == null) return 0;
            foreach (var key in keys)
            {
                var token = item[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                int value;
                if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
                {
                    return value;
                }
            }
            return 0;
        }

        private static bool TryDouble(JToken token, out double value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null) return false;
            return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>按每 token 美元价分档：≤$1/M 便宜，≤$5/M 中等，再贵往上。</summary>
        private static int PriceTierOf(double promptPerToken, double completionPerToken)
        {
            double highest = Math.Max(promptPerToken, completionPerToken);
            if (highest <= 0) return 0;
            if (highest <= 0.000001) return 1;
            if (highest <= 0.000005) return 2;
            return 3;
        }

        /// <summary>OpenRouter 会在 architecture 里说清输入模态；其他家多半不说，返回 null 表示未知。</summary>
        private static bool? DetectVisionFromServer(JToken item)
        {
            var inputs = item["input_modalities"] as JArray;
            var architecture = item["architecture"] as JObject;
            if (inputs == null && architecture != null) inputs = architecture["input_modalities"] as JArray;
            if (inputs != null) {
                foreach (var input in inputs) if (string.Equals(input.ToString(), "image", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            if (architecture == null) return null;

            var modality = JsonStr(architecture["modality"]);
            if (!string.IsNullOrEmpty(modality))
            {
                return modality.Split(new[] { "->" }, StringSplitOptions.None)[0].IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return null;
        }

        /// <summary>发一个最小请求验证连通性与密钥。返回给用户看的一句话。</summary>
        public static async Task<string> TestConnectionAsync(
            IHttpTransport http, ApiProfile api, string apiKey, string model, ILogger log, CancellationToken ct)
        {
            if (http == null || api == null) return "缺少配置。";

            var provider = ProviderFactory.Create(api, http, log);
            var request = new ChatRequest
            {
                Api = api,
                ApiKey = apiKey ?? "",
                MaxTokens = 16
            };
            request.Turns.Add(ChatTurn.User("回复 ok 即可。"));

            // 用指定的模型名（用户可能刚改过还没保存）
            var probeApi = api.Clone();
            if (!string.IsNullOrEmpty(model)) probeApi.Model = model;
            request.Api = probeApi;

            try
            {
                var result = await provider.CompleteAsync(request, ct).ConfigureAwait(false);
                if (result.Ok) return "连接正常，模型有响应。";
                return result.ErrorMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return "连接失败，请检查网络与接口地址。";
            }
        }

        public static void AddAuth(HttpRequestSpec spec, ApiProfile api, string apiKey)
        {
            if (api != null && api.IsGemini)
            {
                spec.AddHeader("x-goog-api-key", apiKey ?? "");
            }
            else if (api != null && api.IsAnthropic)
            {
                spec.AddHeader("x-api-key", apiKey ?? "");
                spec.AddHeader("anthropic-version", AnthropicChatProvider.ApiVersion);
            }
            else
            {
                if (!string.IsNullOrEmpty(apiKey)) spec.AddHeader("Authorization", "Bearer " + apiKey);
            }
            spec.AddHeader("Accept", "application/json");

            if (api != null && api.CustomHeaders != null)
            {
                foreach (var pair in api.CustomHeaders) spec.AddHeader(pair.Key, pair.Value);
            }
        }
    }
}
