using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using EdgeSpan.Crypto;
using EdgeSpan.Layout;
using EdgeSpan.Net;
using EdgeSpan.Product;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Services;

public sealed class SessionRuntime : IDisposable
{
    private static readonly byte[] Probe = "ESPN"u8.ToArray();
    private readonly IngressQueue _ingress = new();
    private bool _crossed;
    private bool _tracedPair;
    private bool _tracedReject;
    private int _relayUdpPort;
    private int _announcing;
    private readonly Dictionary<string, string> _relayPeers = new();
    private readonly DispatcherTimer _timer;
    private readonly string _settingsPath;
    private UdpTransport? _session;
    private DiscoveryAgent? _discovery;
    private UdpClient? _mdns;
    private RelayClient? _relay;
    private TcpTunnel? _tunnel;
    private UsbLink? _usb;
    private WindowsInputCapture? _input;
    private readonly EdgeGlow _glow = new();
    private readonly TransferToast _toast = new();
    private string _handoffToast = "";
    private string? _glowDevice;
    private int _entryGen;
    private int _paired;
    private ClipboardWatch? _clipboard;
    private readonly HandoffHost _handoff;
    private readonly AppStreamHost _appStream;
    private CancellationTokenSource _life = new();
    private long _lastAnnounce;
    private long _lastProbe;
    private long _lastStun;
    private string _relayUrl = "";
    private int _connecting;

    public HostController Host { get; }
    public string BindError { get; private set; } = "";
    public int BoundPort { get; private set; }
    public string? RelayPeerId { get; private set; }
    public string RelayState { get; private set; } = "";
    public string ActiveMethod { get; private set; } = "";
    public string ConnectPhase { get; private set; } = "idle";
    public string ConnectDetail { get; private set; } = "";
    public string InjectNote { get; private set; } = "";
    public string TunnelError { get; private set; } = "usbNoTunnel";
    private long _injectAt;
    private bool _injectQueued;
    private bool _injected;
    public IPEndPoint? Mapped { get; private set; }
    public string HandoffStatus => _handoff.StatusKey;
    public bool NativeClipboard => _clipboard != null;
    public event Action<Effect>? Effect;

