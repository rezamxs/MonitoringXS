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
        typeof(IList<CpuHistorySample>),
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

    public IList<CpuHistorySample>? Samples
    {
        get => (IList<CpuHistorySample>?)GetValue(SamplesProperty);
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
        MetricSparklineLayout layout = MetricSparklineLayout.Create(
            Samples?.ToArray() ?? [],
            ChartRoot.ActualWidth,
            ChartRoot.ActualHeight);
        bool hasSeries = layout.Segments.Count > 0;
        EmptyState.Visibility = hasSeries ? Visibility.Collapsed : Visibility.Visible;
        Line.Visibility = hasSeries ? Visibility.Visible : Visibility.Collapsed;
        PathGeometry geometry = new();

        foreach (IReadOnlyList<MetricSparklinePoint> segment in layout.Segments)
        {
            PathFigure figure = new()
            {
                StartPoint = new Point(segment[0].X, segment[0].Y)
            };
            foreach (MetricSparklinePoint point in segment.Skip(1))
            {
                figure.Segments.Add(new LineSegment
                {
                    Point = new Point(point.X, point.Y)
                });
            }

            geometry.Figures.Add(figure);
        }

        Line.Data = geometry;

        Summary.Text = layout.Summary;
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
