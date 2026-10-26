using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using SharpGen.Runtime;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Inbox Media Foundation H.264 software decoder (CLSID_CMSH264DecoderMFT). Annex-B in, BGRA out.
/// Software keeps it working on every PC; a phone app window is small enough for the CPU path.
/// </summary>
internal sealed class H264Decoder : IDisposable
{
    private static readonly Guid Clsid = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid TransformIid = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid DefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    private static readonly Guid LowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NotAccepting = unchecked((int)0xC00D36B5);
    private static bool _started;

    private readonly IMFTransform _transform;
    private int _width, _height, _stride;
    private long _time;

    public int Width => _width;
    public int Height => _height;
    /// <summary>Luma mean and spread of the last frame, for the capture-blocked check.</summary>
    public double Mean { get; private set; }
    public double Spread { get; private set; }

    private H264Decoder(IMFTransform transform) => _transform = transform;

    public static H264Decoder? Create()
    {
        try
        {
            if (!_started)
            {
                MediaFactory.MFStartup(true);
                _started = true;
            }
            var clsid = Clsid;
            var iid = TransformIid;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var punk);
            DiagLog.Write("appstream decoder CoCreate 0x" + hr.ToString("X8"));
            if (hr < 0)
                return null;
            var transform = new IMFTransform(punk);
            try
            {
                using var attrs = transform.Attributes;
                attrs.Set(LowLatency, 1u);
            }
            catch
            {
            }
            var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, H264);
            transform.SetInputType(0, input, 0);
            var decoder = new H264Decoder(transform);
            decoder.PickOutput();
            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            return decoder;
        }
        catch (Exception ex)
        {
            DiagLog.Write("appstream decoder create failed " + ex.Message);
            return null;
        }
    }

    private void PickOutput()
    {
        for (var i = 0; i < 64; i++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, i);
            }
            catch
            {
                break;
            }
            if (type.GetGUID(MediaTypeAttributeKeys.Subtype) == Nv12)
            {
                _transform.SetOutputType(0, type, 0);
                ReadSize(type);
                return;
            }
            type.Dispose();
        }
    }

    private void ReadSize(IMFMediaType type)
    {
        try
        {
            var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            _width = (int)(size >> 32);
            _height = (int)(size & 0xFFFFFFFF);
        }
        catch
        {
        }
        try
        {
            _stride = (int)type.GetUInt32(DefaultStride);
        }
        catch
        {
            _stride = _width;
        }
        if (_stride <= 0)
            _stride = _width;
        DiagLog.Write($"appstream decoder output {_width}x{_height} stride={_stride}");
    }

    /// <summary>Feeds one access unit and returns the newest decoded BGRA frame (or null when none came out).</summary>
    public byte[]? Decode(byte[] annexb)
    {
        byte[]? last = null;
        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(annexb.Length);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(annexb, 0, ptr, annexb.Length);
        buffer.Unlock();
        buffer.CurrentLength = annexb.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = _time;
        sample.SampleDuration = 166_666;
        _time += 166_666;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _transform.ProcessInput(0, sample, 0);
                break;
            }
            catch (SharpGenException ex) when (ex.HResult == NotAccepting)
            {
                last = Drain() ?? last;
            }
        }
        last = Drain() ?? last;
        buffer.Dispose();
        sample.Dispose();
        return last;
    }

    private byte[]? Drain()
    {
        byte[]? last = null;
        for (var guard = 0; guard < 8; guard++)
        {
            var info = _transform.GetOutputStreamInfo(0);
            var size = Math.Max(info.Size, Math.Max(1, _stride) * Math.Max(16, _height + 16) * 3 / 2);
            using var sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
            sample.AddBuffer(buffer);
            var data = new OutputDataBuffer { Sample = sample };
            try
            {
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
                if (result.Code == StreamChange)
                {
                    PickOutput();
                    continue;
                }
                if (result.Failure)
                    return last;
            }
            catch (SharpGenException ex) when (ex.HResult == StreamChange)
            {
                PickOutput();
                continue;
            }
            catch (SharpGenException ex) when (ex.HResult == NeedMoreInput)
            {
                return last;
            }
            using var contig = sample.ConvertToContiguousBuffer();
            contig.Lock(out var p, out _, out var length);
            try
            {
                last = ToBgra(p, length);
            }
            finally
            {
                contig.Unlock();
            }
        }
        return last;
    }

    private byte[]? ToBgra(IntPtr p, int length)
    {
        int w = _width, h = _height, stride = _stride;
        if (w <= 0 || h <= 0 || length < stride * h * 3 / 2)
            return null;
        var src = new byte[length];
        Marshal.Copy(p, src, 0, length);
        // The decoder may pad rows to a multiple of 16; the chroma plane starts after the padded luma plane.
        var lumaRows = length * 2 / 3 / stride;
        if (lumaRows < h)
            lumaRows = h;
        var uvBase = stride * lumaRows;
        if (uvBase + stride * (h / 2) > length)
            uvBase = stride * h;
        var dst = new byte[w * h * 4];
        long sum = 0, sumSq = 0, count = 0;
        for (var y = 0; y < h; y++)
        {
            var yRow = y * stride;
            var uvRow = uvBase + (y >> 1) * stride;
            var o = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                int Y = src[yRow + x];
                var ui = uvRow + (x & ~1);
                int U = src[ui] - 128, V = src[ui + 1] - 128;
                var c = (Y - 16) * 298;
                dst[o] = Clamp((c + 516 * U + 128) >> 8);
                dst[o + 1] = Clamp((c - 100 * U - 208 * V + 128) >> 8);
                dst[o + 2] = Clamp((c + 409 * V + 128) >> 8);
                dst[o + 3] = 255;
                o += 4;
                if (((x | y) & 15) == 0)
                {
                    sum += Y;
                    sumSq += Y * Y;
                    count++;
                }
            }
        }
        if (count > 0)
        {
            Mean = (double)sum / count;
            Spread = Math.Sqrt(Math.Max(0, (double)sumSq / count - Mean * Mean));
        }
        return dst;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch
        {
        }
        _transform.Dispose();
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
}
