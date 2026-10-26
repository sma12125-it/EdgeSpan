using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EdgeSpan.Protocol;

/// <summary>
/// One encoded frame on the media plane. The nonce space is not the session's,
/// so a video packet cannot be replayed as a mouse packet.
/// </summary>
public static class MediaFrames
{
    public static byte[] Protect(byte[] key, ulong sequence, uint id, uint frameId, ulong captureUs, byte flags, ReadOnlySpan<byte> annexb)
    {
        var plain = new byte[4 + 4 + 8 + 1 + annexb.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(plain, id);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), frameId);
        BinaryPrimitives.WriteUInt64LittleEndian(plain.AsSpan(8), captureUs);
        plain[16] = flags;
        annexb.CopyTo(plain.AsSpan(17));
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, sequence);
        nonce[8] = (byte)'M';
        nonce[9] = (byte)'E';
        nonce[10] = (byte)'D';
        nonce[11] = (byte)'1';
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var body = new byte[4 + cipher.Length + tag.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)(cipher.Length + tag.Length));
        cipher.CopyTo(body.AsSpan(4));
        tag.CopyTo(body.AsSpan(4 + cipher.Length));
        return body;
    }

    public static bool Open(byte[] key, ulong sequence, ReadOnlySpan<byte> body, out uint id, out uint frameId, out ulong captureUs, out byte flags, out byte[] annexb)
    {
        id = frameId = 0;
        captureUs = 0;
        flags = 0;
        annexb = Array.Empty<byte>();
        if (body.Length < 4 + 16) return false;
        var n = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (n < 16 || 4 + n != body.Length) return false;
        var cipherLen = n - 16;
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, sequence);
        nonce[8] = (byte)'M';
        nonce[9] = (byte)'E';
        nonce[10] = (byte)'D';
        nonce[11] = (byte)'1';
        var plain = new byte[cipherLen];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, body.Slice(4, cipherLen), body.Slice(4 + cipherLen, 16), plain);
        }
        catch (CryptographicException)
        {
            return false;
        }
        if (plain.Length < 17) return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(plain);
        frameId = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4));
        captureUs = BinaryPrimitives.ReadUInt64LittleEndian(plain.AsSpan(8));
        flags = plain[16];
        annexb = plain[17..];
        return true;
    }
}
