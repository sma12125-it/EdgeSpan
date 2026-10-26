using EdgeSpan.Product;

namespace EdgeSpan.Windows.ViewModels;

/// <summary>Indexer over the catalog for the Windows 11 style shell ({Binding Ui[key]}).</summary>
public sealed class UiText
{
    private readonly string _lang;

    public UiText(string? lang) => _lang = Catalog.Normalize(lang);

    public string this[string key] => Catalog.Get(_lang, key);

    private static readonly (int Page, string[] Words)[] Index =
    {
        (2, new[] { "arrange", "layout", "edge", "screen", "display", "monitor", "corner", "speed", "چیدمان", "لبه", "صفحه" }),
        (1, new[] { "device", "pair", "pin", "qr", "tablet", "phone", "دستگاه", "جفت" }),
        (8, new[] { "transfer", "file", "clipboard", "handoff", "انتقال", "فایل", "کلیپ" }),
        (3, new[] { "setting", "hotkey", "shortcut", "glow", "sound", "theme", "dark", "light", "language", "port", "relay", "stun",
            "internet", "license", "about", "version", "log", "privacy", "startup", "تنظیم", "زبان", "میانبر", "مجوز", "درباره" }),
        (0, new[] { "home", "connect", "usb", "wifi", "status", "خانه", "اتصال" }),
    };

    /// <summary>Returns the page that best matches a search query, or null.</summary>
    public static int? FindPage(string? query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length < 2)
            return null;
        foreach (var (page, words) in Index)
            foreach (var w in words)
                if (w.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(w, StringComparison.Ordinal))
                    return page;
        return null;
    }
}
