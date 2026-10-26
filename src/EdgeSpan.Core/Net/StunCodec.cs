using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace EdgeSpan.Net;

public static class StunCodec
{
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x21, 0x12, 0xA4, 0x42 };

    public static byte[] BindingRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != 12)
            throw new ArgumentException("STUN transaction id is 12 bytes.");
        var buf = new byte[20];
        buf[0] = 0x00;
        buf[1] = 0x01;
        Magic.CopyTo(buf.AsSpan(4));
        transactionId.CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static bool TryReadMapped(ReadOnlySpan<byte> message, ReadOnlySpan<byte> transactionId, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (message.Length < 20 || transactionId.Length != 12)
            return false;
        var type = BinaryPrimitives.ReadUInt16BigEndian(message);
        if (type != 0x0101)
            return false;
        if (!message.Slice(4, 4).SequenceEqual(Magic))
            return false;
        if (!message.Slice(8, 12).SequenceEqual(transactionId))
            return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        var end = Math.Min(message.Length, 20 + length);
        var o = 20;
        while (o + 4 <= end)
        {
            var attr = BinaryPrimitives.ReadUInt16BigEndian(message[o..]);
            var len = BinaryPrimitives.ReadUInt16BigEndian(message[(o + 2)..]);
            o += 4;
            if (o + len > end)
                return false;
            if (attr is 0x0020 or 0x0001 && TryAddress(message.Slice(o, len), attr == 0x0020, transactionId, out endpoint))
                return true;
            o += len + ((4 - (len % 4)) % 4);
        }
        return false;
    }

    private static bool TryAddress(ReadOnlySpan<byte> value, bool xor, ReadOnlySpan<byte> transactionId, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (value.Length < 4)
            return false;
        var family = value[1];
        var xport = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        var port = xor ? (ushort)(xport ^ 0x2112) : xport;
        if (family == 0x01 && value.Length >= 8)
        {
            var raw = value.Slice(4, 4).ToArray();
            if (xor)
            {
                for (var i = 0; i < 4; i++)
                    raw[i] ^= Magic[i];
            }
            endpoint = new IPEndPoint(new IPAddress(raw), port);
            return true;
        }
        if (family == 0x02 && value.Length >= 20)
        {
            var raw = value.Slice(4, 16).ToArray();
            if (xor)
            {
                var mask = new byte[16];
                Magic.CopyTo(mask);
                transactionId.CopyTo(mask.AsSpan(4));
                for (var i = 0; i < 16; i++)
                    raw[i] ^= mask[i];
            }
            endpoint = new IPEndPoint(new IPAddress(raw), port);
            return true;
        }
        return false;
    }
}

public static class StunQuery
{
    public static async Task<IPEndPoint?> BindingAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(0);
        var tx = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(tx);
        var request = StunCodec.BindingRequest(tx);
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (v4 == null)
            return null;
        var remote = new IPEndPoint(v4, port);
        await udp.SendAsync(request, remote, cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var result = await udp.ReceiveAsync(cts.Token);
            return StunCodec.TryReadMapped(result.Buffer, tx, out var ep) ? ep : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
