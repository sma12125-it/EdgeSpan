using System.Net;
using EdgeSpan.Crypto;
using EdgeSpan.Layout;
using EdgeSpan.Product;
using EdgeSpan.Protocol;
using EdgeSpan.Session;

namespace EdgeSpan.Tests;

public class HandoffTests
{
    [Fact]
    public void A_drag_starts_only_past_the_edge_or_after_the_pointer_crosses()
    {
        Assert.False(HandoffPolicy.ShouldStart(false, false, true, 200));
        Assert.False(HandoffPolicy.ShouldStart(true, true, true, 200));
        Assert.False(HandoffPolicy.ShouldStart(true, false, false, 79));
        Assert.True(HandoffPolicy.ShouldStart(true, false, false, 80));
        Assert.True(HandoffPolicy.ShouldStart(true, false, true, 0));
        Assert.Equal(80, HandoffPolicy.PastEdge(ScreenEdge.Right, 1840, 10, 160, 90, 0, 0, 1920, 1080));
        Assert.Equal(40, HandoffPolicy.PastEdge(ScreenEdge.Left, -40, 10, 200, 100, 0, 0, 1920, 1080));
        Assert.Equal(ScreenEdge.Right, HandoffPolicy.FarthestEdge(1840, 10, 160, 90, 0, 0, 1920, 1080));
        var placed = HandoffPolicy.MapToDevice(ScreenEdge.Right, 1840, 40, 160, 90, 0, 0, 1920, 1080, 1600, 1000, 0);
        Assert.Equal((-80, 40), placed);
        Assert.Equal(10_000_000, HandoffPolicy.Bitrate(1920, 1080, "sharp"));
        Assert.Equal(30, HandoffPolicy.FrameRate("saver"));
        Assert.Equal((1280, 720), HandoffPolicy.EncodeSize(1920, 1080, "smooth"));
        var bytes = HandoffPayloads.WriteOffer(new HandoffPayloads.Offer(1, HandoffPayloads.PcToPhone, 96, 2, 2, 2, 2, HandoffPayloads.CodecH264, (byte)ScreenEdge.Right, "A"));
        Assert.Equal("010000000160000000020002000200020001010141", Convert.ToHexString(bytes));
        Assert.False(HandoffPolicy.AllowsPcToPhone("off"));
        Assert.True(HandoffPolicy.AllowsPcToPhone("pc"));
        Assert.True(HandoffPolicy.AllowsPcToPhone("both"));
    }

