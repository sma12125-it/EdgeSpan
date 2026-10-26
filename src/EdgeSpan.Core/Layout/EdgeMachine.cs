using EdgeSpan.Product;

namespace EdgeSpan.Layout;

public enum ScreenEdge : byte
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3
}

public enum AnchorKind
{
    Edge = 0,
    Corner = 1
}

public enum ScreenCorner
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3
}

/// <summary>One Windows monitor in virtual-desktop physical pixels (per-monitor DPI aware process).</summary>
public sealed record HostMonitor(string Id, int X, int Y, int W, int H, int Dpi = 96, bool Primary = false)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

public sealed class DevicePlacement
{
    /// <summary>Monitor the device is attached to; empty or unknown falls back to the primary monitor.</summary>
    public string MonitorId { get; init; } = "";
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ScreenEdge PcEdge { get; init; }
    public ScreenEdge ReturnEdge { get; init; }
    public AnchorKind Anchor { get; init; }
    public ScreenCorner Corner { get; init; }
    public int OffsetPx { get; init; }
    public int SpanPx { get; init; }
    public int CornerSizePx { get; init; } = 120;
    public double PointerSpeed { get; init; } = 1;
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
}

public abstract record Transition
{
    public sealed record Stay(int X, int Y) : Transition;
    public sealed record Blocked(int X, int Y) : Transition;
    public sealed record Enter(string DeviceId, int X, int Y, ScreenEdge ReturnEdge, int DevW, int DevH, int ParkX, int ParkY) : Transition;
    public sealed record Return(int HostX, int HostY, int DeviceX, int DeviceY, string? MonitorId = null, bool Through = false) : Transition;
}

public static class EdgeGuard
{
    public static bool Crosses(ScreenEdge edge, int x, int y, int width, int height) => edge switch
    {
        ScreenEdge.Left => x < 0,
        ScreenEdge.Right => x >= width,
        ScreenEdge.Top => y < 0,
        ScreenEdge.Bottom => y >= height,
        _ => false
    };
}

