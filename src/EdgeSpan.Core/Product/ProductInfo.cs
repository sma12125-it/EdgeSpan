namespace EdgeSpan.Product;

public static class ProductInfo
{
    public const string Name = "EdgeSpan";
    // Derived from <Version> in Directory.Build.props so the UI, diagnostics and installer never disagree.
    public static readonly string Version =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ProductInfo).Assembly)
            ?.InformationalVersion ?? "0.9.21").Split('+')[0];
    public const int VersionCode = 40;
    public const string BuildDate = "2026-10-26";
    public const int ProtocolVersion = 1;
    public const int DiscoveryPort = 45454;
    public const int DefaultSessionPort = 45455;
    public const int RelayHttpPort = 8787;
    public const int RelayUdpPort = 8788;
    public const int AdbServerPort = 27183;
    public const int HandoffPort = 27184;
    /// <summary>Android app to Windows window stream (phone dials the PC; over USB through adb reverse).</summary>
    public const int AppStreamPort = 27186;
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
