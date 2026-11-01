// Ui/TimelineTemplateSelector.cs —— 时间线行模板分派
//
// 行类型（Core.RowKind）→ DataTemplate，一一对应，不做"一个模板靠 Visibility 切换"
// 那种写法（旧项目就是这么干的，模板里塞了六个形态，改一个动全身）。

using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui
{
    public sealed class TimelineTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UserTemplate { get; set; }
        public DataTemplate ProcessTemplate { get; set; }
        public DataTemplate OutputTemplate { get; set; }
        public DataTemplate MarkerTemplate { get; set; }
        public DataTemplate ErrorTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            var row = item as RowVm;
            if (row == null) return MarkerTemplate;

            switch (row.Kind)
            {
                case RowKind.User: return UserTemplate;
                case RowKind.Process: return ProcessTemplate;
                case RowKind.Output: return OutputTemplate;
                case RowKind.Error: return ErrorTemplate;
                default: return MarkerTemplate;
            }
        }
    }
}
