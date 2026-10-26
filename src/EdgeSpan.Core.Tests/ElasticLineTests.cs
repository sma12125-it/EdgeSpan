using EdgeSpan.Layout;

namespace EdgeSpan.Core.Tests;

public class ElasticLineTests
{
    [Fact]
    public void Deformation_peaks_at_crossing_point()
    {
        var cross = 0.72;
        var best = 0.0;
        var bestAt = 0.0;
        for (var i = 0; i <= 1000; i++)
        {
            var a = i / 1000.0;
            var d = ElasticLine.Displacement(ElasticMode.Enter, a, cross, 150);
            if (d > best) { best = d; bestAt = a; }
        }
        Assert.InRange(bestAt, cross - 0.002, cross + 0.002);
        Assert.True(ElasticLine.Displacement(ElasticMode.Exit, cross, cross, 80) < -0.3);
        Assert.True(Math.Abs(ElasticLine.Displacement(ElasticMode.Exit, 0.1, cross, 80)) < 0.01);
    }

    [Fact]
    public void Enter_tears_then_fades_and_ends_in_time()
    {
        Assert.True(ElasticLine.Opacity(ElasticMode.Enter, 0.5, 0.5, 300) < 0.05);
        Assert.True(ElasticLine.Opacity(ElasticMode.Enter, 0.9, 0.5, 300) > 0.2);
        Assert.Equal(0, ElasticLine.Opacity(ElasticMode.Enter, 0.9, 0.5, ElasticLine.DurationMs));
        Assert.Equal(0, ElasticLine.Displacement(ElasticMode.Exit, 0.5, 0.5, ElasticLine.DurationMs));
    }

    [Fact]
    public void LeaveKeepsBaselineOnTheEdgeAwayFromCrossing()
    {
        for (var ms = 0.0; ms < ElasticLine.DurationMs; ms += 10)
        {
            foreach (var along in new[] { 0.0, 0.1, 0.25, 0.75, 0.9, 1.0 })
            {
                Assert.Equal(0, ElasticLine.Offset(ElasticMode.Exit, along, 0.5, ms));
                Assert.True(Math.Abs(ElasticLine.Thickness(ElasticMode.Exit, along, 0.5, ms) - 1) < 0.01);
            }
            // At the crossing the line never moves into the screen either; it only thins (pulled outward).
            Assert.Equal(0, ElasticLine.Offset(ElasticMode.Exit, 0.5, 0.5, ms));
        }
        Assert.True(ElasticLine.Thickness(ElasticMode.Exit, 0.5, 0.5, 80) < 0.6);
        Assert.True(ElasticLine.Displacement(ElasticMode.Exit, 0.5, 0.5, 80) < 0);
    }
}
