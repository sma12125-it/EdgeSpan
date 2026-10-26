using System.Net;
using System.Text;
using EdgeSpan.Crypto;
using EdgeSpan.Layout;
using EdgeSpan.Product;
using EdgeSpan.Protocol;
using EdgeSpan.Session;

namespace EdgeSpan.Tests;

public class EdgeAndSessionTests
{
    private static EdgeMachine Machine(params DeviceConfig[] devices)
    {
        var machine = new EdgeMachine();
        machine.SetScreen(0, 0, 1920, 1080);
        var placements = new List<DevicePlacement>();
        foreach (var device in devices)
            Assert.True(PlacementParser.TryParse(device, out var placement, out var error), error);
        foreach (var device in devices)
        {
            Assert.True(PlacementParser.TryParse(device, out var placement, out _));
            placements.Add(placement);
        }
        machine.SetDevices(placements);
        return machine;
    }

    private static DeviceConfig Tablet(string id, string edge = "Right", string back = "Left", int offset = 0, int span = 0, double speed = 1, string anchor = "Edge", string corner = "TopRight", int cornerSize = 120) =>
        new()
        {
            Id = id,
            Name = id,
            PcEdge = edge,
            ReturnEdge = back,
            OffsetPx = offset,
            SpanPx = span,
            PointerSpeed = speed,
            Anchor = anchor,
            Corner = corner,
            CornerSizePx = cornerSize,
            ScreenWidth = 1600,
            ScreenHeight = 1000
        };

    [Fact]
    public void Right_edge_enters_and_returns_with_offset()
    {
        var machine = Machine(Tablet("tab", offset: 100));
        var entered = Assert.IsType<Transition.Enter>(machine.MoveHost(1920, 150));
        Assert.Equal("tab", entered.DeviceId);
        Assert.Equal(2, entered.X);
        // Entry is proportional across the overlapping part of the edge (offset 100 clips the segment).
        Assert.InRange(entered.Y, 50, 52);
        Assert.Equal(1919, entered.ParkX);

        var held = machine.MoveDeviceBy(-5, 0);
        Assert.IsType<Transition.Stay>(held);
        Assert.Equal("tab", machine.ActiveDeviceId);
        Assert.False(machine.ReturnArmed);

        Assert.IsType<Transition.Stay>(machine.MoveDeviceBy(20, 0));
        Assert.True(machine.ReturnArmed);
        var returned = Assert.IsType<Transition.Return>(machine.MoveDeviceBy(-40, 0));
        Assert.Null(machine.ActiveDeviceId);
        Assert.Equal(1919, returned.HostX);
        Assert.Equal(150, returned.HostY);
    }

    [Fact]
    public void Top_edge_enters_the_phone_from_the_bottom()
    {
        var machine = Machine(Tablet("tab", edge: "Top", back: "Bottom"));
        var entered = Assert.IsType<Transition.Enter>(machine.MoveHost(400, -1));
        Assert.Equal(ScreenEdge.Bottom, entered.ReturnEdge);
        Assert.Equal(400, entered.X);
        Assert.Equal(997, entered.Y);
        Assert.Equal(0, entered.ParkY);
    }

    [Fact]
    public void Pointer_speed_accumulates_fractional_deltas()
    {
        var machine = Machine(Tablet("tab", speed: 0.5));
        Assert.IsType<Transition.Enter>(machine.MoveHost(1920, 20));
        Assert.IsType<Transition.Stay>(machine.MoveDeviceBy(20, 0));
        var start = machine.X;
        for (var i = 0; i < 10; i++)
            machine.MoveDeviceBy(1, 0);
        Assert.Equal(start + 5, machine.X);
    }

    [Fact]
    public void Lock_corner_and_split_edge_choose_the_right_device()
    {
        var machine = Machine(
            Tablet("low", span: 400),
            Tablet("high", offset: 400, span: 400),
            Tablet("corner", anchor: "Corner", corner: "TopRight", cornerSize: 80));
        Assert.Equal("low", Assert.IsType<Transition.Enter>(machine.MoveHost(1920, 100)).DeviceId);
        machine.ForceReturn(10, 10);
        Assert.Equal("high", Assert.IsType<Transition.Enter>(machine.MoveHost(1920, 500)).DeviceId);
        machine.ForceReturn(10, 10);
        Assert.IsType<Transition.Stay>(machine.MoveHost(1920, 900));
        Assert.Equal("corner", Assert.IsType<Transition.Enter>(machine.MoveHost(1920, 10)).DeviceId);
        machine.ForceReturn(10, 10);
        machine.Locked = true;
        Assert.IsType<Transition.Blocked>(machine.MoveHost(2000, 100));
        Assert.Null(machine.ActiveDeviceId);

        var problems = PlacementParser.Problems(machine.Devices.Where(d => d.Id != "corner").ToArray());
        Assert.Empty(problems);
        var overlap = PlacementParser.Problems(new[]
        {
            Must("a", 0, 300),
            Must("b", 200, 300)
        });
        Assert.Contains(overlap, p => p.StartsWith("overlap:", StringComparison.Ordinal));
    }

