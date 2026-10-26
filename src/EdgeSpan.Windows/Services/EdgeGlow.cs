using System.Runtime.InteropServices;
using EdgeSpan.Layout;
using EdgeSpan.Product;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Click-through bloom on the screen edge the pointer left.
/// The window lives on its own thread so the glow keeps painting when the
/// settings window is in the tray. The first frame is already visible.
/// </summary>
public sealed class EdgeGlow : IDisposable
{
    private const int ExStyle = 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private readonly object _gate = new();
    private Thread? _thread;
    private ManualResetEventSlim? _ready;
    private int _threadId;
    private IntPtr _hwnd;
    private IntPtr _dc;
    private IntPtr _bitmap;
    private IntPtr _bits;
    private IntPtr _old;
    private byte[] _pixels = Array.Empty<byte>();
    private int _width;
    private int _height;
    private double _phase;
    private bool _alive;
    private readonly Motion.Spring1D _burst = new(Motion.Breakthrough);
    private bool _bursting;
    private bool _release;
    private bool _reduce;
    private int _burstPeak = 80;
    private long _lastTick;
    private bool _quit;
    private Request? _request;
    private WndProc? _wndProc;

    public void Show(GlowBand band, string? color, int intensity)
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (_gate)
            _request = new Request(band, color, intensity, true);
        Ensure();
        Post(1);
    }

    public void Hide()
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (_gate)
        {
            if (_request == null && !_alive)
                return;
            var previous = _request;
            _request = new Request(
                previous?.Band ?? default,
                previous?.Color,
                previous?.Intensity ?? 80,
                false);
        }
        Post(1);
    }

    public void Burst(bool reduceMotion, int intensity)
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (_gate)
        {
            _reduce = reduceMotion;
            _burstPeak = Math.Clamp(intensity, 0, 100);
            _bursting = true;
            _release = false;
            _burst.Nudge(0);
            _burst.Retarget(1);
            _lastTick = 0;
        }
        Ensure();
        Post(1);
    }

    public void Dispose()
    {
        lock (_gate)
            _quit = true;
        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
            PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(1));
        _ready?.Dispose();
    }

    public static int DpiAt(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
            return 96;
        var monitor = MonitorFromPoint(new WinPoint { X = x, Y = y }, 2);
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, 0, out var dpi, out _) != 0 || dpi < 48)
            return 96;
        return (int)dpi;
    }

    private void Ensure()
    {
        lock (_gate)
        {
            if (_thread != null)
                return;
            _ready = new ManualResetEventSlim(false);
            _wndProc = WindowProc;
            _thread = new Thread(Pump) { IsBackground = true, Name = "EdgeSpan.Glow" };
            if (OperatingSystem.IsWindows())
                _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private void Post(uint message)
    {
        if (Volatile.Read(ref _threadId) != 0)
            PostThreadMessage(_threadId, 0x8000 + message, IntPtr.Zero, IntPtr.Zero);
    }

    private void Pump()
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        Volatile.Write(ref _threadId, GetCurrentThreadId());
        _ready?.Set();
        Apply();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == 0x8001)
                Apply();
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        Destroy();
    }

    private void Apply()
    {
        Request request;
        lock (_gate)
        {
            if (_quit || _request is not { } pending)
                return;
            request = pending;
        }
        if (!request.Visible || request.Band.Width < 1 || request.Band.Height < 1)
        {
            Destroy();
            return;
        }
        EnsureWindow(request.Band);
        if (_hwnd == IntPtr.Zero)
            return;
        SetWindowPos(_hwnd, new IntPtr(-1), request.Band.X, request.Band.Y, request.Band.Width, request.Band.Height, 0x0010 | 0x0040);
        Paint(request.Band, request.Color, request.Intensity);
        _alive = true;
        Present(request.Band);
        SetTimer(_hwnd, 1, 33, IntPtr.Zero);
        DiagLog.Write($"glow show hwnd={_hwnd} x={request.Band.X} y={request.Band.Y} w={request.Band.Width} h={request.Band.Height}");
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0x0113 && _alive)
        {
            _phase += 0.11;
            StepBurst();
            Request? request;
            lock (_gate)
                request = _request;
            if (request is { Visible: true } current)
            {
                Paint(current.Band, current.Color, Math.Max(current.Intensity, _bursting ? _burstPeak : 0));
                Present(current.Band);
            }
            if (_hwnd != IntPtr.Zero)
                SetTimer(_hwnd, 1, (uint)(_bursting ? 16 : 33), IntPtr.Zero);
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void EnsureWindow(GlowBand band)
    {
        if (_hwnd == IntPtr.Zero)
        {
            var cls = new WndClass
            {
                lpfnWndProc = _wndProc,
                lpszClassName = "EdgeSpanGlow",
                hbrBackground = IntPtr.Zero
            };
            var atom = RegisterClass(ref cls);
            if (atom == 0)
                DiagLog.Write("glow class " + Marshal.GetLastWin32Error());
            _hwnd = CreateWindowEx(ExStyle, "EdgeSpanGlow", "", WsPopup | WsVisible, band.X, band.Y, band.Width, band.Height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                DiagLog.Write("glow window " + Marshal.GetLastWin32Error());
                return;
            }
        }
        if (_width == band.Width && _height == band.Height && _bitmap != IntPtr.Zero)
            return;
        ReleaseBitmap();
        _width = band.Width;
        _height = band.Height;
        var screen = GetDC(IntPtr.Zero);
        _dc = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader
        {
            biSize = 40,
            biWidth = band.Width,
            biHeight = -band.Height,
            biPlanes = 1,
            biBitCount = 32
        };
        _bitmap = CreateDIBSection(screen, ref header, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_dc, _bitmap);
        ReleaseDC(IntPtr.Zero, screen);
    }

    private void StepBurst()
    {
        bool reduce;
        bool bursting;
        lock (_gate)
        {
            reduce = _reduce;
            bursting = _bursting;
        }
        if (!bursting)
            return;
        var now = Environment.TickCount64;
        var dt = _lastTick == 0 ? 0.016 : Math.Clamp((now - _lastTick) / 1000.0, 0.001, 0.05);
        _lastTick = now;
        if (!_burst.Step(dt, reduce))
            return;
        if (!_release)
        {
            _release = true;
            _burst.Retarget(0);
            return;
        }
        lock (_gate)
            _bursting = false;
    }

    private void Paint(GlowBand band, string? color, int intensity)
    {
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero || _width < 1 || _height < 1)
            return;
        var (r, g, b) = GlowBand.ParseColor(color);
        var peak = Math.Clamp(intensity, 20, 100) / 100.0;
        var bytes = _width * _height * 4;
        if (_pixels.Length != bytes)
            _pixels = new byte[bytes];
        var step = bytes > 180_000 * 4 ? 2 : 1;
        var longSide = Math.Max(_width, _height);
        for (var y = 0; y < _height; y += step)
        {
            for (var x = 0; x < _width; x += step)
            {
                var along = longSide <= 1 ? 0 : (band.Edge is GlowEdge.Left or GlowEdge.Right ? y : x) / (double)(longSide - 1);
                var push = _reduce ? 0 : Math.Clamp(_burst.Value, 0, 1);
                var bow = Math.Sin(along * Math.PI) * push * 0.42;
                var dist = Math.Clamp(EdgeDistance(band.Edge, x, y, _width, _height) - bow, 0, 1);
                var ripple = push > 0.45 && Math.Abs(dist - (push - 0.45) / 0.55) < 0.08 ? 0.35 : 0;
                var fall = Math.Exp(-dist * dist * 5.2);
                var core = dist < 0.08 ? 1 : fall;
                var travel = 0.62 + 0.38 * (0.5 + 0.5 * Math.Sin(along * Math.PI * 4 + _phase));
                var breathe = 0.86 + 0.14 * Math.Sin(_phase * 0.65);
                var alpha = (byte)Math.Clamp((int)(255 * peak * Math.Max(core * 0.45, fall + ripple) * travel * breathe), 0, 255);
                var hot = dist < 0.18 ? 1.0 : 0.82;
                var pb = (byte)Math.Clamp(b * hot * alpha / 255, 0, 255);
                var pg = (byte)Math.Clamp(g * hot * alpha / 255, 0, 255);
                var pr = (byte)Math.Clamp(r * hot * alpha / 255, 0, 255);
                for (var yy = y; yy < Math.Min(_height, y + step); yy++)
                {
                    for (var xx = x; xx < Math.Min(_width, x + step); xx++)
                    {
                        var i = (yy * _width + xx) * 4;
                        _pixels[i] = pb;
                        _pixels[i + 1] = pg;
                        _pixels[i + 2] = pr;
                        _pixels[i + 3] = alpha;
                    }
                }
            }
        }
        Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
    }

    private static double EdgeDistance(GlowEdge edge, int x, int y, int width, int height)
    {
        double t = edge switch
        {
            GlowEdge.Left => x / (double)Math.Max(1, width - 1),
            GlowEdge.Right => (width - 1 - x) / (double)Math.Max(1, width - 1),
            GlowEdge.Top => y / (double)Math.Max(1, height - 1),
            GlowEdge.Bottom => (height - 1 - y) / (double)Math.Max(1, height - 1),
            GlowEdge.TopLeft => Math.Min(x / (double)Math.Max(1, width), y / (double)Math.Max(1, height)),
            GlowEdge.TopRight => Math.Min((width - 1 - x) / (double)Math.Max(1, width), y / (double)Math.Max(1, height)),
            GlowEdge.BottomLeft => Math.Min(x / (double)Math.Max(1, width), (height - 1 - y) / (double)Math.Max(1, height)),
            _ => Math.Min((width - 1 - x) / (double)Math.Max(1, width), (height - 1 - y) / (double)Math.Max(1, height))
        };
        return Math.Clamp(t, 0, 1);
    }

    private void Present(GlowBand band)
    {
        if (_hwnd == IntPtr.Zero || _dc == IntPtr.Zero)
            return;
        var dst = new WinPoint { X = band.X, Y = band.Y };
        var size = new WinSize { Cx = _width, Cy = _height };
        var src = new WinPoint();
        var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        var ok = UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _dc, ref src, 0, ref blend, 2);
        if (!ok)
            DiagLog.Write("glow present " + Marshal.GetLastWin32Error());
        SetWindowPos(_hwnd, new IntPtr(-1), band.X, band.Y, _width, _height, 0x0010 | 0x0040);
        ShowWindow(_hwnd, 8);
    }

    private void Destroy()
    {
        _alive = false;
        ReleaseBitmap();
        if (_hwnd != IntPtr.Zero)
        {
            KillTimer(_hwnd, 1);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _width = 0;
        _height = 0;
    }

    private void ReleaseBitmap()
    {
        if (_dc != IntPtr.Zero && _old != IntPtr.Zero)
            SelectObject(_dc, _old);
        if (_bitmap != IntPtr.Zero)
            DeleteObject(_bitmap);
        if (_dc != IntPtr.Zero)
            DeleteDC(_dc);
        _bitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _dc = IntPtr.Zero;
        _old = IntPtr.Zero;
    }

    private readonly record struct Request(GlowBand Band, string? Color, int Intensity, bool Visible);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public WndProc? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public WinPoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hwnd, nuint id, uint ms, IntPtr proc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, nuint id);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref WinPoint pptDst, ref WinSize psize, IntPtr hdcSrc, ref WinPoint pptSrc, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WinPoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
