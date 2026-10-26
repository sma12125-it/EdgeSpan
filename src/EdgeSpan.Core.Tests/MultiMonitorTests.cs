using EdgeSpan.Layout;

namespace EdgeSpan.Core.Tests;

public sealed class MultiMonitorTests
{
    private const string M1 = @"\\.\DISPLAY1";
    private const string M2 = @"\\.\DISPLAY2";

    // Monitor 1: 2560x1440 at 150% (144 dpi), primary. Monitor 2: 1920x1080 at 100% to its right.
    private static EdgeMachine Machine(params DevicePlacement[] devices)
    {
        var m = new EdgeMachine();
        m.SetMonitors(new[]
        {
            new HostMonitor(M1, 0, 0, 2560, 1440, 144, true),
            new HostMonitor(M2, 2560, 0, 1920, 1080, 96)
        });
        m.SetDevices(devices);
        return m;
    }

    private static DevicePlacement Tab(string monitor, ScreenEdge edge, int offset = 0) => new()
    {
        Id = "tab", Name = "Tab", MonitorId = monitor, PcEdge = edge,
        ReturnEdge = edge switch { ScreenEdge.Left => ScreenEdge.Right, ScreenEdge.Right => ScreenEdge.Left, ScreenEdge.Top => ScreenEdge.Bottom, _ => ScreenEdge.Top },
        Anchor = AnchorKind.Edge, ScreenWidth = 2000, ScreenHeight = 1200
    };

    [Fact]
    public void AndroidBetweenMonitorsTakesOnlyTheOverlappingSegment()
    {
        var m = Machine(Tab(M1, ScreenEdge.Right));
        var tab = m.Devices[0];
        Assert.Equal(M2, m.NeighborFor(tab)?.Id);
        Assert.Equal((0, 1200), m.Segment(tab, m.MonitorFor(tab)));

        // Below the device's segment the cursor still passes straight to monitor 2.
        m.SetHostCursor(2550, 1300);
        Assert.IsType<Transition.Stay>(m.MoveHost(2565, 1300));

        // Coming back from monitor 2 over the shared edge does not enter the device.
        m.SetHostCursor(2600, 600);
        Assert.IsType<Transition.Stay>(m.MoveHost(2550, 600));

        // Leaving monitor 1's right edge inside the segment enters Android proportionally.
        m.SetHostCursor(2550, 600);
        var enter = Assert.IsType<Transition.Enter>(m.MoveHost(2565, 600));
        Assert.Equal(600, enter.Y);
        Assert.Equal(2, enter.X);

        // Leaving Android's right side continues onto monitor 2's left edge at the same height.
        var through = Assert.IsType<Transition.Return>(m.MoveDeviceBy(3000, 0));
        Assert.True(through.Through);
        Assert.Equal(M2, through.MonitorId);
        Assert.Equal((2560, 600), (through.HostX, through.HostY));
    }

    [Fact]
    public void AndroidBetweenMonitorsReturnsToMonitorOneOnItsLeftSide()
    {
        var m = Machine(Tab(M1, ScreenEdge.Right));
        m.SetHostCursor(2550, 300);
        Assert.IsType<Transition.Enter>(m.MoveHost(2565, 300));
        m.MoveDeviceBy(20, 0); // arm the return side
        var back = Assert.IsType<Transition.Return>(m.MoveDeviceBy(-100, 0));
        Assert.False(back.Through);
        Assert.Equal(M1, back.MonitorId);
        Assert.Equal((2559, 300), (back.HostX, back.HostY));
    }

    [Fact]
    public void AndroidOnMonitorTwoRightEdgeMapsAcrossTheClippedSegment()
    {
        var m = Machine(Tab(M2, ScreenEdge.Right) is var t ? new DevicePlacement
        {
            Id = t.Id, Name = t.Name, MonitorId = M2, PcEdge = t.PcEdge, ReturnEdge = t.ReturnEdge,
            Anchor = t.Anchor, ScreenWidth = t.ScreenWidth, ScreenHeight = t.ScreenHeight, OffsetPx = 100
        } : null!);
        var tab = m.Devices[0];
        Assert.Null(m.NeighborFor(tab));
        Assert.Equal((100, 1080), m.Segment(tab, m.MonitorFor(tab)));

        // Monitor 1's right edge is not a crossing zone any more (it leads to monitor 2).
        m.SetHostCursor(2550, 500);
        Assert.IsType<Transition.Stay>(m.MoveHost(2565, 500));

        m.SetHostCursor(4470, 500);
        var enter = Assert.IsType<Transition.Enter>(m.MoveHost(4485, 500));
        Assert.Equal(490, enter.Y); // 400 of 980 overlapping px -> 490 of 1200 device px

        m.MoveDeviceBy(20, 0);
        var back = Assert.IsType<Transition.Return>(m.MoveDeviceBy(-100, 0));
        Assert.Equal(M2, back.MonitorId);
        Assert.Equal((4479, 500), (back.HostX, back.HostY));

        // Above the segment the edge is a plain wall.
        m.SetHostCursor(4470, 50);
        Assert.IsType<Transition.Stay>(m.MoveHost(4485, 50));
    }

    [Fact]
    public void UnpluggedMonitorFallsBackToPrimary()
    {
        var m = new EdgeMachine();
        m.SetMonitors(new[] { new HostMonitor(M1, 0, 0, 2560, 1440, 144, true) });
        m.SetDevices(new[] { Tab(M2, ScreenEdge.Right) });
        Assert.Equal(M1, m.MonitorFor(m.Devices[0]).Id);
        m.SetHostCursor(2550, 100);
        Assert.IsType<Transition.Enter>(m.MoveHost(2565, 100));
    }
}