    public SessionRuntime()
    {
        _settingsPath = SettingsStore.DefaultPath;
        var settings = SettingsStore.Load(_settingsPath);
        if (settings.FirstRunUtc == default)
            settings.FirstRunUtc = DateTimeOffset.UtcNow;
        var relayEnv = Environment.GetEnvironmentVariable("EDGESPAN_RELAY");
        if (!string.IsNullOrWhiteSpace(relayEnv))
        {
            settings.InternetEnabled = true;
            settings.RelayUrl = relayEnv.Trim();
            settings.StunHost = "";
        }
        Host = new HostController(settings, () => Environment.TickCount64, new SystemRandomBytes(), settingsPath: _settingsPath);
        if (!string.IsNullOrWhiteSpace(relayEnv))
            Host.Save();
        try
        {
            _session = new UdpTransport(settings.ListenPort);
            BoundPort = _session.Port;
            _session.Received += OnSession;
        }
        catch (SocketException)
        {
            BindError = Catalog.Format(settings.Language, "failedToBind", settings.ListenPort);
        }
        try
        {
            _discovery = new DiscoveryAgent();
            _discovery.Discovered += (announce, from) =>
            {
                if (!string.Equals(announce.Id, Host.Settings.HostId, StringComparison.Ordinal))
                    Host.NoteDiscovery(announce, from, Environment.TickCount64);
            };
        }
        catch (SocketException)
        {
            _discovery = null;
        }
        try
        {
            _mdns = new UdpClient();
            _mdns.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch (SocketException)
        {
            _mdns = null;
        }
        try
        {
            _tunnel = new TcpTunnel(settings.ListenPort);
            _tunnel.Received += (from, data) => _ingress.TryAdd(from, data, null);
        }
        catch (SocketException ex)
        {
            _tunnel = null;
            TunnelError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? "usbPortBusy" : "usbNoTunnel";
        }
        _input = new WindowsInputCapture(Host);
        if (OperatingSystem.IsWindows())
        {
            _clipboard = new ClipboardWatch();
            _clipboard.TextCopied += text => Host.SetLocalClipboard(text);
            _clipboard.FilesCopied += (paths, cut) => Host.OfferLocalFiles(paths, cut);
        }
        StartupRegistration.Apply(settings.LaunchAtStartup);
        _handoff = new HandoffHost(Host);
        Host.DragProbe = () => _handoff.ProbeDrag();
        Host.DragHandoff = (device, edge) => _handoff.BeginDragHandoff(device, edge, ActiveMethod);
        Host.DragFailed = () => _handoff.CancelDrag();
        Host.SendWindowHotkey = () => Dispatcher.UIThread.Post(() => _handoff.SendForeground(ActiveMethod, useLast: false));
        _handoff.ApplySettings();
        // Always listening so a phone gets a clear "turn it on" answer instead of a timeout when Phone to PC is off.
        _appStream = new AppStreamHost(Host);
        _appStream.Start();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
        MaybeAutoConnect();
    }

    public void BeginUsb()
    {
        ActiveMethod = "usb";
        RememberMethod("usb");
        if (_tunnel == null)
        {
            SetPhase("failed", TunnelError);
            Trace(TunnelError);
            return;
        }
        _usb ??= new UsbLink();
        _usb.Start(new UsbHooks
        {
            IsUsbConnected = IsUsbConnected,
            PairingOpen = () => Host.PairingOpen,
            OpenPairing = () => Host.OpenPairingWindow(),
            Pin = () => Host.CurrentPin,
            Phase = SetPhase,
            Trace = Trace,
            Inject = note => InjectNote = note,
            Drop = () =>
            {
                Trace("usb-drop: links + tunnel clients");
                Host.DropLoopbackLinks();
                _tunnel?.DropClients();
            }
        });
    }

    public void BeginLan()
    {
        StopUsb();
        ActiveMethod = "lan";
        RememberMethod("lan");
        Host.Settings.InternetEnabled = false;
        Host.Save();
        NotifySettingsChanged();
        if (!Host.PairingOpen)
            Host.OpenPairingWindow();
        SetPhase("searching", "waitingPhone");
    }

    public void BeginRelay()
    {
        StopUsb();
        ActiveMethod = "relay";
        RememberMethod("relay");
        Host.Settings.InternetEnabled = true;
        Host.Save();
        _relayUrl = "";
        NotifySettingsChanged();
        if (!Host.PairingOpen)
            Host.OpenPairingWindow();
        SetPhase("searching", "waitingRelay");
    }

    public void StopLink()
    {
        StopUsb();
        ActiveMethod = "";
        SetPhase("idle", "");
        Host.Settings.LastMethod = "";
        Host.Save();
    }

    private void MaybeAutoConnect()
    {
        var method = Environment.GetEnvironmentVariable("EDGESPAN_METHOD");
        if (string.IsNullOrWhiteSpace(method))
            method = Host.Settings.LastMethod;
        if (string.Equals(Environment.GetEnvironmentVariable("EDGESPAN_USB"), "1", StringComparison.Ordinal))
            method = "usb";
        switch ((method ?? "").Trim().ToLowerInvariant())
        {
            case "usb": BeginUsb(); break;
            case "lan": BeginLan(); break;
            case "relay": BeginRelay(); break;
        }
    }

    private void RememberMethod(string method)
    {
        if (Host.Settings.LastMethod == method)
            return;
        Host.Settings.LastMethod = method;
        Host.Save();
    }

    private void SetPhase(string phase, string detail)
    {
        ConnectPhase = phase;
        ConnectDetail = detail;
    }

    private void StopUsb() => _usb?.Stop();

    private static bool IsLoopback(string address) =>
        address.Contains("127.0.0.1", StringComparison.Ordinal) || address.Contains("::1", StringComparison.Ordinal);

    private bool IsUsbConnected()
    {
        var snap = Host.SnapshotDevices().FirstOrDefault(d => d.Status == "connected");
        if (snap == null || _tunnel == null)
            return false;
        // A live tunnel client is required: after an unplug the old check stayed true on a stale
        // "paired" status, so the conductor never redid the setup.
        return _tunnel.HasClient;
    }

    public void NotifySettingsChanged()
    {
        StartupRegistration.Apply(Host.Settings.LaunchAtStartup);
        Host.AnnounceCapabilities();
        _handoff.ApplySettings();
        _relayUrl = "";
        if (!Host.Settings.EdgeGlowEnabled || Host.Machine.ActiveDeviceId == null)
            _glow.Hide();
        else
            ShowGlow(Host.Machine.ActiveDeviceId);
    }

    private GlowBand? ShowGlow(string? deviceId, int? atX = null, int? atY = null)
    {
        if (!OperatingSystem.IsWindows() || !Host.Settings.EdgeGlowEnabled || string.IsNullOrEmpty(deviceId))
        {
            _glow.Hide();
            return null;
        }
        var device = Host.Settings.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null || !PlacementParser.TryParse(device, out var placement, out _))
        {
            _glow.Hide();
            return null;
        }
        // Multi-monitor: the band sits on the device's own monitor (virtual-desktop px, that monitor's DPI).
        var mon = Host.Machine.MonitorFor(placement);
        var dpi = mon.Dpi > 0 ? mon.Dpi : EdgeGlow.DpiAt(mon.X + mon.W / 2, mon.Y + mon.H / 2);
        // The band must be deep enough for the bulge (ExitDepth/EnterDepth are fractions of it).
        var thick = Math.Max(112, Math.Clamp(Host.Settings.EdgeGlowThickness, 8, 160)) * dpi / 96;
        var band = GlowBand.For(placement, mon.X, mon.Y, mon.W, mon.H, Math.Max(1, thick)).ClipTo(mon);
        // Returning through the far side of a device placed between monitors: draw on the neighbour's edge.
        if (atX is { } px && atY is { } py && !mon.Contains(px, py) && Host.Machine.NeighborFor(placement) is { } next && next.Contains(px, py))
            band = band.Facing(next);
        _glow.Show(band, Host.Settings.EdgeGlowColor, Host.Settings.EdgeGlowIntensity);
        return band;
    }