public static class PlacementParser
{
    public static bool TryParse(DeviceConfig config, out DevicePlacement placement, out string error)
    {
        placement = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(config.Id))
        {
            error = "bad-id";
            return false;
        }
        if (!TryEdge(config.PcEdge, out var pc) || !TryEdge(config.ReturnEdge, out var ret))
        {
            error = "bad-edge";
            return false;
        }
        if (!TryAnchor(config.Anchor, out var anchor) || !TryCorner(config.Corner, out var corner))
        {
            error = "bad-anchor";
            return false;
        }
        if (config.ScreenWidth is < 1 or > 16384 || config.ScreenHeight is < 1 or > 16384)
        {
            error = "bad-screen";
            return false;
        }
        if (double.IsNaN(config.PointerSpeed) || config.PointerSpeed is < 0.1 or > 8)
        {
            error = "bad-speed";
            return false;
        }
        placement = new DevicePlacement
        {
            Id = config.Id,
            MonitorId = config.Monitor ?? "",
            Name = string.IsNullOrWhiteSpace(config.Name) ? "Android" : config.Name,
            PcEdge = pc,
            ReturnEdge = ret,
            Anchor = anchor,
            Corner = corner,
            OffsetPx = Math.Max(0, config.OffsetPx),
            SpanPx = Math.Max(0, config.SpanPx),
            CornerSizePx = config.CornerSizePx <= 0 ? 120 : config.CornerSizePx,
            PointerSpeed = config.PointerSpeed,
            ScreenWidth = config.ScreenWidth,
            ScreenHeight = config.ScreenHeight
        };
        return true;
    }

    public static List<string> Problems(IReadOnlyList<DevicePlacement> devices)
    {
        var problems = new List<string>();
        for (var i = 0; i < devices.Count; i++)
        {
            for (var j = i + 1; j < devices.Count; j++)
            {
                var a = devices[i];
                var b = devices[j];
                if (a.Anchor == AnchorKind.Corner && b.Anchor == AnchorKind.Corner && a.Corner == b.Corner)
                    problems.Add($"corner:{a.Id}:{b.Id}");
                if (a.Anchor == AnchorKind.Edge && b.Anchor == AnchorKind.Edge && a.PcEdge == b.PcEdge && a.MonitorId == b.MonitorId && RangesOverlap(a, b))
                    problems.Add($"overlap:{a.Id}:{b.Id}");
            }
        }
        return problems;
    }

    public static string Describe(Transition transition) => transition switch
    {
        Transition.Enter e => $"enters {e.DeviceId} at ({e.X},{e.Y})",
        Transition.Return r => $"returns to PC at ({r.HostX},{r.HostY})",
        Transition.Blocked b => $"blocked at ({b.X},{b.Y})",
        Transition.Stay s => $"stays at ({s.X},{s.Y})",
        _ => ""
    };

    private static bool RangesOverlap(DevicePlacement a, DevicePlacement b)
    {
        var a1 = a.OffsetPx;
        var a2 = a.OffsetPx + EffectiveSpan(a);
        var b1 = b.OffsetPx;
        var b2 = b.OffsetPx + EffectiveSpan(b);
        return a1 < b2 && b1 < a2;
    }

    private static int EffectiveSpan(DevicePlacement d)
    {
        var side = d.PcEdge is ScreenEdge.Left or ScreenEdge.Right ? d.ScreenHeight : d.ScreenWidth;
        return d.SpanPx > 0 ? d.SpanPx : side;
    }

    public static bool TryEdge(string? text, out ScreenEdge edge)
    {
        edge = ScreenEdge.Right;
        return text?.Trim().ToLowerInvariant() switch
        {
            "left" => Set(ScreenEdge.Left, out edge),
            "right" => Set(ScreenEdge.Right, out edge),
            "top" => Set(ScreenEdge.Top, out edge),
            "bottom" => Set(ScreenEdge.Bottom, out edge),
            _ => false
        };
    }

    public static bool TryAnchor(string? text, out AnchorKind anchor)
    {
        anchor = AnchorKind.Edge;
        return text?.Trim().ToLowerInvariant() switch
        {
            "edge" => SetA(AnchorKind.Edge, out anchor),
            "corner" => SetA(AnchorKind.Corner, out anchor),
            _ => false
        };
    }

    public static bool TryCorner(string? text, out ScreenCorner corner)
    {
        corner = ScreenCorner.TopRight;
        return text?.Trim().ToLowerInvariant() switch
        {
            "topleft" => SetC(ScreenCorner.TopLeft, out corner),
            "topright" => SetC(ScreenCorner.TopRight, out corner),
            "bottomleft" => SetC(ScreenCorner.BottomLeft, out corner),
            "bottomright" => SetC(ScreenCorner.BottomRight, out corner),
            _ => false
        };
    }

    private static bool Set(ScreenEdge value, out ScreenEdge edge)
    {
        edge = value;
        return true;
    }

    private static bool SetA(AnchorKind value, out AnchorKind anchor)
    {
        anchor = value;
        return true;
    }

    private static bool SetC(ScreenCorner value, out ScreenCorner corner)
    {
        corner = value;
        return true;
    }
}

