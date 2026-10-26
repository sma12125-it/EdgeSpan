using System.Buffers.Binary;
using System.Security.Cryptography;
using EdgeSpan.Protocol;

namespace EdgeSpan.Crypto;

public enum PairRole : byte
{
    Host = 1,
    Client = 2
}

public readonly record struct PairMessage(PacketType Type, byte[] Payload);

public sealed class PairingHandshake : IDisposable
{
    private readonly string _pin;
    private readonly EcKeyPair _local;
    private readonly IRandom _random;
    private byte[]? _peerPub;
    private byte[]? _nonce;
    private byte[]? _shared;
    private Stage _stage = Stage.Fresh;
    private bool _disposed;
    private bool _keyGone;

    private enum Stage { Fresh, Challenged, Confirmed, Done, Dead }

    public PairRole Role { get; }
    public bool Established { get; private set; }
    public bool Failed { get; private set; }
    public string? FailReason { get; private set; }
    public byte[]? SendKey { get; private set; }
    public byte[]? ReceiveKey { get; private set; }
    public byte[]? NoncePrefix { get; private set; }
    public uint SessionId { get; private set; }
    public string PeerName { get; private set; } = "";
    public string PeerId { get; private set; } = "";
    public byte[] PublicKey { get; }

    private PairingHandshake(PairRole role, string pin, EcKeyPair local, IRandom random, uint sessionId)
    {
        Role = role;
        _pin = pin.Trim();
        _local = local;
        _random = random;
        SessionId = sessionId;
        PublicKey = local.Public;
    }

