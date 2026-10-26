using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EdgeSpan.Net;

var role = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "phone";

string? Arg(string key)
{
    var prefix = "--" + key;
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == prefix)
            return args[i + 1];
    return null;
}

var relayUrl = Arg("relay") ?? "http://10.88.0.1:8787";
var stunHost = Arg("stun-host") ?? "10.88.0.1";
var stunPort = int.TryParse(Arg("stun-port"), out var parsedPort) ? parsedPort : 8788;
var expectMapped = Arg("expect-mapped");

Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
using var udp = new UdpClient(0);

async Task<UdpReceiveResult?> ReceiveFor(int milliseconds)
{
    using var cts = new CancellationTokenSource(milliseconds);
    try
    {
        return await udp.ReceiveAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        return null;
    }
}
var transaction = new byte[12];
System.Security.Cryptography.RandomNumberGenerator.Fill(transaction);
var stunAddress = (await Dns.GetHostAddressesAsync(stunHost)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
await udp.SendAsync(StunCodec.BindingRequest(transaction), new IPEndPoint(stunAddress, stunPort));
IPEndPoint? mapped = null;
var stunUntil = Environment.TickCount64 + 2000;
while (Environment.TickCount64 < stunUntil && mapped == null)
{
    var got = await ReceiveFor(200);
    if (got != null && StunCodec.TryReadMapped(got.Value.Buffer, transaction, out var endpoint))
        mapped = endpoint;
}
if (mapped == null)
{
    Console.Error.WriteLine("stun-failed");
    return 2;
}
Console.WriteLine($"mapped {mapped.Address}:{mapped.Port}");
if (!string.IsNullOrWhiteSpace(expectMapped) && mapped.Address.ToString() != expectMapped)
{
    Console.Error.WriteLine($"mapped-mismatch {mapped.Address} != {expectMapped}");
    return 2;
}

var signals = new ConcurrentQueue<(string From, string Data)>();
var peerLists = new ConcurrentQueue<List<SignalPeer>>();
var payloads = new ConcurrentQueue<byte[]>();
await using var client = new RelayClient();
client.MessageReceived += message =>
{
    if (message.Op == "signal" && !string.IsNullOrWhiteSpace(message.Data))
        signals.Enqueue((message.From ?? "", message.Data));
    if (message.Op == "peers" && message.Peers != null)
        peerLists.Enqueue(message.Peers);
};
client.PayloadReceived += (_, payload) => payloads.Enqueue(payload);
await client.ConnectAsync(relayUrl, role, role == "host" ? "host" : "client", CancellationToken.None);
Console.WriteLine($"peer {client.PeerId}");

var ownAnnounce = Announce(role, udp, mapped);
string other;
string? remoteAnnounce;
if (role == "host")
{
    other = "";
    remoteAnnounce = null;
    var wait = Environment.TickCount64 + 12000;
    while (remoteAnnounce == null && Environment.TickCount64 < wait)
    {
        while (signals.TryDequeue(out var signal))
        {
            if (!signal.Data.Contains("announce", StringComparison.Ordinal))
                continue;
            other = signal.From;
            remoteAnnounce = signal.Data;
            await client.SendSignalAsync(other, ownAnnounce, CancellationToken.None);
            break;
        }
        if (remoteAnnounce == null)
            await Task.Delay(40);
    }
}
else
{
    other = Arg("peer") ?? "";
    var deadline = Environment.TickCount64 + 12000;
    while (other.Length == 0 && Environment.TickCount64 < deadline)
    {
        await client.ListAsync(CancellationToken.None);
        await Task.Delay(200);
        while (peerLists.TryDequeue(out var list))
        {
            var host = list.FirstOrDefault(peer => peer.Role == "host" && peer.Id.Length > 0);
            if (host != null)
                other = host.Id;
        }
    }
    if (other.Length == 0)
    {
        Console.Error.WriteLine("no-host");
        return 2;
    }
    await client.SendSignalAsync(other, ownAnnounce, CancellationToken.None);
    remoteAnnounce = null;
    var wait = Environment.TickCount64 + 8000;
    while (remoteAnnounce == null && Environment.TickCount64 < wait)
    {
        while (signals.TryDequeue(out var signal))
        {
            if (signal.Data.Contains("announce", StringComparison.Ordinal))
                remoteAnnounce = signal.Data;
        }
        if (remoteAnnounce == null)
            await Task.Delay(40);
    }
}

if (string.IsNullOrWhiteSpace(other) || remoteAnnounce == null)
{
    Console.Error.WriteLine("announce-failed");
    return 2;
}

var targets = new List<IPEndPoint>();
var peerLan = Endpoint(remoteAnnounce, "lan");
var peerMapped = Endpoint(remoteAnnounce, "mapped");
if (peerLan != null)
    targets.Add(peerLan);
if (peerMapped != null)
    targets.Add(peerMapped);
Console.WriteLine("targets " + string.Join(" ", targets));

var probe = "ESPN"u8.ToArray();
var marker = Encoding.ASCII.GetBytes("edgespan-nat-" + role);
var relayMarker = Encoding.ASCII.GetBytes("edgespan-relay-" + role);
IPEndPoint? directFrom = null;
var directOk = false;
var relayOk = false;
var rx = 0;
var punchUntil = Environment.TickCount64 + 5000;
while (Environment.TickCount64 < punchUntil && (!directOk || !relayOk))
{
    foreach (var target in targets)
    {
        try { await udp.SendAsync(probe, target); }
        catch (SocketException) { }
    }
    if (directFrom != null)
    {
        try { await udp.SendAsync(marker, directFrom); }
        catch (SocketException) { }
    }
    try
    {
        await client.SendPayloadAsync(other, relayMarker, CancellationToken.None);
    }
    catch (Exception) { }
    var got = await ReceiveFor(200);
    if (got != null)
    {
        rx++;
        if (rx <= 4)
            Console.WriteLine($"rx {got.Value.RemoteEndPoint} {Encoding.ASCII.GetString(got.Value.Buffer)}");
        if (got.Value.Buffer.Length >= 4 && got.Value.Buffer.AsSpan(0, 4).SequenceEqual(probe))
            directFrom = got.Value.RemoteEndPoint;
        var text = Encoding.ASCII.GetString(got.Value.Buffer);
        if (text.StartsWith("edgespan-nat-", StringComparison.Ordinal) && !text.EndsWith(role, StringComparison.Ordinal))
            directOk = true;
    }
    while (payloads.TryDequeue(out var payload))
    {
        var text = Encoding.ASCII.GetString(payload);
        if (text.StartsWith("edgespan-relay-", StringComparison.Ordinal) && !text.EndsWith(role, StringComparison.Ordinal))
            relayOk = true;
    }
}

Console.WriteLine($"rx-total {rx}");
Console.WriteLine(directOk ? "path direct" : "path relay");
if (directOk)
    Console.WriteLine("direct-ok");
if (relayOk)
    Console.WriteLine("relay-ok");
if (directOk && relayOk)
    return 0;
return 3;

static string Announce(string role, UdpClient udp, IPEndPoint mapped)
{
    var local = (IPEndPoint)udp.Client.LocalEndPoint!;
    var lan = "127.0.0.1";
    foreach (var (address, _) in NetAddresses.LocalIPv4())
    {
        if (!IPAddress.IsLoopback(address))
        {
            lan = address.ToString();
            break;
        }
    }
    return JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["t"] = "announce",
        ["id"] = role,
        ["lan"] = lan + ":" + local.Port,
        ["mapped"] = mapped.Address + ":" + mapped.Port
    });
}

static IPEndPoint? Endpoint(string json, string name)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(name, out var element))
            return null;
        var text = element.GetString() ?? "";
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !IPAddress.TryParse(text[..colon], out var ip) || !int.TryParse(text[(colon + 1)..], out var port))
            return null;
        return new IPEndPoint(ip, port);
    }
    catch (JsonException)
    {
        return null;
    }
}
