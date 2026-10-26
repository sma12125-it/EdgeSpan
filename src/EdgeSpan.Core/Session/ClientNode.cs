using System.Text;
using EdgeSpan.Crypto;
using EdgeSpan.Layout;
using EdgeSpan.Product;
using EdgeSpan.Protocol;

namespace EdgeSpan.Session;

/// <summary>
/// Reference Android-side session used by tests and the simulator.
/// The Kotlin client follows the same packet sequence.
/// </summary>
public sealed class ClientNode : IDisposable
{
    private readonly Func<long> _clock;
    private readonly IRandom _random;
    private readonly RuntimeOptions _options;
    private readonly object _gate = new();
    private PairingHandshake? _pairing;
    private WireSession? _session;
    private int _clearSeq;

    public string Id { get; }
    public string Name { get; }
    public int ScreenW { get; }
    public int ScreenH { get; }
    public byte Capabilities { get; private set; }
    public bool HasControl { get; private set; }
    public bool Locked { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public ScreenEdge ReturnEdge { get; private set; } = ScreenEdge.Left;
    public string? Clipboard { get; private set; }
    public List<string> Files { get; } = new();
    public string ReceivedDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "edgestpan-received");
    private readonly FileSync _files = new();
    private uint _fileSeq;
    public List<string> Keys { get; } = new();
    public List<string> Pointer { get; } = new();
    public List<uint> HandoffOffers { get; } = new();
    public List<byte[]> Out { get; } = new();
    public bool Established => _session?.Established == true;

    public ClientNode(string id, string name, int screenW, int screenH, Func<long> clock, IRandom random, RuntimeOptions? options = null, byte capabilities = 0x7)
    {
        Id = id;
        Name = name;
        ScreenW = screenW;
        ScreenH = screenH;
        _clock = clock;
        _random = random;
        _options = options ?? new RuntimeOptions();
        Capabilities = capabilities;
    }

    public void StartPairing(string pin)
    {
        lock (_gate)
        {
            _pairing?.Dispose();
            _pairing = PairingHandshake.Create(PairRole.Client, pin, _random);
            var init = _pairing.BuildInit(Id, Name);
            SendClear(init);
        }
    }

    public void Ingest(byte[] datagram, long nowMs)
    {
        lock (_gate)
        {
            if (!PacketCodec.TryReadHeader(datagram, out var header))
                return;
            if (header.Encrypted)
            {
                if (_session == null)
                    return;
                var result = _session.Receive(datagram, nowMs);
                foreach (var reply in result.Replies)
                    Out.Add(reply);
                foreach (var ev in result.Events)
                    OnEvent(ev);
                FlushFiles();
                foreach (var extra in _session.Poll(nowMs))
                    Out.Add(extra);
                return;
            }
            if (_pairing == null || !PacketCodec.TryDecodeClear(datagram, out header, out var payload))
                return;
            var replies = _pairing.Consume(header.Type, payload);
            foreach (var reply in replies)
                SendClear(reply);
            if (_pairing.Established && _pairing.SendKey != null && _pairing.ReceiveKey != null && _pairing.NoncePrefix != null)
            {
                _session = new WireSession(false, _clock, _options);
                _session.Establish(_pairing.SendKey, _pairing.ReceiveKey, _pairing.NoncePrefix, _pairing.SessionId);
                foreach (var packet in _session.SendReliable(PacketType.Hello, Payloads.Hello(2, Capabilities, (ushort)Math.Clamp(ScreenW, 1, 65535), (ushort)Math.Clamp(ScreenH, 1, 65535), 0, Id, Name)))
                    Out.Add(packet);
            }
        }
    }

    public void Tick(long nowMs)
    {
        lock (_gate)
        {
            if (_session == null)
                return;
            FlushFiles();
            foreach (var packet in _session.Poll(nowMs))
                Out.Add(packet);
        }
    }

    public int QueueMouseBurst(int count)
    {
        lock (_gate)
        {
            if (_session?.Established != true)
                return 0;
            for (var i = 0; i < count; i++)
                Out.Add(_session.SendUnreliable(PacketType.MouseMove, Payloads.MouseMove(i & 1023, 3)));
            return count;
        }
    }

