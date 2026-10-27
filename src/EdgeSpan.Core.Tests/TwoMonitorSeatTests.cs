using EdgeSpan.Layout;
using EdgeSpan.Session;

namespace EdgeSpan.Core.Tests;

public sealed class TwoMonitorSeatTests
{
    [Fact]
    public void TabletOnTheInnerEdgeOfTheFirstMonitorIsReachedWhileDragging()
    {
        var left = new HostMonitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, 96, true);
        Assert.True(HostController.SeatPixel(ScreenEdge.Right, 1919, 500, left, 0, 1080));
        Assert.True(HostController.SeatPixel(ScreenEdge.Right, 1950, 500, left, 0, 1080));
        Assert.False(HostController.SeatPixel(ScreenEdge.Right, 2100, 500, left, 0, 1080));
        Assert.False(HostController.SeatPixel(ScreenEdge.Right, 1919, 900, left, 0, 800));
        var right = new HostMonitor(@"\\.\DISPLAY2", 1920, 0, 2560, 1440);
        Assert.True(HostController.SeatPixel(ScreenEdge.Right, 4479, 1300, right, 0, 1440));
        Assert.True(HostController.SeatPixel(ScreenEdge.Left, 1920, 100, right, 0, 1440));
        Assert.False(HostController.SeatPixel(ScreenEdge.Left, 1000, 100, right, 0, 1440));
    }
}
