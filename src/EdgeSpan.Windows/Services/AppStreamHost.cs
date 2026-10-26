using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EdgeSpan.Input;
using EdgeSpan.Layout;
using EdgeSpan.Product;
using EdgeSpan.Protocol;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Android-to-Windows: the phone dials TCP 27186 (adb reverse on USB, the paired address on Wi-Fi),
/// streams one app as H.264 and gets mouse and keys back. Each app gets its own window here.
/// Dragging that window back across the phone's edge, or closing it, returns the app to the phone.
/// </summary>
public sealed class AppStreamHost : IDisposable
{
    private readonly HostController _host;
    private TcpListener? _listener;
    private Thread? _thread;
    private volatile bool _running;

    public AppStreamHost(HostController host) => _host = host;

    public void Start()
    {
        if (_running || !OperatingSystem.IsWindows())
            return;
        _running = true;
        _thread = new Thread(Listen) { IsBackground = true, Name = "edgestpan-appstream" };
        _thread.Start();
    }

    private void Listen()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, ProductInfo.AppStreamPort);
            _listener.Start();
            DiagLog.Write("appstream listening " + ProductInfo.AppStreamPort);
            while (_running)
            {
                var client = _listener.AcceptTcpClient();
                var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (peer == null || !(IPAddress.IsLoopback(peer) || _host.IsPairedAddress(peer)))
                {
                    DiagLog.Write($"appstream rejected {peer}");
                    client.Close();
                    continue;
                }
                new Thread(() => Serve(client, peer)) { IsBackground = true, Name = "edgestpan-appstream-session" }.Start();
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (_running)
                DiagLog.Write("appstream listener stopped " + ex.Message);
        }
    }

    private void Serve(TcpClient client, IPAddress peer)
    {
        AppStreamWindow? window = null;
        H264Decoder? decoder = null;
        try
        {
            client.NoDelay = true;
            client.ReceiveBufferSize = 1 << 20;
            var stream = client.GetStream();
            var head = new byte[4];
            stream.ReadTimeout = 5000;
            stream.ReadExactly(head);
            var n = BinaryPrimitives.ReadInt32LittleEndian(head);
            if (n is < 20 or > 2048)
                return;
            var body = new byte[n];
            stream.ReadExactly(body);
            if (!AppStreamWire.TryHello(body, out var hello))
            {
                DiagLog.Write("appstream bad hello");
                return;
            }
            var allowed = HandoffPolicy.AllowsPhoneToPc(_host.Settings.Handoff);
            var recvKey = _host.MediaReceiveKeyFor(hello.DeviceId);
            var sendKey = _host.MediaKeyFor(hello.DeviceId);
            var startSeq = (BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) >> 16) + 1;
            var ok = allowed && recvKey != null && sendKey != null;
            DiagLog.Write($"appstream hello device={hello.DeviceId} {hello.Width}x{hello.Height} pkg={hello.Package} via={(IPAddress.IsLoopback(peer) ? "usb" : "wifi")} allowed={allowed} keys={recvKey != null} accept={ok}");
            stream.Write(AppStreamWire.WriteReply(ok, startSeq));
            if (!ok)
                return;
            stream.ReadTimeout = Timeout.Infinite;
            decoder = H264Decoder.Create();
            if (decoder == null)
                return;
            var outSeq = startSeq;
            var gate = new object();
            void Send(AppStreamWire.Input input)
            {
                try
                {
                    lock (gate)
                        stream.Write(MediaFrames.Protect(sendKey!, outSeq++, 1, 0, 0, 0, AppStreamWire.WriteInput(input)));
                }
                catch
                {
                }
            }
            var lang = _host.Settings.Language;
            var edge = ScreenEdge.Right;
            var placed = _host.Settings.Devices.FirstOrDefault(d => d.Id == hello.DeviceId);
            if (placed != null && Enum.TryParse<ScreenEdge>(placed.PcEdge, true, out var parsed))
                edge = parsed;
            var settings = _host.Settings;
            var created = new ManualResetEventSlim();
            Dispatcher.UIThread.Post(() =>
            {
                window = new AppStreamWindow(hello, lang, Send, edge, settings.ScreenOriginX, settings.ScreenOriginY, settings.ScreenWidth, settings.ScreenHeight);
                window.Show();
                created.Set();
            });
            created.Wait(5000);
            var seq = hello.StartSeq;
            var frames = 0;
            var blockedRun = 0;
            while (window is { IsGone: false })
            {
                stream.ReadExactly(head);
                var len = BinaryPrimitives.ReadInt32LittleEndian(head);
                if (len is < 16 or > 8_000_000)
                    break;
                var packet = new byte[4 + len];
                head.CopyTo(packet, 0);
                stream.ReadExactly(packet, 4, len);
                if (!MediaFrames.Open(recvKey!, seq++, packet, out _, out _, out _, out _, out var annexb))
                {
                    DiagLog.Write("appstream frame failed to open seq=" + (seq - 1));
                    continue;
                }
                var bgra = decoder.Decode(annexb);
                if (bgra == null)
                    continue;
                frames++;
                blockedRun = HandoffPolicy.LooksBlocked(decoder.Mean, decoder.Spread) ? blockedRun + 1 : 0;
                if (frames == 1)
                    DiagLog.Write($"appstream first frame {decoder.Width}x{decoder.Height}");
                var w = decoder.Width;
                var h = decoder.Height;
                var blocked = blockedRun >= 20;
                if (blockedRun == 20)
                    DiagLog.Write($"appstream looks capture-blocked mean={decoder.Mean:F1} spread={decoder.Spread:F1}");
                Dispatcher.UIThread.Post(() => window?.Present(bgra, w, h, blocked));
            }
            DiagLog.Write($"appstream session ended frames={frames}");
        }
        catch (Exception ex)
        {
            DiagLog.Write("appstream session " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            decoder?.Dispose();
            client.Close();
            Dispatcher.UIThread.Post(() => window?.CloseQuietly());
        }
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _listener?.Stop();
        }
        catch
        {
        }
    }
}

