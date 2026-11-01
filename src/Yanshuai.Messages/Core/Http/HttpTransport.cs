// Core/Http/HttpTransport.cs —— HTTP 传输
//
// 这份实现只用 System.Net.Http，没有任何 UWP 专有 API，所以放在 Core 里：
// 应用与桌面测试共用同一份代码，测试打的就真的是线上跑的那条路径。
//
// 两件事必须做对：
//   1) 超时。HttpClient 没有逐请求超时，必须自己挂 CancellationTokenSource。
//   2) 流式。必须用 ResponseHeadersRead，否则会等整个 body 到齐才返回，流式就白做了。

using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class HttpTransport : IHttpTransport, IDisposable
    {
        private readonly HttpClient _proxied;      // 走系统代理：公网 API
        private readonly HttpClient _direct;       // 不走代理：本机与局域网端点
        private readonly bool _ownsClients;

        public HttpTransport()
        {
            // AutomaticDecompression 必须开：Tune() 里主动要了 gzip/deflate，
            // 而 HttpClientHandler 默认**不解压**——于是 ReadAsStringAsync 把
            // 压缩字节当字符串读，fetch_page 抓回来的就是一坨二进制乱码。
            var decompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            _proxied = new HttpClient(new HttpClientHandler { AutomaticDecompression = decompression });
            _direct = new HttpClient(new HttpClientHandler { UseProxy = false, AutomaticDecompression = decompression });
            _ownsClients = true;
            Tune(_proxied);
            Tune(_direct);
        }

        /// <summary>注入自定义 HttpClient（两个方向都用它，主要给测试用）。</summary>
        public HttpTransport(HttpClient client)
        {
            _proxied = client ?? new HttpClient();
            _direct = _proxied;
            _ownsClients = false;
            Tune(_proxied);
        }

        private static void Tune(HttpClient client)
        {
            try
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            }
            catch (Exception)
            {
                // 某些宿主不允许设这个头，不影响功能
            }
        }

        /// <summary>
        /// 本机与局域网地址一律绕过系统代理。
        ///
        /// 为什么必须这么做：桌面和手机上都很常见装了代理工具（Clash 之类），
        /// 它会把 localhost / 192.168.x.x 的请求也塞进代理，于是自建的模型端点
        /// 直接连不上，报一个看不出原因的 HttpRequestException。
        /// 公网 API 仍然走代理，不破坏需要代理才能出网的场景。
        /// </summary>
        public static bool ShouldBypassProxy(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;

            if (uri.IsLoopback) return true;

            var host = uri.Host ?? "";
            if (host.Length == 0) return false;
            if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;

            // 私有网段：10.x / 172.16-31.x / 192.168.x
            var parts = host.Split('.');
            if (parts.Length == 4)
            {
                int a, b;
                if (int.TryParse(parts[0], out a) && int.TryParse(parts[1], out b))
                {
                    if (a == 10) return true;
                    if (a == 192 && b == 168) return true;
                    if (a == 172 && b >= 16 && b <= 31) return true;
                }
            }

            return false;
        }

        private HttpClient PickClient(string url)
        {
            return ShouldBypassProxy(url) ? _direct : _proxied;
        }

        public async Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken ct)
        {
            var client = PickClient(request != null ? request.Url : null);

            using (var linked = CreateLinkedToken(request, ct))
            using (var message = BuildMessage(request))
            {
                try
                {
                    var response = await client.SendAsync(message, linked.Token).ConfigureAwait(false);
                    var contentType = response.Content.Headers.ContentType != null
                        ? response.Content.Headers.ContentType.MediaType
                        : "";

                    string body;
                    byte[] bytes = null;
                    bool textual = contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                        || contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
                    // 二进制只在成功且不是文本时保留；出错时服务端回的是 JSON / 文本，照常读成字符串给调用方报错
                    if (request != null && request.ExpectBinary && response.IsSuccessStatusCode && !textual)
                    {
                        bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        body = "";
                    }
                    else
                    {
                        body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var header in response.Headers) headers[header.Key] = string.Join(", ", header.Value);
                    return new HttpResponseSpec
                    {
                        StatusCode = (int)response.StatusCode,
                        Body = body,
                        BodyBytes = bytes,
                        ContentType = contentType,
                        Headers = headers
                    };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProviderNetworkException("timeout");
                }
                catch (HttpRequestException ex)
                {
                    throw new ProviderNetworkException(Describe(ex));
                }
            }
        }

        public async Task StreamLinesAsync(HttpRequestSpec request, Func<string, bool> onLine, CancellationToken ct)
        {
            var client = PickClient(request != null ? request.Url : null);

            using (var linked = CreateLinkedToken(request, ct))
            using (var message = BuildMessage(request))
            {
                HttpResponseMessage response;
                try
                {
                    // 关键：ResponseHeadersRead
                    response = await client
                        .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProviderNetworkException("timeout");
                }
                catch (HttpRequestException ex)
                {
                    throw new ProviderNetworkException(Describe(ex));
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var preview = "";
                    try
                    {
                        preview = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                    catch (Exception) { }
                    throw new ProviderStreamException(status, preview);
                }

                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (true)
                    {
                        linked.Token.ThrowIfCancellationRequested();

                        string line;
                        try
                        {
                            line = await reader.ReadLineAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new ProviderNetworkException("timeout");
                        }

                        if (line == null)
                        {
                            onLine(null);
                            break;
                        }
                        if (!onLine(line)) break;
                    }
                }
            }
        }

        /// <summary>给网络异常一个能用来判断原因的摘要。去掉查询串，避免把 token 带进日志。</summary>
        private static string Describe(Exception ex)
        {
            var message = ex.Message ?? "";
            int query = message.IndexOf('?');
            if (query >= 0) message = message.Substring(0, query);

            message = message.Replace('\n', ' ').Replace('\r', ' ');
            return ex.GetType().Name + (message.Length > 0 ? ": " + TextUtil.Truncate(message, 90) : "");
        }

        private static CancellationTokenSource CreateLinkedToken(HttpRequestSpec request, CancellationToken ct)
        {
            var seconds = request != null && request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 120;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            return cts;
        }

        private static HttpRequestMessage BuildMessage(HttpRequestSpec request)
        {
            var method = MethodOf(request.Method);
            var message = new HttpRequestMessage(method, request.Url);

            if (!string.IsNullOrEmpty(request.Body) && method != HttpMethod.Get)
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
            }

            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                {
                    if (string.IsNullOrEmpty(header.Key)) continue;
                    // Content-Type 由 StringContent 负责，重复设置会抛
                    if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return message;
        }

        private static HttpMethod MethodOf(HttpMethodKind kind)
        {
            switch (kind)
            {
                case HttpMethodKind.Post: return HttpMethod.Post;
                // HttpMethod.Patch 在 UWP 的 .NET 面里没有，只能自己构造
                case HttpMethodKind.Patch: return new HttpMethod("PATCH");
                case HttpMethodKind.Delete: return HttpMethod.Delete;
                default: return HttpMethod.Get;
            }
        }

        public void Dispose()
        {
            if (!_ownsClients) return;
            try { _proxied.Dispose(); }
            catch (Exception) { }
            try { _direct.Dispose(); }
            catch (Exception) { }
        }
    }
}
