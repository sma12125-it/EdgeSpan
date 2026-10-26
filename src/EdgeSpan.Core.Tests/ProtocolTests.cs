using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EdgeSpan.Crypto;
using EdgeSpan.Input;
using EdgeSpan.Layout;
using EdgeSpan.Net;
using EdgeSpan.Product;
using EdgeSpan.Protocol;
using EdgeSpan.Session;

namespace EdgeSpan.Tests;

public sealed class MutableClock
{
    public long Now { get; set; } = 10_000;
    public long Ms() => Now;
}

public class ProtocolTests
{
    [Fact]
    public void Hello_header_is_little_endian()
    {
        var payload = Payloads.Hello(1, 0x1, 1920, 1080, 45455, "pc", "PC");
        var packet = PacketCodec.EncodeClear(PacketType.Hello, 0, 0, 1, 0, 0, payload);
        Assert.Equal(0xE5, packet[0]);
        Assert.Equal(1, packet[1]);
        Assert.Equal((byte)PacketType.Hello, packet[2]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
        Assert.Equal(payload.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(24)));
        Assert.True(PacketCodec.TryDecodeClear(packet, out var header, out var body));
        Assert.True(Payloads.TryHello(body, out var hello));
        Assert.Equal("PC", hello.Name);
        Assert.Equal(1920, hello.Width);
        Assert.Equal(45455, hello.Port);
        Assert.False(header.Encrypted);
    }

    [Fact]
    public void Encrypted_mouse_roundtrip_rejects_tampering_and_replays()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { HeartbeatMs = 0, CoalesceMs = 0 };
        var host = new WireSession(true, clock.Ms, options);
        var client = new WireSession(false, clock.Ms, options);
        var h2c = new byte[32];
        h2c[0] = 1;
        var c2h = new byte[32];
        c2h[0] = 2;
        host.Establish(h2c, c2h, new byte[] { 9, 8, 7, 6 }, 42);
        client.Establish(c2h, h2c, new byte[] { 9, 8, 7, 6 }, 42);

        var packet = host.SendUnreliable(PacketType.MouseMove, Payloads.MouseMove(30, 40));
        var opened = client.Receive(packet, clock.Now);
        var move = Assert.IsType<SessionEvent.MouseMoved>(Assert.Single(opened.Events));
        Assert.Equal(30, move.X);
        Assert.Equal(40, move.Y);
        Assert.Empty(client.Receive(packet, clock.Now).Events);

        var tampered = packet.ToArray();
        tampered[^1] ^= 0x5A;
        Assert.Empty(client.Receive(tampered, clock.Now).Events);

