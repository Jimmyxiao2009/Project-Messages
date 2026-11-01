using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class ToolsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            // 工具全部常开、不可逐项修改。能否执行看对话页的「权限审批」三档。
            Title("内置工具", "模型与能力");
            Text("所有工具默认启用。能不能真的执行，由对话页「+」里的权限审批决定：仅可查看 / 工作区内修改 / 完全访问。", true);
            foreach (var group in S.Registry.All.OrderBy(x => x.Group).GroupBy(x => x.Group)) {
                Section(group.Key.ToString());
                foreach (var tool in group) {
                    Link<ToolDetailsPage>(tool.Name + (tool.IsSensitive ? " · 需批准" : ""), tool.Description, null, tool);
                }
            }
            Section("扩展");
            Link<ExtensionsPage>("MCP 服务器", W.McpServers.Count + " 个配置", "mcp");
            Link<ExtensionsPage>("技能", W.Skills.Count + " 个", "skills");
            Link<PermissionsPage>("权限记录", "查看与撤销");
            Link<FolderAccessPage>("授权目录", "系统文件夹授权");
            return Task.CompletedTask;
        }
    }    public sealed class ToolDetailsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var tool = Context.Item as ITool;
            Title(tool != null ? tool.Name : "脚本运行时", "内置工具");
            if (tool != null) {
                Text(tool.Description); Text(tool.IsSensitive ? "需要用户批准。" : "此工具无需审批。", true);
                var schema = Field("参数定义", JsonStore.Serialize(tool.Schema()), true); schema.IsReadOnly = true;
            }
            if (tool == null || tool.Name == "run_python") {
                Text(S.ScriptRuntime.IsAvailable ? "PyUwp 运行时可用。" : "此设备没有可用的 Python 运行时。");
                Text("脚本开关和敏感操作审批均开启后才能执行。", true);
            }
            return Task.CompletedTask;
        }
    }
    public sealed class ExtensionsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            bool mcp = Context.Section == "mcp";
            Title(mcp ? "MCP 服务器" : "技能", "工具与扩展");
            if (mcp) foreach (var server in W.McpServers)
                Link<ExtensionEditorPage>(server.Name, (server.Enabled ? "启用" : "停用") + " · " + server.Tools.Count + " 个工具", "mcp", server);
            else foreach (var skill in W.Skills)
                Link<ExtensionEditorPage>(skill.Name, skill.Description + (skill.Enabled ? "" : " · 停用"), "skill", skill);
            if (Body.Children.Count == 0) Text("还没有配置，点「新增」添加。");
            Command("新增", Symbol.Add, delegate {
                Go<ExtensionEditorPage>(mcp ? "mcp" : "skill", mcp ? (object)new McpServerConfig() : new SkillDefinition());
                return Task.CompletedTask;
            });
            if (!mcp) Command("从文件导入", Symbol.Import, async delegate { var file = await FilesPage.PickAsync(false); if (file == null) return; var bytes = await FilesPage.ReadBytes(file, 1024 * 1024); Go<ExtensionEditorPage>("skill", new SkillDefinition { Name = System.IO.Path.GetFileNameWithoutExtension(file.Name), Content = System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length) }); });
            if (mcp) Command("全部测试", Symbol.Refresh, async delegate {
                foreach (var server in W.McpServers) {
                    try {
                        var resolved = JsonBridge.Clone(server);
                        var token = S.Secrets.Get("mcp." + server.Id);
                        if (!string.IsNullOrEmpty(token)) resolved.Headers["Authorization"] = "Bearer " + token;
                        var tools = await new McpClient(S.Http, resolved).ListToolsAsync(Lifetime.Token);
                        server.Tools = tools; Text(server.Name + " · " + tools.Count + " 个工具");
                    } catch (Exception ex) { Text(server.Name + "：" + ex.Message); }
                }
                await W.SaveMcpAsync();
            });
            return Task.CompletedTask;
        }
    }
    public sealed class ExtensionEditorPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            bool mcp = Context.Section == "mcp";
            Title(mcp ? "MCP 服务器" : "技能", "编辑扩展");
            Func<Task> save; Func<Task> delete;
            if (mcp) {
                var original = (McpServerConfig)Context.Item; var draft = JsonBridge.Clone(original);
                var name = Field("名称", draft.Name); var url = Field("HTTP 地址", draft.Url);
                Text("Windows 10 Mobile 支持 HTTP 传输。", true);
                var token = Secret("Bearer 密钥（可选）", S.Secrets.Get("mcp." + original.Id));
                var headers = Field("自定义请求头（JSON）", JsonStore.Serialize(draft.Headers), true);
                var enabled = Toggle("启用", draft.Enabled);
                Action collect = delegate {
                    draft.Name = Required(name); draft.Url = Required(url); draft.Enabled = enabled.IsOn;
                    draft.Headers = JsonObject<Dictionary<string, string>>(headers);
                };
                save = async delegate {
                    collect();
                    W.McpServers.RemoveAll(x => x.Id == draft.Id); W.McpServers.Add(draft);
                    S.Secrets.Set("mcp." + draft.Id, token.Password); await W.SaveMcpAsync();
                };
                Link<McpToolsPage>("工具清单", original.Tools.Count + " 个已发现工具", null, original);
                delete = async delegate { W.McpServers.RemoveAll(x => x.Id == original.Id); S.Secrets.Remove("mcp." + original.Id); await W.SaveMcpAsync(); };
                Command("连接并拉取", Symbol.Refresh, async delegate {
                    collect(); var resolved = JsonBridge.Clone(draft);
                    if (token.Password.Length > 0) resolved.Headers["Authorization"] = "Bearer " + token.Password;
                    var client = new McpClient(S.Http, resolved); draft.Tools = await client.ListToolsAsync(Lifetime.Token); draft.ToolDescriptions = client.ToolDescriptions; draft.ToolSchemas = client.ToolSchemas;
                    Status.Text = "连接成功：" + draft.Tools.Count + " 个工具，保存后生效。";
                    Section("工具清单"); foreach (var tool in draft.Tools) Text(tool);
                }, true);
            } else {
                var original = (SkillDefinition)Context.Item;
                var name = Field("名称", original.Name); var description = Field("描述", original.Description);
                var content = Field("内容", original.Content, true);
                var mode = Choice("模式", new[] { "prefix", "system", "replace" }, original.Mode);
                var enabled = Toggle("启用", original.Enabled);
                save = async delegate {
                    var draft = JsonBridge.Clone(original); draft.Name = Required(name); draft.Description = description.Text;
                    draft.Content = Required(content); draft.Mode = (string)mode.SelectedItem; draft.Enabled = enabled.IsOn;
                    W.Skills.RemoveAll(x => x.Id == draft.Id); W.Skills.Add(draft); await W.SaveSkillsAsync();
                };
                delete = async delegate { W.Skills.RemoveAll(x => x.Id == original.Id); await W.SaveSkillsAsync(); };
            }
            Save(async delegate { RequireIdle(); await save(); });
            Command("删除", Symbol.Delete, async delegate {
                RequireIdle(); if (await Confirm("删除扩展？", "所有档案将不再使用此扩展。", "删除")) { await delete(); Finish(); }
            }, true);
            return Task.CompletedTask;
        }
    }

    public sealed class PermissionsPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            if (Context.Item is string) {
                string key = (string)Context.Item; Title("授权详情", "权限记录"); Text(key);
                Text("可撤销「始终允许」，之后再次执行将请求批准。", true);
                Command("撤销授权", Symbol.Delete, async delegate { await W.Permissions.RevokeAsync(key); Finish(); });
            } else {
                Title("权限记录", "我的");
                foreach (var key in W.Permissions.Granted) Link<PermissionsPage>(key, "始终允许", null, key);
                if (W.Permissions.Granted.Count == 0) Text("没有已记住的授权。");
                Command("全部清空", Symbol.Delete, async delegate {
                    if (await Confirm("撤销所有授权？", "敏感工具下次使用时将重新请求批准。")) { await W.Permissions.RevokeAllAsync(); Finish(); }
                });
            }
            return Task.CompletedTask;
        }
    }
    public sealed class FolderAccessPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("授权目录", "工具与扩展");
            foreach (var folder in W.Settings.AuthorizedFolders)
                Link<FolderPermissionPage>(folder.Name, (folder.ReadOnly ? "只读" : "读写") + " · " + (folder.ProfileId.Length == 0 ? "全部档案" : W.FindProfile(folder.ProfileId)?.Name ?? "已删除的档案"), null, folder);
            if (W.Settings.AuthorizedFolders.Count == 0) Text("暂无目录。系统授权后还需选择档案范围和权限。");
            Command("添加目录", Symbol.Add, async delegate {
                RequireIdle();
                var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                var access = Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList;
                var token = access.Add(folder, folder.Path);
                Go<FolderPermissionPage>(null, new AuthorizedFolder { Token = token, Name = folder.Name,
                    ProfileId = Context.Profile != null ? Context.Profile.Id : "", ReadOnly = true });
            });
            return Task.CompletedTask;
        }
    }

    public sealed class FolderPermissionPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var original = (AuthorizedFolder)Context.Item;
            Title("目录授权设置", original.Name);
            var name = Field("显示名称", original.Name);
            var permission = Choice("权限", new[] { "只读", "读写" }, original.ReadOnly ? "只读" : "读写");
            var profiles = W.Profiles.ToList();
            var ids = new List<string> { "" }; var names = new List<string> { "全部档案" };
            foreach (var profile in profiles) { ids.Add(profile.Id); names.Add(profile.Name); }
            var selected = Math.Max(0, ids.IndexOf(original.ProfileId));
            var scope = Choice("适用档案", names.ToArray(), names[selected]);
            Text("工具路径：_folders/" + original.Token + "/", true);
            Text("读写权限仍需 write_file 的敏感操作审批。", true);
            Save(async delegate {
                RequireIdle();
                var draft = new AuthorizedFolder { Token = original.Token, Name = Required(name),
                    ProfileId = ids[scope.SelectedIndex], ReadOnly = permission.SelectedIndex == 0 };
                W.Settings.AuthorizedFolders.RemoveAll(x => x.Token == draft.Token);
                W.Settings.AuthorizedFolders.Add(draft);
                await W.SaveSettingsAsync();
            });
            Command("浏览目录", Symbol.OpenFile, async delegate {
                var folder = await Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.GetFolderAsync(original.Token);
                Go<ExternalFolderPage>(original.Token, folder);
            }, true);
            Command("撤销授权", Symbol.Delete, async delegate {
                RequireIdle();
                if (!await Confirm("撤销目录授权？", "工具将不再访问此目录。")) return;
                Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.Remove(original.Token);
                W.Settings.AuthorizedFolders.RemoveAll(x => x.Token == original.Token);
                await W.SaveSettingsAsync(); Finish();
            }, true);
            return Task.CompletedTask;
        }
    }

    public sealed class ExternalFolderPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            var folder = (Windows.Storage.StorageFolder)Context.Item;
            Title(folder.Name, "授权目录"); Text(folder.Path, true);
            foreach (var child in await folder.GetFoldersAsync()) Link<ExternalFolderPage>(child.Name, "文件夹", Context.Section, child);
            foreach (var file in await folder.GetFilesAsync()) {
                var item = file;
                Row(file.Name, "导入到档案文件", async delegate {
                    if (Context.Profile == null) throw new InvalidOperationException("请从档案详情进入文件页导入。");
                    RequireIdle();
                    var target = WorkspaceLayout.FilesDir(Context.Profile.Slug) + "/" + Guid.NewGuid().ToString("N") + "-" + item.Name;
                    await S.Files.WriteBytesAsync(target, await FilesPage.ReadBytes(item, 32 * 1024 * 1024));
                    Status.Text = "已导入 " + item.Name;
                });
            }
        }
    }

}
