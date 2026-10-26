namespace EdgeSpan.Layout;

/// <summary>Which half of the shared boundary line a device draws when the pointer crosses.</summary>
public enum ElasticMode
{
    /// <summary>Pointer leaves this screen: the line bulges outward with it, then snaps back.</summary>
    Exit,
    /// <summary>Pointer enters this screen: the line is stretched inward, tears, and the fragments recoil and fade.</summary>
    Enter
}

/// <summary>
/// The glowing elastic boundary shared by both screens. Pure function of time so the
/// Windows and Android halves stay in step when both start on the same edge-enter/return.
/// Kept identical to android/protocol ElasticLine.kt.
/// </summary>
public static class ElasticLine
{
    public const double DurationMs = 460;
    public const double TearMs = 170;
    public const double Sigma = 0.075;
    public const double ExitDepth = 0.62;
    public const double EnterDepth = 0.68;
    public const uint Accent = 0x14B8A6;

    private const double Omega = 2 * Math.PI / 0.30;
    private const double Zeta = 0.42;
    private static readonly double ExitPeak = FindExitPeak();

    /// <summary>Gaussian bell centered on the crossing point; along and cross are 0..1 along the edge.</summary>
    public static double Bell(double along, double cross, double sigma = Sigma)
    {
        var d = along - cross;
        return Math.Exp(-(d * d) / (2 * sigma * sigma));
    }

    /// <summary>
    /// Line displacement in units of the band thickness. Positive moves into the screen,
    /// negative moves out past the screen edge.
    /// </summary>
    public static double Displacement(ElasticMode mode, double along, double cross, double ms)
    {
        if (ms <= 0 || ms >= DurationMs)
            return 0;
        var bell = Bell(along, Math.Clamp(cross, 0, 1));
        if (mode == ElasticMode.Exit)
            return -ExitDepth * ExitEnvelope(ms) * bell;
        if (ms < TearMs)
        {
            var p = ms / TearMs;
            return EnterDepth * (1 - Math.Pow(1 - p, 3)) * bell;
        }
        var q = (ms - TearMs) / (DurationMs - TearMs);
        return EnterDepth * bell * Math.Exp(-5 * q) * Math.Cos(9 * q);
    }

    /// <summary>Opacity 0..1 of the elastic line at a point, including the tear gap and the fade-out.</summary>
    public static double Opacity(ElasticMode mode, double along, double cross, double ms)
    {
        if (ms < 0 || ms >= DurationMs)
            return 0;
        if (mode == ElasticMode.Exit)
            return 1 - SmoothStep(DurationMs - 90, DurationMs, ms);
        if (ms < TearMs)
            return 1;
        var q = (ms - TearMs) / (DurationMs - TearMs);
        var gap = Sigma * (0.35 + 2.4 * (1 - Math.Pow(1 - q, 3)));
        var d = Math.Abs(along - Math.Clamp(cross, 0, 1));
        var edge = SmoothStep(gap, gap + 0.025, d);
        return edge * Math.Pow(1 - q, 1.4);
    }

    /// <summary>Normalized position 0..1 of a point along an edge of a rect.</summary>
    public static double Along(GlowEdge edge, double x, double y, double left, double top, double width, double height)
    {
        var vertical = edge is GlowEdge.Left or GlowEdge.Right;
        var t = vertical ? (y - top) / Math.Max(1, height) : (x - left) / Math.Max(1, width);
        return Math.Clamp(t, 0, 1);
    }

    private static double ExitEnvelope(double ms)
    {
        var u = ms / 1000;
        var wd = Omega * Math.Sqrt(1 - Zeta * Zeta);
        var v = Math.Exp(-Zeta * Omega * u) * Math.Sin(wd * u) / ExitPeak;
        var tail = 1 - SmoothStep(DurationMs - 120, DurationMs, ms);
        return v * tail;
    }

    private static double FindExitPeak()
    {
        var wd = Omega * Math.Sqrt(1 - Zeta * Zeta);
        var best = 1e-6;
        for (var ms = 1; ms < 200; ms++)
        {
            var u = ms / 1000.0;
            best = Math.Max(best, Math.Exp(-Zeta * Omega * u) * Math.Sin(wd * u));
        }
        return best;
    }

    private static double SmoothStep(double a, double b, double x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