internal sealed class AppStreamWindow : Window
{
    private readonly Action<AppStreamWire.Input> _send;
    private readonly AppStreamWire.Hello _hello;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Border _notice;
    private readonly TextBlock _noticeText;
    private readonly string _lang;
    private readonly ScreenEdge _edge;
    private readonly int _ox, _oy, _sw, _sh;
    private WriteableBitmap? _bitmap;
    private int _w, _h;
    private bool _closeSent;
    private DateTime _opened = DateTime.UtcNow;

    public bool IsGone { get; private set; }

    public AppStreamWindow(AppStreamWire.Hello hello, string? lang, Action<AppStreamWire.Input> send, ScreenEdge edge, int ox, int oy, int sw, int sh)
    {
        _hello = hello;
        _send = send;
        _lang = lang ?? "en";
        _edge = edge;
        (_ox, _oy, _sw, _sh) = (ox, oy, Math.Max(sw, 1), Math.Max(sh, 1));
        _w = hello.Width;
        _h = hello.Height;
        Title = string.IsNullOrWhiteSpace(hello.Title) ? hello.Package : hello.Title;
        Background = new SolidColorBrush(Color.Parse("#101114"));
        CanResize = true;
        // Phone-shaped window that fits comfortably on the PC screen.
        var maxH = 760.0;
        var scale = Math.Min(1.0, maxH / Math.Max(1, (int)hello.Height));
        Width = Math.Max(240, hello.Width * scale);
        Height = Math.Max(240, hello.Height * scale) + 40;
        Focusable = true;
        _noticeText = new TextBlock
        {
            Text = Catalog.Get(_lang, "appStreamWaiting"),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center
        };
        _notice = new Border { Background = new SolidColorBrush(Color.Parse("#E0101114")), Child = _noticeText };
        var back = new Button { Content = "◀", Margin = new Thickness(6, 4) };
        back.Click += (_, _) => _send(new AppStreamWire.Input(AppStreamWire.InputBack, 0, 0, 0, false, 0, 0, 0, ""));
        var home = new Button { Content = Catalog.Get(_lang, "appStreamBack"), Margin = new Thickness(6, 4) };
        home.Click += (_, _) => ReturnToPhone("button");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Children = { back, home }, Height = 40 };
        var surface = new Grid { Children = { _image, _notice } };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(surface);
        Content = root;

