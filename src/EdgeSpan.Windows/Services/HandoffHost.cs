using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using EdgeSpan.Layout;
using EdgeSpan.Product;
using EdgeSpan.Protocol;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// One PC window, over USB. The drag hook and the capture thread are not the
/// low-level input hook, and video uses TCP 27184 so a frame cannot stall a click.
/// </summary>
public sealed class HandoffHost : IDisposable
{
    private const uint MoveSizeStart = 0x000A;
    private const uint MoveSizeEnd = 0x000B;
    private const uint OutOfContext = 0x0001;
    private const uint SkipOwnProcess = 0x0002;
    private const uint PmRemove = 0x0001;
    private const int GuiInMoveSize = 0x0002;
    private readonly HostController _host;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Job> _jobs = new();
    private WinEventProc? _proc;
    private IntPtr _hook;
    private Thread? _hookThread;
    private Thread? _listenThread;
    private TcpListener? _listener;
    private volatile Stream? _video;
    private volatile bool _running;
    private nint _dragHwnd;
    private nint _eventHwnd;
    private nint _offeredHwnd;
    private nint _claim;
    private volatile bool _keepDrag;
    private long _upsAtDrag;
    private long _lastWatch;
    private volatile bool _busy;
    private readonly Dictionary<nint, HandoffPolicy.WindowHome> _homes = new();
    private nint _buttonHwnd;
    private RECT _buttonRect;
    private bool _buttonDragging;

    public string StatusKey { get; private set; } = "";

    public HandoffHost(HostController host) => _host = host;

