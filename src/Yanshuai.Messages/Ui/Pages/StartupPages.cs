using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class OnboardingPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            string step = Context.Section ?? "model";
            if (step == "profile") {
                Title("建第一个档案", "开始使用 · 2 / 3");
                var kind = Choice("从哪里开始", new[] { "通用助手", "工程助理" }, "通用助手");
                var name = Field("名称", "我的助手"); var slug = Field("文件夹名（可选）", "");
                Link<ImportPage>("角色扮演", "从角色卡导入");
                Command("下一步", Symbol.Forward, async delegate {
                    var profile = await W.CreateProfileAsync(Required(name), slug.Text);
                    profile.Tools.ToolsEnabled = kind.SelectedIndex == 1;
                    profile.Persona.Name = profile.Name; await W.SaveProfileAsync(profile);
                    Go<OnboardingPage>("done");
                });
            } else if (step == "done") {
                Title("准备好了", "开始使用 · 3 / 3");
                Text(W.ApiProfiles.Items.Count + " 个模型配置 · " + W.Profiles.Count + " 个档案");
                Text("新会话先以草稿打开，发送第一条消息后保存。", true);
                Command("开始使用", Symbol.Accept, async delegate { W.Settings.OnboardingComplete = true; await W.SaveSettingsAsync(); Context.Close(); });
            } else {
                Title("连接一个模型", "开始使用 · 1 / 3");
                var service = Choice("服务", new[] { "DeepSeek", "OpenAI", "Anthropic", "Gemini", "自定义" }, "DeepSeek");
                var url = Field("自定义地址（可选）", "");
                var model = Field("模型 id", "deepseek-chat");
                var key = Secret("API 密钥", "");
                Text("密钥仅保存到系统凭据保险库。", true);
                Command("测试并继续", Symbol.Forward, async delegate {
                    var api = new ApiProfile { Name=(string)service.SelectedItem,Model=Required(model),
                        Provider=service.SelectedIndex==2?"anthropic":service.SelectedIndex==3?"gemini":"openai" };
                    api.Url=url.Text.Trim();
                    if(service.SelectedIndex==0 && api.Url.Length==0)api.Url="https://api.deepseek.com/chat/completions";
                    var provider = ProviderFactory.Create(api,S.Http,S.Log);
                    var request = new ChatRequest { Api=api,ApiKey=key.Password,MaxTokens=16 };
                    request.Turns.Add(ChatTurn.User("回复 ok 即可。"));
                    var result=await provider.CompleteAsync(request,Lifetime.Token);
                    if(!result.Ok)throw new InvalidOperationException(result.ErrorMessage);
                    var models = await ModelCatalog.FetchAsync(S.Http,api,key.Password,S.Log,Lifetime.Token);
                    if (models.Ok) api.ApplyModelList(models,S.Clock.UtcNow); else api.ModelsFetchError=models.Error;
                    W.ApiProfiles.Items.Add(api);W.ApiProfiles.DefaultId=api.Id;W.SetApiKey(api,key.Password);await W.SaveApiProfilesAsync();
                    Go<OnboardingPage>("profile");
                });
                Command("以后再说", Symbol.Forward, delegate { Go<OnboardingPage>("profile");return Task.CompletedTask; });
                Link<MigrationPage>("迁移旧版数据", "先检查 yanshuaiAppData.json");
            }
            return Task.CompletedTask;
        }
    }

    public sealed class MigrationPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var plan = Context.Item as LegacyImportPlan;
            if(plan == null) {
                Title("迁移旧版数据", "数据导入");
                Text("选择旧版导出的 yanshuaiAppData.json；只读取源文件，不会删除或修改它。");
                Command("选择文件", Symbol.OpenFile, async delegate {
                    RequireIdle();
                    var picker=new FileOpenPicker { SuggestedStartLocation=PickerLocationId.DocumentsLibrary }; picker.FileTypeFilter.Add(".json");
                    var file=await picker.PickSingleFileAsync();if(file==null)return;
                    var bytes=await FilesPage.ReadBytes(file,64*1024*1024);
                    var preview=LegacyImporter.Preview(System.Text.Encoding.UTF8.GetString(bytes,0,bytes.Length));
                    Go<MigrationPage>("preview",preview);
                });
            } else {
                Title("迁移数据检查", "旧版言枢");
                var apis=Check(plan.Apis.Count+" 个模型档案（需重新填密钥）",true);
                var chats=Check(plan.Conversations.Count+" 个会话",true);
                var images=Check(plan.Images.Count+" 张图片",true);
                var memories=Check("会话长期记忆",true);
                Text(plan.Profiles.Count+" 个角色档案将创建为独立目录。",true);
                foreach(var warning in plan.Warnings)Text(warning,true);
                Command("开始迁移",Symbol.Import,async delegate {
                    RequireIdle();
                    string backupPath="_migration-backup/"+BackupNaming.DefaultFileName(DateTimeOffset.Now);
                    using(var stream=new MemoryStream()) {
                        var backup=await S.Backup.CreateAsync(stream,W,"1.1.0",S.Device.DeviceFamily,null,Lifetime.Token);
                        if(backup.Ok)await S.Files.WriteBytesAsync(backupPath,stream.ToArray());
                        else if(W.Profiles.Count>0)throw new InvalidOperationException("迁移前备份失败："+backup.Error);
                    }
                    var count=await LegacyImporter.ImportAsync(W,S.Files,plan,apis.IsChecked==true,chats.IsChecked==true,images.IsChecked==true,memories.IsChecked==true,Lifetime.Token);
                    Title("迁移完成","数据");
                    Text("已迁移 "+count+" 个会话。旧文件保留，API 密钥需重新填写。");
                    Command("完成",Symbol.Accept,delegate { Context.Close();return Task.CompletedTask; });
                });
            }
            return Task.CompletedTask;
        }
    }
}
