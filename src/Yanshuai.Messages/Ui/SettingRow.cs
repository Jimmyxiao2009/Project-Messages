using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Yanshuai.Messages.Ui
{
    /// <summary>
    /// 设置 / 导航列表行：24px 图标 + 15 标题 + 12 副标题（写当前值），无 chevron、无分隔线。
    /// 按下反馈交给系统按钮模板（PointerDownThemeAnimation）。
    /// </summary>
    public sealed class SettingRow : Button
    {
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            "Glyph", typeof(string), typeof(SettingRow), new PropertyMetadata("", Changed));
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            "Title", typeof(string), typeof(SettingRow), new PropertyMetadata("", Changed));
        public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
            "Subtitle", typeof(string), typeof(SettingRow), new PropertyMetadata("", Changed));

        public string Glyph { get { return (string)GetValue(GlyphProperty); } set { SetValue(GlyphProperty, value); } }
        public string Title { get { return (string)GetValue(TitleProperty); } set { SetValue(TitleProperty, value); } }
        public string Subtitle { get { return (string)GetValue(SubtitleProperty); } set { SetValue(SubtitleProperty, value); } }

        private readonly TextBlock _icon = new TextBlock();
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _subtitle = new TextBlock();

        public SettingRow()
        {
            object style;
            if (Application.Current.Resources.TryGetValue("RowButton", out style)) Style = style as Style;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            _icon.FontSize = 20;
            _icon.Width = 24;
            _icon.TextAlignment = TextAlignment.Center;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            _icon.Margin = new Thickness(0, 0, 12, 0);
            _icon.Foreground = Resource("TextBrush");
            grid.Children.Add(_icon);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _title.Style = Resource<Style>("RowTitle");
            _subtitle.Style = Resource<Style>("RowSubtitle");
            texts.Children.Add(_title);
            texts.Children.Add(_subtitle);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            Content = grid;
            Refresh();
        }

        private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
        {
            ((SettingRow)obj).Refresh();
        }

        private void Refresh()
        {
            _icon.Text = Glyph ?? "";
            _icon.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
            _title.Text = Title ?? "";
            _subtitle.Text = Subtitle ?? "";
            _subtitle.Visibility = string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static Brush Resource(string key) { return Resource<Brush>(key); }

        private static T Resource<T>(string key) where T : class
        {
            object value;
            return Application.Current.Resources.TryGetValue(key, out value) ? value as T : null;
        }
    }
}
