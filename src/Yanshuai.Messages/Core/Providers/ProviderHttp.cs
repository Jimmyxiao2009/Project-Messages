// Core/Providers/ProviderHttp.cs —— 带重试的请求发送
//
// 重试策略：只对可重试的 HTTP 状态（429 / 5xx）与网络异常重试，
// 退避 1s → 2s → 4s，最多 3 次。取消令牌贯穿始终，用户点"打断"必须立刻停住。
//
// 注意：401 / 403 / 400 这类是确定性失败，重试只会浪费时间与额度，直接返回。

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    /// <summary>网络层异常（拿不到 HTTP 响应）。与"服务器返回了错误码"区分开。</summary>
    public sealed class ProviderNetworkException : Exception
    {
        public ProviderNetworkException(string reason)
            : base("网络请求失败")
        {
            Reason = reason;
        }

        public string Reason { get; private set; }
    }

    /// <summary>
    /// 流式请求拿到非 2xx。流式路径没法像一次性请求那样直接看状态码返回，
    /// 只能是传输层抛出来，由 provider 翻译成可读错误。
    /// </summary>
    public sealed class ProviderStreamException : Exception
    {
        public ProviderStreamException(int statusCode, string bodyPreview)
            : base("HTTP " + statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            StatusCode = statusCode;
            BodyPreview = bodyPreview ?? "";
        }

        public int StatusCode { get; private set; }
        public string BodyPreview { get; private set; }
    }

    public static class ProviderHttp
    {
        public const int MaxAttempts = 3;

        public static async Task<HttpResponseSpec> SendWithRetryAsync(
            IHttpTransport http,
            HttpRequestSpec spec,
            ILogger log,
            CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                ct.ThrowIfCancellationRequested();

                try
                {
                    var response = await http.SendAsync(spec, ct).ConfigureAwait(false);

                    if (ProviderErrors.IsRetryable(response.StatusCode) && attempt < MaxAttempts)
                    {
                        if (log != null)
                        {
                            log.Log(LogLevel.Warning, "http",
                                "status=" + response.StatusCode + " attempt=" + attempt + " 将重试");
                        }
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }

                    return response;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ProviderNetworkException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt >= MaxAttempts)
                    {
                        throw new ProviderNetworkException(ex.GetType().Name);
                    }
                    if (log != null)
                    {
                        log.Log(LogLevel.Warning, "http", "network error attempt=" + attempt + " 将重试");
                    }
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                }
            }
        }

        private static Task BackoffAsync(int attempt, CancellationToken ct)
        {
            int ms = 1000 * (int)Math.Pow(2, attempt - 1);   // 1s / 2s / 4s
            if (ms > 8000) ms = 8000;
            return Task.Delay(ms, ct);
        }
    }
}
