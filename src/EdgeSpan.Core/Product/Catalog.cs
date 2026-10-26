using System.Text.Json;

namespace EdgeSpan.Product;

public static class Catalog
{
    private static readonly Dictionary<string, Dictionary<string, string>> Map = Load();

    public static IReadOnlyCollection<string> Keys => Map.Keys;

    public static bool IsRtl(string? lang) => string.Equals(lang, "fa", StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? lang) => IsRtl(lang) ? "fa" : "en";

    public static string Get(string? lang, string key)
    {
        lang = Normalize(lang);
        if (!Map.TryGetValue(key, out var langs))
            return key;
        if (langs.TryGetValue(lang, out var value) && !string.IsNullOrEmpty(value))
            return value;
        return langs.TryGetValue("en", out var en) ? en : key;
    }

    public static string Format(string? lang, string key, params object[] args) =>
        string.Format(Get(lang, key), args);

    public static bool Complete(string key) =>
        Map.TryGetValue(key, out var langs)
        && !string.IsNullOrWhiteSpace(langs.GetValueOrDefault("en"))
        && !string.IsNullOrWhiteSpace(langs.GetValueOrDefault("fa"));

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("edgestpan.strings.json")
            ?? throw new InvalidOperationException("Embedded string catalog edgestpan.strings.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var langs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var lang in entry.Value.EnumerateObject())
                langs[lang.Name] = lang.Value.GetString() ?? "";
            map[entry.Name] = langs;
        }
        return map;
    }
}
