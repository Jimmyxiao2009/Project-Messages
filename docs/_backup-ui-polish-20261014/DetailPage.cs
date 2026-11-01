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
        private readonly Button _headerSave = new Button { Content = "保存", Visibility = Visibility.Collapsed, MinHeight = 44 };
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
            var header = new Grid { Margin = new Thickness(12, 10, 12, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var back = new Button { Content = "\uE72B", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 44, Height = 44, Background = Brush("ChromeBrush"), Margin = new Thickness(0, 0, 12, 0) };
            back.Click += delegate { Back(); };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_headerSave, 2); header.Children.Add(_headerSave);
            _headerSave.Click += async delegate { await Safe(async delegate { await _saveAction(); _dirty = false; Finish(); }); };
            header.Children.Add(back);
            var titles = new StackPanel();
            _label.FontSize = 12; _label.Foreground = Brush("MidBrush");
            _title.FontSize = 24; _title.FontWeight = Windows.UI.Text.FontWeights.SemiLight;
            _title.TextTrimming = TextTrimming.CharacterEllipsis; _title.Foreground = Brush("TextBrush");
            titles.Children.Add(_label); titles.Children.Add(_title);
            Grid.SetColumn(titles, 1); header.Children.Add(titles); grid.Children.Add(header);
            var notices = new StackPanel { Margin = new Thickness(12, 0, 12, 4) };
            Status.FontSize = 12; Status.Foreground = Brush("WarnTextBrush"); Status.TextWrapping = TextWrapping.Wrap;
            notices.Children.Add(_busy); notices.Children.Add(Status);
            Grid.SetRow(notices, 1); grid.Children.Add(notices);
            Body.Margin = new Thickness(12, 0, 12, 24);
            var scroll = new ScrollViewer { Content = Body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
            Commands.Background = Brush("ChromeBrush"); Commands.DefaultLabelPosition = CommandBarDefaultLabelPosition.Bottom;
            Grid.SetRow(Commands, 3); grid.Children.Add(Commands);
            Content = grid;
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
            await Safe(BuildAsync);
            _dirty = false;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _departed = true;
            Lifetime.Cancel();
            Lifetime.Dispose();
            base.OnNavigatedFrom(e);
        }

        protected abstract Task BuildAsync();
        protected void Title(string title, string label)
        {
            _title.Text = title; _label.Text = label; Body.Children.Clear();
            Commands.PrimaryCommands.Clear(); Commands.SecondaryCommands.Clear(); Status.Text = "";
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
        }

        protected void Save(Func<Task> save)
        {
            _saveAction = save; _headerSave.Visibility = Visibility.Visible;
            Command("保存", Symbol.Save, async delegate { await save(); _dirty = false; Finish(); });
            Command("取消", Symbol.Cancel, delegate { Finish(); return Task.CompletedTask; });
        }

        protected void Finish()
        {
            if (Frame.CanGoBack) Frame.GoBack(); else Context.Close();
        }

        protected void Section(string name)
        {
            Body.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("MidBrush"), Margin = new Thickness(0, 20, 0, 8) });
        }

        protected TextBlock Text(string text, bool muted = false)
        {
            var block = new TextBlock { Text = text ?? "", FontSize = muted ? 12 : 15,
                Foreground = Brush(muted ? "MidBrush" : "TextBrush"), TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true, Margin = new Thickness(0, 4, 0, 8) };
            Body.Children.Add(block); return block;
        }

        protected Button Row(string title, string subtitle, Func<Task> action = null)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title ?? "", FontSize = 15, Foreground = Brush("TextBrush"),
                TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = subtitle ?? "", FontSize = 12, Foreground = Brush("MidBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            var button = new Button { Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 56,
                Padding = new Thickness(8, 10, 8, 10), Background = Brush("BgBrush"), BorderThickness = new Thickness(0) };
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
                HorizontalAlignment = HorizontalAlignment.Stretch };
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
            var toggle = new ToggleSwitch { Header = label, IsOn = value, OnContent = "开", OffContent = "关",
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
            if (box.SelectedIndex < 0) box.SelectedIndex = 0;
            box.SelectionChanged += delegate { _dirty = true; };
            Body.Children.Add(box); return box;
        }

        protected Slider Range(string label, double value, double min, double max, double step = 1)
        {
            var block = new TextBlock { Text = label + " · " + value.ToString("0.##", CultureInfo.InvariantCulture),
                FontSize = 15, Margin = new Thickness(0, 12, 0, 0) };
            Body.Children.Add(block);
            var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value };
            slider.ValueChanged += delegate { _dirty = true; block.Text = label + " · " + slider.Value.ToString("0.##", CultureInfo.InvariantCulture); };
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
