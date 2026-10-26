using System.Buffers.Binary;
using EdgeSpan.Layout;
using EdgeSpan.Protocol;

namespace EdgeSpan.Tests;

public class AppStreamTests
{
    [Fact]
    public void HelloRoundTrips()
    {
        var framed = AppStreamWire.WriteHello(new AppStreamWire.Hello("phone-1", 1080, 2400, 77, "Notes", "com.x/.Main"));
        Assert.Equal(framed.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(framed));
        Assert.True(AppStreamWire.TryHello(framed.AsSpan(4), out var h));
        Assert.Equal(("phone-1", (ushort)1080, (ushort)2400, 77UL, "Notes", "com.x/.Main"), (h.DeviceId, h.Width, h.Height, h.StartSeq, h.Title, h.Package));
        Assert.False(AppStreamWire.TryHello(framed.AsSpan(4, 10), out _));
    }

    [Fact]
    public void ReplyIsSeventeenBytes()
    {
        var r = AppStreamWire.WriteReply(true, 5);
        Assert.Equal(17, r.Length);
        Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(r));
        Assert.Equal(1, r[8]);
        Assert.Equal(5UL, BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(9)));
    }

    [Fact]
    public void InputRoundTripsThroughSealedFrame()
    {
        var key = new byte[32];
        var input = new AppStreamWire.Input(AppStreamWire.InputButton, 100, 2000, 2, true, -1, 0x41, 4, "hé");
        var sealedFrame = MediaFrames.Protect(key, 9, 1, 0, 0, 0, AppStreamWire.WriteInput(input));
        Assert.True(MediaFrames.Open(key, 9, sealedFrame, out _, out _, out _, out _, out var plain));
        Assert.True(AppStreamWire.TryInput(plain, out var back));
        Assert.Equal(input, back);
        Assert.False(MediaFrames.Open(key, 10, sealedFrame, out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(true, true, "both")]
    [InlineData(true, false, "pc")]
    [InlineData(false, true, "phone")]
    [InlineData(false, false, "off")]
    public void HandoffToggleCombine(bool pc, bool phone, string expected)
    {
        var mode = HandoffPolicy.Combine(pc, phone);
        Assert.Equal(expected, mode);
        Assert.Equal(pc, HandoffPolicy.AllowsPcToPhone(mode));
        Assert.Equal(phone, HandoffPolicy.AllowsPhoneToPc(mode));
    }

    [Fact]
    public void BlackUniformFrameLooksBlocked()
    {
        Assert.True(HandoffPolicy.LooksBlocked(16, 0.5));
        Assert.False(HandoffPolicy.LooksBlocked(16, 40));
        Assert.False(HandoffPolicy.LooksBlocked(120, 2));
    }
}

public class AppStreamWireBytesTests
{
    [Fact]
    public void WireBytesMatchAndroid()
    {
        Assert.Equal("1c00000045534131027031380460094d0000000000000001004e0400612f2e4d",
            Convert.ToHexString(AppStreamWire.WriteHello(new AppStreamWire.Hello("p1", 1080, 2400, 77, "N", "a/.M"))).ToLowerInvariant());
        Assert.Equal("0264000000d00700000201ffff41000400010078",
            Convert.ToHexString(AppStreamWire.WriteInput(new AppStreamWire.Input(AppStreamWire.InputButton, 100, 2000, 2, true, -1, 0x41, 4, "x"))).ToLowerInvariant());
    }
}
