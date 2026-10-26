namespace EdgeSpan.Layout;

/// <summary>
/// A handoff starts only while a window is being dragged, and only when the
/// pointer has already crossed or the window itself is at least 80 px past the
/// shared edge. Sliding along the monitor is left alone.
/// </summary>
public static class HandoffPolicy
{
    public const int PastEdgePx = 80;

    public static bool AllowsPcToPhone(string? mode) => mode is "pc" or "both";

    public static bool ShouldStart(bool dragging, bool ownWindow, bool pointerOnDevice, int windowPastEdgePx) =>
        dragging && !ownWindow && (pointerOnDevice || windowPastEdgePx >= PastEdgePx);

    public static int PastEdge(ScreenEdge edge, int windowX, int windowY, int windowW, int windowH, int originX, int originY, int screenW, int screenH)
    {
        var right = originX + Math.Max(screenW, 1);
        var bottom = originY + Math.Max(screenH, 1);
        return edge switch
        {
            ScreenEdge.Right => windowX + windowW - right,
            ScreenEdge.Left => originX - windowX,
            ScreenEdge.Bottom => windowY + windowH - bottom,
            ScreenEdge.Top => originY - windowY,
            _ => 0
        };
    }

    public static ScreenEdge FarthestEdge(int windowX, int windowY, int windowW, int windowH, int originX, int originY, int screenW, int screenH)
    {
        var best = ScreenEdge.Right;
        var past = int.MinValue;
        foreach (var edge in new[] { ScreenEdge.Right, ScreenEdge.Left, ScreenEdge.Bottom, ScreenEdge.Top })
        {
            var n = PastEdge(edge, windowX, windowY, windowW, windowH, originX, originY, screenW, screenH);
            if (n > past)
            {
                past = n;
                best = edge;
            }
        }
        return best;
    }

    /// <summary>
    /// Places the window on the device that sits against <paramref name="pcEdge"/>.
    /// A window 80 px past the PC's right edge hangs 80 px in from the tablet's left.
    /// </summary>
    public static (int X, int Y) MapToDevice(ScreenEdge pcEdge, int windowX, int windowY, int windowW, int windowH, int originX, int originY, int screenW, int screenH, int deviceW, int deviceH, int offset)
    {
        var localX = windowX - originX;
        var localY = windowY - originY;
        return pcEdge switch
        {
            ScreenEdge.Right => (localX - Math.Max(screenW, 1), localY - offset),
            ScreenEdge.Left => (Math.Max(deviceW, 1) + localX - windowW, localY - offset),
            ScreenEdge.Bottom => (localX - offset, localY - Math.Max(screenH, 1)),
            ScreenEdge.Top => (localX - offset, Math.Max(deviceH, 1) + localY - windowH),
            _ => (0, 0)
        };
    }

    public static int Bitrate(int width, int height, string? quality)
    {
        var pixels = (long)Math.Max(width, 1) * Math.Max(height, 1);
        var rate = 10_000_000L * pixels / (1920 * 1080);
        rate = quality switch
        {
            "saver" => rate / 3,
            "smooth" => rate * 2 / 3,
            _ => rate
        };
        return (int)Math.Clamp(rate, 1_500_000, 20_000_000);
    }

    public static int FrameRate(string? quality) => quality == "saver" ? 30 : 60;

    public static (int Width, int Height) EncodeSize(int width, int height, string? quality)
    {
        var w = Math.Max(2, width) & ~1;
        var h = Math.Max(2, height) & ~1;
        if (quality != "smooth" || Math.Max(w, h) <= 1280)
            return (w, h);
        if (w >= h)
            return (1280, Math.Max(2, h * 1280 / w) & ~1);
        return (Math.Max(2, w * 1280 / h) & ~1, 1280);
    }

    public enum DragAtEdge
    {
        None = 0,
        Start = 1,
        TabletNotReady = 2
    }

    /// <summary>
    /// A title-bar drag is the move-size event, or the left button held while that window's rectangle changed.
    /// </summary>
    public static bool IsWindowDrag(bool moveSizeEvent, bool leftHeld, bool windowMoved) =>
        moveSizeEvent || (leftHeld && windowMoved);

