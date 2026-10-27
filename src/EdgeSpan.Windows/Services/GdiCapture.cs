using System.Runtime.InteropServices;
using EdgeSpan.Media;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Last-resort window capture when Windows.Graphics.Capture is missing, refused or only gives black frames:
/// PrintWindow(PW_RENDERFULLCONTENT) into a DIB, falling back to BitBlt of the window DC. Sent as JPEG stills.
/// </summary>
internal static class GdiCapture
{
    public static void Loop(nint hwnd, Func<bool> stop, Action<byte[], ulong, byte> publish, Action<string> report, string why)
    {
        DiagLog.Write("handoff stage gdi fallback (" + why + ")");
        report("handoffJpeg");
        var lastHash = 0;
        var sent = 0;
        var failures = 0;
        var started = Environment.TickCount64;
        while (!stop())
        {
            if (WindowEncoder.Overlay?.Invoke() is { } overlayJpeg)
            {
                publish(overlayJpeg, (ulong)(Environment.TickCount64 * 1000), JpegStill.Flag);
                sent++;
                Thread.Sleep(50);
                continue;
            }
            var frame = Grab(hwnd, out var w, out var h, out var how);
            if (frame == null)
            {
                if (++failures == 1 || failures % 50 == 0)
                    DiagLog.Write($"handoff stage gdi grab failed x{failures}");
                if (failures > 100 && sent == 0)
                {
                    report("handoffNoEncoder");
                    return;
                }
                Thread.Sleep(60);
                continue;
            }
            var hash = Hash(frame);
            if (hash != lastHash || Environment.TickCount64 - started > 2000 && sent == 0)
            {
                lastHash = hash;
                var (jw, jh, scaled) = Scale(frame, w, h, 1280);
                publish(JpegStill.Encode(scaled, jw, jh, jw * 4), (ulong)(Environment.TickCount64 * 1000), JpegStill.Flag);
                if (sent++ == 0)
                    DiagLog.Write($"handoff stage gdi first frame {w}x{h} via {how}");
            }
            Thread.Sleep(66);
        }
    }

    /// <summary>One BGRA frame of the window, or null.</summary>
    public static byte[]? Grab(nint hwnd, out int width, out int height, out string how)
    {
        width = height = 0;
        how = "";
        if (!GetWindowRect(hwnd, out var r))
            return null;
        width = Math.Max(0, r.Right - r.Left) & ~1;
        height = Math.Max(0, r.Bottom - r.Top) & ~1;
        if (width < 2 || height < 2 || width > 8192 || height > 8192)
            return null;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        try
        {
            var ok = PrintWindow(hwnd, mem, 2);
            how = "PrintWindow";
            if (!ok)
            {
                var wdc = GetWindowDC(hwnd);
                ok = BitBlt(mem, 0, 0, width, height, wdc, 0, 0, 0x00CC0020);
                ReleaseDC(hwnd, wdc);
                how = "BitBlt";
            }
            if (!ok || bits == IntPtr.Zero)
                return null;
            var data = new byte[width * height * 4];
            Marshal.Copy(bits, data, 0, data.Length);
            for (var i = 3; i < data.Length; i += 4)
                data[i] = 255;
            return data;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Main window plus its popups at their offsets, as a JPEG for the tablet (null if the grab failed).</summary>
    public static byte[]? Composite(nint main, IReadOnlyList<HandoffPopups.Popup> popups)
    {
        var frame = Grab(main, out var w, out var h, out _);
        if (frame == null)
            return null;
        foreach (var p in popups)
        {
            var pop = Grab(p.Hwnd, out var pw, out var ph, out _);
            if (pop == null)
                continue;
            for (var y = 0; y < ph; y++)
            {
                var dy = p.Y + y;
                if (dy < 0 || dy >= h)
                    continue;
                var x0 = Math.Max(0, -p.X);
                var x1 = Math.Min(pw, w - p.X);
                if (x1 <= x0)
                    continue;
                Buffer.BlockCopy(pop, (y * pw + x0) * 4, frame, (dy * w + p.X + x0) * 4, (x1 - x0) * 4);
            }
        }
        var (jw, jh, scaled) = Scale(frame, w, h, 1600);
        return JpegStill.Encode(scaled, jw, jh, jw * 4, 80);
    }

    private static int Hash(byte[] d)
    {
        var h = 17;
        for (var i = 0; i < d.Length; i += 4099)
            h = h * 31 + d[i];
        return h;
    }

    private static (int W, int H, byte[] Data) Scale(byte[] src, int w, int h, int max)
    {
        if (Math.Max(w, h) <= max)
            return (w, h, src);
        var s = (double)max / Math.Max(w, h);
        var nw = Math.Max(2, (int)(w * s) & ~1);
        var nh = Math.Max(2, (int)(h * s) & ~1);
        var dst = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
        {
            var sy = Math.Min(h - 1, (int)(y / s));
            for (var x = 0; x < nw; x++)
            {
                var sx = Math.Min(w - 1, (int)(x / s));
                Buffer.BlockCopy(src, (sy * w + sx) * 4, dst, (y * nw + x) * 4, 4);
            }
        }
        return (nw, nh, dst);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}

internal static class Com
{
    /// <summary>Joins the MTA on a worker thread before raw CoCreateInstance; harmless when already initialised.</summary>
    public static void EnsureMta()
    {
        var hr = CoInitializeEx(IntPtr.Zero, 0);
        if (hr < 0 && hr != unchecked((int)0x80010106))
            DiagLog.Write("com init 0x" + hr.ToString("X8"));
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
}
