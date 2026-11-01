using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class ManagementTests
    {
        private static Workspace WorkspaceFor(InMemoryFileStore store)
        {
            var workspace = new Workspace(store, new InMemorySecretStore(), NullLogger.Instance, SystemClock.Instance);
            workspace.InitializeAsync().GetAwaiter().GetResult(); return workspace;
        }
        public static void Register()
        {
            Runner.Add("页面后端", "会话回收恢复同时重建索引", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var p = w.CreateProfileAsync("甲", null).GetAwaiter().GetResult();
                var c = w.CreateConversation(p, "会话"); c.Messages.Add(new Message { Role = MessageRole.User, Content = "hello" });
                w.SaveConversationAsync(p.Slug, c).GetAwaiter().GetResult();
                var service = new WorkspaceOperations(w, store, NullLogger.Instance, SystemClock.Instance);
                service.RecycleConversationAsync(p, c).GetAwaiter().GetResult();
                Check.EqualInt(0, w.ListConversationsAsync(p.Slug, true).GetAwaiter().GetResult().Count, "回收后索引移除");
                var entry = service.ListTrashAsync().GetAwaiter().GetResult()[0];
                service.RestoreAsync(entry).GetAwaiter().GetResult();
                Check.EqualInt(1, w.ListConversationsAsync(p.Slug, true).GetAwaiter().GetResult().Count, "恢复后索引可见");
                Check.EqualInt(0, service.ListTrashAsync().GetAwaiter().GetResult().Count, "移除回收元数据");
            });
            Runner.Add("页面后端", "档案回收保留二进制并可恢复", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var p = w.CreateProfileAsync("照片", null).GetAwaiter().GetResult();
                var data = new byte[] { 0, 255, 17, 98 };
                store.WriteBytesAsync(p.Slug + "/files/image.png", data).GetAwaiter().GetResult();
                var service = new WorkspaceOperations(w, store, NullLogger.Instance, SystemClock.Instance);
                service.RecycleProfileAsync(p).GetAwaiter().GetResult();
                Check.EqualInt(0, w.Profiles.Count, "工作区不显示回收档案");
                service.RestoreAsync(service.ListTrashAsync().GetAwaiter().GetResult()[0]).GetAwaiter().GetResult();
                Check.EqualInt(1, w.Profiles.Count, "档案恢复");
                Check.True(data.SequenceEqual(store.PeekBytes(p.Slug + "/files/image.png")), "二进制未损坏");
            });
            Runner.Add("页面后端", "恢复不覆盖同名文件", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var p = w.CreateProfileAsync("甲", null).GetAwaiter().GetResult();
                string path = p.Slug + "/files/note.txt";
                store.WriteTextAsync(path, "old").GetAwaiter().GetResult();
                var service = new WorkspaceOperations(w, store, NullLogger.Instance, SystemClock.Instance);
                var entry = service.RecycleFileAsync(path, "note", "file").GetAwaiter().GetResult();
                store.WriteTextAsync(path, "new").GetAwaiter().GetResult();
                Check.Throws<InvalidOperationException>(()=> service.RestoreAsync(entry).GetAwaiter().GetResult(), "冲突拒绝");
                Check.Equal("new", store.Peek(path), "现有文件保留");
                Check.True(store.ExistsAsync(entry.StoredPath).GetAwaiter().GetResult(), "回收源保留");
            });
            Runner.Add("页面后端", "跨档案移动复制历史与候选附件", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var from = w.CreateProfileAsync("甲", null).GetAwaiter().GetResult();
                var to = w.CreateProfileAsync("乙", null).GetAwaiter().GetResult();
                var c = w.CreateConversation(from, "移动"); var m = new Message { Role = MessageRole.User, Content = "test" };
                string path = from.Slug + "/files/attachments/a.png";
                store.WriteBytesAsync(path, new byte[] { 1, 2, 255 }).GetAwaiter().GetResult();
                m.Attachments.Add(new Attachment { Kind = "image", Path = path });
                c.Messages.Add(m); var variant = m.Clone(); variant.Id = Guid.NewGuid().ToString("N"); c.AddVariant(0, variant);
                w.SaveConversationAsync(from.Slug, c).GetAwaiter().GetResult();
                new WorkspaceOperations(w, store, NullLogger.Instance, SystemClock.Instance).MoveConversationAsync(from, to, c).GetAwaiter().GetResult();
                var moved = w.LoadConversationAsync(to.Slug, c.Id).GetAwaiter().GetResult();
                Check.Equal(to.Id, moved.ProfileId, "新所属档案");
                foreach (var msg in moved.Messages.Concat(moved.Variants.Values.SelectMany(x=>x)))
                    Check.True(store.ExistsAsync(msg.Attachments[0].Path).GetAwaiter().GetResult(), "全部候选附件可读");
                Check.EqualInt(0, w.ListConversationsAsync(from.Slug, true).GetAwaiter().GetResult().Count, "旧索引移除");
                Check.EqualInt(1, w.ListConversationsAsync(to.Slug, true).GetAwaiter().GetResult().Count, "新索引有数据");
            });
            Runner.Add("页面后端", "编辑文件发现并保护外部修改", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var service = new WorkspaceOperations(w, store, NullLogger.Instance, SystemClock.Instance);
                store.WriteTextAsync("p/files/a.txt", "original").GetAwaiter().GetResult();
                string hash = TextUtil.Sha256Hex("original");
                store.WriteTextAsync("p/files/a.txt", "outside").GetAwaiter().GetResult();
                Check.False(service.SaveTextAsync("p/files/a.txt", hash, "inside", false).GetAwaiter().GetResult(), "检测冲突");
                Check.Equal("outside", store.Peek("p/files/a.txt"), "未覆盖");
                Check.True(service.SaveTextAsync("p/files/a.txt", hash, "inside", true).GetAwaiter().GetResult(), "显式覆盖");
            });
            Runner.Add("页面后端", "新建文件冲突和路径跳转被拒绝", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var service = new WorkspaceOperations(w, store, NullLogger.Instance, SystemClock.Instance);
                store.WriteTextAsync("p/files/a.txt", "exists").GetAwaiter().GetResult();
                Check.False(service.SaveTextAsync("p/files/a.txt", null, "new", false).GetAwaiter().GetResult(), "新建不覆盖");
                Check.Throws<ArgumentException>(()=>WorkspaceOperations.ValidatePath("p/../_settings.json"), "不能穿越");
                Check.Throws<ArgumentException>(()=>WorkspaceOperations.ValidatePath("C:/secret"), "不能绝对路径");
            });
            Runner.Add("页面后端", "重建索引不改变正文时间戳", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var p = w.CreateProfileAsync("甲", null).GetAwaiter().GetResult();
                var c = w.CreateConversation(p, "历史"); w.SaveConversationAsync(p.Slug, c).GetAwaiter().GetResult();
                string before = store.Peek(WorkspaceLayout.ConversationFile(p.Slug, c.Id));
                store.WriteTextAsync(WorkspaceLayout.ConversationIndexFile(p.Slug), "{broken").GetAwaiter().GetResult();
                new WorkspaceOperations(w, store, NullLogger.Instance, SystemClock.Instance).RebuildIndexAsync(p).GetAwaiter().GetResult();
                Check.Equal(before, store.Peek(WorkspaceLayout.ConversationFile(p.Slug,c.Id)), "正文不变");
                Check.EqualInt(1,w.ListConversationsAsync(p.Slug,true).GetAwaiter().GetResult().Count,"索引修复");
            });
            Runner.Add("页面后端", "正则发给模型但不改历史与工具参数", delegate {
                var p = new Profile(); var c = new Conversation();
                c.Messages.Add(new Message { Role = MessageRole.User, Content = "old value" });
                var ctx = new PromptContext { Profile = p, Conversation = c,
                    RegexRules = new List<RegexTransform> { new RegexTransform { Find = "old", Replace = "new", Scope = "user" } } };
                var prompt = PromptAssembler.Build(ctx);
                Check.Contains(prompt.Turns.Last().Content,"new value","发送时转换");
                Check.Equal("old value",c.Messages[0].Content,"原文保留");
            });
            Runner.Add("页面后端", "收藏折叠与正则设置可往返", delegate {
                var settings = new GlobalSettings(); settings.FavoriteModels.Add("provider/model");
                settings.CollapsedProviders.Add("provider"); settings.RegexRules.Add(new RegexTransform { Find = "hello", Replace = "你好", VisualOnly = true });
                var copy = JsonBridge.Deserialize<GlobalSettings>(JsonStore.Serialize(settings));
                Check.EqualInt(1,copy.RegexRules.Count,"规则保留");
                Check.True(copy.FavoriteModels.Contains("provider/model"),"收藏保留");
                Check.True(copy.CollapsedProviders.Contains("provider"),"折叠保留");
            });
            Runner.Add("页面后端", "编辑记忆丢弃旧向量", delegate {
                var store = new InMemoryFileStore();
                var file = new MemoryFile(); var item = new MemoryItem { Text = "旧记忆" }; file.Items.Add(item);
                file.Vectors[item.Id] = new float[] { 1, 0 };
                store.WriteTextAsync(WorkspaceLayout.MemoryFile("p"), JsonStore.Serialize(file)).GetAwaiter().GetResult();
                var memory = new MemoryService(store,NullLogger.Instance,SystemClock.Instance,null); memory.LoadAsync("p").GetAwaiter().GetResult();
                memory.Update(item.Id,MemoryScope.Profile,"新的记忆",MemoryCategory.Fact,.8); memory.SaveAsync().GetAwaiter().GetResult();
                var after = JsonBridge.Deserialize<MemoryFile>(store.Peek(WorkspaceLayout.MemoryFile("p")));
                Check.False(after.Vectors.ContainsKey(item.Id),"旧向量移除");
                Check.Equal("新的记忆",after.Items[0].Text,"文本保存");
            });
            Runner.Add("页面后端", "追问工具等待并传回用户回答", delegate {
                var interaction = new FakeInteraction(); var answer = new AskAnswer { Id="answer" }; answer.Values.Add("选择 A"); interaction.Answers.Add(answer);
                var result = new AskUserTool().InvokeAsync("{\"question\":\"选择哪个？\",\"options\":\"[\\\"A\\\",\\\"B\\\"]\"}",
                    new ToolContext { Interaction = interaction }, CancellationToken.None).GetAwaiter().GetResult();
                Check.True(result.Ok,"调用成功"); Check.Contains(result.Content,"选择 A","答复传回模型");
            });
            Runner.Add("页面后端", "草稿不落盘直到第一条消息", delegate {
                var store=new InMemoryFileStore();var w=WorkspaceFor(store);
                var p=w.CreateProfileAsync("draft",null).GetAwaiter().GetResult();
                var chat=new ChatService(w,store,NullLogger.Instance,SystemClock.Instance,new FakeHttpTransport(),null,null,null,ToolRegistry.CreateDefault());
                chat.OpenProfileAsync(p).GetAwaiter().GetResult();
                var draft=chat.StartConversationAsync("",false).GetAwaiter().GetResult();
                Check.True(chat.IsDraft,"草稿标记");
                Check.EqualInt(0,w.ListConversationsAsync(p.Slug,true).GetAwaiter().GetResult().Count,"不留空索引");
                Check.False(store.ExistsAsync(WorkspaceLayout.ConversationFile(p.Slug,draft.Id)).GetAwaiter().GetResult(),"不写空正文");
            });
            Runner.Add("页面后端", "会话模型覆盖使用对应服务密钥", delegate {
                var store=new InMemoryFileStore();var w=WorkspaceFor(store);
                var first=new ApiProfile { Model="first",Url="https://first.test/v1/chat/completions" };
                var second=new ApiProfile { Model="second",Url="https://second.test/v1/chat/completions" };
                w.ApiProfiles.Items.Add(first);w.ApiProfiles.Items.Add(second);w.ApiProfiles.DefaultId=first.Id;w.SetApiKey(second,"second-key");
                var p=w.CreateProfileAsync("p",null).GetAwaiter().GetResult();p.Tools.ToolsEnabled=false;p.Generation.StreamOutput=false;
                var http=new FakeHttpTransport { NextBody="{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}" };
                var chat=new ChatService(w,store,NullLogger.Instance,SystemClock.Instance,http,null,null,null,ToolRegistry.CreateDefault());
                chat.OpenProfileAsync(p).GetAwaiter().GetResult();
                var conversation=chat.StartConversationAsync("",false).GetAwaiter().GetResult();
                conversation.ModelApiProfileId=second.Id;conversation.ModelOverride="override-model";
                chat.SendAsync("hi",null,null,CancellationToken.None).GetAwaiter().GetResult();
                Check.True(http.Requests.Count>0,"不会被默认服务缺密钥拦下");
                Check.Equal("Bearer second-key",http.Requests[0].Headers["Authorization"],"对应服务凭据");
                Check.Contains(http.Requests[0].Body,"override-model","会话模型生效");
                Check.False(chat.IsDraft,"首条发送后正式保存");
            });
            Runner.Add("页面后端", "旧数据迁移不持久化明文密钥且保存分支和图片", delegate {
                var source=new JObject {
                    ["ApiProfiles"]=new JArray(new JObject { ["Id"]="a",["Name"]="旧模型",["ApiKey"]="plain-secret",["Model"]="m" }),
                    ["CharacterCards"]=new JArray(new JObject { ["Id"]="c",["Name"]="旧角色",["Description"]="persona" }),
                    ["Conversations"]=new JArray(new JObject { ["CharacterCardId"]="c",["Title"]="旧对话",["ApiProfileId"]="a",
                        ["CreatedAt"]="/Date(0)/",["AllBranches"]=new JArray(
                            new JObject { ["Messages"]=new JArray(new JObject { ["Role"]="user",["Content"]="你好",["ImageBase64"]="AQID",["Timestamp"]="/Date(0)/" }) },
                            new JObject { ["Messages"]=new JArray(new JObject { ["Role"]="assistant",["Content"]="另一分支" }) }) })
                };
                var plan=LegacyImporter.Preview(source.ToString());Check.EqualInt(2,plan.Conversations.Count,"所有分支保留");
                var store=new InMemoryFileStore();var w=WorkspaceFor(store);
                LegacyImporter.ImportAsync(w,store,plan,true,true,true,true,CancellationToken.None).GetAwaiter().GetResult();
                Check.NotContains(store.Peek(WorkspaceLayout.SharedApiProfiles),"plain-secret","密钥不落盘");
                Check.EqualInt(2,w.ListAllConversationsAsync(true).GetAwaiter().GetResult().Count,"迁移索引");
                var entry=w.ListAllConversationsAsync(true).GetAwaiter().GetResult().First(x=>x.Title=="旧对话");
                var profile=w.FindProfile(entry.ProfileId);var c=w.LoadConversationAsync(profile.Slug,entry.Id).GetAwaiter().GetResult();
                Check.EqualInt(1970,c.CreatedAtUtc.Year,"历史时间保留");
                Check.True(store.ExistsAsync(c.Messages[0].Attachments[0].Path).GetAwaiter().GetResult(),"图片可读");
            });
            Runner.Add("页面后端", "清空技能选择后不再启用全部", delegate {
                var w=WorkspaceFor(new InMemoryFileStore());w.Skills.Add(new SkillDefinition { Name="a" });
                var profile=new Profile();profile.Tools.UseAllSkills=false;
                Check.EqualInt(0,w.ResolveSkills(profile).Count,"显式空选择");
                profile.Tools.UseAllSkills=true;Check.EqualInt(1,w.ResolveSkills(profile).Count,"保留旧默认");
            });
            Runner.Add("页面后端", "编辑已摘要消息使旧摘要失效", delegate {
                var c=new Conversation { Summary="old summary",SummarizedUpTo=0 };c.Messages.Add(new Message { Content="old" });
                c.AddVariant(0,new Message { Content="new" });
                Check.Equal("",c.Summary,"不继续使用旧摘要");
                Check.EqualInt(-1,c.SummarizedUpTo,"重置位置");
            });
            Runner.Add("页面后端", "授权目录只读与路径边界不可绕过", delegate {
                var workspace=new InMemoryFileStore();var external=new InMemoryFileStore();
                external.WriteTextAsync("note.txt","external").GetAwaiter().GetResult();
                var configuration=new AuthorizedFolder { Token="token",ReadOnly=true };
                var store=new MountedFileStore(workspace,"p/files",new[] { new FileMount { Configuration=configuration,Open=()=>Task.FromResult<IFileStore>(external) } });
                Check.Equal("external",store.ReadTextAsync("p/files/_folders/token/note.txt").GetAwaiter().GetResult(),"显式挂载可读");
                Check.Throws<UnauthorizedAccessException>(()=>store.WriteTextAsync("p/files/_folders/token/note.txt","bad").GetAwaiter().GetResult(),"只读不可写");
                Check.Throws<UnauthorizedAccessException>(()=>store.ReadTextAsync("p/files/_folders/other/note.txt").GetAwaiter().GetResult(),"未授权不可读");
                Check.Throws<ArgumentException>(()=>store.ReadTextAsync("p/files/_folders/token/../note.txt").GetAwaiter().GetResult(),"上跳不可读");
                configuration.ReadOnly=false;
                store.WriteTextAsync("p/files/_folders/token/note.txt","changed").GetAwaiter().GetResult();
                Check.Equal("changed",external.Peek("note.txt"),"显式读写挂载生效");
            });
            Runner.Add("页面后端", "文件工具可发现并读取已挂载目录", delegate {
                var workspace=new InMemoryFileStore();var external=new InMemoryFileStore();
                external.WriteTextAsync("note.txt","folder text").GetAwaiter().GetResult();
                var configuration=new AuthorizedFolder { Token="token",ReadOnly=true };
                var store=new MountedFileStore(workspace,"p/files",new[] { new FileMount { Configuration=configuration,Open=()=>Task.FromResult<IFileStore>(external) } });
                var context=new ToolContext { ProfileSlug="p",Store=store };
                var root=new FileListTool().InvokeAsync("{}",context,CancellationToken.None).GetAwaiter().GetResult();
                Check.Contains(root.Content,"_folders/","根目录可发现");
                var file=new FileReadTool().InvokeAsync("{\"path\":\"_folders/token/note.txt\"}",context,CancellationToken.None).GetAwaiter().GetResult();
                Check.True(file.Ok,"工具读取成功");Check.Contains(file.Content,"folder text","实际目录内容");
            });
            Runner.Add("页面后端", "全局搜索会话正文与私有记忆", delegate {
                var store=new InMemoryFileStore();var w=WorkspaceFor(store);
                var p=w.CreateProfileAsync("test",null).GetAwaiter().GetResult();
                var c=w.CreateConversation(p,"标题没有关键词");c.Messages.Add(new Message { Content="正文有独特关键词" });
                w.SaveConversationAsync(p.Slug,c).GetAwaiter().GetResult();
                var memory=new MemoryFile();memory.Items.Add(new MemoryItem { Text="记忆有独特关键词" });
                store.WriteTextAsync(WorkspaceLayout.MemoryFile(p.Slug),JsonStore.Serialize(memory)).GetAwaiter().GetResult();
                var hits=new WorkspaceSearch(w,store,NullLogger.Instance).SearchAsync("独特关键词",CancellationToken.None).GetAwaiter().GetResult();
                Check.True(hits.Any(x=>x.Kind=="conversation"),"正文命中");
                Check.True(hits.Any(x=>x.Kind=="memory"),"私有记忆命中");
            });
            Runner.Add("页面后端", "禁用 MCP 工具不会发出网络请求", delegate {
                var http=new FakeHttpTransport();
                var server=new McpServerConfig { Name="server",Url="https://example.test/mcp" };server.DisabledTools.Add("write");
                var context=new ToolContext { Http=http,McpServers=new List<McpServerConfig>{server} };
                var result=new McpTool().InvokeAsync("{\"server\":\"server\",\"tool\":\"write\"}",context,CancellationToken.None).GetAwaiter().GetResult();
                Check.False(result.Ok,"禁用被拒绝");Check.EqualInt(0,http.Requests.Count,"没有网络请求");
            });
            Runner.Add("页面后端", "选择性备份只包含所选档案与数据类型", delegate {
                var store=new InMemoryFileStore();var w=WorkspaceFor(store);
                var p=w.CreateProfileAsync("p",null).GetAwaiter().GetResult();var other=w.CreateProfileAsync("other",null).GetAwaiter().GetResult();
                var c=w.CreateConversation(p,"chat");w.SaveConversationAsync(p.Slug,c).GetAwaiter().GetResult();
                store.WriteBytesAsync(p.Slug+"/files/photo.png",new byte[]{1,2,3}).GetAwaiter().GetResult();
                store.WriteTextAsync(WorkspaceLayout.MemoryFile(p.Slug),JsonStore.Serialize(new MemoryFile())).GetAwaiter().GetResult();
                using(var stream=new System.IO.MemoryStream()) {
                    var result=new BackupService(store,NullLogger.Instance,SystemClock.Instance).CreateAsync(stream,w,"1.1","test",null,CancellationToken.None,
                        new BackupSelection { ProfileSlugs=new List<string>{p.Slug},IncludeFiles=false,IncludeMemory=false,IncludeShared=false }).GetAwaiter().GetResult();
                    Check.True(result.Ok,"备份成功");Check.EqualInt(1,result.Manifest.ProfileCount,"档案计数");
                    Check.True(result.Manifest.Entries.Contains(WorkspaceLayout.ProfileFile(p.Slug)),"所选档案");
                    Check.False(result.Manifest.Entries.Any(x=>x.StartsWith(other.Slug+"/")),"不包含其他档案");
                    Check.False(result.Manifest.Entries.Any(x=>x.Contains("/files/")||x.EndsWith("/memory.json")),"不含取消类型");
                    Check.True(result.Manifest.Entries.Contains(WorkspaceLayout.ConversationFile(p.Slug,c.Id)),"包含会话");
                }
            });
            Runner.Add("页面后端", "结构化追问保留类型与问题 id", delegate {
                var interaction=new FakeInteraction();var answer=new AskAnswer { Id="when" };answer.Values.Add("2026-10-14");interaction.Answers.Add(answer);
                var questions=new JArray(new JObject { ["id"]="when",["text"]="日期？",["type"]="date" });
                var args=new JObject { ["questions"]=questions.ToString() };
                var result=new AskUserTool().InvokeAsync(args.ToString(),new ToolContext { Interaction=interaction },CancellationToken.None).GetAwaiter().GetResult();
                Check.True(result.Ok,"调用成功");Check.Contains(result.Content,"when","按 id 返回");Check.Contains(result.Content,"2026-10-14","实际答复");
            });
            Runner.Add("页面后端", "压缩预览不改会话与持久化正文", delegate {
                var store=new InMemoryFileStore();var w=WorkspaceFor(store);
                var api=new ApiProfile { Model="model",Url="https://example.test/v1/chat/completions" };
                w.ApiProfiles.Items.Add(api);w.ApiProfiles.DefaultId=api.Id;w.SetApiKey(api,"key");
                var profile=w.CreateProfileAsync("p",null).GetAwaiter().GetResult();
                var c=w.CreateConversation(profile,"preview");
                for(int i=0;i<50;i++)c.Messages.Add(new Message { Role=i%2==0?MessageRole.User:MessageRole.Assistant,Content="消息 "+i });
                w.SaveConversationAsync(profile.Slug,c).GetAwaiter().GetResult();
                string before=store.Peek(WorkspaceLayout.ConversationFile(profile.Slug,c.Id));
                var http=new FakeHttpTransport { NextBody="{\"choices\":[{\"message\":{\"content\":\"新的摘要\"}}]}" };
                var chat=new ChatService(w,store,NullLogger.Instance,SystemClock.Instance,http,null,null,null,ToolRegistry.CreateDefault());
                var result=chat.PreviewCompactAsync(profile,c,CancellationToken.None).GetAwaiter().GetResult();
                Check.True(result.Compacted,"已生成预览");Check.Equal("新的摘要",result.Summary,"实际模型摘要");
                Check.Equal("",c.Summary,"原会话未改");Check.Equal(before,store.Peek(WorkspaceLayout.ConversationFile(profile.Slug,c.Id)),"磁盘未改");
            });
            Runner.Add("页面后端", "上下文估算只计一次摘要并排除已压缩历史", delegate {
                var store = new InMemoryFileStore(); var w = WorkspaceFor(store);
                var p = w.CreateProfileAsync("额度", null).GetAwaiter().GetResult();
                p.Persona.SystemPrompt = ""; p.Persona.Description = ""; p.Persona.Personality = "";
                var c = w.CreateConversation(p, "长会话");
                c.Messages.Add(new Message { Role = MessageRole.User, Content = new string('旧', 5000) });
                c.Messages.Add(new Message { Role = MessageRole.User, Content = "新的问题" });
                c.Summary = new string('摘', 400); c.SummarizedUpTo = 0;
                var chat = new ChatService(w, store, NullLogger.Instance, SystemClock.Instance,
                    new FakeHttpTransport(), null, null, null, ToolRegistry.CreateDefault());
                chat.OpenProfileAsync(p).GetAwaiter().GetResult();
                chat.OpenConversationAsync(c).GetAwaiter().GetResult();
                int expected = TokenEstimator.Estimate(c.Summary) + TokenEstimator.EstimateMessage("新的问题", "");
                Check.EqualInt(expected, chat.EstimateContextTokens(), "摘要不重算、旧消息不再计入");
                Check.EqualInt(2, c.Messages.Count, "估算不删历史");
            });
            Runner.Add("MCP 新协议", "协商会话与分页工具发现", delegate {
                var transport = new McpTestTransport();
                var client = new McpClient(transport,new McpServerConfig { Url="https://example.test/mcp" });
                var tools = client.ListToolsAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check.EqualInt(2,tools.Count,"两页工具");
                Check.True(transport.Requests.Skip(1).All(x=>x.Headers.ContainsKey("Mcp-Session-Id")),"后续请求携带会话");
                Check.Contains(transport.Requests[0].Headers["Accept"],"text/event-stream","支持 SSE");
            });
        }
    }

    public sealed class McpTestTransport : IHttpTransport
    {
        public List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();
        public Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken ct)
        {
            Requests.Add(request); var body=JObject.Parse(request.Body); string method=(string)body["method"];
            var response=new HttpResponseSpec { StatusCode=200,ContentType="application/json" };
            JObject result;
            if (method=="initialize") {
                result=new JObject { ["protocolVersion"]="2025-06-18" }; response.Headers["Mcp-Session-Id"]="session-test";
            } else if (method=="notifications/initialized") { response.StatusCode=202; response.Body=""; return Task.FromResult(response); }
            else {
                bool second=body["params"]["cursor"]!=null;
                result=new JObject { ["tools"]=new JArray(new JObject { ["name"]=second?"second":"first" }) };
                if(!second) result["nextCursor"]="next";
            }
            response.Body=new JObject { ["jsonrpc"]="2.0",["id"]=body["id"],["result"]=result }.ToString();
            return Task.FromResult(response);
        }
        public Task StreamLinesAsync(HttpRequestSpec request,Func<string,bool> onLine,CancellationToken ct) { throw new NotSupportedException(); }
    }
}