    /// <summary>
    /// The drag has reached the shared edge when the pointer is on the last pixel and still moving outward,
    /// or the window itself touches that edge.
    /// </summary>
    public static bool DragReaches(ScreenEdge edge, int cursorX, int cursorY, int rawDx, int rawDy, int originX, int originY, int screenW, int screenH, int windowPastPx)
    {
        var onLast = edge switch
        {
            ScreenEdge.Right => cursorX >= originX + Math.Max(screenW, 1) - 1 && rawDx > 0,
            ScreenEdge.Left => cursorX <= originX && rawDx < 0,
            ScreenEdge.Bottom => cursorY >= originY + Math.Max(screenH, 1) - 1 && rawDy > 0,
            ScreenEdge.Top => cursorY <= originY && rawDy < 0,
            _ => false
        };
        return onLast || windowPastPx >= 0;
    }

    /// <summary>
    /// How far the pointer must be pushed outward, in raw pixels, before a drag
    /// that is already sitting on the edge pixel becomes a handoff. Windows
    /// clamps the cursor to the desktop during a move, so the cursor itself
    /// never goes past the edge.
    /// </summary>
    public const int DragPushPx = 12;

    public static int SpanOf(DevicePlacement device)
    {
        if (device.SpanPx > 0)
            return device.SpanPx;
        return device.PcEdge is ScreenEdge.Left or ScreenEdge.Right ? device.ScreenHeight : device.ScreenWidth;
    }

    /// <summary>
    /// The last two pixels count. A drag at 150% DPI often lands one pixel
    /// inside the pixel Windows reports as the edge.
    /// </summary>
    public static bool OnEdgePixel(ScreenEdge edge, int x, int y, int originX, int originY, int screenW, int screenH, int offset, int span)
    {
        var length = Math.Max(span, 1);
        var along = edge is ScreenEdge.Left or ScreenEdge.Right ? y - originY : x - originX;
        if (along < offset || along >= offset + length)
            return false;
        var slack = 2;
        return edge switch
        {
            ScreenEdge.Right => x >= originX + Math.Max(screenW, 1) - slack,
            ScreenEdge.Left => x <= originX + slack - 1,
            ScreenEdge.Bottom => y >= originY + Math.Max(screenH, 1) - slack,
            ScreenEdge.Top => y <= originY + slack - 1,
            _ => false
        };
    }

    /// <summary>
    /// Adds outward raw motion while the cursor stays on the edge pixel.
    /// Inward motion, or leaving the pixel, clears the accumulated push.
    /// </summary>
    public static int NextPush(int push, ScreenEdge edge, bool onPixel, int rawDx, int rawDy)
    {
        if (!onPixel)
            return 0;
        var outward = edge switch
        {
            ScreenEdge.Right => rawDx,
            ScreenEdge.Left => -rawDx,
            ScreenEdge.Bottom => rawDy,
            ScreenEdge.Top => -rawDy,
            _ => 0
        };
        if (outward < 0)
            return 0;
        if (outward == 0)
            return Math.Max(0, push);
        var sum = (long)Math.Max(0, push) + outward;
        return (int)Math.Min(sum, 100_000);
    }

    public static bool PushReady(int push) => push >= DragPushPx;

    /// <summary>A handoff that has not restored the window yet owns the drag.</summary>
    public static bool AcceptsTrigger(bool inProgress) => !inProgress;

    /// <summary>
    /// A later hello that drops the handoff bit does not erase a capability this peer already advertised.
    /// </summary>
    public static byte KeepAdvertisedCaps(byte previous, byte advertised, byte handoffBit)
    {
        if ((previous & handoffBit) != 0 && (advertised & handoffBit) == 0)
            return (byte)(advertised | handoffBit);
        return advertised;
    }

    public readonly record struct WindowHome(int Left, int Top, int Right, int Bottom);

    public static bool OverlapsScreen(int left, int top, int right, int bottom, int originX, int originY, int screenW, int screenH)
    {
        var screenRight = originX + Math.Max(screenW, 1);
        var screenBottom = originY + Math.Max(screenH, 1);
        return right > originX && left < screenRight && bottom > originY && top < screenBottom;
    }

    /// <summary>
    /// The rectangle read after a failed park is off the desktop. Keep the last on-screen home.
    /// </summary>
    public static WindowHome ChooseHome(WindowHome current, int originX, int originY, int screenW, int screenH, WindowHome? prior)
    {
        if (OverlapsScreen(current.Left, current.Top, current.Right, current.Bottom, originX, originY, screenW, screenH))
            return current;
        return prior ?? current;
    }

    public static DragAtEdge Decide(bool dragging, bool ownWindow, bool reaches, bool tabletReady)
    {
        if (!dragging || ownWindow || !reaches)
            return DragAtEdge.None;
        return tabletReady ? DragAtEdge.Start : DragAtEdge.TabletNotReady;
    }
}
