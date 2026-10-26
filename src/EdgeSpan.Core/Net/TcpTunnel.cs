using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace EdgeSpan.Net;

/// <summary>Accepts TCP clients and exchanges the same datagrams the session uses on UDP.</summary>
public sealed class TcpTunnel : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Client> _clients = new();

    public int Port { get; }
    public event Action<IPEndPoint, byte[]>? Received;

    public TcpTunnel(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public bool HasClient
    {
        get { lock (_gate) return _clients.Count > 0; }
    }

    public bool Owns(string address, int port)
    {
        if (port <= 0 || !IPAddress.TryParse(address, out var ip))
            return false;
        lock (_gate)
            return _clients.ContainsKey(Key(new IPEndPoint(ip, port)));
    }

    public bool TrySend(IPEndPoint endpoint, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > DatagramFrames.MaxPayload)
            return false;
        Client? client;
        lock (_gate)
            _clients.TryGetValue(Key(endpoint), out client);
        if (client == null)
            return false;
        return client.TryWrite(DatagramFrames.Encode(payload));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        List<Client> clients;
        lock (_gate)
        {
            clients = _clients.Values.ToList();
            _clients.Clear();
        }
        foreach (var client in clients)
            client.Dispose();
        _cts.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { break; }
            tcp.NoDelay = true;
            var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
            var client = new Client(tcp, remote, this);
            lock (_gate)
                _clients[Key(remote)] = client;
            _ = client.ReadLoop(_cts.Token);
        }
    }

    private void Raise(IPEndPoint from, byte[] payload) => Received?.Invoke(from, payload);

    private void Remove(IPEndPoint remote)
    {
        lock (_gate)
            _clients.Remove(Key(remote));
    }

    private static string Key(IPEndPoint endpoint)
    {
        var ip = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        return ip + ":" + endpoint.Port;
    }

    private sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly IPEndPoint _remote;
        private readonly TcpTunnel _owner;
        private readonly object _write = new();

        public Client(TcpClient tcp, IPEndPoint remote, TcpTunnel owner)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _remote = remote;
            _owner = owner;
        }

        public bool TryWrite(byte[] frame)
        {
            lock (_write)
            {
                try
                {
                    _stream.Write(frame);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public async Task ReadLoop(CancellationToken token)
        {
            var buf = new byte[8192];
            var reader = new FrameReader();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var n = await _stream.ReadAsync(buf, token);
                    if (n <= 0)
                        break;
                    reader.Append(buf.AsSpan(0, n));
                    while (reader.TryTake(out var payload))
                        _owner.Raise(_remote, payload);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                _owner.Remove(_remote);
                Dispose();
            }
        }

        public void Dispose()
        {
            try { _tcp.Dispose(); } catch (Exception) { }
        }
    }
}