    public void ApplySettings()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (HandoffPolicy.AllowsPcToPhone(_host.Settings.Handoff))
            Start();
        else
            Stop();
    }

    public void OnAccepted(Effect.HandoffAccepted accepted)
    {
        Job? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(accepted.Id, out job))
                return;
            job.ViewW = Math.Max(2, accepted.ViewW);
            job.ViewH = Math.Max(2, accepted.ViewH);
            if (job.Encoder != null)
                return;
            job.Stop = new CancellationTokenSource();
            var token = job.Stop.Token;
            job.Encoder = new Thread(() => Encode(job, token)) { IsBackground = true, Name = "EdgeSpan.Handoff" };
            job.Encoder.Start();
        }
    }

    public void OnInput(Effect.HandoffInput input)
    {
        Job? job;
        lock (_gate)
            _jobs.TryGetValue(input.Id, out job);
        if (job == null || !OperatingSystem.IsWindows())
            return;
        Post(job, input);
    }

    public void OnGeometry(Effect.HandoffGeometry geometry)
    {
        Job? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(geometry.Id, out job))
                return;
            job.ViewW = Math.Max(2, geometry.PhysW);
            job.ViewH = Math.Max(2, geometry.PhysH);
            job.NeedIdr = true;
        }
        if (!OperatingSystem.IsWindows())
            return;
        if (job.Virtual != null)
        {
            DiagLog.Write($"handoff geometry phys={geometry.PhysW}x{geometry.PhysH} (virtual monitor: window stays maximised)");
            return;
        }
        var dpi = (int)GetDpiForWindow(job.Hwnd);
        var (w, h) = HandoffPolicy.WindowSize(geometry.PhysW, geometry.PhysH, geometry.DipW, geometry.DipH, dpi);
        DiagLog.Write($"handoff geometry phys={geometry.PhysW}x{geometry.PhysH} dip={geometry.DipW}x{geometry.DipH} pcDpi={dpi} window={w}x{h}");
        SetWindowPos(job.Hwnd, IntPtr.Zero, 0, 0, w, h, 0x0002 | 0x0004 | 0x0010);
    }

    public void OnState(Effect.HandoffState state)
    {
        Job? job;
        lock (_gate)
            _jobs.TryGetValue(state.Id, out job);
        if (job == null || !OperatingSystem.IsWindows())
            return;
        if (state.Value == HandoffPayloads.StateMinimized)
            PostMessage(job.Hwnd, 0x0112, (IntPtr)0xF020, IntPtr.Zero);
        else if (state.Value == HandoffPayloads.StateClosed)
            Finish(job, close: true, echo: false);
        else if (state.Value == HandoffPayloads.StateRestored)
            ShowWindow(job.Hwnd, 9);
    }

    public void OnReturned(Effect.HandoffReturned returned) => Finish(returned.Id, close: false, echo: false);

    public void OnVideo(Effect.HandoffVideo video)
    {
        var config = new HandoffPayloads.VideoConfig(video.Id, video.Bitrate, video.Fps, video.Idr);
        if (HandoffPayloads.IsStats(config))
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(video.Id, out var j))
                    return;
                var preset = HandoffPolicy.Bitrate(Math.Max(2, j.ViewW), Math.Max(2, j.ViewH), _host.Settings.HandoffQuality);
                var next = HandoffPolicy.NextCap(j.BitrateCap, preset, HandoffPayloads.StatsDelayMs(config), HandoffPayloads.StatsDropped(config));
                if (next != j.BitrateCap)
                    DiagLog.Write($"handoff abr delay={HandoffPayloads.StatsDelayMs(config)}ms drop={HandoffPayloads.StatsDropped(config)} cap {j.BitrateCap} -> {next}");
                j.BitrateCap = next;
            }
            return;
        }
        if (!video.Idr)
            return;
        lock (_gate)
        {
            if (_jobs.TryGetValue(video.Id, out var job))
                job.NeedIdr = true;
        }
    }

    public void Dispose() => Stop();

    private void Start()
    {
        if (_running)
            return;
        _running = true;
        _proc = OnWinEvent;
        _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "EdgeSpan.HandoffHook" };
        _hookThread.Start();
        _listenThread = new Thread(Listen) { IsBackground = true, Name = "EdgeSpan.HandoffListen" };
        _listenThread.Start();
    }

    private void Stop()
    {
        _running = false;
        uint[] ids;
        lock (_gate)
            ids = _jobs.Keys.ToArray();
        foreach (var id in ids)
            Finish(id, close: false, echo: true); // app exit / handoff off: every window comes home, viewers close
        try { _listener?.Stop(); } catch { /* the accept loop exits */ }
        _video = null;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void HookLoop()
    {
        _hook = SetWinEventHook(MoveSizeStart, MoveSizeEnd, IntPtr.Zero, _proc!, 0, 0, OutOfContext | SkipOwnProcess);
        while (_running)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (ProbeDrag())
                PollDrag();
            if (Environment.TickCount64 - _lastWatch > 1000)
            {
                _lastWatch = Environment.TickCount64;
                try { Watch(); } catch (Exception ex) { DiagLog.Write("handoff watch " + ex.Message); }
            }
            Thread.Sleep(8);
        }
        if (_hook != IntPtr.Zero)
            UnhookWinEvent(_hook);
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0)
            return;
        if (evt == MoveSizeStart)
        {
            lock (_gate)
            {
                if (_busy)
                    return;
                _eventHwnd = hwnd;
                _dragHwnd = hwnd;
                _offeredHwnd = 0;
            }
        }
        else if (evt == MoveSizeEnd)
        {
            if (_keepDrag)
            {
                lock (_gate)
                    _eventHwnd = 0;
                DiagLog.Write("handoff movesize ended while the streamed drag continues");
                return;
            }
            lock (_gate)
            {
                _eventHwnd = 0;
                _dragHwnd = 0;
                _offeredHwnd = 0;
                _buttonDragging = false;
            }
        }
    }

    /// <summary>
    /// Once a second: a window whose device link has been down for 3 s comes back to the PC (original place and
    /// size); a window that was closed on the PC side is dropped from the tablet.
    /// </summary>
    private void Watch()
    {
        Job[] jobs;
        lock (_gate)
            jobs = _jobs.Values.ToArray();
        foreach (var job in jobs)
        {
            if (!IsWindow(job.Hwnd))
            {
                DiagLog.Write($"handoff window gone id={job.Id}");
                _host.SendHandoff(job.DeviceId, PacketType.HandoffState, HandoffPayloads.WriteState(new HandoffPayloads.State(job.Id, HandoffPayloads.StateClosed)));
                Interlocked.Exchange(ref job.Restored, 1);
                lock (_gate)
                {
                    _jobs.Remove(job.Id);
                    if (_jobs.Count == 0) _busy = false;
                }
                try { job.Stop?.Cancel(); } catch { }
                PlaceholderCard.Close(job.Id);
                continue;
            }
            if (_host.LinkLive(job.DeviceId))
            {
                job.LinkLostAt = 0;
                continue;
            }
            if (job.LinkLostAt == 0)
                job.LinkLostAt = Environment.TickCount64;
            else if (Environment.TickCount64 - job.LinkLostAt > 3000)
            {
                PlaceholderCard.Close(job.Id);
                PutBack(job, "link-lost");
            }
        }
    }

    /// <summary>Panic: end any drag and bring every handed-off window back on-screen at its original place.</summary>
    public void Panic()
    {
        _keepDrag = false;
        _offeredHwnd = 0;
        _dragHwnd = 0;
        _eventHwnd = 0;
        Job[] jobs;
        if (!Monitor.TryEnter(_gate, 400))
        {
            DiagLog.Write("panic: handoff lock busy");
            return;
        }
        try { jobs = _jobs.Values.ToArray(); }
        finally { Monitor.Exit(_gate); }
        foreach (var job in jobs)
        {
            PlaceholderCard.Close(job.Id);
            try { job.Popups?.Dismiss(); } catch (Exception) { }
            if (PutBack(job, "panic"))
                _host.SendHandoff(job.DeviceId, PacketType.HandoffReturn, HandoffPayloads.WriteReturn(job.Id));
        }
        _busy = false;
        DiagLog.Write($"panic: {jobs.Length} handed-off window(s) brought back");
    }

    /// <summary>Windows currently shown on other devices (for the Transfers page and the tray).</summary>
    public List<(uint Id, string Title, string DeviceId)> Transferred()
    {
        lock (_gate)
            return _jobs.Values.Select(j => (j.Id, j.Title, j.DeviceId)).ToList();
    }

    /// <summary>"Bring back to PC": original position and size on the PC, viewer closed on the device.</summary>
    public void BringBack(uint id)
    {
        Job? job;
        lock (_gate)
            _jobs.TryGetValue(id, out job);
        if (job == null)
            return;
        PlaceholderCard.Close(job.Id);
        try { job.Popups?.Dismiss(); } catch (Exception) { }
        if (PutBack(job, "bring-back"))
        {
            _host.SendHandoff(job.DeviceId, PacketType.HandoffReturn, HandoffPayloads.WriteReturn(job.Id));
            if (OperatingSystem.IsWindows())
                SetForegroundWindow(job.Hwnd);
        }
    }

    /// <summary>"Close": closes the app window and the viewer on the device.</summary>
    public void CloseTransferred(uint id)
    {
        Job? job;
        lock (_gate)
            _jobs.TryGetValue(id, out job);
        if (job == null)
            return;
        _host.SendHandoff(job.DeviceId, PacketType.HandoffState, HandoffPayloads.WriteState(new HandoffPayloads.State(job.Id, HandoffPayloads.StateClosed)));
        Interlocked.Exchange(ref job.FramesSent, Math.Max(1, Volatile.Read(ref job.FramesSent)));
        Finish(job, close: true, echo: false);
    }

    private void PollDrag()
    {
        // The handoff starts from the input worker when raw push crosses the
        // edge pixel. Offering here, from the window rectangle, raced that
        // path and could swallow the real request.
    }

    private void Listen()
    {
        try
        {
            // Any interface so Wi-Fi tablets can reach it; only loopback (adb tunnel) or a paired
            // device's own address is accepted, and every frame is encrypted with the pairing key anyway.
            _listener = new TcpListener(IPAddress.Any, ProductInfo.HandoffPort);
            _listener.Start();
            while (_running)
            {
                if (_listener.Pending())
                {
                    var client = _listener.AcceptTcpClient();
                    var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    if (peer == null || !(IPAddress.IsLoopback(peer) || _host.IsPairedAddress(peer)))
                    {
                        DiagLog.Write($"handoff video rejected {peer}");
                        client.Close();
                        continue;
                    }
                    DiagLog.Write($"handoff video connected {(IPAddress.IsLoopback(peer) ? "usb" : "wifi")} {peer}");
                    client.NoDelay = true;
                    client.SendBufferSize = 256 * 1024;
                    _video = client.GetStream();
                    lock (_gate)
                    {
                        foreach (var job in _jobs.Values)
                            job.NeedIdr = true;
                    }
                }
                else
                    Thread.Sleep(15);
            }
        }
        catch (SocketException)
        {
            StatusKey = "handoffNoLink";
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Encode(Job job, CancellationToken token)
    {
        var key = _host.MediaKeyFor(job.DeviceId);
        if (key == null)
        {
            StatusKey = "handoffNoLink";
            DiagLog.Write("handoff encoder handoffNoLink");
            Finish(job, close: false);
            return;
        }
        var seq = 0UL;
        var frame = 0u;
        var sentAt = 0L;
        void Publish(byte[] annex, ulong captureUs, byte flags)
        {
            var stream = _video;
            if (stream == null)
            {
                job.NeedIdr = true;
                if (!job.LoggedNoStream)
                {
                    job.LoggedNoStream = true;
                    StatusKey = "handoffNoLink";
                    DiagLog.Write("handoff video socket is not connected");
                }
                return;
            }
            Interlocked.Increment(ref job.FramesSent);
            var wire = MediaFrames.Protect(key, ++seq, job.Id, ++frame, captureUs, flags, annex);
            try
            {
                stream.Write(wire, 0, wire.Length);
            }
            catch (IOException)
            {
                _video = null;
                job.NeedIdr = true;
                StatusKey = "handoffNoLink";
            }
            if ((flags & 1) != 0 && Environment.TickCount64 - sentAt > 800)
            {
                sentAt = Environment.TickCount64;
                _host.SendHandoffFast(job.DeviceId, PacketType.HandoffClock, HandoffPayloads.WriteClock(job.Id, frame, captureUs));
            }
        }
        job.Popups ??= new HandoffPopups(job.Hwnd);
        var popups = job.Popups;
        WindowEncoder.Monitor = job.Virtual?.Monitor ?? 0;
        if (job.Virtual != null)
            DiagLog.Write("handoff capture: virtual monitor " + job.Virtual.Value.Device);
        WindowEncoder.Overlay = job.Virtual != null ? null : () =>
        {
            var open = popups.Current();
            if (open.Count == 0)
                return null;
            try { return GdiCapture.Composite(job.Hwnd, open); }
            catch (Exception ex) { DiagLog.Write("handoff popup composite " + ex.Message); return null; }
        };
        WindowEncoder.Run(job.Hwnd, _host.Settings.HandoffQuality, () => token.IsCancellationRequested || !_running, () =>
        {
            if (!job.NeedIdr)
                return false;
            job.NeedIdr = false;
            return true;
        }, Publish, keyName =>
        {
            StatusKey = keyName;
            DiagLog.Write("handoff encoder " + keyName);
            if (IsCaptureFailure(keyName))
                Finish(job, close: false);
        }, () => job.BitrateCap);
        if (token.IsCancellationRequested || !_running)
            return;
        if (Volatile.Read(ref job.FramesSent) == 0)
            PutBack(job, "no-frame");
        else if (IsCaptureFailure(StatusKey))
            Finish(job, close: false);
    }

    private static bool IsCaptureFailure(string key) =>
        key is "handoffBlack" or "handoffNoEncoder" or "handoffNoLink" or "handoffNoHwnd";

    private void SendDragGeometry(nint hwnd)
    {
        Job? job;
        lock (_gate)
            job = _jobs.Values.FirstOrDefault(item => item.Hwnd == hwnd);
        if (job == null || Environment.TickCount64 - job.GeometryAt < 50 || !GetWindowRect(hwnd, out var rect))
            return;
        job.GeometryAt = Environment.TickCount64;
        var winW = Math.Max(1, rect.Right - rect.Left);
        var winH = Math.Max(1, rect.Bottom - rect.Top);
        var settings = _host.Settings;
        var spot = HandoffPolicy.MapToDevice(job.Edge, rect.Left, rect.Top, winW, winH, settings.ScreenOriginX, settings.ScreenOriginY, settings.ScreenWidth, settings.ScreenHeight, job.DeviceW, job.DeviceH, job.Offset);
        _host.SendHandoff(job.DeviceId, PacketType.HandoffGeometry, HandoffPayloads.WriteGeometry(new HandoffPayloads.Geometry(job.Id, spot.X, spot.Y, (ushort)Math.Clamp(winW, 1, 65535), (ushort)Math.Clamp(winH, 1, 65535), (ushort)Math.Clamp(winW, 1, 65535), (ushort)Math.Clamp(winH, 1, 65535))));
    }

    private void Finish(uint id, bool close, bool echo)
    {
        Job? job;
        lock (_gate)
            _jobs.TryGetValue(id, out job);
        if (job != null)
            Finish(job, close, echo);
    }

    private void Finish(Job job, bool close, bool echo = true)
    {
        PlaceholderCard.Close(job.Id);
        try { job.Popups?.Dismiss(); } catch (Exception) { }
        if (close && Volatile.Read(ref job.FramesSent) > 0)
        {
            if (Interlocked.CompareExchange(ref job.Restored, 1, 0) != 0)
                return;
            lock (_gate)
            {
                _jobs.Remove(job.Id);
                if (job.Hwnd == _offeredHwnd)
                {
                    _offeredHwnd = 0;
                    _keepDrag = false;
                }
                if (_jobs.Count == 0)
                    _busy = false;
            }
            try { job.Stop?.Cancel(); } catch { /* already stopped */ }
            if (OperatingSystem.IsWindows())
            {
                PostMessage(job.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                var hwnd = job.Hwnd;
                var home = job.Home;
                // An app that asks "save changes?" keeps its window: bring it (and the prompt) back on the PC
                // instead of leaving it parked off-screen where nobody can answer.
                _ = Task.Delay(1500).ContinueWith(_ =>
                {
                    if (IsWindow(hwnd) && IsWindowVisible(hwnd))
                    {
                        VirtualDisplay.Leave(hwnd);
                        var ok = SetWindowPos(hwnd, IntPtr.Zero, home.Left, home.Top, home.Right - home.Left, home.Bottom - home.Top, 0x0004 | 0x0010);
                        DiagLog.Write($"handoff close kept the window open; restored {ok}");
                        SetForegroundWindow(hwnd);
                    }
                });
            }
            return;
        }
        if (PutBack(job, close ? "closed-before-frame" : "failure") && echo)
            _host.SendHandoff(job.DeviceId, PacketType.HandoffReturn, HandoffPayloads.WriteReturn(job.Id));
    }

    private bool PutBack(Job job, string why)
    {
        if (Interlocked.CompareExchange(ref job.Restored, 1, 0) != 0)
            return false;
        lock (_gate)
        {
            _jobs.Remove(job.Id);
            if (job.Hwnd == _offeredHwnd)
            {
                _offeredHwnd = 0;
                _keepDrag = false;
            }
            if (_jobs.Count == 0)
                _busy = false;
        }
        try { job.Stop?.Cancel(); } catch { /* already stopped */ }
        if (!OperatingSystem.IsWindows())
            return true;
        VirtualDisplay.Leave(job.Hwnd);
        var placed = SetWindowPos(job.Hwnd, IntPtr.Zero, job.Home.Left, job.Home.Top, job.Home.Right - job.Home.Left, job.Home.Bottom - job.Home.Top, 0x0004 | 0x0010);
        DiagLog.Write($"handoff restore {placed} at {job.Home.Left},{job.Home.Top} {why}");
        return true;
    }

    private static void Post(Job job, Effect.HandoffInput input)
    {
        var hwnd = job.Hwnd;
        if (job.Virtual is { } vs)
        {
            PostReal(job, vs, input);
            return;
        }
        if (input.Kind == HandoffPayloads.InputText)
        {
            foreach (var ch in input.Text)
                PostMessage(hwnd, 0x0102, (IntPtr)ch, IntPtr.Zero);
            return;
        }
        if (input.Kind == HandoffPayloads.InputKey)
        {
            var lparam = input.Down ? (IntPtr)1 : (IntPtr)0xC0000001;
            PostMessage(hwnd, input.Down ? 0x0100u : 0x0101u, (IntPtr)input.Vk, lparam);
            return;
        }
        if (!GetWindowRect(hwnd, out var rect))
            return;
        var winW = Math.Max(1, rect.Right - rect.Left);
        var winH = Math.Max(1, rect.Bottom - rect.Top);
        var sx = rect.Left + input.X * winW / Math.Max(1, job.ViewW);
        var sy = rect.Top + input.Y * winH / Math.Max(1, job.ViewH);
        if (job.Popups is { } popups)
        {
            if (input.Kind != HandoffPayloads.InputMove && input.Down)
            {
                popups.AnchorX = sx;
                popups.AnchorY = sy;
                popups.HasAnchor = true;
            }
            // A click on a menu/dropdown goes to that popup, in its own client coordinates.
            var target = popups.HitTest(sx, sy);
            if (target != 0)
                hwnd = target;
        }
        var pt = new POINT { X = sx, Y = sy };
        ScreenToClient(hwnd, ref pt);
        var packed = (IntPtr)((pt.X & 0xFFFF) | ((pt.Y & 0xFFFF) << 16));
        if (input.Kind == HandoffPayloads.InputWheel)
        {
            ClientToScreen(hwnd, ref pt);
            var screen = (IntPtr)((pt.X & 0xFFFF) | ((pt.Y & 0xFFFF) << 16));
            // Pinch arrives as a wheel with WheelCtrl: MK_CONTROL makes most apps zoom.
            var keys = (input.Button & HandoffPayloads.WheelCtrl) != 0 ? 0x0008 : 0;
            if (input.WheelY != 0)
                PostMessage(hwnd, 0x020A, (IntPtr)((input.WheelY << 16) | keys), screen);
            if (input.WheelX != 0)
                PostMessage(hwnd, 0x020E, (IntPtr)(input.WheelX << 16), screen);
            return;
        }
        var button = input.Button == 0 ? (byte)1 : input.Button;
        var down = button switch
        {
            2 => 0x0204u,
            3 => 0x0207u,
            _ => 0x0201u
        };
        var up = button switch
        {
            2 => 0x0205u,
            3 => 0x0208u,
            _ => 0x0202u
        };
        var mk = button switch
        {
            2 => 2,
            3 => 16,
            _ => 1
        };
        if (input.Kind == HandoffPayloads.InputMove)
            PostMessage(hwnd, 0x0200, IntPtr.Zero, packed);
        else
            PostMessage(hwnd, input.Down ? down : up, input.Down ? (IntPtr)mk : IntPtr.Zero, packed);
    }

    /// <summary>Real input on the virtual monitor: the tablet view maps 1:1 onto the monitor rect.</summary>
    private static void PostReal(Job job, VirtualDisplay.Screen s, Effect.HandoffInput input)
    {
        if (input.Kind == HandoffPayloads.InputText)
        {
            VirtualDisplay.Text(input.Text, job.Hwnd);
            return;
        }
        if (input.Kind == HandoffPayloads.InputKey)
        {
            VirtualDisplay.Key(input.Vk, input.Down, job.Hwnd);
            return;
        }
        var x = s.Left + Math.Clamp(input.X * s.Width / Math.Max(1, job.ViewW), 0, s.Width - 1);
        var y = s.Top + Math.Clamp(input.Y * s.Height / Math.Max(1, job.ViewH), 0, s.Height - 1);
        if (input.Kind == HandoffPayloads.InputWheel)
        {
            var ctrl = (input.Button & HandoffPayloads.WheelCtrl) != 0;
            if (ctrl) VirtualDisplay.Key(0x11, true, job.Hwnd);
            if (input.WheelY != 0) VirtualDisplay.Wheel(x, y, input.WheelY, false);
            if (input.WheelX != 0) VirtualDisplay.Wheel(x, y, input.WheelX, true);
            if (ctrl) VirtualDisplay.Key(0x11, false, job.Hwnd);
            return;
        }
        if (input.Kind == HandoffPayloads.InputMove)
            VirtualDisplay.MouseMove(x, y);
        else
            VirtualDisplay.MouseButton(x, y, input.Button == 0 ? 1 : input.Button, input.Down);
    }

    private sealed class Job
    {
        public uint Id;
        public nint Hwnd;
        public string DeviceId = "";
        public ScreenEdge Edge;
        public RECT Home;
        public int ViewW;
        public int ViewH;
        public int DeviceW;
        public int DeviceH;
        public int Offset;
        public long GeometryAt;
        public string Title = "";
        public long LinkLostAt;
        public volatile bool NeedIdr = true;
        public volatile bool LoggedNoStream;
        public int FramesSent;
        public volatile int BitrateCap;
        public int Restored;
        public CancellationTokenSource? Stop;
        public Thread? Encoder;
        public HandoffPopups? Popups;
        /// <summary>Set when the window lives maximised on the EdgeSpan virtual monitor (real input, monitor capture).</summary>
        public VirtualDisplay.Screen? Virtual;
    }

    public bool ProbeDrag()
    {
        if (_keepDrag && _offeredHwnd != 0)
        {
            // The release usually happens while the pointer is on the tablet, where the hook swallows it, so
            // GetAsyncKeyState stays "down" until the pointer comes back. The hook's own release counter ends
            // the drag the moment the button goes up, wherever the pointer is.
            var released = _host.LeftUps != Interlocked.Read(ref _upsAtDrag);
            if (!released && (!OperatingSystem.IsWindows() || (GetAsyncKeyState(0x01) & 0x8000) != 0))
            {
                _host.WindowDrag = true;
                return true;
            }
            EndFollow();
            _host.WindowDrag = false;
            return false;
        }
        if (!OperatingSystem.IsWindows())
            return _host.WindowDrag;
        var fromGui = MoveSizeFromGui();
        nint hwnd;
        lock (_gate)
        {
            hwnd = _eventHwnd != 0 ? _eventHwnd : fromGui != 0 ? fromGui : ButtonMoved();
            if (hwnd != 0)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == (uint)Environment.ProcessId)
                    hwnd = 0;
            }
            _dragHwnd = hwnd;
        }
        var on = hwnd != 0;
        _host.WindowDrag = on;
        return on;
    }

    public void BeginDragHandoff(string deviceId, ScreenEdge edge, string? method) => BeginDragHandoff(deviceId, edge, method, 0);

    private void BeginDragHandoff(string deviceId, ScreenEdge edge, string? method, nint explicitHwnd)
    {
        DiagLog.Write($"handoff begin device={deviceId} edge={edge} method={method} hwnd=0x{(explicitHwnd != 0 ? explicitHwnd : _dragHwnd):X}{(explicitHwnd != 0 ? " (tray/hotkey)" : "")}");
        nint hwnd;
        lock (_gate)
        {
            if (!HandoffPolicy.AcceptsTrigger(_busy))
            {
                DiagLog.Write("handoff busy");
                return;
            }
            // Tray/hotkey sends carry their own window: the mouse hook's drag probe clears _dragHwnd on every
            // move while no drag is in progress, which made the tray path fail with "no window".
            hwnd = explicitHwnd != 0 ? explicitHwnd : _dragHwnd;
        }
        // USB and LAN/Wi-Fi have a direct socket for the video; a relay link does not.
        if (method is "relay")
        {
            DiagLog.Write("handoff refused method=" + method);
            _host.ReleaseDrag(deviceId, "handoffNoLink");
            CancelDrag();
            return;
        }
        if (hwnd == 0 || !OperatingSystem.IsWindows())
        {
            DiagLog.Write("handoff no hwnd");
            _host.ReleaseDrag(deviceId, "handoffNoHwnd");
            return;
        }
        lock (_gate)
        {
            if (!HandoffPolicy.AcceptsTrigger(_busy))
            {
                DiagLog.Write("handoff busy");
                return;
            }
            _busy = true;
        }
        Interlocked.Exchange(ref _upsAtDrag, _host.LeftUps);
        _keepDrag = true;
        DiagLog.Write("handoff cancel " + CancelWindow(hwnd));
        if (!GetWindowRect(hwnd, out var home))
        {
            _keepDrag = false;
            ClearBusy();
            DiagLog.Write("handoff rect failed");
            _host.ReleaseDrag(deviceId, "handoffNoHwnd");
            return;
        }
        home = RememberHome(hwnd, home);
        var onVirtual = VirtualDisplay.Place(hwnd);
        if (onVirtual == null)
            Park(hwnd, edge, home);
        var winW = onVirtual?.Width ?? Math.Max(1, home.Right - home.Left);
        var winH = onVirtual?.Height ?? Math.Max(1, home.Bottom - home.Top);
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var title = new StringBuilder(180);
        GetWindowText(hwnd, title, title.Capacity);
        var name = title.Length == 0 ? "Window" : title.ToString();
        var id = _host.CommitDragHandoff(deviceId, name, (int)dpi, winW, winH, Math.Max(1, winW * 96 / (int)dpi), Math.Max(1, winH * 96 / (int)dpi), edge);
        if (id == 0)
        {
            _keepDrag = false;
            VirtualDisplay.Leave(hwnd);
            var restored = SetWindowPos(hwnd, IntPtr.Zero, home.Left, home.Top, home.Right - home.Left, home.Bottom - home.Top, 0x0004 | 0x0010);
            ClearBusy();
            var key = string.IsNullOrEmpty(_host.DragFailureKey) ? "handoffNoLink" : _host.DragFailureKey;
            StatusKey = key;
            DiagLog.Write($"handoff restore {restored} at {home.Left},{home.Top}");
            DiagLog.Write("handoff offer failed " + key);
            return;
        }
        StatusKey = "";
        if (explicitHwnd != 0)
            _keepDrag = false; // no mouse button is held for a tray/hotkey send; the drag-follow must not "release" it
        var placed = _host.Settings.Devices.FirstOrDefault(d => d.Id == deviceId);
        lock (_gate)
        {
            _offeredHwnd = hwnd;
            _dragHwnd = hwnd;
            _jobs[id] = new Job
            {
                Id = id,
                Hwnd = hwnd,
                DeviceId = deviceId,
                Edge = edge,
                Home = home,
                ViewW = winW,
                ViewH = winH,
                Title = name,
                DeviceW = placed?.ScreenWidth ?? 1600,
                DeviceH = placed?.ScreenHeight ?? 1000,
                Offset = placed?.OffsetPx ?? 0,
                Virtual = onVirtual
            };
        }
        DiagLog.Write($"handoff parked id={id} {winW}x{winH} title={name}");
        if (_host.Settings.HandoffPlaceholder)
            PlaceholderCard.Show(id, home.Left, home.Top, winW, winH, dpi / 96.0, name,
                EdgeSpan.Product.Catalog.Get(_host.Settings.Language, "handoffOnTablet"));
    }

    private nint _lastForeign;

    /// <summary>Remembers the last foreground window that is not EdgeSpan, so the tray item can send it.</summary>
    public void NoteForeground()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid != (uint)Environment.ProcessId)
            _lastForeign = fg;
    }

    /// <summary>
    /// "Send to tablet": hands the foreground window (or the last non-EdgeSpan one) to the tablet without dragging.
    /// Windows offers no supported way to add items to other apps' title-bar menus, so this is driven by a hotkey and the tray.
    /// </summary>
    public void SendForeground(string? method, bool useLast)
    {
        if (!OperatingSystem.IsWindows())
            return;
        NoteForeground();
        var hwnd = useLast ? _lastForeign : GetForegroundWindow();
        if (hwnd != 0)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId)
                hwnd = 0;
        }
        SendWindow(hwnd, method);
    }

    /// <summary>Sends a specific top-level window (tray list, hotkey) to the tablet, with a stage log.</summary>
    public void SendWindow(nint hwnd, string? method)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var deviceId = _host.DeviceOnEdge(ScreenEdge.Right);
        DiagLog.Write($"handoff send-window hwnd=0x{hwnd:X} device={deviceId ?? "none"} method={method} blocker={(deviceId == null ? "-" : _host.HandoffBlocker(deviceId) ?? "none")}");
        foreach (var line in _host.HandoffLinkReport())
            DiagLog.Write("handoff link " + line);
        if (hwnd == 0 || deviceId == null)
        {
            NoteFailure(deviceId == null ? "handoffNeedTablet" : "handoffNoHwnd");
            return;
        }
        var edge = ScreenEdge.Right;
        var placed = _host.Settings.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (placed != null && Enum.TryParse<ScreenEdge>(placed.PcEdge, true, out var parsed))
            edge = parsed;
        lock (_gate)
            _dragHwnd = hwnd;
        BeginDragHandoff(deviceId, edge, method, hwnd);
        _keepDrag = false;
        // No mouse drag carries this window: tell the tablet the "drag" is over at once, so the viewer stays put
        // instead of following the pointer (it only moves when its title bar is dragged on the tablet).
        Job? sent;
        lock (_gate)
        {
            sent = _jobs.Values.FirstOrDefault(j => j.Hwnd == hwnd);
            if (sent != null)
                _offeredHwnd = 0;
        }
        if (sent != null)
        {
            _host.SendHandoff(sent.DeviceId, PacketType.HandoffState, HandoffPayloads.WriteState(new HandoffPayloads.State(sent.Id, HandoffPayloads.StateRestored)));
            DiagLog.Write("handoff placed (tray/hotkey) id=" + sent.Id);
        }
    }

    /// <summary>Visible, titled, non-tool top-level windows of other processes, for the tray "Send window" list.</summary>
    public static List<(nint Hwnd, string Title)> ListWindows()
    {
        var list = new List<(nint, string)>();
        if (!OperatingSystem.IsWindows())
            return list;
        var self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != 0)
                return true;
            if ((GetWindowLongW(h, -20) & 0x80) != 0)
                return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self)
                return true;
            var sb = new System.Text.StringBuilder(256);
            if (GetWindowTextW(h, sb, sb.Capacity) <= 0)
                return true;
            var t = sb.ToString();
            if (t is "Program Manager" or "Windows Input Experience" || DwmCloaked(h))
                return true;
            list.Add((h, t.Length > 60 ? t[..57] + "…" : t));
            return list.Count < 25;
        }, 0);
        return list;
    }

    private static bool DwmCloaked(nint h) => DwmGetWindowAttribute(h, 14, out var c, 4) == 0 && c != 0;

    private delegate bool EnumProc(nint hwnd, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);

    public void CancelDrag()
    {
        nint hwnd;
        lock (_gate)
            hwnd = _dragHwnd != 0 ? _dragHwnd : _eventHwnd;
        if (hwnd == 0 || !OperatingSystem.IsWindows())
            return;
        DiagLog.Write("handoff cancel drag " + CancelWindow(hwnd));
    }

    public void OnDragReached(string deviceId)
    {
        if (_offeredHwnd != 0 || string.IsNullOrEmpty(deviceId))
            return;
        var edge = ScreenEdge.Right;
        var placed = _host.Settings.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (placed != null && Enum.TryParse<ScreenEdge>(placed.PcEdge, true, out var parsed))
            edge = parsed;
        BeginDragHandoff(deviceId, edge, "usb");
    }

    public void NoteTabletNotReady() => NoteFailure("handoffNeedTablet");

    public void NoteFailure(string reason)
    {
        if (string.IsNullOrEmpty(reason))
            return;
        if (StatusKey != reason)
            StatusKey = reason;
        DiagLog.Write("handoff status " + reason);
    }

    private void ClearBusy()
    {
        lock (_gate)
        {
            if (_jobs.Count == 0)
                _busy = false;
        }
    }

    private RECT RememberHome(nint hwnd, RECT current)
    {
        var settings = _host.Settings;
        _homes.TryGetValue(hwnd, out var stored);
        var prior = _homes.ContainsKey(hwnd)
            ? new HandoffPolicy.WindowHome(stored.Left, stored.Top, stored.Right, stored.Bottom)
            : (HandoffPolicy.WindowHome?)null;
        var chosen = HandoffPolicy.ChooseHome(
            new HandoffPolicy.WindowHome(current.Left, current.Top, current.Right, current.Bottom),
            settings.ScreenOriginX, settings.ScreenOriginY, settings.ScreenWidth, settings.ScreenHeight,
            prior);
        var home = new RECT { Left = chosen.Left, Top = chosen.Top, Right = chosen.Right, Bottom = chosen.Bottom };
        if (HandoffPolicy.OverlapsScreen(home.Left, home.Top, home.Right, home.Bottom, settings.ScreenOriginX, settings.ScreenOriginY, settings.ScreenWidth, settings.ScreenHeight))
            _homes[hwnd] = chosen;
        return home;
    }

    private void EndFollow()
    {
        Job? job;
        lock (_gate)
        {
            if (_offeredHwnd == 0)
                return;
            job = _jobs.Values.FirstOrDefault(item => item.Hwnd == _offeredHwnd);
            _offeredHwnd = 0;
            _keepDrag = false;
            _dragHwnd = 0;
            _eventHwnd = 0;
            _buttonDragging = false;
        }
        if (job == null)
            return;
        _host.SendHandoff(job.DeviceId, PacketType.HandoffState, HandoffPayloads.WriteState(new HandoffPayloads.State(job.Id, HandoffPayloads.StateRestored)));
        DiagLog.Write("handoff drag released id=" + job.Id);
    }

    private void Park(nint hwnd, ScreenEdge edge, RECT home)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, 9);
        var settings = _host.Settings;
        var width = Math.Max(1, home.Right - home.Left);
        var height = Math.Max(1, home.Bottom - home.Top);
        var x = home.Left;
        var y = home.Top;
        // Park past the whole virtual desktop (all monitors), not just the configured screen, so on multi-monitor
        // setups the window does not land on a second display; keep a 4 px sliver on-screen so DWM keeps the
        // window "visible" and Chromium/Electron apps keep painting (fully off-screen windows can go black in WGC).
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = GetSystemMetrics(78);
        var vh = GetSystemMetrics(79);
        if (vw <= 0 || vh <= 0)
        {
            vx = settings.ScreenOriginX;
            vy = settings.ScreenOriginY;
            vw = Math.Max(settings.ScreenWidth, 1);
            vh = Math.Max(settings.ScreenHeight, 1);
        }
        const int sliver = 4;
        switch (edge)
        {
            case ScreenEdge.Right:
                x = vx + vw - sliver;
                break;
            case ScreenEdge.Left:
                x = vx - width + sliver;
                break;
            case ScreenEdge.Bottom:
                y = vy + vh - sliver;
                break;
            default:
                y = vy - height + sliver;
                break;
        }
        var parked = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        DiagLog.Write($"handoff park {parked} at {x},{y}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    private static string CancelWindow(nint hwnd)
    {
        var returned = SendMessageTimeout(hwnd, 0x001F, IntPtr.Zero, IntPtr.Zero, 0x0002, 50, out _);
        return returned == IntPtr.Zero ? "timeout" : "ok";
    }

    private void OfferNow(nint hwnd, string device)
    {
        if (hwnd == _offeredHwnd)
        {
            SendDragGeometry(hwnd);
            return;
        }
        lock (_gate)
        {
            if (_claim == hwnd)
                return;
            _claim = hwnd;
        }
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
            return;
        if (!GetWindowRect(hwnd, out var rect))
        {
            lock (_gate)
                if (_claim == hwnd) _claim = 0;
            NoteFailure("handoffNoHwnd");
            return;
        }
        var winW = rect.Right - rect.Left;
        var winH = rect.Bottom - rect.Top;
        if (winW < 40 || winH < 40)
        {
            lock (_gate)
                if (_claim == hwnd) _claim = 0;
            NoteFailure("handoffNoHwnd");
            return;
        }
        var settings = _host.Settings;
        var edge = HandoffPolicy.FarthestEdge(rect.Left, rect.Top, winW, winH, settings.ScreenOriginX, settings.ScreenOriginY, settings.ScreenWidth, settings.ScreenHeight);
        var placed = settings.Devices.FirstOrDefault(d => d.Id == device);
        if (placed != null && Enum.TryParse<ScreenEdge>(placed.PcEdge, true, out var parsed))
            edge = parsed;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var title = new StringBuilder(180);
        GetWindowText(hwnd, title, title.Capacity);
        var id = _host.OfferHandoff(device, title.Length == 0 ? "Window" : title.ToString(), (int)dpi, winW, winH, winW * 96 / (int)dpi, winH * 96 / (int)dpi, edge);
        if (id == 0)
        {
            lock (_gate)
                if (_claim == hwnd) _claim = 0;
            var key = string.IsNullOrEmpty(_host.DragFailureKey) ? "handoffNeedTablet" : _host.DragFailureKey;
            NoteFailure(key);
            return;
        }
        if (StatusKey == "handoffNeedTablet")
            StatusKey = "";
        _offeredHwnd = hwnd;
        lock (_gate)
        {
            _jobs[id] = new Job
            {
                Id = id,
                Hwnd = hwnd,
                DeviceId = device,
                Edge = edge,
                Home = rect,
                ViewW = winW,
                ViewH = winH,
                DeviceW = placed?.ScreenWidth ?? 1600,
                DeviceH = placed?.ScreenHeight ?? 1000,
                Offset = placed?.OffsetPx ?? 0
            };
        }
    }

    private nint MoveSizeFromGui()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return 0;
        var tid = GetWindowThreadProcessId(fg, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(tid, ref info) || (info.flags & GuiInMoveSize) == 0)
            return 0;
        return info.hwndMoveSize;
    }

    private nint ButtonMoved()
    {
        if ((GetAsyncKeyState(0x01) & 0x8000) == 0)
        {
            _buttonHwnd = 0;
            _buttonDragging = false;
            return 0;
        }
        if (!GetCursorPos(out var pt))
            return 0;
        var root = GetAncestor(WindowFromPoint(pt), 2);
        if (root == 0)
            return 0;
        if (!GetWindowRect(root, out var rect))
            return 0;
        if (root != _buttonHwnd)
        {
            _buttonHwnd = root;
            _buttonRect = rect;
            _buttonDragging = false;
            return 0;
        }
        if (rect.Left != _buttonRect.Left || rect.Top != _buttonRect.Top || rect.Right != _buttonRect.Right || rect.Bottom != _buttonRect.Bottom)
            _buttonDragging = true;
        _buttonRect = rect;
        return _buttonDragging ? root : 0;
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
