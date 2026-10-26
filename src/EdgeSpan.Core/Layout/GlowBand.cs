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
