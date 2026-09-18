using System.Drawing.Drawing2D;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;

namespace OBDim.UI.Memory;

using static UiPalette;

/// <summary>
/// The memory page's single trend chart: one series at a time, selected by metric and range,
/// drawn from geometry that <see cref="MemoryChartGeometry"/> produced from the raw samples.
/// <para>
/// Paint only consumes prepared geometry. The previous implementation re-filtered, re-sampled and
/// re-min/maxed the whole history inside OnPaint for two series, so exposing, resizing or hovering
/// each paid for the full pipeline again — and the reduction it did there is what fragmented the
/// line. A cache keyed on the snapshot version, the selection, the viewport, the DPI and the
/// second the window ends in makes those repaints a lookup.
/// </para>
/// </summary>
internal sealed class MemoryTrendPanel : Control
{
    private const int GutterLeft = 54;
    private const int GutterRight = 10;
    private const int HeaderHeight = 22;
    private const int FooterHeight = 16;
    private const int TooltipPadding = 6;

    private readonly List<MemoryTrendPoint> _navigationPoints = [];

    private MemoryHistorySnapshot _snapshot = new(0, Array.Empty<MemorySample>());
    private MemoryChartGeometry? _geometry;

    /// <summary>
    /// Counts geometry rebuilds. Painting must consume the cache rather than re-run the pipeline,
    /// and "it looks smooth" is not evidence of that — this is.
    /// </summary>
    internal long GeometryBuildCount { get; private set; }

    /// <summary>True while the crosshair is showing a keyboard- or pointer-selected reading.</summary>
    internal bool HasReading => _hover is not null || _cursor >= 0;
    private long _geometryStamp = -1;
    private MemoryTrendMetric _geometryMetric;
    private MemoryTrendRange _geometryRange;
    private Size _geometrySize;
    private int _geometryDpi;
    private long _geometrySecond;

    private Pen? _linePen;
    private Pen? _stalePen;
    private Pen? _gridPen;
    private Pen? _cursorPen;
    private LinearGradientBrush? _fillBrush;
    private RectangleF _fillBounds;
    private Font? _axisFont;
    private Font? _titleFont;

    private int _cursor = -1;
    private MemoryTrendPoint? _hover;
    private bool _hoverInGap;

