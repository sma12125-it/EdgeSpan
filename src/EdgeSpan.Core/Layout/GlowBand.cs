using System.Globalization;

namespace EdgeSpan.Layout;

public enum GlowEdge
{
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public readonly record struct GlowBand(int X, int Y, int Width, int Height, GlowEdge Edge)
{
    public static GlowBand For(DevicePlacement device, int originX, int originY, int hostW, int hostH, int thicknessPx)
    {
        var thick = Math.Max(1, thicknessPx);
        if (device.Anchor == AnchorKind.Corner)
        {
            var size = Math.Max(thick, Math.Max(1, device.CornerSizePx));
            return device.Corner switch
            {
                ScreenCorner.TopLeft => new GlowBand(originX, originY, size, size, GlowEdge.TopLeft),
                ScreenCorner.TopRight => new GlowBand(originX + hostW - size, originY, size, size, GlowEdge.TopRight),
                ScreenCorner.BottomLeft => new GlowBand(originX, originY + hostH - size, size, size, GlowEdge.BottomLeft),
                _ => new GlowBand(originX + hostW - size, originY + hostH - size, size, size, GlowEdge.BottomRight)
            };
        }
        var span = device.SpanPx > 0
            ? device.SpanPx
            : device.ReturnEdge is ScreenEdge.Left or ScreenEdge.Right ? Math.Max(1, device.ScreenHeight) : Math.Max(1, device.ScreenWidth);
        var offset = Math.Max(0, device.OffsetPx);
        return device.PcEdge switch
        {
            ScreenEdge.Left => new GlowBand(originX, originY + offset, thick, span, GlowEdge.Left),
            ScreenEdge.Right => new GlowBand(originX + hostW - thick, originY + offset, thick, span, GlowEdge.Right),
            ScreenEdge.Top => new GlowBand(originX + offset, originY, span, thick, GlowEdge.Top),
            _ => new GlowBand(originX + offset, originY + hostH - thick, span, thick, GlowEdge.Bottom)
        };
    }

    /// <summary>Clips a band to its monitor so the line is drawn only on the overlapping part of the edge.</summary>
    public GlowBand ClipTo(HostMonitor m)
    {
        var x1 = Math.Max(X, m.X);
        var y1 = Math.Max(Y, m.Y);
        var x2 = Math.Min(X + Width, m.Right);
        var y2 = Math.Min(Y + Height, m.Bottom);
        return x2 <= x1 || y2 <= y1 ? this : new GlowBand(x1, y1, x2 - x1, y2 - y1, Edge);
    }

    /// <summary>The same band mirrored onto the neighbouring monitor's facing edge (device between monitors).</summary>
    public GlowBand Facing(HostMonitor next) => Edge switch
    {
        GlowEdge.Right => new GlowBand(next.X, Y, Width, Height, GlowEdge.Left).ClipTo(next),
        GlowEdge.Left => new GlowBand(next.Right - Width, Y, Width, Height, GlowEdge.Right).ClipTo(next),
        GlowEdge.Bottom => new GlowBand(X, next.Y, Width, Height, GlowEdge.Top).ClipTo(next),
        GlowEdge.Top => new GlowBand(X, next.Bottom - Height, Width, Height, GlowEdge.Bottom).ClipTo(next),
        _ => this
    };

    public static (byte R, byte G, byte B) ParseColor(string? hex)
    {
        var text = (hex ?? "").Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return ((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
        return (0x3D, 0xDC, 0x84);
    }
}
