using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Platform;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class PageContext
    {
        public AppServices Services { get; set; }
        public Profile Profile { get; set; }
        public Conversation Conversation { get; set; }
        public object Item { get; set; }
        public string Section { get; set; }
        public Action Close { get; set; }
        public Func<AppServices, Task> SwitchServices { get; set; }
        public Func<Profile, Conversation, Task> OpenChat { get; set; }
        public Func<bool> IsRunning { get; set; }
        public Action<Attachment> AddAttachment { get; set; }

        public PageContext With(string section, object item)
        {
            return new PageContext { Services = Services, Profile = Profile, Conversation = Conversation,
                Section = section, Item = item, Close = Close, SwitchServices = SwitchServices, OpenChat = OpenChat,
                IsRunning = IsRunning, AddAttachment = AddAttachment };
        }
    }

    // Native W10M page chrome: 12/24 header, 12px inset, bottom CommandBar.
    // Forms keep their own drafts; cancellation never mutates the workspace.
    public abstract class DetailPage : Page
    {
        protected PageContext Context;
        protected AppServices S { get { return Context.Services; } }
        protected Workspace W { get { return S.Workspace; } }
        protected readonly StackPanel Body = new StackPanel();
        protected readonly CommandBar Commands = new CommandBar();
        protected readonly TextBlock Status = new TextBlock();
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _label = new TextBlock();
        private readonly ProgressBar _busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private bool _working;
        private bool _dirty;
        private bool _departed;
        protected bool RetainDraftOnBack;
        private Func<Task> _saveAction;
        private readonly Button _headerSave = new Button { Content = "保存", Visibility = Visibility.Collapsed };
        protected CancellationTokenSource Lifetime = new CancellationTokenSource();

        protected DetailPage()
        {
            NavigationCacheMode = NavigationCacheMode.Disabled;
            Background = Brush("BgBrush");
            var grid = new Grid { Background = Brush("BgBrush") };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // 设计稿 .ph：12 上标签 + 24 SemiLight 标题，最小高 62，页边 12。
            // 手机走系统返回键，不画箭头；桌面没有硬件返回，保留一个无底色的图标按钮。
            bool mobile = ThemeHelper.IsMobile;
            var header = new Grid { Margin = new Thickness(mobile ? 12 : 0, 8, 0, 6), MinHeight = 62 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var back = new Button { Content = "\uE72B", Style = FindStyle("BackGlyphButton"),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2),
                Visibility = mobile ? Visibility.Collapsed : Visibility.Visible };
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(back, "返回");
            back.Click += delegate { Back(); };
            header.Children.Add(back);
            _headerSave.Style = FindStyle("HeaderTextButton");
            _headerSave.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(_headerSave, 2); header.Children.Add(_headerSave);
            _headerSave.Click += async delegate { await Safe(async delegate { await _saveAction(); _dirty = false; Finish(); }); };
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            _label.Style = FindStyle("PageOverline");
            _title.Style = FindStyle("PageTitle");
            _label.Visibility = Visibility.Collapsed;
            titles.Children.Add(_label); titles.Children.Add(_title);
            Grid.SetColumn(titles, 1); header.Children.Add(titles); grid.Children.Add(header);

            // 忙碌：贴在页面最顶上的一条细进度，不占布局
            _busy.VerticalAlignment = VerticalAlignment.Top;
            _busy.Foreground = Brush("AccentBrush");
            _busy.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            _busy.MinHeight = 0; _busy.Height = 4;
            Grid.SetRowSpan(_busy, 2); grid.Children.Add(_busy);

            // 状态提示：左侧 4px 色条的提示块；没有内容时整块收起，不留空行
            Status.FontSize = 13; Status.TextWrapping = TextWrapping.Wrap; Status.IsTextSelectionEnabled = true;
            _statusBox.Child = Status;
            _statusBox.Margin = new Thickness(12, 0, 12, 8);
            _statusBox.Padding = new Thickness(12, 8, 12, 8);
            _statusBox.BorderThickness = new Thickness(4, 0, 0, 0);
            _statusBox.Visibility = Visibility.Collapsed;
            Status.RegisterPropertyChangedCallback(TextBlock.TextProperty, delegate { SyncStatus(); });
            Grid.SetRow(_statusBox, 1); grid.Children.Add(_statusBox);

            Body.Margin = new Thickness(12, 8, 12, 24);
            var scroll = new ScrollViewer { Content = Body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
            Commands.Background = Brush("ChromeBrush"); Commands.DefaultLabelPosition = CommandBarDefaultLabelPosition.Bottom;
            Commands.Visibility = Visibility.Collapsed;
            Grid.SetRow(Commands, 3); grid.Children.Add(Commands);
            Content = grid;
        }

        private readonly Border _statusBox = new Border();

        private void SyncStatus()
        {
            var text = Status.Text ?? "";
            _statusBox.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            bool error = text.StartsWith("操作失败", StringComparison.Ordinal) || text.StartsWith("无法", StringComparison.Ordinal)
                || text.IndexOf("失败", StringComparison.Ordinal) >= 0 || text.IndexOf("错误", StringComparison.Ordinal) >= 0;
            _statusBox.Background = Brush(error ? "ErrSoftBrush" : "ChromeLowBrush");
            _statusBox.BorderBrush = Brush(error ? "ErrBrush" : "AccentBrush");
            Status.Foreground = Brush(error ? "ErrTextBrush" : "SoftTextBrush");
        }

        private void SyncCommands()
        {
            Commands.Visibility = Commands.PrimaryCommands.Count + Commands.SecondaryCommands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 软键盘弹出时底部 CommandBar 会被盖住，把「保存」提到标题行；收起时放回去，不重复出现
        private Windows.UI.ViewManagement.InputPane _inputPane;

        private void HookInputPane()
        {
            try
            {
                _inputPane = Windows.UI.ViewManagement.InputPane.GetForCurrentView();
                _inputPane.Showing += InputPaneShowing;
                _inputPane.Hiding += InputPaneHiding;
            }
            catch (Exception) { _inputPane = null; }
        }

        private void UnhookInputPane()
        {
            if (_inputPane == null) return;
            _inputPane.Showing -= InputPaneShowing;
            _inputPane.Hiding -= InputPaneHiding;
            _inputPane = null;
        }

        private void InputPaneShowing(Windows.UI.ViewManagement.InputPane sender, Windows.UI.ViewManagement.InputPaneVisibilityEventArgs args)
        {
            _headerSave.Visibility = _saveAction != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InputPaneHiding(Windows.UI.ViewManagement.InputPane sender, Windows.UI.ViewManagement.InputPaneVisibilityEventArgs args)
        {
            _headerSave.Visibility = Visibility.Collapsed;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_departed) { Lifetime = new CancellationTokenSource(); _departed = false; }
            if (RetainDraftOnBack && e.NavigationMode == NavigationMode.Back && Context != null) return;
            Context = e.Parameter as PageContext;
            if (Context == null) return;
            RequestedTheme = S.Workspace.Settings.Theme == "light" ? ElementTheme.Light
                : S.Workspace.Settings.Theme == "system" ? ElementTheme.Default : ElementTheme.Dark;
            HookInputPane();
            await Safe(BuildAsync);
            _dirty = false;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            UnhookInputPane();
            _departed = true;
            Lifetime.Cancel();
            Lifetime.Dispose();
            base.OnNavigatedFrom(e);
        }

        protected abstract Task BuildAsync();
        protected void Title(string title, string label)
        {
            _title.Text = title; _label.Text = label ?? "";
            _label.Visibility = string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;
            Body.Children.Clear();
            Commands.PrimaryCommands.Clear(); Commands.SecondaryCommands.Clear(); Status.Text = "";
            _saveAction = null; _headerSave.Visibility = Visibility.Collapsed;
            SyncCommands();
        }

        public virtual async void Back()
        {
            if (_working) { Status.Text = "正在处理，请稍候。"; return; }
            await Safe(async delegate {
                if (_dirty && _saveAction != null && !await Confirm("放弃未保存的修改？", "返回后将丢弃此表单的修改。", "放弃")) return;
                Finish();
            });
        }

        protected async void Go<T>(string section = null, object item = null, bool retainDraft = false) where T : Page
        {
            try {
                if (!retainDraft && _dirty && _saveAction != null && !await Confirm("放弃未保存的修改？", "离开后将丢弃此表单修改。", "放弃")) return;
                if (!retainDraft) _dirty = false;
                Frame.Navigate(typeof(T), Context.With(section, item));
            } catch (Exception ex) { App.WriteCrashLog("NAV", ex); Status.Text = "无法打开页面：" + ex.Message; }
        }

        protected async Task Safe(Func<Task> action)
        {
            if (_working) return;
            _working = true; _busy.Visibility = Visibility.Visible;
            try { await action(); }
            catch (OperationCanceledException) { Status.Text = "操作已取消。"; }
            catch (Exception ex)
            {
                App.WriteCrashLog(GetType().Name, ex);
                Status.Text = "操作失败：" + ex.Message;
            }
            finally { _working = false; _busy.Visibility = Visibility.Collapsed; }
        }

        protected void Command(string label, Symbol symbol, Func<Task> action, bool secondary = false)
        {
            var button = new AppBarButton { Label = label, Icon = new SymbolIcon(symbol) };
            button.Click += async delegate { await Safe(action); };
            if (secondary || Commands.PrimaryCommands.Count >= 3) Commands.SecondaryCommands.Add(button);
            else Commands.PrimaryCommands.Add(button);
            SyncCommands();
        }

        protected void Save(Func<Task> save)
        {
            _saveAction = save;
            if (_inputPane != null && _inputPane.OccludedRect.Height > 0) _headerSave.Visibility = Visibility.Visible;
            Command("保存", Symbol.Save, async delegate { await save(); _dirty = false; Finish(); });
            Command("取消", Symbol.Cancel, delegate { Finish(); return Task.CompletedTask; });
        }

        protected void Finish()
        {
            if (Frame.CanGoBack) Frame.GoBack(); else Context.Close();
        }

        protected void Section(string name)
        {
            SectionLabel(name);
        }

        protected TextBlock Text(string text, bool muted = false)
        {
            var block = new TextBlock { Text = text ?? "", Style = FindStyle(muted ? "SubText" : "BodyText"),
                IsTextSelectionEnabled = true, Margin = new Thickness(0, muted ? 2 : 4, 0, 8) };
            if (muted) block.TextTrimming = TextTrimming.None;
            // 空文字不占一行（例如档案没有标签时）
            if (string.IsNullOrWhiteSpace(text)) block.Visibility = Visibility.Collapsed;
            block.RegisterPropertyChangedCallback(TextBlock.TextProperty, delegate {
                block.Visibility = string.IsNullOrWhiteSpace(block.Text) ? Visibility.Collapsed : Visibility.Visible;
            });
            Body.Children.Add(block); return block;
        }

        /// <summary>页面顶部的大头像 + 名字 + 一行说明（档案页这类"一个对象"的详情页）。</summary>
        protected void Hero(string name, string subtitle, string avatarPath)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(Avatar(name, avatarPath, 72));
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            if (!string.IsNullOrWhiteSpace(subtitle))
                texts.Children.Add(new TextBlock { Text = subtitle, Style = FindStyle("SubText"), MaxLines = 3 });
            Grid.SetColumn(texts, 1); grid.Children.Add(texts);
            Body.Children.Add(grid);
        }

        /// <summary>圆形头像：有图用图，没图用首字 + 强调色柔底。</summary>
        protected static FrameworkElement Avatar(string name, string avatarPath, double size)
        {
            var holder = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            holder.Children.Add(new Windows.UI.Xaml.Shapes.Ellipse { Fill = Brush("AccentSoftBrush") });
            if (!string.IsNullOrEmpty(avatarPath))
                holder.Children.Add(new WorkspaceImage { SourcePath = avatarPath, Stretch = Stretch.UniformToFill, Circular = true });
            else
                holder.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(name) ? "言" : name.Substring(0, 1),
                    FontSize = Math.Round(size * 0.42), FontWeight = Windows.UI.Text.FontWeights.SemiLight, Foreground = Brush("TextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return holder;
        }

        /// <summary>带 40px 圆形头像的列表行（选档案、选成员）。</summary>
        protected Button AvatarRow(string title, string subtitle, string avatarPath, Func<Task> action)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var avatar = Avatar(title, avatarPath, 40);
            avatar.Margin = new Thickness(0, 0, 12, 0);
            grid.Children.Add(avatar);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title ?? "", Style = FindStyle("RowTitle") });
            if (!string.IsNullOrEmpty(subtitle)) texts.Children.Add(new TextBlock { Text = subtitle, Style = FindStyle("RowSubtitle") });
            Grid.SetColumn(texts, 1); grid.Children.Add(texts);
            var button = new Button { Content = grid, Style = FindStyle("RowButton"), Margin = new Thickness(-12, 0, -12, 0), MinHeight = 60 };
            button.Click += async delegate { if (action != null) await Safe(action); };
            Body.Children.Add(button); return button;
        }

        /// <summary>
        /// 列表行（设计稿 .row）：透明底、24px 图标、15 标题、12 Mid 副标题写当前值；
        /// 无 chevron、无分隔线，靠副标题表达"可点"。行按钮左右各伸出 12 贴到屏幕边，
        /// 这样按下的倾斜反馈是整行，而文字仍与页边 12 对齐。
        /// chevron 参数保留只为兼容旧调用，W10M 列表行不画箭头。
        /// </summary>
        protected Button Row(string title, string subtitle, Func<Task> action = null,
                             string glyph = null, bool chevron = false)
        {
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (!string.IsNullOrEmpty(glyph))
            {
                var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 20, Width = 24, TextAlignment = TextAlignment.Center, Foreground = Brush("TextBrush"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                Grid.SetColumn(icon, 0); panel.Children.Add(icon);
            }

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title ?? "", FontSize = 15, Foreground = Brush("TextBrush"),
                TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(subtitle))
                texts.Children.Add(new TextBlock { Text = subtitle, Style = FindStyle("SubText"), MaxLines = 2,
                    Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(texts, 1); panel.Children.Add(texts);

            var button = new Button { Content = panel, Style = FindStyle("RowButton"), Margin = new Thickness(-12, 0, -12, 0) };
            button.Click += async delegate { if (action != null) await Safe(action); };
            Body.Children.Add(button); return button;
        }

        protected void Link<T>(string title, string subtitle, string section = null, object item = null) where T : Page
        {
            Row(title, subtitle, delegate { Go<T>(section, item); return Task.CompletedTask; });
        }

        protected TextBox Field(string label, string value, bool multiline = false)
        {
            var box = new TextBox { Header = label, Text = value ?? "", TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = multiline, MinHeight = multiline ? 112 : 44, Margin = new Thickness(0, 8, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch, Style = FindStyle("FieldBox") };
            box.TextChanged += delegate { _dirty = true; };
            Body.Children.Add(box); return box;
        }

        protected PasswordBox Secret(string label, string value)
        {
            var box = new PasswordBox { Header = label, Password = value ?? "", Margin = new Thickness(0, 8, 0, 8) };
            box.PasswordChanged += delegate { _dirty = true; };
            Body.Children.Add(box); return box;
        }

        protected ToggleSwitch Toggle(string label, bool value)
        {
            return Toggle(label, value, null);
        }

        /// <summary>开关：标题在上、说明紧跟标题、开关在下（W10M 设置页形态）。说明不再飘在开关下面。</summary>
        protected ToggleSwitch Toggle(string label, bool value, string description)
        {
            object header = label;
            if (!string.IsNullOrEmpty(description))
            {
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = label, FontSize = 15, Foreground = Brush("TextBrush"), TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(new TextBlock { Text = description, Style = FindStyle("SubText"), Margin = new Thickness(0, 2, 0, 0) });
                header = stack;
            }
            var toggle = new ToggleSwitch { Header = header, IsOn = value, OnContent = "开", OffContent = "关",
                Margin = new Thickness(0, 10, 0, 10) };
            toggle.Toggled += delegate { _dirty = true; };
            Body.Children.Add(toggle); return toggle;
        }

        protected CheckBox Check(string label, bool value)
        {
            var box = new CheckBox { Content = label, IsChecked = value, MinHeight = 44, Margin = new Thickness(0, 6, 0, 6) };
            box.Checked += delegate { _dirty = true; };
            box.Unchecked += delegate { _dirty = true; };
            Body.Children.Add(box); return box;
        }

        protected ComboBox Choice(string label, string[] values, string selected)
        {
            var box = new ComboBox { Header = label, ItemsSource = values, HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 8), SelectedItem = selected };
            if (box.SelectedIndex < 0 && values != null && values.Length > 0) box.SelectedIndex = 0;
            box.SelectionChanged += delegate { _dirty = true; };
            Body.Children.Add(box); return box;
        }

        /// <summary>
        /// 下拉选择，界面显示中文、存储用代码值。避免把 "dark" / "send" 这种内部值直接给用户看。
        /// options 是 { 值, 显示名 } 成对的数组。返回取当前值的委托。
        /// </summary>
        protected Func<string> Pick(string label, string[,] options, string selected, out ComboBox box)
        {
            int count = options.GetLength(0);
            var labels = new string[count];
            int index = 0;
            for (int i = 0; i < count; i++) { labels[i] = options[i, 1]; if (options[i, 0] == selected) index = i; }
            box = Choice(label, labels, labels[index]);
            box.SelectedIndex = index;
            var captured = box;
            return delegate { return options[Math.Max(0, captured.SelectedIndex), 0]; };
        }

        protected Func<string> Pick(string label, string[,] options, string selected)
        {
            ComboBox ignored;
            return Pick(label, options, selected, out ignored);
        }

        /// <summary>单选项：标题 + 一行说明。同一 group 内互斥。</summary>
        protected RadioButton Radio(string group, string title, string description, bool isChecked)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontSize = 15, Foreground = Brush("TextBrush"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(description))
                content.Children.Add(new TextBlock { Text = description, Style = FindStyle("SubText"), Margin = new Thickness(0, 2, 0, 0) });
            var radio = new RadioButton { GroupName = group, Content = content, IsChecked = isChecked,
                Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
            radio.Checked += delegate { _dirty = true; };
            Body.Children.Add(radio); return radio;
        }

        /// <summary>和 Section 一样，但返回标签本身，便于整组显隐。</summary>
        protected TextBlock SectionLabel(string name)
        {
            var label = new TextBlock { Text = name, Style = FindStyle("SectionLabel"), Margin = new Thickness(0, 18, 0, 6) };
            Body.Children.Add(label); return label;
        }

        protected void RunOnUi(Action action)
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate { action(); });
        }

        protected Slider Range(string label, double value, double min, double max, double step = 1)
        {
            // 设计稿 .sld：标题在左、当前值（等宽、SoftText）在右，同一行
            var head = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = label, FontSize = 15, Foreground = Brush("TextBrush"), TextWrapping = TextWrapping.Wrap });
            var number = new TextBlock { Text = value.ToString("0.##", CultureInfo.InvariantCulture), FontSize = 15,
                FontFamily = new FontFamily("Consolas"), Foreground = Brush("SoftTextBrush"), Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(number, 1); head.Children.Add(number);
            Body.Children.Add(head);
            var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value };
            slider.ValueChanged += delegate { _dirty = true; number.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture); };
            Body.Children.Add(slider); return slider;
        }

        protected async Task<bool> Confirm(string title, string detail, string action = "确认")
        {
            return await new ContentDialog { Title = title, Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action, CloseButtonText = "取消", RequestedTheme = RequestedTheme }.ShowAsync()
                == ContentDialogResult.Primary;
        }

        protected void RequireIdle()
        {
            if (Context.IsRunning != null && Context.IsRunning()) throw new InvalidOperationException("会话正在生成，请先停止或等本轮完成。");
        }

        /// <summary>
        /// 取全局样式。必须走 TryGetValue —— 见 UI-HANDOFF §5：
        /// Page.Resources 索引器不会往上找父级字典，直接索引会抛。
        /// 拿不到就返回 null，让控件回落到默认外观，不要在这里抛。
        /// </summary>
        protected static Style FindStyle(string key)
        {
            object value;
            return Application.Current.Resources.TryGetValue(key, out value) ? value as Style : null;
        }

        protected static Brush Brush(string key)
        {
            object value;
            return Application.Current.Resources.TryGetValue(key, out value) ? value as Brush : null;
        }

        protected static T JsonObject<T>(TextBox box) where T : class
        {
            Newtonsoft.Json.Linq.JObject.Parse(box.Text);
            var result = JsonBridge.Deserialize<T>(box.Text);
            if (result == null) throw new ArgumentException(box.Header + "必须是 JSON 对象。");
            return result;
        }

        protected static int Int(TextBox box, int min, int max)
        {
            int value;
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
                throw new ArgumentException(box.Header + "应在 " + min + "–" + max + " 之间。");
            return value;
        }
        protected static string Required(TextBox box)
        {
            if (string.IsNullOrWhiteSpace(box.Text)) throw new ArgumentException(box.Header + "不能为空。");
            return box.Text.Trim();
        }
        protected static string[] Words(string text)
        {
            return (text ?? "").Split(new[] { ',', '，', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
