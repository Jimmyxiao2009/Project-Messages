using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class ImportPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            string section = Context.Section ?? "source";
            var imported = Context.Item as CardImportResult;
            if (section == "preview" || section == "options") {
                if (imported == null || !imported.Ok) throw new InvalidOperationException("没有可导入的角色卡。");
                var p = imported.Profile;
                Title(section == "preview" ? "预览与补齐" : "导入选项", "角色卡");
                if (section == "preview") {
                    Text("角色卡 v" + imported.CardSpecVersion + " · " + imported.FilledFieldCount + " 个字段");
                    foreach (var warning in imported.Warnings) Text(warning, true);
                    var name = Field("名称", p.Name); var description = Field("角色描述", p.Persona.Description, true);
                    var first = Field("首句", p.Persona.FirstMessage, true);
                    Command("下一步", Symbol.Forward, delegate {
                        p.Name = Required(name); p.Persona.Name = p.Name; p.Persona.Description = description.Text; p.Persona.FirstMessage = first.Text;
                        Go<ImportPage>("options", imported); return Task.CompletedTask;
                    });
                } else {
                    var lore = Check("导入世界书", imported.Lorebook != null);
                    var picture = Check("使用角色卡图片作为头像", imported.ImageBytes != null);
                    var background = Check("使用角色卡图片作为背景", false);
                    Text("会创建独立档案；同名文件夹自动追加编号。密钥需自行配置。", true);
                    Command("导入", Symbol.Accept, async delegate {
                        RequireIdle();
                        var created = await W.CreateProfileAsync(p.Name, p.Slug);
                        p.Id = created.Id; p.Slug = created.Slug; p.CreatedAtUtc = created.CreatedAtUtc;
                        if (lore.IsChecked == true && imported.Lorebook != null) {
                            imported.Lorebook.Id = Guid.NewGuid().ToString("N");
                            W.Library.Lorebooks.Add(imported.Lorebook); p.Prompts.LorebookIds.Add(imported.Lorebook.Id);
                            await W.SaveLibraryAsync();
                        }
                        if (imported.ImageBytes != null) {
                            await S.Files.WriteBytesAsync(WorkspaceLayout.CardPngFile(p.Slug), imported.ImageBytes);
                            if (picture.IsChecked == true) {
                                var bytes = await Yanshuai.Messages.Platform.ImagePreparation.CompressAsync(imported.ImageBytes, 1024);
                                var cropped = await Yanshuai.Messages.Platform.ImagePreparation.CropSquareAsync(bytes, .5, .5, 1);
                                p.Avatar = WorkspaceLayout.AvatarFile(p.Slug); await S.Files.WriteBytesAsync(p.Avatar, cropped);
                            }
                            if (background.IsChecked == true) { p.Appearance.BackgroundType = "image"; p.Appearance.BackgroundValue = WorkspaceLayout.CardPngFile(p.Slug); }
                        }
                        await W.SaveProfileAsync(p); await W.ReloadAsync();
                        Go<ImportPage>("done", p);
                    });
                }
                Command("取消", Symbol.Cancel, delegate { Context.Close(); return Task.CompletedTask; });
            } else if (section == "done") {
                var p = (Profile)Context.Item; Title("导入完成", "角色卡"); Text(p.Name + " 已保存到 " + p.Slug);
                Command("查看档案", Symbol.Contact, delegate {
                    Context.Profile = W.FindProfile(p.Id); Go<ProfilePage>(); return Task.CompletedTask;
                });
                Command("完成", Symbol.Accept, delegate { Context.Close(); return Task.CompletedTask; });
            } else if (section == "url") {
                Title("从链接导入", "角色卡");
                var url = Field("PNG / JSON 直接链接", "");
                Text("仅下载公开 HTTP / HTTPS 文件，最多 5 MB。", true);
                Command("下载并解析", Symbol.Download, async delegate {
                    Uri uri; if (!Uri.TryCreate(Required(url), UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                        throw new ArgumentException("请输入 HTTP / HTTPS 地址。");
                    using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                    using (var response = await client.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, Lifetime.Token)) {
                        response.EnsureSuccessStatusCode();
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var memory = new MemoryStream()) {
                            var buffer = new byte[8192]; int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, Lifetime.Token)) > 0) {
                                if (memory.Length + read > CardImporter.MaxPngBytes) throw new InvalidOperationException("角色卡超过 5 MB。");
                                memory.Write(buffer, 0, read);
                            }
                            var result = CardImporter.Import(memory.ToArray(), uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "card.png" : "card.json");
                            if (!result.Ok) throw new InvalidOperationException(result.Error);
                            Go<ImportPage>("preview", result);
                        }
                    }
                });
            } else {
                Title("导入角色卡", "档案");
                Row("从本地文件", "PNG / JSON · SillyTavern v2 / v3", async delegate {
                    var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                    picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".json");
                    var file = await picker.PickSingleFileAsync(); if (file == null) return;
                    var result = CardImporter.Import(await FilesPage.ReadBytes(file, CardImporter.MaxPngBytes), file.Name);
                    if (!result.Ok) throw new InvalidOperationException(result.Error);
                    Go<ImportPage>("preview", result);
                });
                Link<ImportPage>("从链接下载", "公开 PNG / JSON 文件", "url");
            }
            return Task.CompletedTask;
        }
    }

    public sealed class BackupPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            string section = Context.Section ?? "home";
            if (section == "preview") {
                var file = (StorageFile)Context.Item;
                Title("备份检查与预览", "恢复");
                BackupManifest manifest;
                using (var stream = await file.OpenStreamForReadAsync()) {
                    string error = BackupService.Validate(stream);
                    if (error != null) throw new InvalidOperationException(error);
                    stream.Position = 0; manifest = BackupService.ReadManifest(stream);
                }
                Text(file.Name); Text(manifest.ProfileCount + " 个档案 · " + manifest.ConversationCount + " 个会话 · " + manifest.Entries.Count + " 个文件");
                Text(manifest.CreatedAtUtc.ToLocalTime().ToString("g"), true);
                Text("恢复会覆盖包内同名数据，其他文件保留。系统先保存恢复前备份；密钥需重新配置。", true);
                Command("恢复", Symbol.Upload, async delegate {
                    RequireIdle();
                    if (!await Confirm("覆盖当前工作区？", "将用此备份替换现有文件。先自动备份当前状态。", "恢复")) return;
                    RestoreResult result;
                    using (var stream = await file.OpenStreamForReadAsync())
                        result = await S.Backup.RestoreAsync(stream, "_restore", null, Lifetime.Token);
                    if (!result.Ok) throw new InvalidOperationException(result.Error);
                    await W.ReloadAsync();
                    Title("恢复完成", "备份与恢复"); Text("已恢复 " + result.RestoredFiles + " 个文件。"); Text("恢复前备份：" + result.SafetyBackupPath, true);
                    foreach (var warning in result.Warnings) Text(warning, true);
                    Command("完成", Symbol.Accept, delegate { Context.Close(); return Task.CompletedTask; });
                });
            } else if (section == "create") {
                Title("创建备份", "备份与恢复");
                Text(W.Profiles.Count + " 个档案 · 备份全部工作区数据。");
                Text("可选择备份内容；不包括 API 密钥。", true);
                var profiles = new System.Collections.Generic.Dictionary<string, CheckBox>(); foreach (var profile in W.Profiles) profiles[profile.Slug] = Check(profile.Name, true);
                var conversations = Check("会话", true); var files = Check("文件与附件", true); var memory = Check("记忆", true); var shared = Check("共享资源与配置", true);
                Command("开始备份", Symbol.Download, async delegate {
                    RequireIdle();
                    var file = await S.Files.CreateExportFileAsync(BackupNaming.DefaultFileName(DateTimeOffset.Now));
                    if (file == null) { Status.Text = "已取消。"; return; }
                    BackupResult result;
                    using (var stream = await file.OpenStreamForWriteAsync()) {
                        stream.SetLength(0);
                        result = await S.Backup.CreateAsync(stream, W, "1.1.0", S.Device.DeviceFamily,
                            delegate (int done, int total, string path) {
                                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate {
                                    Status.Text = "正在备份 " + done + " / " + total + " · " + WorkspaceLayout.FileName(path);
                                });
                            }, Lifetime.Token, new BackupSelection { ProfileSlugs = profiles.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList(), IncludeConversations = conversations.IsChecked == true, IncludeFiles = files.IsChecked == true, IncludeMemory = memory.IsChecked == true, IncludeShared = shared.IsChecked == true });
                    }
                    if (!result.Ok) throw new InvalidOperationException(result.Error);
                    Title("备份完成", "备份与恢复"); Text(file.Name);
                    Text(result.FileCount + " 个文件 · " + StorageStats.FormatBytes(result.ByteSize));
                    foreach (var warning in result.Warnings) Text(warning, true);
                    Command("完成", Symbol.Accept, delegate { Context.Close(); return Task.CompletedTask; });
                });
            } else {
                Title("备份与恢复", "数据");
                Link<BackupPage>("创建备份", "工作区 ZIP 文件，不含密钥", "create");
                Row("从备份恢复", "选择 .yanshuai-backup.zip", async delegate {
                    RequireIdle(); var file = await S.Files.PickBackupFileAsync();
                    if (file != null) Go<BackupPage>("preview", file);
                });
            }
        }
    }

    public sealed class StoragePage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            Title("存储空间", "数据");
            var stats = await S.StorageStats.MeasureAsync(Context.Profile != null ? Context.Profile.Slug : "", Lifetime.Token);
            Text("已用 " + StorageStats.FormatBytes(stats.TotalBytes) + " · " + stats.TotalFiles + " 个文件");
            foreach (var bucket in stats.Buckets) Text(bucket.Label + " · " + StorageStats.FormatBytes(bucket.Bytes));
            Link<TrashPage>("回收区", "恢复误删的档案、会话和文件");
            Command("重新统计", Symbol.Refresh, BuildAsync);
            Command("清理空间", Symbol.Delete, delegate { Go<StorageCleanupPage>(); return Task.CompletedTask; });
            Command("清理回收区", Symbol.Delete, async delegate {
                RequireIdle();
                if (await Confirm("永久清空回收区？", "所有回收项将永久删除，无法恢复。", "清空")) {
                    foreach (var entry in await S.Operations.ListTrashAsync()) await S.Operations.PurgeAsync(entry);
                    await W.PurgeTrashAsync(); await BuildAsync();
                }
            });
        }
    }
    public sealed class TrashPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            var entry = Context.Item as TrashEntry;
            if (entry != null) {
                Title("回收项详情", entry.Name); Text(entry.Kind + " · " + entry.DeletedAtUtc.ToLocalTime().ToString("g"));
                Text("原位置：" + entry.OriginalPath, true);
                Command("恢复", Symbol.Undo, async delegate { RequireIdle(); await S.Operations.RestoreAsync(entry); Finish(); });
                Command("永久删除", Symbol.Delete, async delegate {
                    RequireIdle(); if (await Confirm("永久删除？", "删除后不能恢复。", "删除")) { await S.Operations.PurgeAsync(entry); Finish(); }
                });
            } else {
                Title("回收区", "数据"); var entries = await S.Operations.ListTrashAsync();
                foreach (var item in entries) Link<TrashPage>(item.Name, item.Kind + " · " + item.DeletedAtUtc.ToLocalTime().ToString("d"), null, item);
                if (entries.Count == 0) Text("回收区为空。");
            }
        }
    }
    public sealed class DataPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("数据导入与导出", "数据");
            Link<ImportPage>("导入角色卡", "PNG / JSON / 链接");
            Link<MigrationPage>("迁移旧版数据", "AppData JSON，导入前预览");
            Link<BackupPage>("完整备份与恢复", "ZIP 工作区");
            if (Context.Profile != null) Row("导出档案", "SillyTavern 角色卡 JSON", async delegate {
                await ExportAsync(Context.Profile.Name + ".json", CardImporter.ExportJson(Context.Profile), ".json");
            });
            if (Context.Conversation != null) {
                Row("导出会话 JSON", "消息、分支、用量与附件路径", async delegate {
                    await ExportAsync(Context.Conversation.Title + ".json", JsonStore.Serialize(Context.Conversation), ".json");
                });
                Row("导出会话 Markdown", "适合阅读与分享", async delegate {
                    var text = new StringBuilder("# " + Context.Conversation.Title + "\n\n");
                    foreach (var message in Context.Conversation.Messages) text.Append("## ").Append(message.Role).Append("\n\n").Append(message.Content).Append("\n\n");
                    await ExportAsync(Context.Conversation.Title + ".md", text.ToString(), ".md");
                });
            }
            return Task.CompletedTask;
        }
        private async Task ExportAsync(string name, string content, string extension)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = name };
            picker.FileTypeChoices.Add("言枢数据", new[] { extension });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            await FileIO.WriteTextAsync(file, content); Status.Text = "已保存 " + file.Name;
        }
    }
}
