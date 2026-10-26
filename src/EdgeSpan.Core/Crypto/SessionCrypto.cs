using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EdgeSpan.Protocol;

namespace EdgeSpan.Crypto;

public interface IRandom
{
    void Fill(Span<byte> buffer);
}

public sealed class SystemRandomBytes : IRandom
{
    public void Fill(Span<byte> buffer) => RandomNumberGenerator.Fill(buffer);
}

public sealed class EcKeyPair : IDisposable
{
    private readonly ECDiffieHellman _ecdh;
    public byte[] Public { get; }

    private EcKeyPair(ECDiffieHellman ecdh)
    {
        _ecdh = ecdh;
        var p = ecdh.ExportParameters(false);
        Public = new byte[65];
        Public[0] = 0x04;
        Pad32(p.Q.X!).CopyTo(Public, 1);
        Pad32(p.Q.Y!).CopyTo(Public, 33);
    }

    public static EcKeyPair Generate() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public static EcKeyPair Import(ReadOnlySpan<byte> d, ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var param = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Pad32(d),
            Q = new ECPoint { X = Pad32(x), Y = Pad32(y) }
        };
        return new EcKeyPair(ECDiffieHellman.Create(param));
    }

    public byte[] SharedSecret(ReadOnlySpan<byte> uncompressedPublic)
    {
        if (uncompressedPublic.Length != 65 || uncompressedPublic[0] != 0x04)
            throw new ArgumentException("Expected an uncompressed P-256 public key.");
        var param = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = uncompressedPublic.Slice(1, 32).ToArray(),
                Y = uncompressedPublic.Slice(33, 32).ToArray()
            }
        };
        using var other = ECDiffieHellman.Create(param);
        return Pad32(_ecdh.DeriveRawSecretAgreement(other.PublicKey));
    }

    public void Dispose() => _ecdh.Dispose();

    public static byte[] Pad32(ReadOnlySpan<byte> src)
    {
        if (src.Length == 32)
            return src.ToArray();
        if (src.Length == 33 && src[0] == 0)
            return src[1..].ToArray();
        if (src.Length > 32)
            throw new ArgumentException("Coordinate is longer than 32 bytes.");
        var dst = new byte[32];
        src.CopyTo(dst.AsSpan(32 - src.Length));
        return dst;
    }
}

public static class SessionCrypto
{
    public const string PairInfo = "edgestpan-pair-v1";
    public const string HostToClientInfo = "edgestpan-h2c-v1";
    public const string ClientToHostInfo = "edgestpan-c2h-v1";
    public const string NonceInfo = "edgestpan-nonce-v1";
    public const string MediaInfo = "edgestpan-media-v1";

    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm.ToArray(), length, salt.ToArray(), Encoding.UTF8.GetBytes(info));

    public static byte[] PairMacKey(ReadOnlySpan<byte> sharedX, string pin) =>
        Hkdf(sharedX, Encoding.UTF8.GetBytes(pin.Trim()), PairInfo, 32);

    public static byte[] ClientProof(ReadOnlySpan<byte> macKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> clientPub, ReadOnlySpan<byte> hostPub)
    {
        var data = new byte[1 + nonce.Length + clientPub.Length + hostPub.Length];
        data[0] = 0x02;
        nonce.CopyTo(data.AsSpan(1));
        clientPub.CopyTo(data.AsSpan(1 + nonce.Length));
        hostPub.CopyTo(data.AsSpan(1 + nonce.Length + clientPub.Length));
        return HMACSHA256.HashData(macKey, data);
    }

    public static byte[] HostProof(ReadOnlySpan<byte> macKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> hostPub, ReadOnlySpan<byte> clientPub)
    {
        var data = new byte[1 + nonce.Length + salt.Length + hostPub.Length + clientPub.Length];
        data[0] = 0x01;
        nonce.CopyTo(data.AsSpan(1));
        salt.CopyTo(data.AsSpan(1 + nonce.Length));
        hostPub.CopyTo(data.AsSpan(1 + nonce.Length + salt.Length));
        clientPub.CopyTo(data.AsSpan(1 + nonce.Length + salt.Length + hostPub.Length));
        return HMACSHA256.HashData(macKey, data);
    }

    public static void Derive(
        ReadOnlySpan<byte> sharedX,
        ReadOnlySpan<byte> salt,
        bool isHost,
        out byte[] sendKey,
        out byte[] receiveKey,
        out byte[] noncePrefix)
    {
        var hostToClient = Hkdf(sharedX, salt, HostToClientInfo, 32);
        var clientToHost = Hkdf(sharedX, salt, ClientToHostInfo, 32);
        noncePrefix = Hkdf(sharedX, salt, NonceInfo, 4);
        if (isHost)
        {
            sendKey = hostToClient;
            receiveKey = clientToHost;
        }
        else
        {
            sendKey = clientToHost;
            receiveKey = hostToClient;
        }
    }
}

