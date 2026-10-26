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

public sealed class DevicePlacement
{
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
    public sealed record Return(int HostX, int HostY, int DeviceX, int DeviceY) : Transition;
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
                if (a.Anchor == AnchorKind.Edge && b.Anchor == AnchorKind.Edge && a.PcEdge == b.PcEdge && RangesOverlap(a, b))
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
        var cx = Clamp(x, OriginX, OriginX + ScreenW - 1);
        var cy = Clamp(y, OriginY, OriginY + ScreenH - 1);
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
            return new Transition.Return(hostX, hostY, nx, ny);
        }
        X = Clamp(nx, 0, device.ScreenWidth - 1);
        Y = Clamp(ny, 0, device.ScreenHeight - 1);
        return new Transition.Stay(X, Y);
    }

    public void SetHostCursor(int x, int y)
    {
        if (ActiveDeviceId != null)
            return;
        X = Clamp(x, OriginX, OriginX + ScreenW - 1);
        Y = Clamp(y, OriginY, OriginY + ScreenH - 1);
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
        X = Clamp(hostX, OriginX, OriginX + ScreenW - 1);
        Y = Clamp(hostY, OriginY, OriginY + ScreenH - 1);
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
        var left = OriginX;
        var top = OriginY;
        var right = OriginX + ScreenW;
        var bottom = OriginY + ScreenH;
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
        var crossed = device.PcEdge switch
        {
            ScreenEdge.Left => x < OriginX,
            ScreenEdge.Right => x >= OriginX + ScreenW,
            ScreenEdge.Top => y < OriginY,
            ScreenEdge.Bottom => y >= OriginY + ScreenH,
            _ => false
        };
        if (!crossed)
            return false;
        var along = device.PcEdge is ScreenEdge.Left or ScreenEdge.Right ? y - OriginY : x - OriginX;
        var span = EffectiveSpan(device);
        return along >= device.OffsetPx && along < device.OffsetPx + span;
    }

    private (int X, int Y) Entry(DevicePlacement device, int x, int y)
    {
        if (device.Anchor == AnchorKind.Corner)
            return (Math.Max(0, device.ScreenWidth / 2), Math.Max(0, device.ScreenHeight / 2));
        var alongPc = device.PcEdge is ScreenEdge.Left or ScreenEdge.Right
            ? Clamp(y, OriginY, OriginY + ScreenH - 1) - OriginY
            : Clamp(x, OriginX, OriginX + ScreenW - 1) - OriginX;
        var relative = Math.Max(0, alongPc - device.OffsetPx);
        var side = AlongLength(device);
        var span = EffectiveSpan(device);
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
        if (device.Anchor == AnchorKind.Corner)
        {
            hostX = device.Corner is ScreenCorner.TopRight or ScreenCorner.BottomRight ? OriginX + ScreenW - 1 : OriginX;
            hostY = device.Corner is ScreenCorner.BottomLeft or ScreenCorner.BottomRight ? OriginY + ScreenH - 1 : OriginY;
            return;
        }
        var alongDev = device.ReturnEdge is ScreenEdge.Left or ScreenEdge.Right ? devY : devX;
        var side = AlongLength(device);
        var span = EffectiveSpan(device);
        var relative = Scale(alongDev, side, span);
        var alongPc = device.OffsetPx + relative;
        switch (device.PcEdge)
        {
            case ScreenEdge.Left:
                hostX = OriginX;
                hostY = OriginY + Clamp(alongPc, 0, ScreenH - 1);
                break;
            case ScreenEdge.Right:
                hostX = OriginX + ScreenW - 1;
                hostY = OriginY + Clamp(alongPc, 0, ScreenH - 1);
                break;
            case ScreenEdge.Top:
                hostY = OriginY;
                hostX = OriginX + Clamp(alongPc, 0, ScreenW - 1);
                break;
            default:
                hostY = OriginY + ScreenH - 1;
                hostX = OriginX + Clamp(alongPc, 0, ScreenW - 1);
                break;
        }
    }

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
