using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public sealed class LegacyImportPlan
    {
        public List<ApiProfile> Apis = new List<ApiProfile>();
        public List<Profile> Profiles = new List<Profile>();
        public List<Conversation> Conversations = new List<Conversation>();
        public Dictionary<string, List<MemoryItem>> Memories = new Dictionary<string, List<MemoryItem>>();
        public Dictionary<string, byte[]> Images = new Dictionary<string, byte[]>();
        public List<string> Warnings = new List<string>();
    }

    public static class LegacyImporter
    {
        private static JToken Value(JToken root, string key)
        {
            var obj = root as JObject;
            if (obj == null) return null;
            foreach (var property in obj.Properties())
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) return property.Value;
            return null;
        }
        private static string Str(JToken root, string key, string fallback = "")
        {
            var value = Value(root, key);
            return value == null || value.Type == JTokenType.Null ? fallback : value.ToString();
        }
        private static JArray Array(JToken root, string key) { return Value(root, key) as JArray ?? new JArray(); }
        private static DateTimeOffset Date(JToken root, string key)
        {
            var raw = Str(root, key);
            if (raw.StartsWith("/Date(", StringComparison.Ordinal)) {
                long millis;
                var number = raw.Substring(6).Split(')', '+')[0];
                if (long.TryParse(number, out millis)) return new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddMilliseconds(millis);
            }
            DateTimeOffset date;
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date) ? date : DateTimeOffset.UtcNow;
        }

        public static LegacyImportPlan Preview(string json)
        {
            var root = JObject.Parse(json);
            if (Value(root,"ApiProfiles") == null && Value(root,"Conversations") == null && Value(root,"CharacterCards") == null)
                throw new ArgumentException("不是支持的旧版 yanshuaiAppData.json 文件。");
            var plan = new LegacyImportPlan();
            var apis = new Dictionary<string,string>(); var chars = new Dictionary<string,string>();
            foreach (var old in Array(root,"ApiProfiles")) {
                var provider = Str(old,"ProviderType","openai");
                var api = new ApiProfile { Name=Str(old,"Name","旧模型"), Url=Str(old,"Url"), Model=Str(old,"Model"),
                    Provider=provider=="claude"?"anthropic":provider, SubModel=Str(old,"SubModel") };
                apis[Str(old,"Id")] = api.Id; plan.Apis.Add(api);
            }
            foreach (var old in Array(root,"CharacterCards")) {
                var p = new Profile { Name=Str(old,"Name","旧角色"), Slug="legacy-"+TextUtil.Slug(Str(old,"Name","角色")) };
                p.Persona.Name=p.Name; p.Persona.Description=Str(old,"Description"); p.Persona.Personality=Str(old,"Personality");
                p.Persona.Scenario=Str(old,"Scenario"); p.Persona.SystemPrompt=Str(old,"SystemPrompt"); p.Persona.FirstMessage=Str(old,"FirstMessage");
                p.Persona.ExampleDialogue=Str(old,"MesExample"); p.Persona.PostHistoryInstructions=Str(old,"PostHistoryInstructions");
                p.Persona.Creator=Str(old,"Creator"); p.Persona.CreatorNotes=Str(old,"CreatorNotes");
                p.Persona.Tags=Str(old,"Tags").Split(new[]{',','，'},StringSplitOptions.RemoveEmptyEntries).ToList();
                p.Tools.ToolsEnabled=false;
                chars[Str(old,"Id")] = p.Id; plan.Profiles.Add(p);
                string image=Str(old,"AvatarBase64");
                if(image.Length>0) try { plan.Images[p.Id]=Convert.FromBase64String(image); } catch(FormatException) { plan.Warnings.Add(p.Name+"：头像编码损坏，已跳过。"); }
            }
            var fallback=new Profile { Name="已导入",Slug="legacy-imported" }; fallback.Persona.Name=fallback.Name; fallback.Tools.ToolsEnabled=false;
            foreach(var old in Array(root,"Conversations")) {
                string owner; if(!chars.TryGetValue(Str(old,"CharacterCardId"),out owner)) {
                    owner=fallback.Id; if(!plan.Profiles.Contains(fallback))plan.Profiles.Add(fallback);
                }
                var branches=Array(old,"AllBranches");
                var sources = branches.Count>0 ? branches.Select(x=>Array(x,"Messages")).ToList() : new List<JArray>{Array(old,"Messages")};
                int branchIndex=0;
                foreach(var source in sources) {
                    var conversation=new Conversation { ProfileId=owner,Title=Str(old,"Title","旧会话")+(branchIndex>0?" · 分支 "+branchIndex:""),
                        CreatedAtUtc=Date(old,"CreatedAt"),UpdatedAtUtc=Date(old,"UpdatedAt") };
                    string api;
                    if(apis.TryGetValue(Str(old,"ApiProfileId"),out api))conversation.ModelApiProfileId=api;
                    foreach(var oldMessage in source) {
                        var role=Str(oldMessage,"Role","user").ToLowerInvariant();
                        var message=new Message { Role=role=="assistant"?MessageRole.Assistant:role=="system"?MessageRole.System:MessageRole.User,
                            Content=Str(oldMessage,"Content"),Reasoning=Str(oldMessage,"ReasoningContent"),CreatedAtUtc=Date(oldMessage,"Timestamp"),
                            IsFinal=role=="assistant" };
                        var image=Str(oldMessage,"ImageBase64");
                        if(image.Length>0) try {
                            string name=Guid.NewGuid().ToString("N")+".png"; plan.Images[owner+"/"+name]=Convert.FromBase64String(image);
                            message.Attachments.Add(new Attachment { Kind="image",Name=name,Path=name,MimeType=Str(oldMessage,"ImageMimeType","image/png") });
                        } catch(FormatException) { plan.Warnings.Add("一张消息图片损坏，已跳过。"); }
                        conversation.Messages.Add(message);
                    }
                    plan.Conversations.Add(conversation); branchIndex++;
                }
                var memories = Array(old,"MemoryItems");
                if(memories.Count>0) {
                    List<MemoryItem> list;
                    if(!plan.Memories.TryGetValue(owner,out list)){list=new List<MemoryItem>();plan.Memories[owner]=list;}
                    foreach(var memory in memories)if(!string.IsNullOrWhiteSpace(memory.ToString()))list.Add(new MemoryItem { Text=memory.ToString() });
                }
            }
            plan.Warnings.Add("旧 API 密钥与旧授权不会导入，需要重新配置；旧分支保存为独立会话。");
            return plan;
        }

        public static async Task<int> ImportAsync(Workspace workspace,IFileStore store,LegacyImportPlan plan,
            bool importApis,bool importChats,bool importImages,bool importMemory,CancellationToken ct)
        {
            if(plan==null)throw new ArgumentNullException("plan");
            if(importApis) {
                foreach(var api in plan.Apis) workspace.ApiProfiles.Items.Add(api);
                if(workspace.ApiProfiles.DefaultId.Length==0 && plan.Apis.Count>0)workspace.ApiProfiles.DefaultId=plan.Apis[0].Id;
                await workspace.SaveApiProfilesAsync().ConfigureAwait(false);
            }
            int count=0;
            foreach(var template in plan.Profiles) {
                ct.ThrowIfCancellationRequested();
                var created=await workspace.CreateProfileAsync(template.Name,template.Slug).ConfigureAwait(false);
                var profile=JsonBridge.Clone(template); profile.Id=created.Id;profile.Slug=created.Slug;
                if(importImages) {
                    byte[] avatar;
                    if(plan.Images.TryGetValue(template.Id,out avatar)) {
                        profile.Avatar=WorkspaceLayout.AvatarFile(profile.Slug);await store.WriteBytesAsync(profile.Avatar,avatar).ConfigureAwait(false);
                    }
                }
                await workspace.SaveProfileAsync(profile).ConfigureAwait(false);
                if(importChats)foreach(var conversation in plan.Conversations.Where(x=>x.ProfileId==template.Id)) {
                    ct.ThrowIfCancellationRequested();
                    var copy=JsonBridge.Clone(conversation); copy.Id=Guid.NewGuid().ToString("N");copy.ProfileId=profile.Id;
                    if(!importApis)copy.ModelApiProfileId="";
                    foreach(var message in copy.Messages)foreach(var attachment in message.Attachments) {
                        byte[] bytes;
                        if(importImages && plan.Images.TryGetValue(template.Id+"/"+attachment.Path,out bytes)) {
                            string path=WorkspaceLayout.AttachmentsDir(profile.Slug)+"/"+attachment.Path;
                            await store.WriteBytesAsync(path,bytes).ConfigureAwait(false);attachment.Path=path;attachment.SizeBytes=bytes.Length;
                        }
                    }
                    if(!importImages)foreach(var message in copy.Messages)message.Attachments.Clear();
                    // Preserve historical timestamps; the index is rebuilt without touching bodies.
                    await AtomicFile.WriteAsync(store,WorkspaceLayout.ConversationFile(profile.Slug,copy.Id),JsonStore.Serialize(copy)).ConfigureAwait(false);
                    count++;
                }
                if(importMemory && plan.Memories.ContainsKey(template.Id)) {
                    var file=new MemoryFile { Items=plan.Memories[template.Id] };
                    await AtomicFile.WriteAsync(store,WorkspaceLayout.MemoryFile(profile.Slug),JsonStore.Serialize(file)).ConfigureAwait(false);
                }
                await new WorkspaceOperations(workspace,store,null,SystemClock.Instance).RebuildIndexAsync(profile).ConfigureAwait(false);
            }
            await workspace.ReloadAsync().ConfigureAwait(false);
            return count;
        }
    }
}
