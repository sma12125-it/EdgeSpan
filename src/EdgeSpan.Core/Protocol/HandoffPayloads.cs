using System.Buffers.Binary;
using System.Text;

namespace EdgeSpan.Protocol;

public static class HandoffPayloads
{
    public const byte PcToPhone = 1;
    public const byte PhoneToPc = 2;
    public const byte CodecH264 = 1;
    public const byte CodecHevc = 2;

    public readonly record struct Offer(uint Id, byte Direction, uint Dpi, ushort PhysW, ushort PhysH, ushort DipW, ushort DipH, byte Codec, byte Edge, string Title);
    public readonly record struct Accept(uint Id, byte Codec, ushort ViewW, ushort ViewH);
    public readonly record struct Geometry(uint Id, int X, int Y, ushort PhysW, ushort PhysH, ushort DipW, ushort DipH);
    public readonly record struct Input(uint Id, byte Kind, int X, int Y, byte Button, bool Down, short WheelX, short WheelY, ushort Vk, string Text);
    public readonly record struct State(uint Id, byte Value);
    public readonly record struct VideoConfig(uint Id, uint Bitrate, byte Fps, bool Idr);

    public const byte InputMove = 1;
    public const byte InputButton = 2;
    public const byte InputWheel = 3;
    public const byte InputKey = 4;
    public const byte InputText = 5;
    /// <summary>Button field of a wheel input: hold Ctrl (pinch zoom).</summary>
    public const byte WheelCtrl = 8;

    /// <summary>A VideoConfig with fps 0 and a non-zero bitrate field is a receiver stats report.</summary>
    public static bool IsStats(VideoConfig c) => c.Fps == 0 && c.Bitrate != 0 && !c.Idr;
    public static int StatsDelayMs(VideoConfig c) => (int)(c.Bitrate & 0xFFFF);
    public static int StatsDropped(VideoConfig c) => (int)((c.Bitrate >> 16) & 0x7FFF);
    public const byte StateFocused = 1;
    public const byte StateMinimized = 2;
    public const byte StateRestored = 3;
    public const byte StateClosed = 4;

    public static byte[] WriteOffer(Offer offer)
    {
        var title = Encoding.UTF8.GetBytes(offer.Title ?? "");
        if (title.Length > 180) title = title[..180];
        var buf = new byte[4 + 1 + 4 + 2 + 2 + 2 + 2 + 1 + 1 + 1 + title.Length];
        var o = 0;
        WriteU32(buf, ref o, offer.Id);
        buf[o++] = offer.Direction;
        WriteU32(buf, ref o, offer.Dpi);
        WriteU16(buf, ref o, offer.PhysW);
        WriteU16(buf, ref o, offer.PhysH);
        WriteU16(buf, ref o, offer.DipW);
        WriteU16(buf, ref o, offer.DipH);
        buf[o++] = offer.Codec;
        buf[o++] = offer.Edge;
        buf[o++] = (byte)title.Length;
        title.CopyTo(buf.AsSpan(o));
        return buf;
    }

    public static bool TryOffer(ReadOnlySpan<byte> payload, out Offer offer)
    {
        offer = default;
        if (payload.Length < 20) return false;
        var id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var direction = payload[4];
        var dpi = BinaryPrimitives.ReadUInt32LittleEndian(payload[5..]);
        var physW = BinaryPrimitives.ReadUInt16LittleEndian(payload[9..]);
        var physH = BinaryPrimitives.ReadUInt16LittleEndian(payload[11..]);
        var dipW = BinaryPrimitives.ReadUInt16LittleEndian(payload[13..]);
        var dipH = BinaryPrimitives.ReadUInt16LittleEndian(payload[15..]);
        var codec = payload[17];
        var edge = payload[18];
        var n = payload[19];
        if (20 + n > payload.Length) return false;
        var title = Encoding.UTF8.GetString(payload.Slice(20, n));
        offer = new Offer(id, direction, dpi, physW, physH, dipW, dipH, codec, edge, title);
        return true;
    }

