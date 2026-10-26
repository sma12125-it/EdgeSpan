using EdgeSpan.Crypto;
using EdgeSpan.Product;
using EdgeSpan.Protocol;

namespace EdgeSpan.Session;

public sealed class WireSession
{
    private readonly bool _isHost;
    private readonly Func<long> _clock;
    private readonly RuntimeOptions _options;
    private readonly ReplayWindow _replay = new();
    private readonly MouseCoalescer _mouse = new();
    private readonly FragmentAssembler _fragments = new();
    private readonly Queue<Pending> _wait = new();
    private readonly Dictionary<ulong, long> _pingAt = new();
    private SessionCipher? _cipher;
    private bool _inFlight;
    private uint _flightSeq;
    private byte[] _flightBytes = Array.Empty<byte>();
    private long _flightAt;
    private int _attempts;
    private uint _msgId;
    private uint _lastMouse;
    private bool _seenMouse;
    private long _lastBeat;
    private ulong _pingToken;

    private readonly record struct Pending(PacketType Type, byte Flags, byte[] Payload);

    public bool IsHost => _isHost;
    public bool Established => _cipher != null;
    public byte[]? MediaSendKey() => _cipher?.MediaSendKey();
    public uint SessionId => _cipher?.SessionId ?? 0;
    public double? LastRttMs { get; private set; }

    public WireSession(bool isHost, Func<long> clock, RuntimeOptions options)
    {
        _isHost = isHost;
        _clock = clock;
        _options = options;
    }

    public void Establish(byte[] sendKey, byte[] receiveKey, byte[] noncePrefix, uint sessionId)
    {
        _cipher = new SessionCipher(sendKey, receiveKey, noncePrefix, sessionId, _isHost);
        _inFlight = false;
        _wait.Clear();
        _lastBeat = _clock();
    }

    public void QueueMouse(int x, int y) => _mouse.Set(x, y);

    public void ClearMouse() => _mouse.Clear();

    public void FlushMouse() => _mouse.Force = true;

