using System.Buffers.Binary;

namespace EdgeSpan.Net;

/// <summary>Length-prefixed datagrams so the UDP session can travel over a TCP tunnel.</summary>
public static class DatagramFrames
{
    public const int MaxPayload = 65535;

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(2));
        return frame;
    }
}

public sealed class FrameReader
{
    private byte[] _buf = Array.Empty<byte>();
    private int _len;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        if (_buf.Length < _len + data.Length)
        {
            var grown = new byte[Math.Max(Math.Max(64, _buf.Length * 2), _len + data.Length)];
            _buf.AsSpan(0, _len).CopyTo(grown);
            _buf = grown;
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    public bool TryTake(out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (_len < 2)
            return false;
        var size = _buf[0] | (_buf[1] << 8);
        if (size == 0 || size > DatagramFrames.MaxPayload)
        {
            _len = 0;
            return false;
        }
        if (_len < 2 + size)
            return false;
        payload = _buf.AsSpan(2, size).ToArray();
        var rest = _len - (2 + size);
        if (rest > 0)
            Buffer.BlockCopy(_buf, 2 + size, _buf, 0, rest);
        _len = rest;
        return true;
    }
}
