using System.Buffers.Binary;

namespace EdgeSpan.Input;

/// <summary>
/// Windows RAWMOUSE is not a packed sequence of ushorts.
/// usFlags is at 0, then two bytes of alignment padding, then usButtonFlags at 4.
/// Reading buttons from offset 2 returns that padding and drops every click and wheel tick.
/// </summary>
public static class RawMouseLayout
{
    public const int ButtonFlagsOffset = 4;
    public const int ButtonDataOffset = 6;
    public const int LastXOffset = 12;
    public const int LastYOffset = 16;

    public static bool TryRead(ReadOnlySpan<byte> raw, out int dx, out int dy, out ushort buttonFlags, out short buttonData)
    {
        dx = 0;
        dy = 0;
        buttonFlags = 0;
        buttonData = 0;
        if (raw.Length < LastYOffset + 4)
            return false;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        buttonFlags = BinaryPrimitives.ReadUInt16LittleEndian(raw[ButtonFlagsOffset..]);
        buttonData = BinaryPrimitives.ReadInt16LittleEndian(raw[ButtonDataOffset..]);
        if ((flags & 0x01) != 0)
            return true;
        dx = BinaryPrimitives.ReadInt32LittleEndian(raw[LastXOffset..]);
        dy = BinaryPrimitives.ReadInt32LittleEndian(raw[LastYOffset..]);
        return true;
    }
}
