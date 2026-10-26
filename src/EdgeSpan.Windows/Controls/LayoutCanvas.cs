using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Controls;

public sealed class LayoutCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<DeviceSnapshot>?> DevicesProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<DeviceSnapshot>?>(nameof(Devices));

    public static readonly StyledProperty<string?> ActiveIdProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(ActiveId));

    public static readonly StyledProperty<string?> HostLabelProperty =
        AvaloniaProperty.Register<LayoutCanvas, string?>(nameof(HostLabel), "PC");

    public IReadOnlyList<DeviceSnapshot>? Devices
    {
        get => GetValue(DevicesProperty);
        set => SetValue(DevicesProperty, value);
    }

    public string? ActiveId
    {
        get => GetValue(ActiveIdProperty);
        set => SetValue(ActiveIdProperty, value);
    }

    public string? HostLabel
    {
        get => GetValue(HostLabelProperty);
        set => SetValue(HostLabelProperty, value);
    }

    public event Action<string, string, string, string, int>? PlacementChanged;

    private readonly Dictionary<string, Rect> _virtual = new();
    private string? _dragId;
    private Point _grab;
    private Vector _drag;
    private bool _moved;

    static LayoutCanvas()
    {
        AffectsRender<LayoutCanvas>(DevicesProperty, ActiveIdProperty, HostLabelProperty);
    }

    public LayoutCanvas()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 8 || bounds.Height < 8)
            return;
        var host = new Rect(0, 0, 1920, 1080);
        _virtual.Clear();
        var devices = Devices ?? Array.Empty<DeviceSnapshot>();
        foreach (var device in devices)
        {
            var rect = Place(device);
            if (_dragId == device.Id)
                rect = rect.Translate(_drag);
            _virtual[device.Id] = rect;
            host = host.Union(rect);
        }
        host = host.Inflate(80);
        var scale = Math.Min(bounds.Width / host.Width, bounds.Height / host.Height);
        var ox = (bounds.Width - host.Width * scale) / 2 - host.X * scale;
        var oy = (bounds.Height - host.Height * scale) / 2 - host.Y * scale;
        Rect Map(Rect r) => new(ox + r.X * scale, oy + r.Y * scale, Math.Max(8, r.Width * scale), Math.Max(8, r.Height * scale));

        var pc = Map(new Rect(0, 0, 1920, 1080));
        var bg = Color.Parse("#18233C");
        var paper = Color.Parse("#F7FBFF");
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0E1528")), bounds, 22);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#2A3558")), 1), bounds, 22);
        context.FillRectangle(new SolidColorBrush(bg), pc, 18);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#8EA0C8")), 1.5), pc, 18);
        DrawLabel(context, pc, HostLabel ?? "PC", paper);

        foreach (var device in devices)
        {
            var rect = _virtual[device.Id];
            var mapped = Map(rect);
            var active = device.Id == ActiveId || device.Id == _dragId;
            var fill = active ? Color.Parse("#142C4E") : Color.Parse("#243252");
            var stroke = active ? Color.Parse("#6AA4E0") : Color.Parse("#9BB0D4");
            context.FillRectangle(new SolidColorBrush(fill), mapped, 16);
            context.DrawRectangle(new Pen(new SolidColorBrush(stroke), active ? 2.5 : 1.4), mapped, 16);
            DrawSeam(context, pc, mapped, device);
            DrawLabel(context, mapped, device.Name, paper);
        }
    }

    private static void DrawSeam(DrawingContext context, Rect pc, Rect device, DeviceSnapshot snapshot)
    {
        Rect seam;
        if (string.Equals(snapshot.Anchor, "Corner", StringComparison.OrdinalIgnoreCase))
        {
            var size = 14.0;
            seam = snapshot.Corner switch
            {
                "TopLeft" => new Rect(pc.X - 2, pc.Y - 2, size, size),
                "BottomLeft" => new Rect(pc.X - 2, pc.Bottom - size, size, size),
                "BottomRight" => new Rect(pc.Right - size, pc.Bottom - size, size, size),
                _ => new Rect(pc.Right - size, pc.Y - 2, size, size)
            };
        }
        else
        {
            seam = snapshot.PcEdge switch
            {
                "Left" => new Rect(pc.X - 3, Math.Max(pc.Y, device.Y), 6, Math.Min(pc.Height, device.Height)),
                "Top" => new Rect(Math.Max(pc.X, device.X), pc.Y - 3, Math.Min(pc.Width, device.Width), 6),
                "Bottom" => new Rect(Math.Max(pc.X, device.X), pc.Bottom - 3, Math.Min(pc.Width, device.Width), 6),
                _ => new Rect(pc.Right - 3, Math.Max(pc.Y, device.Y), 6, Math.Min(pc.Height, device.Height))
            };
        }
        context.FillRectangle(new SolidColorBrush(Color.Parse("#6AA4E0")), seam, 4);
    }

    private static void DrawLabel(DrawingContext context, Rect rect, string text, Color color)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Plus Jakarta Sans"),
            Math.Clamp(rect.Height * 0.16, 12, 18),
            new SolidColorBrush(color));
        var x = rect.X + Math.Max(8, (rect.Width - formatted.Width) / 2);
        var y = rect.Y + Math.Max(6, (rect.Height - formatted.Height) / 2);
        context.DrawText(formatted, new Point(x, y));
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
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragId == null)
            return;
        var point = e.GetPosition(this);
        var delta = point - _grab;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
            _moved = true;
        var host = VirtualHost();
        if (host.Width < 1)
            return;
        var scale = Math.Min(Bounds.Width / host.Width, Bounds.Height / host.Height);
        _drag = new Vector(delta.X / scale, delta.Y / scale);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragId == null)
            return;
        e.Pointer.Capture(null);
        var id = _dragId;
        Rect dropped = default;
        var moved = _moved && _virtual.TryGetValue(id, out dropped);
        _dragId = null;
        _drag = default;
        if (moved)
        {
            var (edge, anchor, corner, offset) = Snap(dropped);
            PlacementChanged?.Invoke(id, edge, anchor, corner, offset);
        }
        InvalidateVisual();
    }

    private string? Hit(Point point)
    {
        var host = VirtualHost();
        if (host.Width < 1)
            return null;
        var scale = Math.Min(Bounds.Width / host.Width, Bounds.Height / host.Height);
        var ox = (Bounds.Width - host.Width * scale) / 2 - host.X * scale;
        var oy = (Bounds.Height - host.Height * scale) / 2 - host.Y * scale;
        foreach (var pair in _virtual.Reverse())
        {
            var mapped = new Rect(ox + pair.Value.X * scale, oy + pair.Value.Y * scale, pair.Value.Width * scale, pair.Value.Height * scale);
            if (mapped.Contains(point))
                return pair.Key;
        }
        return null;
    }

    private Rect VirtualHost()
    {
        var host = new Rect(0, 0, 1920, 1080);
        foreach (var rect in _virtual.Values)
            host = host.Union(rect);
        return host.Inflate(80);
    }

    private static (string Edge, string Anchor, string Corner, int Offset) Snap(Rect rect)
    {
        var cx = rect.Center.X;
        var cy = rect.Center.Y;
        var nearLeft = cx < 80;
        var nearRight = cx > 1840;
        var nearTop = cy < 80;
        var nearBottom = cy > 1000;
        if (nearLeft && nearTop)
            return ("Left", "Corner", "TopLeft", 0);
        if (nearRight && nearTop)
            return ("Right", "Corner", "TopRight", 0);
        if (nearLeft && nearBottom)
            return ("Left", "Corner", "BottomLeft", 0);
        if (nearRight && nearBottom)
            return ("Right", "Corner", "BottomRight", 0);
        var left = Math.Abs(cx);
        var right = Math.Abs(cx - 1920);
        var top = Math.Abs(cy);
        var bottom = Math.Abs(cy - 1080);
        var best = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        if (best == top)
            return ("Top", "Edge", "TopRight", (int)Math.Clamp(rect.X, 0, 1800));
        if (best == bottom)
            return ("Bottom", "Edge", "BottomRight", (int)Math.Clamp(rect.X, 0, 1800));
        if (best == left)
            return ("Left", "Edge", "TopLeft", (int)Math.Clamp(rect.Y, 0, 900));
        return ("Right", "Edge", "TopRight", (int)Math.Clamp(rect.Y, 0, 900));
    }

    private static Rect Place(DeviceSnapshot device)
    {
        var span = device.SpanPx > 0 ? device.SpanPx : Along(device);
        var thick = Thick(device);
        if (string.Equals(device.Anchor, "Corner", StringComparison.OrdinalIgnoreCase))
        {
            var size = Math.Max(80, device.CornerSizePx * 4);
            return device.Corner switch
            {
                "TopLeft" => new Rect(-size - 28, -size - 28, size, size),
                "BottomLeft" => new Rect(-size - 28, 1080 + 28, size, size),
                "BottomRight" => new Rect(1920 + 28, 1080 + 28, size, size),
                _ => new Rect(1920 + 28, -size - 28, size, size)
            };
        }
        var offset = Math.Max(0, device.OffsetPx);
        return device.PcEdge switch
        {
            "Left" => new Rect(-thick - 28, offset, thick, span),
            "Top" => new Rect(offset, -thick - 28, span, thick),
            "Bottom" => new Rect(offset, 1080 + 28, span, thick),
            _ => new Rect(1920 + 28, offset, thick, span)
        };
    }

    private static int Along(DeviceSnapshot device)
    {
        var vertical = device.PcEdge is "Left" or "Right";
        var length = vertical ? device.ScreenH : device.ScreenW;
        return Math.Clamp(length, 240, 1400);
    }

    private static int Thick(DeviceSnapshot device)
    {
        var vertical = device.PcEdge is "Left" or "Right";
        var length = vertical ? device.ScreenW : device.ScreenH;
        return Math.Clamp(length / 5, 180, 420);
    }
}
