// Core/Speech/CloudSpeech.cs —— 朗读模型（OpenAI 兼容 /audio/speech）
//
// 只管"把一段文字换成音频字节"，播放交给平台的 IAudioPlayback。
// 兼容面：OpenAI（tts-1 / gpt-4o-mini-tts）、硅基流动等国内中转、自建的 OpenAI 兼容服务。
// Anthropic 没有语音合成；Gemini 的 TTS 走的是完全不同的接口，这里不支持，界面上会明说。

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    /// <summary>平台播放器：播放一段完整的音频，播完（或被停止）才返回。</summary>
    public interface IAudioPlayback
    {
        Task PlayAsync(byte[] audio, string contentType, double volume, CancellationToken ct);
        Task StopAsync();
    }

    public sealed class CloudSpeechTarget
    {
        public ApiProfile Api { get; set; }
        public string ApiKey { get; set; } = "";
        public string Voice { get; set; } = "alloy";
        /// <summary>mp3 体积小、UWP MediaPlayer 原生支持，默认就用它。</summary>
        public string Format { get; set; } = "mp3";
    }

    public sealed class CloudSpeechResult
    {
        public bool Ok { get; set; }
        public byte[] Audio { get; set; }
        public string ContentType { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public static class CloudSpeech
    {
        /// <summary>这个 Provider 能不能当朗读模型用。不能时返回原因，能时返回空串。</summary>
        public static string Unsupported(ApiProfile api)
        {
            if (api == null) return "没有选择 Provider。";
            if (api.IsAnthropic) return "Anthropic 没有语音合成接口。";
            if (api.IsGemini) return "暂不支持 Gemini 的语音合成，请选 OpenAI 兼容的服务。";
            if (string.IsNullOrWhiteSpace(api.Model)) return "没有填朗读模型。";
            return "";
        }

        public static HttpRequestSpec BuildRequest(CloudSpeechTarget target, string text, double rate)
        {
            var body = new JObject
            {
                ["model"] = target.Api.Model,
                ["input"] = text ?? "",
                ["voice"] = string.IsNullOrWhiteSpace(target.Voice) ? "alloy" : target.Voice.Trim(),
                ["response_format"] = string.IsNullOrWhiteSpace(target.Format) ? "mp3" : target.Format
            };
            // OpenAI 接受 0.25–4.0；正常语速不发，少一个字段就少一个中转不认的风险
            if (Math.Abs(rate - 1.0) > 0.01) body["speed"] = Math.Max(0.25, Math.Min(4.0, rate));

            var spec = new HttpRequestSpec
            {
                Method = HttpMethodKind.Post,
                Url = ProviderEndpoints.SpeechUrl(target.Api.Url),
                Body = body.ToString(Newtonsoft.Json.Formatting.None),
                TimeoutSeconds = 60,
                ExpectBinary = true
            };
            if (!string.IsNullOrEmpty(target.ApiKey)) spec.AddHeader("Authorization", "Bearer " + target.ApiKey);
            spec.AddHeader("Content-Type", "application/json");
            if (target.Api.CustomHeaders != null)
                foreach (var header in target.Api.CustomHeaders) spec.AddHeader(header.Key, header.Value);
            return spec;
        }

        public static CloudSpeechResult Interpret(HttpResponseSpec response)
        {
            if (response == null) return Fail("没有响应。");
            if (!response.IsSuccess)
                return Fail("HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + ErrorDetail(response.Body));
            if (response.BodyBytes == null || response.BodyBytes.Length == 0)
            {
                // 有些中转出错也回 200 + JSON
                var detail = ErrorDetail(response.Body);
                return Fail(detail.Length > 0 ? detail.TrimStart('：') : "服务没有返回音频。");
            }
            return new CloudSpeechResult
            {
                Ok = true,
                Audio = response.BodyBytes,
                ContentType = string.IsNullOrEmpty(response.ContentType) ? "audio/mpeg" : response.ContentType
            };
        }

        public static async Task<CloudSpeechResult> SynthesizeAsync(IHttpTransport http, CloudSpeechTarget target, string text, double rate, CancellationToken ct)
        {
            if (http == null || target == null) return Fail("朗读模型未就绪。");
            var reason = Unsupported(target.Api);
            if (reason.Length > 0) return Fail(reason);
            if (target.Api.RequiresApiKey && string.IsNullOrEmpty(target.ApiKey)) return Fail("这个 Provider 还没有填密钥。");
            try
            {
                var response = await http.SendAsync(BuildRequest(target, text, rate), ct).ConfigureAwait(false);
                return Interpret(response);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var network = ex as ProviderNetworkException;
                return Fail(network != null ? "网络错误：" + network.Reason : "请求失败：" + ex.GetType().Name);
            }
        }

        private static string ErrorDetail(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            try
            {
                var json = JObject.Parse(body);
                var error = json["error"];
                string message = null;
                if (error != null && error.Type == JTokenType.Object) message = (string)error["message"];
                else if (error != null && error.Type == JTokenType.String) message = (string)error;
                if (string.IsNullOrEmpty(message)) message = (string)json["message"];
                return string.IsNullOrEmpty(message) ? "" : "：" + TextUtil.Truncate(message, 160);
            }
            catch (Exception)
            {
                return "：" + TextUtil.Truncate(body.Trim(), 160);
            }
        }

        private static CloudSpeechResult Fail(string error)
        {
            return new CloudSpeechResult { Ok = false, Error = error };
        }
    }
}
