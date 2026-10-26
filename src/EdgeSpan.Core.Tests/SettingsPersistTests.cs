using EdgeSpan.Crypto;
using EdgeSpan.Layout;
using EdgeSpan.Product;
using EdgeSpan.Session;

namespace EdgeSpan.Core.Tests;

public sealed class SettingsPersistTests
{
    [Fact]
    public void AutoSavedSettingsAndPerMonitorLayoutsReadBackOnStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), "edgespan-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AppSettings { DisplayName = "Desk", ClipboardShare = false, EdgeGlowIntensity = 37, ListenPort = 40123, ArrangeScale = "uniform" };
            s.Devices.Add(new DeviceConfig { Id = "tab", Name = "Tab", Address = "10.0.0.5", Monitor = @"\\.\DISPLAY2", PcEdge = "Right", ReturnEdge = "Left", OffsetPx = 120 });
            var two = new[] { new HostMonitor(@"\\.\DISPLAY1", 0, 0, 2560, 1440, 144, true), new HostMonitor(@"\\.\DISPLAY2", 2560, 0, 1920, 1080) };
            using (var host = new HostController(s, () => 0, new SystemRandomBytes(), null, null, path))
            {
                host.SetHostMonitors(two);
                host.Save();
            }
            var loaded = SettingsStore.Load(path);
            Assert.Equal("Desk", loaded.DisplayName);
            Assert.False(loaded.ClipboardShare);
            Assert.Equal(37, loaded.EdgeGlowIntensity);
            Assert.Equal(40123, loaded.ListenPort);
            Assert.Equal("uniform", loaded.ArrangeScale);
            var dev = Assert.Single(loaded.Devices);
            Assert.Equal(@"\\.\DISPLAY2", dev.Monitor);
            Assert.Equal(120, dev.OffsetPx);
            Assert.True(dev.Layouts.ContainsKey(HostController.KeyFor(two)));

            // Unplug monitor 2: the device falls back to the primary; plug it back: its saved layout returns.
            using var again = new HostController(loaded, () => 0, new SystemRandomBytes(), null, null, path);
            Assert.True(again.Machine.UniformScale);
            again.SetHostMonitors(new[] { two[0] });
            Assert.Equal(@"\\.\DISPLAY1", again.Machine.MonitorFor(again.Machine.Devices[0]).Id);
            loaded.Devices[0].Monitor = @"\\.\DISPLAY1";
            loaded.Devices[0].PcEdge = "Left";
            again.UpdateDevice(loaded.Devices[0]);
            again.SetHostMonitors(two);
            Assert.Equal(@"\\.\DISPLAY2", loaded.Devices[0].Monitor);
            Assert.Equal("Right", loaded.Devices[0].PcEdge);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
