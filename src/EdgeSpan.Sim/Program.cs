using System.Net;
using System.Net.Sockets;
using EdgeSpan.Crypto;
using EdgeSpan.Net;
using EdgeSpan.Product;
using EdgeSpan.Session;

var host = Arg("host") ?? "127.0.0.1";
var port = int.TryParse(Arg("port"), out var parsed) ? parsed : ProductInfo.DefaultSessionPort;
var pin = Arg("pin") ?? "";
var name = Arg("name") ?? "EdgeSpan Sim";
if (pin.Length < 4)
{
    Console.Error.WriteLine("Usage: EdgeSpan.Sim --host 127.0.0.1 --port 45455 --pin 123456 --name Tablet");
    return 2;
}

using var udp = new UdpClient(0);
udp.EnableBroadcast = true;
var local = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
var node = new ClientNode(Guid.NewGuid().ToString("N"), name, 1920, 1080, () => Environment.TickCount64, new SystemRandomBytes());
var remote = new IPEndPoint(IPAddress.Parse(host), port);
var announce = DiscoveryCodec.Encode(new DiscoveryAnnounce(node.Id, name, 2, 0x7, (ushort)local, 1920, 1080));
udp.Send(announce, announce.Length, new IPEndPoint(IPAddress.Broadcast, ProductInfo.DiscoveryPort));
node.StartPairing(pin);
Flush();
Console.WriteLine($"pairing {name} -> {host}:{port}");
var started = Environment.TickCount64;
while (Environment.TickCount64 - started < 20000)
{
    if (udp.Available > 0)
    {
        var result = udp.Receive(ref remote);
        node.Ingest(result, Environment.TickCount64);
        Flush();
        if (node.HasControl)
            Console.WriteLine($"control {node.X},{node.Y}");
    }
    else
    {
        node.Tick(Environment.TickCount64);
        Flush();
        Thread.Sleep(10);
    }
    if (node.Established && node.Keys.Count > 0)
        Console.WriteLine(string.Join(" ", node.Keys));
}
return node.Established ? 0 : 1;

void Flush()
{
    foreach (var packet in node.Out)
        udp.Send(packet, packet.Length, remote);
    node.Out.Clear();
}

string? Arg(string key)
{
    var prefix = "--" + key;
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == prefix)
            return args[i + 1];
    return null;
}
