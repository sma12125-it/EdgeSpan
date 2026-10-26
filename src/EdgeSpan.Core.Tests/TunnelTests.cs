using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeSpan.Net;

namespace EdgeSpan.Tests;

public class TunnelTests
{
    [Fact]
    public void Frames_round_trip_across_a_split_read()
    {
        var first = DatagramFrames.Encode(new byte[] { 1, 2, 3, 4 });
        var second = DatagramFrames.Encode(Encoding.ASCII.GetBytes("edgestpan"));
        var reader = new FrameReader();
        reader.Append(first.AsSpan(0, 3));
        Assert.False(reader.TryTake(out _));
        reader.Append(first.AsSpan(3));
        reader.Append(second.AsSpan(0, 1));
        Assert.True(reader.TryTake(out var payload));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload);
        reader.Append(second.AsSpan(1));
        Assert.True(reader.TryTake(out var text));
        Assert.Equal("edgestpan", Encoding.ASCII.GetString(text));
        Assert.False(reader.TryTake(out _));
    }

    [Fact]
    public void Adb_text_keeps_ready_unauthorized_and_offline()
    {
        var text = """
            List of devices attached
            emulator-5554	device
            R58M unauthorized
            ABC123	offline
            
            * daemon started successfully
            """;
        var devices = AdbText.Parse(text);
        Assert.Equal(3, devices.Count);
        Assert.Equal(new AdbDevice("emulator-5554", "device"), devices[0]);
        Assert.Equal("unauthorized", devices[1].State);
        Assert.Equal("offline", devices[2].State);
        Assert.Empty(AdbText.Parse("List of devices attached\n"));
    }

    [Fact]
    public async Task Tcp_tunnel_returns_the_same_datagram()
    {
        using var tunnel = new TcpTunnel(0);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        tunnel.Received += (_, data) => received.TrySetResult(data);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tunnel.Port);
        client.NoDelay = true;
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        var frame = DatagramFrames.Encode(payload);
        await client.GetStream().WriteAsync(frame);
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(payload, got);
        var remote = (IPEndPoint)client.Client.LocalEndPoint!;
        Assert.True(tunnel.HasClient);
        Assert.True(tunnel.Owns(remote.Address.ToString(), remote.Port));
        var reply = new byte[] { 4, 4 };
        Assert.True(tunnel.TrySend(remote, reply));
        var back = new byte[16];
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var read = await client.GetStream().ReadAsync(back.AsMemory(0, 16), readCts.Token);
        var reader = new FrameReader();
        reader.Append(back.AsSpan(0, read));
        Assert.True(reader.TryTake(out var echoed));
        Assert.Equal(reply, echoed);
    }

    [Fact]
    public void Discovery_can_carry_a_pairing_pin()
    {
        var announce = new DiscoveryAnnounce("abc", "Office", 1, 0x1, 45455, 1920, 1080, "482913");
        Assert.True(DiscoveryCodec.TryDecode(DiscoveryCodec.Encode(announce), out var decoded));
        Assert.Equal("482913", decoded.Pin);
        Assert.Equal("Office", decoded.Name);
        var plain = new DiscoveryAnnounce("abc", "Office", 1, 0x1, 45455, 1920, 1080);
        Assert.True(DiscoveryCodec.TryDecode(DiscoveryCodec.Encode(plain), out var bare));
        Assert.Equal("", bare.Pin);
    }
}
