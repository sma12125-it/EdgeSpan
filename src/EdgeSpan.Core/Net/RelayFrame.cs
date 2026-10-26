using System.Text;
using System.Text.Json;

namespace EdgeSpan.Net;

public enum RelayOp : byte
{
    Bind = 1,
    Data = 2,
    Deliver = 3
}

public static class RelayFrame
{
    public const byte Magic0 = 0xE5;
    public const byte Magic1 = 0x52;

    public static byte[] Bind(ReadOnlySpan<byte> token, string peerId) =>
        Encode(RelayOp.Bind, token, peerId, ReadOnlySpan<byte>.Empty);

    public static byte[] Data(ReadOnlySpan<byte> token, string destinationId, ReadOnlySpan<byte> payload) =>
        Encode(RelayOp.Data, token, destinationId, payload);

    public static byte[] Deliver(string sourceId, ReadOnlySpan<byte> payload) =>
        Encode(RelayOp.Deliver, new byte[16], sourceId, payload);

    public static bool TryParse(ReadOnlySpan<byte> buffer, out RelayOp op, out byte[] token, out string peerId, out byte[] payload)
    {
        op = 0;
        token = Array.Empty<byte>();
        peerId = "";
        payload = Array.Empty<byte>();
        if (buffer.Length < 21 || buffer[0] != Magic0 || buffer[1] != Magic1 || buffer[2] != 1)
            return false;
        if (buffer[3] is < 1 or > 3)
            return false;
        op = (RelayOp)buffer[3];
        token = buffer.Slice(4, 16).ToArray();
        var idLen = buffer[20];
        if (idLen > 64 || buffer.Length < 21 + idLen)
            return false;
        peerId = Encoding.UTF8.GetString(buffer.Slice(21, idLen));
        payload = buffer[(21 + idLen)..].ToArray();
        return true;
    }

    private static byte[] Encode(RelayOp op, ReadOnlySpan<byte> token, string peerId, ReadOnlySpan<byte> payload)
    {
        if (token.Length != 16)
            throw new ArgumentException("Relay token is 16 bytes.");
        var id = Encoding.UTF8.GetBytes(peerId ?? "");
        if (id.Length is < 1 or > 64)
            throw new ArgumentException("Relay peer id must be 1 to 64 bytes.");
        if (payload.Length > 60_000)
            throw new ArgumentException("Relay payload is too large.");
        var buf = new byte[21 + id.Length + payload.Length];
        buf[0] = Magic0;
        buf[1] = Magic1;
        buf[2] = 1;
        buf[3] = (byte)op;
        token.CopyTo(buf.AsSpan(4));
        buf[20] = (byte)id.Length;
        id.CopyTo(buf.AsSpan(21));
        payload.CopyTo(buf.AsSpan(21 + id.Length));
        return buf;
    }
}

public sealed record SignalPeer(string Id, string Name, string Role);

public sealed record SignalMessage(
    string Op,
    string? Name,
    string? Role,
    string? PeerId,
    string? Token,
    int? UdpPort,
    string? To,
    string? From,
    string? Data,
    List<SignalPeer>? Peers,
    string? Message);

public static class SignalJson
{
    public static string Hello(string name, string role) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["op"] = "hello", ["name"] = name, ["role"] = role });

    public static string Signal(string to, string data) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["op"] = "signal", ["to"] = to, ["data"] = data });

    public static string List() =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["op"] = "list" });

    public static bool TryParse(string json, out SignalMessage message)
    {
        message = new SignalMessage("", null, null, null, null, null, null, null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl))
                return false;
            var op = opEl.GetString() ?? "";
            string? Str(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            int? Num(string name) => root.TryGetProperty(name, out var el) && el.TryGetInt32(out var n) ? n : null;
            List<SignalPeer>? peers = null;
            if (root.TryGetProperty("peers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                peers = new List<SignalPeer>();
                foreach (var item in arr.EnumerateArray())
                {
                    peers.Add(new SignalPeer(
                        item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        item.TryGetProperty("role", out var r) ? r.GetString() ?? "" : ""));
                }
            }
            message = new SignalMessage(op, Str("name"), Str("role"), Str("peerId"), Str("token"), Num("udpPort"), Str("to"), Str("from"), Str("data"), peers, Str("message"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Uri ToWebSocket(string httpUrl)
    {
        var uri = new Uri(httpUrl);
        var scheme = uri.Scheme == "https" ? "wss" : "ws";
        var builder = new UriBuilder(uri) { Scheme = scheme, Path = "/v1/signal" };
        return builder.Uri;
    }
}
