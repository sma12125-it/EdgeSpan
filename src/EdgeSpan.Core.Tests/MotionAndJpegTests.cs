using EdgeSpan.Media;
using EdgeSpan.Product;

namespace EdgeSpan.Tests;

public class MotionAndJpegTests
{
    [Fact]
    public void Spring_settles_and_keeps_velocity_when_retargeted()
    {
        var spring = new Motion.Spring1D(Motion.Breakthrough);
        spring.Retarget(1);
        var sawVelocity = false;
        for (var i = 0; i < 40; i++)
        {
            spring.Step(0.016, reduceMotion: false);
            if (spring.Velocity > 0.2)
                sawVelocity = true;
        }
        Assert.True(sawVelocity);
        var velocity = spring.Velocity;
        spring.Retarget(0);
        spring.Step(0.016, reduceMotion: false);
        Assert.True(spring.Velocity < velocity);
        var guard = 0;
        while (!spring.Step(0.016, reduceMotion: false) && guard++ < 200)
        {
        }
        Assert.Equal(0, spring.Value, 3);
        Assert.Equal(0, spring.Velocity, 3);
    }

    [Fact]
    public void Reduce_motion_snaps()
    {
        var spring = new Motion.Spring1D(Motion.Press, 0.2);
        spring.Retarget(1);
        Assert.True(spring.Step(0.016, reduceMotion: true));
        Assert.Equal(1, spring.Value, 5);
        Assert.Equal(0, spring.Velocity, 5);
        Assert.True(Motion.Reduced(appToggle: false, systemToggle: true));
        Assert.False(Motion.Reduced(false, false));
    }

    [Fact]
    public void Jpeg_is_a_baseline_frame_a_decoder_can_open()
    {
        var bgra = new byte[32 * 24 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 20;
            bgra[i + 1] = 40;
            bgra[i + 2] = 210;
            bgra[i + 3] = 255;
        }
        var jpeg = JpegStill.Encode(bgra, 32, 24, 32 * 4);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xFF, jpeg[^2]);
        Assert.Equal(0xD9, jpeg[^1]);
        Assert.Equal(0x03, JpegStill.Flag);
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        Assert.True(sof > 0);
        Assert.Equal(0, jpeg[sof + 5]);
        Assert.Equal(24, jpeg[sof + 6]);
        Assert.Equal(0, jpeg[sof + 7]);
        Assert.Equal(32, jpeg[sof + 8]);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "edgestpan-jpeg-probe.jpg"), jpeg);
    }
}
