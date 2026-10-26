using EdgeSpan.Layout;
using EdgeSpan.Protocol;

namespace EdgeSpan.Tests;

public class HandoffTuningTests
{
    [Fact]
    public void CapHalvesOnHeavyDelayAndRecovers()
    {
        var preset = 12_000_000;
        var cap = HandoffPolicy.NextCap(preset, preset, 300, 0);
        Assert.Equal(preset / 2, cap);
        Assert.True(HandoffPolicy.NextCap(cap, preset, 150, 0) < cap);
        Assert.True(HandoffPolicy.NextCap(cap, preset, 20, 0) > cap);
        Assert.Equal(preset, HandoffPolicy.NextCap(preset, preset, 10, 0));
        Assert.True(HandoffPolicy.NextCap(1_500_000, preset, 900, 50) >= 1_500_000);
    }

    [Fact]
    public void RetuneNeedsBigChangeAndAge()
    {
        Assert.False(HandoffPolicy.ShouldRetune(8_000_000, 4_000_000, 1000));
        Assert.True(HandoffPolicy.ShouldRetune(8_000_000, 4_000_000, 4000));
        Assert.False(HandoffPolicy.ShouldRetune(8_000_000, 7_000_000, 9000));
    }

    [Fact]
    public void StatsPacketIsRecognisedAndUnpacked()
    {
        var packed = (uint)((7 << 16) | 85);
        var c = new HandoffPayloads.VideoConfig(1, packed, 0, false);
        Assert.True(HandoffPayloads.IsStats(c));
        Assert.Equal(85, HandoffPayloads.StatsDelayMs(c));
        Assert.Equal(7, HandoffPayloads.StatsDropped(c));
        Assert.False(HandoffPayloads.IsStats(new HandoffPayloads.VideoConfig(1, 8_000_000, 60, false)));
        Assert.False(HandoffPayloads.IsStats(new HandoffPayloads.VideoConfig(1, 0, 0, true)));
    }

    [Fact]
    public void WindowSizeFollowsTabletDensity()
    {
        Assert.Equal((2560, 1600), HandoffPolicy.WindowSize(2560, 1600, 2560, 1600, 144));
        Assert.Equal((1920, 1200), HandoffPolicy.WindowSize(2560, 1600, 1280, 800, 144));
        Assert.Equal((1280, 800), HandoffPolicy.WindowSize(2560, 1600, 1280, 800, 96));
        Assert.Equal((2560, 1600), HandoffPolicy.WindowSize(2560, 1600, 1280, 800, 288));
    }
}