    public static PairingHandshake Create(PairRole role, string pin, IRandom random, uint sessionId = 0)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Trim().Length < 4)
            throw new ArgumentException("PIN must be at least 4 characters.");
        return new PairingHandshake(role, pin, EcKeyPair.Generate(), random, sessionId);
    }

    public PairMessage BuildInit(string id, string name) =>
        new(PacketType.PairInit, Payloads.PairInit(PublicKey, id, name));

    public List<PairMessage> Consume(PacketType type, ReadOnlySpan<byte> payload)
    {
        var list = new List<PairMessage>();
        if (_stage is Stage.Done or Stage.Dead)
            return list;
        try
        {
            if (type == PacketType.PairReject)
            {
                Fail("rejected");
                return list;
            }
            if (Role == PairRole.Host)
                ConsumeHost(type, payload, list);
            else
                ConsumeClient(type, payload, list);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
        {
            Fail(ex.Message);
        }
        return list;
    }

    private void ConsumeHost(PacketType type, ReadOnlySpan<byte> payload, List<PairMessage> list)
    {
        var pendingNonce = _nonce;
        if (type == PacketType.PairInit && _stage == Stage.Challenged && _peerPub != null && pendingNonce != null
            && Payloads.TryPairInit(payload, out var again) && again.PublicKey.AsSpan().SequenceEqual(_peerPub))
        {
            list.Add(new PairMessage(PacketType.PairChallenge, Payloads.PairChallenge(PublicKey, pendingNonce)));
            return;
        }
        if (type == PacketType.PairInit && _stage == Stage.Fresh)
        {
            if (!Payloads.TryPairInit(payload, out var msg))
            {
                Fail("bad init");
                return;
            }
            _peerPub = msg.PublicKey;
            PeerId = msg.Id;
            PeerName = msg.Name;
            _shared = _local.SharedSecret(msg.PublicKey);
            _nonce = new byte[16];
            _random.Fill(_nonce);
            _stage = Stage.Challenged;
            list.Add(new PairMessage(PacketType.PairChallenge, Payloads.PairChallenge(PublicKey, _nonce)));
            return;
        }
        if (type == PacketType.PairConfirm && _stage == Stage.Challenged)
        {
            if (!Payloads.TryPairConfirm(payload, out var pub, out var proof) || _peerPub == null || _nonce == null || _shared == null)
            {
                Fail("bad confirm");
                return;
            }
            if (!pub.AsSpan().SequenceEqual(_peerPub))
            {
                Fail("key mismatch");
                list.Add(new PairMessage(PacketType.PairReject, Payloads.PairReject(1)));
                return;
            }
            var mac = SessionCrypto.PairMacKey(_shared, _pin);
            var expect = SessionCrypto.ClientProof(mac, _nonce, _peerPub, PublicKey);
            if (!CryptographicOperations.FixedTimeEquals(expect, proof))
            {
                Fail("bad pin");
                list.Add(new PairMessage(PacketType.PairReject, Payloads.PairReject(1)));
                return;
            }
            var salt = new byte[16];
            _random.Fill(salt);
            if (SessionId == 0)
            {
                Span<byte> raw = stackalloc byte[4];
                _random.Fill(raw);
                SessionId = BinaryPrimitives.ReadUInt32LittleEndian(raw);
                if (SessionId == 0)
                    SessionId = 1;
            }
            var hostProof = SessionCrypto.HostProof(mac, _nonce, salt, PublicKey, _peerPub);
            SessionCrypto.Derive(_shared, salt, true, out var send, out var recv, out var prefix);
            SendKey = send;
            ReceiveKey = recv;
            NoncePrefix = prefix;
            Established = true;
            _stage = Stage.Done;
            DropKey();
            list.Add(new PairMessage(PacketType.PairAccept, Payloads.PairAccept(SessionId, salt, hostProof)));
            return;
        }
        Fail("unexpected");
    }

    private void ConsumeClient(PacketType type, ReadOnlySpan<byte> payload, List<PairMessage> list)
    {
        if (type == PacketType.PairChallenge && _stage == Stage.Confirmed && _peerPub != null
            && Payloads.TryPairChallenge(payload, out var repeated, out _) && repeated.AsSpan().SequenceEqual(_peerPub))
            return;
        if (type == PacketType.PairChallenge && _stage == Stage.Fresh)
        {
            if (!Payloads.TryPairChallenge(payload, out var pub, out var nonce))
            {
                Fail("bad challenge");
                return;
            }
            _peerPub = pub;
            _nonce = nonce;
            _shared = _local.SharedSecret(pub);
            var mac = SessionCrypto.PairMacKey(_shared, _pin);
            var proof = SessionCrypto.ClientProof(mac, nonce, PublicKey, pub);
            _stage = Stage.Confirmed;
            list.Add(new PairMessage(PacketType.PairConfirm, Payloads.PairConfirm(PublicKey, proof)));
            return;
        }
        if (type == PacketType.PairAccept && _stage == Stage.Confirmed && _shared != null && _nonce != null && _peerPub != null)
        {
            if (!Payloads.TryPairAccept(payload, out var sid, out var salt, out var hostProof))
            {
                Fail("bad accept");
                return;
            }
            var mac = SessionCrypto.PairMacKey(_shared, _pin);
            var expect = SessionCrypto.HostProof(mac, _nonce, salt, _peerPub, PublicKey);
            if (!CryptographicOperations.FixedTimeEquals(expect, hostProof))
            {
                Fail("host proof");
                list.Add(new PairMessage(PacketType.PairReject, Payloads.PairReject(1)));
                return;
            }
            SessionId = sid;
            SessionCrypto.Derive(_shared, salt, false, out var send, out var recv, out var prefix);
            SendKey = send;
            ReceiveKey = recv;
            NoncePrefix = prefix;
            Established = true;
            _stage = Stage.Done;
            DropKey();
            return;
        }
        Fail("unexpected");
    }

    private void Fail(string reason)
    {
        Failed = true;
        FailReason = reason;
        _stage = Stage.Dead;
        Established = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DropKey();
    }

    private void DropKey()
    {
        if (_keyGone)
            return;
        _keyGone = true;
        _local.Dispose();
    }
}
