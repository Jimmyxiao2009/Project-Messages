using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Yanshuai.Messages.Platform;

namespace Yanshuai.Messages.Ui
{
    // Decode from the brokered file store, including workspaces on an authorized SD/document folder.
    public sealed class WorkspaceImage : UserControl
    {
        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
            "SourcePath", typeof(string), typeof(WorkspaceImage), new PropertyMetadata("", Changed));
        public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
            "Stretch", typeof(Stretch), typeof(WorkspaceImage), new PropertyMetadata(Stretch.Uniform, Changed));
        public static readonly DependencyProperty CircularProperty = DependencyProperty.Register("Circular", typeof(bool), typeof(WorkspaceImage), new PropertyMetadata(false, Changed));
        public bool Circular { get { return (bool)GetValue(CircularProperty); } set { SetValue(CircularProperty, value); } }
        public string SourcePath { get { return (string)GetValue(SourcePathProperty); } set { SetValue(SourcePathProperty, value); } }
        public Stretch Stretch { get { return (Stretch)GetValue(StretchProperty); } set { SetValue(StretchProperty, value); } }
        private readonly Image _image = new Image();
        private readonly Windows.UI.Xaml.Shapes.Ellipse _circle = new Windows.UI.Xaml.Shapes.Ellipse();
        private int _generation;
        private bool _loaded;
        public WorkspaceImage()
        {
            Content = _image;
            Loaded += delegate { _loaded = true; Load(); };
            Unloaded += delegate { _loaded = false; _generation++; _image.Source = null; _circle.Fill = null; };
        }
        private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
        {
            var control = (WorkspaceImage)obj;
            control._image.Stretch = control.Stretch;
            control.Content = control.Circular ? (UIElement)control._circle : control._image;
            if (control._loaded) control.Load();
        }
        private async void Load()
        {
            int generation = ++_generation;
            string path = SourcePath;
            _image.Source = null; _circle.Fill = null;
            if (string.IsNullOrWhiteSpace(path) || AppServices.Current == null) return;
            try {
                var bytes = await AppServices.Current.Files.ReadBytesAsync(path);
                if (generation != _generation || !_loaded) return;
                var bitmap = new BitmapImage { DecodePixelWidth = (int)Math.Min(1536, Math.Max(128, ActualWidth)) };
                using (var stream = await ImagePreparation.StreamAsync(bytes)) await bitmap.SetSourceAsync(stream);
                if (generation == _generation && _loaded) { if (Circular) _circle.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill }; else _image.Source = bitmap; }
            } catch (Exception ex) { App.WriteCrashLog("IMAGE", ex); }
        }
    }
}
