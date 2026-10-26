namespace EdgeSpan.Product;

public sealed record PairingInvite(string Host, int Port, string Pin, string Name, string RelayPeer = "", string RelayUrl = "");

public static class PairingLink
{
    public static string Build(string host, int port, string pin, string name) =>
        $"edgestpan://pair?v=1&h={Uri.EscapeDataString(host)}&p={port}&pin={Uri.EscapeDataString(pin)}&n={Uri.EscapeDataString(name)}";

    public static bool TryParse(string? text, out PairingInvite invite)
    {
        invite = new PairingInvite("", 0, "", "");
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        string query;
        if (trimmed.StartsWith("edgestpan://", StringComparison.OrdinalIgnoreCase))
        {
            var q = trimmed.IndexOf('?');
            if (q < 0)
                return false;
            query = trimmed[(q + 1)..];
        }
        else if (trimmed.Contains('='))
        {
            query = trimmed.TrimStart('?');
        }
        else
        {
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0)
                continue;
            map[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        if (!map.TryGetValue("h", out var host) || string.IsNullOrWhiteSpace(host))
            return false;
        if (!map.TryGetValue("pin", out var pin) || pin.Trim().Length < 4)
            return false;
        if (!map.TryGetValue("p", out var portText) || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
            return false;
        map.TryGetValue("n", out var name);
        map.TryGetValue("r", out var relayPeer);
        map.TryGetValue("u", out var relayUrl);
        invite = new PairingInvite(host, port, pin.Trim(), name ?? "", relayPeer ?? "", relayUrl ?? "");
        return true;
    }
}
