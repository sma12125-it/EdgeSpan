using System.Net;
using System.Text;
using EdgeSpan.Crypto;
using EdgeSpan.Input;
using EdgeSpan.Layout;
using EdgeSpan.Net;
using EdgeSpan.Product;
using EdgeSpan.Protocol;

namespace EdgeSpan.Session;

public sealed record Outbound(string DeviceId, IPEndPoint? Remote, byte[] Bytes, string? RelayPeer = null);

public sealed record DiscoveredPeer(string Id, string Name, string Address, int Port, int ScreenW, int ScreenH, long LastSeenMs);

public sealed record DeviceSnapshot(
    string Id,
    string Name,
    string Status,
    string Address,
    int Port,
    LinkChoice Path,
    double? RttMs,
    bool Active,
    int ScreenW,
    int ScreenH,
    string PcEdge,
    string ReturnEdge,
    string Anchor,
    string Corner,
    int OffsetPx,
    int SpanPx,
    int CornerSizePx,
    double PointerSpeed,
    string Monitor = "");

public abstract record Effect
{
    public sealed record Log(string Message) : Effect;
    public sealed record StateChanged : Effect;
    public sealed record ControlChanged(string? DeviceId, int X, int Y, int DevW = 0, int DevH = 0) : Effect;
    public sealed record ModifiersReleased : Effect;
    public sealed record HandoffAccepted(string DeviceId, uint Id, byte Codec, int ViewW, int ViewH) : Effect;
    public sealed record HandoffGeometry(uint Id, int X, int Y, int PhysW, int PhysH, int DipW = 0, int DipH = 0) : Effect;
    public sealed record HandoffInput(uint Id, byte Kind, int X, int Y, byte Button, bool Down, int WheelX, int WheelY, int Vk, string Text) : Effect;
    public sealed record HandoffState(uint Id, byte Value) : Effect;
    public sealed record HandoffReturned(uint Id) : Effect;
    public sealed record HandoffVideo(uint Id, uint Bitrate, byte Fps, bool Idr) : Effect;
    public sealed record WarpCursor(int X, int Y) : Effect;
    public sealed record WindowDragEdge(string DeviceId) : Effect;
    public sealed record WindowDragBlocked(string DeviceId) : Effect;
    public sealed record WindowDragCommit(string DeviceId, uint Id, ScreenEdge Edge) : Effect;
    public sealed record WindowDragFailed(string DeviceId, string Reason) : Effect;
    public sealed record ClipboardArrived(string Text) : Effect;
    public sealed record FileProgress(string Name, long Done, long Total, bool Incoming, string Phase, string Path) : Effect;
}

public sealed class HostController : IDisposable
{
    private readonly Func<long> _clock;
    private readonly IRandom _random;
    private readonly RuntimeOptions _options;
    private readonly ILicenseValidator _licenses;
    private readonly string? _settingsPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, Link> _links = new();
    private readonly Dictionary<string, DiscoveredPeer> _discovered = new();
    private readonly List<Outbound> _out = new();
    private readonly List<Effect> _effects = new();
    private readonly List<string> _logs = new();
    private long _windowUntil;
    private int _fails;
    private int _parkX;
    private int _parkY;
    private uint _clipId;
    private string _lastClip = "";
    private string? _heldClip;
    private long _clipSentAt;
    private bool _clipArmed;
    private bool _suppressClip;
    private bool _homeSaved;
    private int _homeX;
    private int _homeY;
    private readonly Dictionary<string, (int X, int Y)> _devicePoint = new();
    private readonly ModifierLedger _modifiers = new();
    private volatile bool _windowDrag;
    private int _dragPush;
    private int _seatX;
    private int _seatY;
    private bool _dragArmed;
    private bool _dragGaveUp;
    private bool _dragStillLogged;
    public bool WindowDrag { get => _windowDrag; set => _windowDrag = value; }
    public Func<bool>? DragProbe { get; set; }
    public Action<string, ScreenEdge>? DragHandoff { get; set; }
    /// <summary>Raised by the "send window to tablet" hotkey; the Windows side picks the foreground window.</summary>
    public Action? SendWindowHotkey { get; set; }
    public Action? DragFailed { get; set; }
    public string DragFailureKey { get; private set; } = "";
    private bool _dragMissLogged;
    private long _edgeIdleLogged = -100000;
    private readonly FileSync _files = new();
    private readonly Dictionary<string, StagedPeer> _staged = new();
    private uint _fileSeq;

