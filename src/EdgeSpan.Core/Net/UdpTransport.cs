using System.Net;
using System.Net.Sockets;
using EdgeSpan.Product;

namespace EdgeSpan.Net;

public sealed class UdpTransport : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly SendQueue _tx;

    public int Port { get; }
    public event Action<IPEndPoint, byte[]>? Received;

    public UdpTransport(int port)
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _udp.EnableBroadcast = true;
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _tx = new SendQueue(SendNow);
        _loop = Task.Run(Loop);
    }

    public void Send(IPEndPoint to, ReadOnlySpan<byte> data) => _tx.Enqueue(to, data.ToArray());

    public void Dispose()
    {
        _cts.Cancel();
        _tx.Dispose();
        _udp.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }

    private void SendNow(IPEndPoint to, byte[] data) => _udp.Send(data, data.Length, to);

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                Received?.Invoke(result.RemoteEndPoint, result.Buffer);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }
}

public sealed class DiscoveryAgent : IDisposable
{
    private readonly UdpTransport _transport;
    public event Action<DiscoveryAnnounce, IPEndPoint>? Discovered;

    public DiscoveryAgent()
    {
        _transport = new UdpTransport(ProductInfo.DiscoveryPort);
        _transport.Received += OnReceived;
    }

    public void Announce(byte[] packet, IEnumerable<IPEndPoint> targets)
    {
        foreach (var target in targets)
            _transport.Send(target, packet);
    }

    public void Dispose() => _transport.Dispose();

    private void OnReceived(IPEndPoint from, byte[] data)
    {
        if (DiscoveryCodec.TryDecode(data, out var announce))
            Discovered?.Invoke(announce, from);
    }
}
