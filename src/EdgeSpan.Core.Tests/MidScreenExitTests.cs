using EdgeSpan.Product;
using EdgeSpan.Layout;
using EdgeSpan.Session;
using EdgeSpan.Crypto;

namespace EdgeSpan.Tests;

public class MidScreenExitTests
{
    private static DevicePlacement Place(int w, int h)
    {
        var c = new DeviceConfig { Id = "tab", Name = "tab", PcEdge = "Left", ReturnEdge = "Right", ScreenWidth = w, ScreenHeight = h };
        Assert.True(PlacementParser.TryParse(c, out var p, out var e), e);
        return p;
    }

    [Fact]
    public void Rotation_while_in_control_does_not_exit_mid_screen()
    {
        var m = new EdgeMachine();
        m.SetScreen(0, 0, 1920, 1080);
        m.SetDevices(new[] { Place(2400, 1080) });
        Assert.IsType<Transition.Enter>(m.MoveHost(-1, 500));
        Assert.IsType<Transition.Stay>(m.MoveDeviceBy(-30, 0)); // arm, pointer near x=2370
        m.SetDevices(new[] { Place(1080, 2400) });              // phone rotated to portrait
        m.ClampActive();
        Assert.True(m.X <= 1079);
        Assert.IsType<Transition.Stay>(m.MoveDeviceBy(-5, 0));  // moving inward never exits
        Assert.IsType<Transition.Stay>(m.MoveDeviceBy(0, 40));  // nor moving along the edge
        Assert.IsType<Transition.Return>(m.MoveDeviceBy(50, 0)); // pushing out at the real edge does
    }

    [Fact]
    public void Ui_save_does_not_overwrite_the_phone_reported_size()
    {
        var s = new AppSettings();
        s.Devices.Add(new DeviceConfig { Id = "tab", Name = "Tab", PcEdge = "Right", ReturnEdge = "Left", ScreenWidth = 2400, ScreenHeight = 1080 });
        using var host = new HostController(s, () => 0, new SystemRandomBytes(), null, null,
            Path.Combine(Path.GetTempPath(), "edgespan-test-" + Guid.NewGuid().ToString("N") + ".json"));
        host.UpdateDevice(new DeviceConfig { Id = "tab", Name = "Tab", PcEdge = "Right", ReturnEdge = "Left", ScreenWidth = 1080, ScreenHeight = 2400 });
        Assert.Equal(2400, host.Machine.Devices[0].ScreenWidth);
        Assert.Equal(1080, host.Machine.Devices[0].ScreenHeight);
    }
}
