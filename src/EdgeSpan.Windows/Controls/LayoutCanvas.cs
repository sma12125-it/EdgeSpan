using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using EdgeSpan.Layout;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Controls;

/// <summary>
/// Arrange map in the style of Windows 11 Settings &gt; Display: every Windows monitor at its real
/// virtual-desktop position and resolution (numbered, primary marked) plus the Android tiles, which snap
/// edge-to-edge onto any side of any monitor (or between two monitors) and slide along it to set the offset.
/// </summary>
public sealed class LayoutCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<DeviceSnapshot>?> DevicesProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<DeviceSnapshot>?>(nameof(Devices));
    public static readonly StyledProperty<IReadOnlyList<HostMonitor>?> MonitorsProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<HostMonitor>?>(nameof(Monitors));
    public static readonly StyledProperty<string?> ActiveIdProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(ActiveId));
    public static readonly StyledProperty<string?> SelectedIdProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(SelectedId));
    public static readonly StyledProperty<bool> UniformProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(Uniform));
    public bool Uniform { get => GetValue(UniformProperty); set => SetValue(UniformProperty, value); }
    public static readonly StyledProperty<string?> HostLabelProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(HostLabel), "PC");

    public IReadOnlyList<DeviceSnapshot>? Devices { get => GetValue(DevicesProperty); set => SetValue(DevicesProperty, value); }
    public IReadOnlyList<HostMonitor>? Monitors { get => GetValue(MonitorsProperty); set => SetValue(MonitorsProperty, value); }
    public string? ActiveId { get => GetValue(ActiveIdProperty); set => SetValue(ActiveIdProperty, value); }
    public string? SelectedId { get => GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }
    public string? HostLabel { get => GetValue(HostLabelProperty); set => SetValue(HostLabelProperty, value); }

    /// <summary>(deviceId, monitorId, edge, anchor, corner, offsetPx)</summary>
    public event Action<string, string, string, string, string, int>? PlacementChanged;
    public event Action<string>? TileSelected;

    private string? _dragId;
    private Point _grab;
    private Vector _drag;
    private bool _moved;
    private (Rect Free, Snapped Snap)? _preview;
    private double _scale = 1, _ox, _oy;
    // Note: _drag is in world units of the layout at drag start, so the scale must not jump mid-drag.

    private readonly record struct Snapped(HostMonitor Monitor, ScreenEdge Edge, int Offset, Rect Rect);

    static LayoutCanvas()
    {
        AffectsRender<LayoutCanvas>(DevicesProperty, MonitorsProperty, ActiveIdProperty, SelectedIdProperty, HostLabelProperty, UniformProperty);
    }

    public LayoutCanvas() => Cursor = new Cursor(StandardCursorType.Hand);

    private IReadOnlyList<HostMonitor> Mons()
    {
        var list = Monitors;
        return list is { Count: > 0 } ? list : new[] { new HostMonitor("", 0, 0, 1920, 1080, 96, true) };
    }

    private HostMonitor MonitorOf(DeviceSnapshot d)
    {
        var all = Mons();
        return all.FirstOrDefault(m => d.Monitor.Length > 0 && m.Id == d.Monitor) ?? all.FirstOrDefault(m => m.Primary) ?? all[0];
    }

    private static (double W, double H) Size(DeviceSnapshot d) =>
        (d.ScreenW > 0 ? Math.Clamp(d.ScreenW, 400, 4000) : 1600, d.ScreenH > 0 ? Math.Clamp(d.ScreenH, 400, 4000) : 1000);

    private static Rect PlaceOn(Rect m, ScreenEdge edge, int offset, double w, double h) => edge switch
    {
        ScreenEdge.Left => new Rect(m.X - w, m.Y + offset, w, h),
        ScreenEdge.Top => new Rect(m.X + offset, m.Y - h, w, h),
        ScreenEdge.Bottom => new Rect(m.X + offset, m.Bottom, w, h),
        _ => new Rect(m.Right, m.Y + offset, w, h)
    };

    private static Rect R(HostMonitor m) => new(m.X, m.Y, m.W, m.H);

    /// <summary>Where a device is attached: monitor, edge, offset (corner anchors are treated as after the monitor).</summary>
    private readonly record struct Attach(HostMonitor Monitor, ScreenEdge Edge, int Offset, string? Corner);

    private Attach AttachOf(DeviceSnapshot d)
    {
        var m = MonitorOf(d);
        if (string.Equals(d.Anchor, "Corner", StringComparison.OrdinalIgnoreCase))
            return new Attach(m, d.Corner is "TopLeft" or "BottomLeft" ? ScreenEdge.Left : ScreenEdge.Right, 0, d.Corner ?? "TopRight");
        PlacementParser.TryEdge(d.PcEdge, out var edge);
        return new Attach(m, edge, Math.Max(0, d.OffsetPx), null);
    }

    // Uniform cell: every display (monitor or Android) is drawn at this one size, top and bottom edges aligned.
    private const double CellW = 1600, CellH = 1000;

    /// <summary>
    /// Drawn rectangles for monitors and devices in world units. Never overlapping:
    /// "By resolution" keeps real positions/sizes and pushes the monitors beyond a device's seam outward by the
    /// device's size, so a tablet between monitors 1 and 2 sits edge-to-edge between them instead of on top.
    /// "Uniform" draws one row of identical cells ordered by the arrangement.
    /// </summary>
    private (Rect[] Mons, Dictionary<string, Rect> Devs) Layout(string? overrideId = null, Attach? over = null)
    {
        var mons = Mons();
        var devices = Devices ?? Array.Empty<DeviceSnapshot>();
        Attach Of(DeviceSnapshot d) => d.Id == overrideId && over is { } o ? o : AttachOf(d);
        var devs = new Dictionary<string, Rect>();
        if (Uniform)
        {
            var order = mons.Select((m, i) => (m, i)).OrderBy(x => x.m.X).ThenBy(x => x.m.Y).Select(x => x.i).ToList();
            var seq = new List<(double Key, int Mon, string? Dev)>();
            for (var k = 0; k < order.Count; k++)
                seq.Add((k, order[k], null));
            var n = 0;
            foreach (var d in devices)
            {
                var at = Of(d);
                var k = order.IndexOf(Array.IndexOf(mons.ToArray(), at.Monitor));
                if (k < 0) k = 0;
                var after = at.Edge is ScreenEdge.Right or ScreenEdge.Bottom;
                seq.Add((k + (after ? 0.5 : -0.5) + (++n) * 1e-4, -1, d.Id));
            }
            seq.Sort((x, y) => x.Key.CompareTo(y.Key));
            var rects = new Rect[mons.Count];
            for (var i = 0; i < seq.Count; i++)
            {
                var r = new Rect(i * CellW, 0, CellW, CellH);
                if (seq[i].Dev is { } id) devs[id] = r; else rects[seq[i].Mon] = r;
            }
            return (rects, devs);
        }
        var mr = mons.Select(R).ToArray();
        foreach (var d in devices)
        {
            var at = Of(d);
            var (w, h) = Size(d);
            var mi = Math.Max(0, Array.IndexOf(mons.ToArray(), at.Monitor));
            var m = mr[mi];
            Rect r;
            if (at.Corner is { } c)
                r = c switch
                {
                    "TopLeft" => new Rect(m.X - w, m.Y - h, w, h),
                    "BottomLeft" => new Rect(m.X - w, m.Bottom, w, h),
                    "BottomRight" => new Rect(m.Right, m.Bottom, w, h),
                    _ => new Rect(m.Right, m.Y - h, w, h)
                };
            else
            {
                var len = at.Edge is ScreenEdge.Left or ScreenEdge.Right ? m.Height : m.Width;
                r = PlaceOn(m, at.Edge, (int)Math.Min(at.Offset, Math.Max(0, len - 40)), w, h);
                // Make room: everything on the far side of the seam moves out by the device's size.
                var shift = at.Edge switch
                {
                    ScreenEdge.Right => new Vector(w, 0), ScreenEdge.Left => new Vector(-w, 0),
                    ScreenEdge.Bottom => new Vector(0, h), _ => new Vector(0, -h)
                };
                bool Beyond(Rect o) => at.Edge switch
                {
                    ScreenEdge.Right => o.X >= m.Right - 0.5, ScreenEdge.Left => o.Right <= m.X + 0.5,
                    ScreenEdge.Bottom => o.Y >= m.Bottom - 0.5, _ => o.Bottom <= m.Y + 0.5
                };
                for (var i = 0; i < mr.Length; i++)
                    if (i != mi && Beyond(mr[i]))
                        mr[i] = mr[i].Translate(shift);
                foreach (var key in devs.Keys.ToList())
                    if (Beyond(devs[key]))
                        devs[key] = devs[key].Translate(shift);
            }
            devs[d.Id] = r;
        }
        return (mr, devs);
    }

    private Snapped Snap(Rect free, DeviceSnapshot d)
    {
        var mons = Mons();
        Snapped? best = null;
        var bestScore = double.MaxValue;
        for (var i = 0; i < mons.Count; i++)
        {
            foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
            {
                if (Uniform && edge is ScreenEdge.Top or ScreenEdge.Bottom)
                    continue; // one row: before or after a monitor
                var m = mons[i];
                var offset = 0;
                if (!Uniform)
                {
                    var mr = Layout(d.Id, new Attach(m, edge, 0, null)).Mons[i];
                    var vertical = edge is ScreenEdge.Left or ScreenEdge.Right;
                    var raw = vertical ? free.Y - mr.Y : free.X - mr.X;
                    offset = (int)Math.Clamp(Math.Round(raw / 10) * 10, 0, Math.Max(0, (vertical ? m.H : m.W) - 40));
                }
                var r = Layout(d.Id, new Attach(m, edge, offset, null)).Devs[d.Id];
                var score = Math.Abs(r.Center.X - free.Center.X) + Math.Abs(r.Center.Y - free.Center.Y);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Snapped(m, edge, offset, r);
                }
            }
        }
        return best!.Value;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 8 || bounds.Height < 8)
            return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var monitors = Mons();
        var devices = Devices ?? Array.Empty<DeviceSnapshot>();
        var lay = _preview is { } p0 && _dragId != null
            ? Layout(_dragId, new Attach(p0.Snap.Monitor, p0.Snap.Edge, p0.Snap.Offset, null))
            : Layout();
        if (_preview == null)
            _rest = lay;
        var all = lay.Mons.Concat(lay.Devs.Values).ToList();
        if (_preview is { } pv)
            all.Add(pv.Free);
        var world = all.Aggregate((a, b) => a.Union(b));
        var pad = Math.Max(world.Width, world.Height) * 0.06;
        world = world.Inflate(pad);
        _scale = Math.Min(bounds.Width / world.Width, bounds.Height / world.Height);
        _ox = (bounds.Width - world.Width * _scale) / 2 - world.X * _scale;
        _oy = (bounds.Height - world.Height * _scale) / 2 - world.Y * _scale;

        var tile = new SolidColorBrush(Color.Parse(dark ? "#3A3A3A" : "#E3E6EB"));
        var tileLine = new Pen(new SolidColorBrush(Color.Parse(dark ? "#4A4A4A" : "#C9CED6")), 1);
        var ink = new SolidColorBrush(Color.Parse(dark ? "#FFFFFF" : "#1A1A1A"));
        var muted = new SolidColorBrush(Color.Parse(dark ? "#C5C5C5" : "#5D5D5D"));
        var accent = new Pen(new SolidColorBrush(Color.Parse("#3B6CFF")), 2.5);
        var teal = new SolidColorBrush(Color.Parse(dark ? "#1F4D48" : "#CDEFEA"));
        var tealLine = new Pen(new SolidColorBrush(Color.Parse("#14B8A6")), 1.2);
        const double gap = 2;

        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var r = Map(lay.Mons[i]).Deflate(gap);
            context.DrawRectangle(tile, tileLine, r, 6, 6);
            Label(context, r, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                m.Primary ? (HostLabel ?? "PC") + " · primary" : $"{m.W} × {m.H}", ink, muted);
        }

        var number = monitors.Count;
        foreach (var d in devices)
        {
            number++;
            if (!lay.Devs.TryGetValue(d.Id, out var drawn))
                continue;
            var mapped = Map(drawn).Deflate(gap);
            if (d.Id == _dragId && _preview is { } pf)
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 59, 108, 255)), null, Map(pf.Free).Deflate(gap), 6, 6);
            context.DrawRectangle(teal, tealLine, mapped, 6, 6);
            if (d.Id == SelectedId || d.Id == _dragId || d.Id == ActiveId)
                context.DrawRectangle(null, accent, mapped.Deflate(1), 6, 6);
            Label(context, mapped, number.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Name, ink, muted);
        }
        DrawCrossings(context, lay.Mons.Concat(lay.Devs.Values).ToList());
    }

    /// <summary>Green crossing line on every edge two displays share, only along the overlapping part (live while dragging).</summary>
    private void DrawCrossings(DrawingContext context, List<Rect> rects)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#22C55E")), 3, lineCap: PenLineCap.Round);
        const double eps = 1.5; // world units
        for (var i = 0; i < rects.Count; i++)
            for (var j = 0; j < rects.Count; j++)
            {
                if (i == j) continue;
                var a = rects[i]; var b = rects[j];
                if (Math.Abs(a.Right - b.X) < eps)
                {
                    var lo = Math.Max(a.Y, b.Y); var hi = Math.Min(a.Bottom, b.Bottom);
                    if (hi - lo > eps)
                    {
                        var x = _ox + a.Right * _scale;
                        context.DrawLine(pen, new Point(x, _oy + lo * _scale + 4), new Point(x, _oy + hi * _scale - 4));
                    }
                }
                if (Math.Abs(a.Bottom - b.Y) < eps)
                {
                    var lo = Math.Max(a.X, b.X); var hi = Math.Min(a.Right, b.Right);
                    if (hi - lo > eps)
                    {
                        var y = _oy + a.Bottom * _scale;
                        context.DrawLine(pen, new Point(_ox + lo * _scale + 4, y), new Point(_ox + hi * _scale - 4, y));
                    }
                }
            }
    }

    private (Rect[] Mons, Dictionary<string, Rect> Devs)? _rest;

    private Rect Map(Rect r) => new(_ox + r.X * _scale, _oy + r.Y * _scale, Math.Max(6, r.Width * _scale), Math.Max(6, r.Height * _scale));

    private static void Label(DrawingContext context, Rect rect, string big, string small, IBrush ink, IBrush muted)
    {
        var face = new Typeface("Segoe UI Variable Display, Segoe UI, Vazirmatn");
        var size = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.32, 11, 34);
        var a = new FormattedText(big, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, ink);
        var b = new FormattedText(small, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, Math.Clamp(size * 0.42, 10, 13), muted)
        {
            MaxTextWidth = Math.Max(10, rect.Width - 8), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
        };
        var showSmall = rect.Height > a.Height + b.Height + 6;
        var total = a.Height + (showSmall ? b.Height : 0);
        var y = rect.Y + (rect.Height - total) / 2;
        context.DrawText(a, new Point(rect.X + (rect.Width - a.Width) / 2, y));
        if (showSmall)
            context.DrawText(b, new Point(rect.X + Math.Max(4, (rect.Width - b.Width) / 2), y + a.Height));
    }

    private string? Hit(Point point)
    {
        var devices = Devices ?? Array.Empty<DeviceSnapshot>();
        var lay = Layout();
        foreach (var d in devices.Reverse())
            if (lay.Devs.TryGetValue(d.Id, out var r) && Map(r).Contains(point))
                return d.Id;
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        var hit = Hit(point);
        if (hit == null)
            return;
        _dragId = hit;
        _grab = point;
        _drag = default;
        _moved = false;
        _preview = null;
        TileSelected?.Invoke(hit);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragId == null || _scale <= 0)
            return;
        var point = e.GetPosition(this);
        var delta = point - _grab;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
            _moved = true;
        if (!_moved)
            return;
        var d = (Devices ?? Array.Empty<DeviceSnapshot>()).FirstOrDefault(x => x.Id == _dragId);
        if (d == null)
            return;
        _drag = new Vector(delta.X / _scale, delta.Y / _scale);
        var start = (_rest ?? Layout()).Devs.TryGetValue(d.Id, out var r0) ? r0 : new Rect(0, 0, CellW, CellH);
        var free = start.Translate(_drag);
        _preview = (free, Snap(free, d));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragId == null)
            return;
        e.Pointer.Capture(null);
        var id = _dragId;
        var snap = _preview?.Snap;
        _dragId = null;
        _preview = null;
        _drag = default;
        if (_moved && snap is { } s)
            PlacementChanged?.Invoke(id, s.Monitor.Id, s.Edge.ToString(), "Edge", "TopRight", s.Offset);
        InvalidateVisual();
    }
}
