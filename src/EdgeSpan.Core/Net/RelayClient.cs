using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using EdgeSpan.Product;

namespace EdgeSpan.Net;

public sealed class RelayClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private ClientWebSocket? _ws;
    private UdpClient? _udp;
    private IPEndPoint? _serverUdp;
    private CancellationTokenSource? _cts;
    private Task? _wsLoop;
    private Task? _udpLoop;

    public string? PeerId { get; private set; }
    public byte[]? Token { get; private set; }
    public int LocalUdpPort { get; private set; }
    public int RemoteUdpPort { get; private set; }
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action<SignalMessage>? MessageReceived;
    public event Action<string, byte[]>? PayloadReceived;
    public event Action<string>? Failed;

    public async Task ConnectAsync(string httpUrl, string name, string role, CancellationToken cancellationToken)
    {
        await DisposeAsync();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var uri = SignalJson.ToWebSocket(httpUrl);
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(uri, _cts.Token);
        await SendRawAsync(SignalJson.Hello(name, role), _cts.Token);
        var welcomeText = await ReceiveTextAsync(_cts.Token);
        if (!SignalJson.TryParse(welcomeText, out var welcome) || welcome.Op != "welcome" || string.IsNullOrWhiteSpace(welcome.PeerId) || string.IsNullOrWhiteSpace(welcome.Token))
            throw new InvalidOperationException("Relay registration was rejected.");
        PeerId = welcome.PeerId;
        Token = Convert.FromBase64String(welcome.Token);
        var udpPort = welcome.UdpPort ?? ProductInfo.RelayUdpPort;
        RemoteUdpPort = udpPort;
        var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, _cts.Token);
        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException("Relay host has no IPv4 address.");
        _serverUdp = new IPEndPoint(v4, udpPort);
        _udp = new UdpClient(0);
        LocalUdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        await _udp.SendAsync(RelayFrame.Bind(Token, PeerId), _serverUdp, _cts.Token);
        _wsLoop = Task.Run(() => ReadSocketAsync(_cts.Token));
        _udpLoop = Task.Run(() => ReadUdpAsync(_cts.Token));
    }

    public async Task SendSignalAsync(string toPeerId, string data, CancellationToken cancellationToken) =>
        await SendRawAsync(SignalJson.Signal(toPeerId, data), cancellationToken);

    public async Task ListAsync(CancellationToken cancellationToken) =>
        await SendRawAsync(SignalJson.List(), cancellationToken);

    public async Task SendPayloadAsync(string destinationPeerId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_udp == null || _serverUdp == null || Token == null)
            throw new InvalidOperationException("Relay is not connected.");
        var frame = RelayFrame.Data(Token, destinationPeerId, payload.Span);
        await _udp.SendAsync(frame, _serverUdp, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch (Exception) { }
        }
        _ws?.Dispose();
        _udp?.Dispose();
        if (_wsLoop != null)
            await Observe(_wsLoop);
        if (_udpLoop != null)
            await Observe(_udpLoop);
        _cts?.Dispose();
        _ws = null;
        _udp = null;
        _cts = null;
        PeerId = null;
        Token = null;
    }

    private async Task SendRawAsync(string json, CancellationToken cancellationToken)
    {
        if (_ws == null)
            throw new InvalidOperationException("Relay is not connected.");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _send.WaitAsync(cancellationToken);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        if (_ws == null)
            throw new InvalidOperationException("Relay is not connected.");
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Relay closed the signaling connection.");
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task ReadSocketAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _ws != null)
            {
                var text = await ReceiveTextAsync(cancellationToken);
                if (SignalJson.TryParse(text, out var message))
                    MessageReceived?.Invoke(message);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            if (!cancellationToken.IsCancellationRequested)
                Failed?.Invoke(ex.Message);
        }
    }

    private async Task ReadUdpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _udp != null)
            {
                var result = await _udp.ReceiveAsync(cancellationToken);
                if (!RelayFrame.TryParse(result.Buffer, out var op, out _, out var peer, out var payload))
                    continue;
                if (op == RelayOp.Deliver)
                    PayloadReceived?.Invoke(peer, payload);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!cancellationToken.IsCancellationRequested)
                Failed?.Invoke(ex.Message);
        }
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (Exception) { }
    }
}
