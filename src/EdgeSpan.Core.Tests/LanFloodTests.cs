using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EdgeSpan.Crypto;
using EdgeSpan.Net;
using EdgeSpan.Product;
using EdgeSpan.Protocol;
using EdgeSpan.Session;

namespace EdgeSpan.Tests;

public class LanFloodTests
{
    [Fact]
    public void Moves_collapse_and_a_burst_stays_cheap()
    {
        var mail = new InputMailbox();
        var started = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
            mail.PostAbsolute(i, 10);
        started.Stop();
        Assert.True(started.ElapsedMilliseconds < 500, "posting moves took " + started.ElapsedMilliseconds + "ms");
        Assert.True(mail.Depth <= 2);
        var batch = mail.Take(32);
        Assert.True(batch.HasMove);
        Assert.Equal(99_999, batch.X);
        Assert.Equal(99_999, batch.Dx);
        Assert.Empty(batch.Notes);
        Assert.Equal(0, mail.Depth);
    }

    [Fact]
    public void Button_notes_stop_at_the_cap()
    {
        var mail = new InputMailbox();
        for (var i = 0; i < 1_000; i++)
            mail.PostButton(0x0201, 0);
        Assert.Equal(InputMailbox.NoteCap, mail.Depth);
        Assert.True(mail.Dropped > 0);
        var batch = mail.Take(InputMailbox.NoteCap);
        Assert.Equal(InputMailbox.NoteCap, batch.Notes.Length);
    }

    [Fact]
    public void Watchdog_unhooks_a_stall_and_waits_until_the_hook_returns()
    {
        Assert.False(HookWatchdog.ShouldUnhook(0, 1_000, 2_000));
        Assert.False(HookWatchdog.ShouldUnhook(1, 1_000, 1_079));
        Assert.True(HookWatchdog.ShouldUnhook(1, 1_000, 1_080));
        Assert.False(HookWatchdog.ShouldRehook(false, 0));
        Assert.False(HookWatchdog.ShouldRehook(true, 1));
        Assert.True(HookWatchdog.ShouldRehook(true, 0));
    }

    [Fact]
    public void A_slow_socket_does_not_block_the_sender()
    {
        var gate = new ManualResetEventSlim(false);
        using var queue = new SendQueue((_, _) => gate.Wait(2_000));
        var packet = new byte[32];
        packet[0] = PacketCodec.Magic;
        packet[1] = PacketCodec.Version;
        packet[2] = (byte)PacketType.MouseMove;
        var started = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
            queue.Enqueue(new IPEndPoint(IPAddress.Loopback, 9), packet);
        started.Stop();
        Assert.True(started.ElapsedMilliseconds < 500, "enqueue blocked for " + started.ElapsedMilliseconds + "ms");
        Assert.True(queue.Depth <= SendQueue.Cap);
        Assert.True(queue.Dropped > 0);
        gate.Set();
    }

    [Fact]
    public void A_lan_peer_blast_is_capped_at_the_inbox()
    {
        using var transport = new UdpTransport(0);
        var ingress = new IngressQueue();
        transport.Received += (from, data) => ingress.TryAdd(from, data, null);
        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, transport.Port);
        var packet = new byte[48];
        packet[0] = PacketCodec.Magic;
        packet[1] = PacketCodec.Version;
        packet[2] = (byte)PacketType.MouseMove;
        var started = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
            sender.Send(packet, packet.Length, target);
        started.Stop();
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && ingress.Dropped == 0 && ingress.Count < 20_000)
            Thread.Sleep(10);
        Assert.True(started.ElapsedMilliseconds < 2_000, "blast took " + started.ElapsedMilliseconds + "ms");
        for (var i = 0; i < 5_000; i++)
            ingress.TryAdd(target, packet, null);
        Assert.InRange(ingress.Count, 1, IngressQueue.Cap);
        Assert.True(ingress.Dropped > 0);
    }

    [Fact]
    public async Task Clipboard_and_mouse_flood_stays_bounded_while_input_is_posted()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, ListenPort = 45455 };
        using var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        using var client = new ClientNode("tablet1", "Pixel", 1600, 1000, clock.Ms, new SystemRandomBytes(), options);
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.44"), 45455);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        Pump(host, client, remote, clock);
        Assert.True(client.Established);
        client.Out.Clear();
        host.DrainOutbound();

        Assert.Equal(4_000, client.QueueMouseBurst(4_000));
        Assert.Equal(400, client.QueueClipboardBurst("same text", 400));
        var packets = client.Out.ToArray();
        client.Out.Clear();
        Assert.True(packets.Length > 1_000);

        var mail = new InputMailbox();
        long maxUs = 0;
        var poster = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                var stamp = Stopwatch.GetTimestamp();
                mail.PostAbsolute(i, 4);
                var us = (Stopwatch.GetTimestamp() - stamp) * 1_000_000 / Stopwatch.Frequency;
                long seen;
                do
                {
                    seen = Volatile.Read(ref maxUs);
                    if (us <= seen)
                        break;
                }
                while (Interlocked.CompareExchange(ref maxUs, us, seen) != seen);
            }
        });
        var ingest = Task.Run(() =>
        {
            foreach (var packet in packets)
                host.Ingest(remote, packet, clock.Now);
        });
        await Task.WhenAll(poster, ingest).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(maxUs < 5_000, "input post blocked for " + maxUs + "us");
        Assert.True(mail.Depth <= 2);

        var outbound = host.DrainOutbound();
        Assert.True(outbound.Count <= WirePressure.OutboundCap, "outbound " + outbound.Count);
        var clips = host.DrainEffects().OfType<Effect.ClipboardArrived>().ToList();
        Assert.Single(clips);
        Assert.Equal("same text", clips[0].Text);
    }

    private static void Pump(HostController host, ClientNode client, IPEndPoint remote, MutableClock clock)
    {
        for (var i = 0; i < 24; i++)
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
