using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Platform;

namespace Yanshuai.Messages.Ui
{
    public sealed class AttachmentStrip : UserControl
    {
        public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
            "Items", typeof(object), typeof(AttachmentStrip), new PropertyMetadata(null, Changed));
        public object Items { get { return GetValue(ItemsProperty); } set { SetValue(ItemsProperty, value); } }
        public static Action<Attachment> OpenAttachment { get; set; }
        private readonly StackPanel _panel = new StackPanel { Orientation = Orientation.Horizontal };

        public AttachmentStrip()
        {
            Content = new ScrollViewer { Content = _panel, HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            ((AttachmentStrip)sender).Populate();
        }
        private void Populate()
        {
            _panel.Children.Clear();
            var items = Items as IEnumerable<Attachment>;
            if (items == null) return;
            foreach (var item in items)
            {
                var stack = new StackPanel { MaxWidth = 120 };
                if (item.IsImage && AppServices.Current != null)
                {
                    var path = item.Path;
                    if (AppServices.Current.Chat.Profile != null && !path.StartsWith(AppServices.Current.Chat.ProfileSlug + "/", StringComparison.Ordinal))
                        path = AppServices.Current.Chat.ProfileSlug + "/" + path;
                    stack.Children.Add(new WorkspaceImage { SourcePath = path, Width = 80, Height = 64, Stretch = Stretch.UniformToFill });
                }
                stack.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12, MaxWidth = 120, Margin = new Thickness(0, 4, 0, 0) });
                var button = new Button { Content = stack, MinHeight = 44, Margin = new Thickness(0, 0, 8, 0) };
                var attachment = item;
                button.Click += delegate { if (OpenAttachment != null) OpenAttachment(attachment); };
                _panel.Children.Add(button);
            }
        }
    }
}
