using System;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;
namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class McpSchemaPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var server=(McpServerConfig)Context.Item;string name=Context.Section;
            Title(name,"MCP 工具");
            Text(server.ToolDescriptions.ContainsKey(name)?server.ToolDescriptions[name]:"尚未取得描述。");
            var box=Field("输入 schema",server.ToolSchemas.ContainsKey(name)?server.ToolSchemas[name]:"刷新服务器工具清单后可查看。",true);
            box.IsReadOnly=true;return Task.CompletedTask;
        }
    }
}
