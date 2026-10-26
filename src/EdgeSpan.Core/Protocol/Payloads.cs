using System.Buffers.Binary;
using System.Text;

namespace EdgeSpan.Protocol;

public static class Payloads
{
    public const int PublicKeyLength = 65;
    public const int NonceLength = 16;
    public const int SaltLength = 16;
    public const int ProofLength = 32;

    public readonly record struct HelloMsg(byte Role, byte Capabilities, ushort Width, ushort Height, ushort Port, string Id, string Name);
    public readonly record struct PairInitMsg(byte[] PublicKey, string Id, string Name);

    public static byte[] Hello(byte role, byte caps, ushort width, ushort height, ushort port, string id, string name)
    {
        var idBytes = Utf8Trim(id, 32);
        var nameBytes = Utf8Trim(name, 96);
        var buf = new byte[1 + 1 + 2 + 2 + 2 + 1 + idBytes.Length + 1 + nameBytes.Length];
        var o = 0;
        buf[o++] = role;
        buf[o++] = caps;
        WriteU16(buf, ref o, width);
        WriteU16(buf, ref o, height);
        WriteU16(buf, ref o, port);
        buf[o++] = (byte)idBytes.Length;
        idBytes.CopyTo(buf.AsSpan(o));
        o += idBytes.Length;
        buf[o++] = (byte)nameBytes.Length;
        nameBytes.CopyTo(buf.AsSpan(o));
        return buf;
    }

    public static bool TryHello(ReadOnlySpan<byte> payload, out HelloMsg msg)
    {
        msg = default;
        if (payload.Length < 8)
            return false;
        var role = payload[0];
        var caps = payload[1];
        var w = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        var h = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        var port = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        var o = 8;
        if (!ReadStr(payload, ref o, out var id) || !ReadStr(payload, ref o, out var name))
            return false;
        msg = new HelloMsg(role, caps, w, h, port, id, name);
        return true;
    }

    public static byte[] PairInit(ReadOnlySpan<byte> publicKey, string id, string name)
    {
        RequirePub(publicKey);
        var idBytes = Utf8Trim(id, 32);
        var nameBytes = Utf8Trim(name, 96);
        var buf = new byte[PublicKeyLength + 1 + idBytes.Length + 1 + nameBytes.Length];
        publicKey.CopyTo(buf);
        buf[PublicKeyLength] = (byte)idBytes.Length;
        idBytes.CopyTo(buf.AsSpan(PublicKeyLength + 1));
        var nameAt = PublicKeyLength + 1 + idBytes.Length;
        buf[nameAt] = (byte)nameBytes.Length;
        nameBytes.CopyTo(buf.AsSpan(nameAt + 1));
        return buf;
    }

    public static bool TryPairInit(ReadOnlySpan<byte> payload, out PairInitMsg msg)
    {
        msg = default;
        if (payload.Length < PublicKeyLength + 2 || payload[0] != 0x04)
            return false;
        var pub = payload[..PublicKeyLength].ToArray();
        var o = PublicKeyLength;
        if (!ReadStr(payload, ref o, out var id) || !ReadStr(payload, ref o, out var name))
            return false;
        msg = new PairInitMsg(pub, id, name);
        return true;
    }

    public static byte[] PairChallenge(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> nonce)
    {
        RequirePub(publicKey);
        if (nonce.Length != NonceLength)
            throw new ArgumentException("Nonce must be 16 bytes.");
        var buf = new byte[PublicKeyLength + NonceLength];
        publicKey.CopyTo(buf);
        nonce.CopyTo(buf.AsSpan(PublicKeyLength));
        return buf;
    }

    public static bool TryPairChallenge(ReadOnlySpan<byte> payload, out byte[] publicKey, out byte[] nonce)
    {
        publicKey = Array.Empty<byte>();
        nonce = Array.Empty<byte>();
        if (payload.Length != PublicKeyLength + NonceLength || payload[0] != 0x04)
            return false;
        publicKey = payload[..PublicKeyLength].ToArray();
        nonce = payload.Slice(PublicKeyLength, NonceLength).ToArray();
        return true;
    }

    public static byte[] PairConfirm(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> proof)
    {
        RequirePub(publicKey);
        if (proof.Length != ProofLength)
            throw new ArgumentException("Proof must be 32 bytes.");
        var buf = new byte[PublicKeyLength + ProofLength];
        publicKey.CopyTo(buf);
        proof.CopyTo(buf.AsSpan(PublicKeyLength));
        return buf;
    }

    public static bool TryPairConfirm(ReadOnlySpan<byte> payload, out byte[] publicKey, out byte[] proof)
    {
        publicKey = Array.Empty<byte>();
        proof = Array.Empty<byte>();
        if (payload.Length != PublicKeyLength + ProofLength || payload[0] != 0x04)
            return false;
        publicKey = payload[..PublicKeyLength].ToArray();
        proof = payload.Slice(PublicKeyLength, ProofLength).ToArray();
        return true;
    }

