using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class ProviderModelTests
    {
        private static Workspace CreateWorkspace(InMemoryFileStore store)
        {
            var w = new Workspace(store, new InMemorySecretStore(), NullLogger.Instance, SystemClock.Instance);
            w.InitializeAsync().GetAwaiter().GetResult(); return w;
        }
        private static ChatService Chat(Workspace w, InMemoryFileStore store, IHttpTransport http)
        {
            return new ChatService(w, store, NullLogger.Instance, SystemClock.Instance, http, null, null, null, ToolRegistry.CreateDefault());
        }
        public static void Register()
        {
            Runner.Add("Provider 模型", "能力缓存日期与可空生成参数保存后保持原值", delegate {
                var api = new ApiProfile { ModelsFetchedAtUtc = new DateTimeOffset(2026,10,14,0,0,0,TimeSpan.Zero) };
                api.ModelMetadataCache["known"] = new ModelMetadata { ModelId="known",Vision=true,Tools=false,Source=MetadataSource.Server };
                var copy = api.Clone();
                Check.True(copy.ResolveMetadata("known").Vision==true && copy.ResolveMetadata("known").Tools==false,"能力不会写成空对象");
                Check.Equal(api.ModelsFetchedAtUtc,copy.ModelsFetchedAtUtc,"刷新时间保留");
                var profile = new Profile(); profile.Api.MaxTokens=2048;profile.Api.Temperature=0.75;
                var profileCopy=JsonBridge.Clone(profile);
                Check.Equal(profile.Api.MaxTokens,profileCopy.Api.MaxTokens,"输出 token 数保留");
                Check.Equal(profile.Api.Temperature,profileCopy.Api.Temperature,"温度保留");
                var old=JsonBridge.Deserialize<ModelMetadata>("{\"ModelId\":\"legacy\",\"Vision\":{},\"Tools\":{}}");
                Check.True(old.Vision==null && old.Tools==null,"旧空对象应恢复为未知能力而非不支持");
            });
            Runner.Add("Provider 模型", "根地址版本地址和完整端点都推导为正确模型接口", delegate {
                Check.Equal("https://example.test/v1/models", ModelCatalog.DeriveModelsUrl("https://example.test"), "服务根地址");
                Check.Equal("https://example.test/api/v1/models", ModelCatalog.DeriveModelsUrl("https://example.test/api/v1/"), "自定义前缀");
                Check.Equal("https://example.test/v1/models", ModelCatalog.DeriveModelsUrl("https://example.test/v1/models"), "不重复 models");
                Check.Equal("https://example.test/v1/models?tenant=demo", ModelCatalog.DeriveModelsUrl("https://example.test/v1/chat/completions?tenant=demo"), "保留查询参数");
                Check.Equal("https://example.test/v1/chat/completions", new ApiProfile { Url="https://example.test" }.EffectiveUrl, "对话补全");
                Check.Equal("https://example.test/v1/messages", new ApiProfile { Provider="anthropic",Url="https://example.test/v1" }.EffectiveUrl, "原生协议");
            });
            Runner.Add("Provider 模型", "v1 models 分页去重并使用对应 Provider 认证", delegate {
                var http = new PagedModelsTransport();
                var a = new ApiProfile { Url="https://a.test/v1",CustomHeaders=new Dictionary<string,string> { ["X-Tenant"]="one" } };
                var b = new ApiProfile { Url="https://b.test",ModelsUrl="https://b.test/catalog?locale=zh" };
                var first=ModelCatalog.FetchAsync(http,a,"a-key",NullLogger.Instance,CancellationToken.None).GetAwaiter().GetResult();
                var second=ModelCatalog.FetchAsync(http,b,"b-key",NullLogger.Instance,CancellationToken.None).GetAwaiter().GetResult();
                Check.True(first.Ok && second.Ok,"两个服务都成功");
                Check.EqualInt(2,first.Models.Count,"分页同名模型去重");
                Check.Equal("https://a.test/v1/models",http.Requests[0].Url,"自动端点");
                Check.Contains(http.Requests[1].Url,"after=second","分页游标");
                Check.Equal("https://b.test/catalog?locale=zh",http.Requests[2].Url,"模型端点覆盖");
                Check.Equal("Bearer a-key",http.Requests[0].Headers["Authorization"],"A 密钥");
                Check.Equal("Bearer b-key",http.Requests[2].Headers["Authorization"],"B 密钥");
                Check.Equal("one",http.Requests[1].Headers["X-Tenant"],"分页保留自定义头");
            });
            Runner.Add("Provider 模型", "重复分页游标不会把不完整列表存成成功", delegate {
                var result=ModelCatalog.FetchAsync(new PagedModelsTransport { RepeatCursor=true },new ApiProfile { Url="https://a.test/v1" },"k",NullLogger.Instance,CancellationToken.None).GetAwaiter().GetResult();
                Check.False(result.Ok,"拒绝循环分页");
                Check.Contains(result.Error,"分页未完成","明确原因");
            });
            Runner.Add("Provider 模型", "显式负能力覆盖内置推断并解析输出限制与模态", delegate {
                var m=ModelCatalog.ParseServerEntry(JObject.Parse("{\"id\":\"gpt-4o\",\"context_window\":4096,\"max_output_tokens\":512,\"input_modalities\":[\"text\"],\"output_modalities\":[\"text\",\"image\"],\"capabilities\":{\"vision\":false,\"tools\":false,\"reasoning\":true}}"));
                Check.True(m.Vision==false && m.Tools==false && m.Reasoning==true,"采用服务端显式能力");
                Check.EqualInt(4096,m.ContextWindow,"窗口");
                Check.EqualInt(512,m.MaxOutputTokens,"输出限制");
                Check.True(m.OutputModalities.Contains("image"),"输出图像");
                Check.Equal(MetadataSource.Server,m.Source,"服务端来源");
                var onlyOutput=ModelCatalog.ParseServerEntry(JObject.Parse("{\"id\":\"new-model\",\"architecture\":{\"modality\":\"text->text+image\"}}"));
                Check.True(onlyOutput.Vision==false,"输出图像不代表支持图片输入");
                var unknown=ModelCatalog.ParseServerEntry(JObject.Parse("{\"id\":\"unknown-model-xyz\"}"));
                Check.True(unknown.Vision==null && unknown.Tools==null,"没有信息时保持未知");
                var parameters=ModelCatalog.ParseServerEntry(JObject.Parse("{\"id\":\"gpt-4o\",\"supported_parameters\":[\"temperature\"]}"));
                Check.True(parameters.Tools==false,"服务端能力清单没有工具");
            });
            Runner.Add("Provider 模型", "刷新模型保留旧版手动覆盖与手动模型", delegate {
                var api=new ApiProfile { Model="manual" };
                api.CachedModels.Add("manual");
                api.ModelMetadataCache["manual"]=new ModelMetadata { ModelId="manual",Vision=true,ContextWindow=12345,Source=MetadataSource.Manual };
                var fresh=new ModelListResult { Ok=true };
                fresh.Entries.Add(new ModelMetadata { ModelId="server",Tools=true,Source=MetadataSource.Server });
                api.ApplyModelList(fresh,DateTimeOffset.UtcNow);
                Check.True(api.ManualModels.Contains("manual"),"手动模型仍可切换");
                Check.True(api.ModelOverrides.ContainsKey("manual"),"迁移手动覆盖");
                Check.EqualInt(12345,api.ResolveMetadata("manual").ContextWindow,"能力保留");
                var second=new ModelListResult { Ok=true };
                second.Entries.Add(new ModelMetadata { ModelId="manual",Vision=false,Source=MetadataSource.Server });
                api.ApplyModelList(second,DateTimeOffset.UtcNow);
                Check.True(api.ResolveMetadata("manual").Vision==true,"再次刷新也不覆盖手动设置");
                Check.True(api.ModelMetadataCache["manual"].Vision==false,"服务端数据独立保存");
            });
            Runner.Add("Provider 模型", "获取失败和取消保留原有模型缓存", delegate {
                var api=new ApiProfile(); api.CachedModels.Add("existing");
                Check.Throws<InvalidOperationException>(()=>api.ApplyModelList(new ModelListResult { Ok=false },DateTimeOffset.UtcNow),"不保存失败结果");
                Check.Equal("existing",api.CachedModels[0],"缓存不丢失");
                var cancellation=new CancellationTokenSource();cancellation.Cancel();
                Check.Throws<OperationCanceledException>(()=>ModelCatalog.FetchAsync(new PagedModelsTransport(),api,"k",NullLogger.Instance,cancellation.Token).GetAwaiter().GetResult(),"取消传递");
            });
            Runner.Add("Provider 模型", "多 Provider 同名模型配置往返互不覆盖", delegate {
                var file=new ApiProfileFile();
                var a=new ApiProfile { Name="服务",Model="shared" };var b=new ApiProfile { Name="服务",Model="shared" };
                a.CachedModels.AddRange(new[]{"shared","second"});b.CachedModels.Add("shared");
                a.ModelOverrides["shared"]=new ModelMetadata { ModelId="shared",Vision=true,Source=MetadataSource.Manual };
                b.ModelOverrides["shared"]=new ModelMetadata { ModelId="shared",Vision=false,Source=MetadataSource.Manual };
                file.Items.Add(a);file.Items.Add(b);file.DefaultId=b.Id;
                var copy=JsonBridge.Deserialize<ApiProfileFile>(JsonStore.Serialize(file));
                var catalog=ModelCatalogBuilder.Build(copy,b.Id,"shared");
                Check.EqualInt(3,catalog.Count,"三条不同 Provider 模型");
                Check.True(copy.Find(a.Id).ResolveMetadata("shared").Vision==true,"A 能力");
                Check.True(copy.Find(b.Id).ResolveMetadata("shared").Vision==false,"B 能力");
                Check.EqualInt(1,catalog.Entries.Count(x=>x.IsDefault),"当前选中唯一");
            });
            Runner.Add("Provider 模型", "会话选择独立于 Provider 与档案默认并采用模型窗口", delegate {
                var a=new ApiProfile { Model="a-default" }; var b=new ApiProfile { Model="b-default",MaxContextTokens=999999 };
                b.ModelMetadataCache["b-model"]=new ModelMetadata { ModelId="b-model",ContextWindow=4096,Vision=true,Source=MetadataSource.Server };
                var file=new ApiProfileFile { DefaultId=a.Id };file.Items.Add(a);file.Items.Add(b);
                var p=new Profile();p.Api.ProfileId=a.Id;p.Api.Model="profile-model";p.Api.MaxContextTokens=123456;
                var c=new Conversation { ModelApiProfileId=b.Id,ModelOverride="b-model" };
                var effective=ModelSelection.Resolve(file,p,c);
                Check.Equal(b.Id,effective.Id,"所属 Provider");
                Check.Equal("b-model",effective.Model,"会话模型");
                Check.EqualInt(4096,effective.MaxContextTokens,"不会继承上个模型窗口");
                Check.True(effective.VisionEnabled,"自动应用视觉能力");
                Check.Equal("a-default",a.Model,"服务默认不变");
                Check.Equal("profile-model",p.Api.Model,"档案默认不变");
                c.ModelApiProfileId="removed";
                Check.True(ModelSelection.Resolve(file,p,c)==null,"已删除的 Provider 不偷偷回落到另一密钥");
            });
            Runner.Add("Provider 模型", "已有会话跨 Provider 与同服务模型连续切换实际请求正确", delegate {
                var store=new InMemoryFileStore();var w=CreateWorkspace(store);
                var a=new ApiProfile { Url="https://a.test/v1",Model="a-default" };
                var b=new ApiProfile { Url="https://b.test/v1",Model="b-default" };
                a.CustomBody["model"]="stale-body-model";
                w.ApiProfiles.Items.Add(a);w.ApiProfiles.Items.Add(b);w.ApiProfiles.DefaultId=a.Id;w.SetApiKey(a,"key-a");w.SetApiKey(b,"key-b");
                var p=w.CreateProfileAsync("切换",null).GetAwaiter().GetResult();p.Tools.ToolsEnabled=false;p.Generation.StreamOutput=false;p.Memory.AutoSummarize=false;
                var http=new FakeHttpTransport { NextBody="{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}" };
                var chat=Chat(w,store,http);chat.OpenProfileAsync(p).GetAwaiter().GetResult();var c=chat.StartConversationAsync("",false).GetAwaiter().GetResult();string id=c.Id;
                c.ModelApiProfileId=a.Id;c.ModelOverride="shared";chat.SendAsync("one",null,null,CancellationToken.None).GetAwaiter().GetResult();
                c.ModelApiProfileId=b.Id;c.ModelOverride="shared";chat.SendAsync("two",null,null,CancellationToken.None).GetAwaiter().GetResult();
                c.ModelOverride="b-second";chat.SendAsync("three",null,null,CancellationToken.None).GetAwaiter().GetResult();
                Check.EqualInt(3,http.Requests.Count,"三轮真实构造请求");
                Check.Equal("https://a.test/v1/chat/completions",http.Requests[0].Url,"A 端点");
                Check.Equal("Bearer key-a",http.Requests[0].Headers["Authorization"],"A 密钥");
                Check.Equal("Bearer key-b",http.Requests[1].Headers["Authorization"],"B 密钥");
                Check.Equal("shared",(string)JObject.Parse(http.Requests[0].Body)["model"],"旧自定义 model 不覆盖选择");
                Check.Equal("b-second",(string)JObject.Parse(http.Requests[2].Body)["model"],"同 Provider 第二个模型");
                Check.Equal(id,c.Id,"会话不变");
                Check.EqualInt(3,c.Messages.Count(x=>x.Role==MessageRole.User),"保留三轮历史");
                var stored=w.LoadConversationAsync(p.Slug,c.Id).GetAwaiter().GetResult();
                Check.Equal(b.Id,stored.ModelApiProfileId,"所属服务持久化");
                Check.Equal("b-second",stored.ModelOverride,"模型持久化");
                Check.Equal("a-default",a.Model,"全局默认不变");
            });
            Runner.Add("Provider 模型", "无工具模型不发送工具定义并限制最大输出", delegate {
                var store=new InMemoryFileStore();var w=CreateWorkspace(store);
                var api=new ApiProfile { Url="http://localhost:1234/v1",Model="local",RequiresApiKey=false };
                api.ModelMetadataCache["local"]=new ModelMetadata { ModelId="local",Tools=false,MaxOutputTokens=128,Source=MetadataSource.Server };
                w.ApiProfiles.Items.Add(api);w.ApiProfiles.DefaultId=api.Id;
                var p=w.CreateProfileAsync("p",null).GetAwaiter().GetResult();p.Generation.StreamOutput=false;p.Memory.AutoSummarize=false;p.Tools.ToolsEnabled=true;p.Tools.Enabled.Add("file_list");p.Api.MaxTokens=2000;
                var http=new FakeHttpTransport { NextBody="{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}" };
                var chat=Chat(w,store,http);chat.OpenProfileAsync(p).GetAwaiter().GetResult();chat.StartConversationAsync("",false).GetAwaiter().GetResult();
                var result=chat.SendAsync("hi",null,null,CancellationToken.None).GetAwaiter().GetResult();
                Check.True(result.Outcome!=AgentOutcome.Failed,"无密钥本地服务可请求");
                var body=JObject.Parse(http.Requests[0].Body);
                Check.True(body["tools"]==null,"不发送不支持的工具");
                Check.EqualInt(128,(int)body["max_tokens"],"输出上限");
                Check.False(http.Requests[0].Headers.ContainsKey("Authorization"),"不发送空 Bearer");
            });
            Runner.Add("Provider 模型", "已知不支持图片时不落用户消息也不发请求", delegate {
                var store=new InMemoryFileStore();var w=CreateWorkspace(store);var api=new ApiProfile { Model="text-only",RequiresApiKey=false };
                api.ModelOverrides["text-only"]=new ModelMetadata { ModelId="text-only",Vision=false,Source=MetadataSource.Manual };
                w.ApiProfiles.Items.Add(api);
                var p=w.CreateProfileAsync("p",null).GetAwaiter().GetResult();var http=new FakeHttpTransport();var chat=Chat(w,store,http);
                chat.OpenProfileAsync(p).GetAwaiter().GetResult();var c=chat.StartConversationAsync("",false).GetAwaiter().GetResult();
                var result=chat.SendAsync("hi",new[]{new Attachment { Kind="image",Name="photo.png",Path="x.png" }},null,CancellationToken.None).GetAwaiter().GetResult();
                Check.Equal(AgentOutcome.Failed,result.Outcome,"明确拒绝");
                Check.EqualInt(0,c.Messages.Count,"不留半条用户消息");
                Check.EqualInt(0,http.Requests.Count,"不发送不兼容请求");
            });
            Runner.Add("Provider 模型", "视觉判定以模型元数据为准并回退到两级开关", delegate {
                // 判定依据必须只有一处（ModelSelection.ResolveVision）。分散过一次，
                // 症状是图片被静默丢弃：装配层只看 Provider 的裸布尔量，模型级元数据的"支持"到不了那里。
                var profile=new Profile();
                var api=new ApiProfile { Model="m" };

                // 都没表态 → 不支持
                Check.False(ModelSelection.ResolveVision(profile,api),"全未知默认不支持");

                // Provider 级开关是兜底
                api.VisionEnabled=true;
                Check.True(ModelSelection.ResolveVision(profile,api),"回退 Provider 级");

                // 档案级优先于 Provider 级
                profile.Api.VisionEnabled=false;
                Check.False(ModelSelection.ResolveVision(profile,api),"档案级覆盖 Provider 级");

                // 元数据优先于两级开关（这正是真机上踩到的组合）
                api.ModelOverrides["m"]=new ModelMetadata { ModelId="m",Vision=true,Source=MetadataSource.Manual };
                Check.True(ModelSelection.ResolveVision(profile,api),"元数据覆盖两级开关");
            });
        }
    }

    public sealed class PagedModelsTransport : IHttpTransport
    {
        public bool RepeatCursor;
        public readonly List<HttpRequestSpec> Requests=new List<HttpRequestSpec>();
        public Task<HttpResponseSpec> SendAsync(HttpRequestSpec request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Snapshot: the catalog reuses a request while advancing its cursor.
            var snapshot = new HttpRequestSpec { Url=request.Url,Method=request.Method,Body=request.Body };
            foreach (var header in request.Headers) snapshot.AddHeader(header.Key,header.Value);
            Requests.Add(snapshot);
            bool next=request.Url.Contains("after=");
            string body=request.Url.StartsWith("https://b.test",StringComparison.Ordinal) ? "{\"data\":[{\"id\":\"b-model\"}]}" :
                next && !RepeatCursor ? "{\"data\":[{\"id\":\"shared\"},{\"id\":\"second\"}],\"has_more\":false}" :
                "{\"data\":[{\"id\":\"shared\"}],\"has_more\":true,\"last_id\":\"second\"}";
            return Task.FromResult(new HttpResponseSpec { StatusCode=200,Body=body,ContentType="application/json" });
        }
        public Task StreamLinesAsync(HttpRequestSpec request,Func<string,bool> onLine,CancellationToken ct) { throw new NotSupportedException(); }
    }
}
