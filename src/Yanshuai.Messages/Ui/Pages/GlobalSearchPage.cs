using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class GlobalSearchPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("全局搜索","工作区");
            var query=Field("搜索档案、会话正文与记忆","");
            var results=new StackPanel();Body.Children.Add(results);
            Command("搜索",Symbol.Find,async delegate {
                results.Children.Clear();
                var hits=await new WorkspaceSearch(W,S.Files,S.Log).SearchAsync(Required(query),Lifetime.Token);
                if(hits.Count==0){Status.Text="没有匹配的结果。";return;}
                Status.Text=hits.Count+" 项结果（最多 100 项）";
                foreach(var hit in hits) {
                    var item=hit;
                    var panel=new StackPanel();
                    panel.Children.Add(new TextBlock { Text=item.Title,TextWrapping=Windows.UI.Xaml.TextWrapping.Wrap,FontSize=15 });
                    panel.Children.Add(new TextBlock { Text=item.Kind+" · "+item.Preview,TextWrapping=Windows.UI.Xaml.TextWrapping.Wrap,FontSize=12,Foreground=Brush("MidBrush") });
                    var row=new Button { Content=panel,HorizontalAlignment=Windows.UI.Xaml.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment=Windows.UI.Xaml.HorizontalAlignment.Stretch,MinHeight=56,Background=Brush("BgBrush") };
                    row.Click+=async delegate { await Safe(async delegate {
                        Context.Profile=W.FindProfile(item.ProfileId);Context.Conversation=null;
                        if(item.Kind=="profile")Go<ProfilePage>();
                        else if(item.Kind=="conversation") {
                            RequireIdle();var conversation=await W.LoadConversationAsync(Context.Profile.Slug,item.Id);
                            await Context.OpenChat(Context.Profile,conversation);
                        } else {
                            var memory=new MemoryService(S.Files,S.Log,S.Clock,S.Embedder);
                            if(Context.Profile!=null)await memory.LoadAsync(Context.Profile.Slug);
                            await memory.LoadGlobalAsync(true);
                            var found=memory.Find(item.Id,item.Kind=="global-memory"?MemoryScope.Global:MemoryScope.Profile);
                            if(found==null)throw new InvalidOperationException("此记忆已不存在。");
                            Go<MemoryEditorPage>(null,new MemoryEditContext { Memory=memory,Item=found });
                        }
                    }); };
                    results.Children.Add(row);
                }
            });
            return Task.CompletedTask;
        }
    }
}