    [Fact]
    public void Pipe_session_pairs_crosses_shares_clipboard_and_switches()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, ListenPort = 45455, Language = "en" };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("tablet1", "Pixel", 1600, 1000, clock.Ms, new SystemRandomBytes(), options);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.8"), 45455);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        Pump(host, client, remote, clock);
        Assert.True(client.Established);
        var device = Assert.Single(settings.Devices);
        Assert.Equal("tablet1", device.Id);
        Assert.Equal(1600, device.ScreenWidth);
        Assert.Equal(1000, device.ScreenHeight);

        host.OnPhysicalMouse(100, 400, 0, 0);
        host.OnPhysicalMouse(1920, 400, 0, 0);
        Pump(host, client, remote, clock);
        Assert.True(client.HasControl);
        Assert.Equal("tablet1", host.Machine.ActiveDeviceId);
        Assert.Equal(2, client.X);
        Assert.Equal(400, client.Y);

        host.OnPhysicalMouse(1919, 400, 20, 0);
        Pump(host, client, remote, clock);
        Assert.True(host.Machine.ReturnArmed);
        host.OnPhysicalMouse(1919, 400, -50, 0);
        Pump(host, client, remote, clock);
        Assert.False(client.HasControl);
        Assert.Null(host.Machine.ActiveDeviceId);
        Assert.Equal(1919, host.Machine.X);

        host.SetLocalClipboard("سلام EdgeSpan");
        Pump(host, client, remote, clock);
        Assert.Equal("سلام EdgeSpan", client.Clipboard);

        var dir = Path.Combine(Path.GetTempPath(), "edgestpan-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "note.txt");
        File.WriteAllText(source, "hello file");
        host.ReceivedDirectory = Path.Combine(dir, "pc");
        client.ReceivedDirectory = Path.Combine(dir, "phone");
        host.OfferLocalFiles(new[] { source }, cut: false);
        for (var i = 0; i < 8; i++)
            Pump(host, client, remote, clock);
        Assert.Contains(client.Files, path => File.ReadAllText(path) == "hello file");

        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Pump(host, client, remote, clock);
        Assert.True(client.HasControl);
        Assert.True(host.OnKey(0x41, true, false, false, true, false));
        Pump(host, client, remote, clock);
        Assert.Contains(client.Keys, k => k.Contains(":65:") && k.StartsWith("down", StringComparison.Ordinal));

        host.ToggleLock();
        var locked = host.Machine.X;
        host.OnPhysicalMouse(1919, 10, -80, 0);
        Assert.Equal("tablet1", host.Machine.ActiveDeviceId);
        Assert.True(client.HasControl || host.Machine.ActiveDeviceId != null);
        _ = locked;
    }

    [Fact]
    public void A_repeated_pair_init_still_completes()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, ListenPort = 45455, Language = "en" };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("tablet1", "Pixel", 1080, 2400, clock.Ms, new SystemRandomBytes(), options);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.2.16"), 43000);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        var init = Assert.Single(client.Out);
        client.Out.Clear();
        host.Ingest(remote, init, clock.Now);
        host.Ingest(remote, init, clock.Now);
        foreach (var challenge in host.DrainOutbound())
            client.Ingest(challenge.Bytes, clock.Now);
        Assert.NotEmpty(client.Out);
        Pump(host, client, remote, clock);
        Assert.True(client.Established);
        Assert.Contains(Assert.Single(host.SnapshotDevices()).Status, new[] { "paired", "connected" });
    }

    [Fact]
    public void Pairing_replies_and_edge_events_go_back_through_the_relay_peer()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, InternetEnabled = true };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("tablet1", "Pixel", 1080, 2400, clock.Ms, new SystemRandomBytes(), options);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        var bogus = new IPEndPoint(IPAddress.Any, 0);
        var established = false;
        for (var i = 0; i < 16 && !established; i++)
        {
            var inbound = client.Out.ToArray();
            client.Out.Clear();
            foreach (var packet in inbound)
                host.Ingest(bogus, packet, clock.Now, "relay-host");
            foreach (var packet in host.DrainOutbound())
            {
                Assert.Equal("relay-host", packet.RelayPeer);
                client.Ingest(packet.Bytes, clock.Now);
            }
            established = client.Established;
            clock.Now += 20;
        }
        Assert.True(established);
        Assert.True(host.CrossOnce());
        Assert.Contains(host.DrainOutbound(), packet => packet.RelayPeer == "relay-host");
        Assert.Equal("tablet1", host.Machine.ActiveDeviceId);
    }

    [Fact]
    public void A_wrong_pair_code_is_rejected_without_throwing()
    {
        var clock = new MutableClock();
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 800, ScreenHeight = 600 };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes());
        using var client = new ClientNode("phone", "Phone", 100, 100, clock.Ms, new SystemRandomBytes());
        host.OpenPairingWindow();
        if (host.CurrentPin == "000000")
            host.OpenPairingWindow();
        client.StartPairing("000000");
        var remote = new IPEndPoint(IPAddress.Loopback, 9);
        for (var i = 0; i < 8; i++)
        {
            var inbound = client.Out.ToArray();
            client.Out.Clear();
            foreach (var packet in inbound)
                host.Ingest(remote, packet, clock.Now);
            foreach (var packet in host.DrainOutbound())
                client.Ingest(packet.Bytes, clock.Now);
        }
        Assert.False(client.Established);
        Assert.Equal("rejected", Assert.Single(host.SnapshotDevices()).Status);
    }

    [Fact]
    public void Udp_loopback_delivers_an_encrypted_move()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 30 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080 };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("phone", "Phone", 1080, 2400, clock.Ms, new SystemRandomBytes(), options);
        using var hostUdp = new EdgeSpan.Net.UdpTransport(0);
        using var clientUdp = new EdgeSpan.Net.UdpTransport(0);
        var hostEp = new IPEndPoint(IPAddress.Loopback, hostUdp.Port);
        var clientEp = new IPEndPoint(IPAddress.Loopback, clientUdp.Port);
        var clientInbox = new List<byte[]>();
        var hostInbox = new List<byte[]>();
        clientUdp.Received += (_, data) => { lock (clientInbox) clientInbox.Add(data); };
        hostUdp.Received += (_, data) => { lock (hostInbox) hostInbox.Add(data); };

        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        var ready = Spin(host, client, hostUdp, clientUdp, hostEp, clientEp, hostInbox, clientInbox, clock, () => client.Established && settings.Devices.Count == 1);
        Assert.True(ready, "pairing did not finish over UDP");

        host.OnPhysicalMouse(1920, 80, 0, 0);
        var crossed = Spin(host, client, hostUdp, clientUdp, hostEp, clientEp, hostInbox, clientInbox, clock, () => client.HasControl);
        Assert.True(crossed, "edge cross was not delivered over UDP");
        Assert.Equal("phone", host.Machine.ActiveDeviceId);
    }

    private static bool Spin(HostController host, ClientNode client, EdgeSpan.Net.UdpTransport hostUdp, EdgeSpan.Net.UdpTransport clientUdp, IPEndPoint hostEp, IPEndPoint clientEp, List<byte[]> hostInbox, List<byte[]> clientInbox, MutableClock clock, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            clock.Now += 5;
            host.Tick(clock.Now);
            client.Tick(clock.Now);
            foreach (var packet in host.DrainOutbound())
            {
                if (packet.Remote != null)
                    hostUdp.Send(packet.Remote, packet.Bytes);
                else
                    hostUdp.Send(clientEp, packet.Bytes);
            }
            byte[][] fromClient;
            lock (client.Out)
            {
                fromClient = client.Out.ToArray();
                client.Out.Clear();
            }
            foreach (var packet in fromClient)
                clientUdp.Send(hostEp, packet);
            byte[][] toClient;
            byte[][] toHost;
            lock (clientInbox) { toClient = clientInbox.ToArray(); clientInbox.Clear(); }
            lock (hostInbox) { toHost = hostInbox.ToArray(); hostInbox.Clear(); }
            foreach (var packet in toClient)
                client.Ingest(packet, clock.Now);
            foreach (var packet in toHost)
                host.Ingest(clientEp, packet, clock.Now);
            if (done())
                return true;
            Thread.Sleep(15);
        }
        return done();
    }

    private static void Pump(HostController host, ClientNode client, IPEndPoint remote, MutableClock clock)
    {
        for (var i = 0; i < 80; i++)
        {
            host.Tick(clock.Now);
            client.Tick(clock.Now);
            var outbound = host.DrainOutbound();
            var inbound = client.Out.ToArray();
            client.Out.Clear();
            if (outbound.Count == 0 && inbound.Length == 0)
                break;
            foreach (var packet in outbound)
                client.Ingest(packet.Bytes, clock.Now);
            foreach (var packet in inbound)
                host.Ingest(remote, packet, clock.Now);
        }
    }

    private static DevicePlacement Must(string id, int offset, int span)
    {
        Assert.True(PlacementParser.TryParse(Tablet(id, offset: offset, span: span), out var placement, out _));
        return placement;
    }
}
