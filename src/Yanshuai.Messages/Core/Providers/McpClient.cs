using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    // Streamable HTTP: negotiate, preserve the session header, then discover/call tools.
    public sealed class McpClient
    {
        private readonly IHttpTransport _http;
        private readonly McpServerConfig _server;
        private string _session = "";
        private string _version = "2025-06-18";
        private int _id;
        public Dictionary<string, string> ToolDescriptions { get; private set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ToolSchemas { get; private set; } = new Dictionary<string, string>();
        public McpClient(IHttpTransport http, McpServerConfig server) { _http = http; _server = server; }

        public async Task InitializeAsync(CancellationToken ct)
        {
            Uri endpoint;
            if (_server == null || _server.Transport != "http"
                || !Uri.TryCreate(_server.Url, UriKind.Absolute, out endpoint)
                || (endpoint.Scheme != "http" && endpoint.Scheme != "https"))
                throw new InvalidOperationException("请配置有效的 MCP HTTP 地址。");
            var result = await RequestAsync("initialize", new JObject {
                ["protocolVersion"] = _version, ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "Yanshuai.Messages", ["version"] = "1.0" }
            }, ct).ConfigureAwait(false);
            var negotiated = (string)result["protocolVersion"];
            if (!string.IsNullOrEmpty(negotiated)) _version = negotiated;
            await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct).ConfigureAwait(false);
        }

        public async Task<List<string>> ListToolsAsync(CancellationToken ct)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var tools = new List<string>(); var seen = new HashSet<string>();
            string cursor = null;
            for (int page = 0; page < 100; page++)
            {
                var parameters = new JObject();
                if (cursor != null) parameters["cursor"] = cursor;
                var result = await RequestAsync("tools/list", parameters, ct).ConfigureAwait(false);
                var array = result["tools"] as JArray;
                if (array != null) foreach (var tool in array)
                {
                    var name = (string)tool["name"];
                    if (!string.IsNullOrEmpty(name)) { if (!tools.Contains(name)) tools.Add(name); ToolDescriptions[name] = (string)tool["description"] ?? ""; ToolSchemas[name] = tool["inputSchema"] != null ? tool["inputSchema"].ToString() : "{}"; }
                }
                cursor = (string)result["nextCursor"];
                if (string.IsNullOrEmpty(cursor)) return tools;
                if (!seen.Add(cursor)) throw new InvalidOperationException("MCP 返回重复分页游标。");
            }
            throw new InvalidOperationException("MCP 工具列表超过分页上限。");
        }

        public async Task<ToolResult> CallAsync(string name, string arguments, CancellationToken ct)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            var result = await RequestAsync("tools/call", new JObject {
                ["name"] = name, ["arguments"] = JObject.Parse(arguments)
            }, ct).ConfigureAwait(false);
            var text = new StringBuilder();
            var parts = result["content"] as JArray;
            if (parts != null) foreach (var part in parts)
            {
                if ((string)part["type"] == "text") text.AppendLine((string)part["text"]);
            }
            var output = text.Length > 0 ? text.ToString() : result.ToString();
            if ((bool?)result["isError"] == true) return ToolResult.Failure(TextUtil.Truncate(output, Limits.ToolOutputChars));
            return ToolResult.Success(TextUtil.Truncate(output, Limits.ToolOutputChars), "MCP 返回");
        }

        private async Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _id);
            var response = await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id,
                ["method"] = method, ["params"] = parameters }, ct).ConfigureAwait(false);
            JObject message = null;
            if ((response.ContentType ?? "").IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var data = new StringBuilder();
                foreach (var line in (response.Body ?? "").Replace("\r", "").Split('\n'))
                {
                    if (line.Length == 0 && data.Length > 0) {
                        var candidate = JObject.Parse(data.ToString()); data.Clear();
                        if ((int?)candidate["id"] == id) { message = candidate; break; }
                    } else if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line.Substring(5).TrimStart());
                }
                if (message == null && data.Length > 0) {
                    var candidate = JObject.Parse(data.ToString());
                    if ((int?)candidate["id"] == id) message = candidate;
                }
            }
            else message = JObject.Parse(response.Body ?? "{}");
            if (message == null || (int?)message["id"] != id) throw new InvalidOperationException("MCP 响应缺少对应的请求 id。");
            if (message["error"] != null) throw new InvalidOperationException("MCP 请求失败：" + (string)message["error"]["message"]);
            var result = message["result"] as JObject;
            if (result == null) throw new InvalidOperationException("MCP 返回无效结果。");
            return result;
        }

        private async Task<HttpResponseSpec> SendAsync(JObject payload, CancellationToken ct)
        {
            var spec = new HttpRequestSpec { Method = HttpMethodKind.Post, Url = _server.Url,
                Body = payload.ToString(), TimeoutSeconds = 60 };
            if (_server.Headers != null) foreach (var header in _server.Headers) spec.AddHeader(header.Key, header.Value);
            spec.AddHeader("Content-Type", "application/json");
            spec.AddHeader("Accept", "application/json, text/event-stream");
            if (_session.Length > 0) spec.AddHeader("Mcp-Session-Id", _session);
            spec.AddHeader("MCP-Protocol-Version", _version);
            var response = await _http.SendAsync(spec, ct).ConfigureAwait(false);
            if (!response.IsSuccess) throw new InvalidOperationException("MCP 连接失败（HTTP " + response.StatusCode + "）。");
            string session;
            if (response.Headers.TryGetValue("Mcp-Session-Id", out session)) _session = session;
            return response;
        }
    }
}
