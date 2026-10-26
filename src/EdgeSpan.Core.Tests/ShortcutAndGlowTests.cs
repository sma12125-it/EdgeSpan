using System.Net;
using EdgeSpan.Crypto;
using EdgeSpan.Input;
using EdgeSpan.Layout;
using EdgeSpan.Product;
using EdgeSpan.Session;

namespace EdgeSpan.Tests;

public class ShortcutAndGlowTests
{
    [Fact]
    public void Old_default_switch_chord_becomes_ctrl_alt_space()
    {
        var path = Path.Combine(Path.GetTempPath(), "edgestpan-hotkey-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"schema":1,"hotkeySwitch":"Ctrl+Alt+S","hotkeyLock":"Ctrl+Alt+K"}""");
        var settings = SettingsStore.Load(path);
        Assert.Equal("Ctrl+Alt+Space", settings.HotkeySwitch);
        Assert.Equal("Ctrl+Alt+K", settings.HotkeyLock);
        File.Delete(path);
    }

    [Fact]
    public void A_custom_switch_chord_is_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), "edgestpan-hotkey-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"hotkeySwitch":"Ctrl+Alt+K"}""");
        Assert.Equal("Ctrl+Alt+K", SettingsStore.Load(path).HotkeySwitch);
        File.Delete(path);
    }

    [Fact]
    public void Number_chords_conflict_with_the_switch_shortcut()
    {
        Assert.False(HotkeyPolicy.Conflicts("Ctrl+Alt+Space", "Ctrl+Alt+L", true));
        Assert.True(HotkeyPolicy.Conflicts("Ctrl+Alt+L", "Ctrl+Alt+L", true));
        Assert.True(HotkeyPolicy.Conflicts("Ctrl+Alt+1", "Ctrl+Alt+L", true));
        Assert.False(HotkeyPolicy.Conflicts("Ctrl+Alt+1", "Ctrl+Alt+L", false));
    }

    [Fact]
    public void Shortcut_restores_the_pc_cursor_and_remembers_the_device_point()
    {
        var settings = new AppSettings { ScreenWidth = 1920, ScreenHeight = 1080 };
        settings.Devices.Add(new DeviceConfig
        {
            Id = "tab",
            Name = "Tab",
            PcEdge = "Right",
            ReturnEdge = "Left",
            ScreenWidth = 1600,
            ScreenHeight = 1000
        });
        using var host = new HostController(settings, () => 0, new SystemRandomBytes(), new RuntimeOptions());
        host.OnPhysicalMouse(400, 300, 0, 0);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Assert.Equal("tab", host.Machine.ActiveDeviceId);
        Assert.Equal(800, host.Machine.X);
        Assert.Equal(500, host.Machine.Y);
        Assert.Equal((400, 300), host.CursorPark);
        host.OnPhysicalMouse(400, 300, 40, -15);
        Assert.Equal(840, host.Machine.X);
        Assert.Equal(485, host.Machine.Y);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Assert.Null(host.Machine.ActiveDeviceId);
        Assert.Equal(400, host.Machine.X);
        Assert.Equal(300, host.Machine.Y);
        Assert.True(host.OnKey(0x20, false, false, false, false, false));
        Assert.True(host.OnKey(0x31, true, true, true, false, false));
        Assert.Equal("tab", host.Machine.ActiveDeviceId);
        Assert.Equal(840, host.Machine.X);
        Assert.Equal(485, host.Machine.Y);
        Assert.Equal((400, 300), host.CursorPark);
        Assert.True(host.OnKey(0x31, true, true, true, false, false));
        Assert.Equal(400, host.Machine.X);
        Assert.Equal(300, host.Machine.Y);
    }

    [Fact]
    public void Two_devices_cycle_in_order_and_a_digit_jumps()
    {
        var settings = new AppSettings { ScreenWidth = 1920, ScreenHeight = 1080 };
        settings.Devices.Add(new DeviceConfig { Id = "a", Name = "A", PcEdge = "Right", ReturnEdge = "Left", ScreenWidth = 800, ScreenHeight = 600 });
        settings.Devices.Add(new DeviceConfig { Id = "b", Name = "B", PcEdge = "Left", ReturnEdge = "Right", ScreenWidth = 900, ScreenHeight = 700 });
        using var host = new HostController(settings, () => 0, new SystemRandomBytes(), new RuntimeOptions());
        host.OnPhysicalMouse(120, 80, 0, 0);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Assert.Equal("a", host.Machine.ActiveDeviceId);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Assert.Equal("b", host.Machine.ActiveDeviceId);
        Assert.Equal(450, host.Machine.X);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Assert.Null(host.Machine.ActiveDeviceId);
        Assert.Equal(120, host.Machine.X);
        Assert.Equal(80, host.Machine.Y);
        Assert.True(host.OnKey(0x32, true, true, true, false, false));
        Assert.Equal("b", host.Machine.ActiveDeviceId);
    }

    [Fact]
    public void Disabled_switch_shortcut_does_not_leave_the_pc()
    {
        var settings = new AppSettings { ScreenWidth = 800, ScreenHeight = 600, HotkeySwitchEnabled = false };
        settings.Devices.Add(new DeviceConfig { Id = "a", Name = "A", PcEdge = "Right", ReturnEdge = "Left", ScreenWidth = 400, ScreenHeight = 400 });
        using var host = new HostController(settings, () => 0, new SystemRandomBytes(), new RuntimeOptions());
        Assert.False(host.OnKey(0x20, true, true, true, false, false));
        Assert.Null(host.Machine.ActiveDeviceId);
    }

    [Fact]
    public void Top_edge_glow_is_the_exit_segment_and_a_corner_is_its_square()
    {
        var top = new DevicePlacement
        {
            Id = "t",
            Name = "Tab",
            PcEdge = ScreenEdge.Top,
            ReturnEdge = ScreenEdge.Bottom,
            Anchor = AnchorKind.Edge,
            Corner = ScreenCorner.TopLeft,
            OffsetPx = 100,
            SpanPx = 400,
            ScreenWidth = 1600,
            ScreenHeight = 1000
        };
        var band = GlowBand.For(top, 0, 0, 1920, 1080, 22);
        Assert.Equal(new GlowBand(100, 0, 400, 22, GlowEdge.Top), band);
        var corner = new DevicePlacement
        {
            Id = "c",
            Name = "Tab",
            PcEdge = ScreenEdge.Right,
            ReturnEdge = ScreenEdge.Left,
            Anchor = AnchorKind.Corner,
            Corner = ScreenCorner.TopRight,
            CornerSizePx = 120,
            ScreenWidth = 1600,
            ScreenHeight = 1000
        };
        var square = GlowBand.For(corner, -1920, 0, 1920, 1080, 22);
        Assert.Equal(new GlowBand(-120, 0, 120, 120, GlowEdge.TopRight), square);
        Assert.Equal((0x3D, 0xDC, 0x84), GlowBand.ParseColor("#3DDC84"));
    }

    [Fact]
    public void Switch_chord_releases_modifiers_and_does_not_forward_space()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, ListenPort = 45455 };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("tablet1", "Pixel", 1600, 1000, clock.Ms, new SystemRandomBytes(), options);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 45455);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        Pump(host, client, remote, clock);
        host.OnPhysicalMouse(200, 240, 0, 0);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Pump(host, client, remote, clock);
        Assert.True(client.HasControl);
        Assert.True(host.OnKey(0x11, true, true, false, false, false));
        Assert.True(host.OnKey(0x12, true, true, true, false, false));
        client.Keys.Clear();
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Pump(host, client, remote, clock);
        Assert.False(client.HasControl);
        Assert.Equal(200, host.Machine.X);
        Assert.Equal(240, host.Machine.Y);
        Assert.Contains(client.Keys, k => k.StartsWith("up:17:", StringComparison.Ordinal));
        Assert.Contains(client.Keys, k => k.StartsWith("up:18:", StringComparison.Ordinal));
        Assert.DoesNotContain(client.Keys, k => k.Contains(":32:"));
        Assert.True(host.OnKey(0x20, false, false, false, false, false));
        Assert.False(host.OnKey(0xA2, false, false, false, false, false));
        Assert.Contains(host.DrainEffects().OfType<Effect.ModifiersReleased>(), _ => true);
    }

    [Fact]
    public void Alt_held_on_the_tablet_is_released_when_control_returns()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, ListenPort = 45455 };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("tablet1", "Pixel", 1600, 1000, clock.Ms, new SystemRandomBytes(), options);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 45455);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        Pump(host, client, remote, clock);
        host.OnPhysicalMouse(200, 240, 0, 0);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Pump(host, client, remote, clock);
        client.Keys.Clear();
        Assert.True(host.OnKey(0xA4, true, false, true, false, false));
        Pump(host, client, remote, clock);
        Assert.Contains(client.Keys, k => k.StartsWith("down:164:", StringComparison.Ordinal));
        client.Keys.Clear();
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Pump(host, client, remote, clock);
        Assert.False(client.HasControl);
        Assert.Contains(client.Keys, k => k.StartsWith("up:164:", StringComparison.Ordinal));
        Assert.DoesNotContain(client.Keys, k => k.Contains(":32:", StringComparison.Ordinal));
        client.Keys.Clear();
        Assert.True(host.OnKey(0xA4, false, false, false, false, false));
        Pump(host, client, remote, clock);
        Assert.Empty(client.Keys);
    }

    [Fact]
    public void Button_flags_are_read_from_the_raw_mouse_offset()
    {
        var raw = new byte[24];
        raw[2] = 0x01;
        raw[4] = 0x04;
        raw[6] = 0x78;
        raw[12] = 10;
        Assert.True(RawMouseLayout.TryRead(raw, out var dx, out var dy, out var flags, out var data));
        Assert.Equal(10, dx);
        Assert.Equal(0, dy);
        Assert.Equal(0x0004, flags);
        Assert.Equal(0x78, data);
        raw[0] = 0x01;
        Assert.True(RawMouseLayout.TryRead(raw, out dx, out dy, out flags, out _));
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
        Assert.Equal(0x0004, flags);
    }

    [Fact]
    public void Buttons_and_wheel_reach_the_device_only_while_it_has_control()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, ListenPort = 45455 };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("tablet1", "Pixel", 1600, 1000, clock.Ms, new SystemRandomBytes(), options);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 45455);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        Pump(host, client, remote, clock);
        host.OnMouseButton(0, true);
        host.OnScroll(0, -1);
        Assert.Equal(0, host.ButtonsSent);
        Assert.Equal(0, host.WheelsSent);
        host.OnPhysicalMouse(200, 240, 0, 0);
        Assert.True(host.OnKey(0x20, true, true, true, false, false));
        Pump(host, client, remote, clock);
        Assert.True(client.HasControl);
        host.OnMouseButton(0, true);
        host.OnMouseButton(0, false);
        host.OnMouseButton(1, true);
        host.OnScroll(1, -1);
        Pump(host, client, remote, clock);
        Assert.Equal(3, host.ButtonsSent);
        Assert.Equal(1, host.WheelsSent);
        Assert.Contains("button:0:1", client.Pointer);
        Assert.Contains("button:0:0", client.Pointer);
        Assert.Contains("button:1:1", client.Pointer);
        Assert.Contains("wheel:1:-1", client.Pointer);
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
}