    private void NoteCross(string? deviceId, int x, int y, int devW, int devH)
    {
        var reduce = Motion.Reduced(Host.Settings.ReduceMotion, ClientAnimationOff());
        if (!string.IsNullOrEmpty(deviceId))
        {
            // Windows -> Android: the Windows half of the shared line bulges out at the exit point.
            _glowDevice = deviceId;
            var band = ShowGlow(deviceId);
            if (band is { } b && Host.Settings.BreakthroughEnabled)
            {
                var vertical = b.Edge is GlowEdge.Left or GlowEdge.Right;
                var along = vertical ? y / (double)Math.Max(1, devH) : x / (double)Math.Max(1, devW);
                if (devW <= 0 || devH <= 0)
                    along = CursorAlong(b);
                _glow.Cross(ElasticMode.Exit, along, reduce);
            }
            CrossCue(deviceId);
            return;
        }
        var back = _glowDevice;
        if (Host.Settings.BreakthroughEnabled && Host.Settings.EdgeGlowEnabled && !string.IsNullOrEmpty(back))
        {
            // Android -> Windows: the line is stretched in at the mapped point and tears.
            var band = ShowGlow(back, x, y);
            if (band is { } b)
            {
                var along = ElasticLine.Along(b.Edge, x, y, b.X, b.Y, b.Width, b.Height);
                _glow.Cross(ElasticMode.Enter, along, reduce);
            }
            CrossCue(back);
            var token = ++_entryGen;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep((int)ElasticLine.DurationMs + 40);
                if (token == _entryGen && Host.Machine.ActiveDeviceId == null)
                    _glow.Hide();
            });
            return;
        }
        ShowGlow(null);
    }

    private double CursorAlong(GlowBand band)
    {
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var p))
            return 0.5;
        return ElasticLine.Along(band.Edge, p.X, p.Y, band.X, band.Y, band.Width, band.Height);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private void CrossCue(string? deviceId)
    {
        if (!Host.Settings.BreakthroughEnabled)
            return;
        var reduce = Motion.Reduced(Host.Settings.ReduceMotion, ClientAnimationOff());
        _glow.Burst(reduce, Host.Settings.BreakthroughIntensity);
        if (Host.Settings.SoundEnabled && !reduce)
            EdgeTone.Play();
        _ = deviceId;
    }

    private static bool ClientAnimationOff()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var enabled = 1;
        SystemParametersInfo(0x1042, 0, ref enabled, 0);
        return enabled == 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint ini);

    public void Dispose()
    {
        _timer.Stop();
        _life.Cancel();
        _glow.Dispose();
        _handoff.Dispose();
        _appStream.Dispose();
        _input?.Dispose();
        _clipboard?.Dispose();
        _usb?.Dispose();
        _discovery?.Dispose();
        _session?.Dispose();
        _tunnel?.Dispose();
        _mdns?.Dispose();
        var relay = _relay;
        _relay = null;
        if (relay != null)
            _ = relay.DisposeAsync();
        Host.Dispose();
    }

    private void OnSession(IPEndPoint from, byte[] data)
    {
        _ingress.TryAdd(from, data, null);
    }

    private static bool IsHandoffFailure(string key) => key is
        "handoffNeedTablet" or "handoffNoLink" or "handoffNoEncoder" or "handoffBlack" or "handoffPcOff" or "handoffNoHwnd" or "handoffNoOverlay" or "handoffNoVideo" or "handoffNoKey" or "handoffJpeg";

    /// <summary>Tray "Send window to tablet": uses the last window that was in front before the tray took focus.</summary>
    public void SendWindowToTablet() => _handoff.SendForeground(ActiveMethod, useLast: true);

    public void SendWindowHandle(nint hwnd) => _handoff.SendWindow(hwnd, ActiveMethod);

    private void Pump()
    {
        _handoff.NoteForeground();
        var now = Environment.TickCount64;
        var settings = Host.Settings;
        for (var budget = WirePressure.InboxPerPump; budget > 0 && _ingress.TryTake(out var from, out var data, out var relayPeer); budget--)
        {
            var item = (From: from, Data: data, RelayPeer: relayPeer);
            if (item.RelayPeer == null && item.Data.Length >= 20 && item.Data[0] == 0x01 && item.Data[1] == 0x01 && _stunTx != null
                && StunCodec.TryReadMapped(item.Data, _stunTx, out var mapped) && mapped != null)
            {
                Mapped = mapped;
                continue;
            }
            if (item.RelayPeer == null)
            {
                foreach (var device in settings.Devices)
                    Host.NotePunchFrom(device.Id, item.From);
            }
            Host.Ingest(item.From, item.Data, now, item.RelayPeer);
        }
        Host.Tick(now);
        foreach (var packet in Host.DrainOutbound())
            Send(packet);
        foreach (var effect in Host.DrainEffects())
        {
            if (effect is Effect.ControlChanged changed)
            {
                _input?.ApplyControl(changed.DeviceId != null);
                _input?.ReleaseStuckModifiers();
                NoteCross(changed.DeviceId, changed.X, changed.Y, changed.DevW, changed.DevH);
            }
            else if (effect is Effect.ModifiersReleased)
                _input?.ReleaseStuckModifiers();
            else if (effect is Effect.HandoffAccepted accepted)
                _handoff.OnAccepted(accepted);
            else if (effect is Effect.HandoffInput handoffInput)
                _handoff.OnInput(handoffInput);
            else if (effect is Effect.HandoffGeometry geometry)
                _handoff.OnGeometry(geometry);
            else if (effect is Effect.HandoffState handoffState)
                _handoff.OnState(handoffState);
            else if (effect is Effect.HandoffReturned returned)
                _handoff.OnReturned(returned);
            else if (effect is Effect.HandoffVideo video)
                _handoff.OnVideo(video);
            else if (effect is Effect.Log log)
                DiagLog.Write(log.Message);
            else if (effect is Effect.WindowDragCommit commit)
            {
                DiagLog.Write($"handoff commit id={commit.Id} device={commit.DeviceId} edge={commit.Edge}");
                CrossCue(commit.DeviceId);
            }
            else if (effect is Effect.WindowDragFailed failed)
                _handoff.NoteFailure(failed.Reason);
            else if (effect is Effect.WindowDragEdge dragEdge)
                _handoff.OnDragReached(dragEdge.DeviceId);
            else if (effect is Effect.WindowDragBlocked)
                _handoff.NoteTabletNotReady();
            else if (effect is Effect.FileProgress file)
            {
                _toast.Update(Host.Settings.Language, file.Name, file.Done, file.Total, file.Phase);
                if (file.Incoming && file.Phase == "ready" && Host.Settings.FileTransfer)
                    _clipboard?.SetFiles(new[] { file.Path });
            }
            else if (effect is Effect.ClipboardArrived clip)
                _clipboard?.SetText(clip.Text);
            Effect?.Invoke(effect);
        }
        var handoffKey = _handoff.StatusKey;
        if (IsHandoffFailure(handoffKey) && _handoffToast != handoffKey)
        {
            _handoffToast = handoffKey;
            var lang = Host.Settings.Language;
            _toast.Say(Catalog.Get(lang, handoffKey), handoffKey);
            DiagLog.Write("handoff toast " + handoffKey);
        }
        else if (!IsHandoffFailure(handoffKey))
            _handoffToast = "";
        if (settings.SoundEnabled)
        {
            var pairedCount = 0;
            foreach (var device in Host.SnapshotDevices())
            {
                if (device.Status is "paired" or "connected")
                    pairedCount++;
            }
            if (pairedCount > _paired && !Motion.Reduced(settings.ReduceMotion, ClientAnimationOff()))
                EdgeTone.Play();
            _paired = pairedCount;
        }
        if (now - _lastAnnounce > 2000 && Interlocked.CompareExchange(ref _announcing, 1, 0) == 0)
        {
            _lastAnnounce = now;
            var packet = Host.BuildAnnounce();
            byte[]? mdns = null;
            if (_mdns != null)
            {
                var host = NetAddresses.LocalIPv4().Select(x => x.Address).FirstOrDefault() ?? IPAddress.Loopback;
                mdns = MdnsPacket.Announce(Host.Settings.DisplayName, (ushort)Host.Settings.ListenPort, host, Host.Settings.DisplayName);
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Announce(packet, mdns); }
                finally { Interlocked.Exchange(ref _announcing, 0); }
            });
        }
        if (settings.InternetEnabled && now - _lastProbe > 200)
        {
            _lastProbe = now;
            foreach (var target in Host.StagedTargets())
                _session?.Send(target, Probe);
        }
        if (settings.InternetEnabled && now - _lastStun > 15000)
        {
            _lastStun = now;
            SendStun();
        }
        EnsureRelay();
        var paired = Host.SnapshotDevices().FirstOrDefault(d => d.Status is "paired" or "connected");
        if (paired != null && ConnectPhase != "connected")
        {
            var usb = _tunnel != null && (_tunnel.Owns(paired.Address, paired.Port) || (IsLoopback(paired.Address) && _tunnel.HasClient));
            string? kind = usb ? "usb"
                : IsLoopback(paired.Address) ? null
                : paired.Path == LinkChoice.Relay ? "relay"
                : paired.Path is LinkChoice.Lan or LinkChoice.Direct or LinkChoice.Pending ? "lan"
                : null;
            if (kind != null)
            {
                ActiveMethod = kind;
                var detail = kind switch
                {
                    "usb" => "pairOkUsb",
                    "relay" when paired.Path == LinkChoice.Direct => "pairOkDirect",
                    "relay" => "pairOkRelay",
                    _ => "pairOkLan"
                };
                SetPhase("connected", detail);
                RememberMethod(kind);
            }
        }
        if (string.Equals(Environment.GetEnvironmentVariable("EDGESPAN_INJECT"), "1", StringComparison.Ordinal) && !_injected)
        {
            var ready = Host.SnapshotDevices().Any(d => d.Status == "connected" && d.ScreenH > 0);
            if (ready && IsUsbConnected())
            {
                if (!_injectQueued)
                {
                    _injectQueued = true;
                    _injectAt = now + 800;
                }
                else if (now >= _injectAt)
                {
                    _injected = true;
                    Host.RunInjectProbe();
                    Trace("inject-script");
                }
            }
        }
        if (string.Equals(Environment.GetEnvironmentVariable("EDGESPAN_CROSS"), "1", StringComparison.Ordinal) && !_crossed)
        {
            if (Host.CrossOnce())
            {
                _crossed = true;
                Trace("edge-enter");
            }
        }
        if (!_tracedPair && Host.SnapshotDevices().Any(device => device.Status is "paired" or "connected"))
        {
            _tracedPair = true;
            Trace("pairing-ok");
        }
        else if (!_tracedReject && Host.SnapshotDevices().Any(device => device.Status == "rejected"))
        {
            _tracedReject = true;
            Trace("pairing-rejected");
        }
    }

    private (string Host, int Port) StunTarget()
    {
        var settings = Host.Settings;
        if (!string.IsNullOrWhiteSpace(settings.StunHost))
            return (settings.StunHost, settings.StunPort);
        if (!settings.InternetEnabled || !Uri.TryCreate(settings.RelayUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return ("", 0);
        var port = _relayUdpPort > 0 ? _relayUdpPort : ProductInfo.RelayUdpPort;
        return (uri.Host, port);
    }

    private void Send(Outbound packet)
    {
        if (packet.Remote != null && _tunnel != null && _tunnel.Owns(packet.Remote.Address.ToString(), packet.Remote.Port))
        {
            _tunnel.TrySend(packet.Remote, packet.Bytes);
            return;
        }
        var path = Host.SnapshotDevices().FirstOrDefault(d => d.Id == packet.DeviceId)?.Path ?? LinkChoice.Offline;
        var pending = path == LinkChoice.Pending;
        if (!string.IsNullOrEmpty(packet.RelayPeer) && _relay != null && (path is LinkChoice.Relay or LinkChoice.Pending or LinkChoice.Offline))
            _ = _relay.SendPayloadAsync(packet.RelayPeer, packet.Bytes, CancellationToken.None);
        else if (path == LinkChoice.Relay && _relay != null && _relayPeers.TryGetValue(packet.DeviceId, out var peer))
            _ = _relay.SendPayloadAsync(peer, packet.Bytes, CancellationToken.None);
        if (pending)
        {
            foreach (var target in Host.PunchTargets(packet.DeviceId))
                _session?.Send(target, packet.Bytes);
        }
        if (packet.Remote != null && path != LinkChoice.Relay)
            _session?.Send(packet.Remote, packet.Bytes);
    }

    private static void Trace(string line)
    {
        DiagLog.Write(line);
        var path = Environment.GetEnvironmentVariable("EDGESPAN_LOG");
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { File.AppendAllText(path, line + Environment.NewLine); }
        catch (IOException) { }
    }

    private void Announce(byte[] packet, byte[]? mdns)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, ProductInfo.DiscoveryPort) };
        foreach (var (_, broadcast) in NetAddresses.LocalIPv4())
            targets.Add(new IPEndPoint(broadcast, ProductInfo.DiscoveryPort));
        try { _discovery?.Announce(packet, targets); }
        catch (SocketException) { }
        if (_mdns == null || mdns == null)
            return;
        try { _mdns.Send(mdns, mdns.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353)); }
        catch (SocketException) { }
    }

    private byte[]? _stunTx;

    private void SendStun()
    {
        if (_session == null)
            return;
        _stunTx = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(_stunTx);
        var request = StunCodec.BindingRequest(_stunTx);
        _ = ResolveStunAsync(request);
    }

    private async Task ResolveStunAsync(byte[] request)
    {
        try
        {
            var (host, port) = StunTarget();
            if (string.IsNullOrWhiteSpace(host) || _session == null)
                return;
            var addresses = await Dns.GetHostAddressesAsync(host);
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v4 == null)
                return;
            _session.Send(new IPEndPoint(v4, port), request);
        }
        catch (Exception)
        {
        }
    }

    private void EnsureRelay()
    {
        var settings = Host.Settings;
        if (!settings.InternetEnabled)
        {
            if (_relay != null)
            {
                var old = _relay;
                _relay = null;
                RelayPeerId = null;
                RelayState = "";
                _ = old.DisposeAsync();
            }
            return;
        }
        if (_relay != null && _relay.IsConnected && _relayUrl == settings.RelayUrl)
            return;
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
            return;
        _relayUrl = settings.RelayUrl;
        _ = ConnectRelayAsync(settings.RelayUrl, settings.DisplayName);
    }

    private async Task ConnectRelayAsync(string url, string name)
    {
        try
        {
            var client = new RelayClient();
            client.MessageReceived += message => Dispatcher.UIThread.Post(() => OnSignal(message));
            client.PayloadReceived += (peer, payload) => _ingress.TryAdd(new IPEndPoint(IPAddress.Any, 0), payload, peer);
            client.Failed += reason => Dispatcher.UIThread.Post(() => RelayState = reason);
            await client.ConnectAsync(url, name, "host", _life.Token);
            _relay = client;
            RelayPeerId = client.PeerId;
            _relayUdpPort = client.RemoteUdpPort;
            RelayState = "connected";
            Trace("relay-connected " + client.PeerId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RelayState = ex.Message;
            _relayUrl = "";
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    private void OnSignal(SignalMessage message)
    {
        if (message.Op != "signal" || string.IsNullOrWhiteSpace(message.From) || string.IsNullOrWhiteSpace(message.Data))
            return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(message.Data); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            var kind = root.TryGetProperty("t", out var t) ? t.GetString() : "";
            if (kind == "hello")
            {
                if (Host.PairingOpen && _relay != null && !string.IsNullOrWhiteSpace(message.From))
                {
                    var pinBody = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["t"] = "pin",
                        ["pin"] = Host.CurrentPin,
                        ["id"] = Host.Settings.HostId
                    });
                    _ = _relay.SendSignalAsync(message.From, pinBody, CancellationToken.None);
                }
                return;
            }
            if (kind != "announce")
                return;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
            if (string.IsNullOrWhiteSpace(id))
                return;
            _relayPeers[id] = message.From;
            TryEndpoint(root, "mapped", out var mapped);
            TryEndpoint(root, "lan", out var lan);
            var mappedEp = mapped.Port > 0 ? mapped : null;
            var lanEp = lan.Port > 0 ? lan : null;
            Host.StageInternet(id, message.From, mappedEp, lanEp);
            Host.BeginInternet(id);
            if (mappedEp != null)
                Host.SetPeerMapped(id, mappedEp);
            ReplyAnnounce(message.From);
        }
    }

    private void ReplyAnnounce(string toPeer)
    {
        if (_relay == null)
            return;
        var lan = NetAddresses.GuessLanHost() + ":" + BoundPort;
        var mapped = Mapped == null ? "" : Mapped.Address + ":" + Mapped.Port;
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["t"] = "announce",
            ["id"] = Host.Settings.HostId,
            ["lan"] = lan,
            ["mapped"] = mapped
        });
        _ = _relay.SendSignalAsync(toPeer, body, CancellationToken.None);
    }

    public string ExtendedPairingUri()
    {
        var settings = Host.Settings;
        var host = string.IsNullOrWhiteSpace(settings.PublicHost) ? NetAddresses.GuessLanHost() : settings.PublicHost.Trim();
        var port = settings.PublicPort is > 0 and <= 65535 ? settings.PublicPort : (BoundPort > 0 ? BoundPort : settings.ListenPort);
        var uri = string.IsNullOrWhiteSpace(Host.CurrentPin)
            ? Host.PairingUri
            : PairingLink.Build(host, port, Host.CurrentPin, settings.DisplayName);
        if (!settings.InternetEnabled || string.IsNullOrWhiteSpace(RelayPeerId) || string.IsNullOrWhiteSpace(uri))
            return uri;
        return uri
            + "&r=" + Uri.EscapeDataString(RelayPeerId)
            + "&u=" + Uri.EscapeDataString(settings.RelayUrl);
    }

    private static bool TryEndpoint(JsonElement root, string name, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        if (!root.TryGetProperty(name, out var el))
            return false;
        var text = el.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], out var port))
            return false;
        if (!IPAddress.TryParse(text[..colon], out var ip))
            return false;
        endpoint = new IPEndPoint(ip, port);
        return true;
    }
}
