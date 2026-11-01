using System;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class ApiProbe
    {
        public ApiProfile Api;
        public string Key;
    }
    public sealed class ApiTestPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            var probe = Context.Item as ApiProbe;
            if (probe == null) throw new InvalidOperationException("没有要测试的模型配置。");
            Title("连接测试结果", probe.Api.Name);
            Text(new Uri(probe.Api.EffectiveUrl).Host, true);
            var list = await ModelCatalog.FetchAsync(S.Http, probe.Api, probe.Key, S.Log, Lifetime.Token);
            if (list.Ok) {
                Text("模型列表请求成功 · " + list.Models.Count + " 个模型");
                Text("服务器接受了本次模型列表请求。", true);
            } else Text("模型列表：" + list.Error);
            Section("最小消息请求");
            if (!string.IsNullOrEmpty(probe.Api.Model)) {
                var result = await ModelCatalog.TestConnectionAsync(S.Http, probe.Api, probe.Key, probe.Api.Model, S.Log, Lifetime.Token);
                Text(result);
            } else Text("未选择默认模型。保存 Provider 并选择模型后，可以测试消息请求。");
            Text("此测试仅发送「回复 ok 即可」，不会包含工作区或会话内容。", true);
            Command("重新测试", Windows.UI.Xaml.Controls.Symbol.Refresh, BuildAsync);
        }
    }
}