    [Fact]
    public void A_window_drag_at_the_edge_starts_handoff_instead_of_crossing()
    {
        Assert.True(HandoffPolicy.IsWindowDrag(true, false, false));
        Assert.True(HandoffPolicy.IsWindowDrag(false, true, true));
        Assert.False(HandoffPolicy.IsWindowDrag(false, true, false));
        Assert.False(HandoffPolicy.IsWindowDrag(false, false, true));
        Assert.Equal(HandoffPolicy.DragAtEdge.Start, HandoffPolicy.Decide(true, false, true, true));
        Assert.Equal(HandoffPolicy.DragAtEdge.TabletNotReady, HandoffPolicy.Decide(true, false, true, false));
        Assert.Equal(HandoffPolicy.DragAtEdge.None, HandoffPolicy.Decide(false, false, true, true));
        Assert.Equal(HandoffPolicy.DragAtEdge.None, HandoffPolicy.Decide(true, true, true, true));
        Assert.True(HandoffPolicy.DragReaches(ScreenEdge.Right, 1919, 400, 8, 0, 0, 0, 1920, 1080, -20));
        Assert.False(HandoffPolicy.DragReaches(ScreenEdge.Right, 1800, 400, 8, 0, 0, 0, 1920, 1080, -20));
        Assert.True(HandoffPolicy.OnEdgePixel(ScreenEdge.Right, 1919, 400, 0, 0, 1920, 1080, 0, 1000));
        Assert.True(HandoffPolicy.OnEdgePixel(ScreenEdge.Right, 1918, 400, 0, 0, 1920, 1080, 0, 1000));
        Assert.False(HandoffPolicy.OnEdgePixel(ScreenEdge.Right, 1917, 400, 0, 0, 1920, 1080, 0, 1000));
        Assert.Equal(6, HandoffPolicy.NextPush(0, ScreenEdge.Right, true, 6, 0));
        Assert.Equal(14, HandoffPolicy.NextPush(6, ScreenEdge.Right, true, 8, 0));
        Assert.Equal(0, HandoffPolicy.NextPush(14, ScreenEdge.Right, true, -3, 0));
        Assert.Equal(0, HandoffPolicy.NextPush(14, ScreenEdge.Right, false, 8, 0));
        Assert.False(HandoffPolicy.PushReady(11));
        Assert.True(HandoffPolicy.PushReady(12));

        var (host, client, setupId) = Paired("pc", 0x0F);
        using (host)
        using (client)
        {
            var before = client.HandoffOffers.ToArray();
            host.WindowDrag = true;
            host.OnPhysicalMouse(1919, 400, 6, 0);
            Assert.Null(host.Machine.ActiveDeviceId);
            Assert.Equal(before, client.HandoffOffers);
            host.OnPhysicalMouse(1919, 400, 8, 0);
            Assert.Equal("tab", host.Machine.ActiveDeviceId);
            Assert.Contains(host.DrainEffects(), effect => effect is Effect.WindowDragCommit commit && commit.DeviceId == "tab" && commit.Id != setupId);
            Pump(host, client);
            Assert.True(client.HasControl);
            Assert.Contains(client.HandoffOffers, id => id != setupId);
        }

        var (blocked, other, _) = Paired("pc", 0x07);
        using (blocked)
        using (other)
        {
            var offers = other.HandoffOffers.Count;
            blocked.WindowDrag = true;
            blocked.OnPhysicalMouse(1919, 400, 6, 0);
            blocked.OnPhysicalMouse(1919, 400, 8, 0);
            Assert.Null(blocked.Machine.ActiveDeviceId);
            Assert.Contains(blocked.DrainEffects(), effect => effect is Effect.WindowDragFailed failed && failed.Reason == "handoffNeedTablet");
            Assert.Equal(offers, other.HandoffOffers.Count);
            blocked.OnPhysicalMouse(1919, 400, 20, 0);
            Assert.Equal("tab", blocked.Machine.ActiveDeviceId);
        }
    }

    [Fact]
    public void A_failed_handoff_keeps_the_window_home_and_the_tablet_capability()
    {
        Assert.False(HandoffPolicy.AcceptsTrigger(true));
        Assert.True(HandoffPolicy.AcceptsTrigger(false));
        Assert.Equal((byte)0x0F, HandoffPolicy.KeepAdvertisedCaps(0x0F, 0x07, ProductInfo.HandoffCapability));
        Assert.Equal((byte)0x07, HandoffPolicy.KeepAdvertisedCaps(0x07, 0x07, ProductInfo.HandoffCapability));
        var parked = new HandoffPolicy.WindowHome(-703, 216, -80, 843);
        var original = new HandoffPolicy.WindowHome(40, 216, 663, 843);
        Assert.False(HandoffPolicy.OverlapsScreen(parked.Left, parked.Top, parked.Right, parked.Bottom, 0, 0, 3520, 1080));
        Assert.Equal(original, HandoffPolicy.ChooseHome(parked, 0, 0, 3520, 1080, original));
        Assert.Equal(original, HandoffPolicy.ChooseHome(original, 0, 0, 3520, 1080, parked));

        var (host, client, _) = Paired("pc", 0x0F);
        using (host)
        using (client)
        {
            client.Advertise(0x07);
            Pump(host, client);
            Assert.Contains(host.DrainEffects(), effect => effect is Effect.Log log && log.Message.Contains("handoff caps kept"));
            var starts = 0;
            host.DragHandoff = (_, _) => starts++;
            host.WindowDrag = true;
            host.OnPhysicalMouse(1919, 400, 6, 0);
            host.OnPhysicalMouse(1919, 400, 8, 0);
            host.OnPhysicalMouse(1919, 400, 9, 0);
            host.OnPhysicalMouse(1919, 400, 10, 0);
            Assert.Equal(1, starts);
            Assert.DoesNotContain(host.DrainEffects(), effect => effect is Effect.WindowDragFailed failed && failed.Reason == "handoffNeedTablet");
            host.WindowDrag = false;
            host.OnPhysicalMouse(1900, 400, 0, 0);
            host.WindowDrag = true;
            host.OnPhysicalMouse(1919, 400, 6, 0);
            host.OnPhysicalMouse(1919, 400, 8, 0);
            Assert.Equal(2, starts);
            Assert.Null(host.Machine.ActiveDeviceId);
            Assert.DoesNotContain(host.DrainEffects(), effect => effect is Effect.WindowDragFailed failed && failed.Reason == "handoffNeedTablet");
        }
    }

