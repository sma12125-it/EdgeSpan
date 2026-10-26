using System.Buffers.Binary;

namespace EdgeSpan.Protocol;

public enum PacketType : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    PairInit = 0x10,
    PairChallenge = 0x11,
    PairConfirm = 0x12,
    PairAccept = 0x13,
    PairReject = 0x14,
    MouseMove = 0x20,
    MouseButton = 0x21,
    MouseScroll = 0x22,
    KeyEvent = 0x23,
    Clipboard = 0x24,
    FileOffer = 0x25,
    FileChunk = 0x26,
    FileAck = 0x27,
    FileDone = 0x28,
    FileCancel = 0x29,
    EdgeEnter = 0x30,
    EdgeReturn = 0x31,
    Heartbeat = 0x40,
    Ping = 0x41,
    Pong = 0x42,
    Ack = 0x43,
    Goodbye = 0x44,
    Lock = 0x50,
    Unlock = 0x51,
    HandoffOffer = 0x60,
    HandoffAccept = 0x61,
    HandoffGeometry = 0x62,
    HandoffInput = 0x63,
    HandoffState = 0x64,
    HandoffReturn = 0x65,
    HandoffVideoConfig = 0x66,
    HandoffClock = 0x67
}

public static class PacketFlags
{
    public const byte Encrypted = 0x01;
    public const byte Reliable = 0x02;
    public const byte Fragment = 0x04;
}

public readonly record struct PacketHeader(
    PacketType Type,
    byte Flags,
    uint SessionId,
    uint Sequence,
    uint Ack,
    ulong TimestampUs,
    ushort PayloadLength)
{
    public bool Encrypted => (Flags & PacketFlags.Encrypted) != 0;
    public bool Reliable => (Flags & PacketFlags.Reliable) != 0;
    public bool Fragment => (Flags & PacketFlags.Fragment) != 0;
}

/// <summary>
/// Little-endian datagram layout. Header is 26 bytes and is the AES-GCM AAD when encrypted.
/// The GCM tag, when present, is the 16 bytes immediately after the payload.
/// </summary>
public static class PacketCodec
{
    public const byte Magic = 0xE5;
    public const byte Version = 1;
    public const int HeaderSize = 26;
    public const int TagSize = 16;
    public const int MaxPayload = 48_000;

    public static int EncodedSize(int payloadLength, bool encrypted) =>
        HeaderSize + payloadLength + (encrypted ? TagSize : 0);

    public static byte[] EncodeClear(PacketType type, byte flags, uint sessionId, uint sequence, uint ack, ulong timestampUs, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new ArgumentException("Payload is too large for one datagram.");
        var buf = new byte[HeaderSize + payload.Length];
        WriteHeader(buf, type, flags, sessionId, sequence, ack, timestampUs, (ushort)payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static void WriteHeader(Span<byte> dest, PacketType type, byte flags, uint sessionId, uint sequence, uint ack, ulong timestampUs, ushort payloadLength)
    {
        dest[0] = Magic;
        dest[1] = Version;
        dest[2] = (byte)type;
        dest[3] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[12..], ack);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[16..], timestampUs);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[24..], payloadLength);
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out PacketHeader header)
    {
        header = default;
        if (buffer.Length < HeaderSize)
            return false;
        if (buffer[0] != Magic || buffer[1] != Version)
            return false;
        if (!Enum.IsDefined(typeof(PacketType), buffer[2]))
            return false;
        header = new PacketHeader(
            (PacketType)buffer[2],
            buffer[3],
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[24..]));
        return true;
    }

    public static bool TryDecodeClear(ReadOnlySpan<byte> buffer, out PacketHeader header, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (!TryReadHeader(buffer, out header) || header.Encrypted)
            return false;
        if (buffer.Length < HeaderSize + header.PayloadLength)
            return false;
        payload = buffer.Slice(HeaderSize, header.PayloadLength).ToArray();
        return true;
    }
}
