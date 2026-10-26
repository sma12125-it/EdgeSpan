using System.Buffers.Binary;
using System.Text;

namespace EdgeSpan.Protocol;

/// <summary>
/// Android-to-Windows app stream on TCP ProductInfo.AppStreamPort (27186).
/// The phone dials the PC and sends a plaintext hello; everything after it is MediaFrames-sealed:
/// video phone to PC with the phone's media send key, input PC to phone with the PC's media send key.
/// Each side announces a random starting sequence so a reconnect never reuses a nonce.
/// </summary>
public static class AppStreamWire
{
    public static readonly byte[] Magic = "ESA1"u8.ToArray();

    public readonly record struct Hello(string DeviceId, ushort Width, ushort Height, ulong StartSeq, string Title, string Package);

    public const byte InputMove = 1, InputButton = 2, InputWheel = 3, InputKey = 4, InputText = 5, InputClose = 6, InputBack = 7, InputIdr = 8;

    public readonly record struct Input(byte Kind, int X, int Y, byte Button, bool Down, int Wheel, int Vk, int Hid, string Text);

    public static byte[] WriteHello(Hello h)
    {
        var id = Encoding.UTF8.GetBytes(h.DeviceId);
        var title = Encoding.UTF8.GetBytes(h.Title);
        var pkg = Encoding.UTF8.GetBytes(h.Package);
        var body = new byte[4 + 1 + id.Length + 2 + 2 + 8 + 2 + title.Length + 2 + pkg.Length];
        var o = 0;
        Magic.CopyTo(body, 0); o += 4;
        body[o++] = (byte)id.Length; id.CopyTo(body, o); o += id.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), h.Width); o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), h.Height); o += 2;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(o), h.StartSeq); o += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), (ushort)title.Length); o += 2;
        title.CopyTo(body, o); o += title.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), (ushort)pkg.Length); o += 2;
        pkg.CopyTo(body, o);
        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, body.Length);
        body.CopyTo(framed, 4);
        return framed;
    }

    /// <summary>Parses a hello body (without its u32 length prefix).</summary>
    public static bool TryHello(ReadOnlySpan<byte> b, out Hello hello)
    {
        hello = default;
        try
        {
            if (b.Length < 4 + 1 + 2 + 2 + 8 + 2 + 2 || !b[..4].SequenceEqual(Magic)) return false;
            var o = 4;
            int idLen = b[o++];
            var id = Encoding.UTF8.GetString(b.Slice(o, idLen)); o += idLen;
            var w = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]); o += 2;
            var h = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]); o += 2;
            var seq = BinaryPrimitives.ReadUInt64LittleEndian(b[o..]); o += 8;
            int tl = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]); o += 2;
            var title = Encoding.UTF8.GetString(b.Slice(o, tl)); o += tl;
            int pl = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]); o += 2;
            var pkg = Encoding.UTF8.GetString(b.Slice(o, pl));
            if (id.Length == 0 || w == 0 || h == 0) return false;
            hello = new Hello(id, w, h, seq, title, pkg);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>PC answer to the hello: magic, accepted flag, PC's starting input sequence.</summary>
    public static byte[] WriteReply(bool accepted, ulong startSeq)
    {
        var b = new byte[4 + 4 + 1 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(b, 13);
        Magic.CopyTo(b, 4);
        b[8] = (byte)(accepted ? 1 : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(9), startSeq);
        return b;
    }

    public static byte[] WriteInput(Input i)
    {
        var text = Encoding.UTF8.GetBytes(i.Text ?? "");
        var b = new byte[1 + 4 + 4 + 1 + 1 + 2 + 2 + 2 + 2 + text.Length];
        b[0] = i.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), i.X);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), i.Y);
        b[9] = i.Button;
        b[10] = (byte)(i.Down ? 1 : 0);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(11), (short)Math.Clamp(i.Wheel, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), (ushort)i.Vk);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(15), (ushort)i.Hid);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(17), (ushort)text.Length);
        text.CopyTo(b, 19);
        return b;
    }

    public static bool TryInput(ReadOnlySpan<byte> b, out Input input)
    {
        input = default;
        if (b.Length < 19) return false;
        int tl = BinaryPrimitives.ReadUInt16LittleEndian(b[17..]);
        if (b.Length < 19 + tl) return false;
        input = new Input(b[0], BinaryPrimitives.ReadInt32LittleEndian(b[1..]), BinaryPrimitives.ReadInt32LittleEndian(b[5..]), b[9], b[10] != 0,
            BinaryPrimitives.ReadInt16LittleEndian(b[11..]), BinaryPrimitives.ReadUInt16LittleEndian(b[13..]), BinaryPrimitives.ReadUInt16LittleEndian(b[15..]),
            Encoding.UTF8.GetString(b.Slice(19, tl)));
        return true;
    }
}