    public MemoryTrendPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        TabStop = true;
        // AccessibleRole.Chart is the closest native role; the name carries the reading itself so
        // a screen reader announces the current figure rather than "chart, blank".
        AccessibleRole = AccessibleRole.Chart;
    }

    /// <summary>Which series is drawn. Setting it repaints from the cached history; nothing is re-read.</summary>
    public MemoryTrendMetric Metric
    {
        get => _metric;
        set
        {
            if (_metric == value) return;
            _metric = value;
            ClearReadingSelection();
            SelectionChanged?.Invoke();
            Invalidate();
        }
    }

    private MemoryTrendMetric _metric = MemoryTrendMetric.PhysicalUsed;

    public MemoryTrendRange Range
    {
        get => _range;
        set
        {
            if (_range == value) return;
            _range = value;
            ClearReadingSelection();
            SelectionChanged?.Invoke();
            Invalidate();
        }
    }

    private MemoryTrendRange _range = MemoryTrendRange.Hour1;

    /// <summary>Raised when the user changes metric or range, so the page can follow the selection.</summary>
    public event Action? SelectionChanged;

    public string Title { get; set; } = "";

    public string EmptyText { get; set; } = "";

    public string NoSamplesText { get; set; } = "";

    /// <summary>True when the newest sample is older than the freshness bound: grey the series out.</summary>
    public bool Stale { get; private set; }

    /// <summary>
    /// Loads a history snapshot. Same version means the data cannot have changed, so the repaint is
    /// skipped outright — this is what keeps the 5-second sampler from redrawing an identical curve.
    /// </summary>
    public void Apply(MemoryHistorySnapshot snapshot, bool stale)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version == _snapshot.Version && stale == Stale) return;
        _snapshot = snapshot;
        Stale = stale;
        Invalidate();
    }

    /// <summary>
    /// The newest sample the selected window can draw. Runs are chronological, so the last point of
    /// the flattened navigation list is the newest overall.
    /// </summary>
    private MemoryTrendPoint? NewestPoint() => _navigationPoints.Count > 0 ? _navigationPoints[^1] : null;

    /// <summary>
    /// The value the headline shows: the newest drawable point for the selected metric.
    /// </summary>
    public MemoryTrendPoint? CurrentPoint
    {
        get
        {
            EnsureGeometry();
            return NewestPoint();
        }
    }

    /// <summary>Forces the next paint to rebuild geometry (localization or palette change).</summary>
    public void RebuildGeometry()
    {
        _geometryStamp = -1;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var geometry = EnsureGeometry();
        var handled = true;

        switch (e.KeyCode)
        {
            case Keys.Left when _navigationPoints.Count > 0:
                _cursor = Math.Max(0, (_cursor < 0 ? _navigationPoints.Count - 1 : _cursor) - 1);
                break;
            case Keys.Right when _navigationPoints.Count > 0:
                _cursor = Math.Min(_navigationPoints.Count - 1, (_cursor < 0 ? -1 : _cursor) + 1);
                break;
            case Keys.Home when _navigationPoints.Count > 0:
                _cursor = 0;
                break;
            case Keys.End when _navigationPoints.Count > 0:
                _cursor = _navigationPoints.Count - 1;
                break;
            case Keys.Escape:
                ClearHover();
                _cursor = -1;
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _hover = _cursor >= 0 ? _navigationPoints[_cursor] : null;
            _hoverInGap = false;
            InvalidatePlot();
            UpdateAccessibleDescription();
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// The chart's own arrow keys must reach it. Without this the form's ProcessCmdKey consumes
    /// Left/Right as "switch page", and the keyboard path over the series is unreachable.
    /// Escape belongs to the chart only while a reading is on screen; the next one has to fall
    /// through to the form so the popover can still be dismissed.
    /// </summary>
    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End => true,
        Keys.Escape => _hover is not null || _cursor >= 0,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_cursor < 0 && _navigationPoints.Count > 0) _cursor = _navigationPoints.Count - 1;
        InvalidatePlot();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _cursor = -1;
        InvalidatePlot();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var geometry = EnsureGeometry();
        if (geometry.IsEmpty) { ClearHover(); return; }

        var plot = PlotBounds;
        if (e.X < plot.Left || e.X > plot.Right || e.Y < plot.Top || e.Y > plot.Bottom)
        {
            ClearHover();
            return;
        }

        var at = geometry.WindowStart.AddSeconds(
            (double)(e.X - plot.Left) / Math.Max(1, plot.Width) * geometry.WindowEnd.Subtract(geometry.WindowStart).TotalSeconds);
        var hit = MemoryChartGeometry.PointAt(geometry, at);

        // A reading is only shown when the pointer is actually on the sampled band; anywhere else
        // this is "no samples in this period", never the nearest point from the far side of a gap.
        var inGap = hit is null;
        if (hit is not null)
        {
            var hitX = PlotX(geometry, hit.Value);
            inGap = Math.Abs(hitX - e.X) > Math.Max(6f, PointSpacing(geometry));
        }

        if (Equals(hit, _hover) && inGap == _hoverInGap) return;

        _hover = hit;
        _hoverInGap = inGap;
        _cursor = -1;
        InvalidatePlot();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    private void ClearHover()
    {
        if (_hover is null && !_hoverInGap) return;
        _hover = null;
        _hoverInGap = false;
        InvalidatePlot();
    }

    private void ClearReadingSelection()
    {
        _cursor = -1;
        _hover = null;
        _hoverInGap = false;
    }

    private Rectangle PlotBounds => new(
        S(GutterLeft), S(HeaderHeight),
        Math.Max(1, ClientSize.Width - S(GutterLeft + GutterRight)),
        Math.Max(1, ClientSize.Height - S(HeaderHeight + FooterHeight)));

    private int S(int logical) => (int)Math.Round(logical * (DeviceDpi / 96.0));

    private static double PlotX(MemoryChartGeometry geometry, MemoryTrendPoint point) =>
        geometry.WindowStart == geometry.WindowEnd
            ? 0
            : (point.AtUtc - geometry.WindowStart).TotalSeconds /
              geometry.WindowEnd.Subtract(geometry.WindowStart).TotalSeconds * geometry.PlotWidth;

    /// <summary>
    /// Rebuilds geometry only when an input actually moved. The window end is bucketed to a second
    /// because the axis slides continuously: without the bucket, every hover repaint would also
    /// re-run the whole pipeline.
    /// </summary>
    private MemoryChartGeometry EnsureGeometry()
    {
        var plot = PlotBounds;
        var nowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cacheValid = _geometry is not null
            && _geometryStamp == _snapshot.Version
            && _geometryMetric == _metric
            && _geometryRange == _range
            && _geometrySize == plot.Size
            && _geometryDpi == DeviceDpi
            && _geometrySecond == nowSecond;

        if (cacheValid) return _geometry!;

        var geometry = MemoryChartGeometry.Build(
            _snapshot.Samples, _metric, DateTimeOffset.UtcNow, _range, plot.Width, plot.Height);

        _geometry = geometry;
        GeometryBuildCount++;
        _geometryStamp = _snapshot.Version;
        _geometryMetric = _metric;
        _geometryRange = _range;
        _geometrySize = plot.Size;
        _geometryDpi = DeviceDpi;
        _geometrySecond = nowSecond;

        _navigationPoints.Clear();
        foreach (var run in geometry.RawRuns) _navigationPoints.AddRange(run);
        if (_cursor >= _navigationPoints.Count) _cursor = _navigationPoints.Count - 1;
        UpdateAccessibleDescription();
        return geometry;
    }

    private double PointSpacing(MemoryChartGeometry geometry) =>
        _navigationPoints.Count < 2
            ? 0
            : Math.Abs(PlotX(geometry, _navigationPoints[^1]) - PlotX(geometry, _navigationPoints[0])) / (_navigationPoints.Count - 1);

    private void InvalidatePlot()
    {
        var plot = PlotBounds;
        Invalidate(new Rectangle(plot.Left - S(4), plot.Top - S(2), plot.Width + S(8), plot.Height + S(20)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);

        var geometry = EnsureGeometry();
        var plot = PlotBounds;

        _titleFont ??= new Font(Font, FontStyle.Bold);
        _axisFont ??= new Font(Font.FontFamily, Math.Max(6.5f, Font.SizeInPoints * 0.85f));
        g.DrawString(Title, _titleFont, SystemBrushes.ControlText, S(2), S(2));

        if (geometry.IsEmpty)
        {
            g.DrawString(EmptyText, Font, SystemBrushes.ControlText, plot.Left, plot.Top + S(8));
            return;
        }

        DrawFrame(g, geometry, plot);
        DrawSeries(g, geometry, plot);
        DrawMarkers(g, geometry, plot);
        DrawFooter(g, geometry, plot);
        DrawReading(g, geometry, plot);
    }

    private void DrawFrame(Graphics g, MemoryChartGeometry geometry, Rectangle plot)
    {
        _gridPen ??= new Pen(Hairline) { DashStyle = DashStyle.Dot };
        using var border = new Pen(CardBorder);
        g.DrawRectangle(border, plot);

        // The axis runs 0 → capacity, so the dotted midline is half the capacity: the cue that a
        // curve sitting low is low usage, not a clipped local range.
        var midY = plot.Top + plot.Height / 2;
        g.DrawLine(_gridPen, plot.Left, midY, plot.Right, midY);

        var axis = _axisFont ?? Font;
        g.DrawString(MonitorForm.FormatBytes((ulong)geometry.AxisCapacity), axis,
            SystemBrushes.ControlText, S(2), plot.Top - S(1));
        g.DrawString("0", axis, SystemBrushes.ControlText, S(2), plot.Bottom - S(13));
        g.DrawString(SeriesLabel, axis, SystemBrushes.ControlText, plot.Left + S(4), plot.Top + S(2));
    }

    private string SeriesLabel => OBDim.Services.LocalizationService.Get(
        _metric == MemoryTrendMetric.PhysicalUsed ? "monitor.metric_physical" : "monitor.metric_commit");

    private void DrawSeries(Graphics g, MemoryChartGeometry geometry, Rectangle plot)
    {
        if (geometry.Series.Count == 0) return;

        if (Stale)
        {
            _stalePen ??= new Pen(StaleSeries, 1.6f);
        }
        _linePen ??= new Pen(Accent, 1.6f);
        var linePen = Stale ? _stalePen! : _linePen;

        foreach (var series in geometry.Series)
        {
            var pts = ToDevice(series.Line, plot);
            if (pts.Length < 2) continue;

            // Fill first, then the OPEN polyline. Stroking the closed polygon would draw the two
            // closing vertical edges, which is exactly the thin-bar artifact being removed.
            if (EnsureFillBrush(plot))
            {
                g.FillPolygon(_fillBrush!, ToDevice(series.Area, plot));
            }

            g.DrawLines(linePen, pts);
        }
    }

    private bool EnsureFillBrush(Rectangle plot)
    {
        var bounds = new RectangleF(plot.Left, plot.Top, plot.Width, plot.Height);
        if (_fillBrush is not null && _fillBounds == bounds) return true;

        _fillBrush?.Dispose();
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            _fillBrush = null;
            return false;
        }

        _fillBrush = new LinearGradientBrush(bounds, Color.FromArgb(40, Accent), Color.FromArgb(9, Accent),
            LinearGradientMode.Vertical);
        _fillBounds = bounds;
        return true;
    }

    private void DrawMarkers(Graphics g, MemoryChartGeometry geometry, Rectangle plot)
    {
        using var dot = new SolidBrush(Stale ? StaleSeries : Accent);
        var radius = S(3);

        foreach (var isolated in geometry.IsolatedPoints)
        {
            var p = ToDevice(isolated, plot);
            g.FillEllipse(dot, p.X - radius, p.Y - radius, radius * 2, radius * 2);
        }

        if (geometry.Latest is { } latest)
        {
            var p = ToDevice(latest, plot);
            g.FillEllipse(dot, p.X - radius, p.Y - radius, radius * 2, radius * 2);
        }
    }

    private void DrawFooter(Graphics g, MemoryChartGeometry geometry, Rectangle plot)
    {
        // Absolute clock labels at both bounds: "the axis really spans the selected range" is the
        // claim a range button makes, and relative text like "-60m" cannot show it is honest.
        var start = MonitorForm.FormatTimeShort(geometry.WindowStart);
        var end = MonitorForm.FormatTimeShort(geometry.WindowEnd);
        g.DrawString(start, _axisFont ?? Font, SystemBrushes.ControlText, plot.Left, plot.Bottom + S(2));

        var width = TextRenderer.MeasureText(g, end, _axisFont ?? Font).Width;
        g.DrawString(end, _axisFont ?? Font, SystemBrushes.ControlText, plot.Right - width, plot.Bottom + S(2));
    }

    private void DrawReading(Graphics g, MemoryChartGeometry geometry, Rectangle plot)
    {
        var point = _hover ?? (_cursor >= 0 && _cursor < _navigationPoints.Count ? _navigationPoints[_cursor] : null);
        if (point is null && !_hoverInGap) return;

        var anchorX = plot.Left;
        if (point is not null) anchorX = ToDevice(point.Value, plot).X;

        _cursorPen ??= new Pen(TextSecondary) { DashStyle = DashStyle.Dot };
        g.DrawLine(_cursorPen, anchorX, plot.Top, anchorX, plot.Bottom);

        var text = _hoverInGap || point is null
            ? NoSamplesText
            : $"{MonitorForm.FormatTime(point.Value.AtUtc)}  {MonitorForm.FormatBytes(point.Value.Value)}";

        using var back = new SolidBrush(TooltipBack);
        using var border = new Pen(CardBorder);
        var measured = TextRenderer.MeasureText(g, text, Font);
        var width = Math.Min(measured.Width + TooltipPadding * 2, plot.Width);
        var x = Math.Clamp(anchorX - width / 2, plot.Left, Math.Max(plot.Left, plot.Right - width));
        var y = point is not null
            ? Math.Max(plot.Top + S(2), ToDevice(point.Value, plot).Y - S(24))
            : plot.Top + S(2);
        var bounds = new Rectangle(x, y, width, measured.Height + TooltipPadding);

        g.FillRectangle(back, bounds);
        g.DrawRectangle(border, bounds);
        TextRenderer.DrawText(g, text, Font, bounds, TextStrong,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private Point[] ToDevice(IReadOnlyList<MemoryChartPoint> points, Rectangle plot)
    {
        var device = new Point[points.Count];
        for (var i = 0; i < points.Count; i++) device[i] = ToDevice(points[i], plot);
        return device;
    }

    private Point ToDevice(MemoryChartPoint point, Rectangle plot) =>
        new(plot.Left + (int)Math.Round(point.X), plot.Top + (int)Math.Round(point.Y));

    private Point ToDevice(MemoryTrendPoint point, Rectangle plot)
    {
        var geometry = _geometry!;
        var span = geometry.WindowEnd.Subtract(geometry.WindowStart).TotalSeconds;
        var x = span <= 0 ? 0 : (point.AtUtc - geometry.WindowStart).TotalSeconds / span * geometry.PlotWidth;
        var y = geometry.AxisCapacity > 0
            ? geometry.PlotHeight - (double)point.Value / geometry.AxisCapacity * geometry.PlotHeight
            : geometry.PlotHeight;
        return new Point(plot.Left + (int)Math.Round(x), plot.Top + (int)Math.Round(y));
    }

    /// <summary>
    /// Reads the navigation list rather than <see cref="CurrentPoint"/>: that property ensures
    /// geometry exists, and this method is called from inside that build.
    /// </summary>
    private void UpdateAccessibleDescription()
    {
        var point = NewestPoint();
        AccessibleDescription = point is null
            ? EmptyText
            : $"{SeriesLabel} {MonitorForm.FormatBytes(point.Value.Value)}, " +
              $"{MonitorForm.FormatTime(point.Value.AtUtc)}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _linePen?.Dispose();
            _stalePen?.Dispose();
            _gridPen?.Dispose();
            _cursorPen?.Dispose();
            _fillBrush?.Dispose();
            _titleFont?.Dispose();
            _axisFont?.Dispose();
            _linePen = _stalePen = _gridPen = _cursorPen = null;
            _fillBrush = null;
        }
        base.Dispose(disposing);
    }
}