        _image.PointerMoved += (_, e) => Pointer(e, AppStreamWire.InputMove, 0, false);
        _image.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(_image).Properties;
            var button = props.IsRightButtonPressed ? (byte)2 : props.IsMiddleButtonPressed ? (byte)3 : (byte)1;
            Pointer(e, AppStreamWire.InputButton, button, true);
            e.Pointer.Capture(_image);
        };
        _image.PointerReleased += (_, e) =>
        {
            var button = e.InitialPressMouseButton switch { MouseButton.Right => (byte)2, MouseButton.Middle => (byte)3, _ => (byte)1 };
            Pointer(e, AppStreamWire.InputButton, button, false);
        };
        _image.PointerWheelChanged += (_, e) =>
        {
            var (x, y) = Map(e.GetPosition(_image));
            _send(new AppStreamWire.Input(AppStreamWire.InputWheel, x, y, 0, false, e.Delta.Y > 0 ? 1 : -1, 0, 0, ""));
        };
        KeyDown += (_, e) => Key(e, true);
        KeyUp += (_, e) => Key(e, false);
        PositionChanged += (_, _) => CheckDragBack();
        Closing += (_, _) => ReturnToPhone("closed");
    }

    private void Pointer(PointerEventArgs e, byte kind, byte button, bool down)
    {
        var (x, y) = Map(e.GetPosition(_image));
        _send(new AppStreamWire.Input(kind, x, y, button, down, 0, 0, 0, ""));
    }

    private void Key(KeyEventArgs e, bool down)
    {
        var vk = AppStreamKeys.ToVk(e.Key);
        if (vk == 0 || !KeyMap.TryHid(vk, out var hid))
            return;
        _send(new AppStreamWire.Input(AppStreamWire.InputKey, 0, 0, 0, down, 0, vk, hid, ""));
        e.Handled = true;
    }

    /// <summary>Maps a point on the letterboxed image to phone pixels.</summary>
    private (int X, int Y) Map(Point p)
    {
        var bw = _image.Bounds.Width;
        var bh = _image.Bounds.Height;
        if (bw <= 0 || bh <= 0 || _w <= 0 || _h <= 0)
            return (0, 0);
        var s = Math.Min(bw / _w, bh / _h);
        var dx = (bw - _w * s) / 2;
        var dy = (bh - _h * s) / 2;
        var x = (int)Math.Round((p.X - dx) / s);
        var y = (int)Math.Round((p.Y - dy) / s);
        return (Math.Clamp(x, 0, _w - 1), Math.Clamp(y, 0, _h - 1));
    }

    public void Present(byte[] bgra, int w, int h, bool blocked)
    {
        if (IsGone || w <= 0 || h <= 0 || bgra.Length < w * h * 4)
            return;
        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
        }
        _w = w;
        _h = h;
        using (var fb = _bitmap.Lock())
        {
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        }
        _image.InvalidateVisual();
        _notice.IsVisible = blocked;
        if (blocked)
            _noticeText.Text = Catalog.Get(_lang, "appStreamBlocked");
    }

    private void CheckDragBack()
    {
        if (_closeSent || (DateTime.UtcNow - _opened).TotalMilliseconds < 800)
            return;
        var scale = RenderScaling;
        var pw = (int)(Bounds.Width * scale);
        var ph = (int)(Bounds.Height * scale);
        var past = HandoffPolicy.PastEdge(_edge, Position.X, Position.Y, pw, ph, _ox, _oy, _sw, _sh);
        var needed = Math.Min(160, (_edge is ScreenEdge.Left or ScreenEdge.Right ? pw : ph) / 3);
        if (past >= needed)
        {
            DiagLog.Write($"appstream drag back past={past}px edge={_edge}");
            ReturnToPhone("drag");
            Close();
        }
    }

    private void ReturnToPhone(string why)
    {
        if (_closeSent)
            return;
        _closeSent = true;
        DiagLog.Write($"appstream return to phone ({why}) pkg={_hello.Package}");
        _send(new AppStreamWire.Input(AppStreamWire.InputClose, 0, 0, 0, false, 0, 0, 0, ""));
        IsGone = true;
        if (why == "button")
            Close();
    }

    public void CloseQuietly()
    {
        _closeSent = true;
        IsGone = true;
        Close();
    }
}

internal static class AppStreamKeys
{
    public static int ToVk(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return 0x41 + (key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return 0x30 + (key - Key.D0);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return 0x60 + (key - Key.NumPad0);
        if (key >= Key.F1 && key <= Key.F12) return 0x70 + (key - Key.F1);
        return key switch
        {
            Key.Enter => 0x0D, Key.Back => 0x08, Key.Tab => 0x09, Key.Escape => 0x1B, Key.Space => 0x20,
            Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
            Key.Delete => 0x2E, Key.Insert => 0x2D, Key.Home => 0x24, Key.End => 0x23, Key.PageUp => 0x21, Key.PageDown => 0x22,
            Key.LeftCtrl => 0xA2, Key.RightCtrl => 0xA3, Key.LeftShift => 0xA0, Key.RightShift => 0xA1,
            Key.LeftAlt => 0xA4, Key.RightAlt => 0xA5, Key.LWin => 0x5B, Key.RWin => 0x5C,
            Key.OemSemicolon => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD, Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF, Key.OemTilde => 0xC0, Key.OemOpenBrackets => 0xDB, Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD, Key.OemQuotes => 0xDE, Key.CapsLock => 0x14,
            _ => 0
        };
    }
}
