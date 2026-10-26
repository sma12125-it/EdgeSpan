using System.Runtime.Versioning;

namespace EdgeSpan.Windows.Services;

public static class StartupRegistration
{
    private const string ValueName = "EdgeSpan";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;
        ApplyWindows(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled)
    {
        var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null)
            return;
        using (key)
        {
            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
                return;
            }
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
                key.SetValue(ValueName, $"\"{path}\"");
        }
    }
}