    public byte[] SendUnreliable(PacketType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > _options.FragmentBytes)
            throw new ArgumentException("Unreliable payload must fit in one fragment.");
        return Seal(type, 0, payload, 0);
    }

    public List<byte[]> SendReliable(PacketType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ProductInfo.MaxClipboardBytes)
            throw new ArgumentException("Payload exceeds the clipboard limit.");
        var output = new List<byte[]>();
        if (payload.Length <= _options.FragmentBytes)
        {
            HoldOrSend(type, 0, payload.ToArray(), output);
            return output;
        }
        var count = (payload.Length + _options.FragmentBytes - 1) / _options.FragmentBytes;
        var msgId = ++_msgId;
        for (var i = 0; i < count; i++)
        {
            var n = Math.Min(_options.FragmentBytes, payload.Length - (i * _options.FragmentBytes));
            var wrapped = Payloads.Fragment(msgId, (ushort)i, (ushort)count, payload.Slice(i * _options.FragmentBytes, n));
            HoldOrSend(type, PacketFlags.Fragment, wrapped, output);
        }
        return output;
    }

    public ReceiveResult Receive(ReadOnlySpan<byte> datagram, long nowMs)
    {
        var result = new ReceiveResult();
        if (_cipher == null || !_cipher.TryOpen(datagram, out var header, out var plaintext))
            return result;
        if (header.Ack != 0 && header.Ack == _flightSeq)
            _inFlight = false;
        var fresh = _replay.Accept(header.Sequence);
        if (header.Reliable)
            result.Replies.Add(Seal(PacketType.Ack, 0, ReadOnlySpan<byte>.Empty, header.Sequence));
        if (!fresh || header.Type == PacketType.Ack)
            return result;
        if (header.Type == PacketType.MouseMove)
        {
            if (_seenMouse && header.Sequence < _lastMouse)
                return result;
            _seenMouse = true;
            _lastMouse = header.Sequence;
        }
        var body = plaintext;
        if (header.Fragment)
        {
            if (!Payloads.TryFragment(plaintext, out var msgId, out var index, out var count, out var chunk))
                return result;
            var full = _fragments.Push((byte)header.Type, msgId, index, count, chunk, nowMs);
            if (full == null)
                return result;
            body = full;
        }
        Dispatch(header, body, nowMs, result);
        return result;
    }

    public List<byte[]> Poll(long nowMs)
    {
        var output = new List<byte[]>();
        if (_cipher == null)
            return output;
        if (_inFlight && _attempts < _options.ReliableMaxAttempts && nowMs - _flightAt >= _options.ReliableRetryMs)
        {
            output.Add(_flightBytes);
            _flightAt = nowMs;
            _attempts++;
        }
        if (!_inFlight && _wait.Count > 0)
        {
            var next = _wait.Dequeue();
            HoldOrSend(next.Type, next.Flags, next.Payload, output);
        }
        if (_mouse.TryFlush(nowMs, _options.CoalesceMs, out var x, out var y))
            output.Add(Seal(PacketType.MouseMove, 0, Payloads.MouseMove(x, y), 0));
        if (_options.HeartbeatMs > 0 && nowMs - _lastBeat >= _options.HeartbeatMs)
        {
            _lastBeat = nowMs;
            var token = ++_pingToken;
            _pingAt[token] = nowMs;
            output.Add(Seal(PacketType.Ping, 0, Payloads.Token(token), 0));
            if (_pingAt.Count > 8)
            {
                var oldest = _pingAt.Keys.Min();
                _pingAt.Remove(oldest);
            }
        }
        return output;
    }

    private void Dispatch(PacketHeader header, byte[] body, long nowMs, ReceiveResult result)
    {
        switch (header.Type)
        {
            case PacketType.Hello:
            case PacketType.HelloAck:
                if (Payloads.TryHello(body, out var hello))
                    result.Events.Add(new SessionEvent.HelloReceived(hello.Role, hello.Capabilities, hello.Width, hello.Height, hello.Port, hello.Id, hello.Name));
                break;
            case PacketType.MouseMove:
                if (Payloads.TryMouseMove(body, out var mx, out var my))
                    result.Events.Add(new SessionEvent.MouseMoved(mx, my));
                break;
            case PacketType.MouseButton:
                if (Payloads.TryMouseButton(body, out var button, out var down, out var bx, out var by))
                    result.Events.Add(new SessionEvent.MouseButtonChanged(button, down, bx, by));
                break;
            case PacketType.MouseScroll:
                if (Payloads.TryScroll(body, out var sdx, out var sdy, out var sx, out var sy))
                    result.Events.Add(new SessionEvent.Scrolled(sdx, sdy, sx, sy));
                break;
            case PacketType.KeyEvent:
                if (Payloads.TryKey(body, out var vk, out var hid, out var mods, out var keyDown, out var cp))
                    result.Events.Add(new SessionEvent.KeyChanged(vk, hid, mods, keyDown, cp));
                break;
            case PacketType.Clipboard:
                if (Payloads.TryClipboard(body, out var id, out var text))
                    result.Events.Add(new SessionEvent.ClipboardReceived(id, text));
                break;
            case PacketType.FileOffer:
                if (Payloads.TryFileOffer(body, out var offerId, out var cut, out var offerSize, out var offerName))
                    result.Events.Add(new SessionEvent.FileOfferReceived(offerId, offerName, offerSize, cut));
                break;
            case PacketType.FileChunk:
                if (Payloads.TryFileChunk(body, out var chunkId, out var chunkOffset, out var chunkData))
                    result.Events.Add(new SessionEvent.FileChunkReceived(chunkId, chunkOffset, chunkData));
                break;
            case PacketType.FileAck:
                if (Payloads.TryFileAck(body, out var ackId, out var ackNext))
                    result.Events.Add(new SessionEvent.FileAckReceived(ackId, ackNext));
                break;
            case PacketType.FileDone:
                if (Payloads.TryFileDone(body, out var doneId, out var doneOk))
                    result.Events.Add(new SessionEvent.FileDoneReceived(doneId, doneOk));
                break;
            case PacketType.EdgeEnter:
                if (Payloads.TryEdgeEnter(body, out var edge, out var ex, out var ey, out var w, out var h))
                    result.Events.Add(new SessionEvent.EdgeEntered(edge, ex, ey, w, h));
                break;
            case PacketType.EdgeReturn:
                if (Payloads.TryEdgeReturn(body, out var hx, out var hy, out var dx, out var dy))
                    result.Events.Add(new SessionEvent.EdgeReturned(hx, hy, dx, dy));
                break;
            case PacketType.Lock:
                result.Events.Add(new SessionEvent.Locked(true));
                break;
            case PacketType.Unlock:
                result.Events.Add(new SessionEvent.Locked(false));
                break;
            case PacketType.Goodbye:
                result.Events.Add(new SessionEvent.GoodbyeReceived(body.Length > 0 ? body[0] : (byte)0));
                break;
            case PacketType.HandoffOffer:
                if (HandoffPayloads.TryOffer(body, out var offer))
                    result.Events.Add(new SessionEvent.HandoffOffered(offer.Id, offer.Direction, offer.Dpi, offer.PhysW, offer.PhysH, offer.DipW, offer.DipH, offer.Codec, offer.Title));
                break;
            case PacketType.HandoffAccept:
                if (HandoffPayloads.TryAccept(body, out var accept))
                    result.Events.Add(new SessionEvent.HandoffAccepted(accept.Id, accept.Codec, accept.ViewW, accept.ViewH));
                break;
            case PacketType.HandoffGeometry:
                if (HandoffPayloads.TryGeometry(body, out var geometry))
                    result.Events.Add(new SessionEvent.HandoffGeometryReceived(geometry.Id, geometry.X, geometry.Y, geometry.PhysW, geometry.PhysH, geometry.DipW, geometry.DipH));
                break;
            case PacketType.HandoffInput:
                if (HandoffPayloads.TryInput(body, out var handoffInput))
                    result.Events.Add(new SessionEvent.HandoffInputReceived(handoffInput.Id, handoffInput.Kind, handoffInput.X, handoffInput.Y, handoffInput.Button, handoffInput.Down, handoffInput.WheelX, handoffInput.WheelY, handoffInput.Vk, handoffInput.Text));
                break;
            case PacketType.HandoffState:
                if (HandoffPayloads.TryState(body, out var handoffState))
                    result.Events.Add(new SessionEvent.HandoffStateReceived(handoffState.Id, handoffState.Value));
                break;
            case PacketType.HandoffReturn:
                if (HandoffPayloads.TryReturn(body, out var handoffId))
                    result.Events.Add(new SessionEvent.HandoffReturned(handoffId));
                break;
            case PacketType.HandoffVideoConfig:
                if (HandoffPayloads.TryVideoConfig(body, out var video))
                    result.Events.Add(new SessionEvent.HandoffVideoConfigReceived(video.Id, video.Bitrate, video.Fps, video.Idr));
                break;
            case PacketType.Ping:
                if (Payloads.TryToken(body, out var token))
                    result.Replies.Add(Seal(PacketType.Pong, 0, Payloads.Token(token), 0));
                break;
            case PacketType.Pong:
                if (Payloads.TryToken(body, out var pong) && _pingAt.TryGetValue(pong, out var sent))
                {
                    _pingAt.Remove(pong);
                    var rtt = Math.Max(0, nowMs - sent);
                    LastRttMs = rtt;
                    result.Events.Add(new SessionEvent.PongReceived(pong, rtt));
                }
                break;
        }
    }

    private void HoldOrSend(PacketType type, byte flags, byte[] payload, List<byte[]> output)
    {
        if (_inFlight)
        {
            _wait.Enqueue(new Pending(type, flags, payload));
            return;
        }
        var bytes = Seal(type, (byte)(flags | PacketFlags.Reliable), payload, 0);
        _inFlight = true;
        _flightSeq = _cipher!.LastSequence;
        _flightBytes = bytes;
        _flightAt = _clock();
        _attempts = 1;
        output.Add(bytes);
    }

    private byte[] Seal(PacketType type, byte flags, ReadOnlySpan<byte> payload, uint ack)
    {
        if (_cipher == null)
            throw new InvalidOperationException("Session is not established.");
        return _cipher.Seal(type, flags, payload, ack, (ulong)_clock() * 1000UL);
    }
}