public sealed class SessionCipher
{
    private readonly byte[] _sendKey;
    private readonly byte[] _recvKey;
    private readonly byte[] _prefix;
    private readonly byte _sendDir;
    private readonly byte _recvDir;
    private uint _seq;

    public uint SessionId { get; }
    public uint LastSequence => _seq;
    public byte[] MediaSendKey() => SessionCrypto.Hkdf(_sendKey, _prefix, SessionCrypto.MediaInfo, 32);
    public byte[] MediaReceiveKey() => SessionCrypto.Hkdf(_recvKey, _prefix, SessionCrypto.MediaInfo, 32);

    public SessionCipher(byte[] sendKey, byte[] receiveKey, byte[] noncePrefix, uint sessionId, bool isHost)
    {
        if (sendKey.Length != 32 || receiveKey.Length != 32 || noncePrefix.Length < 4)
            throw new ArgumentException("Session key material is the wrong length.");
        if (sessionId == 0)
            throw new ArgumentException("Session id must be non-zero.");
        _sendKey = sendKey;
        _recvKey = receiveKey;
        _prefix = noncePrefix[..4];
        _sendDir = isHost ? (byte)1 : (byte)2;
        _recvDir = isHost ? (byte)2 : (byte)1;
        SessionId = sessionId;
    }

    public byte[] Seal(PacketType type, byte flags, ReadOnlySpan<byte> payload, uint ack, ulong timestampUs)
    {
        var seq = ++_seq;
        flags |= PacketFlags.Encrypted;
        var packet = new byte[PacketCodec.HeaderSize + payload.Length + PacketCodec.TagSize];
        PacketCodec.WriteHeader(packet, type, flags, SessionId, seq, ack, timestampUs, (ushort)payload.Length);
        var cipher = packet.AsSpan(PacketCodec.HeaderSize, payload.Length);
        var tag = packet.AsSpan(PacketCodec.HeaderSize + payload.Length, PacketCodec.TagSize);
        using var aes = new AesGcm(_sendKey, PacketCodec.TagSize);
        aes.Encrypt(Nonce(_sendDir, seq), payload, cipher, tag, packet.AsSpan(0, PacketCodec.HeaderSize));
        return packet;
    }

    public bool TryOpen(ReadOnlySpan<byte> datagram, out PacketHeader header, out byte[] plaintext)
    {
        header = default;
        plaintext = Array.Empty<byte>();
        if (!PacketCodec.TryReadHeader(datagram, out header) || !header.Encrypted)
            return false;
        if (header.SessionId != SessionId)
            return false;
        var total = PacketCodec.HeaderSize + header.PayloadLength + PacketCodec.TagSize;
        if (datagram.Length < total)
            return false;
        var plain = new byte[header.PayloadLength];
        try
        {
            using var aes = new AesGcm(_recvKey, PacketCodec.TagSize);
            aes.Decrypt(
                Nonce(_recvDir, header.Sequence),
                datagram.Slice(PacketCodec.HeaderSize, header.PayloadLength),
                datagram.Slice(PacketCodec.HeaderSize + header.PayloadLength, PacketCodec.TagSize),
                plain,
                datagram[..PacketCodec.HeaderSize]);
        }
        catch (CryptographicException)
        {
            return false;
        }
        plaintext = plain;
        return true;
    }

    private byte[] Nonce(byte direction, uint sequence)
    {
        var nonce = new byte[12];
        _prefix.CopyTo(nonce, 0);
        nonce[4] = direction;
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(5), sequence);
        return nonce;
    }
}
