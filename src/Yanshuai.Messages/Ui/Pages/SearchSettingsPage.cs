using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class SearchSettingsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("联网搜索", "模型与能力");
            var source=W.Settings.Search;
            var provider=Choice("搜索源",new[] { "duckduckgo","bing","tavily","searxng" },source.Provider);
            var url=Field("服务地址（可选）",source.BaseUrl);
            var key=Secret("服务密钥（可选）",source.ApiKey);
            var count=Field("最多结果条数",source.MaxResults.ToString());
            var depth=Field("每条结果最多字数（0 不限）",source.Depth.ToString());
            var timeout=Field("请求超时秒数",source.TimeoutSeconds.ToString());
            var fetch=Toggle("允许抓取网页",source.AllowFetchPage);
            Save(async delegate {
                RequireIdle();
                var draft=JsonBridge.Clone(source);draft.Provider=(string)provider.SelectedItem;draft.BaseUrl=url.Text.Trim();
                draft.ApiKey=key.Password;draft.MaxResults=Int(count,1,50);draft.Depth=Int(depth,0,1000000);
                draft.TimeoutSeconds=Int(timeout,1,120);draft.AllowFetchPage=fetch.IsOn;
                W.Settings.Search=draft;await W.SaveSettingsAsync();
            });
            return Task.CompletedTask;
        }
    }
}
