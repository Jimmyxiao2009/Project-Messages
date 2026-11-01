// Platform/CloudEmbedder.cs —— 云端嵌入（RAG 的 P1 路径）
//
// 说明设计取舍：本地嵌入（bge.embmodel + OnEmbedder）在旧仓库里其实没接通 ——
// 模型文件不在仓库、Encode() 从未被调用。这里先用云端 /embeddings：
// 把对话端点的 /chat/completions 换成 /embeddings 即可，不需要额外配置。
//
// 失败处理：连不上或这个模型不支持 embeddings 时，把实例标记为不可用，
// 之后一律走 BM25 关键词检索 —— 不让 RAG 的失败拖慢每次对话。

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class CloudEmbedder : IEmbedder
    {
        private readonly IHttpTransport _http;
        private readonly ILogger _log;
        private readonly Func<ApiProfile> _apiResolver;
        private readonly Func<ApiProfile, string> _keyResolver;

        private bool _disabled;
        private int _dimensions;
        private int _failures;

        public CloudEmbedder(
            IHttpTransport http, ILogger log,
            Func<ApiProfile> apiResolver, Func<ApiProfile, string> keyResolver)
        {
            _http = http;
            _log = log;
            _apiResolver = apiResolver;
            _keyResolver = keyResolver;
        }

        public bool IsAvailable
        {
            get
            {
                if (_disabled || _http == null || _apiResolver == null) return false;
                var api = _apiResolver();
                return api != null && !api.IsAnthropic;   // Anthropic 没有 embeddings 端点
            }
        }

        public int Dimensions { get { return _dimensions; } }

        /// <summary>手动关掉（设置里"不用向量检索"时）。</summary>
        public bool Disabled
        {
            get { return _disabled; }
            set { _disabled = value; }
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;

            var api = _apiResolver();
            var key = _keyResolver != null ? _keyResolver(api) : "";
            if (string.IsNullOrEmpty(key)) return null;

            var url = DeriveEmbeddingsUrl(api.EffectiveUrl);
            if (string.IsNullOrEmpty(url)) return null;

            var model = !string.IsNullOrEmpty(api.SubModel) ? api.SubModel : api.Model;

            var body = new JObject
            {
                ["model"] = model,
                ["input"] = TextUtil.Truncate(text, 4000)
            };

            var spec = new HttpRequestSpec
            {
                Method = HttpMethodKind.Post,
                Url = url,
                Body = body.ToString(),
                TimeoutSeconds = 30
            };
            spec.AddHeader("Authorization", "Bearer " + key);
            spec.AddHeader("Content-Type", "application/json");

            try
            {
                var response = await _http.SendAsync(spec, ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    RegisterFailure("HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                var vector = ParseEmbedding(response.Body);
                if (vector == null || vector.Length == 0)
                {
                    RegisterFailure("空向量");
                    return null;
                }

                _dimensions = vector.Length;
                _failures = 0;
                return vector;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                RegisterFailure("网络错误");
                return null;
            }
        }

        private void RegisterFailure(string reason)
        {
            _failures++;
            if (_log != null) _log.Log(LogLevel.Warning, "embed", "嵌入失败(" + reason + ") 第 " + _failures + " 次");

            // 连续失败就彻底关掉，避免每次对话都白等一次超时
            if (_failures >= 2)
            {
                _disabled = true;
                if (_log != null) _log.Log(LogLevel.Info, "embed", "已停用向量检索，改用关键词检索");
            }
        }

        /// <summary>/v1/chat/completions → /v1/embeddings</summary>
        public static string DeriveEmbeddingsUrl(string chatUrl)
        {
            if (string.IsNullOrEmpty(chatUrl)) return "";

            var url = chatUrl.Trim();
            var suffixes = new string[] { "/chat/completions", "/completions", "/responses" };

            foreach (var suffix in suffixes)
            {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Substring(0, url.Length - suffix.Length);
                    break;
                }
            }

            url = url.TrimEnd('/');
            return url + "/embeddings";
        }

        /// <summary>兼容两种响应形态：{"data":[{"embedding":[...]}]} 与 {"embedding":[...]}。</summary>
        public static float[] ParseEmbedding(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;

            try
            {
                var root = JObject.Parse(body);

                JArray array = null;
                var data = root["data"] as JArray;
                if (data != null && data.Count > 0)
                {
                    array = data[0]["embedding"] as JArray;
                }
                if (array == null)
                {
                    array = root["embedding"] as JArray;
                }
                if (array == null) return null;

                var result = new float[array.Count];
                for (int i = 0; i < array.Count; i++)
                {
                    double value;
                    if (double.TryParse(array[i].ToString(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value))
                    {
                        result[i] = (float)value;
                    }
                }
                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
