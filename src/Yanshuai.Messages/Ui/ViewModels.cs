// Ui/ViewModels.cs —— 页面绑定的视图模型
//
// 为什么不直接绑 Core 的模型：Core 里的类型是"数据"，不该带"该显示成什么颜色"。
// 这里包一层，把状态翻译成文案与画刷。接手的 AI 想改视觉，改这一层就够了。

using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui
{
    public sealed class ConversationGroup
    {
        public string Title { get; set; }
        public bool Attention { get; set; }
        public List<ConversationVm> Items { get; set; } = new List<ConversationVm>();
        public Brush HeaderBrush { get { return ConversationVm.Brush(Attention ? "WarnTextBrush" : "MidBrush"); } }
    }

    public sealed class ConversationVm
    {
        public ConversationIndexEntry Entry { get; private set; }

        /// <summary>所属档案名，第三行显示。由列表刷新时填入（索引条目里只有档案 id）。</summary>
        public string ProfileName { get; set; }

        public ConversationVm(ConversationIndexEntry entry, string profileName = null)
        {
            Entry = entry;
            ProfileName = profileName ?? "";
        }

        // ── 第三行：状态徽标 · 档案名 · 用量 ──
        private bool HasBadge { get { return Entry.NeedsAttention || Entry.Running || Entry.Failed; } }

        public string BadgeText
        {
            get
            {
                if (Entry.NeedsAttention) return "需要你处理";
                if (Entry.Running) return "运行中";
                if (Entry.Failed) return "失败";
                return "";
            }
        }

        public Brush BadgeBackground
        {
            get
            {
                if (Entry.NeedsAttention) return Brush("WarnSoftBrush");
                if (Entry.Running) return Brush("AccentSoftBrush");
                return Brush("ErrSoftBrush");
            }
        }

        public Brush BadgeForeground
        {
            get
            {
                if (Entry.NeedsAttention) return Brush("WarnTextBrush");
                if (Entry.Running) return Brush("TextBrush");
                return Brush("ErrTextBrush");
            }
        }

        public Visibility BadgeVisibility { get { return HasBadge ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility PlainStatusVisibility { get { return HasBadge ? Visibility.Collapsed : Visibility.Visible; } }

        public Visibility ProfileNameVisibility
        {
            get { return string.IsNullOrEmpty(ProfileName) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string TokenShort
        {
            get
            {
                if (Entry.Tokens <= 0) return "";
                if (Entry.Tokens < 1000) return Entry.Tokens.ToString(CultureInfo.InvariantCulture);
                return (Entry.Tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            }
        }

        public Visibility TokenVisibility { get { return Entry.Tokens > 0 ? Visibility.Visible : Visibility.Collapsed; } }

        public string Title
        {
            get { return string.IsNullOrEmpty(Entry.Title) ? "新对话" : Entry.Title; }
        }

        public string Preview
        {
            get { return string.IsNullOrEmpty(Entry.Preview) ? "（还没有内容）" : Entry.Preview; }
        }

        public string TimeLabel
        {
            get { return RelativeTime(Entry.UpdatedAtUtc); }
        }

        public string StatusText
        {
            get
            {
                if (Entry.NeedsAttention) return "需要你处理";
                if (Entry.Running) return "运行中";
                if (Entry.Failed) return "上次失败";
                return TokenLabel;
            }
        }

        private string TokenLabel
        {
            get
            {
                if (Entry.Tokens <= 0) return "已完成";
                if (Entry.Tokens < 1000) return Entry.Tokens.ToString(CultureInfo.InvariantCulture) + " tokens";
                return (Entry.Tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k tokens";
            }
        }

        /// <summary>左侧 4px 色条：扫一眼就知道哪条要处理。</summary>
        public Brush ToneBrush
        {
            get
            {
                if (Entry.NeedsAttention) return Brush("WarnBrush");
                if (Entry.Running) return Brush("AccentBrush");
                if (Entry.Failed) return Brush("ErrBrush");
                return Brush("LowBrush");
            }
        }

        public static Brush Brush(string key)
        {
            object value;
            if (Application.Current.Resources.TryGetValue(key, out value))
            {
                var brush = value as SolidColorBrush;
                if (brush != null) return brush;
            }
            return new SolidColorBrush(Windows.UI.Colors.Gray);
        }

        public static string RelativeTime(DateTimeOffset when)
        {
            var local = when.ToLocalTime();
            var now = DateTimeOffset.Now;
            var span = now - local;

            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return ((int)span.TotalMinutes) + " 分钟前";

            if (local.Date == now.Date) return local.ToString("HH:mm", CultureInfo.InvariantCulture);

            var yesterday = now.Date.AddDays(-1);
            if (local.Date == yesterday) return "昨天";

            if (local.Year == now.Year) return local.ToString("M月d日", CultureInfo.InvariantCulture);
            return local.ToString("yyyy年M月", CultureInfo.InvariantCulture);
        }
    }

    public sealed class ProfileVm
    {
        public Profile Profile { get; private set; }

        public ProfileVm(Profile profile)
        {
            Profile = profile;
        }

        public string Name
        {
            get { return string.IsNullOrEmpty(Profile.Name) ? "未命名档案" : Profile.Name; }
        }

        public string Initial
        {
            get
            {
                var name = Name;
                return name.Length > 0 ? name.Substring(0, 1) : "言";
            }
        }

        public string AvatarPath { get { return Profile.Avatar; } }
        public string AvatarUri { get { return string.IsNullOrEmpty(Profile.Avatar) ? "" : Platform.AppServices.Current.Files.GetAbsolutePath(Profile.Avatar); } }
        public Visibility AvatarVisibility { get { return string.IsNullOrEmpty(Profile.Avatar) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility InitialVisibility { get { return string.IsNullOrEmpty(Profile.Avatar) ? Visibility.Visible : Visibility.Collapsed; } }
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();

                if (!string.IsNullOrEmpty(Profile.Api.Model)) parts.Add(Profile.Api.Model);
                else parts.Add("跟随默认模型");

                if (Profile.Memory.Enabled) parts.Add("记忆已开");
                else parts.Add("记忆已关");

                if (Profile.Tools.Enabled != null && Profile.Tools.Enabled.Count > 0)
                {
                    parts.Add(Profile.Tools.Enabled.Count.ToString(CultureInfo.InvariantCulture) + " 个工具");
                }
                else
                {
                    parts.Add("全部工具");
                }

                return string.Join(" · ", parts.ToArray());
            }
        }
    }

    /// <summary>
    /// 时间线一行。模板选择器按 Kind 分派，与设计稿 §5.4 的行类型一一对应。
    /// 实现 INotifyPropertyChanged：流式输出时只更新这一条并通知，不重建整个列表。
    /// </summary>
    public sealed class RowVm : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public TimelineRow Row { get; private set; }

        public RowVm(TimelineRow row)
        {
            Row = row;
        }

        /// <summary>就地刷新整行（流式增量、步骤状态变化时调用）。</summary>
        public void RaiseChanged()
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(null));
            }
        }

        public RowKind Kind { get { return Row.Kind; } }

        // ── 用户 / 输出 ──
        private static readonly RegexTransformer VisualTransformer = new RegexTransformer();
        public string Text { get { var rules = Platform.AppServices.Current.Workspace.Settings.RegexRules; VisualTransformer.Load(rules); return VisualTransformer.ApplyVisual(Row.Text ?? "", Row.Kind == RowKind.User); } }
        private AppearanceOptions Appearance
        {
            get { var s = Platform.AppServices.Current; return s.Chat.Conversation != null && s.Chat.Conversation.AppearanceOverride != null
                    ? s.Chat.Conversation.AppearanceOverride : s.Chat.Profile != null ? s.Chat.Profile.Appearance : s.Workspace.Settings.DefaultAppearance; }
        }
        public int FontSize { get { return Platform.AppServices.Current.Workspace.Settings.BodyFontSize; } }
        public int LineHeight { get { return Platform.AppServices.Current.Workspace.Settings.LineHeight; } }
        public string QuoteColor { get { return Appearance.QuoteColor; } }
        public string BracketColor { get { return Appearance.BracketColor; } }
        public Brush UserBrush { get { var color = ThemeHelper.ParseHex(Appearance.UserBubbleColor); return color.A > 0 ? new SolidColorBrush(color) : ConversationVm.Brush("AccentBrush"); } }
        public Brush UserForeground { get { var color = ThemeHelper.ParseHex(Appearance.UserBubbleColor); return color.A > 0 ? new SolidColorBrush(ThemeHelper.ContrastForeground(color)) : ConversationVm.Brush("OnAccentBrush"); } }

        public Visibility TextVisibility
        {
            get { return string.IsNullOrEmpty(Row.Text) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public List<Attachment> Attachments { get { return Row.Attachments; } }

        /// <summary>气泡操作行（复制/重试/分支）是否显示：只认最终定稿的助手消息和用户消息。</summary>
        public Visibility ActionsVisibility
        {
            get
            {
                if (Row.Source == null) return Visibility.Collapsed;
                if (Row.Kind == RowKind.Output) return Row.IsFinal ? Visibility.Visible : Visibility.Collapsed;
                if (Row.Kind == RowKind.User) return Visibility.Visible;
                return Visibility.Collapsed;
            }
        }

        public Visibility AttachVisibility
        {
            get { return Row.Attachments != null && Row.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string AttachText
        {
            get
            {
                if (Row.Attachments == null || Row.Attachments.Count == 0) return "";
                var names = new List<string>();
                foreach (var attachment in Row.Attachments) names.Add(attachment.Name);
                return string.Join(" · ", names.ToArray());
            }
        }

        // ── 过程 ──
        public List<StepVm> StepItems
        {
            get
            {
                // 运行中且步骤很多时只露最近若干步，更早的折叠成一个"显示前面 N 步"节点
                bool running = Row.State == StepState.Running;
                var visible = TimelineProjector.VisibleSteps(Row.Steps, running);

                var list = new List<StepVm>();
                foreach (var step in visible) list.Add(new StepVm(step));
                return list;
            }
        }

        public Visibility RailVisibility { get { return Row.Collapsed ? Visibility.Collapsed : Visibility.Visible; } }
        public void ToggleRail() { Row.Collapsed = !Row.Collapsed; RaiseProcessChanged(); }

        /// <summary>
        /// 过程行的定向通知。
        /// 不能用 RaiseChanged()（PropertyChanged(null) = "全部属性已变"）：过程行模板里带嵌套的
        /// ItemsControl，绑定引擎在重读整行时会抛 NRE（HResult 0x80004003，堆栈为空，连
        /// DebugSettings.BindingFailed 都收不到），异常直接穿透 Click 处理器 →
        /// 表现为"点过程行没反应" + crash.log 里一条空堆栈的 XAML NRE。
        /// 工具循环里的 StepUpdated 也走这里，所以它同时也是"工具调用看起来不工作"的原因。
        /// </summary>
        public void RaiseProcessChanged()
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            handler(this, new System.ComponentModel.PropertyChangedEventArgs("RailVisibility"));
            handler(this, new System.ComponentModel.PropertyChangedEventArgs("Summary"));
            handler(this, new System.ComponentModel.PropertyChangedEventArgs("StateGlyph"));
            handler(this, new System.ComponentModel.PropertyChangedEventArgs("StateBrush"));
            // 必须带 StepItems：它是嵌套列表的数据源，且 getter 每次新建 StepVm。
            // 不通知它，工具步骤的状态变化（运行中 → 完成 / 失败）在界面上不会刷新，
            // 看起来就像"工具跑了但没反应"。
            handler(this, new System.ComponentModel.PropertyChangedEventArgs("StepItems"));
        }

        public string Summary { get { return Row.Summary ?? ""; } }

        public string StateGlyph
        {
            get
            {
                switch (Row.State)
                {
                    case StepState.Running: return "\uE895";     // 进行中
                    case StepState.Failed: return "\uE783";      // 错误
                    case StepState.Stopped: return "\uE7BA";     // 警告
                    default: return "\uE73E";                    // 完成
                }
            }
        }

        /// <summary>过程轨道竖线：失败转红、停止转灰。</summary>
        public Brush RailBrush
        {
            get
            {
                if (Row.State == StepState.Failed) return ConversationVm.Brush("ErrBrush");
                if (Row.State == StepState.Stopped) return ConversationVm.Brush("MidBrush");
                return ConversationVm.Brush("AccentBrush");
            }
        }

        public Brush StateBrush
        {
            get
            {
                switch (Row.State)
                {
                    case StepState.Running: return ConversationVm.Brush("AccentTextBrush");
                    case StepState.Failed: return ConversationVm.Brush("ErrTextBrush");
                    case StepState.Stopped: return ConversationVm.Brush("WarnBrush");
                    default: return ConversationVm.Brush("OkBrush");
                }
            }
        }

        // ── 错误 ──
        public string ErrorCode { get { return Row.ErrorCode ?? ""; } }

        public Visibility ErrorCodeVisibility
        {
            get { return string.IsNullOrEmpty(Row.ErrorCode) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string TimeLabel { get { return Row.Time == default(DateTimeOffset) ? "" : ConversationVm.RelativeTime(Row.Time); } }
    }

    /// <summary>步骤节点的展示（过程轨道里的一项）。</summary>
    public sealed class StepVm
    {
        public StepRecord Step { get; private set; }

        public StepVm(StepRecord step)
        {
            Step = step;
        }

        public string Title { get { return Step.Title ?? ""; } }

        public string Subtitle { get { return Step.Subtitle ?? ""; } }

        public Visibility SubtitleVisibility
        {
            get { return string.IsNullOrEmpty(Step.Subtitle) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string Body { get { return Step.Body ?? ""; } }

        public Visibility BodyVisibility
        {
            get { return string.IsNullOrEmpty(Step.Body) ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>节点形状：思考是圆点，工具是方块，失败是红方块。</summary>
        public string Glyph
        {
            get
            {
                switch (Step.Kind)
                {
                    case StepKind.Reasoning: return "\uE82F";
                    case StepKind.More: return "\uE712";
                    case StepKind.System: return "\uE946";
                    case StepKind.Error: return "\uE783";
                    case StepKind.Note: return "\uE8BB";
                    default: return "\uE713";
                }
            }
        }

        // 节点 10×10 压在 2px 轨道上：工具是方块，其余是圆点。
        public CornerRadius NodeCornerRadius { get { return new CornerRadius(Step.Kind == StepKind.Tool ? 0 : 5); } }
        public Brush NodeBrush
        {
            get
            {
                switch (Step.State)
                {
                    case StepState.Running: return ConversationVm.Brush("AccentBrush");
                    case StepState.Failed: return ConversationVm.Brush("ErrBrush");
                    case StepState.Stopped: return ConversationVm.Brush("MidBrush");
                    default:
                        switch (Step.Kind)
                        {
                            case StepKind.Reasoning: return ConversationVm.Brush("ThinkBorderBrush");
                            case StepKind.More: return ConversationVm.Brush("AccentBrush");
                            case StepKind.Note: return ConversationVm.Brush("MidBrush");
                            case StepKind.System: return ConversationVm.Brush("FaintBrush");
                            case StepKind.Error: return ConversationVm.Brush("ErrBrush");
                            default: return ConversationVm.Brush("OkBrush");
                        }
                }
            }
        }

        public Brush TitleBrush
        {
            get
            {
                if (Step.State == StepState.Failed || Step.Kind == StepKind.Error) return ConversationVm.Brush("ErrTextBrush");
                switch (Step.Kind)
                {
                    case StepKind.Reasoning: return ConversationVm.Brush("ThinkBrush");
                    case StepKind.More: return ConversationVm.Brush("AccentTextBrush");
                    case StepKind.Note: return ConversationVm.Brush("SoftTextBrush");
                    default: return ConversationVm.Brush("TextBrush");
                }
            }
        }

        public Windows.UI.Text.FontStyle TitleFontStyle
        {
            get { return Step.Kind == StepKind.Reasoning ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal; }
        }

        public string StatusGlyph
        {
            get
            {
                switch (Step.State)
                {
                    case StepState.Failed: return "\uE711";
                    case StepState.Running: return "";
                    default: return Step.Kind == StepKind.Tool ? "\uE73E" : "";
                }
            }
        }

        public Brush StatusBrush
        {
            get
            {
                return Step.State == StepState.Failed
                    ? ConversationVm.Brush("ErrTextBrush")
                    : ConversationVm.Brush("OkBrush");
            }
        }
    }
}
