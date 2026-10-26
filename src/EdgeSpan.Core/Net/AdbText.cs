namespace EdgeSpan.Net;

public sealed record AdbDevice(string Serial, string State);

public static class AdbText
{
    public static IReadOnlyList<AdbDevice> Parse(string? text)
    {
        var list = new List<AdbDevice>();
        if (string.IsNullOrEmpty(text))
            return list;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("*", StringComparison.Ordinal))
                continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var state = parts[1];
            if (state is "device" or "unauthorized" or "offline")
                list.Add(new AdbDevice(parts[0], state));
        }
        return list;
    }
}
