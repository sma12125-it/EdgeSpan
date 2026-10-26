namespace EdgeSpan.Product;

public static class ProductInfo
{
    public const string Name = "EdgeSpan";
    public const string Version = "0.9.12";
    public const int VersionCode = 30;
    public const string BuildDate = "2026-10-25";
    public const int ProtocolVersion = 1;
    public const int DiscoveryPort = 45454;
    public const int DefaultSessionPort = 45455;
    public const int RelayHttpPort = 8787;
    public const int RelayUdpPort = 8788;
    public const int AdbServerPort = 27183;
    public const int HandoffPort = 27184;
    public const byte HandoffCapability = 0x08;
    public const string SamsungUsbDriverUrl = "https://developer.samsung.com/android-usb-driver";
    public const int MaxClipboardBytes = 256 * 1024;
}

public sealed class RuntimeOptions
{
    public int CoalesceMs { get; init; } = 2;
    public int HeartbeatMs { get; init; } = 1000;
    public int ReliableRetryMs { get; init; } = 40;
    public int ReliableMaxAttempts { get; init; } = 30;
    public int PairingWindowMs { get; init; } = 300_000;
    public int MaxPairFailures { get; init; } = 5;
    public int FragmentBytes { get; init; } = 900;
}
