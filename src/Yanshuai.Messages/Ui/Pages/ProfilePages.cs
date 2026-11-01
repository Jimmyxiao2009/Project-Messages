using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class ProfilePage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var p = Context.Profile;
            if (p == null) { Title("档案", "工作区"); Text("请先选择一个档案。"); return Task.CompletedTask; }
            Title(p.Name, "档案");
            var intro = new List<string>();
            if (p.Persona.Tags != null && p.Persona.Tags.Count > 0) intro.Add(string.Join(" · ", p.Persona.Tags));
            if (!string.IsNullOrWhiteSpace(p.Persona.Description)) intro.Add(TextUtil.Truncate(p.Persona.Description.Trim(), 80));
            intro.Add("创建于 " + p.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd"));
            Hero(p.Name, string.Join("\n", intro), p.Avatar);

            Section("人物");
            Row("身份", "名称、头像、标签、用户人设", delegate { Go<ProfileEditorPage>("identity"); return Task.CompletedTask; }, "\uE77B");
            Row("人设", "系统提示词、描述、性格、场景、首句", delegate { Go<ProfileEditorPage>("persona"); return Task.CompletedTask; }, "\uE8D4");
            Row("外观", "背景、透明度与消息颜色", delegate { Go<AppearancePage>("profile"); return Task.CompletedTask; }, "\uE790");

            Section("能力");
            Row("模型", p.Api.Model.Length > 0 ? p.Api.Model : "跟随默认对话模型", delegate { Go<ModelPickerPage>("profile"); return Task.CompletedTask; }, "\uE945");
            Row("生成参数", "温度、长度、推理强度", delegate { Go<ProfileEditorPage>("model"); return Task.CompletedTask; }, "\uE9E9");
            Row("工具 · MCP · 技能", "全部默认启用，权限在对话页「+」调整", delegate { Go<ToolsPage>("profile"); return Task.CompletedTask; }, "\uE90F");
            Row("记忆", p.Memory.Enabled ? "已开启 · 查看、编辑、整理" : "已关闭", delegate { Go<MemoryPage>(); return Task.CompletedTask; }, "\uE81C");
            Row("记忆与检索设置", "抽取频率、全局记忆、向量检索", delegate { Go<ProfileEditorPage>("memory"); return Task.CompletedTask; }, "\uE713");
            Row("世界书 · 注入", "选择提示词资源", delegate { Go<ResourcesPage>("selection"); return Task.CompletedTask; }, "\uE8F1");

            Section("数据");
            Row("会话", "查看与批量管理", delegate { Go<ConversationsPage>("profile"); return Task.CompletedTask; }, "\uE8BD");
            Row("文件", "档案文件与附件", delegate { Go<FilesPage>(); return Task.CompletedTask; }, "\uE8B7");
            Row("查看最终提示词", "检查装配结果", delegate { Go<PromptPreviewPage>(); return Task.CompletedTask; }, "\uE8A1");
            Command("新建会话", Symbol.Add, async delegate {
                RequireIdle(); await S.OpenProfileAsync(p);
                var c = await S.Chat.StartConversationAsync(null, false); await Context.OpenChat(p, c);
            });
            Command("导出", Symbol.Download, delegate { Go<DataPage>("profile"); return Task.CompletedTask; });
            Command("复制为新档案", Symbol.Copy, async delegate {
                RequireIdle(); var copy = JsonBridge.Clone(p); var created = await W.CreateProfileAsync(p.Name + " 副本", p.Slug + "-copy");
                copy.Id = created.Id; copy.Slug = created.Slug; copy.Name = created.Name; copy.Avatar = ""; copy.CreatedAtUtc = created.CreatedAtUtc;
                await W.SaveProfileAsync(copy); await W.ReloadAsync(); Context.Close();
            }, true);
            Command("删除档案", Symbol.Delete, async delegate {
                RequireIdle();
                if (await Confirm("删除档案？", "档案将移入回收区。", "删除")) {
                    await S.Operations.RecycleProfileAsync(p); Context.Close();
                }
            }, true);
            return Task.CompletedTask;
        }
    }

    public sealed class NewProfilePage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("新建档案", "档案");
            var kind = Choice("从哪里开始", new[] { "空白档案", "通用助手", "工程助理" }, "空白档案");
            var name = Field("名称", ""); var slug = Field("文件夹名（可选）", "");
            Text("文件夹名创建后不变；重复时自动追加编号。", true);
            Link<ImportPage>("从角色卡导入", "PNG / JSON / 链接");
            Save(async delegate {
                RequireIdle();
                var p = await W.CreateProfileAsync(Required(name), slug.Text);
                p.Tools.ToolsEnabled = (string)kind.SelectedItem == "工程助理";
                p.Persona.Name = p.Name;
                if ((string)kind.SelectedItem == "通用助手") p.Persona.SystemPrompt = "你是一位严谨、友好的助手。";
                p.Appearance = JsonBridge.Clone(W.Settings.DefaultAppearance);
                await W.SaveProfileAsync(p);
            });
            return Task.CompletedTask;
        }
    }

    public sealed class ProfileEditorPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var original = Context.Profile;
            if (original == null) throw new InvalidOperationException("没有选择档案。");
            var p = JsonBridge.Clone(original); p.Repair();
            var section = Context.Section ?? "identity";
            Title(section == "persona" ? "人设" : section == "model" ? "模型与生成" : section == "memory" ? "记忆与检索" : "身份", p.Name);
            Func<Task> commit;
            if (section == "persona")
            {
                var system = Field("系统提示词", p.Persona.SystemPrompt, true);
                Text("可用变量：{{user}} · {{char}} · {{cur_date}} · {{cur_time}}", true);
                var description = Field("角色描述", p.Persona.Description, true);
                var personality = Field("性格", p.Persona.Personality, true);
                var scenario = Field("场景", p.Persona.Scenario, true);
                var first = Field("首句", p.Persona.FirstMessage, true);
                var examples = Field("示例对话", p.Persona.ExampleDialogue, true);
                var post = Field("历史后置指令", p.Persona.PostHistoryInstructions, true);
                var creator = Field("创作者", p.Persona.Creator);
                var notes = Field("创作者备注", p.Persona.CreatorNotes, true);
                commit = delegate {
                    p.Persona.SystemPrompt = system.Text; p.Persona.Description = description.Text;
                    p.Persona.Personality = personality.Text; p.Persona.Scenario = scenario.Text;
                    p.Persona.FirstMessage = first.Text; p.Persona.ExampleDialogue = examples.Text;
                    p.Persona.PostHistoryInstructions = post.Text; p.Persona.Creator = creator.Text; p.Persona.CreatorNotes = notes.Text;
                    original.Persona = p.Persona; return Task.CompletedTask;
                };
            }
            else if (section == "model")
            {
                var selected = ModelSelection.Resolve(W.ApiProfiles, original, null);
                Text("当前模型：" + (selected == null || string.IsNullOrEmpty(selected.Model) ? "未选择" : selected.Name + " · " + selected.Model), true);
                Link<ModelPickerPage>("选择 Provider 与模型", "查看模型列表、能力与收藏", "profile");
                var temperature = Range("temperature", p.Api.Temperature ?? 0.7, 0, 2, 0.05);
                var tempDefault = Check("temperature 用模型默认", !p.Api.Temperature.HasValue);
                temperature.IsEnabled = tempDefault.IsChecked != true;
                tempDefault.Checked += delegate { temperature.IsEnabled = false; };
                tempDefault.Unchecked += delegate { temperature.IsEnabled = true; };
                var topP = Range("top_p", p.Api.TopP ?? 1, 0, 1, 0.05);
                var topDefault = Check("top_p 用模型默认", !p.Api.TopP.HasValue);
                topP.IsEnabled = topDefault.IsChecked != true;
                topDefault.Checked += delegate { topP.IsEnabled = false; }; topDefault.Unchecked += delegate { topP.IsEnabled = true; };
                var tokens = Field("最大输出 tokens（0 用模型默认）", (p.Api.MaxTokens ?? 0).ToString());
                var messages = Field("上下文消息数（0 不限）", p.Api.ContextMessageSize.ToString());
                var reasoning = Choice("推理级别", new[] { "auto", "low", "medium", "high" }, p.Api.ReasoningLevel);
                var stream = Toggle("流式输出", p.Generation.StreamOutput);
                var template = Field("用户消息模板", p.Generation.MessageTemplate, true);
                var headers = Field("自定义请求头（JSON）", JsonStore.Serialize(p.Api.CustomHeaders), true);
                var body = Field("自定义请求体（JSON）", JsonStore.Serialize(p.Api.CustomBody), true);
                commit = delegate {
                    if (!template.Text.Contains("{{message}}")) throw new ArgumentException("消息模板必须包含 {{message}}。");
                    p.Api.Temperature = tempDefault.IsChecked == true ? (double?)null : temperature.Value;
                    p.Api.TopP = topDefault.IsChecked == true ? (double?)null : topP.Value;
                    int max = Int(tokens, 0, 1000000); p.Api.MaxTokens = max == 0 ? (int?)null : max;
                    p.Api.ContextMessageSize = Int(messages, 0, 100000);
                    p.Api.ReasoningLevel = (string)reasoning.SelectedItem;
                    p.Api.CustomHeaders = JsonObject<Dictionary<string, string>>(headers);
                    p.Api.CustomBody = JsonObject<Dictionary<string, object>>(body);
                    p.Generation.StreamOutput = stream.IsOn; p.Generation.MessageTemplate = template.Text;
                    original.Api = p.Api; original.Generation = p.Generation; return Task.CompletedTask;
                };
            }
            else if (section == "memory")
            {
                var enabled = Toggle("启用记忆", p.Memory.Enabled);
                var global = Toggle("使用全局记忆池", p.Memory.UseGlobal);
                var auto = Toggle("自动总结", p.Memory.AutoSummarize);
                var recent = Toggle("引用近期会话", p.Memory.RecentChatsReference);
                var time = Toggle("时间提醒", p.Memory.TimeReminder);
                var rag = Toggle("向量检索", p.Memory.RagEnabled);
                var max = Field("最多记忆条数", p.Memory.MaxMemories.ToString());
                var min = Field("开始总结的消息数", p.Memory.MinMessagesForSummary.ToString());
                var top = Field("检索 Top K", p.Memory.RagTopK.ToString());
                var score = Range("最小相关度", p.Memory.RagMinScore, 0, 1, 0.05);
                commit = delegate {
                    p.Memory.Enabled = enabled.IsOn; p.Memory.UseGlobal = global.IsOn; p.Memory.AutoSummarize = auto.IsOn;
                    p.Memory.RecentChatsReference = recent.IsOn; p.Memory.TimeReminder = time.IsOn; p.Memory.RagEnabled = rag.IsOn;
                    p.Memory.MaxMemories = Int(max, 1, 10000); p.Memory.MinMessagesForSummary = Int(min, 2, 10000);
                    p.Memory.RagTopK = Int(top, 1, 100); p.Memory.RagMinScore = score.Value;
                    original.Memory = p.Memory; return Task.CompletedTask;
                };
            }
            else
            {
                var name = Field("名称", p.Name);
                var slug = Field("文件夹名", p.Slug); slug.IsReadOnly = true;
                var tags = Field("标签（逗号分隔）", string.Join(", ", p.Persona.Tags));
                var personaIds = new List<string> { "" };
                var personaNames = new List<string> { "跟随默认" };
                foreach (var user in W.UserPersonas.Items) { personaIds.Add(user.Id); personaNames.Add(user.Name); }
                var choice = Choice("用户人设", personaNames.ToArray(), personaNames[Math.Max(0, personaIds.IndexOf(p.UserPersonaId))]);
                commit = delegate {
                    original.Name = Required(name); original.Persona.Name = original.Name;
                    original.Persona.Tags = Words(tags.Text).Select(x => x.Trim()).ToList();
                    original.UserPersonaId = personaIds[choice.SelectedIndex]; return Task.CompletedTask;
                };
                Link<FilesPage>("选择头像", "从本地图片选择", "avatar");
            }
            Save(async delegate { RequireIdle(); await commit(); await W.SaveProfileAsync(original); });
            return Task.CompletedTask;
        }
    }
}
