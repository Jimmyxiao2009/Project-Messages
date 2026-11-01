// Core/Tools/BuiltinTools.cs —— 内置工具集
//
// 20 个工具，与设计稿 §9.1 的清单一致：
//   保留旧的 19 个（去掉 set_working_directory，因为手机上"档案文件夹即工作目录"），
//   新增 get_time 与 memory_search。
//
// 两条纪律：
//   1) 工具只依赖 ToolContext，不 new HttpClient、不碰 Dispatcher；
//   2) 失败返回 ToolResult.Failure（一句可读说明），让模型自己纠偏，不抛异常打断整轮。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    // ═════════════════════════════════════════════════════════════════════
    // 时间
    // ═════════════════════════════════════════════════════════════════════
    public sealed class TimeTool : ToolBase
    {
        public override string Name { get { return "get_time"; } }
        public override string Description { get { return "获取当前日期、时间和时区。回答涉及时间的问题前先调用它。"; } }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema) { }

        public override Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var now = context.Clock != null ? context.Clock.UtcNow : DateTimeOffset.UtcNow;
            var local = now.ToLocalTime();
            var sb = new StringBuilder();
            sb.Append("当前时间：").Append(local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("星期：").Append(local.ToString("dddd", new CultureInfo("zh-CN"))).Append('\n');
            sb.Append("UTC：").Append(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("时区：").Append(TimeZoneInfo.Local.DisplayName);
            return Task.FromResult(ToolResult.Success(sb.ToString()));
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 文件（只能落在档案自己的 files 目录内）
    // ═════════════════════════════════════════════════════════════════════
    public sealed class FileListTool : ToolBase
    {
        public override string Name { get { return "list_files"; } }
        public override string Description
        {
            get { return "列出当前档案目录下的文件。不传 path 则列根目录。"; }
        }
        public override ToolGroup Group { get { return ToolGroup.Files; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("path", "string", "相对档案目录的路径，留空表示根目录", false);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var raw = ToolArguments.GetString(argumentsJson, "path", "");
            var dir = PathSandbox.ResolveInFiles(context, raw);
            if (dir == null) return ToolResult.Failure(PathSandbox.RejectReason(raw));

            IReadOnlyList<string> files;
            try
            {
                files = await context.Store.ListFilesAsync(dir, false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return ToolResult.Failure("无法读取目录。");
            }

            var directories = await context.Store.ListDirectoriesAsync(dir).ConfigureAwait(false);
            if (files.Count == 0 && directories.Count == 0) return ToolResult.Success("这个目录是空的。");

            var sb = new StringBuilder();
            foreach (var directory in directories) sb.Append(WorkspaceLayout.FileName(directory)).Append("/\n");
            foreach (var file in files)
            {
                var name = WorkspaceLayout.FileName(file);
                long size = 0;
                try
                {
                    var text = await context.Store.ReadTextAsync(file).ConfigureAwait(false);
                    size = text != null ? text.Length : 0;
                }
                catch (Exception) { }

                sb.Append(name);
                if (size > 0) sb.Append("  (").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" 字)");
                sb.Append('\n');
            }

            return ToolResult.Success(sb.ToString().Trim());
        }
    }

    public sealed class FileReadTool : ToolBase
    {
        public override string Name { get { return "read_file"; } }
        public override string Description { get { return "读取档案目录内的文本文件。超出上限会被截断。"; } }
        public override ToolGroup Group { get { return ToolGroup.Files; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("path", "string", "相对档案目录的文件路径", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var raw = ToolArguments.GetString(argumentsJson, "path", "");
            var path = PathSandbox.ResolveInFiles(context, raw);
            if (path == null) return ToolResult.Failure(PathSandbox.RejectReason(raw));
            if (string.IsNullOrEmpty(PathSandbox.NormalizeRelative(raw)))
            {
                return ToolResult.Failure("请指定要读取的文件。");
            }

            try
            {
                if (!await context.Store.ExistsAsync(path).ConfigureAwait(false))
                {
                    return ToolResult.Failure("文件不存在：" + raw);
                }
                var text = await context.Store.ReadTextAsync(path).ConfigureAwait(false);
                if (text == null) return ToolResult.Failure("文件为空或无法解码为文本。");

                var body = TextUtil.Truncate(text, Limits.ReadFileChars);
                var summary = text.Length.ToString(CultureInfo.InvariantCulture) + " 字";
                return ToolResult.Success(body, summary);
            }
            catch (Exception)
            {
                return ToolResult.Failure("读取失败，可能是二进制文件或权限不足。");
            }
        }
    }

    public sealed class FileWriteTool : ToolBase
    {
        public override string Name { get { return "write_file"; } }
        public override string Description
        {
            get { return "把文本写入档案目录内的文件（覆盖同名文件，写入前会自动备份原内容）。"; }
        }
        public override ToolGroup Group { get { return ToolGroup.Files; } }
        public override bool IsSensitive { get { return true; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("path", "string", "相对档案目录的文件路径", true);
            schema.Param("content", "string", "要写入的完整内容", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var raw = ToolArguments.GetString(argumentsJson, "path", "");
            var path = PathSandbox.ResolveInFiles(context, raw);
            if (path == null) return ToolResult.Failure(PathSandbox.RejectReason(raw));
            if (string.IsNullOrEmpty(PathSandbox.NormalizeRelative(raw)))
            {
                return ToolResult.Failure("请指定要写入的文件路径。");
            }

            var content = ToolArguments.GetString(argumentsJson, "content", "");
            if (content == null) content = "";

            try
            {
                // 覆盖前留一份备份，误写还有得救
                if (await context.Store.ExistsAsync(path).ConfigureAwait(false))
                {
                    var previous = await context.Store.ReadTextAsync(path).ConfigureAwait(false);
                    if (previous != null)
                    {
                        var backup = context.FilesRoot + "/.backup/" + WorkspaceLayout.FileName(path);
                        await AtomicFile.WriteAsync(context.Store, backup, previous).ConfigureAwait(false);
                    }
                }

                await AtomicFile.WriteAsync(context.Store, path, content).ConfigureAwait(false);

                var summary = content.Length.ToString(CultureInfo.InvariantCulture) + " 字";
                return ToolResult.Success(
                    "已写入 " + raw + "（" + summary + "）。原内容已备份到 .backup/ 目录。",
                    summary);
            }
            catch (Exception)
            {
                return ToolResult.Failure("写入失败。");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 联网
    // ═════════════════════════════════════════════════════════════════════
    public sealed class WebSearchTool : ToolBase
    {
        public override string Name { get { return "web_search"; } }
        public override string Description
        {
            get { return "联网搜索（Bing，中文市场）。返回标题、来源域名、链接与摘要。"
                 + "需要最新信息或查资料时使用。查不到好结果时，换更具体的关键词重试"
                 + "（加书名号、站点名如 site:zhihu.com、或补上类型词如\"小说\"\"百科\"），"
                 + "比反复搜同一个词有效。可用 market 参数指定市场（中文 zh-CN / 英文 en-US / 日文 ja-JP），"
                 + "按查询内容的语言选，结果质量差别很大。"; }
        }
        public override ToolGroup Group { get { return ToolGroup.Network; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("query", "string", "搜索关键词", true);
            // 市场由模型按查询内容自己选：中文内容用 zh-CN，英文内容用 en-US 效果好得多。
            // 之前写死 zh-CN，搜英文资料时结果质量明显差。
            schema.Param("market", "string",
                "市场/地区代码，形如 zh-CN / en-US / ja-JP / zh-TW。按查询内容选：中文内容 zh-CN，"
                + "英文内容 en-US，日文 ja-JP。不传默认 zh-CN。", false);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var query = ToolArguments.GetString(argumentsJson, "query", "");
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Failure("请给出搜索关键词。");

            var settings = context.Search ?? new SearchSettings();
            var max = settings.MaxResults > 0 ? settings.MaxResults : 5;
            var depth = settings.Depth > 0 ? settings.Depth : 2000;
            var market = NormalizeMarket(ToolArguments.GetString(argumentsJson, "market", ""));

            try
            {
                List<SearchHit> hits;

                switch ((settings.Provider ?? "duckduckgo").ToLowerInvariant())
                {
                    case "tavily":
                        hits = await SearchTavilyAsync(context, settings, query, max, ct).ConfigureAwait(false);
                        break;
                    case "searxng":
                        hits = await SearchSearxngAsync(context, settings, query, max, ct).ConfigureAwait(false);
                        break;
                    case "bing":
                        hits = await SearchBingAsync(context, settings, query, max, market, ct).ConfigureAwait(false);
                        break;
                    default:
                        hits = await SearchBingAsync(context, settings, query, max, market, ct).ConfigureAwait(false);
                        break;
                }

                if (hits.Count == 0) return ToolResult.Success("没有找到相关结果。");

                var sb = new StringBuilder();
                int index = 1;
                foreach (var hit in hits)
                {
                    sb.Append(index).Append(". ").Append(hit.Title).Append('\n');
                    // 带上来源域名：模型能据此判断是权威源还是垃圾站，也能按站点再检索
                    var host = "";
                    try { if (Uri.IsWellFormedUriString(hit.Url, UriKind.Absolute)) host = new Uri(hit.Url).Host; }
                    catch (Exception) { host = ""; }
                    if (!string.IsNullOrEmpty(host)) sb.Append("   来源: ").Append(host).Append('\n');
                    sb.Append("   ").Append(hit.Url).Append('\n');
                    if (!string.IsNullOrEmpty(hit.Snippet))
                    {
                        sb.Append("   ").Append(TextUtil.Truncate(WebText.OneLine(hit.Snippet, depth), depth)).Append('\n');
                    }
                    index++;
                    if (index > max) break;
                }

                return ToolResult.Success(sb.ToString().Trim(),
                    hits.Count.ToString(CultureInfo.InvariantCulture) + " 条结果");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProviderNetworkException)
            {
                return ToolResult.Failure("网络不可用，搜索失败。", "network");
            }
            catch (Exception ex)
            {
                // 把现场带出来：之前 0 结果只回一句"没有找到相关结果"，
                // 分不清是真没结果还是被同意页/验证码挡住。
                var detail = ex is InvalidOperationException ? ex.Message : "";
                return ToolResult.Failure("搜索失败。" + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"), "search");
            }
        }

        private sealed class SearchHit
        {
            public string Title;
            public string Url;
            public string Snippet;
        }

        /// <summary>
        /// 归一化模型传上来的市场代码。形如 xx-XX（语言-地区）才收，否则兜底 zh-CN。
        /// 大小写归一成 zh-CN 这种形式（Bing 的 mkt 参数认这个写法）。
        /// </summary>
        private static string NormalizeMarket(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "zh-CN";
            var text = raw.Trim();
            var match = Regex.Match(text, "^([A-Za-z]{2})[-_]([A-Za-z]{2})$");
            if (!match.Success) return "zh-CN";
            return match.Groups[1].Value.ToLowerInvariant() + "-" + match.Groups[2].Value.ToUpperInvariant();
        }

        private static HttpRequestSpec MakeSpec(string url, SearchSettings settings)
        {
            var spec = new HttpRequestSpec
            {
                Method = HttpMethodKind.Get,
                Url = url,
                TimeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30
            };
            var ua = string.IsNullOrEmpty(settings.UserAgent)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
                : settings.UserAgent;
            spec.AddHeader("User-Agent", ua);
            spec.AddHeader("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            return spec;
        }

        private static async Task<List<SearchHit>> SearchTavilyAsync(
            ToolContext context, SearchSettings settings, string query, int max, CancellationToken ct)
        {
            var url = string.IsNullOrEmpty(settings.BaseUrl) ? "https://api.tavily.com/search" : settings.BaseUrl;

            var body = new Newtonsoft.Json.Linq.JObject
            {
                ["query"] = query,
                ["max_results"] = max,
                ["search_depth"] = "basic",
                ["include_answer"] = false
            };
            if (!string.IsNullOrEmpty(settings.ApiKey)) body["api_key"] = settings.ApiKey;

            var spec = MakeSpec(url, settings);
            spec.Method = HttpMethodKind.Post;
            spec.Body = body.ToString();
            spec.AddHeader("Content-Type", "application/json");

            var response = await context.Http.SendAsync(spec, ct).ConfigureAwait(false);
            if (!response.IsSuccess) return new List<SearchHit>();

            var hits = new List<SearchHit>();
            var root = Newtonsoft.Json.Linq.JObject.Parse(response.Body);
            var results = root["results"] as Newtonsoft.Json.Linq.JArray;
            if (results != null)
            {
                foreach (var item in results)
                {
                    hits.Add(new SearchHit
                    {
                        Title = item["title"] != null ? item["title"].ToString() : "",
                        Url = item["url"] != null ? item["url"].ToString() : "",
                        Snippet = item["content"] != null ? item["content"].ToString() : ""
                    });
                }
            }
            return hits;
        }

        private static async Task<List<SearchHit>> SearchSearxngAsync(
            ToolContext context, SearchSettings settings, string query, int max, CancellationToken ct)
        {
            var baseUrl = settings.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl)) return new List<SearchHit>();

            var url = baseUrl.TrimEnd('/') + "/search?q=" + Uri.EscapeDataString(query) + "&format=json";
            var response = await context.Http.SendAsync(MakeSpec(url, settings), ct).ConfigureAwait(false);
            if (!response.IsSuccess) return new List<SearchHit>();

            var hits = new List<SearchHit>();
            var root = Newtonsoft.Json.Linq.JObject.Parse(response.Body);
            var results = root["results"] as Newtonsoft.Json.Linq.JArray;
            if (results != null)
            {
                foreach (var item in results)
                {
                    hits.Add(new SearchHit
                    {
                        Title = item["title"] != null ? item["title"].ToString() : "",
                        Url = item["url"] != null ? item["url"].ToString() : "",
                        Snippet = item["content"] != null ? item["content"].ToString() : ""
                    });
                    if (hits.Count >= max) break;
                }
            }
            return hits;
        }

        private static readonly Regex DdgLink = new Regex(
            @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex DdgSnippet = new Regex(
            @"class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>
        /// Bing 搜索。**HTML 优先、RSS 兜底**——顺序是实测定下来的：
        /// RSS 端点（&amp;format=rss）返回的是降级结果，实测搜中文小说名回来的全是
        /// 学信网/知乎这类无关内容；HTML 页才是正常的相关性排序。
        /// 设置里的"服务地址"可覆盖（比如指向 cn.bing.com 或自建反代）。
        /// </summary>
        private static async Task<List<SearchHit>> SearchBingAsync(
            ToolContext context, SearchSettings settings, string query, int max, string market, CancellationToken ct)
        {
            var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? "https://www.bing.com/search"
                : settings.BaseUrl.Trim().TrimEnd('/');
            var trace = new StringBuilder();

            // 1) HTML 优先
            try
            {
                var htmlUrl = baseUrl + "?q=" + Uri.EscapeDataString(query)
                            + "&setlang=" + market + "&mkt=" + market
                            + "&count=" + (max * 2).ToString(CultureInfo.InvariantCulture);
                var html = await context.Http.SendAsync(MakeSpec(htmlUrl, settings), ct).ConfigureAwait(false);
                if (!html.IsSuccess)
                {
                    trace.Append("html:HTTP").Append(html.StatusCode.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    var hits = ParseBingHtml(html.Body, max);
                    if (hits.Count > 0) return hits;

                    // 0 条不一定是"没有结果"——可能是同意页/验证码/改版。带现场回去。
                    var body = html.Body ?? "";
                    trace.Append("html:0条/len=").Append(body.Length);
                    if (body.Length > 0)
                    {
                        var text = WebText.HtmlToText(body);
                        trace.Append(" 开头=").Append(TextUtil.Truncate(WebText.OneLine(text, 100), 100));
                    }
                }
            }
            catch (Exception ex)
            {
                trace.Append("html:").Append(ex.GetType().Name);
            }

            // 2) RSS 兜底（结果质量差，只在 HTML 拿不到时用）
            try
            {
                var rssUrl = baseUrl + "?q=" + Uri.EscapeDataString(query)
                           + "&format=rss&count=" + (max * 2).ToString(CultureInfo.InvariantCulture)
                           + "&mkt=" + market + "&setlang=" + market;
                var rss = await context.Http.SendAsync(MakeSpec(rssUrl, settings), ct).ConfigureAwait(false);
                if (!rss.IsSuccess)
                {
                    trace.Append(" rss:HTTP").Append(rss.StatusCode.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    var hits = ParseBingRss(rss.Body, max);
                    if (hits.Count > 0) return hits;
                    trace.Append(" rss:0条/len=").Append((rss.Body ?? "").Length);
                }
            }
            catch (Exception ex)
            {
                trace.Append(" rss:").Append(ex.GetType().Name);
            }

            throw new InvalidOperationException("Bing 未返回可解析结果（" + trace.ToString().Trim() + "）");
        }
        /// <summary>Bing RSS：&lt;item&gt;&lt;title&gt;/&lt;link&gt;/&lt;description&gt;。</summary>
        private static List<SearchHit> ParseBingRss(string xml, int max)
        {
            var hits = new List<SearchHit>();
            if (string.IsNullOrEmpty(xml)) return hits;
            try
            {
                var document = System.Xml.Linq.XDocument.Parse(xml);
                foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "item"))
                {
                    if (hits.Count >= max) break;
                    string title = "", link = "", description = "";
                    foreach (var element in item.Elements())
                    {
                        var name = element.Name.LocalName;
                        if (name == "title") title = element.Value;
                        else if (name == "link") link = element.Value;
                        else if (name == "description") description = element.Value;
                    }
                    if (string.IsNullOrEmpty(link)) continue;
                    // description 里带 HTML 标签，先转纯文本
                    var snippet = WebText.HtmlToText(WebText.DecodeEntities(description));
                    hits.Add(new SearchHit
                    {
                        Title = WebText.DecodeEntities(title).Trim(),
                        Url = link.Trim(),
                        Snippet = snippet
                    });
                }
            }
            catch (Exception)
            {
                // RSS 解析失败：返回空，由调用方退回 HTML
            }
            return hits;
        }
        private static List<SearchHit> ParseBingHtml(string html, int max)
        {
            var hits = new List<SearchHit>();
            if (string.IsNullOrEmpty(html)) return hits;

            foreach (Match block in Regex.Matches(html, "<li[^>]*class=\"[^\"]*b_algo[^\"]*\"[^>]*>(.*?)</li>",
                       RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                if (hits.Count >= max) break;
                var inner = block.Groups[1].Value;

                var link = Regex.Match(inner, "<h2[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!link.Success) continue;

                var url = WebText.DecodeEntities(link.Groups[1].Value).Trim();
                if (string.IsNullOrEmpty(url)) continue;
                if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;

                var title = WebText.DecodeEntities(Regex.Replace(link.Groups[2].Value, "<[^>]+>", "")).Trim();
                var snippet = "";
                var snip = Regex.Match(inner, "<p[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (snip.Success) snippet = WebText.DecodeEntities(Regex.Replace(snip.Groups[1].Value, "<[^>]+>", "")).Trim();

                hits.Add(new SearchHit { Title = title, Url = url, Snippet = snippet });
            }
            return hits;
        }
        private static async Task<List<SearchHit>> SearchDuckDuckGoAsync(
            ToolContext context, SearchSettings settings, string query, int max, CancellationToken ct)
        {
            var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            var response = await context.Http.SendAsync(MakeSpec(url, settings), ct).ConfigureAwait(false);
            if (!response.IsSuccess) return new List<SearchHit>();

            var html = response.Body ?? "";
            var links = DdgLink.Matches(html);
            var snippets = DdgSnippet.Matches(html);
            var hits = new List<SearchHit>();

            for (int i = 0; i < links.Count && hits.Count < max; i++)
            {
                var href = WebText.DecodeEntities(links[i].Groups[1].Value);
                href = UnwrapDdgRedirect(href);
                if (string.IsNullOrEmpty(href) || href.IndexOf("duckduckgo.com", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                hits.Add(new SearchHit
                {
                    Title = WebText.HtmlToText(links[i].Groups[2].Value),
                    Url = href,
                    Snippet = i < snippets.Count ? WebText.HtmlToText(snippets[i].Groups[1].Value) : ""
                });
            }

            return hits;
        }

        /// <summary>DuckDuckGo 的跳转链接里藏着真实地址（uddg 参数）。</summary>
        private static string UnwrapDdgRedirect(string href)
        {
            if (string.IsNullOrEmpty(href)) return href;
            int idx = href.IndexOf("uddg=", StringComparison.Ordinal);
            if (idx < 0) return href;

            var value = href.Substring(idx + 5);
            int amp = value.IndexOf('&');
            if (amp >= 0) value = value.Substring(0, amp);
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch (Exception)
            {
                return href;
            }
        }
    }

    public sealed class FetchPageTool : ToolBase
    {
        private static readonly string[] BlockedHosts = new[] { "google.com", "www.google.com", "googleusercontent.com" };

        public override string Name { get { return "fetch_page"; } }
        public override string Description { get { return "抓取网页正文并转成纯文本。用于读取搜索结果里的具体页面。"; } }
        public override ToolGroup Group { get { return ToolGroup.Network; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("url", "string", "要抓取的完整网址（http 或 https）", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var url = ToolArguments.GetString(argumentsJson, "url", "");
            if (string.IsNullOrWhiteSpace(url)) return ToolResult.Failure("请给出网址。");

            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri))
            {
                return ToolResult.Failure("网址格式不正确。");
            }
            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                return ToolResult.Failure("只支持 http 与 https。");
            }
            if (IsBlocked(uri.Host))
            {
                return ToolResult.Failure("这个域名不允许抓取（服务端会拒绝），请换一个来源。");
            }

            var settings = context.Search ?? new SearchSettings();
            if (!settings.AllowFetchPage)
            {
                return ToolResult.Failure("抓取网页未开启。请到「我的 → 联网搜索」里打开「允许读取搜索页面」。");
            }

            try
            {
                var spec = new HttpRequestSpec
                {
                    Method = HttpMethodKind.Get,
                    Url = uri.ToString(),
                    TimeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30
                };
                spec.AddHeader("User-Agent", string.IsNullOrEmpty(settings.UserAgent)
                    ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
                    : settings.UserAgent);

                var response = await context.Http.SendAsync(spec, ct).ConfigureAwait(false);
                if (!response.IsSuccess) return ToolResult.Failure("抓取失败（HTTP " + response.StatusCode + "）。");

                var text = WebText.HtmlToText(response.Body);
                if (text.Length == 0) return ToolResult.Failure("页面没有可读正文。");
                // 兜底：万一响应是没解开的压缩数据（Content-Encoding 没被处理），
                // HtmlToText 会产出一堆控制字符。看一眼开头，有大量控制字符就明说，
                // 不要把二进制当正文返回给模型（真机上就这么吐出过一屏乱码）。
                if (LooksBinary(text))
                {
                    return ToolResult.Failure("抓到的内容不是文本（疑似压缩响应未解开）。");
                }

                var depth = settings.Depth > 0 ? settings.Depth : 8000;
                var body = TextUtil.Truncate(text, depth);
                return ToolResult.Success(body, body.Length.ToString(CultureInfo.InvariantCulture) + " 字");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return ToolResult.Failure("抓取失败。");
            }
        }

        /// <summary>开头一小段里控制字符占比过高 → 当成二进制，不当正文。</summary>
        private static bool LooksBinary(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int check = Math.Min(512, text.Length);
            int control = 0;
            for (int i = 0; i < check; i++)
            {
                char c = text[i];
                if (c == '\t' || c == '\r' || c == '\n') continue;
                if (c < 32 || (c >= 127 && c < 160)) control++;
            }
            return control * 10 > check;
        }

        private static bool IsBlocked(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;
            foreach (var blocked in BlockedHosts)
            {
                if (string.Equals(host, blocked, StringComparison.OrdinalIgnoreCase)) return true;
                if (host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 记忆
    // ═════════════════════════════════════════════════════════════════════
    public sealed class MemorySearchTool : ToolBase
    {
        public override string Name { get { return "memory_search"; } }
        public override string Description
        {
            get { return "在你与用户过往对话的记忆里检索相关事实。需要回忆用户偏好或旧事时使用。"; }
        }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("query", "string", "要检索的内容", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var query = ToolArguments.GetString(argumentsJson, "query", "");
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Failure("请给出检索内容。");
            if (context.Memory == null) return ToolResult.Failure("记忆功能当前未启用。");

            var hits = await context.Memory.SearchAsync(query, 5, true, ct).ConfigureAwait(false);
            if (hits.Count == 0) return ToolResult.Success("记忆里没有找到相关内容。");

            var sb = new StringBuilder();
            foreach (var hit in hits)
            {
                sb.Append("- [").Append(MemoryCategories.Label(hit.Item.Category)).Append("] ");
                sb.Append(hit.Item.Text);
                sb.Append("（相关度 ").Append(hit.Score.ToString("0.00", CultureInfo.InvariantCulture)).Append("）\n");
            }
            return ToolResult.Success(sb.ToString().Trim(),
                hits.Count.ToString(CultureInfo.InvariantCulture) + " 条记忆");
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 子代理 / MCP / 技能
    // ═════════════════════════════════════════════════════════════════════
    public sealed class SubagentTool : ToolBase
    {
        public override string Name { get { return "spawn_subagent"; } }
        public override string Description
        {
            get { return "派一个子代理独立完成一件子任务，返回它的结论。适合需要多步探索、又不想占用主对话的活。"; }
        }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("task", "string", "交给子代理的完整任务描述，要自包含", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var task = ToolArguments.GetString(argumentsJson, "task", "");
            if (string.IsNullOrWhiteSpace(task)) return ToolResult.Failure("请给出子任务描述。");
            if (context.RunSubagentAsync == null) return ToolResult.Failure("子代理不可用。");

            try
            {
                var output = await context.RunSubagentAsync(task, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(output)) return ToolResult.Success("子代理没有返回内容。");
                return ToolResult.Success(TextUtil.Truncate(output, 4000), "子代理完成");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return ToolResult.Failure("子代理执行失败。");
            }
        }
    }

    public sealed class McpTool : ToolBase
    {
        public override string Name { get { return "mcp_call"; } }
        public override string Description
        {
            get { return "调用已启用的 MCP 服务器上的工具。先用 mcp_list 查看有哪些服务器与工具。"; }
        }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("server", "string", "MCP 服务器名称", true);
            schema.Param("tool", "string", "要调用的工具名", true);
            schema.Param("arguments", "string", "工具参数的 JSON 字符串，没有参数就填 {}", false);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var serverName = ToolArguments.GetString(argumentsJson, "server", "");
            var toolName = ToolArguments.GetString(argumentsJson, "tool", "");
            var args = ToolArguments.GetString(argumentsJson, "arguments", "{}");

            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(toolName))
            {
                return ToolResult.Failure("请给出服务器名称与工具名。");
            }

            McpServerConfig server = null;
            if (context.McpServers != null)
            {
                foreach (var s in context.McpServers)
                {
                    if (string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase)) { server = s; break; }
                }
            }

            if (server == null) return ToolResult.Failure("找不到这个 MCP 服务器：" + serverName);
            if (!server.Enabled) return ToolResult.Failure("这个 MCP 服务器已被禁用。");
            if (server.DisabledTools != null && server.DisabledTools.Contains(toolName)) return ToolResult.Failure("此 MCP 工具已被禁用。");
            if (!string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Failure("目前只支持 http 传输的 MCP 服务器。");
            }

            if (!ToolArguments.IsValidJson(args)) args = ToolArguments.RepairTruncated(args);

            var payload = new Newtonsoft.Json.Linq.JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["name"] = toolName,
                    ["arguments"] = Newtonsoft.Json.Linq.JToken.Parse(args)
                }
            };

            try
            {
                var spec = new HttpRequestSpec
                {
                    Method = HttpMethodKind.Post,
                    Url = server.Url,
                    Body = payload.ToString(),
                    TimeoutSeconds = 60
                };
                spec.AddHeader("Content-Type", "application/json");
                spec.AddHeader("Accept", "application/json");
                if (server.Headers != null)
                {
                    foreach (var kv in server.Headers) spec.AddHeader(kv.Key, kv.Value);
                }

                var response = await context.Http.SendAsync(spec, ct).ConfigureAwait(false);
                if (!response.IsSuccess) return ToolResult.Failure("MCP 调用失败（HTTP " + response.StatusCode + "）。");

                return ToolResult.Success(TextUtil.Truncate(response.Body, Limits.ToolOutputChars), "MCP 返回");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return ToolResult.Failure("MCP 调用失败。");
            }
        }
    }

    public sealed class McpListTool : ToolBase
    {
        public override string Name { get { return "mcp_list"; } }
        public override string Description { get { return "列出当前档案可用的 MCP 服务器及其工具。"; } }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema) { }

        public override Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.McpServers == null || context.McpServers.Count == 0)
            {
                return Task.FromResult(ToolResult.Success("当前没有配置 MCP 服务器。"));
            }

            var sb = new StringBuilder();
            foreach (var server in context.McpServers)
            {
                if (!server.Enabled) continue;
                sb.Append("- ").Append(server.Name).Append("（").Append(server.Transport).Append("）");
                if (server.Tools != null && server.Tools.Count > 0)
                {
                    sb.Append("：").Append(string.Join("、", server.Tools.ToArray()));
                }
                sb.Append('\n');
            }

            var text = sb.ToString().Trim();
            return Task.FromResult(ToolResult.Success(text.Length == 0 ? "没有启用的 MCP 服务器。" : text));
        }
    }

    public sealed class SkillTool : ToolBase
    {
        public override string Name { get { return "skill_use"; } }
        public override string Description { get { return "启用一个技能模板，把它的内容作为本次回答的额外要求。"; } }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("name", "string", "技能名称", true);
        }

        public override Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            var name = ToolArguments.GetString(argumentsJson, "name", "");
            if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(ToolResult.Failure("请给出技能名称。"));
            if (context.Skills == null || context.Skills.Count == 0)
            {
                return Task.FromResult(ToolResult.Failure("当前没有可用技能。"));
            }

            foreach (var skill in context.Skills)
            {
                if (!skill.Enabled) continue;
                if (!string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return Task.FromResult(ToolResult.Success("已启用技能《" + skill.Name + "》。\n" + skill.Content));
            }

            return Task.FromResult(ToolResult.Failure("找不到这个技能：" + name));
        }
    }

    public sealed class SkillListTool : ToolBase
    {
        public override string Name { get { return "skill_list"; } }
        public override string Description { get { return "列出当前档案可用的技能。"; } }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema) { }

        public override Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Skills == null || context.Skills.Count == 0)
            {
                return Task.FromResult(ToolResult.Success("当前没有可用技能。"));
            }

            var sb = new StringBuilder();
            foreach (var skill in context.Skills)
            {
                if (!skill.Enabled) continue;
                sb.Append("- ").Append(skill.Name);
                if (!string.IsNullOrEmpty(skill.Description)) sb.Append("：").Append(skill.Description);
                sb.Append('\n');
            }

            var text = sb.ToString().Trim();
            return Task.FromResult(ToolResult.Success(text.Length == 0 ? "没有启用的技能。" : text));
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 设备（全部经 IDeviceBridge，Core 不认识 WinRT）
    // ═════════════════════════════════════════════════════════════════════
    public sealed class CalendarTool : ToolBase
    {
        public override string Name { get { return "calendar_list"; } }
        public override string Description { get { return "查看日历里接下来的日程。"; } }
        public override ToolGroup Group { get { return ToolGroup.Personal; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("max", "integer", "最多返回几条，默认 10", false);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");
            var max = ToolArguments.GetInt(argumentsJson, "max", 10);
            if (max <= 0 || max > 50) max = 10;

            try
            {
                var items = await context.Device.ListCalendarAsync(max, ct).ConfigureAwait(false);
                if (items == null || items.Count == 0) return ToolResult.Success("近期没有日程。");
                return ToolResult.Success(string.Join("\n", ToArray(items)), items.Count + " 条");
            }
            catch (Exception)
            {
                return ToolResult.Failure("读取日历失败，可能是没有授予日历权限。");
            }
        }

        private static string[] ToArray(IReadOnlyList<string> items)
        {
            var list = new List<string>();
            foreach (var item in items) list.Add("- " + item);
            return list.ToArray();
        }
    }

    public sealed class CalendarCreateTool : ToolBase
    {
        public override string Name { get { return "calendar_create"; } }
        public override string Description { get { return "在日历里新建一条日程。"; } }
        public override ToolGroup Group { get { return ToolGroup.Personal; } }
        public override bool IsSensitive { get { return true; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("title", "string", "日程标题", true);
            schema.Param("start", "string", "开始时间，ISO 8601 格式，例如 2026-10-12T09:00:00", true);
            schema.Param("minutes", "integer", "时长（分钟），默认 60", false);
            schema.Param("notes", "string", "备注", false);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");

            var title = ToolArguments.GetString(argumentsJson, "title", "");
            var startRaw = ToolArguments.GetString(argumentsJson, "start", "");
            if (string.IsNullOrWhiteSpace(title)) return ToolResult.Failure("请给出日程标题。");

            DateTimeOffset start;
            if (!DateTimeOffset.TryParse(startRaw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out start))
            {
                return ToolResult.Failure("开始时间格式不对，请用 ISO 8601，例如 2026-10-12T09:00:00。");
            }

            var minutes = ToolArguments.GetInt(argumentsJson, "minutes", 60);
            if (minutes <= 0 || minutes > 24 * 60) minutes = 60;

            try
            {
                var message = await context.Device.CreateCalendarEventAsync(
                    title, start, minutes, ToolArguments.GetString(argumentsJson, "notes", ""), ct).ConfigureAwait(false);
                return ToolResult.Success(message ?? "已创建日程。");
            }
            catch (Exception)
            {
                return ToolResult.Failure("创建日程失败。");
            }
        }
    }

    public sealed class ContactsTool : ToolBase
    {
        public override string Name { get { return "contacts_search"; } }
        public override string Description { get { return "在联系人里按姓名或号码搜索。"; } }
        public override ToolGroup Group { get { return ToolGroup.Personal; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("query", "string", "姓名或号码片段", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");
            var query = ToolArguments.GetString(argumentsJson, "query", "");
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Failure("请给出搜索内容。");

            try
            {
                var items = await context.Device.SearchContactsAsync(query, 10, ct).ConfigureAwait(false);
                if (items == null || items.Count == 0) return ToolResult.Success("没有找到匹配的联系人。");
                var list = new List<string>();
                foreach (var item in items) list.Add("- " + item);
                return ToolResult.Success(string.Join("\n", list.ToArray()), items.Count + " 位联系人");
            }
            catch (Exception)
            {
                return ToolResult.Failure("读取联系人失败，可能是没有授予联系人权限。");
            }
        }
    }

    public sealed class CallTool : ToolBase
    {
        public override string Name { get { return "make_call"; } }
        public override string Description { get { return "拨打电话。会打开系统电话应用，用户仍需自己按下拨号。"; } }
        public override ToolGroup Group { get { return ToolGroup.Device; } }
        public override bool IsSensitive { get { return true; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("number", "string", "电话号码", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");
            var number = ToolArguments.GetString(argumentsJson, "number",
                ToolArguments.GetString(argumentsJson, "to", ""));
            if (string.IsNullOrWhiteSpace(number)) return ToolResult.Failure("请给出电话号码。");

            var cleaned = NormalizeNumber(number);
            if (cleaned.Length == 0) return ToolResult.Failure("电话号码里没有有效数字。");

            try
            {
                await context.Device.LaunchUriAsync("tel:" + cleaned, ct).ConfigureAwait(false);
                return ToolResult.Success("已打开电话应用并填入 " + cleaned + "。");
            }
            catch (Exception)
            {
                return ToolResult.Failure("无法打开电话应用。");
            }
        }

        internal static string NormalizeNumber(string raw)
        {
            var sb = new StringBuilder();
            foreach (var ch in raw ?? "")
            {
                if (char.IsDigit(ch) || ch == '+' || ch == '-' || ch == ' ' || ch == '(' || ch == ')') sb.Append(ch);
            }
            return sb.ToString().Trim();
        }
    }

    public sealed class SmsTool : ToolBase
    {
        public override string Name { get { return "send_sms"; } }
        public override string Description { get { return "发送短信。会打开系统短信应用，用户仍需自己按下发送。"; } }
        public override ToolGroup Group { get { return ToolGroup.Device; } }
        public override bool IsSensitive { get { return true; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("number", "string", "手机号码", true);
            schema.Param("message", "string", "短信内容", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");

            var number = CallTool.NormalizeNumber(ToolArguments.GetString(argumentsJson, "number",
                ToolArguments.GetString(argumentsJson, "to", "")));
            var message = ToolArguments.GetString(argumentsJson, "message", "");
            if (number.Length == 0) return ToolResult.Failure("请给出手机号码。");

            // 号码里只保留数字（sms: 不支持 + 与分隔符）
            var digits = new StringBuilder();
            foreach (var ch in number)
            {
                if (char.IsDigit(ch) || ch == '+') digits.Append(ch);
            }

            try
            {
                var uri = "sms:" + digits.ToString();
                if (!string.IsNullOrEmpty(message))
                {
                    uri += "?body=" + Uri.EscapeDataString(message);
                }
                await context.Device.LaunchUriAsync(uri, ct).ConfigureAwait(false);
                return ToolResult.Success("已打开短信应用并填入内容。");
            }
            catch (Exception)
            {
                return ToolResult.Failure("无法打开短信应用。");
            }
        }
    }

    public sealed class OpenAppTool : ToolBase
    {
        public override string Name { get { return "open_app"; } }
        public override string Description { get { return "通过协议或应用标识启动一个应用，例如 settings:、ms-settings:、weixin:。"; } }
        public override ToolGroup Group { get { return ToolGroup.Device; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("target", "string", "协议 URI 或应用标识", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");
            var target = ToolArguments.GetString(argumentsJson, "target", "");
            if (string.IsNullOrWhiteSpace(target)) return ToolResult.Failure("请给出要启动的目标。");

            try
            {
                await context.Device.LaunchUriAsync(target.Trim(), ct).ConfigureAwait(false);
                return ToolResult.Success("已尝试启动：" + target);
            }
            catch (Exception)
            {
                return ToolResult.Failure("无法启动这个目标，可能设备上没装对应的应用。");
            }
        }
    }

    public sealed class MediaTool : ToolBase
    {
        public override string Name { get { return "media_control"; } }
        public override string Description { get { return "控制正在播放的媒体：暂停/继续、上一首/下一首、音量。"; } }
        public override ToolGroup Group { get { return ToolGroup.Device; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.ParamEnum("action",
                new string[] { "play_pause", "next", "previous", "volume_up", "volume_down", "mute" },
                "要执行的动作", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");

            var action = ToolArguments.GetString(argumentsJson, "action", "");
            if (string.IsNullOrWhiteSpace(action)) return ToolResult.Failure("请给出动作。");

            var allowed = new[] { "play_pause", "next", "previous", "volume_up", "volume_down", "mute" };
            bool valid = false;
            foreach (var a in allowed)
            {
                if (string.Equals(a, action, StringComparison.OrdinalIgnoreCase)) { action = a; valid = true; break; }
            }
            if (!valid) return ToolResult.Failure("不认识的动作：" + action);

            try
            {
                var message = await context.Device.MediaControlAsync(action, ct).ConfigureAwait(false);
                return ToolResult.Success(message ?? ("已执行 " + action));
            }
            catch (Exception)
            {
                return ToolResult.Failure("媒体控制失败，可能没有正在播放的媒体。");
            }
        }
    }

    public sealed class ReadImageTool : ToolBase
    {
        /// <summary>工具结果用这个前缀告诉 runner：这是一张图，要转成图片消息。</summary>
        public const string Marker = "__IMAGE__:";

        public override string Name { get { return "read_image"; } }
        public override string Description
        {
            get { return "读取档案目录内的一张图片，交给视觉模型查看。请先确认这个模型支持图片输入。"; }
        }
        public override ToolGroup Group { get { return ToolGroup.Files; } }
        public override bool IsSensitive { get { return true; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("path", "string", "相对档案目录的图片路径", true);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Device == null) return ToolResult.Failure("设备能力不可用。");

            var raw = ToolArguments.GetString(argumentsJson, "path", "");
            var path = PathSandbox.ResolveInFiles(context, raw);
            if (path == null) return ToolResult.Failure(PathSandbox.RejectReason(raw));

            var extension = WorkspaceLayout.Extension(raw);
            var mime = MimeForExtension(extension);
            if (mime == null) return ToolResult.Failure("只支持 png / jpg / jpeg / gif / bmp / webp 图片。");

            try
            {
                var base64 = await context.Device.ReadImageAsBase64Async(path, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(base64)) return ToolResult.Failure("图片为空或读取失败。");

                return ToolResult.Success(
                    Marker + mime + ":" + base64,
                    "已读取图片");
            }
            catch (Exception)
            {
                return ToolResult.Failure("读取图片失败。");
            }
        }

        private static string MimeForExtension(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                default: return null;
            }
        }
    }
}
