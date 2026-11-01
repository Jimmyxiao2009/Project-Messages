using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class ConversationsPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            bool archived = Context.Section == "archived";
            Title(archived ? "已归档会话" : "会话管理", Context.Profile != null ? Context.Profile.Name : "全部档案");
            var entries = Context.Profile != null && Context.Section == "profile"
                ? await W.ListConversationsAsync(Context.Profile.Slug, true) : await W.ListAllConversationsAsync(true);
            var selected = new Dictionary<ConversationIndexEntry, CheckBox>();
            foreach (var entry in entries.Where(x => archived ? x.Archived : !x.Archived)) {
                var item = entry;
                var box = Check(entry.Title, false); selected[item] = box;
                Row(entry.Preview, entry.UpdatedAtUtc.ToLocalTime().ToString("g"), async delegate {
                    RequireIdle(); var p = W.FindProfile(item.ProfileId);
                    var c = await W.LoadConversationAsync(p.Slug, item.Id);
                    await Context.OpenChat(p, c);
                });
            }
            if (selected.Count == 0) Text(archived ? "暂无归档会话。" : "暂无会话。");
            Command(archived ? "取消归档" : "归档", Symbol.Folder, async delegate {
                RequireIdle();
                foreach (var item in selected.Where(x => x.Value.IsChecked == true)) {
                    var p = W.FindProfile(item.Key.ProfileId); var c = await W.LoadConversationAsync(p.Slug, item.Key.Id);
                    c.Archived = !archived; await W.SaveConversationAsync(p.Slug, c);
                }
                await BuildAsync();
            });
            Command("移动", Symbol.MoveToFolder, delegate {
                var ids = selected.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
                if (ids.Count == 0) throw new InvalidOperationException("先选择会话。");
                Go<MoveConversationsPage>(null, ids); return Task.CompletedTask;
            });
            Command("删除", Symbol.Delete, async delegate {
                RequireIdle(); var chosen = selected.Where(x => x.Value.IsChecked == true).ToList();
                if (chosen.Count == 0) throw new InvalidOperationException("先选择会话。");
                if (await Confirm("删除所选会话？", chosen.Count + " 条会话将进入回收区。", "删除")) {
                    foreach (var item in chosen) {
                        var p = W.FindProfile(item.Key.ProfileId); var c = await W.LoadConversationAsync(p.Slug, item.Key.Id);
                        await S.Operations.RecycleConversationAsync(p, c);
                    }
                    await BuildAsync();
                }
            });
        }
    }
    public sealed class MoveConversationsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("移动会话", "会话管理");
            var entries = (List<ConversationIndexEntry>)Context.Item;
            Text("已选择 " + entries.Count + " 条会话。", true);
            foreach (var target in W.Profiles) Row(target.Name, "移入此档案，附件一并复制", async delegate {
                RequireIdle();
                foreach (var entry in entries) {
                    var source = W.FindProfile(entry.ProfileId);
                    var conversation = await W.LoadConversationAsync(source.Slug, entry.Id);
                    await S.Operations.MoveConversationAsync(source, target, conversation);
                }
                Finish();
            });
            return Task.CompletedTask;
        }
    }
    public sealed class ChatSettingsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var c = Context.Conversation;
            if (c == null) throw new InvalidOperationException("没有打开会话。");
            Title("会话设置", c.Title);
            var title = Field("标题", c.Title);
            var pin = Toggle("置顶", c.Pinned); var archive = Toggle("归档", c.Archived);
            var prompt = Field("会话提示词覆盖", c.SystemPromptOverride, true);
            prompt.IsEnabled = Context.Profile.Prompts.AllowConversationOverride;
            Save(async delegate {
                RequireIdle(); if (c.Title != title.Text.Trim()) c.TitleSource = ""; c.Title = Required(title); c.Pinned = pin.IsOn; c.Archived = archive.IsOn;
                if (prompt.IsEnabled) c.SystemPromptOverride = prompt.Text;
                await W.SaveConversationAsync(Context.Profile.Slug, c);
            });
            Link<ModelPickerPage>("临时切换模型", c.ModelOverride.Length > 0 ? c.ModelOverride : "跟随档案");
            Link<GroupPage>("群聊成员", c.Members.Count + " 位成员");
            Link<AppearancePage>("会话外观", c.AppearanceOverride == null ? "跟随档案" : "自定义", "chat");
            Link<ContextPage>("上下文与压缩", c.Messages.Count + " 条消息");
            Link<SettingsPage>("朗读设置", "音色、语速与音量", "speech");
            Link<SettingsPage>("翻译设置", W.Settings.TranslationLanguage, "translation");
            Link<DiagnosticsPage>("会话用量", "每轮 tokens 与耗时", "usage");
            Link<FilesPage>("会话附件", "查看与添加", "attachments");
            Link<DataPage>("导出会话", "JSON / Markdown", "conversation");
            Command("删除会话", Symbol.Delete, async delegate {
                RequireIdle();
                if (await Confirm("删除会话？", "此会话将移入回收区。", "删除")) {
                    await S.Operations.RecycleConversationAsync(Context.Profile, c); Context.Close();
                }
            }, true);
            return Task.CompletedTask;
        }
    }

    public sealed class ContextPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var c = Context.Conversation;
            Title("上下文与压缩", c.Title);
            Text(c.Messages.Count + " 条消息 · 约 " + c.Messages.Sum(x => TokenEstimator.EstimateMessage(x.Content, x.Reasoning)) + " tokens");
            var summary = Field("当前摘要", c.Summary, true); summary.IsReadOnly = true;
            Text("压缩会调用辅助模型，成功后将早期消息摘要用于后续请求；原消息保留。", true);
            Command("压缩上下文", Symbol.Refresh, async delegate {
                RequireIdle();
                var result = await S.Chat.PreviewCompactAsync(Context.Profile, c, Lifetime.Token);
                if (!result.Compacted) { Status.Text = result.Reason; return; }
                Go<CompactPreviewPage>(null, new CompactPreview { Result = result, MessageCount = c.Messages.Count, LastMessageId = c.Messages.Count > 0 ? c.Messages[c.Messages.Count - 1].Id : "" });
            });
            Command("提取记忆", Symbol.Add, async delegate {
                RequireIdle(); await S.OpenProfileAsync(Context.Profile); await S.Chat.OpenConversationAsync(c);
                var count = await S.Chat.ExtractMemoryAsync(Lifetime.Token); Status.Text = "已提取 " + count + " 条记忆。";
            });
            return Task.CompletedTask;
        }
    }

    public sealed class GroupPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var c = Context.Conversation;
            if (c == null) throw new InvalidOperationException("请先新建会话，再添加群聊成员。");
            Title("群聊成员", c.Title);
            var mode = Choice("回复模式", GroupReplyModes.All, c.GroupReplyMode);
            Text("round_robin 轮流 · mention 点名 · free 自由", true);
            foreach (var member in c.Members.OrderBy(x => x.Order))
                Link<GroupMemberPage>(member.Name, (member.Enabled ? "参与" : "停用") + " · " + member.Order, null, member);
            if (c.Members.Count == 0) Text("添加两个或更多成员后会成为群聊。");
            Save(async delegate { RequireIdle(); c.GroupReplyMode = (string)mode.SelectedItem; await W.SaveConversationAsync(Context.Profile.Slug, c); });
            Command("添加成员", Symbol.Add, delegate { Go<GroupMemberPage>(null, new GroupMember()); return Task.CompletedTask; });
            return Task.CompletedTask;
        }
    }
    public sealed class GroupMemberPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var original = Context.Item as GroupMember ?? new GroupMember();
            Title("群聊成员", "编辑");
            var profiles = W.Profiles.ToList();
            var selected = profiles.Find(x => x.Id == original.ProfileId);
            var profile = Choice("角色档案", profiles.Select(x => x.Name).ToArray(), selected != null ? selected.Name : profiles[0].Name);
            var name = Field("群内名称（可选）", original.Name);
            var greeting = Field("首句", original.Greeting, true);
            var order = Field("发言顺序", original.Order.ToString());
            var enabled = Toggle("参与回复", original.Enabled);
            Save(async delegate {
                RequireIdle(); var draft = JsonBridge.Clone(original); var p = profiles[profile.SelectedIndex];
                if (Context.Conversation.Members.Any(x => x != original && x.ProfileId == p.Id))
                    throw new ArgumentException("这个角色已在群聊中。");
                draft.ProfileId = p.Id; draft.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : p.Name;
                draft.Greeting = greeting.Text; draft.Order = Int(order, 0, 10000); draft.Enabled = enabled.IsOn;
                int at = Context.Conversation.Members.IndexOf(original);
                if (at >= 0) Context.Conversation.Members[at] = draft; else Context.Conversation.Members.Add(draft);
                await W.SaveConversationAsync(Context.Profile.Slug, Context.Conversation);
            });
            if (Context.Conversation.Members.Contains(original)) Command("移除成员", Symbol.Delete, async delegate {
                RequireIdle(); Context.Conversation.Members.Remove(original); await W.SaveConversationAsync(Context.Profile.Slug, Context.Conversation); Finish();
            }, true);
            return Task.CompletedTask;
        }
    }

    public sealed class MessageEditPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var message = Context.Item as Message;
            if (message == null) throw new InvalidOperationException("消息不存在。");
            bool fork = Context.Section == "fork";
            Title(fork ? "创建会话分支" : "编辑消息", Context.Conversation.Title);
            var text = Field(fork ? "新会话标题" : "正文", fork ? Context.Conversation.Title + " 分支" : message.Content, !fork);
            Text(fork ? "新会话保留到此消息为止的历史，原会话保留。" : "保存为此位置的新候选，原消息保留在候选池。", true);
            Save(async delegate {
                RequireIdle(); var c = Context.Conversation; int index = c.Messages.FindIndex(x => x.Id == message.Id);
                if (index < 0) throw new InvalidOperationException("消息已变更，请重新打开。");
                if (fork) {
                    var copy = c.ForkAt(index, Required(text)); await W.SaveConversationAsync(Context.Profile.Slug, copy);
                    await Context.OpenChat(Context.Profile, copy);
                } else {
                    var candidate = message.Clone(); candidate.Id = Guid.NewGuid().ToString("N"); candidate.Content = Required(text);
                    candidate.Translations = null; c.AddVariant(index, candidate); await W.SaveConversationAsync(Context.Profile.Slug, c);
                }
            });
            return Task.CompletedTask;
        }
    }
}
