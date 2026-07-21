using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonitoringXS.App.ViewModels;

namespace MonitoringXS.App;

public sealed class ApplicationListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ApplicationTemplate { get; set; }

    public DataTemplate? SectionTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        ApplicationCardViewModel => ApplicationTemplate,
        ApplicationSectionViewModel => SectionTemplate,
        _ => base.SelectTemplateCore(item)
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