    public int QueueClipboardBurst(string text, int count)
    {
        lock (_gate)
        {
            if (_session?.Established != true)
                return 0;
            var payload = Payloads.Clipboard(1, Encoding.UTF8.GetBytes(text));
            for (var i = 0; i < count; i++)
                Out.Add(_session.SendUnreliable(PacketType.Clipboard, payload));
            return count;
        }
    }

    public void OfferLocalFile(string path, bool cut)
    {
        lock (_gate)
        {
            _files.Queue("pc", path, cut);
            FlushFiles();
        }
    }

    public void Advertise(byte capabilities)
    {
        lock (_gate)
        {
            Capabilities = capabilities;
            if (_session?.Established != true)
                return;
            foreach (var packet in _session.SendReliable(PacketType.Hello, Payloads.Hello(2, capabilities, (ushort)Math.Clamp(ScreenW, 1, 65535), (ushort)Math.Clamp(ScreenH, 1, 65535), 0, Id, Name)))
                Out.Add(packet);
        }
    }

    public void SendHandoff(PacketType type, byte[] payload)
    {
        lock (_gate)
        {
            if (_session?.Established != true)
                return;
            foreach (var packet in _session.SendReliable(type, payload))
                Out.Add(packet);
        }
    }

    public void Dispose()
    {
        _pairing?.Dispose();
        _files.Dispose();
    }

    private void OnEvent(SessionEvent ev)
    {
        switch (ev)
        {
            case SessionEvent.EdgeEntered entered:
                HasControl = true;
                Locked = false;
                ReturnEdge = (ScreenEdge)entered.Edge;
                X = entered.X;
                Y = entered.Y;
                break;
            case SessionEvent.EdgeReturned:
                HasControl = false;
                break;
            case SessionEvent.MouseMoved moved when HasControl:
                X = moved.X;
                Y = moved.Y;
                if (!Locked && EdgeGuard.Crosses(ReturnEdge, X, Y, ScreenW, ScreenH) && _session != null)
                {
                    HasControl = false;
                    foreach (var packet in _session.SendReliable(PacketType.EdgeReturn, Payloads.EdgeReturn(int.MinValue, int.MinValue, X, Y)))
                        Out.Add(packet);
                }
                break;
            case SessionEvent.MouseButtonChanged button when HasControl:
                Pointer.Add($"button:{button.Button}:{(button.Down ? 1 : 0)}");
                break;
            case SessionEvent.Scrolled wheel when HasControl:
                Pointer.Add($"wheel:{wheel.Dx}:{wheel.Dy}");
                break;
            case SessionEvent.KeyChanged key when HasControl:
                Keys.Add($"{(key.Down ? "down" : "up")}:{key.Vk}:{key.Codepoint}");
                break;
            case SessionEvent.ClipboardReceived clip:
                Clipboard = clip.Text;
                break;
            case SessionEvent.FileOfferReceived offer:
                _files.OnOffer("pc", offer.Id, offer.Name, offer.Size, offer.Cut, ReceivedDirectory);
                break;
            case SessionEvent.FileChunkReceived chunk:
                _files.OnChunk("pc", chunk.Id, chunk.Offset, chunk.Data);
                break;
            case SessionEvent.FileAckReceived ack:
                _files.OnAck("pc", ack.Id, ack.Next);
                break;
            case SessionEvent.FileDoneReceived done:
                _files.OnDone("pc", done.Id, done.Ok);
                break;
            case SessionEvent.Locked locked:
                Locked = locked.IsLocked;
                break;
            case SessionEvent.HandoffOffered offered:
                HandoffOffers.Add(offered.Id);
                break;
        }
    }

    private void FlushFiles()
    {
        if (_session?.Established != true)
            return;
        foreach (var packet in _files.Drain(() => ++_fileSeq))
        {
            foreach (var bytes in _session.SendReliable(packet.Type, packet.Payload))
                Out.Add(bytes);
        }
        foreach (var note in _files.Notes())
        {
            if (note.Incoming && note.Phase == "ready")
                Files.Add(note.Path);
        }
    }

    private void SendClear(PairMessage message)
    {
        Out.Add(PacketCodec.EncodeClear(message.Type, 0, 0, (uint)++_clearSeq, 0, (ulong)_clock() * 1000UL, message.Payload));
    }
}
