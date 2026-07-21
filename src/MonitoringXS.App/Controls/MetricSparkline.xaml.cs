using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MonitoringXS.App.Controls;

public sealed partial class MetricSparkline : UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IList<double?>),
        typeof(MetricSparkline),
        new PropertyMetadata(null, OnSamplesChanged));

    private INotifyCollectionChanged? _observableSamples;
    private bool _resizeRedrawPending;

    public MetricSparkline()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public IList<double?>? Samples
    {
        get => (IList<double?>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private static void OnSamplesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        MetricSparkline chart = (MetricSparkline)sender;
        chart.Unsubscribe();
        chart._observableSamples = args.NewValue as INotifyCollectionChanged;
        if (chart._observableSamples is not null)
        {
            chart._observableSamples.CollectionChanged += chart.OnCollectionChanged;
        }

        chart.Redraw();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Redraw();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_observableSamples is null && Samples is INotifyCollectionChanged observable)
        {
            _observableSamples = observable;
            _observableSamples.CollectionChanged += OnCollectionChanged;
        }

        Redraw();
    }

    private void ChartRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_resizeRedrawPending)
        {
            return;
        }

        _resizeRedrawPending = DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                _resizeRedrawPending = false;
                if (IsLoaded)
                {
                    Redraw();
                }
            });
    }

    private void Redraw()
    {
        double[] values = Samples?.Where(value => value.HasValue).Select(value => value!.Value).ToArray() ?? [];
        bool hasSeries = values.Length >= 2 && ChartRoot.ActualWidth > 24 && ChartRoot.ActualHeight > 48;
        EmptyState.Visibility = hasSeries ? Visibility.Collapsed : Visibility.Visible;
        Line.Visibility = hasSeries ? Visibility.Visible : Visibility.Collapsed;
        Line.Points.Clear();

        if (!hasSeries)
        {
            Summary.Text = "CPU history is warming up. Unavailable samples are not drawn as zero.";
            return;
        }

        double width = Math.Max(1, ChartRoot.ActualWidth - 32);
        double height = Math.Max(1, ChartRoot.ActualHeight - 52);
        double peak = Math.Max(1, values.Max());
        for (int index = 0; index < values.Length; index++)
        {
            double x = 16 + width * index / Math.Max(1, values.Length - 1);
            double y = 12 + height * (1 - values[index] / peak);
            Line.Points.Add(new Point(x, y));
        }

        Summary.Text = $"Last {values.Length} real samples · peak {peak:0.0}% of total CPU capacity.";
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => Unsubscribe();

    private void Unsubscribe()
    {
        if (_observableSamples is not null)
        {
            _observableSamples.CollectionChanged -= OnCollectionChanged;
            _observableSamples = null;
        }
    }
}
