using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using Windows.Foundation;
using WToolTipService = Microsoft.UI.Xaml.Controls.ToolTipService;

namespace MonitoringXS.App.Controls;

public sealed partial class MetricSparkline : UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IList<CpuHistorySample>),
        typeof(MetricSparkline),
        new PropertyMetadata(null, OnSamplesChanged));

    public static readonly DependencyProperty SummaryTextProperty = DependencyProperty.Register(
        nameof(SummaryText),
        typeof(string),
        typeof(MetricSparkline),
        new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty ShowSummaryProperty = DependencyProperty.Register(
        nameof(ShowSummary),
        typeof(bool),
        typeof(MetricSparkline),
        new PropertyMetadata(true, OnTextChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText),
        typeof(string),
        typeof(MetricSparkline),
        new PropertyMetadata("Waiting for real samples…", OnTextChanged));

    public static readonly DependencyProperty ChartScaleProperty = DependencyProperty.Register(
        nameof(ChartScale),
        typeof(MetricSparklineScale),
        typeof(MetricSparkline),
        new PropertyMetadata(MetricSparklineScale.Percent, OnLayoutChanged));

    public static readonly DependencyProperty RangeStartUtcProperty = DependencyProperty.Register(
        nameof(RangeStartUtc),
        typeof(DateTimeOffset?),
        typeof(MetricSparkline),
        new PropertyMetadata(null, OnLayoutChanged));

    public static readonly DependencyProperty RangeEndUtcProperty = DependencyProperty.Register(
        nameof(RangeEndUtc),
        typeof(DateTimeOffset?),
        typeof(MetricSparkline),
        new PropertyMetadata(null, OnLayoutChanged));

    public static readonly DependencyProperty UnitTextProperty = DependencyProperty.Register(
        nameof(UnitText),
        typeof(string),
        typeof(MetricSparkline),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty IsEmbeddedProperty = DependencyProperty.Register(
        nameof(IsEmbedded),
        typeof(bool),
        typeof(MetricSparkline),
        new PropertyMetadata(false, OnEmbeddedChanged));

    public static readonly DependencyProperty TooltipMetricNameProperty = DependencyProperty.Register(
        nameof(TooltipMetricName),
        typeof(string),
        typeof(MetricSparkline),
        new PropertyMetadata(null));

    public static readonly DependencyProperty TooltipValueUnitProperty = DependencyProperty.Register(
        nameof(TooltipValueUnit),
        typeof(string),
        typeof(MetricSparkline),
        new PropertyMetadata(null));

    public static readonly DependencyProperty TooltipUsesPercentUnitProperty = DependencyProperty.Register(
        nameof(TooltipUsesPercentUnit),
        typeof(bool),
        typeof(MetricSparkline),
        new PropertyMetadata(false));

    private INotifyCollectionChanged? _observableSamples;
    private bool _redrawPending;
    private readonly Brush? _defaultBackground;

    public MetricSparkline()
    {
        InitializeComponent();
        _defaultBackground = ChartRoot.Background;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

    public IList<CpuHistorySample>? Samples
    {
        get => (IList<CpuHistorySample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public string? SummaryText
    {
        get => (string?)GetValue(SummaryTextProperty);
        set => SetValue(SummaryTextProperty, value);
    }

    public bool ShowSummary
    {
        get => (bool)GetValue(ShowSummaryProperty);
        set => SetValue(ShowSummaryProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public MetricSparklineScale ChartScale
    {
        get => (MetricSparklineScale)GetValue(ChartScaleProperty);
        set => SetValue(ChartScaleProperty, value);
    }

    public DateTimeOffset? RangeStartUtc
    {
        get => (DateTimeOffset?)GetValue(RangeStartUtcProperty);
        set => SetValue(RangeStartUtcProperty, value);
    }

    public DateTimeOffset? RangeEndUtc
    {
        get => (DateTimeOffset?)GetValue(RangeEndUtcProperty);
        set => SetValue(RangeEndUtcProperty, value);
    }

    public string UnitText
    {
        get => (string)GetValue(UnitTextProperty);
        set => SetValue(UnitTextProperty, value);
    }

    public bool IsEmbedded
    {
        get => (bool)GetValue(IsEmbeddedProperty);
        set => SetValue(IsEmbeddedProperty, value);
    }

    public string? TooltipMetricName
    {
        get => (string?)GetValue(TooltipMetricNameProperty);
        set => SetValue(TooltipMetricNameProperty, value);
    }

    public string? TooltipValueUnit
    {
        get => (string?)GetValue(TooltipValueUnitProperty);
        set => SetValue(TooltipValueUnitProperty, value);
    }

    public bool TooltipUsesPercentUnit
    {
        get => (bool)GetValue(TooltipUsesPercentUnitProperty);
        set => SetValue(TooltipUsesPercentUnitProperty, value);
    }

    private static void OnEmbeddedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        MetricSparkline chart = (MetricSparkline)sender;
        chart.ApplyEmbeddedState();
        chart.QueueRedraw();
    }

    private void ApplyEmbeddedState()
    {
        if (IsEmbedded)
        {
            ChartRoot.Background = null;
            ChartRoot.BorderThickness = new Thickness(0);
            ChartRoot.CornerRadius = new CornerRadius(0);
            Summary.Visibility = Visibility.Collapsed;
            TopAxisLabel.Visibility = Visibility.Collapsed;
            BottomAxisLabel.Visibility = Visibility.Collapsed;
            StartAxisLabel.Visibility = Visibility.Collapsed;
            EndAxisLabel.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            MidGridLine.Visibility = Visibility.Collapsed;
            BaselineGridLine.Opacity = 0.35;
            MinHeight = 44;
        }
        else
        {
            ChartRoot.Background = _defaultBackground;
            ChartRoot.BorderThickness = new Thickness(1);
            ChartRoot.CornerRadius = new CornerRadius(8);
            TopAxisLabel.Visibility = Visibility.Visible;
            BottomAxisLabel.Visibility = Visibility.Visible;
            StartAxisLabel.Visibility = Visibility.Visible;
            EndAxisLabel.Visibility = Visibility.Visible;
            MidGridLine.Visibility = Visibility.Visible;
            BaselineGridLine.Opacity = 0.55;
            Summary.Visibility = ShowSummary ? Visibility.Visible : Visibility.Collapsed;
            MinHeight = 120;
        }
    }

    private static void OnSamplesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        MetricSparkline chart = (MetricSparkline)sender;
        chart.Unsubscribe();
        if (chart.IsLoaded && args.NewValue is INotifyCollectionChanged observable)
        {
            chart._observableSamples = observable;
            chart._observableSamples.CollectionChanged += chart.OnCollectionChanged;
        }

        chart.QueueRedraw();
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MetricSparkline)sender).QueueRedraw();

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MetricSparkline)sender).QueueRedraw();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRedraw();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_observableSamples is null && Samples is INotifyCollectionChanged observable)
        {
            _observableSamples = observable;
            _observableSamples.CollectionChanged += OnCollectionChanged;
        }

        ApplyEmbeddedState();
        Redraw();
    }

    private void ChartRoot_SizeChanged(object sender, SizeChangedEventArgs args)
        => QueueRedraw();

    private void QueueRedraw()
    {
        if (_redrawPending || !IsLoaded)
        {
            return;
        }

        _redrawPending = DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                _redrawPending = false;
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
            PlotArea.ActualWidth,
            PlotArea.ActualHeight,
            ChartScale,
            RangeStartUtc,
            RangeEndUtc,
            IsEmbedded);
        bool hasSeries = layout.Segments.Count > 0 || layout.Markers.Count > 0;
        EmptyState.Text = EmptyText;
        EmptyState.Visibility = IsEmbedded
            ? Visibility.Collapsed
            : (hasSeries ? Visibility.Collapsed : Visibility.Visible);
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
        Rect plotClip = new(
            layout.PlotLeft,
            layout.PlotTop,
            Math.Max(0, layout.PlotRight - layout.PlotLeft),
            Math.Max(0, layout.PlotBottom - layout.PlotTop));
        Line.Clip = new RectangleGeometry { Rect = plotClip };
        Markers.Clip = new RectangleGeometry { Rect = plotClip };
        Markers.Children.Clear();
        foreach (MetricSparklinePoint marker in layout.Markers)
        {
            Ellipse ellipse = new()
            {
                Width = 6,
                Height = 6,
                Fill = Line.Stroke
            };
            Canvas.SetLeft(ellipse, marker.X - 3);
            Canvas.SetTop(ellipse, marker.Y - 3);
            Markers.Children.Add(ellipse);
        }

        if (!IsEmbedded)
        {
            TopAxisLabel.Text = FormatAxis(layout.DomainMaximum);
            BottomAxisLabel.Text = FormatAxis(layout.DomainMinimum);
            StartAxisLabel.Text = layout.RangeStartUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            EndAxisLabel.Text = layout.RangeEndUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            Summary.Text = string.IsNullOrWhiteSpace(SummaryText) ? layout.Summary : SummaryText;
            Summary.Visibility = ShowSummary ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string FormatAxis(double value) =>
        ChartScale == MetricSparklineScale.Percent
            ? $"{value:0}%"
            : UnitText switch
            {
                "bytes/s" => $"{FormatBytes(value)}/s",
                "bytes" => FormatBytes(value),
                _ => $"{FormatBytes(value)} {UnitText}".Trim()
            };

    private static string FormatBytes(double value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        value = Math.Max(0, value);
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
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

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) => UpdateHover(args);

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _hoverIndex = -1;
        WToolTipService.SetToolTip(ChartRoot, null);
    }

    private void UpdateHover(PointerRoutedEventArgs args)
    {
        if (Samples is not { Count: > 0 } samples
            || TooltipMetricName is not { Length: > 0 } metricName)
        {
            _hoverIndex = -1;
            WToolTipService.SetToolTip(ChartRoot, null);
            return;
        }

        Point position = args.GetCurrentPoint(ChartRoot).Position;
        int index = NearestIndex(position.X);
        if (index < 0 || index >= samples.Count || index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        string? tooltip = ChartTooltipBuilder.Build(
            samples.ToArray(),
            index,
            metricName,
            TooltipUsesPercentUnit ? HistoryValueKind.Percent : HistoryValueKind.Bytes,
            TooltipValueUnit ?? string.Empty,
            _localization.Get(LocalizationKeys.Available),
            _localization.Get(LocalizationKeys.PartialLowerBound),
            _localization.Get(LocalizationKeys.Unavailable),
            _localization.Get(LocalizationKeys.TooltipReason),
            _localization.Get(LocalizationKeys.TooltipValue));
        if (string.IsNullOrEmpty(tooltip))
        {
            _hoverIndex = -1;
            WToolTipService.SetToolTip(ChartRoot, null);
            return;
        }

        TooltipText.Text = tooltip;
        WToolTipService.SetToolTip(ChartRoot, TooltipText);
    }

    private int NearestIndex(double x)
    {
        IList<CpuHistorySample>? samples = Samples;
        if (samples is null || samples.Count == 0)
        {
            return -1;
        }

        return ChartHoverMapper.NearestIndex(
            x,
            samples,
            RangeStartUtc,
            RangeEndUtc,
            IsEmbedded,
            PlotArea.ActualWidth);
    }

    private int _hoverIndex = -1;
    private readonly LocalizationService _localization = new();
}
