using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class McpToolsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var server = Context.Item as McpServerConfig;
            if (server == null) throw new InvalidOperationException("请选择服务器。");
            Title("MCP 工具清单", server.Name);
            var selections = new Dictionary<string,CheckBox>();
            foreach (var name in server.Tools) {
                selections[name] = Check(name, server.DisabledTools == null || !server.DisabledTools.Contains(name));
                var tool = name; Row("查看 " + name + " 的输入定义", server.ToolDescriptions.ContainsKey(name) ? server.ToolDescriptions[name] : "刷新后获取元数据", delegate { Go<McpSchemaPage>(tool, server); return Task.CompletedTask; });
            }
            if (selections.Count == 0) Text("尚未发现工具。先连接服务器。");
            Save(async delegate {
                RequireIdle(); server.DisabledTools = selections.Where(x=>x.Value.IsChecked!=true).Select(x=>x.Key).ToList();
                await W.SaveMcpAsync();
            });
            Command("重新拉取",Symbol.Refresh,async delegate {
                var resolved=JsonBridge.Clone(server);var key=S.Secrets.Get("mcp."+server.Id);
                if(!string.IsNullOrEmpty(key))resolved.Headers["Authorization"]="Bearer "+key;
                var client = new McpClient(S.Http,resolved);
                server.Tools = await client.ListToolsAsync(Lifetime.Token);
                server.ToolDescriptions = client.ToolDescriptions; server.ToolSchemas = client.ToolSchemas;
                await W.SaveMcpAsync();await BuildAsync();
            },true);
            return Task.CompletedTask;
        }
    }
}