    public static byte[] PairAccept(uint sessionId, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> proof)
    {
        if (salt.Length != SaltLength || proof.Length != ProofLength)
            throw new ArgumentException("Salt or proof has the wrong length.");
        var buf = new byte[4 + SaltLength + ProofLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, sessionId);
        salt.CopyTo(buf.AsSpan(4));
        proof.CopyTo(buf.AsSpan(4 + SaltLength));
        return buf;
    }

    public static bool TryPairAccept(ReadOnlySpan<byte> payload, out uint sessionId, out byte[] salt, out byte[] proof)
    {
        sessionId = 0;
        salt = Array.Empty<byte>();
        proof = Array.Empty<byte>();
        if (payload.Length != 4 + SaltLength + ProofLength)
            return false;
        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        salt = payload.Slice(4, SaltLength).ToArray();
        proof = payload.Slice(4 + SaltLength, ProofLength).ToArray();
        return true;
    }

    public static byte[] PairReject(byte reason) => new[] { reason };

    public static byte[] MouseMove(int x, int y)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf, x);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), y);
        return buf;
    }

    public static bool TryMouseMove(ReadOnlySpan<byte> payload, out int x, out int y)
    {
        x = y = 0;
        if (payload.Length != 8)
            return false;
        x = BinaryPrimitives.ReadInt32LittleEndian(payload);
        y = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        return true;
    }

    public static byte[] MouseButton(byte button, bool down, int x, int y)
    {
        var buf = new byte[10];
        buf[0] = button;
        buf[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(2), x);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(6), y);
        return buf;
    }

    public static bool TryMouseButton(ReadOnlySpan<byte> payload, out byte button, out bool down, out int x, out int y)
    {
        button = 0;
        down = false;
        x = y = 0;
        if (payload.Length != 10)
            return false;
        button = payload[0];
        down = payload[1] != 0;
        x = BinaryPrimitives.ReadInt32LittleEndian(payload[2..]);
        y = BinaryPrimitives.ReadInt32LittleEndian(payload[6..]);
        return true;
    }

    public static byte[] Scroll(short dx, short dy, int x, int y)
    {
        var buf = new byte[12];
        BinaryPrimitives.WriteInt16LittleEndian(buf, dx);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(2), dy);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), x);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), y);
        return buf;
    }

    public static bool TryScroll(ReadOnlySpan<byte> payload, out short dx, out short dy, out int x, out int y)
    {
        dx = dy = 0;
        x = y = 0;
        if (payload.Length != 12)
            return false;
        dx = BinaryPrimitives.ReadInt16LittleEndian(payload);
        dy = BinaryPrimitives.ReadInt16LittleEndian(payload[2..]);
        x = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        y = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        return true;
    }

    public static byte[] Key(ushort vk, ushort hid, byte mods, bool down, uint codepoint)
    {
        var buf = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, vk);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), hid);
        buf[4] = mods;
        buf[5] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(6), codepoint);
        return buf;
    }

    public static bool TryKey(ReadOnlySpan<byte> payload, out ushort vk, out ushort hid, out byte mods, out bool down, out uint codepoint)
    {
        vk = hid = 0;
        mods = 0;
        down = false;
        codepoint = 0;
        if (payload.Length != 10)
            return false;
        vk = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        hid = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        mods = payload[4];
        down = payload[5] != 0;
        codepoint = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
        return true;
    }

    public static byte[] Clipboard(uint id, ReadOnlySpan<byte> utf8)
    {
        var buf = new byte[8 + utf8.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)utf8.Length);
        utf8.CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static bool TryClipboard(ReadOnlySpan<byte> payload, out uint id, out string text)
    {
        id = 0;
        text = "";
        if (payload.Length < 8)
            return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (len > payload.Length - 8)
            return false;
        text = Encoding.UTF8.GetString(payload.Slice(8, (int)len));
        return true;
    }

    public static byte[] EdgeEnter(byte returnEdge, int x, int y, ushort width, ushort height)
    {
        var buf = new byte[13];
        buf[0] = returnEdge;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1), x);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(5), y);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(9), width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(11), height);
        return buf;
    }

    public static bool TryEdgeEnter(ReadOnlySpan<byte> payload, out byte edge, out int x, out int y, out ushort width, out ushort height)
    {
        edge = 0;
        x = y = 0;
        width = height = 0;
        if (payload.Length != 13)
            return false;
        edge = payload[0];
        x = BinaryPrimitives.ReadInt32LittleEndian(payload[1..]);
        y = BinaryPrimitives.ReadInt32LittleEndian(payload[5..]);
        width = BinaryPrimitives.ReadUInt16LittleEndian(payload[9..]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(payload[11..]);
        return true;
    }

    public static byte[] EdgeReturn(int hostX, int hostY, int deviceX, int deviceY)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf, hostX);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), hostY);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), deviceX);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), deviceY);
        return buf;
    }

    public static bool TryEdgeReturn(ReadOnlySpan<byte> payload, out int hostX, out int hostY, out int deviceX, out int deviceY)
    {
        hostX = hostY = deviceX = deviceY = 0;
        if (payload.Length != 16)
            return false;
        hostX = BinaryPrimitives.ReadInt32LittleEndian(payload);
        hostY = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        deviceX = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        deviceY = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]);
        return true;
    }

    public static byte[] Token(ulong token)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, token);
        return buf;
    }

    public static bool TryToken(ReadOnlySpan<byte> payload, out ulong token)
    {
        token = 0;
        if (payload.Length != 8)
            return false;
        token = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        return true;
    }

    public static byte[] Fragment(uint msgId, ushort index, ushort count, ReadOnlySpan<byte> chunk)
    {
        var buf = new byte[8 + chunk.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, msgId);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), index);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), count);
        chunk.CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static bool TryFragment(ReadOnlySpan<byte> payload, out uint msgId, out ushort index, out ushort count, out byte[] chunk)
    {
        msgId = 0;
        index = count = 0;
        chunk = Array.Empty<byte>();
        if (payload.Length < 8)
            return false;
        msgId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        index = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        count = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        chunk = payload[8..].ToArray();
        return true;
    }

    public static byte[] FileOffer(uint id, bool cut, long size, string name)
    {
        var utf = Utf8Trim(name, 240);
        var buf = new byte[15 + utf.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, id);
        buf[4] = cut ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(5), size);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(13), (ushort)utf.Length);
        utf.CopyTo(buf.AsSpan(15));
        return buf;
    }

    public static bool TryFileOffer(ReadOnlySpan<byte> payload, out uint id, out bool cut, out long size, out string name)
    {
        id = 0;
        cut = false;
        size = 0;
        name = "";
        if (payload.Length < 15)
            return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        cut = payload[4] != 0;
        size = BinaryPrimitives.ReadInt64LittleEndian(payload[5..]);
        var len = BinaryPrimitives.ReadUInt16LittleEndian(payload[13..]);
        if (len > payload.Length - 15 || size < 0)
            return false;
        name = Encoding.UTF8.GetString(payload.Slice(15, len));
        return true;
    }

    public static byte[] FileChunk(uint id, long offset, ReadOnlySpan<byte> data)
    {
        var buf = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)offset);
        data.CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static bool TryFileChunk(ReadOnlySpan<byte> payload, out uint id, out long offset, out byte[] data)
    {
        id = 0;
        offset = 0;
        data = Array.Empty<byte>();
        if (payload.Length < 8)
            return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        offset = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        data = payload[8..].ToArray();
        return true;
    }

    public static byte[] FileAck(uint id, long next)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)next);
        return buf;
    }

    public static bool TryFileAck(ReadOnlySpan<byte> payload, out uint id, out long next)
    {
        id = 0;
        next = 0;
        if (payload.Length < 8)
            return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        next = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        return true;
    }

    public static byte[] FileDone(uint id, bool ok)
    {
        return new[] { (byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24), ok ? (byte)1 : (byte)0 };
    }

    public static bool TryFileDone(ReadOnlySpan<byte> payload, out uint id, out bool ok)
    {
        id = 0;
        ok = false;
        if (payload.Length < 5)
            return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ok = payload[4] != 0;
        return true;
    }

    public static byte[] Utf8Trim(string? value, int maxBytes)
    {
        var acc = new List<byte>();
        Span<byte> tmp = stackalloc byte[4];
        foreach (var rune in (value ?? "").EnumerateRunes())
        {
            var n = rune.EncodeToUtf8(tmp);
            if (acc.Count + n > maxBytes)
                break;
            for (var i = 0; i < n; i++)
                acc.Add(tmp[i]);
        }
        return acc.ToArray();
    }

    private static void WriteU16(byte[] buf, ref int o, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), value);
        o += 2;
    }

    private static bool ReadStr(ReadOnlySpan<byte> payload, ref int o, out string value)
    {
        value = "";
        if (o >= payload.Length)
            return false;
        var n = payload[o++];
        if (o + n > payload.Length)
            return false;
        value = Encoding.UTF8.GetString(payload.Slice(o, n));
        o += n;
        return true;
    }

    private static void RequirePub(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength || publicKey[0] != 0x04)
            throw new ArgumentException("Public key must be a 65-byte uncompressed P-256 point.");
    }
}