    [Fact]
    public void Offer_and_input_round_trip()
    {
        var offer = new HandoffPayloads.Offer(7, HandoffPayloads.PcToPhone, 144, 800, 600, 533, 400, HandoffPayloads.CodecH264, (byte)ScreenEdge.Right, "Notes");
        Assert.True(HandoffPayloads.TryOffer(HandoffPayloads.WriteOffer(offer), out var back));
        Assert.Equal(offer, back);
        var input = new HandoffPayloads.Input(7, HandoffPayloads.InputText, 3, 4, 0, false, 0, 0, 0, "سلام");
        Assert.True(HandoffPayloads.TryInput(HandoffPayloads.WriteInput(input), out var keys));
        Assert.Equal(input, keys);
        var config = new HandoffPayloads.VideoConfig(7, 10_000_000, 60, true);
        Assert.True(HandoffPayloads.TryVideoConfig(HandoffPayloads.WriteVideoConfig(config), out var cfg));
        Assert.Equal(config, cfg);
    }

    [Fact]
    public void A_media_frame_uses_its_own_sequence_and_rejects_a_session_replay()
    {
        var shared = new byte[32];
        shared[0] = 9;
        var salt = new byte[16];
        salt[1] = 4;
        SessionCrypto.Derive(shared, salt, true, out var send, out var recv, out var prefix);
        var media = SessionCrypto.Hkdf(send, prefix, SessionCrypto.MediaInfo, 32);
        var annex = new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3 };
        var wire = MediaFrames.Protect(media, 3, 7, 11, 123456, 1, annex);
        Assert.True(MediaFrames.Open(media, 3, wire, out var id, out var frame, out var when, out var flags, out var body));
        Assert.Equal(7u, id);
        Assert.Equal(11u, frame);
        Assert.Equal(123456ul, when);
        Assert.Equal(1, flags);
        Assert.Equal(annex, body);
        Assert.False(MediaFrames.Open(recv, 3, wire, out _, out _, out _, out _, out _));
        Assert.False(MediaFrames.Open(media, 4, wire, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void An_offer_waits_for_the_setting_and_the_peer_and_an_accept_is_an_effect()
    {
        Assert.Equal(0u, Offer("off", 0x0F));
        Assert.Equal(0u, Offer("pc", 0x07));
        var (host, client, id) = Paired("pc", 0x0F);
        using (host)
        using (client)
        {
            Assert.NotEqual(0u, id);
            Assert.Contains(id, client.HandoffOffers);
            client.SendHandoff(PacketType.HandoffAccept, HandoffPayloads.WriteAccept(new HandoffPayloads.Accept(id, HandoffPayloads.CodecH264, 800, 600)));
            Pump(host, client);
            Assert.Contains(host.DrainEffects(), effect => effect is Effect.HandoffAccepted accepted && accepted.Id == id && accepted.ViewW == 800);
        }
    }

    private static uint Offer(string mode, byte caps)
    {
        var (host, client, id) = Paired(mode, caps);
        using (host)
        using (client)
            return id;
    }

    private static (HostController Host, ClientNode Client, uint Id) Paired(string mode, byte caps)
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { CoalesceMs = 0, HeartbeatMs = 0, ReliableRetryMs = 500, PairingWindowMs = 60_000 };
        var settings = new AppSettings { DisplayName = "Office", ScreenWidth = 1920, ScreenHeight = 1080, Handoff = mode };
        var host = new HostController(settings, clock.Ms, new SystemRandomBytes(), options);
        var client = new ClientNode("tab", "Tab", 1600, 1000, clock.Ms, new SystemRandomBytes(), options, caps);
        host.OpenPairingWindow();
        client.StartPairing(host.CurrentPin);
        Pump(host, client, clock);
        var id = host.OfferHandoff("tab", "Notes", 144, 800, 600, 533, 400, ScreenEdge.Right);
        Pump(host, client, clock);
        return (host, client, id);
    }

    private static void Pump(HostController host, ClientNode client, MutableClock? clock = null)
    {
        clock ??= new MutableClock();
        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.8"), 45455);
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
            clock.Now += 20;
        }
    }
}
