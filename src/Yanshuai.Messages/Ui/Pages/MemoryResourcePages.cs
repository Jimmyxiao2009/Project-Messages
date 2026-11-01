using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class MemoryPage : DetailPage
    {
        private MemoryService _memory;
        private TextBox _query;
        private StackPanel _list;
        protected override async Task BuildAsync()
        {
            if (Context.Section == "global") Context.Profile = null;
            Title("记忆", Context.Profile != null ? Context.Profile.Name : "全局");
            _memory = new MemoryService(S.Files, S.Log, S.Clock, S.Embedder);
            if (Context.Profile != null) await _memory.LoadAsync(Context.Profile.Slug);
            await _memory.LoadGlobalAsync(true);
            _query = Field("搜索记忆", "");
            _list = new StackPanel(); Body.Children.Add(_list);
            _query.TextChanged += delegate { Populate(); };
            Populate();
            Command("新增", Symbol.Add, delegate { Go<MemoryEditorPage>(null, _memory); return Task.CompletedTask; });
            Command("整理", Symbol.Delete, delegate { Go<MemoryMaintenancePage>("prune", _memory); return Task.CompletedTask; });
            Command("索引", Symbol.Refresh, delegate { Go<MemoryMaintenancePage>("index", _memory); return Task.CompletedTask; });
            Command("导出记忆", Symbol.Download, async delegate { var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "memories.json" }; picker.FileTypeChoices.Add("记忆 JSON", new[] { ".json" }); var file = await picker.PickSaveFileAsync(); if (file != null) await Windows.Storage.FileIO.WriteTextAsync(file, JsonStore.Serialize(Context.Profile == null ? _memory.GlobalItems : _memory.ProfileItems)); }, true);
            Command("清空记忆", Symbol.Delete, async delegate { RequireIdle(); if (!await Confirm("清空记忆？", "建议先导出记忆。清空后无法恢复。", "清空")) return; var items = (Context.Profile == null ? _memory.GlobalItems : _memory.ProfileItems).ToList(); foreach (var item in items) _memory.Remove(item.Id, item.Scope); await _memory.SaveAsync(); await _memory.SaveGlobalAsync(); Populate(); }, true);
            if (Context.Profile != null) Link<ProfileEditorPage>("检索设置", "自动总结、全局池、Top K、阈值", "memory");
        }
        private void Populate()
        {
            _list.Children.Clear();
            var items = Context.Profile == null ? _memory.GlobalItems.ToList()
                : _memory.ProfileItems.Concat(_memory.GlobalItems).ToList();
            var query = _query.Text ?? "";
            foreach (var item in items.Where(x => x.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0))
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = item.Text, TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap, FontSize = 15 });
                panel.Children.Add(new TextBlock { Text = MemoryCategories.Label(item.Category) + " · " + item.Scope + " · " + item.Importance.ToString("0.0"),
                    FontSize = 12, Foreground = Brush("MidBrush") });
                var button = new Button { Content = panel, HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch, Background = Brush("BgBrush"), MinHeight = 56 };
                button.Click += delegate { Go<MemoryEditorPage>(item.Id, new MemoryEditContext { Memory = _memory, Item = item }); };
                _list.Children.Add(button);
            }
            if (_list.Children.Count == 0) _list.Children.Add(new TextBlock { Text = query.Length == 0 ? "暂无记忆，新增一条或在对话中提取。" : "没有找到匹配的记忆。", TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap });
        }
    }
    public sealed class MemoryEditContext { public MemoryService Memory; public MemoryItem Item; }
    public sealed class MemoryEditorPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var edit = Context.Item as MemoryEditContext;
            var memory = edit != null ? edit.Memory : Context.Item as MemoryService;
            var item = edit != null ? edit.Item : null;
            if (memory == null) throw new InvalidOperationException("记忆池未装载。");
            Title(item == null ? "新增记忆" : "编辑记忆", "记忆");
            var text = Field("内容", item != null ? item.Text : "", true);
            var categories = Enum.GetNames(typeof(MemoryCategory));
            var category = Choice("类别", categories, item != null ? item.Category.ToString() : MemoryCategory.Fact.ToString());
            var importance = Range("重要性", item != null ? item.Importance : 0.6, 0, 1, 0.05);
            var scope = Choice("记忆池", new[] { "Profile", "Global" }, item != null ? item.Scope.ToString() : Context.Profile == null ? "Global" : "Profile");
            scope.IsEnabled = item == null && Context.Profile != null;
            Save(async delegate {
                RequireIdle(); var value = Required(text);
                if (value.Length < 3) throw new ArgumentException("请写至少 3 个字。");
                var selectedScope = (MemoryScope)Enum.Parse(typeof(MemoryScope), (string)scope.SelectedItem);
                var selectedCategory = (MemoryCategory)Enum.Parse(typeof(MemoryCategory), (string)category.SelectedItem);
                if (item == null) memory.Add(value, selectedCategory, importance.Value, "", selectedScope);
                else memory.Update(item.Id, item.Scope, value, selectedCategory, importance.Value);
                await memory.SaveAsync(); await memory.SaveGlobalAsync();
            });
            if (item != null) Command("删除", Symbol.Delete, async delegate {
                RequireIdle();
                if (await Confirm("删除记忆？", "此操作无法撤销。", "删除")) {
                    memory.Remove(item.Id, item.Scope); await memory.SaveAsync(); await memory.SaveGlobalAsync(); Finish();
                }
            }, true);
            return Task.CompletedTask;
        }
    }
    public sealed class MemoryMaintenancePage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var memory = Context.Item as MemoryService;
            if (memory == null) throw new InvalidOperationException("记忆池未装载。");
            if (Context.Section == "prune") {
                Title("整理记忆预览", "记忆");
                var plan = memory.PlanPrune(Context.Profile != null ? Context.Profile.Memory.MaxMemories : 100);
                Text("保留 " + plan.KeepCount + " 条，待移除 " + plan.ToRemove.Count + " 条。");
                Text(plan.Reason, true);
                foreach (var item in plan.ToRemove) Text(item.Text);
                Command("应用整理", Symbol.Accept, async delegate {
                    RequireIdle();
                    if (plan.ToRemove.Count > 0 && await Confirm("整理记忆？", "将移除预览中的 " + plan.ToRemove.Count + " 条记忆。")) {
                        memory.ApplyPrune(plan); await memory.SaveAsync(); Finish();
                    }
                });
            } else {
                Title("记忆索引", "记忆");
                Text(memory.ProfileCount + " 条档案记忆 · " + memory.GlobalCount + " 条全局记忆");
                Text(memory.HasVectorIndex ? "向量索引可用" : "目前使用 BM25 检索");
                Text(S.Embedder.IsAvailable ? "嵌入服务可用。" : "请先配置支持嵌入的服务。", true);
                Command("重建索引", Symbol.Refresh, async delegate {
                    RequireIdle();
                    if (!S.Embedder.IsAvailable) throw new InvalidOperationException("嵌入服务不可用。");
                    memory.ResetVectors(); var count = await memory.IndexVectorsAsync(Lifetime.Token);
                    await memory.SaveAsync(); Status.Text = "已索引 " + count + " 条。";
                });
            }
            return Task.CompletedTask;
        }
    }

    public sealed class ResourcesPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            string section = Context.Section ?? "lorebooks";
            if (section == "selection") {
                Title("世界书 · 注入", Context.Profile.Name);
                var books = new Dictionary<string, CheckBox>(); var injections = new Dictionary<string, CheckBox>();
                Section("世界书");
                foreach (var book in W.Library.Lorebooks) books[book.Id] = Check(book.Name, Context.Profile.Prompts.LorebookIds.Contains(book.Id));
                Section("模式注入");
                foreach (var injection in W.Library.Injections) injections[injection.Id] = Check(injection.Name, Context.Profile.Prompts.InjectionIds.Contains(injection.Id));
                var allow = Toggle("允许会话覆盖提示词", Context.Profile.Prompts.AllowConversationOverride);
                Save(async delegate {
                    RequireIdle();
                    Context.Profile.Prompts.LorebookIds = books.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
                    Context.Profile.Prompts.InjectionIds = injections.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
                    Context.Profile.Prompts.AllowConversationOverride = allow.IsOn;
                    await W.SaveProfileAsync(Context.Profile);
                });
                Link<ResourcesPage>("管理世界书", "列表与条目编辑", "lorebooks");
                Link<ResourcesPage>("管理模式注入", "常驻提示词", "injections");
            } else if (section == "user-personas") {
                Title("用户人设", "我的");
                foreach (var user in W.UserPersonas.Items)
                    Link<ResourceEditorPage>((user.Id == W.UserPersonas.ActiveId ? "★ " : "") + user.Name, user.Description, "user", user);
                Command("新建", Symbol.Add, delegate { Go<ResourceEditorPage>("user", new UserPersona()); return Task.CompletedTask; });
            } else if (section == "injections") {
                Title("模式注入", "提示词");
                foreach (var injection in W.Library.Injections) Link<ResourceEditorPage>(injection.Name, injection.Content, "injection", injection);
                Command("新建", Symbol.Add, delegate { Go<ResourceEditorPage>("injection", new ModeInjection()); return Task.CompletedTask; });
            } else if (section == "book") {
                var book = Context.Item as Lorebook;
                Title(book.Name, "世界书");
                foreach (var entry in book.Entries) Link<ResourceEditorPage>(entry.Name, string.Join(", ", entry.Keywords), "entry", new LoreEditContext { Book = book, Entry = entry });
                Command("新增条目", Symbol.Add, delegate { Go<ResourceEditorPage>("entry", new LoreEditContext { Book = book, Entry = new LorebookEntry() }); return Task.CompletedTask; });
                Command("编辑", Symbol.Edit, delegate { Go<ResourceEditorPage>("book", book); return Task.CompletedTask; });
            } else {
                Title("世界书", "提示词");
                foreach (var book in W.Library.Lorebooks) Link<ResourcesPage>(book.Name, book.Entries.Count + " 个条目", "book", book);
                Command("新建", Symbol.Add, delegate { Go<ResourceEditorPage>("book", new Lorebook()); return Task.CompletedTask; });
            }
            if (Body.Children.Count == 0) Text("还没有内容，点「新建」添加。");
            return Task.CompletedTask;
        }
    }
    public sealed class LoreEditContext { public Lorebook Book; public LorebookEntry Entry; }

    public sealed class ResourceEditorPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            string section = Context.Section;
            Title(section == "user" ? "用户人设" : section == "entry" ? "世界书条目" : section == "book" ? "世界书" : "模式注入", "编辑");
            Func<Task> save; Func<Task> delete;
            if (section == "user") {
                var source = (UserPersona)Context.Item;
                var name = Field("名称", source.Name); var text = Field("描述", source.Description, true);
                var avatar = source.Avatar;
                Row("选择头像", "从相册选择，保存后生效", async delegate {
                    var file = await FilesPage.PickAsync(true); if (file == null) return;
                    var bytes = await Yanshuai.Messages.Platform.ImagePreparation.CompressAsync(await FilesPage.ReadBytes(file, 10 * 1024 * 1024), 1024);
                    avatar = "_shared/user-avatars/" + Guid.NewGuid().ToString("N") + ".jpg";
                    await S.Files.WriteBytesAsync(avatar, bytes);
                    Status.Text = "已选择头像，保存后生效。";
                });                var active = Toggle("设为默认用户人设", source.Id == W.UserPersonas.ActiveId);
                save = async delegate {
                    var draft = JsonBridge.Clone(source); draft.Name = Required(name); draft.Description = text.Text; draft.Avatar = avatar;
                    W.UserPersonas.Items.RemoveAll(x => x.Id == draft.Id); W.UserPersonas.Items.Add(draft);
                    if (active.IsOn) W.UserPersonas.ActiveId = draft.Id;
                    await W.SaveUserPersonasAsync();
                };
                delete = async delegate { W.UserPersonas.Items.RemoveAll(x => x.Id == source.Id); await W.SaveUserPersonasAsync(); };
            } else if (section == "book") {
                var source = (Lorebook)Context.Item;
                var name = Field("名称", source.Name); var enabled = Toggle("启用", source.Enabled);
                save = async delegate {
                    source.Name = Required(name); source.Enabled = enabled.IsOn;
                    if (W.Library.FindLorebook(source.Id) == null) W.Library.Lorebooks.Add(source);
                    await W.SaveLibraryAsync();
                };
                delete = async delegate { W.Library.Lorebooks.RemoveAll(x => x.Id == source.Id); await W.SaveLibraryAsync(); };
            } else {
                var edit = Context.Item as LoreEditContext;
                var injection = Context.Item as ModeInjection;
                var entry = edit != null ? edit.Entry : null;
                var name = Field("名称", entry != null ? entry.Name : injection.Name);
                var content = Field("内容", entry != null ? entry.Content : injection.Content, true);
                var enabled = Toggle("启用", entry != null ? entry.Enabled : injection.Enabled);
                var positions = Enum.GetNames(typeof(InjectionPosition));
                var position = Choice("注入位置", positions, (entry != null ? entry.Position : injection.Position).ToString());
                var role = Choice("角色", new[] { "system", "user", "assistant" }, entry != null ? entry.Role : injection.Role);
                var priority = Field("优先级", (entry != null ? entry.Priority : injection.Priority).ToString());
                TextBox keywords = null, depth = null; ToggleSwitch always = null, regex = null, caseSensitive = null;
                if (entry != null) {
                    keywords = Field("关键词（逗号分隔）", string.Join(", ", entry.Keywords));
                    always = Toggle("常驻激活", entry.AlwaysActive); regex = Toggle("关键词作为正则", entry.UseRegex);
                    caseSensitive = Toggle("区分大小写", entry.CaseSensitive); depth = Field("扫描消息深度", entry.ScanDepth.ToString());
                } else depth = Field("指定深度", injection.Depth.ToString());
                save = async delegate {
                    var validName = Required(name); var validContent = Required(content);
                    var validPriority = Int(priority, -100000, 100000); var validDepth = Int(depth, 0, 10000);
                    var validPosition = (InjectionPosition)Enum.Parse(typeof(InjectionPosition), (string)position.SelectedItem);
                    if (entry != null) {
                        var draft = JsonBridge.Clone(entry); draft.Name = validName; draft.Content = validContent; draft.Enabled = enabled.IsOn;
                        draft.Position = validPosition; draft.Role = (string)role.SelectedItem; draft.Priority = validPriority;
                        draft.Keywords = Words(keywords.Text).Select(x => x.Trim()).ToList(); draft.AlwaysActive = always.IsOn;
                        draft.UseRegex = regex.IsOn; draft.CaseSensitive = caseSensitive.IsOn; draft.ScanDepth = validDepth;
                        if (draft.UseRegex) foreach (var pattern in draft.Keywords)
                            new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
                        edit.Book.Entries.RemoveAll(x => x.Id == draft.Id); edit.Book.Entries.Add(draft);
                    } else {
                        var draft = JsonBridge.Clone(injection); draft.Name = validName; draft.Content = validContent; draft.Enabled = enabled.IsOn;
                        draft.Position = validPosition; draft.Role = (string)role.SelectedItem; draft.Priority = validPriority; draft.Depth = validDepth;
                        W.Library.Injections.RemoveAll(x => x.Id == draft.Id); W.Library.Injections.Add(draft);
                    }
                    await W.SaveLibraryAsync();
                };
                delete = async delegate {
                    if (entry != null) edit.Book.Entries.RemoveAll(x => x.Id == entry.Id);
                    else W.Library.Injections.RemoveAll(x => x.Id == injection.Id);
                    await W.SaveLibraryAsync();
                };
            }
            Save(async delegate { RequireIdle(); await save(); });
            Command("删除", Symbol.Delete, async delegate {
                RequireIdle(); if (await Confirm("删除此项？", "会从共享资源中移除。", "删除")) { await delete(); Finish(); }
            }, true);
            return Task.CompletedTask;
        }
    }

    public sealed class PromptPreviewPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            Title("提示词装配预览", Context.Profile.Name);
            RequireIdle();


            var prompt = await S.Chat.PreviewPromptAsync(Context.Profile, Context.Conversation, Lifetime.Token);
            Text("约 " + TokenEstimator.Estimate(prompt.SystemPrompt) + " tokens · " + prompt.Turns.Count + " 条消息", true);
            var box = Field("系统提示词", prompt.SystemPrompt, true); box.IsReadOnly = true;
            foreach (var turn in prompt.Turns) { Section(turn.Role); Text(turn.Content); }
            Command("复制", Symbol.Copy, delegate {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(prompt.SystemPrompt);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); Status.Text = "已复制。"; return Task.CompletedTask;
            });
        }
    }
}
