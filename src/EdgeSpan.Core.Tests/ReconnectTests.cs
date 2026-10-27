using System.Net;
using EdgeSpan.Crypto;
using EdgeSpan.Product;
using EdgeSpan.Protocol;
using EdgeSpan.Session;

namespace EdgeSpan.Tests;

/// <summary>connect → disconnect → reconnect, many times, with no app restart on either side.</summary>
public sealed class ReconnectTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Parse("10.0.0.8"), 45455);

    private static (HostController Host, MutableClock Clock, RuntimeOptions Options) NewHost(int heartbeat)
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = heartbeat, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080 };
        return (new HostController(settings, clock.Ms, new SystemRandomBytes(), options), clock, options);
    }

    private static void Pump(HostController host, ClientNode client, MutableClock clock, int rounds = 120)
    {
        for (var i = 0; i < rounds; i++)
        {
            host.Tick(clock.Now);
            client.Tick(clock.Now);
            foreach (var packet in host.DrainOutbound())
                client.Ingest(packet.Bytes, clock.Now);
            var inbound = client.Out.ToArray();
            client.Out.Clear();
            foreach (var packet in inbound)
                host.Ingest(Remote, packet, clock.Now);
            clock.Now += 20;
        }
    }

    private static ClientNode Connect(HostController host, MutableClock clock, RuntimeOptions options, string pin)
    {
        // A brand-new client object = the Android app rebuilding its connection state from scratch.
        var client = new ClientNode("tab", "Tab", 1600, 1000, clock.Ms, new SystemRandomBytes(), options, 0);
        client.StartPairing(pin);
        Pump(host, client, clock);
        return client;
    }

    [Fact]
    public void Heartbeat_loss_then_rejoin_works_ten_times_with_the_same_code()
    {
        var (host, clock, options) = NewHost(1000);
        using var _ = host;
        host.OpenPairingWindow();
        var pin = host.CurrentPin;
        for (var round = 0; round < 10; round++)
        {
            using var client = Connect(host, clock, options, pin);
            Assert.True(host.LinkLive("tab"), $"round {round}: not connected");
            // Cable pulled / Wi-Fi gone: the host hears nothing for a while.
            for (var t = 0; t < 12; t++)
            {
                clock.Now += 1000;
                host.Tick(clock.Now);
                host.DrainOutbound();
            }
            Assert.False(host.LinkLive("tab"), $"round {round}: still marked live");
            Assert.True(host.PairingOpen, $"round {round}: pairing closed, phone could not rejoin");
            Assert.Equal(pin, host.CurrentPin);
        }
    }

    [Fact]
    public void Phone_re_handshakes_while_pc_still_thinks_it_is_connected()
    {
        var (host, clock, options) = NewHost(0);
        using var _ = host;
        host.OpenPairingWindow();
        var pin = host.CurrentPin;
        for (var round = 0; round < 6; round++)
        {
            using var client = Connect(host, clock, options, pin);
            Assert.True(client.Established, $"round {round}: phone handshake did not finish " + string.Join(" | ", host.Logs.TakeLast(6)));
            Assert.True(host.LinkLive("tab"), $"round {round}: not connected");
        }
    }

    [Fact]
    public void Manual_reconnect_drops_and_rebuilds_the_session()
    {
        var (host, clock, options) = NewHost(0);
        using var _ = host;
        host.OpenPairingWindow();
        var pin = host.CurrentPin;
        for (var round = 0; round < 5; round++)
        {
            host.Reconnect();
            Assert.False(host.LinkLive("tab"));
            Assert.True(host.PairingOpen);
            using var client = Connect(host, clock, options, pin);
            Assert.True(host.LinkLive("tab"), $"round {round}: not connected after Reconnect()");
        }
    }
}
