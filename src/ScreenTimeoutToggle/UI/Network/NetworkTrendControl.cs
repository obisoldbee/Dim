using System.Diagnostics;
using System.Drawing.Drawing2D;
using OBDim.Monitoring.Network.V2;
using OBDim.Services;
using static OBDim.Monitoring.Network.V2.NetworkSeries;

namespace OBDim.UI.Network;

/// <summary>Native dual-direction chart; paints only prepared data, never reads the OS.</summary>
public sealed class NetworkTrendControl : Control
{
    public static readonly Color UploadColor = ColorTranslator.FromHtml("#E63D58");
    public static readonly Color DownloadColor = ColorTranslator.FromHtml("#087BFF");
    private static readonly TimeSpan[] Windows = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2)];
    private readonly Button[] _ranges = Enumerable.Range(0, 5).Select(_ => new Button()).ToArray();
    private readonly Font _caption = new("Microsoft YaHei UI", 9F);
    private readonly Font _heading = new("Microsoft YaHei UI", 10F, FontStyle.Bold);
    private readonly Font _value = new("Microsoft YaHei UI", 19F, FontStyle.Bold);
    private readonly Font _small = new("Microsoft YaHei UI", 8F);
    private InterfaceReading? _reading;
    private DateTimeOffset _now;
    private bool _fresh;
    private int _range = 3;
    private Projection _plot = Project([], null, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
    private Axis? _upAxis, _downAxis;
    private DateTimeOffset? _cursor;
    private bool _pinned;
    private long _version = -1;
    public int GeometryBuildCount { get; private set; }
    public int PaintCount { get; private set; }
    public bool HasReading => _cursor is not null;
    public int RangeIndex => _range;
    public IReadOnlyList<Button> RangeButtons => _ranges;
    public DateTimeOffset? CursorTime => _cursor;
    public event Action? FramePainted;
    private static bool English => LocalizationService.CurrentLanguage == "en-US";
    private static string T(string zh, string en) => English ? en : zh;
    private int S(double n) => (int)Math.Round(n * DeviceDpi / 96.0);
    public NetworkTrendControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White; TabStop = true; AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "网络流量趋势";
        for (var i = 0; i < 5; i++)
        {
            var index = i; var b = _ranges[i];
            b.Name = "network-range-" + i;
            b.Font = _caption; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.Cursor = Cursors.Hand; b.TabIndex = i;
            b.Click += (_, _) => SetRange(index);
            b.KeyDown += (_, e) =>
            {
                var next = e.KeyCode switch { Keys.Left => Math.Max(0, _range - 1), Keys.Right => Math.Min(4, _range + 1), Keys.Home => 0, Keys.End => 4, _ => -1 };
                if (next >= 0) { SetRange(next); _ranges[next].Focus(); e.Handled = e.SuppressKeyPress = true; }
            };
            Controls.Add(b);
        }
        Localize();
    }
    public void Localize()
    {
        var labels = English ? new[] { "1 min", "10 min", "30 min", "1 hour", "2 hours" } :
            new[] { "1 分钟", "10 分钟", "30 分钟", "1 小时", "2 小时" };
        for (var i = 0; i < 5; i++) { _ranges[i].Text = labels[i]; _ranges[i].AccessibleName = labels[i]; }
        AccessibleName = T("网络流量趋势", "Network traffic trend"); StyleRanges(); Invalidate();
    }
    public void SetRange(int value)
    {
        if (value < 0 || value >= 5) return;
        _range = value; _cursor = null; _pinned = false; StyleRanges(); Prepare();
    }
    private void StyleRanges()
    {
        for (var i = 0; i < 5; i++)
        { _ranges[i].BackColor = i == _range ? DownloadColor : Color.FromArgb(246, 247, 249); _ranges[i].ForeColor = i == _range ? Color.White : Color.FromArgb(48, 54, 65); }
    }
    public void Apply(InterfaceReading? reading, DateTimeOffset now, long version, bool fresh)
    {
        var changed = _version != version || _reading?.Id != reading?.Id || _fresh != fresh;
        _reading = reading; _now = now; _version = version; _fresh = fresh;
        if (changed) Prepare();
    }
    private void Prepare()
    {
        _plot = Project(_reading?.Samples ?? [], _reading?.Id, _now, Windows[_range]);
        GeometryBuildCount++;
        var key = $"{_reading?.Id}:{_range}";
        var monotonicMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        _upAxis = UpdateAxis(_upAxis, _plot.Directions.Upload.Peak, monotonicMs, key);
        _downAxis = UpdateAxis(_downAxis, _plot.Directions.Download.Peak, monotonicMs, key);
        if (_cursor < _plot.From || _cursor > _plot.To) { _cursor = null; _pinned = false; }
        UpdateDescription(); Invalidate();
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var x = S(12); var width = Math.Max(5, Width - S(24));
        for (var i = 0; i < 5; i++)
            _ranges[i].SetBounds(x + width * i / 5, S(42), width * (i + 1) / 5 - width * i / 5, S(32));
    }
    public Rectangle PlotBounds(bool upload) => new(S(78), S(upload ? 184 : 393),
        Math.Max(1, Width - S(96)), S(80));
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); PaintCount++;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Color.FromArgb(231, 233, 238));
        using var path = Rounded(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), S(12));
        g.DrawPath(border, path);
        DrawLabel(g, T("流量趋势", "Traffic trend"), _heading, Color.FromArgb(48, 54, 65), new(S(12), S(12), Width - S(24), S(25)));
        DrawLabel(g, T("独立刻度", "Independent axes"), _small, Color.DimGray, new(Width - S(150), S(13), S(138), S(22)), true);
        DrawDirection(g, true); DrawDirection(g, false);
        g.DrawLine(border, S(12), S(285), Width - S(12), S(285));
        g.DrawLine(border, S(12), S(509), Width - S(12), S(509));
        var lower = PlotBounds(false);
        foreach (var fraction in new[] { 0.0, .5, 1.0 })
        {
            var time = _plot.From + TimeSpan.FromTicks((long)((_plot.To - _plot.From).Ticks * fraction));
            var rect = new Rectangle(lower.Left + (int)(lower.Width * fraction) - S(34), lower.Bottom + S(7), S(70), S(20));
            DrawLabel(g, time.LocalDateTime.ToString("HH:mm:ss"), _small, Color.DimGray, rect);
        }
        var note = _cursor is { } at
            ? $"{at.LocalDateTime:HH:mm:ss}   ↑ {Rate(Probe(_plot.Directions.Upload, at), T("未知", "Unknown"))}   ↓ {Rate(Probe(_plot.Directions.Download, at), T("未知", "Unknown"))}"
            : T("独立缩放：上下两图等高不代表等速", "Independent scales: equal height does not mean equal speed");
        DrawLabel(g, note, _small, Color.DimGray, new(S(12), S(516), Width - S(24), S(27)));
        if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(S(4), S(80), Width - S(8), S(384)));
        FramePainted?.Invoke();
    }
    private void DrawDirection(Graphics g, bool upload)
    {
        var top = S(upload ? 87 : 296); var color = upload ? UploadColor : DownloadColor;
        var dir = _plot.Directions[upload]; var axis = (upload ? _upAxis : _downAxis)?.Max ?? 1;
        var bounds = PlotBounds(upload);
        var current = _fresh ? _reading?.Rates[upload] : null;
        DrawLabel(g, upload ? T("↑ 上传", "↑ Upload") : T("↓ 下载", "↓ Download"), _heading, color, new(S(12), top, S(150), S(25)));
        DrawLabel(g, Rate(current, T("未知", "Unknown")), _value, Color.FromArgb(48, 54, 65), new(S(12), top + S(24), Width - S(150), S(34)));
        DrawLabel(g, T("所选范围峰值", "Range peak"), _small, Color.DimGray, new(Width - S(153), top + S(3), S(141), S(23)), true);
        DrawLabel(g, Rate(dir.Peak, T("未知", "Unknown")), _caption, Color.FromArgb(48, 54, 65), new(Width - S(160), top + S(28), S(148), S(27)), true);
        var total = _reading?.Totals[upload];
        var since = total?.Bytes is not null && total.Since is { } date ? date.LocalDateTime.ToString("HH:mm:ss") : T("起点待确认", "start unconfirmed");
        DrawLabel(g, $"{T("本段累计", "Segment total")} {Bytes(total?.Bytes, T("未知", "Unknown"))} · {since}",
            _small, Color.DimGray, new(S(12), top + S(60), Width - S(24), S(22)));
        using var grid = new Pen(Color.FromArgb(225, 231, 240)) { DashStyle = DashStyle.Dash };
        for (var i = 0; i < 3; i++)
        {
            var y = bounds.Top + bounds.Height * i / 2;
            g.DrawLine(grid, bounds.Left, y, bounds.Right, y);
            DrawLabel(g, Rate(axis * (2 - i) / 2), _small, Color.DimGray, new(S(3), y - S(9), S(70), S(20)), true);
        }
        for (var i = 0; i < 5; i++)
        { var x = bounds.Left + bounds.Width * i / 4; g.DrawLine(grid, x, bounds.Top, x, bounds.Bottom); }
        if (dir.Raw.Count == 0)
            DrawLabel(g, T("暂无已知速率", "No known samples"), _small, Color.DimGray, bounds);
        float X(DateTimeOffset at) => bounds.Left + (float)((at - _plot.From).TotalSeconds / Windows[_range].TotalSeconds * bounds.Width);
        using var stroke = new Pen(color, Math.Max(1.5f, DeviceDpi / 64f));
        using var fill = new SolidBrush(Color.FromArgb(20, color));
        using var dot = new SolidBrush(color);
        foreach (var run in dir.Runs)
        {
            var pts = run.Select(p => new PointF(X(p.At), bounds.Bottom - (float)(p.Value / axis * bounds.Height))).ToArray();
            if (pts.Length == 1) { g.FillEllipse(dot, pts[0].X - S(2), pts[0].Y - S(2), S(4), S(4)); continue; }
            using var area = new GraphicsPath();
            area.AddLines(pts); area.AddLine(pts[^1], new(pts[^1].X, bounds.Bottom));
            area.AddLine(new PointF(pts[^1].X, bounds.Bottom), new PointF(pts[0].X, bounds.Bottom)); area.CloseFigure();
            g.FillPath(fill, area); g.DrawLines(stroke, pts);
        }
        if (_cursor is { } cursor)
        {
            using var pen = new Pen(Color.Gray) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, X(cursor), bounds.Top, X(cursor), bounds.Bottom);
        }
    }
    private static void DrawLabel(Graphics g, string value, Font font, Color color, Rectangle bounds, bool right = false) =>
        TextRenderer.DrawText(g, value, font, bounds, color,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            (right ? TextFormatFlags.Right : TextFormatFlags.Left));
    private static GraphicsPath Rounded(Rectangle rect, int r)
    {
        var p = new GraphicsPath(); var d = Math.Max(2, Math.Min(r * 2, Math.Min(rect.Width, rect.Height)));
        p.AddArc(rect.Left, rect.Top, d, d, 180, 90); p.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        p.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); p.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pinned) return;
        var p = PlotBounds(true); var lower = PlotBounds(false);
        if (!p.Contains(e.Location) && !lower.Contains(e.Location)) return;
        _cursor = _plot.From + TimeSpan.FromSeconds(Windows[_range].TotalSeconds * Math.Clamp((e.X - p.Left) / (double)p.Width, 0, 1));
        UpdateDescription(); Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!_pinned && !Focused) { _cursor = null; UpdateDescription(); Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Y >= S(80)) { Focus(); _pinned = !_pinned; } }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Space or Keys.Escape || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var points = _plot.Directions.Upload.Raw.Concat(_plot.Directions.Download.Raw).Select(p => p.At).Distinct().Order().ToArray();
        if (e.KeyCode == Keys.Escape) { _cursor = null; _pinned = false; }
        else if (e.KeyCode == Keys.Space) _pinned = !_pinned;
        else if (points.Length > 0)
        {
            var index = _cursor is null ? points.Length - 1 : Array.FindIndex(points, p => p >= _cursor);
            if (index < 0) index = points.Length - 1;
            var next = e.KeyCode switch { Keys.Left => Math.Max(0, index - 1), Keys.Right => Math.Min(points.Length - 1, index + 1), Keys.Home => 0, Keys.End => points.Length - 1, _ => -1 };
            if (next < 0) { base.OnKeyDown(e); return; }
            _cursor = points[next]; _pinned = true;
        }
        e.Handled = e.SuppressKeyPress = true; UpdateDescription(); Invalidate();
    }
    private void UpdateDescription()
    {
        AccessibleDescription = _cursor is { } at
            ? $"{at.LocalDateTime:HH:mm:ss}; {T("上传", "Upload")} {Rate(Probe(_plot.Directions.Upload, at), T("未知", "Unknown"))}; {T("下载", "Download")} {Rate(Probe(_plot.Directions.Download, at), T("未知", "Unknown"))}"
            : T("上下行独立纵轴；方向键查看，空格固定，Escape清除", "Independent axes; arrows inspect, Space pins, Escape clears");
    }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing) { _caption.Dispose(); _heading.Dispose(); _value.Dispose(); _small.Dispose(); } }
}
