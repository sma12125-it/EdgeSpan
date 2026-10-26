using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EdgeSpan.Net;

public readonly record struct DiscoveryAnnounce(
    string Id,
    string Name,
    byte Role,
    byte Capabilities,
    ushort Port,
    ushort ScreenW,
    ushort ScreenH,
    string Pin = "");

public static class DiscoveryCodec
{
    public const byte Magic0 = 0xE5;
    public const byte Magic1 = 0x44;

    public static byte[] Encode(DiscoveryAnnounce announce)
    {
        var id = Encoding.UTF8.GetBytes(Trim(announce.Id, 32));
        var name = Encoding.UTF8.GetBytes(Trim(announce.Name, 96));
        var pin = PinBytes(announce.Pin);
        var buf = new byte[11 + 1 + id.Length + 1 + name.Length + (pin == null ? 0 : 1 + pin.Length)];
        buf[0] = Magic0;
        buf[1] = Magic1;
        buf[2] = 1;
        buf[3] = announce.Role;
        buf[4] = announce.Capabilities;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5), announce.Port);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(7), announce.ScreenW);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(9), announce.ScreenH);
        var o = 11;
        buf[o++] = (byte)id.Length;
        id.CopyTo(buf.AsSpan(o));
        o += id.Length;
        buf[o++] = (byte)name.Length;
        name.CopyTo(buf.AsSpan(o));
        o += name.Length;
        if (pin != null)
        {
            buf[o++] = (byte)pin.Length;
            pin.CopyTo(buf.AsSpan(o));
        }
        return buf;
    }

    public static bool TryDecode(ReadOnlySpan<byte> buffer, out DiscoveryAnnounce announce)
    {
        announce = default;
        if (buffer.Length < 13 || buffer[0] != Magic0 || buffer[1] != Magic1 || buffer[2] != 1)
            return false;
        var role = buffer[3];
        var caps = buffer[4];
        var port = BinaryPrimitives.ReadUInt16LittleEndian(buffer[5..]);
        var w = BinaryPrimitives.ReadUInt16LittleEndian(buffer[7..]);
        var h = BinaryPrimitives.ReadUInt16LittleEndian(buffer[9..]);
        var o = 11;
        if (!Read(buffer, ref o, out var id) || !Read(buffer, ref o, out var name))
            return false;
        var pin = "";
        if (o < buffer.Length)
        {
            var n = buffer[o];
            if (n is >= 4 and <= 8 && o + 1 + n <= buffer.Length)
            {
                var text = Encoding.ASCII.GetString(buffer.Slice(o + 1, n));
                if (text.All(static c => c is >= '0' and <= '9'))
                    pin = text;
            }
        }
        announce = new DiscoveryAnnounce(id, name, role, caps, port, w, h, pin);
        return true;
    }

    private static byte[]? PinBytes(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < 4 or > 8)
            return null;
        foreach (var c in pin)
        {
            if (c is < '0' or > '9')
                return null;
        }
        return Encoding.ASCII.GetBytes(pin);
    }

    private static bool Read(ReadOnlySpan<byte> buffer, ref int o, out string value)
    {
        value = "";
        if (o >= buffer.Length)
            return false;
        var n = buffer[o++];
        if (o + n > buffer.Length)
            return false;
        value = Encoding.UTF8.GetString(buffer.Slice(o, n));
        o += n;
        return true;
    }

    private static string Trim(string? value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (bytes.Length <= maxBytes)
            return value ?? "";
        var len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }
}

public static class NetAddresses
{
    public static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (ip.Length != 4 || m.Length != 4)
            throw new ArgumentException("Broadcast is calculated for IPv4.");
        var b = new byte[4];
        for (var i = 0; i < 4; i++)
            b[i] = (byte)(ip[i] | ~m[i]);
        return new IPAddress(b);
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127)
            return true;
        if (b[0] == 192 && b[1] == 168)
            return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            return true;
        return false;
    }

    public static IEnumerable<(IPAddress Address, IPAddress Broadcast)> LocalIPv4()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;
            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(uni.Address))
                    continue;
                var mask = uni.IPv4Mask;
                if (mask == null)
                    continue;
                IPAddress broadcast;
                try { broadcast = Broadcast(uni.Address, mask); }
                catch (ArgumentException) { continue; }
                yield return (uni.Address, broadcast);
            }
        }
    }

    public static string GuessLanHost()
    {
        foreach (var (address, _) in LocalIPv4())
        {
            if (IsPrivate(address))
                return address.ToString();
        }
        return "127.0.0.1";
    }
}

public static class MdnsPacket
{
    public static byte[] Announce(string instance, ushort port, IPAddress ipv4, string displayName)
    {
        var service = new[] { "_edgestpan", "_udp", "local" };
        var host = Sanitize(instance);
        var instanceName = new[] { host, "_edgestpan", "_udp", "local" };
        var hostName = new[] { host, "local" };
        using var ms = new MemoryStream();
        void U16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        void Name(string[] labels)
        {
            foreach (var label in labels)
            {
                var raw = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)raw.Length);
                ms.Write(raw);
            }
            ms.WriteByte(0);
        }
        U16(0);
        U16(0x8400);
        U16(0);
        U16(4);
        U16(0);
        U16(0);
        Name(service);
        U16(12);
        U16(1);
        U32(120);
        var ptr = NameBytes(instanceName);
        U16((ushort)ptr.Length);
        ms.Write(ptr);
        Name(instanceName);
        U16(33);
        U16(0x8001);
        U32(120);
        var target = NameBytes(hostName);
        U16((ushort)(6 + target.Length));
        U16(0);
        U16(0);
        U16(port);
        ms.Write(target);
        Name(instanceName);
        U16(16);
        U16(0x8001);
        U32(120);
        var txt = Encoding.UTF8.GetBytes("name=" + displayName);
        if (txt.Length > 200)
            txt = txt[..200];
        U16((ushort)(txt.Length + 1));
        ms.WriteByte((byte)txt.Length);
        ms.Write(txt);
        Name(hostName);
        U16(1);
        U16(1);
        U32(120);
        U16(4);
        ms.Write(ipv4.GetAddressBytes());
        return ms.ToArray();
    }

    private static byte[] NameBytes(string[] labels)
    {
        using var ms = new MemoryStream();
        foreach (var label in labels)
        {
            var raw = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)raw.Length);
            ms.Write(raw);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static string Sanitize(string instance)
    {
        var chars = (instance ?? "pc").Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var text = new string(chars).Trim('-');
        return text.Length == 0 ? "pc" : text[..Math.Min(40, text.Length)];
    }
}