    public static byte[] WriteAccept(Accept accept)
    {
        var buf = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, accept.Id);
        buf[4] = accept.Codec;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5), accept.ViewW);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(7), accept.ViewH);
        return buf;
    }

    public static bool TryAccept(ReadOnlySpan<byte> payload, out Accept accept)
    {
        accept = default;
        if (payload.Length < 9) return false;
        accept = new Accept(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            payload[4],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[5..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[7..]));
        return true;
    }

    public static byte[] WriteGeometry(Geometry geometry)
    {
        var buf = new byte[4 + 4 + 4 + 8];
        var o = 0;
        WriteU32(buf, ref o, geometry.Id);
        WriteI32(buf, ref o, geometry.X);
        WriteI32(buf, ref o, geometry.Y);
        WriteU16(buf, ref o, geometry.PhysW);
        WriteU16(buf, ref o, geometry.PhysH);
        WriteU16(buf, ref o, geometry.DipW);
        WriteU16(buf, ref o, geometry.DipH);
        return buf;
    }

    public static bool TryGeometry(ReadOnlySpan<byte> payload, out Geometry geometry)
    {
        geometry = default;
        if (payload.Length < 20) return false;
        geometry = new Geometry(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]));
        return true;
    }

    public static byte[] WriteInput(Input input)
    {
        var text = Encoding.UTF8.GetBytes(input.Text ?? "");
        if (text.Length > 400) text = text[..400];
        var buf = new byte[4 + 1 + 4 + 4 + 1 + 1 + 2 + 2 + 2 + 2 + text.Length];
        var o = 0;
        WriteU32(buf, ref o, input.Id);
        buf[o++] = input.Kind;
        WriteI32(buf, ref o, input.X);
        WriteI32(buf, ref o, input.Y);
        buf[o++] = input.Button;
        buf[o++] = input.Down ? (byte)1 : (byte)0;
        WriteI16(buf, ref o, input.WheelX);
        WriteI16(buf, ref o, input.WheelY);
        WriteU16(buf, ref o, input.Vk);
        WriteU16(buf, ref o, (ushort)text.Length);
        text.CopyTo(buf.AsSpan(o));
        return buf;
    }

    public static bool TryInput(ReadOnlySpan<byte> payload, out Input input)
    {
        input = default;
        if (payload.Length < 23) return false;
        var textLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[21..]);
        if (23 + textLen > payload.Length) return false;
        input = new Input(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            payload[4],
            BinaryPrimitives.ReadInt32LittleEndian(payload[5..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[9..]),
            payload[13],
            payload[14] != 0,
            BinaryPrimitives.ReadInt16LittleEndian(payload[15..]),
            BinaryPrimitives.ReadInt16LittleEndian(payload[17..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[19..]),
            Encoding.UTF8.GetString(payload.Slice(23, textLen)));
        return true;
    }

    public static byte[] WriteState(State state)
    {
        var buf = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, state.Id);
        buf[4] = state.Value;
        return buf;
    }

    public static bool TryState(ReadOnlySpan<byte> payload, out State state)
    {
        state = default;
        if (payload.Length < 5) return false;
        state = new State(BinaryPrimitives.ReadUInt32LittleEndian(payload), payload[4]);
        return true;
    }

    public static byte[] WriteReturn(uint id)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, id);
        return buf;
    }

    public static bool TryReturn(ReadOnlySpan<byte> payload, out uint id)
    {
        id = 0;
        if (payload.Length < 4) return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return true;
    }

    public static byte[] WriteVideoConfig(VideoConfig config)
    {
        var buf = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, config.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), config.Bitrate);
        buf[8] = config.Fps;
        buf[9] = config.Idr ? (byte)1 : (byte)0;
        return buf;
    }

    public static bool TryVideoConfig(ReadOnlySpan<byte> payload, out VideoConfig config)
    {
        config = default;
        if (payload.Length < 10) return false;
        config = new VideoConfig(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            payload[8],
            payload[9] != 0);
        return true;
    }

    public static byte[] WriteClock(uint id, uint frameId, ulong captureUs)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), frameId);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8), captureUs);
        return buf;
    }

    public static bool TryClock(ReadOnlySpan<byte> payload, out uint id, out uint frameId, out ulong captureUs)
    {
        id = frameId = 0;
        captureUs = 0;
        if (payload.Length < 16) return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        frameId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        captureUs = BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]);
        return true;
    }

    private static void WriteU16(byte[] buf, ref int o, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), value);
        o += 2;
    }

    private static void WriteU32(byte[] buf, ref int o, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), value);
        o += 4;
    }

    private static void WriteI16(byte[] buf, ref int o, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), value);
        o += 2;
    }

    private static void WriteI32(byte[] buf, ref int o, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), value);
        o += 4;
    }
}
