using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeSpan.Product;

public sealed class DeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Android";
    public string Address { get; set; } = "";
    public int Port { get; set; } = ProductInfo.DefaultSessionPort;
    public string PcEdge { get; set; } = "Right";
    public string ReturnEdge { get; set; } = "Left";
    public string Anchor { get; set; } = "Edge";
    public string Corner { get; set; } = "TopRight";
    public int OffsetPx { get; set; }
    public int SpanPx { get; set; }
    public int CornerSizePx { get; set; } = 120;
    public double PointerSpeed { get; set; } = 1;
    public int ScreenWidth { get; set; } = 1920;
    public int ScreenHeight { get; set; } = 1080;
}

public sealed class AppSettings
{
    public int Schema { get; set; } = 1;
    public string HostId { get; set; } = Guid.NewGuid().ToString("N");
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "dark";
    public string DisplayName { get; set; } = Environment.MachineName;
    public int ListenPort { get; set; } = ProductInfo.DefaultSessionPort;
    public int ScreenWidth { get; set; } = 1920;
    public int ScreenHeight { get; set; } = 1080;
    public int ScreenOriginX { get; set; }
    public int ScreenOriginY { get; set; }
    public bool ClipboardShare { get; set; } = true;
    public bool FileTransfer { get; set; } = true;
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; }
    public bool LaunchAtStartup { get; set; }
    public string HotkeySwitch { get; set; } = "Ctrl+Alt+Space";
    public bool HotkeySwitchEnabled { get; set; } = true;
    public bool DeviceNumberHotkeys { get; set; } = true;
    public string HotkeyLock { get; set; } = "Ctrl+Alt+L";
    public string Handoff { get; set; } = "off";
    public string HandoffQuality { get; set; } = "sharp";
    public bool EdgeGlowEnabled { get; set; } = true;
    public string EdgeGlowColor { get; set; } = "#3DDC84";
    public int EdgeGlowIntensity { get; set; } = 80;
    public int EdgeGlowThickness { get; set; } = 22;
    public bool BreakthroughEnabled { get; set; } = true;
    public int BreakthroughIntensity { get; set; } = 70;
    public bool ReduceMotion { get; set; }
    public bool SoundEnabled { get; set; }
    public bool InternetEnabled { get; set; }
    public string RelayUrl { get; set; } = "http://127.0.0.1:8787";
    public string StunHost { get; set; } = "";
    public int StunPort { get; set; } = 19302;
    public string LastMethod { get; set; } = "";
    public string? LicenseKey { get; set; }
    public DateTimeOffset FirstRunUtc { get; set; }
    public List<DeviceConfig> Devices { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EdgeSpan",
        "settings.json");

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new AppSettings { FirstRunUtc = DateTimeOffset.UtcNow };
            return fresh;
        }
        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        settings.Devices ??= new List<DeviceConfig>();
        if (string.IsNullOrWhiteSpace(settings.HostId))
            settings.HostId = Guid.NewGuid().ToString("N");
        if (settings.FirstRunUtc == default)
            settings.FirstRunUtc = DateTimeOffset.UtcNow;
        if (settings.ListenPort is < 1 or > 65535)
            settings.ListenPort = ProductInfo.DefaultSessionPort;
        if (string.Equals(settings.StunHost, "stun.l.google.com", StringComparison.OrdinalIgnoreCase))
            settings.StunHost = "";
        if (settings.HotkeySwitch == "Ctrl+Alt+S")
            settings.HotkeySwitch = "Ctrl+Alt+Space";
        if (settings.Handoff is not ("off" or "pc" or "phone" or "both"))
            settings.Handoff = "off";
        if (settings.HandoffQuality is not ("sharp" or "smooth" or "saver"))
            settings.HandoffQuality = "sharp";
        settings.EdgeGlowIntensity = Math.Clamp(settings.EdgeGlowIntensity, 0, 100);
        if (settings.EdgeGlowThickness == 0)
            settings.EdgeGlowThickness = 22;
        settings.EdgeGlowThickness = Math.Clamp(settings.EdgeGlowThickness, 4, 120);
        settings.BreakthroughIntensity = Math.Clamp(settings.BreakthroughIntensity, 0, 100);
        if (string.IsNullOrWhiteSpace(settings.EdgeGlowColor))
            settings.EdgeGlowColor = "#3DDC84";
        return settings;
    }

    public static void Save(string path, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}