        var stale = host.SendUnreliable(PacketType.MouseMove, Payloads.MouseMove(1, 1));
        var newer = host.SendUnreliable(PacketType.MouseMove, Payloads.MouseMove(8, 9));
        Assert.Equal(8, Assert.IsType<SessionEvent.MouseMoved>(Assert.Single(client.Receive(newer, clock.Now).Events)).X);
        Assert.Empty(client.Receive(stale, clock.Now).Events);
    }

    [Fact]
    public void Reliable_key_is_retried_until_acked()
    {
        var clock = new MutableClock();
        var options = new RuntimeOptions { HeartbeatMs = 0, ReliableRetryMs = 40, ReliableMaxAttempts = 5 };
        var host = new WireSession(true, clock.Ms, options);
        var client = new WireSession(false, clock.Ms, options);
        var h2c = Enumerable.Repeat((byte)3, 32).ToArray();
        var c2h = Enumerable.Repeat((byte)4, 32).ToArray();
        host.Establish(h2c, c2h, new byte[] { 1, 2, 3, 4 }, 7);
        client.Establish(c2h, h2c, new byte[] { 1, 2, 3, 4 }, 7);
        var sent = host.SendReliable(PacketType.KeyEvent, Payloads.Key(0x41, 0x04, 0, true, 'a'));
        Assert.Single(sent);
        clock.Now += 10;
        Assert.Empty(host.Poll(clock.Now));
        clock.Now += 40;
        Assert.Equal(sent[0], Assert.Single(host.Poll(clock.Now)));
        var received = client.Receive(sent[0], clock.Now);
        var key = Assert.IsType<SessionEvent.KeyChanged>(Assert.Single(received.Events));
        Assert.Equal('a', key.Codepoint);
        Assert.True(key.Down);
        host.Receive(Assert.Single(received.Replies), clock.Now);
        clock.Now += 40;
        Assert.Empty(host.Poll(clock.Now));
    }

    [Fact]
    public void Clipboard_fragments_survive_reordering()
    {
        var assembler = new FragmentAssembler();
        var text = Encoding.UTF8.GetBytes(new string('ی', 80) + new string('A', 80));
        var wrapped = new List<byte[]>();
        const int chunk = 50;
        var count = (text.Length + chunk - 1) / chunk;
        for (var i = 0; i < count; i++)
        {
            var n = Math.Min(chunk, text.Length - (i * chunk));
            wrapped.Add(Payloads.Fragment(9, (ushort)i, (ushort)count, text.AsSpan(i * chunk, n)));
        }
        byte[]? full = null;
        var order = Enumerable.Range(0, wrapped.Count).OrderByDescending(i => i).ToArray();
        foreach (var index in order)
        {
            Assert.True(Payloads.TryFragment(wrapped[index], out var id, out var part, out var parts, out var body));
            full = assembler.Push((byte)PacketType.Clipboard, id, part, parts, body, 1000);
        }
        Assert.NotNull(full);
        Assert.Equal(text, full);
    }

    [Fact]
    public void Shared_secret_matches_and_wrong_pin_is_rejected()
    {
        var random = new SystemRandomBytes();
        var host = PairingHandshake.Create(PairRole.Host, "482913", random, 99);
        var client = PairingHandshake.Create(PairRole.Client, "482913", random);
        var init = client.BuildInit("tablet", "Pixel");
        var challenge = Assert.Single(host.Consume(init.Type, init.Payload));
        var confirm = Assert.Single(client.Consume(challenge.Type, challenge.Payload));
        var accept = Assert.Single(host.Consume(confirm.Type, confirm.Payload));
        Assert.Empty(client.Consume(accept.Type, accept.Payload));
        Assert.True(host.Established);
        Assert.True(client.Established);
        Assert.Equal(host.SendKey, client.ReceiveKey);
        Assert.Equal(host.ReceiveKey, client.SendKey);
        Assert.Equal(host.NoncePrefix, client.NoncePrefix);
        Assert.Equal(host.SessionId, client.SessionId);

        var bad = PairingHandshake.Create(PairRole.Client, "000000", random);
        var badInit = bad.BuildInit("tablet", "Pixel");
        var badHost = PairingHandshake.Create(PairRole.Host, "482913", random, 3);
        var badChallenge = Assert.Single(badHost.Consume(badInit.Type, badInit.Payload));
        var badConfirm = Assert.Single(bad.Consume(badChallenge.Type, badChallenge.Payload));
        var rejected = Assert.Single(badHost.Consume(badConfirm.Type, badConfirm.Payload));
        Assert.Equal(PacketType.PairReject, rejected.Type);
        Assert.True(badHost.Failed);
        host.Dispose();
        client.Dispose();
        bad.Dispose();
        badHost.Dispose();
    }

    [Fact]
    public void Hkdf_matches_rfc5869_case_1()
    {
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Enumerable.Range(0, 13).Select(i => (byte)i).ToArray();
        var info = Enumerable.Range(0, 10).Select(i => (byte)(0xf0 + i)).ToArray();
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42, salt, info);
        Assert.Equal("3CB25F25FAACD57A90434F64D0362F2A2D2D0A90CF1A5A4C5DB02D56ECC4C5BF34007208D5B887185865", Convert.ToHexString(okm));
    }

    [Fact]
    public void Discovery_stun_relay_and_hotkey_codecs()
    {
        var announce = new DiscoveryAnnounce("abc", "Office", 1, 0x1, 45455, 1920, 1080);
        Assert.True(DiscoveryCodec.TryDecode(DiscoveryCodec.Encode(announce), out var decoded));
        Assert.Equal(announce, decoded);

        var tx = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var response = new byte[]
        {
            0x01, 0x01, 0x00, 0x0C, 0x21, 0x12, 0xA4, 0x42,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C,
            0x00, 0x20, 0x00, 0x08, 0x00, 0x01, 0x90, 0x9D, 0xEA, 0x12, 0xD5, 0x4A
        };
        Assert.True(StunCodec.TryReadMapped(response, tx, out var mapped));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.8"), 45455), mapped);
        Assert.Equal(20, StunCodec.BindingRequest(tx).Length);

        var token = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var bind = RelayFrame.Bind(token, "ab");
        Assert.Equal("E5520101" + new string('1', 32) + "026162", Convert.ToHexString(bind));
        Assert.True(RelayFrame.TryParse(bind, out var op, out var parsedToken, out var id, out var payload));
        Assert.Equal(RelayOp.Bind, op);
        Assert.Equal(token, parsedToken);
        Assert.Equal("ab", id);
        Assert.Empty(payload);

        var hotkey = HotkeyParser.Parse("Ctrl+Alt+S");
        Assert.True(hotkey.Matches(0x53, true, true, false, false));
        Assert.False(hotkey.Matches(0x53, true, false, false, false));
        Assert.Equal(0x04, KeyMap.All.Single(k => k.Vk == 0x41).Hid);
        Assert.Equal(29, KeyMap.All.Single(k => k.Vk == 0x41).Android);

        Assert.Equal(IPAddress.Parse("192.168.1.255"), NetAddresses.Broadcast(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")));
        var mdns = MdnsPacket.Announce("Office PC", 45455, IPAddress.Parse("10.0.0.2"), "Office");
        Assert.Contains("_edgestpan", Encoding.ASCII.GetString(mdns));

        var invite = PairingLink.Build("10.0.0.2", 45455, "482913", "اتاق");
        Assert.True(PairingLink.TryParse(invite, out var parsed));
        Assert.Equal("اتاق", parsed.Name);
        Assert.Equal(45455, parsed.Port);
    }

    [Fact]
    public void Punch_falls_back_to_relay_and_accepts_a_direct_hit()
    {
        var punch = new CandidateExchange
        {
            StartedMs = 0,
            PeerMapped = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 4000),
            PeerLan = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 45455)
        };
        Assert.Equal(LinkChoice.Pending, punch.Evaluate(500, true, 1200));
        punch.NoteInbound(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 4000));
        Assert.Equal(LinkChoice.Direct, punch.Evaluate(600, true, 1200));

        var failed = new CandidateExchange { StartedMs = 0, PeerMapped = new IPEndPoint(IPAddress.Parse("198.51.100.2"), 9) };
        Assert.Equal(LinkChoice.Relay, failed.Evaluate(1200, true, 1200));
        Assert.Equal(LinkChoice.Offline, failed.Evaluate(1200, false, 1200));
    }

    [Fact]
    public void License_trial_and_signed_dev_key()
    {
        var validator = new DevLicenseValidator();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var trial = validator.Evaluate(null, first, first.AddDays(3));
        Assert.True(trial.IsValid);
        Assert.Equal(LicenseEdition.Trial, trial.Edition);
        var expired = validator.Evaluate(null, first, first.AddDays(15));
        Assert.False(expired.IsValid);
        var key = DevLicenseValidator.SignDevKey("PR", "K7M2Q9XW");
        var pro = validator.Evaluate(key.ToLowerInvariant(), first, first.AddDays(40));
        Assert.True(pro.IsValid);
        Assert.Equal(LicenseEdition.Pro, pro.Edition);
        Assert.False(validator.Evaluate(key.Replace("K7M2", "M7M2"), first, first).IsValid);
    }

    [Fact]
    public void Mouse_move_vector_is_stable()
    {
        var packet = PacketCodec.EncodeClear(PacketType.MouseMove, 0, 0, 1, 0, 0, Payloads.MouseMove(10, -3));
        Assert.Equal("E5012000000000000100000000000000000000000000000008000A000000FDFFFFFF", Convert.ToHexString(packet));
    }

    [Fact]
    public void Catalog_covers_english_and_persian()
    {
        Assert.NotEmpty(Catalog.Keys);
        foreach (var key in Catalog.Keys)
            Assert.True(Catalog.Complete(key), key);
        Assert.True(Catalog.IsRtl("fa"));
        Assert.False(Catalog.IsRtl("en"));
        Assert.Equal("نمای کلی", Catalog.Get("fa", "overview"));
        Assert.Equal("Overview", Catalog.Get("en", "overview"));
    }

    [Fact]
    public void Settings_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "edgestpan-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var settings = new AppSettings { DisplayName = "Desk", Language = "fa", ClipboardShare = false };
        settings.Devices.Add(new DeviceConfig { Id = "d1", Name = "Tab", PcEdge = "Left", ReturnEdge = "Right" });
        SettingsStore.Save(path, settings);
        var loaded = SettingsStore.Load(path);
        Assert.Equal("Desk", loaded.DisplayName);
        Assert.Equal("fa", loaded.Language);
        Assert.Equal("Tab", loaded.Devices[0].Name);
        Assert.False(loaded.ClipboardShare);
    }

    [Fact]
    public void Signal_json_roundtrip()
    {
        Assert.True(SignalJson.TryParse(SignalJson.Hello("Office", "host"), out var hello));
        Assert.Equal("hello", hello.Op);
        Assert.Equal("Office", hello.Name);
        Assert.Equal(new Uri("ws://127.0.0.1:8787/v1/signal"), SignalJson.ToWebSocket("http://127.0.0.1:8787"));
    }
}