public sealed class EdgeMachine
{
    private readonly List<DevicePlacement> _devices = new();
    private readonly List<HostMonitor> _monitors = new();
    private double _subX;
    private double _subY;

    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public int ScreenW { get; private set; } = 1920;
    public int ScreenH { get; private set; } = 1080;
    public bool Locked { get; set; }
    public string? ActiveDeviceId { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public bool ReturnArmed { get; private set; }
    public IReadOnlyList<DevicePlacement> Devices => _devices;

    /// <summary>
    /// Uniform (1:1 scale) arrangement: every attach edge is the monitor's FULL edge, so the whole height
    /// (or width) of one screen maps proportionally onto the whole height of the next, ignoring pixel counts and offsets.
    /// </summary>
    public bool UniformScale { get; set; }

    /// <summary>Explicit monitors; when none were set the whole SetScreen rect acts as one primary monitor.</summary>
    public IReadOnlyList<HostMonitor> Monitors => _monitors.Count > 0
        ? _monitors
        : new[] { new HostMonitor("", OriginX, OriginY, ScreenW, ScreenH, 96, true) };

    public void SetMonitors(IReadOnlyList<HostMonitor> monitors)
    {
        _monitors.Clear();
        _monitors.AddRange(monitors.Where(m => m.W > 0 && m.H > 0));
        if (_monitors.Count == 0)
            return;
        var left = _monitors.Min(m => m.X);
        var top = _monitors.Min(m => m.Y);
        SetScreen(left, top, _monitors.Max(m => m.Right) - left, _monitors.Max(m => m.Bottom) - top);
        if (ActiveDeviceId == null)
            (X, Y) = ClampHost(X, Y, X, Y);
    }

    /// <summary>Monitor a device is attached to; unknown ids (unplugged) fall back to the primary.</summary>
    public HostMonitor MonitorFor(DevicePlacement device)
    {
        var all = Monitors;
        foreach (var m in all)
            if (device.MonitorId.Length > 0 && m.Id == device.MonitorId)
                return m;
        return all.FirstOrDefault(m => m.Primary) ?? all[0];
    }

    /// <summary>
    /// The monitor directly across the device's attach edge (the device sits BETWEEN two monitors).
    /// Leaving the device's far side then lands on this monitor's facing edge.
    /// </summary>
    public HostMonitor? NeighborFor(DevicePlacement device)
    {
        if (device.Anchor != AnchorKind.Edge)
            return null;
        var m = MonitorFor(device);
        var (lo, hi) = Segment(device, m);
        foreach (var n in Monitors)
        {
            if (n == m)
                continue;
            var touches = device.PcEdge switch
            {
                ScreenEdge.Right => n.X == m.Right && n.Y < hi && n.Bottom > lo,
                ScreenEdge.Left => n.Right == m.X && n.Y < hi && n.Bottom > lo,
                ScreenEdge.Bottom => n.Y == m.Bottom && n.X < hi && n.Right > lo,
                _ => n.Bottom == m.Y && n.X < hi && n.Right > lo
            };
            if (touches)
                return n;
        }
        return null;
    }

    /// <summary>Absolute [lo, hi) of the crossing zone along the attach edge, clipped to the monitor.</summary>
    public (int Lo, int Hi) Segment(DevicePlacement device, HostMonitor m)
    {
        var vertical = device.PcEdge is ScreenEdge.Left or ScreenEdge.Right;
        var start = vertical ? m.Y : m.X;
        var len = vertical ? m.H : m.W;
        if (UniformScale)
            return (start, start + len);
        var lo = start + Math.Clamp(device.OffsetPx, 0, Math.Max(0, len - 1));
        var hi = Math.Min(start + len, lo + EffectiveSpan(device));
        return (lo, hi);
    }

    private (int X, int Y) ClampHost(int x, int y, int fromX, int fromY)
    {
        var all = Monitors;
        foreach (var m in all)
            if (m.Contains(x, y))
                return (x, y);
        var home = all.FirstOrDefault(m => m.Contains(fromX, fromY))
            ?? all.OrderBy(m => Dist(m, x, y)).First();
        return (Clamp(x, home.X, home.Right - 1), Clamp(y, home.Y, home.Bottom - 1));
    }

    private static long Dist(HostMonitor m, int x, int y)
    {
        long dx = x < m.X ? m.X - x : x >= m.Right ? x - m.Right + 1 : 0;
        long dy = y < m.Y ? m.Y - y : y >= m.Bottom ? y - m.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }

    public void SetScreen(int originX, int originY, int width, int height)
    {
        OriginX = originX;
        OriginY = originY;
        ScreenW = Math.Max(1, width);
        ScreenH = Math.Max(1, height);
        if (ActiveDeviceId == null)
        {
            X = Clamp(X, OriginX, OriginX + ScreenW - 1);
            Y = Clamp(Y, OriginY, OriginY + ScreenH - 1);
        }
    }

    public void SetDevices(IReadOnlyList<DevicePlacement> devices)
    {
        _devices.Clear();
        _devices.AddRange(devices);
        if (ActiveDeviceId != null && Find(ActiveDeviceId) == null)
            ForceReturn(OriginX + ScreenW / 2, OriginY + ScreenH / 2);
    }

    public bool WouldEnter(int x, int y, out string deviceId)
    {
        deviceId = "";
        if (ActiveDeviceId != null || Locked)
            return false;
        var hit = Hit(x, y);
        if (hit == null)
            return false;
        deviceId = hit.Id;
        return true;
    }

    public Transition MoveHost(int x, int y)
    {
        if (ActiveDeviceId != null)
            return new Transition.Stay(X, Y);
        var (cx, cy) = ClampHost(x, y, X, Y);
        if (Locked)
        {
            X = cx;
            Y = cy;
            return new Transition.Blocked(cx, cy);
        }
        var hit = Hit(x, y);
        if (hit == null)
        {
            X = cx;
            Y = cy;
            return new Transition.Stay(cx, cy);
        }
        var (ex, ey) = Entry(hit, x, y);
        ActiveDeviceId = hit.Id;
        X = ex;
        Y = ey;
        ReturnArmed = false;
        _subX = _subY = 0;
        return new Transition.Enter(hit.Id, ex, ey, hit.ReturnEdge, hit.ScreenWidth, hit.ScreenHeight, cx, cy);
    }

    public Transition MoveDeviceBy(int dx, int dy)
    {
        if (ActiveDeviceId == null)
            return new Transition.Stay(X, Y);
        var device = Find(ActiveDeviceId);
        if (device == null)
            return ForceReturn(OriginX + ScreenW / 2, OriginY + ScreenH / 2);
        var speed = Math.Clamp(device.PointerSpeed, 0.1, 8);
        _subX += dx * speed;
        _subY += dy * speed;
        var sx = (int)Math.Truncate(_subX);
        var sy = (int)Math.Truncate(_subY);
        _subX -= sx;
        _subY -= sy;
        var nx = X + sx;
        var ny = Y + sy;
        if (IsInsideArm(device, nx, ny) || IsInsideArm(device, X, Y))
            ReturnArmed = true;
        if (!Locked && ReturnArmed && EdgeGuard.Crosses(device.ReturnEdge, nx, ny, device.ScreenWidth, device.ScreenHeight))
        {
            MapBack(device, X, Y, out var hostX, out var hostY);
            ActiveDeviceId = null;
            ReturnArmed = false;
            _subX = _subY = 0;
            X = hostX;
            Y = hostY;
            return new Transition.Return(hostX, hostY, nx, ny, MonitorFor(device).Id);
        }
        // Device between two monitors: its far side continues onto the neighbour's facing edge.
        if (!Locked && NeighborFor(device) is { } next
            && EdgeGuard.Crosses(Opposite(device.ReturnEdge), nx, ny, device.ScreenWidth, device.ScreenHeight))
        {
            MapThrough(device, next, X, Y, out var hostX, out var hostY);
            ActiveDeviceId = null;
            ReturnArmed = false;
            _subX = _subY = 0;
            X = hostX;
            Y = hostY;
            return new Transition.Return(hostX, hostY, nx, ny, next.Id, true);
        }
        X = Clamp(nx, 0, device.ScreenWidth - 1);
        Y = Clamp(ny, 0, device.ScreenHeight - 1);
        return new Transition.Stay(X, Y);
    }

    public void SetHostCursor(int x, int y)
    {
        if (ActiveDeviceId != null)
            return;
        (X, Y) = ClampHost(x, y, X, Y);
    }

    public Transition ForceEnter(string id) =>
        ForceEnterAt(id, CenterOf(id).X, CenterOf(id).Y, OriginX + ScreenW / 2, OriginY + ScreenH / 2);

    public Transition ForceEnterAt(string id, int x, int y, int parkX, int parkY)
    {
        var device = Find(id);
        if (device == null)
            return new Transition.Stay(X, Y);
        if (Locked)
            return new Transition.Blocked(X, Y);
        ActiveDeviceId = id;
        X = Clamp(x, 0, device.ScreenWidth - 1);
        Y = Clamp(y, 0, device.ScreenHeight - 1);
        ReturnArmed = false;
        _subX = _subY = 0;
        return new Transition.Enter(id, X, Y, device.ReturnEdge, device.ScreenWidth, device.ScreenHeight, parkX, parkY);
    }

    private (int X, int Y) CenterOf(string id)
    {
        var device = Find(id);
        if (device == null)
            return (0, 0);
        return (Math.Max(0, device.ScreenWidth / 2), Math.Max(0, device.ScreenHeight / 2));
    }

    public Transition ForceReturn(int hostX, int hostY)
    {
        ActiveDeviceId = null;
        ReturnArmed = false;
        _subX = _subY = 0;
        (X, Y) = ClampHost(hostX, hostY, hostX, hostY);
        return new Transition.Return(X, Y, 0, 0);
    }

    public Transition ReturnFromCurrent()
    {
        if (ActiveDeviceId == null)
            return new Transition.Stay(X, Y);
        var device = Find(ActiveDeviceId);
        if (device == null)
            return ForceReturn(OriginX, OriginY);
        MapBack(device, X, Y, out var hostX, out var hostY);
        return ForceReturn(hostX, hostY);
    }

    private DevicePlacement? Hit(int x, int y)
    {
        DevicePlacement? corner = null;
        foreach (var device in _devices)
        {
            if (device.Anchor == AnchorKind.Corner && MatchesCorner(device, x, y))
            {
                if (corner == null || string.CompareOrdinal(device.Id, corner.Id) < 0)
                    corner = device;
            }
        }
        if (corner != null)
            return corner;
        DevicePlacement? edge = null;
        foreach (var device in _devices)
        {
            if (device.Anchor != AnchorKind.Edge || !MatchesEdge(device, x, y))
                continue;
            if (edge == null
                || device.OffsetPx < edge.OffsetPx
                || (device.OffsetPx == edge.OffsetPx && string.CompareOrdinal(device.Id, edge.Id) < 0))
                edge = device;
        }
        return edge;
    }

    private bool MatchesCorner(DevicePlacement device, int x, int y)
    {
        var size = Math.Max(1, device.CornerSizePx);
        var mon = MonitorFor(device);
        var left = mon.X;
        var top = mon.Y;
        var right = mon.Right;
        var bottom = mon.Bottom;
        return device.Corner switch
        {
            ScreenCorner.TopLeft => x < left + size && y < top + size && (x < left || y < top),
            ScreenCorner.TopRight => x >= right - size && y < top + size && (x >= right || y < top),
            ScreenCorner.BottomLeft => x < left + size && y >= bottom - size && (x < left || y >= bottom),
            ScreenCorner.BottomRight => x >= right - size && y >= bottom - size && (x >= right || y >= bottom),
            _ => false
        };
    }

    private bool MatchesEdge(DevicePlacement device, int x, int y)
    {
        // Virtual-desktop coordinates: the pointer must come from inside the device's monitor and step past
        // its attach edge (also when another monitor continues there - the device sits between them).
        var m = MonitorFor(device);
        if (_monitors.Count > 1 && !m.Contains(X, Y))
            return false;
        var crossed = device.PcEdge switch
        {
            ScreenEdge.Left => x < m.X,
            ScreenEdge.Right => x >= m.Right,
            ScreenEdge.Top => y < m.Y,
            ScreenEdge.Bottom => y >= m.Bottom,
            _ => false
        };
        if (!crossed)
            return false;
        var along = device.PcEdge is ScreenEdge.Left or ScreenEdge.Right ? y : x;
        var (lo, hi) = Segment(device, m);
        return along >= lo && along < hi;
    }

    private (int X, int Y) Entry(DevicePlacement device, int x, int y)
    {
        if (device.Anchor == AnchorKind.Corner)
            return (Math.Max(0, device.ScreenWidth / 2), Math.Max(0, device.ScreenHeight / 2));
        var m = MonitorFor(device);
        var (lo, hi) = Segment(device, m);
        var alongPc = device.PcEdge is ScreenEdge.Left or ScreenEdge.Right ? y : x;
        var relative = Math.Max(0, alongPc - lo);
        var side = AlongLength(device);
        var span = Math.Max(1, hi - lo);
        relative = Clamp(relative, 0, Math.Max(0, span - 1));
        var along = Scale(relative, span, side);
        return Place(device, along);
    }

    private static (int X, int Y) Place(DevicePlacement device, int along)
    {
        const int inset = 2;
        var w = device.ScreenWidth;
        var h = device.ScreenHeight;
        along = device.ReturnEdge is ScreenEdge.Left or ScreenEdge.Right
            ? Clamp(along, 0, h - 1)
            : Clamp(along, 0, w - 1);
        return device.ReturnEdge switch
        {
            ScreenEdge.Left => (Math.Min(inset, w - 1), along),
            ScreenEdge.Right => (Math.Max(0, w - 1 - inset), along),
            ScreenEdge.Top => (along, Math.Min(inset, h - 1)),
            _ => (along, Math.Max(0, h - 1 - inset))
        };
    }

    private void MapBack(DevicePlacement device, int devX, int devY, out int hostX, out int hostY)
    {
        var m = MonitorFor(device);
        if (device.Anchor == AnchorKind.Corner)
        {
            hostX = device.Corner is ScreenCorner.TopRight or ScreenCorner.BottomRight ? m.Right - 1 : m.X;
            hostY = device.Corner is ScreenCorner.BottomLeft or ScreenCorner.BottomRight ? m.Bottom - 1 : m.Y;
            return;
        }
        var along = AlongOnSegment(device, m, devX, devY);
        switch (device.PcEdge)
        {
            case ScreenEdge.Left:
                hostX = m.X;
                hostY = Clamp(along, m.Y, m.Bottom - 1);
                break;
            case ScreenEdge.Right:
                hostX = m.Right - 1;
                hostY = Clamp(along, m.Y, m.Bottom - 1);
                break;
            case ScreenEdge.Top:
                hostY = m.Y;
                hostX = Clamp(along, m.X, m.Right - 1);
                break;
            default:
                hostY = m.Bottom - 1;
                hostX = Clamp(along, m.X, m.Right - 1);
                break;
        }
    }

    private int AlongOnSegment(DevicePlacement device, HostMonitor m, int devX, int devY)
    {
        var alongDev = device.ReturnEdge is ScreenEdge.Left or ScreenEdge.Right ? devY : devX;
        var (lo, hi) = Segment(device, m);
        return lo + Scale(alongDev, AlongLength(device), Math.Max(1, hi - lo));
    }

    /// <summary>Leaving the far side of a device placed between monitors: land on the neighbour's facing edge.</summary>
    private void MapThrough(DevicePlacement device, HostMonitor next, int devX, int devY, out int hostX, out int hostY)
    {
        var along = AlongOnSegment(device, MonitorFor(device), devX, devY);
        if (UniformScale)
        {
            var alongDev = device.ReturnEdge is ScreenEdge.Left or ScreenEdge.Right ? devY : devX;
            var vertical = device.PcEdge is ScreenEdge.Left or ScreenEdge.Right;
            along = (vertical ? next.Y : next.X) + Scale(alongDev, AlongLength(device), vertical ? next.H : next.W);
        }
        switch (device.PcEdge)
        {
            case ScreenEdge.Right:
                hostX = next.X;
                hostY = Clamp(along, next.Y, next.Bottom - 1);
                break;
            case ScreenEdge.Left:
                hostX = next.Right - 1;
                hostY = Clamp(along, next.Y, next.Bottom - 1);
                break;
            case ScreenEdge.Bottom:
                hostY = next.Y;
                hostX = Clamp(along, next.X, next.Right - 1);
                break;
            default:
                hostY = next.Bottom - 1;
                hostX = Clamp(along, next.X, next.Right - 1);
                break;
        }
    }

    private static ScreenEdge Opposite(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => ScreenEdge.Right,
        ScreenEdge.Right => ScreenEdge.Left,
        ScreenEdge.Top => ScreenEdge.Bottom,
        _ => ScreenEdge.Top
    };

    private static bool IsInsideArm(DevicePlacement device, int x, int y)
    {
        const int margin = 8;
        if (device.ScreenWidth <= margin * 2 || device.ScreenHeight <= margin * 2)
            return true;
        return device.ReturnEdge switch
        {
            ScreenEdge.Left => x >= margin,
            ScreenEdge.Right => x <= device.ScreenWidth - 1 - margin,
            ScreenEdge.Top => y >= margin,
            _ => y <= device.ScreenHeight - 1 - margin
        };
    }

    private static int AlongLength(DevicePlacement device) =>
        device.ReturnEdge is ScreenEdge.Left or ScreenEdge.Right ? device.ScreenHeight : device.ScreenWidth;

    private static int EffectiveSpan(DevicePlacement device)
    {
        var side = device.PcEdge is ScreenEdge.Left or ScreenEdge.Right ? device.ScreenHeight : device.ScreenWidth;
        if (device.Anchor == AnchorKind.Edge && device.SpanPx <= 0)
            side = AlongLength(device);
        return device.SpanPx > 0 ? device.SpanPx : side;
    }

    private static int Scale(int value, int fromSpan, int toSpan)
    {
        if (toSpan <= 1 || fromSpan <= 1)
            return 0;
        value = Clamp(value, 0, fromSpan - 1);
        if (fromSpan == toSpan)
            return value;
        var scaled = (int)Math.Round(value * (toSpan - 1) / (double)(fromSpan - 1));
        return Clamp(scaled, 0, toSpan - 1);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max < min)
            return min;
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    private DevicePlacement? Find(string id)
    {
        foreach (var device in _devices)
        {
            if (device.Id == id)
                return device;
        }
        return null;
    }
}