    public string ReceivedDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "EdgeSpan", "Received");

    private sealed class StagedPeer
    {
        public string RelayPeer = "";
        public IPEndPoint? Mapped;
        public IPEndPoint? Lan;
    }

    public bool HotkeyCapture { get; set; }

    private sealed class Link
    {
        public DeviceConfig Config { get; set; } = new();
        public IPEndPoint? Remote { get; set; }
        public string? RelayPeer { get; set; }
        public PairingHandshake? Pairing { get; set; }
        public WireSession? Session { get; set; }
        public bool OnLan { get; set; }
        public LinkChoice Path { get; set; } = LinkChoice.Offline;
        public CandidateExchange? Punch { get; set; }
        public long LastRx { get; set; }
        public int ClearSeq { get; set; }
        public byte[]? LastClear { get; set; }
        public string Status { get; set; } = "idle";
        public byte PeerCaps { get; set; }
    }

    public AppSettings Settings { get; }
    /// <summary>Raised (under the host lock) when a connected link stops answering pings.</summary>
    public Action<string>? LinkLost { get; set; }
    public EdgeMachine Machine { get; } = new();
    public string CurrentPin { get; private set; } = "";
    public string PairingUri { get; private set; } = "";
    public bool PairingOpen { get; private set; }
    public long PacketsSent { get; private set; }
    public IReadOnlyList<string> LayoutProblems { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Logs => _logs;

    public HostController(AppSettings settings, Func<long> clock, IRandom random, RuntimeOptions? options = null, ILicenseValidator? licenses = null, string? settingsPath = null)
    {
        Settings = settings;
        _clock = clock;
        _random = random;
        _options = options ?? new RuntimeOptions();
        _licenses = licenses ?? new DevLicenseValidator();
        _settingsPath = settingsPath;
        if (string.IsNullOrWhiteSpace(Settings.HostId))
            Settings.HostId = Guid.NewGuid().ToString("N");
        PruneDevices(Settings);
        foreach (var device in Settings.Devices)
        {
            var link = new Link { Config = device, Status = "saved" };
            if (IPAddress.TryParse(device.Address, out var ip))
                link.Remote = new IPEndPoint(ip, device.Port);
            _links[device.Id] = link;
        }
        Machine.SetScreen(Settings.ScreenOriginX, Settings.ScreenOriginY, Settings.ScreenWidth, Settings.ScreenHeight);
        Machine.UniformScale = Settings.ArrangeScale == "uniform";
        _parkX = Settings.ScreenOriginX + Settings.ScreenWidth / 2;
        _parkY = Settings.ScreenOriginY + Settings.ScreenHeight / 2;
        ReloadLayout();
    }

    public LicenseStatus License => _licenses.Evaluate(Settings.LicenseKey, Settings.FirstRunUtc, DateTimeOffset.UtcNow);

    public bool DeviceHasControl => Machine.ActiveDeviceId != null;

    public (int X, int Y) CursorPark
    {
        get { lock (_gate) return (_parkX, _parkY); }
    }

    public List<string> SnapshotLogs()
    {
        lock (_gate) return _logs.ToList();
    }

    /// <summary>Key of the current monitor configuration (ids + rects), used to persist layouts per setup.</summary>
    public string MonitorKey { get; private set; } = "";

    public IReadOnlyList<HostMonitor> SnapshotMonitors()
    {
        lock (_gate) return Machine.Monitors.ToList();
    }

    /// <summary>
    /// Updates the Windows monitor list (virtual-desktop physical pixels). When the configuration changes
    /// (plugged/unplugged/moved) each device restores its layout saved for that configuration, if any; a
    /// device whose monitor disappeared falls back to the primary monitor.
    /// </summary>
    public void SetHostMonitors(IReadOnlyList<HostMonitor> monitors)
    {
        lock (_gate)
        {
            if (monitors.Count == 0)
                return;
            var key = KeyFor(monitors);
            if (key == MonitorKey)
                return;
            MonitorKey = "";
            Machine.SetMonitors(monitors);
            foreach (var d in Settings.Devices)
            {
                if (d.Layouts.TryGetValue(key, out var saved))
                    ApplySaved(d, saved);
                else if (d.Monitor.Length > 0 && monitors.All(m => m.Id != d.Monitor))
                {
                    var text = $"monitor {d.Monitor} missing; {d.Name} falls back to the primary monitor";
                    _logs.Add(text);
                    _effects.Add(new Effect.Log(text));
                }
            }
            MonitorKey = key;
            ReloadLayout();
            Save();
            Touch();
        }
    }

    public static string KeyFor(IReadOnlyList<HostMonitor> monitors) =>
        string.Join(";", monitors.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => $"{m.Id}@{m.X},{m.Y},{m.W}x{m.H}"));

    private static string Saved(DeviceConfig d) => $"{d.Monitor}|{d.PcEdge}|{d.Anchor}|{d.Corner}|{d.OffsetPx}";

    private static void ApplySaved(DeviceConfig d, string saved)
    {
        var p = saved.Split('|');
        if (p.Length != 5 || !int.TryParse(p[4], out var offset))
            return;
        d.Monitor = p[0];
        d.PcEdge = p[1];
        d.ReturnEdge = p[1] switch { "Left" => "Right", "Right" => "Left", "Top" => "Bottom", _ => "Top" };
        d.Anchor = p[2];
        d.Corner = p[3];
        d.OffsetPx = offset;
    }

    public void SetHostScreen(int originX, int originY, int width, int height)
    {
        lock (_gate)
        {
            Settings.ScreenOriginX = originX;
            Settings.ScreenOriginY = originY;
            Settings.ScreenWidth = width;
            Settings.ScreenHeight = height;
            Machine.SetScreen(originX, originY, width, height);
            Touch();
        }
    }

    public byte[] BuildAnnounce()
    {
        lock (_gate)
        {
            return DiscoveryCodec.Encode(new DiscoveryAnnounce(
                Settings.HostId,
                Settings.DisplayName,
                1,
                0x1,
                (ushort)Settings.ListenPort,
                (ushort)Math.Clamp(Settings.ScreenWidth, 1, 65535),
                (ushort)Math.Clamp(Settings.ScreenHeight, 1, 65535),
                PairingOpen ? CurrentPin : ""));
        }
    }

    public void OpenPairingWindow()
    {
        lock (_gate)
        {
            CurrentPin = MakePin();
            PairingOpen = true;
            _fails = 0;
            _windowUntil = _clock() + _options.PairingWindowMs;
            PairingUri = PairingLink.Build(NetAddresses.GuessLanHost(), Settings.ListenPort, CurrentPin, Settings.DisplayName);
            Log("pairingOpen");
            Touch();
        }
    }

    public void ClosePairingWindow()
    {
        lock (_gate)
        {
            PairingOpen = false;
            Touch();
        }
    }

    public void NoteDiscovery(DiscoveryAnnounce announce, IPEndPoint from, long nowMs)
    {
        lock (_gate)
        {
            if (announce.Id == Settings.HostId || string.IsNullOrWhiteSpace(announce.Id))
                return;
            _discovered[announce.Id] = new DiscoveredPeer(announce.Id, announce.Name, from.Address.ToString(), announce.Port == 0 ? from.Port : announce.Port, announce.ScreenW, announce.ScreenH, nowMs);
            if (_links.TryGetValue(announce.Id, out var link))
            {
                link.Remote = new IPEndPoint(from.Address, announce.Port == 0 ? from.Port : announce.Port);
                link.OnLan = true;
                link.Path = link.Session?.Established == true ? LinkChoice.Lan : link.Path;
                link.Config.Address = from.Address.ToString();
                link.Config.Port = link.Remote.Port;
                if (announce.ScreenW > 0) link.Config.ScreenWidth = announce.ScreenW;
                if (announce.ScreenH > 0) link.Config.ScreenHeight = announce.ScreenH;
                ReloadLayout();
            }
            Touch();
        }
    }

    public string AddManual(string name, string address, int port)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            var edge = NextFreeEdge();
            var config = new DeviceConfig
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? "Android" : name.Trim(),
                Address = address.Trim(),
                Port = port,
                PcEdge = edge,
                ReturnEdge = Opposite(edge)
            };
            Settings.Devices.Add(config);
            var link = new Link { Config = config, Status = "saved" };
            if (IPAddress.TryParse(config.Address, out var ip))
                link.Remote = new IPEndPoint(ip, port);
            _links[id] = link;
            ReloadLayout();
            Save();
            Touch();
            return id;
        }
    }

    public void UpdateDevice(DeviceConfig incoming)
    {
        lock (_gate)
        {
            var existing = Settings.Devices.FirstOrDefault(d => d.Id == incoming.Id);
            if (existing == null)
                return;
            existing.Name = incoming.Name;
            existing.Address = incoming.Address;
            existing.Port = incoming.Port;
            existing.Monitor = incoming.Monitor ?? "";
            existing.PcEdge = incoming.PcEdge;
            existing.ReturnEdge = incoming.ReturnEdge;
            existing.Anchor = incoming.Anchor;
            existing.Corner = incoming.Corner;
            existing.OffsetPx = incoming.OffsetPx;
            existing.SpanPx = incoming.SpanPx;
            existing.CornerSizePx = incoming.CornerSizePx;
            existing.PointerSpeed = incoming.PointerSpeed;
            // The phone is the source of truth for its size (Hello/announce). The UI copy can be stale
            // (old rotation, saved before the phone reported), and every auto-save used to write it back
            // over the live size, so the host thought the phone was smaller and exited mid-screen.
            if (existing.ScreenWidth <= 0 || existing.ScreenHeight <= 0)
            {
                existing.ScreenWidth = incoming.ScreenWidth;
                existing.ScreenHeight = incoming.ScreenHeight;
            }
            if (_links.TryGetValue(existing.Id, out var link) && IPAddress.TryParse(existing.Address, out var ip))
                link.Remote = new IPEndPoint(ip, existing.Port);
            ReloadLayout();
            Save();
            Touch();
        }
    }

    public void RemoveDevice(string id)
    {
        lock (_gate)
        {
            if (Machine.ActiveDeviceId == id)
                ReturnToHost(Get(id));
            else if (_links.TryGetValue(id, out var dropping))
                ReleaseModifiers(dropping, includeHeldKeys: true);
            Settings.Devices.RemoveAll(d => d.Id == id);
            if (_links.Remove(id, out var link))
                link.Pairing?.Dispose();
            ReloadLayout();
            Save();
            Touch();
        }
    }

    public void Ingest(IPEndPoint from, byte[] datagram, long nowMs, string? relayPeer = null)
    {
        lock (_gate)
        {
            if (!PacketCodec.TryReadHeader(datagram, out var header))
                return;
            if (header.Encrypted)
            {
                var link = FindBySession(header.SessionId)
                    ?? (string.IsNullOrEmpty(relayPeer) ? null : FindByRelay(relayPeer))
                    ?? FindByRemote(from);
                if (link?.Session == null || !link.Session.Established)
                    return;
                RememberRoute(link, from, relayPeer);
                link.LastRx = nowMs;
                link.Status = "connected";
                var result = link.Session.Receive(datagram, nowMs);
                Apply(link, result, nowMs);
                foreach (var packet in link.Session.Poll(nowMs))
                    Enqueue(link, packet);
                return;
            }
            if (!PacketCodec.TryDecodeClear(datagram, out header, out var payload))
                return;
            HandleClear(from, header, payload, nowMs, relayPeer);
        }
    }

    /// <summary>Moves the pointer onto the first paired device. Used to prove an edge crossing.</summary>
    /// <summary>Places the phone on the top edge and crosses it, then clicks and types. Used to prove injection.</summary>
    public void RunInjectProbe()
    {
        lock (_gate)
        {
            var device = Settings.Devices.FirstOrDefault(d => _links.TryGetValue(d.Id, out var link) && link.Session?.Established == true);
            if (device == null)
                return;
            device.PcEdge = "Top";
            device.ReturnEdge = "Bottom";
            device.Anchor = "Edge";
            device.SpanPx = 0;
            ReloadLayout();
            Machine.SetScreen(Settings.ScreenOriginX, Settings.ScreenOriginY, Math.Max(1, Settings.ScreenWidth), Math.Max(1, Settings.ScreenHeight));
        }
        OnPhysicalMouse(Settings.ScreenWidth / 2, Settings.ScreenOriginY, 0, -80);
        OnMouseButton(0, true);
        OnMouseButton(0, false);
        OnKey(0x41, true, false, false, false, false);
        OnKey(0x41, false, false, false, false, false);
        OnPhysicalMouse(Settings.ScreenWidth / 2, Settings.ScreenOriginY, 40, -12);
    }

    public bool CrossOnce()
    {
        lock (_gate)
        {
            if (Machine.ActiveDeviceId != null)
                return false;
            var id = Settings.Devices.FirstOrDefault(d => _links.TryGetValue(d.Id, out var link) && link.Session?.Established == true)?.Id;
            if (id == null)
                return false;
            if (Machine.ForceEnter(id) is not Transition.Enter entered)
                return false;
            Grant(entered);
            Log("edgeEnter", id);
            return true;
        }
    }

    public void NoteHostCursor(int x, int y)
    {
        lock (_gate)
            Machine.SetHostCursor(x, y);
    }

    public void OnPhysicalMouse(int x, int y, int rawDx, int rawDy)
    {
        var dragging = _windowDrag;
        if (DragProbe != null)
        {
            try { dragging = DragProbe(); }
            catch (Exception) { dragging = _windowDrag; }
        }
        string? commitId = null;
        var commitEdge = ScreenEdge.Right;
        string? failed = null;
        lock (_gate)
        {
            if (Machine.ActiveDeviceId is string active)
            {
                var transition = Machine.MoveDeviceBy(rawDx, rawDy);
                var link = Get(active);
                if (transition is Transition.Return returned)
                {
                    var exitDev = Machine.Devices.FirstOrDefault(d => d.Id == active);
                    _effects.Add(new Effect.Log($"edge-exit host device={active} edge={(returned.Through ? "through" : exitDev?.ReturnEdge.ToString())} at=({returned.DeviceX},{returned.DeviceY}) size={exitDev?.ScreenWidth}x{exitDev?.ScreenHeight} delta={rawDx},{rawDy} host=({returned.HostX},{returned.HostY})"));
                    _devicePoint[active] = (returned.DeviceX, returned.DeviceY);
                    _homeSaved = false;
                    if (link != null)
                    {
                        link.Session?.ClearMouse();
                        SendFast(link, PacketType.MouseMove, Payloads.MouseMove(returned.DeviceX, returned.DeviceY));
                        ReleaseModifiers(link, includeHeldKeys: true);
                        SendReliable(link, PacketType.EdgeReturn, Payloads.EdgeReturn(returned.HostX, returned.HostY, returned.DeviceX, returned.DeviceY));
                    }
                    else
                        ReleaseModifiers(null, includeHeldKeys: true);
                    _parkX = returned.HostX;
                    _parkY = returned.HostY;
                    _dragArmed = false;
                    _dragPush = 0;
                    _dragGaveUp = false;
                    _effects.Add(new Effect.WarpCursor(returned.HostX, returned.HostY));
                    _effects.Add(new Effect.ControlChanged(null, returned.HostX, returned.HostY));
                    Touch();
                    return;
                }
                if (transition is Transition.Stay stay && link != null)
                    link.Session?.QueueMouse(stay.X, stay.Y);
                _effects.Add(new Effect.WarpCursor(_parkX, _parkY));
                return;
            }

            var vx = x;
            var vy = y;
            var right = Machine.OriginX + Machine.ScreenW - 1;
            var bottom = Machine.OriginY + Machine.ScreenH - 1;
            if (rawDx > 0 && x >= right) vx = x + rawDx;
            if (rawDx < 0 && x <= Machine.OriginX) vx = x + rawDx;
            if (rawDy > 0 && y >= bottom) vy = y + rawDy;
            if (rawDy < 0 && y <= Machine.OriginY) vy = y + rawDy;
            var hold = false;
            if (!dragging)
            {
                _dragPush = 0;
                _dragArmed = false;
                _dragGaveUp = false;
                _dragStillLogged = false;
            }
            if (dragging && !_dragArmed && !_dragGaveUp && !_dragMissLogged && (x >= right || x <= Machine.OriginX || y >= bottom || y <= Machine.OriginY) && !TryEdgeSeat(x, y, out _, out _))
            {
                // Dragging against a screen border where no tablet sits (common with two monitors: the tablet is
                // arranged on the other monitor's edge). Say where the tablets are so the log explains it.
                _dragMissLogged = true;
                var where = string.Join("; ", Machine.Devices.Select(d => $"{d.Id} {d.Anchor} {d.PcEdge} monitor={Machine.MonitorFor(d)} segment={Machine.Segment(d, Machine.MonitorFor(d))}"));
                _effects.Add(new Effect.Log($"handoff edge-miss x={x} y={y} screen={Machine.OriginX},{Machine.OriginY} {Machine.ScreenW}x{Machine.ScreenH} tablets=[{where}]"));
            }
            if (!dragging && TryEdgeSeat(x, y, out var idleSeat, out var idleEdge) && _clock() - _edgeIdleLogged > 15000)
            {
                _edgeIdleLogged = _clock();
                _effects.Add(new Effect.Log($"handoff edge-touch without window drag device={idleSeat} edge={idleEdge} x={x} y={y} (drag a title bar here to send a window)"));
            }
            if (!dragging)
                _dragMissLogged = false;
            else if (!_dragGaveUp && !_dragArmed && TryEdgeSeat(x, y, out var seatId, out var seatEdge))
            {
                _seatX = x;
                _seatY = y;
                _dragPush = HandoffPolicy.NextPush(_dragPush, seatEdge, true, rawDx, rawDy);
                if (_dragPush == 0 && rawDx == 0 && rawDy == 0 && !_dragStillLogged)
                {
                    _dragStillLogged = true;
                    _effects.Add(new Effect.Log($"handoff watching x={x} y={y} screen={Machine.ScreenW}x{Machine.ScreenH} edge={seatEdge} raw=0"));
                }
                if (!HandoffPolicy.PushReady(_dragPush))
                {
                    Machine.SetHostCursor(Math.Clamp(x, Machine.OriginX, right), Math.Clamp(y, Machine.OriginY, bottom));
                    hold = true;
                }
                else
                {
                    var reason = DragReject(seatId);
                    if (reason != null)
                    {
                        _dragGaveUp = true;
                        _effects.Add(new Effect.WindowDragFailed(seatId, reason));
                        _effects.Add(new Effect.Log($"handoff blocked {reason} device={seatId} edge={seatEdge} push={_dragPush}"));
                        _dragPush = 0;
                        DragFailureKey = reason;
                        failed = reason;
                        hold = true;
                    }
                    else
                    {
                        _dragArmed = true;
                        commitId = seatId;
                        commitEdge = seatEdge;
                        _effects.Add(new Effect.Log($"handoff trigger device={seatId} edge={seatEdge} x={x} y={y} push={_dragPush} raw={rawDx},{rawDy}"));
                        hold = true;
                    }
                }
            }
            else if (dragging && _dragArmed)
                hold = true;
            else if (dragging && !_dragGaveUp && !_dragArmed)
                _dragPush = 0;

            if (!hold)
            {
                var hostMove = Machine.MoveHost(vx, vy);
                if (hostMove is Transition.Enter entered)
                {
                    _homeSaved = false;
                    Grant(entered);
                }
            }
        }
        if (commitId != null)
            StartDragHandoff(commitId, commitEdge);
        else if (failed != null)
            DragFailed?.Invoke();
    }

    private void StartDragHandoff(string deviceId, ScreenEdge edge)
    {
        try
        {
            if (DragHandoff != null)
                DragHandoff(deviceId, edge);
            else if (CommitDragHandoff(deviceId, "Window", 96, 800, 600, 800, 600, edge) == 0)
                DragFailed?.Invoke();
        }
        catch (Exception ex)
        {
            ReleaseDrag(deviceId, "handoffNoLink");
            lock (_gate)
                _effects.Add(new Effect.Log("handoff commit " + ex.GetType().Name + " " + ex.Message));
        }
    }

    public int ButtonsSent { get; private set; }
    public int WheelsSent { get; private set; }
    public int DroppedOutbound { get; private set; }

    public void OnMouseButton(byte button, bool down)
    {
        lock (_gate)
        {
            if (Machine.ActiveDeviceId is not string id || Get(id) is not { } link)
                return;
            Flush(link);
            SendReliable(link, PacketType.MouseButton, Payloads.MouseButton(button, down, Machine.X, Machine.Y));
            ButtonsSent++;
        }
    }

    public void OnScroll(int dxTicks, int dyTicks)
    {
        lock (_gate)
        {
            if (Machine.ActiveDeviceId is not string id || Get(id) is not { } link)
                return;
            SendReliable(link, PacketType.MouseScroll, Payloads.Scroll((short)Math.Clamp(dxTicks, short.MinValue, short.MaxValue), (short)Math.Clamp(dyTicks, short.MinValue, short.MaxValue), Machine.X, Machine.Y));
            WheelsSent++;
        }
    }

    public ModifierLedger.Decision PreviewKey(int virtualKey, bool down, bool ctrl, bool alt, bool shift, bool win)
    {
        if (HotkeyCapture)
            return default;
        var hotkey = IsHotkey(virtualKey, ctrl, alt, shift, win);
        return _modifiers.Observe(virtualKey, down, DeviceHasControl, hotkey);
    }

    public bool OnKey(int virtualKey, bool down, bool ctrl, bool alt, bool shift, bool win)
    {
        var decision = PreviewKey(virtualKey, down, ctrl, alt, shift, win);
        return OnKeyObserved(virtualKey, down, ctrl, alt, shift, win, decision);
    }

    public bool OnKeyObserved(int virtualKey, bool down, bool ctrl, bool alt, bool shift, bool win, ModifierLedger.Decision decision)
    {
        lock (_gate)
            return ApplyKey(virtualKey, down, ctrl, alt, shift, win, decision);
    }

    public void ReleaseRemoteModifiers()
    {
        lock (_gate)
        {
            var ups = _modifiers.TakeRemoteUps(includeHeldKeys: true);
            foreach (var link in _links.Values)
            {
                if (link.Session?.Established == true)
                {
                    foreach (var vk in ups)
                        ReleaseKey(link, vk);
                }
            }
            _effects.Add(new Effect.ModifiersReleased());
        }
    }

    public void NoteHostModifiersReleased(IReadOnlyList<int> virtualKeys) =>
        _modifiers.NoteHostReleased(virtualKeys);

    private uint _handoffSeq;

    public uint OfferHandoff(string deviceId, string title, int dpi, int physW, int physH, int dipW, int dipH, ScreenEdge edge)
    {
        lock (_gate)
            return OfferLocked(deviceId, title, dpi, physW, physH, dipW, dipH, edge);
    }

    /// <summary>
    /// Sends the offer and moves the pointer onto the tablet. Called after the
    /// native drag has been cancelled. Returns 0 and records <see cref="DragFailureKey"/>
    /// when the tablet cannot take the window.
    /// </summary>
    public uint CommitDragHandoff(string deviceId, string title, int dpi, int physW, int physH, int dipW, int dipH, ScreenEdge edge)
    {
        lock (_gate)
        {
            var reason = DragReject(deviceId);
            if (reason != null)
            {
                NoteDragFailed(deviceId, reason);
                return 0;
            }
            var id = OfferLocked(deviceId, title, dpi, physW, physH, dipW, dipH, edge);
            if (id == 0)
            {
                NoteDragFailed(deviceId, "handoffNoLink");
                return 0;
            }
            if (Machine.ActiveDeviceId != deviceId && !EnterForDrag(deviceId, edge))
            {
                NoteDragFailed(deviceId, "handoffNoLink");
                _effects.Add(new Effect.Log("handoff enter failed id=" + id));
                return 0;
            }
            if (_links.TryGetValue(deviceId, out var link))
            {
                var devW = Math.Max(1, link.Config.ScreenWidth);
                var devH = Math.Max(1, link.Config.ScreenHeight);
                var gx = Math.Clamp(Machine.X - Math.Min(48, Math.Max(1, physW / 4)), 0, devW - 1);
                var gy = Math.Clamp(Machine.Y - 16, 0, devH - 1);
                SendReliable(link, PacketType.HandoffGeometry, HandoffPayloads.WriteGeometry(new HandoffPayloads.Geometry(
                    id, gx, gy,
                    (ushort)Math.Clamp(physW, 1, 65535),
                    (ushort)Math.Clamp(physH, 1, 65535),
                    (ushort)Math.Clamp(dipW, 1, 65535),
                    (ushort)Math.Clamp(dipH, 1, 65535))));
            }
            _effects.Add(new Effect.WindowDragCommit(deviceId, id, edge));
            _effects.Add(new Effect.Log($"handoff offered id={id} device={deviceId} edge={edge}"));
            return id;
        }
    }

    public void ReleaseDrag(string deviceId, string reason)
    {
        lock (_gate)
            NoteDragFailed(deviceId, string.IsNullOrEmpty(reason) ? "handoffNoLink" : reason);
    }

    private void NoteDragFailed(string deviceId, string reason)
    {
        _dragArmed = false;
        _dragGaveUp = true;
        _dragPush = 0;
        DragFailureKey = reason;
        _effects.Add(new Effect.WindowDragFailed(deviceId, reason));
        _effects.Add(new Effect.Log("handoff failed " + reason + " device=" + deviceId));
    }

    private bool EnterForDrag(string deviceId, ScreenEdge edge)
    {
        var vx = _seatX;
        var vy = _seatY;
        switch (edge)
        {
            case ScreenEdge.Right: vx = Machine.OriginX + Machine.ScreenW; break;
            case ScreenEdge.Left: vx = Machine.OriginX - 1; break;
            case ScreenEdge.Bottom: vy = Machine.OriginY + Machine.ScreenH; break;
            case ScreenEdge.Top: vy = Machine.OriginY - 1; break;
        }
        var move = Machine.MoveHost(vx, vy);
        if (move is Transition.Enter entered && entered.DeviceId == deviceId)
        {
            _homeSaved = false;
            Grant(entered);
            return true;
        }
        if (Machine.ActiveDeviceId != null && Machine.ActiveDeviceId != deviceId)
            Machine.ForceReturn(_seatX, _seatY);
        if (Machine.ForceEnter(deviceId) is Transition.Enter forced)
        {
            _homeSaved = false;
            Grant(forced);
            return true;
        }
        return Machine.ActiveDeviceId == deviceId;
    }

    private bool TryEdgeSeat(int x, int y, out string deviceId, out ScreenEdge edge)
    {
        deviceId = "";
        edge = ScreenEdge.Right;
        foreach (var device in Machine.Devices)
        {
            if (device.Anchor != AnchorKind.Edge)
                continue;
            // Use the monitor the tablet is attached to, not the bounding box of all monitors: with two monitors
            // the box edge is usually not the tablet's edge, so a dragged window never "reached" the tablet.
            var m = Machine.MonitorFor(device);
            var (lo, hi) = Machine.Segment(device, m);
            if (!SeatPixel(device.PcEdge, x, y, m, lo, hi))
                continue;
            deviceId = device.Id;
            edge = device.PcEdge;
            return true;
        }
        return false;
    }

    /// <summary>On the tablet's edge of its monitor (2 px inside, or up to 40 px into a neighbouring monitor).</summary>
    public static bool SeatPixel(ScreenEdge edge, int x, int y, HostMonitor m, int lo, int hi)
    {
        const int slack = 2;
        const int over = 40;
        var along = edge is ScreenEdge.Left or ScreenEdge.Right ? y : x;
        if (along < lo || along >= hi)
            return false;
        return edge switch
        {
            ScreenEdge.Right => x >= m.Right - slack && x < m.Right + over && y >= m.Y && y < m.Bottom,
            ScreenEdge.Left => x <= m.X + slack - 1 && x > m.X - over && y >= m.Y && y < m.Bottom,
            ScreenEdge.Bottom => y >= m.Bottom - slack && y < m.Bottom + over && x >= m.X && x < m.Right,
            ScreenEdge.Top => y <= m.Y + slack - 1 && y > m.Y - over && x >= m.X && x < m.Right,
            _ => false
        };
    }

    private string? DragReject(string deviceId)
    {
        if (!HandoffPolicy.AllowsPcToPhone(Settings.Handoff))
            return "handoffPcOff";
        if (!_links.TryGetValue(deviceId, out var link) || link.Session?.Established != true)
            return "handoffNoLink";
        if ((link.PeerCaps & ProductInfo.HandoffCapability) == 0)
            return "handoffNeedTablet";
        return null;
    }

    private uint OfferLocked(string deviceId, string title, int dpi, int physW, int physH, int dipW, int dipH, ScreenEdge edge)
    {
        if (!HandoffPolicy.AllowsPcToPhone(Settings.Handoff))
            return 0;
        if (!_links.TryGetValue(deviceId, out var link) || link.Session?.Established != true)
            return 0;
        if ((link.PeerCaps & ProductInfo.HandoffCapability) == 0)
            return 0;
        var id = ++_handoffSeq;
        if (id == 0) id = ++_handoffSeq;
        var offer = new HandoffPayloads.Offer(
            id,
            HandoffPayloads.PcToPhone,
            (uint)Math.Clamp(dpi, 96, 480),
            (ushort)Math.Clamp(physW, 1, 65535),
            (ushort)Math.Clamp(physH, 1, 65535),
            (ushort)Math.Clamp(dipW, 1, 65535),
            (ushort)Math.Clamp(dipH, 1, 65535),
            HandoffPayloads.CodecH264,
            (byte)edge,
            title ?? "");
        SendReliable(link, PacketType.HandoffOffer, HandoffPayloads.WriteOffer(offer));
        return id;
    }

    public void AnnounceCapabilities()
    {
        lock (_gate)
        {
            foreach (var link in _links.Values)
            {
                if (link.Session?.Established == true)
                    SendHello(link);
            }
        }
    }

    public void SendHandoff(string deviceId, PacketType type, byte[] payload)
    {
        lock (_gate)
        {
            if (_links.TryGetValue(deviceId, out var link))
                SendReliable(link, type, payload);
        }
    }

    public void SendHandoffFast(string deviceId, PacketType type, byte[] payload)
    {
        lock (_gate)
        {
            if (_links.TryGetValue(deviceId, out var link))
                SendFast(link, type, payload);
        }
    }

    /// <summary>True when <paramref name="address"/> is the current direct (non-relay) address of a paired, live link.</summary>
    public bool IsPairedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        lock (_gate)
            return _links.Values.Any(l => l.Session != null && l.RelayPeer == null && l.Remote != null
                && (l.Remote.Address.IsIPv4MappedToIPv6 ? l.Remote.Address.MapToIPv4() : l.Remote.Address).Equals(address));
    }

    public byte[]? MediaReceiveKeyFor(string deviceId)
    {
        lock (_gate)
            return _links.TryGetValue(deviceId, out var link) ? link.Session?.MediaReceiveKey() : null;
    }

    public byte[]? MediaKeyFor(string deviceId)
    {
        lock (_gate)
            return _links.TryGetValue(deviceId, out var link) ? link.Session?.MediaSendKey() : null;
    }

    /// <summary>One line per link for the handoff self-test and the diagnostics log.</summary>
    public IReadOnlyList<string> HandoffLinkReport()
    {
        lock (_gate)
        {
            return _links.Values.Select(l =>
                $"device={l.Config.Id} name={l.Config.Name} established={l.Session?.Established == true} caps=0x{l.PeerCaps:X2} " +
                $"tabletHandoff={(l.PeerCaps & ProductInfo.HandoffCapability) != 0} relay={l.RelayPeer != null} remote={l.Remote} edge={l.Config.PcEdge} " +
                $"screen={l.Config.ScreenWidth}x{l.Config.ScreenHeight} keys={l.Session?.MediaSendKey() != null}").ToList();
        }
    }

    /// <summary>Why a handoff to <paramref name="deviceId"/> would be refused right now, or null when it can start.</summary>
    public string? HandoffBlocker(string deviceId)
    {
        lock (_gate)
            return DragReject(deviceId);
    }

    public string? DeviceOnEdge(ScreenEdge edge)
    {
        lock (_gate)
        {
            var name = edge.ToString();
            foreach (var link in _links.Values)
            {
                if (link.Session?.Established == true && string.Equals(link.Config.PcEdge, name, StringComparison.OrdinalIgnoreCase))
                    return link.Config.Id;
            }
            return _links.Values.FirstOrDefault(l => l.Session?.Established == true)?.Config.Id;
        }
    }

    /// <summary>Marks every loopback (USB-tunnelled) link disconnected so a fresh session can replace it.</summary>
    public void DropLoopbackLinks()
    {
        lock (_gate)
        {
            foreach (var link in _links.Values)
            {
                var addr = link.Remote?.Address.ToString() ?? link.Config.Address ?? "";
                if (!(addr.Contains("127.0.0.1") || addr.Contains("::1")) || link.Status is not ("connected" or "paired"))
                    continue;
                link.Status = "disconnected";
                if (Machine.ActiveDeviceId == link.Config.Id)
                    ReturnToHost(link);
                else
                    ReleaseModifiers(link, includeHeldKeys: true);
            }
            Touch();
        }
    }

    public void Tick(long nowMs)
    {
        lock (_gate)
        {
            ReleaseHeldClip(nowMs);
            if (PairingOpen && nowMs > _windowUntil)
            {
                PairingOpen = false;
                Touch();
            }
            foreach (var link in _links.Values)
            {
                if (link.Session == null)
                    continue;
                // Liveness: the host pings every HeartbeatMs and the phone answers. A cable pull (adb reverse
                // half-open socket) used to leave the link "connected" forever, so the USB conductor never redid
                // the setup and only an app restart recovered. Mark it disconnected so reconnect can start.
                if (_options.HeartbeatMs > 0 && link.Status == "connected" && link.LastRx > 0
                    && nowMs - link.LastRx > Math.Max(5000, _options.HeartbeatMs * 5))
                {
                    link.Status = "disconnected";
                    LinkLost?.Invoke(link.Config.Id);
                    if (Machine.ActiveDeviceId == link.Config.Id)
                        ReturnToHost(link);
                    else
                        ReleaseModifiers(link, includeHeldKeys: true);
                    Touch();
                }
                foreach (var packet in link.Session.Poll(nowMs))
                    Enqueue(link, packet);
                if (link.Punch != null && link.Path == LinkChoice.Pending)
                {
                    var choice = link.Punch.Evaluate(nowMs, Settings.InternetEnabled, 2500);
                    if (choice != LinkChoice.Pending)
                    {
                        link.Path = choice;
                        if (choice is LinkChoice.Direct or LinkChoice.Lan)
                        {
                            var hit = link.Punch.SawLan ? link.Punch.PeerLan : link.Punch.PeerMapped;
                            if (hit != null)
                            {
                                link.Remote = hit;
                                link.Config.Address = hit.Address.ToString();
                                link.Config.Port = hit.Port;
                            }
                        }
                        Touch();
                    }
                }
            }
        }
    }

    public void ToggleLock()
    {
        lock (_gate)
            ToggleLockCore();
    }

    public void Cycle()
    {
        lock (_gate) CycleUnlocked();
    }

    public void SetLocalClipboard(string? text)
    {
        lock (_gate)
        {
            if (!Settings.ClipboardShare)
                return;
            text ??= "";
            if (_suppressClip)
            {
                _suppressClip = false;
                return;
            }
            if (text == _lastClip || text == _heldClip)
                return;
            if (Encoding.UTF8.GetByteCount(text) > ProductInfo.MaxClipboardBytes)
                return;
            var now = _clock();
            if (_clipArmed && now - _clipSentAt < 200)
            {
                _heldClip = text;
                return;
            }
            EmitClip(text, now);
        }
    }

    private void EmitClip(string text, long now)
    {
        _heldClip = null;
        _lastClip = text;
        _clipArmed = true;
        _clipSentAt = now;
        var payload = Payloads.Clipboard(++_clipId, Encoding.UTF8.GetBytes(text));
        foreach (var link in _links.Values)
        {
            if (link.Session?.Established == true)
                SendReliable(link, PacketType.Clipboard, payload);
        }
    }

    private void ReleaseHeldClip(long now)
    {
        if (_heldClip == null || (_clipArmed && now - _clipSentAt < 200))
            return;
        EmitClip(_heldClip, now);
    }

    public void OfferLocalFiles(IReadOnlyList<string> paths, bool cut)
    {
        lock (_gate)
        {
            if (!Settings.FileTransfer)
                return;
            foreach (var link in _links.Values.Where(l => l.Session?.Established == true))
            {
                foreach (var path in paths)
                    _files.Queue(link.Config.Id, path, cut);
            }
            FlushFiles();
        }
    }

    public void StageInternet(string deviceId, string relayPeer, IPEndPoint? mapped, IPEndPoint? lan)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return;
            _staged[deviceId] = new StagedPeer { RelayPeer = relayPeer, Mapped = mapped, Lan = lan };
            if (_links.TryGetValue(deviceId, out var link))
                AttachStaged(link, deviceId);
        }
    }

    public IReadOnlyList<IPEndPoint> StagedTargets()
    {
        lock (_gate)
        {
            var list = new List<IPEndPoint>();
            foreach (var staged in _staged.Values)
            {
                if (staged.Lan != null)
                    list.Add(staged.Lan);
                if (staged.Mapped != null && !list.Any(e => e.Port == staged.Mapped.Port && e.Address.Equals(staged.Mapped.Address)))
                    list.Add(staged.Mapped);
            }
            foreach (var link in _links.Values)
            {
                if (link.Punch == null)
                    continue;
                foreach (var target in link.Punch.Targets())
                {
                    if (!list.Any(e => e.Port == target.Port && e.Address.Equals(target.Address)))
                        list.Add(target);
                }
            }
            return list;
        }
    }

    public void BeginInternet(string deviceId)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(deviceId, out var link))
                return;
            link.Punch = new CandidateExchange { StartedMs = _clock(), PeerLan = link.OnLan ? link.Remote : null };
            link.Path = LinkChoice.Pending;
            Touch();
        }
    }

    public void SetPeerMapped(string deviceId, IPEndPoint mapped)
    {
        lock (_gate)
        {
            if (_links.TryGetValue(deviceId, out var link) && link.Punch != null)
                link.Punch.PeerMapped = mapped;
        }
    }

    public void NotePunchFrom(string deviceId, IPEndPoint from)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(deviceId, out var link) || link.Punch == null)
                return;
            link.Punch.NoteInbound(from);
            var choice = link.Punch.Evaluate(_clock(), Settings.InternetEnabled, 2500);
            if (choice is LinkChoice.Direct or LinkChoice.Lan)
            {
                link.Remote = from;
                link.Path = choice;
                link.Config.Address = from.Address.ToString();
                link.Config.Port = from.Port;
                Touch();
            }
        }
    }

    public IReadOnlyList<IPEndPoint> PunchTargets(string deviceId)
    {
        lock (_gate)
            return _links.TryGetValue(deviceId, out var link) && link.Punch != null
                ? link.Punch.Targets()
                : Array.Empty<IPEndPoint>();
    }

    public string PreviewCross(int x, int y)
    {
        var probe = new EdgeMachine();
        probe.SetScreen(Machine.OriginX, Machine.OriginY, Machine.ScreenW, Machine.ScreenH);
        probe.SetDevices(Machine.Devices.ToArray());
        return PlacementParser.Describe(probe.MoveHost(x, y));
    }

    public bool TryOutsidePoint(string deviceId, out int x, out int y)
    {
        x = y = 0;
        var device = Machine.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null)
            return false;
        var along = device.OffsetPx + 10;
        if (device.Anchor == AnchorKind.Corner)
        {
            x = device.Corner is ScreenCorner.TopRight or ScreenCorner.BottomRight ? Machine.OriginX + Machine.ScreenW : Machine.OriginX - 1;
            y = device.Corner is ScreenCorner.BottomLeft or ScreenCorner.BottomRight ? Machine.OriginY + Machine.ScreenH : Machine.OriginY - 1;
            return true;
        }
        switch (device.PcEdge)
        {
            case ScreenEdge.Left:
                x = Machine.OriginX - 1;
                y = Machine.OriginY + along;
                break;
            case ScreenEdge.Right:
                x = Machine.OriginX + Machine.ScreenW;
                y = Machine.OriginY + along;
                break;
            case ScreenEdge.Top:
                y = Machine.OriginY - 1;
                x = Machine.OriginX + along;
                break;
            default:
                y = Machine.OriginY + Machine.ScreenH;
                x = Machine.OriginX + along;
                break;
        }
        return true;
    }

    public List<Outbound> DrainOutbound()
    {
        lock (_gate)
        {
            var copy = _out.ToList();
            _out.Clear();
            return copy;
        }
    }

    public List<Effect> DrainEffects()
    {
        lock (_gate)
        {
            var copy = _effects.ToList();
            _effects.Clear();
            return copy;
        }
    }

    public List<DeviceSnapshot> SnapshotDevices()
    {
        lock (_gate)
        {
            return _links.Values.Select(link => new DeviceSnapshot(
                link.Config.Id,
                link.Config.Name,
                link.Status,
                link.Config.Address,
                link.Config.Port,
                link.Path,
                link.Session?.LastRttMs,
                Machine.ActiveDeviceId == link.Config.Id,
                link.Config.ScreenWidth,
                link.Config.ScreenHeight,
                link.Config.PcEdge,
                link.Config.ReturnEdge,
                link.Config.Anchor,
                link.Config.Corner,
                link.Config.OffsetPx,
                link.Config.SpanPx,
                link.Config.CornerSizePx,
                link.Config.PointerSpeed,
                link.Config.Monitor)).ToList();
        }
    }

    public List<DiscoveredPeer> SnapshotDiscovered()
    {
        lock (_gate) return _discovered.Values.OrderBy(d => d.Name).ToList();
    }

    public void SetArrangeScale(string mode)
    {
        lock (_gate)
        {
            Settings.ArrangeScale = mode == "uniform" ? "uniform" : "resolution";
            Machine.UniformScale = Settings.ArrangeScale == "uniform";
        }
        Save();
        Touch();
    }

    public void Save()
    {
        if (!string.IsNullOrWhiteSpace(_settingsPath))
            SettingsStore.Save(_settingsPath, Settings);
    }

    public void Dispose()
    {
        foreach (var link in _links.Values)
            link.Pairing?.Dispose();
        _files.Dispose();
    }

    private void HandleClear(IPEndPoint from, PacketHeader header, byte[] payload, long nowMs, string? relayPeer)
    {
        if (header.Type == PacketType.PairInit)
        {
            if (!PairingOpen || nowMs > _windowUntil || _fails >= _options.MaxPairFailures)
                return;
            if (!Payloads.TryPairInit(payload, out var init))
                return;
            var id = string.IsNullOrWhiteSpace(init.Id) ? Guid.NewGuid().ToString("N") : init.Id;
            var link = EnsureLink(id, string.IsNullOrWhiteSpace(init.Name) ? "Android" : init.Name, from, relayPeer);
        if (string.IsNullOrEmpty(relayPeer))
            NotePunchUnlocked(link, from);
            if (link.Pairing is { Failed: false, Established: false })
            {
                var again = link.Pairing.Consume(PacketType.PairInit, payload);
                if (!link.Pairing.Failed)
                {
                    SendClear(link, again, nowMs, from);
                    return;
                }
                link.Pairing.Dispose();
                link.Pairing = null;
            }
            link.Pairing?.Dispose();
            link.Pairing = PairingHandshake.Create(PairRole.Host, CurrentPin, _random, NewSessionId());
            SendClear(link, link.Pairing.Consume(PacketType.PairInit, payload), nowMs, from);
            link.Status = "pairing";
            Touch();
            return;
        }
        var known = string.IsNullOrEmpty(relayPeer) ? FindByRemote(from) : FindByRelay(relayPeer) ?? FindByRemote(from);
        if (known == null)
            return;
        if (known.Pairing == null)
        {
            if (header.Type == PacketType.PairConfirm && known.Status == "rejected" && known.LastClear != null)
                Enqueue(known, known.LastClear, from);
            return;
        }
        if (known.Session?.Established == true && header.Type == PacketType.PairConfirm && known.LastClear != null)
        {
            Enqueue(known, known.LastClear, from);
            return;
        }
        SendClear(known, known.Pairing.Consume(header.Type, payload), nowMs, from);
        if (known.Pairing.Established)
            RememberRoute(known, from, relayPeer);
        if (known.Pairing.Failed)
        {
            _fails++;
            Log("pairingFailed");
            known.Pairing.Dispose();
            known.Pairing = null;
            known.Status = "rejected";
            if (_fails >= _options.MaxPairFailures)
                PairingOpen = false;
            Touch();
            return;
        }
        if (known.Pairing.Established)
            BringUp(known);
    }

    private void BringUp(Link link)
    {
        var pairing = link.Pairing!;
        link.Session = new WireSession(true, _clock, _options);
        link.Session.Establish(pairing.SendKey!, pairing.ReceiveKey!, pairing.NoncePrefix!, pairing.SessionId);
        link.Status = "paired";
        if (link.Path is not (LinkChoice.Direct or LinkChoice.Lan or LinkChoice.Pending))
        {
            link.Path = !string.IsNullOrEmpty(link.RelayPeer)
                ? LinkChoice.Relay
                : link.OnLan || (link.Remote != null && NetAddresses.IsPrivate(link.Remote.Address))
                    ? LinkChoice.Lan
                    : Settings.InternetEnabled ? LinkChoice.Pending : LinkChoice.Lan;
        }
        if (!string.IsNullOrWhiteSpace(pairing.PeerName))
            link.Config.Name = pairing.PeerName;
        SendHello(link);
        Log("pairingOk", link.Config.Name);
        PairingOpen = false;
        Save();
        Touch();
    }

    private void Apply(Link link, ReceiveResult result, long nowMs)
    {
        foreach (var reply in result.Replies)
            Enqueue(link, reply);
        foreach (var ev in result.Events)
        {
            switch (ev)
            {
                case SessionEvent.HelloReceived hello:
                    var caps = HandoffPolicy.KeepAdvertisedCaps(link.PeerCaps, hello.Capabilities, ProductInfo.HandoffCapability);
                    if (caps != hello.Capabilities)
                        _effects.Add(new Effect.Log("handoff caps kept device=" + link.Config.Id));
                    link.PeerCaps = caps;
                    var oldW = link.Config.ScreenWidth;
                    var oldH = link.Config.ScreenHeight;
                    // Ignore placeholder sizes (an Android display not measured yet reports 1x1).
                    if (hello.Width > 16 && hello.Height > 16)
                    {
                        link.Config.ScreenWidth = hello.Width;
                        link.Config.ScreenHeight = hello.Height;
                    }
                    if (oldW != link.Config.ScreenWidth || oldH != link.Config.ScreenHeight)
                        _effects.Add(new Effect.Log($"device-size id={link.Config.Id} {oldW}x{oldH} -> {link.Config.ScreenWidth}x{link.Config.ScreenHeight}"));
                    if (!string.IsNullOrWhiteSpace(hello.Name)) link.Config.Name = hello.Name;
                    if (!string.IsNullOrWhiteSpace(hello.Id) && hello.Id != link.Config.Id)
                    {
                        // The id was agreed at pairing. Keep the link id stable.
                    }
                    ReloadLayout();
                    if (Machine.ActiveDeviceId == link.Config.Id)
                        Machine.ClampActive(); // rotation while in control: keep the pointer inside the new bounds
                    link.Status = "connected";
                    Touch();
                    break;
                case SessionEvent.EdgeReturned back when Machine.ActiveDeviceId == link.Config.Id:
                    _effects.Add(new Effect.Log($"edge-exit device-reported device={link.Config.Id} at=({back.DeviceX},{back.DeviceY}) model=({Machine.X},{Machine.Y}) size={link.Config.ScreenWidth}x{link.Config.ScreenHeight}"));
                    ReturnToHost(link);
                    break;
                case SessionEvent.ClipboardReceived clip when Settings.ClipboardShare && clip.Text != _lastClip:
                    _lastClip = clip.Text;
                    _suppressClip = true;
                    _effects.Add(new Effect.ClipboardArrived(clip.Text));
                    break;
                case SessionEvent.FileOfferReceived offer when Settings.FileTransfer:
                    _files.OnOffer(link.Config.Id, offer.Id, offer.Name, offer.Size, offer.Cut, ReceivedDirectory);
                    FlushFiles();
                    break;
                case SessionEvent.FileChunkReceived chunk when Settings.FileTransfer:
                    _files.OnChunk(link.Config.Id, chunk.Id, chunk.Offset, chunk.Data);
                    FlushFiles();
                    break;
                case SessionEvent.FileAckReceived ack when Settings.FileTransfer:
                    _files.OnAck(link.Config.Id, ack.Id, ack.Next);
                    FlushFiles();
                    break;
                case SessionEvent.FileDoneReceived done when Settings.FileTransfer:
                    _files.OnDone(link.Config.Id, done.Id, done.Ok);
                    FlushFiles();
                    break;
                case SessionEvent.PongReceived:
                    Touch();
                    break;
                case SessionEvent.HandoffAccepted accepted:
                    _effects.Add(new Effect.HandoffAccepted(link.Config.Id, accepted.Id, accepted.Codec, accepted.ViewW, accepted.ViewH));
                    break;
                case SessionEvent.HandoffGeometryReceived geometry:
                    _effects.Add(new Effect.HandoffGeometry(geometry.Id, geometry.X, geometry.Y, geometry.PhysW, geometry.PhysH, geometry.DipW, geometry.DipH));
                    break;
                case SessionEvent.HandoffInputReceived input:
                    _effects.Add(new Effect.HandoffInput(input.Id, input.Kind, input.X, input.Y, input.Button, input.Down, input.WheelX, input.WheelY, input.Vk, input.Text));
                    break;
                case SessionEvent.HandoffStateReceived state:
                    _effects.Add(new Effect.HandoffState(state.Id, state.Value));
                    break;
                case SessionEvent.HandoffReturned returnedId:
                    _effects.Add(new Effect.HandoffReturned(returnedId.Id));
                    break;
                case SessionEvent.HandoffVideoConfigReceived video:
                    _effects.Add(new Effect.HandoffVideo(video.Id, video.Bitrate, video.Fps, video.Idr));
                    break;
                case SessionEvent.GoodbyeReceived:
                    link.Status = "disconnected";
                    if (Machine.ActiveDeviceId == link.Config.Id)
                        ReturnToHost(link);
                    else
                        ReleaseModifiers(link, includeHeldKeys: true);
                    Touch();
                    break;
            }
        }
    }

    private void Grant(Transition.Enter entered)
    {
        _parkX = entered.ParkX;
        _parkY = entered.ParkY;
        var link = Get(entered.DeviceId);
        if (link != null)
        {
            link.Session?.ClearMouse();
            SendReliable(link, PacketType.GlowStyle, Payloads.GlowStyle(Settings.EdgeGlowColor, Settings.EdgeGlowIntensity));
            SendReliable(link, PacketType.EdgeEnter, Payloads.EdgeEnter((byte)entered.ReturnEdge, entered.X, entered.Y, (ushort)Math.Clamp(entered.DevW, 1, 65535), (ushort)Math.Clamp(entered.DevH, 1, 65535)));
            SendFast(link, PacketType.MouseMove, Payloads.MouseMove(entered.X, entered.Y));
            ReleaseModifiers(link, includeHeldKeys: false);
        }
        else
            ReleaseModifiers(null, includeHeldKeys: false);
        _effects.Add(new Effect.WarpCursor(entered.ParkX, entered.ParkY));
        _effects.Add(new Effect.ControlChanged(entered.DeviceId, entered.X, entered.Y, entered.DevW, entered.DevH));
        Touch();
    }

    private void ReturnToHost(Link? link)
    {
        ReleaseModifiers(link, includeHeldKeys: true);
        var transition = Machine.ReturnFromCurrent();
        if (transition is Transition.Return returned)
        {
            _parkX = returned.HostX;
            _parkY = returned.HostY;
            if (link?.Session != null)
                SendReliable(link, PacketType.EdgeReturn, Payloads.EdgeReturn(returned.HostX, returned.HostY, returned.DeviceX, returned.DeviceY));
            _effects.Add(new Effect.WarpCursor(returned.HostX, returned.HostY));
            _effects.Add(new Effect.ControlChanged(null, returned.HostX, returned.HostY));
        }
        Touch();
    }

    private void CycleUnlocked()
    {
        if (Machine.Locked)
            return;
        var ids = OrderedIds();
        if (ids.Count == 0)
            return;
        if (Machine.ActiveDeviceId == null)
        {
            RememberHome();
            EnterAt(ids[0]);
            return;
        }
        var index = ids.IndexOf(Machine.ActiveDeviceId);
        var previousId = Machine.ActiveDeviceId;
        RememberDevicePoint(previousId);
        var previous = Get(previousId);
        if (index < 0 || index + 1 >= ids.Count)
        {
            ReturnHome(previous);
            return;
        }
        if (previous != null)
        {
            ReleaseModifiers(previous, includeHeldKeys: true);
            SendReliable(previous, PacketType.EdgeReturn, Payloads.EdgeReturn(HomeX(), HomeY(), Machine.X, Machine.Y));
        }
        EnterAt(ids[index + 1]);
    }

    private void JumpTo(int number)
    {
        if (Machine.Locked)
            return;
        var ids = OrderedIds();
        if (number < 1 || number > ids.Count)
            return;
        var target = ids[number - 1];
        if (Machine.ActiveDeviceId == target)
        {
            RememberDevicePoint(target);
            ReturnHome(Get(target));
            return;
        }
        if (Machine.ActiveDeviceId == null)
            RememberHome();
        else
        {
            var previousId = Machine.ActiveDeviceId;
            RememberDevicePoint(previousId);
            var previous = Get(previousId);
            if (previous != null)
            {
                ReleaseModifiers(previous, includeHeldKeys: true);
                SendReliable(previous, PacketType.EdgeReturn, Payloads.EdgeReturn(HomeX(), HomeY(), Machine.X, Machine.Y));
            }
        }
        EnterAt(target);
    }

    private List<string> OrderedIds() =>
        Settings.Devices.Select(d => d.Id).Where(id => Machine.Devices.Any(p => p.Id == id)).ToList();

    private void RememberHome()
    {
        _homeSaved = true;
        _homeX = Machine.X;
        _homeY = Machine.Y;
    }

    private int HomeX() => _homeSaved ? _homeX : Machine.X;
    private int HomeY() => _homeSaved ? _homeY : Machine.Y;

    private void RememberDevicePoint(string id) => _devicePoint[id] = (Machine.X, Machine.Y);

    private void EnterAt(string id)
    {
        var device = Settings.Devices.FirstOrDefault(d => d.Id == id);
        if (device == null)
            return;
        var x = Math.Max(0, device.ScreenWidth / 2);
        var y = Math.Max(0, device.ScreenHeight / 2);
        if (_devicePoint.TryGetValue(id, out var last))
        {
            x = last.X;
            y = last.Y;
        }
        var parkX = _homeSaved ? _homeX : Machine.X;
        var parkY = _homeSaved ? _homeY : Machine.Y;
        if (Machine.ForceEnterAt(id, x, y, parkX, parkY) is Transition.Enter entered)
            Grant(entered);
    }

    private void ReturnHome(Link? link)
    {
        if (!_homeSaved)
        {
            ReturnToHost(link);
            return;
        }
        var devX = Machine.X;
        var devY = Machine.Y;
        var transition = Machine.ForceReturn(_homeX, _homeY);
        _homeSaved = false;
        if (transition is not Transition.Return returned)
            return;
        _parkX = returned.HostX;
        _parkY = returned.HostY;
        ReleaseModifiers(link, includeHeldKeys: true);
        if (link?.Session != null)
            SendReliable(link, PacketType.EdgeReturn, Payloads.EdgeReturn(returned.HostX, returned.HostY, devX, devY));
        _effects.Add(new Effect.WarpCursor(returned.HostX, returned.HostY));
        _effects.Add(new Effect.ControlChanged(null, returned.HostX, returned.HostY));
        Touch();
    }

    private bool IsHotkey(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
    {
        if (HotkeyCapture)
            return false;
        var cycle = HotkeyParser.Parse(Settings.HotkeySwitch);
        if (Settings.HotkeySwitchEnabled && cycle.VirtualKey != 0 && cycle.Matches(virtualKey, ctrl, alt, shift, win))
            return true;
        if (Settings.DeviceNumberHotkeys && HotkeyPolicy.TryNumberChord(virtualKey, ctrl, alt, shift, win, out _))
            return true;
        var send = HotkeyParser.Parse(Settings.HotkeySendWindow);
        if (send.VirtualKey != 0 && HandoffPolicy.AllowsPcToPhone(Settings.Handoff) && send.Matches(virtualKey, ctrl, alt, shift, win))
            return true;
        var locker = HotkeyParser.Parse(Settings.HotkeyLock);
        return locker.VirtualKey != 0 && locker.Matches(virtualKey, ctrl, alt, shift, win);
    }

    private bool ApplyKey(int virtualKey, bool down, bool ctrl, bool alt, bool shift, bool win, ModifierLedger.Decision decision)
    {
        if (HotkeyCapture)
            return false;
        if (decision.Hotkey && down)
        {
            var cycle = HotkeyParser.Parse(Settings.HotkeySwitch);
            if (Settings.HotkeySwitchEnabled && cycle.VirtualKey != 0 && cycle.Matches(virtualKey, ctrl, alt, shift, win))
            {
                ReleaseModifiers(ActiveLink(), includeHeldKeys: false);
                CycleUnlocked();
                return true;
            }
            if (Settings.DeviceNumberHotkeys && HotkeyPolicy.TryNumberChord(virtualKey, ctrl, alt, shift, win, out var number))
            {
                ReleaseModifiers(ActiveLink(), includeHeldKeys: false);
                JumpTo(number);
                return true;
            }
            var send = HotkeyParser.Parse(Settings.HotkeySendWindow);
            if (send.VirtualKey != 0 && HandoffPolicy.AllowsPcToPhone(Settings.Handoff) && send.Matches(virtualKey, ctrl, alt, shift, win))
            {
                SendWindowHotkey?.Invoke();
                return true;
            }
            var locker = HotkeyParser.Parse(Settings.HotkeyLock);
            if (locker.VirtualKey != 0 && locker.Matches(virtualKey, ctrl, alt, shift, win))
            {
                ReleaseModifiers(ActiveLink(), includeHeldKeys: false);
                ToggleLockCore();
                return true;
            }
        }
        if (decision.Hotkey || decision.Swallow && !decision.Forward)
            return decision.Hotkey || decision.Swallow;
        if (!decision.Forward)
            return false;
        if (Machine.ActiveDeviceId is not string id || Get(id) is not { } link || link.Session == null)
            return decision.Swallow;
        KeyMap.TryHid(virtualKey, out var hid);
        var payload = Payloads.Key((ushort)virtualKey, (ushort)hid, KeyMap.ModifierBits(ctrl, alt, shift, win), down, KeyMap.Codepoint(virtualKey, shift));
        SendReliable(link, PacketType.KeyEvent, payload);
        return true;
    }

    private Link? ActiveLink() =>
        Machine.ActiveDeviceId is string id ? Get(id) : null;

    private void ToggleLockCore()
    {
        Machine.Locked = !Machine.Locked;
        if (Machine.ActiveDeviceId is string id && Get(id) is { } link)
            SendReliable(link, Machine.Locked ? PacketType.Lock : PacketType.Unlock, Array.Empty<byte>());
        Log(Machine.Locked ? "lockedState" : "unlockedState");
        Touch();
    }

    private void ReleaseModifiers(Link? link, bool includeHeldKeys)
    {
        var ups = _modifiers.TakeRemoteUps(includeHeldKeys);
        if (link?.Session?.Established == true)
        {
            foreach (var vk in ups)
                ReleaseKey(link, vk);
        }
        _effects.Add(new Effect.ModifiersReleased());
    }

    private void ReleaseKey(Link link, int virtualKey) =>
        SendReliable(link, PacketType.KeyEvent, Payloads.Key((ushort)virtualKey, 0, 0, false, 0));

    private void Flush(Link link)
    {
        link.Session?.FlushMouse();
        if (link.Session == null)
            return;
        foreach (var packet in link.Session.Poll(_clock()))
            Enqueue(link, packet);
    }

    private void SendHello(Link link)
    {
        var caps = (byte)0x1;
        if (HandoffPolicy.AllowsPcToPhone(Settings.Handoff))
            caps |= ProductInfo.HandoffCapability;
        var hello = Payloads.Hello(1, caps, (ushort)Math.Clamp(Settings.ScreenWidth, 1, 65535), (ushort)Math.Clamp(Settings.ScreenHeight, 1, 65535), (ushort)Settings.ListenPort, Settings.HostId, Settings.DisplayName);
        var marked = new byte[hello.Length + 2];
        hello.CopyTo(marked, 0);
        var license = License;
        marked[^2] = license.Code switch
        {
            "trial" => 1,
            "licensed" when license.Edition == LicenseEdition.Family => 3,
            "licensed" => 2,
            "expired" => 4,
            "invalid" => 5,
            _ => 0
        };
        marked[^1] = (byte)Math.Clamp(license.DaysLeft, 0, 255);
        SendReliable(link, PacketType.Hello, marked);
    }

    private bool TabletReady(string deviceId)
    {
        if (!HandoffPolicy.AllowsPcToPhone(Settings.Handoff))
            return false;
        if (!_links.TryGetValue(deviceId, out var link) || link.Session?.Established != true)
            return false;
        return (link.PeerCaps & ProductInfo.HandoffCapability) != 0;
    }

    private void SendFast(Link link, PacketType type, byte[] payload)
    {
        if (link.Session?.Established != true)
            return;
        Enqueue(link, link.Session.SendUnreliable(type, payload));
    }

    private void SendReliable(Link link, PacketType type, byte[] payload)
    {
        if (link.Session?.Established != true)
            return;
        foreach (var packet in link.Session.SendReliable(type, payload))
            Enqueue(link, packet);
    }

    private void SendClear(Link link, IEnumerable<PairMessage> messages, long nowMs, IPEndPoint? to = null)
    {
        foreach (var message in messages)
        {
            var bytes = PacketCodec.EncodeClear(message.Type, 0, 0, (uint)++link.ClearSeq, 0, (ulong)nowMs * 1000UL, message.Payload);
            link.LastClear = bytes;
            Enqueue(link, bytes, to);
        }
    }

    private void Enqueue(Link link, byte[] packet, IPEndPoint? remote = null)
    {
        PacketsSent++;
        if (_out.Count >= WirePressure.OutboundCap)
        {
            for (var i = 0; i < _out.Count; i++)
            {
                if (WirePressure.IsDroppable(_out[i].Bytes))
                {
                    _out.RemoveAt(i);
                    DroppedOutbound++;
                    break;
                }
            }
        }
        if (_out.Count >= WirePressure.OutboundCap && WirePressure.IsDroppable(packet))
        {
            DroppedOutbound++;
            return;
        }
        var dest = Usable(remote) ? remote : Usable(link.Remote) ? link.Remote : null;
        _out.Add(new Outbound(link.Config.Id, dest, packet, link.RelayPeer));
    }

    /// <summary>
    /// Removes duplicate ids and the placeholder "Android" entries that a pairing attempt left behind
    /// (created at PairInit with no address and never completed), so only real devices are listed.
    /// </summary>
    public static int PruneDevices(AppSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var removed = 0;
        for (var i = 0; i < settings.Devices.Count; i++)
        {
            var d = settings.Devices[i];
            var placeholder = string.Equals(d.Name, "Android", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(d.Address)
                && settings.Devices.Count > 1;
            if (string.IsNullOrWhiteSpace(d.Id) || !seen.Add(d.Id) || placeholder)
            {
                settings.Devices.RemoveAt(i--);
                removed++;
            }
        }
        return removed;
    }

    private Link EnsureLink(string id, string name, IPEndPoint from, string? relayPeer)
    {
        if (_links.TryGetValue(id, out var existing))
        {
            RememberRoute(existing, from, relayPeer);
            if (!string.IsNullOrWhiteSpace(name))
                existing.Config.Name = name;
            return existing;
        }
        // Same tablet with a new id (reinstall / cleared data) or a stale placeholder: adopt that entry
        // instead of adding a second "Android" device, keeping its arrangement.
        var stale = Settings.Devices.FirstOrDefault(d =>
            (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name, "Android", StringComparison.Ordinal))
            && !(_links.TryGetValue(d.Id, out var l) && l.Session?.Established == true));
        if (stale != null && _links.Remove(stale.Id, out var old))
        {
            old.Pairing?.Dispose();
            stale.Id = id;
            if (!string.IsNullOrWhiteSpace(name))
                stale.Name = name;
            var adopted = new Link { Config = stale, Status = "pairing" };
            RememberRoute(adopted, from, relayPeer);
            _links[id] = adopted;
            AttachStaged(adopted, id);
            ReloadLayout();
            return adopted;
        }
        var edge = NextFreeEdge();
        var usable = Usable(from);
        var config = new DeviceConfig
        {
            Id = id,
            Name = name,
            Address = usable ? from.Address.ToString() : "",
            Port = usable ? from.Port : 0,
            PcEdge = edge,
            ReturnEdge = Opposite(edge)
        };
        if (_discovered.TryGetValue(id, out var found))
        {
            if (found.ScreenW > 0) config.ScreenWidth = found.ScreenW;
            if (found.ScreenH > 0) config.ScreenHeight = found.ScreenH;
        }
        Settings.Devices.Add(config);
        var link = new Link { Config = config, Status = "pairing" };
        RememberRoute(link, from, relayPeer);
        _links[id] = link;
        AttachStaged(link, id);
        ReloadLayout();
        return link;
    }

    private void AttachStaged(Link link, string id)
    {
        if (!_staged.TryGetValue(id, out var staged))
            return;
        if (!string.IsNullOrEmpty(staged.RelayPeer))
            link.RelayPeer = staged.RelayPeer;
        link.Punch ??= new CandidateExchange { StartedMs = _clock() };
        if (staged.Mapped != null)
            link.Punch.PeerMapped = staged.Mapped;
        if (staged.Lan != null)
            link.Punch.PeerLan = staged.Lan;
        if (link.Path is not (LinkChoice.Direct or LinkChoice.Lan))
            link.Path = LinkChoice.Pending;
    }

    private void NotePunchUnlocked(Link link, IPEndPoint from)
    {
        if (link.Punch == null)
            return;
        link.Punch.NoteInbound(from);
        var choice = link.Punch.Evaluate(_clock(), Settings.InternetEnabled, 2500);
        if (choice is LinkChoice.Direct or LinkChoice.Lan)
        {
            link.Remote = from;
            link.Path = choice;
            link.Config.Address = from.Address.ToString();
            link.Config.Port = from.Port;
            Touch();
        }
    }

    private void FlushFiles()
    {
        foreach (var packet in _files.Drain(() => ++_fileSeq))
        {
            if (_links.TryGetValue(packet.PeerId, out var link))
                SendReliable(link, packet.Type, packet.Payload);
        }
        foreach (var note in _files.Notes())
            _effects.Add(new Effect.FileProgress(note.Name, note.Done, note.Total, note.Incoming, note.Phase, note.Path));
    }

    private void RememberRoute(Link link, IPEndPoint from, string? relayPeer)
    {
        if (!string.IsNullOrEmpty(relayPeer))
        {
            link.RelayPeer = relayPeer;
            link.OnLan = false;
            if (link.Path is not (LinkChoice.Direct or LinkChoice.Lan))
                link.Path = link.Punch != null ? LinkChoice.Pending : LinkChoice.Relay;
        }
        if (!Usable(from))
            return;
        link.Remote = from;
        link.Config.Address = from.Address.ToString();
        link.Config.Port = from.Port;
        link.OnLan = NetAddresses.IsPrivate(from.Address);
        if (string.IsNullOrEmpty(relayPeer) && link.Path == LinkChoice.Offline)
            link.Path = link.OnLan ? LinkChoice.Lan : LinkChoice.Pending;
    }

    private static bool Usable(IPEndPoint? from) =>
        from != null && from.Port > 0 && !from.Address.Equals(IPAddress.Any) && !from.Address.Equals(IPAddress.None);

    private void ReloadLayout()
    {
        if (MonitorKey.Length > 0)
            foreach (var d in Settings.Devices)
                d.Layouts[MonitorKey] = Saved(d);
        var placements = new List<DevicePlacement>();
        foreach (var device in Settings.Devices)
        {
            if (PlacementParser.TryParse(device, out var placement, out _))
                placements.Add(placement);
        }
        Machine.SetDevices(placements);
        LayoutProblems = PlacementParser.Problems(placements);
    }

    private Link? Get(string? id) => id != null && _links.TryGetValue(id, out var link) ? link : null;

    private Link? FindByRemote(IPEndPoint from)
    {
        Link? addressOnly = null;
        foreach (var link in _links.Values)
        {
            if (link.Remote == null || !link.Remote.Address.Equals(from.Address))
                continue;
            if (link.Remote.Port == from.Port)
                return link;
            addressOnly ??= link;
        }
        return addressOnly;
    }

    private Link? FindByRelay(string? peer)
    {
        if (string.IsNullOrEmpty(peer))
            return null;
        foreach (var link in _links.Values)
        {
            if (string.Equals(link.RelayPeer, peer, StringComparison.Ordinal))
                return link;
        }
        return null;
    }

    private Link? FindBySession(uint sessionId)
    {
        if (sessionId == 0)
            return null;
        foreach (var link in _links.Values)
        {
            if (link.Session?.SessionId == sessionId)
                return link;
        }
        return null;
    }

    private string NextFreeEdge()
    {
        var used = new HashSet<string>(Settings.Devices.Where(d => string.Equals(d.Anchor, "Edge", StringComparison.OrdinalIgnoreCase)).Select(d => d.PcEdge), StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Right", "Left", "Top", "Bottom" })
        {
            if (!used.Contains(name))
                return name;
        }
        return "Right";
    }

    private static string Opposite(string edge) => edge.Trim().ToLowerInvariant() switch
    {
        "left" => "Right",
        "right" => "Left",
        "top" => "Bottom",
        _ => "Top"
    };

    private uint NewSessionId()
    {
        Span<byte> raw = stackalloc byte[4];
        _random.Fill(raw);
        var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw);
        return id == 0 ? 1u : id;
    }

    private string MakePin()
    {
        Span<byte> raw = stackalloc byte[6];
        _random.Fill(raw);
        var chars = new char[6];
        for (var i = 0; i < 6; i++)
            chars[i] = (char)('0' + (raw[i] % 10));
        return new string(chars);
    }

    private void Log(string key, params object[] args)
    {
        var text = args.Length == 0 ? Catalog.Get(Settings.Language, key) : Catalog.Format(Settings.Language, key, args);
        _logs.Add(text);
        if (_logs.Count > 200)
            _logs.RemoveAt(0);
        _effects.Add(new Effect.Log(text));
    }

    private void Touch() => _effects.Add(new Effect.StateChanged());
}
