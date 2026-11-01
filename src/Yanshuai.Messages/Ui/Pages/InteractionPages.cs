using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class NewConversationPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            Title("新建会话", "选一个档案开始");
            var title = Field("标题（可选）", "");
            title.PlaceholderText = "留空会自动起标题";
            if (W.Profiles.Count > 0) Section("和谁聊");
            foreach (var p in W.Profiles) AvatarRow(p.Name, p.Api.Model.Length > 0 ? p.Api.Model : "跟随默认对话模型", p.Avatar, async delegate {
                RequireIdle(); await S.OpenProfileAsync(p);
                var conversation = await S.Chat.StartConversationAsync(title.Text, false);
                await Context.OpenChat(p, conversation);
            });
            if (W.Profiles.Count == 0) {
                Link<NewProfilePage>("新建档案", "先创建一个助手");
                Link<ImportPage>("导入角色卡", "PNG / JSON");
            }
            return Task.CompletedTask;
        }
    }

    public sealed class InteractionRequest
    {
        public ToolPermissionRequest Permission;
        public IReadOnlyList<AskQuestion> Questions;
        public Func<bool, Task> PermissionAnswer;
        public Func<IReadOnlyList<AskAnswer>, Task> AskAnswer;
    }

    public sealed class InteractionPage : DetailPage
    {
        private InteractionRequest _request;
        private bool _answered;
        protected override Task BuildAsync()
        {
            _request = (InteractionRequest)Context.Item;
            if (_request.Permission != null) {
                var p = _request.Permission;
                Title(p.Title ?? "需要你批准", "敏感操作");
                Text(p.Detail);
                foreach (var field in p.Fields) { Section(field.Key); Text(field.Value); }
                Text("本次批准会记住同一权限键，可在「权限记录」撤销。", true);
                Command("允许", Symbol.Accept, async delegate { _answered = true; await _request.PermissionAnswer(true); });
                Command("拒绝", Symbol.Cancel, async delegate { _answered = true; await _request.PermissionAnswer(false); });
            } else {
                Title("向你追问", "对话");
                var inputs = new List<Func<AskAnswer>>();
                foreach (var question in _request.Questions) {
                    Section(question.Text);
                    var checks = new List<CheckBox>();
                    foreach (var option in question.Options) checks.Add(Check(option, false));
                    if (!question.AllowMultiple) foreach (var check in checks) check.Checked += delegate {
                        foreach (var other in checks) if (other != check) other.IsChecked = false;
                    };
                    TextBox free = question.AllowFreeText && question.InputType != "date" ? Field(question.InputType == "number" ? "数字" : "补充回答", "") : null;
                    DatePicker date = null; if (question.InputType == "date") { date = new DatePicker { Header = "日期", HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch }; Body.Children.Add(date); }
                    inputs.Add(delegate {
                        var answer = new AskAnswer { Id = question.Id };
                        foreach (var check in checks) if (check.IsChecked == true) answer.Values.Add((string)check.Content);
                        if (date != null) answer.Values.Add(date.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                        if (free != null && !string.IsNullOrWhiteSpace(free.Text)) { if (question.InputType == "number") { double value; if (!double.TryParse(free.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) || double.IsInfinity(value) || double.IsNaN(value)) throw new System.ArgumentException(question.Text + "：请输入有效数字。"); } answer.Values.Add(free.Text.Trim()); }
                        return answer;
                    });
                }
                Command("提交", Symbol.Accept, async delegate {
                    var answers = new List<AskAnswer>(); foreach (var input in inputs) answers.Add(input());
                    foreach (var answer in answers) if (answer.Values.Count == 0) throw new ArgumentException("请回答所有问题。");
                    _answered = true; await _request.AskAnswer(answers);
                });
                Command("取消", Symbol.Cancel, RejectAsync);
            }
            return Task.CompletedTask;
        }
        private async Task RejectAsync()
        {
            if (_answered) return; _answered = true;
            if (_request.Permission != null) await _request.PermissionAnswer(false);
            else await _request.AskAnswer(new List<AskAnswer>());
        }
        public override async void Back() { await Safe(RejectAsync); }
    }
}
