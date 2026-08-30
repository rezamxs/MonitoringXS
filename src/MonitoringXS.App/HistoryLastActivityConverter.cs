using Microsoft.UI.Xaml.Data;
using MonitoringXS.App.Localization;

namespace MonitoringXS.App;

public sealed class HistoryLastActivityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset updatedUtc)
        {
            return string.Empty;
        }

        LocalizationService localization = ((App)Microsoft.UI.Xaml.Application.Current).Localization;
        return localization.Format(
            LocalizationKeys.HistoryLastUpdatedFormat,
            updatedUtc.ToLocalTime().ToString("g", localization.Culture));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
