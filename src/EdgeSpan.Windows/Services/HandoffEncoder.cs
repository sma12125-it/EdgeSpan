using System.Runtime.InteropServices;
using EdgeSpan.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using SharpGen.Runtime;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Captures one HWND on the GPU and encodes H.264. Hardware (NVENC, Quick Sync, AMF)
/// is first. A CPU color convert or the inbox software encoder is a labeled fallback.
/// An unchanged frame is not sent.
/// </summary>
internal static class WindowEncoder
{
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid Gop = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid MeanBitrate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid RateMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid ForceKey = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid SoftwareEncoder = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid TransformIid = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);

    public static void Run(nint hwnd, string? quality, Func<bool> stop, Func<bool> takeIdr, Action<byte[], ulong, byte> publish, Action<string> report)
    {
        var sent = 0;
        try
        {
            Loop(hwnd, quality, stop, takeIdr, (bytes, us, flags) =>
            {
                sent++;
                publish(bytes, us, flags);
            }, report);
        }
        catch (Exception ex)
        {
            Stage("run", ex);
            if (sent == 0)
                report("handoffNoEncoder");
        }
    }

    private static void Loop(nint hwnd, string? quality, Func<bool> stop, Func<bool> takeIdr, Action<byte[], ulong, byte> publish, Action<string> report)
    {
        ID3D11Device device;
        try
        {
            device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, new[] { FeatureLevel.Level_11_0 });
            DiagLog.Write("handoff stage D3D11CreateDevice ok");
        }
        catch (Exception ex)
        {
            Stage("D3D11CreateDevice", ex);
            report("handoffNoEncoder");
            return;
        }
        using (device)
        {
            var context = device.ImmediateContext;
            GraphicsCaptureItem item;
            try
            {
                item = CaptureInterop.CreateItemForWindow(hwnd);
                DiagLog.Write($"handoff stage GraphicsCaptureItem {item.Size.Width}x{item.Size.Height}");
            }
            catch (Exception ex)
            {
                Stage("GraphicsCaptureItem", ex);
                report("handoffNoEncoder");
                return;
            }
            IDirect3DDevice winrt;
            try
            {
                winrt = WinrtDevice(device);
                DiagLog.Write("handoff stage WinrtDevice ok");
            }
            catch (Exception ex)
            {
                Stage("WinrtDevice", ex);
                report("handoffNoEncoder");
                return;
            }
            var size = item.Size;
            Direct3D11CaptureFramePool pool;
            GraphicsCaptureSession session;
            try
            {
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                session = pool.CreateCaptureSession(item);
                DiagLog.Write($"handoff stage framePool {size.Width}x{size.Height}");
            }
            catch (Exception ex)
            {
                Stage("framePool", ex);
                report("handoffNoEncoder");
                if (winrt is IDisposable broken)
                    broken.Dispose();
                return;
            }
            var mf = false;
            try
            {
                var started = MediaFactory.MFStartup(true);
                DiagLog.Write("handoff stage MFStartup " + started);
                mf = started.Success;
                if (!mf)
                    DiagLog.Write("handoff stage MFStartup failed, jpeg only");
            }
            catch (Exception ex)
            {
                Stage("MFStartup", ex);
            }
            session.IsCursorCaptureEnabled = false;
            var border = session.GetType().GetProperty("IsBorderRequired");
            border?.SetValue(session, false);
            var arrived = new ManualResetEventSlim(false);
            pool.FrameArrived += (_, _) => arrived.Set();
            session.StartCapture();
            try
            {
                Pump(device, context, pool, quality, stop, takeIdr, publish, report, arrived, mf);
            }
            finally
            {
                session.Dispose();
                pool.Dispose();
                if (winrt is IDisposable disposable)
                    disposable.Dispose();
                arrived.Dispose();
            }
        }
    }

    private static void Pump(ID3D11Device device, ID3D11DeviceContext context, Direct3D11CaptureFramePool pool, string? quality, Func<bool> stop, Func<bool> takeIdr, Action<byte[], ulong, byte> publish, Action<string> report, ManualResetEventSlim arrived, bool allowH264)
    {
        Encoder? encoder = null;
        var black = 0;
        var sinceIdr = 0;
        var reported = false;
        var useJpeg = !allowH264;
        var jpegHash = 0;
        var jpegSent = false;
        var sawFrame = false;
        var loggedTexture = false;
        while (!stop())
        {
            if (!arrived.Wait(12))
                continue;
            arrived.Reset();
            using var frame = pool.TryGetNextFrame();
            if (frame == null)
                continue;
            if (!sawFrame)
            {
                sawFrame = true;
                DiagLog.Write($"handoff stage firstFrame {frame.ContentSize.Width}x{frame.ContentSize.Height}");
            }
            var size = frame.ContentSize;
            var (width, height) = EdgeSpan.Layout.HandoffPolicy.EncodeSize(size.Width, size.Height, quality);
            if (width < 2 || height < 2)
                continue;
            var texture = TextureOf(frame.Surface);
            if (texture == null)
            {
                if (!loggedTexture)
                {
                    loggedTexture = true;
                    DiagLog.Write("handoff stage texture null");
                }
                continue;
            }
            using (texture)
            {
                if (!useJpeg && (encoder == null || encoder.Width != width || encoder.Height != height || encoder.Failed))
                {
                    encoder?.Dispose();
                    encoder = null;
                    var bitrate = EdgeSpan.Layout.HandoffPolicy.Bitrate(width, height, quality);
                    var fps = EdgeSpan.Layout.HandoffPolicy.FrameRate(quality);
                    encoder = Encoder.Open(device, width, height, bitrate, fps, out var path);
                    if (encoder == null)
                    {
                        useJpeg = true;
                        DiagLog.Write("handoff stage encoder none, jpeg fallback");
                    }
                    else if (!reported)
                    {
                        report(path);
                        reported = true;
                    }
                    sinceIdr = 0;
                }
                var stamp = Sample(context, texture, width, height, out var hash, out var dark);
                if (stamp == null)
                    continue;
                if (dark)
                {
                    if (++black >= 45)
                    {
                        report("handoffBlack");
                        return;
                    }
                }
                else
                    black = 0;
                if (useJpeg || encoder == null || encoder.Failed)
                {
                    if (encoder is { Failed: true })
                    {
                        encoder.Dispose();
                        encoder = null;
                        useJpeg = true;
                        DiagLog.Write("handoff stage h264 failed, jpeg fallback");
                    }
                    if (hash == jpegHash && jpegSent)
                        continue;
                    var (jw, jh) = JpegSize(width, height);
                    var bgra = ReadScaled(context, texture, jw, jh);
                    if (bgra == null)
                        continue;
                    byte[] jpeg;
                    try
                    {
                        jpeg = JpegStill.Encode(bgra, jw, jh, jw * 4);
                    }
                    catch (Exception ex)
                    {
                        Stage("jpeg", ex);
                        report("handoffNoEncoder");
                        return;
                    }
                    jpegHash = hash;
                    jpegSent = true;
                    if (!reported)
                    {
                        report("handoffJpeg");
                        reported = true;
                    }
                    publish(jpeg, (ulong)StopwatchMicros(), JpegStill.Flag);
                    continue;
                }
                if (hash == encoder.LastHash && sinceIdr > 0)
                    continue;
                encoder.LastHash = hash;
                var idr = sinceIdr == 0 || sinceIdr >= encoder.Fps || takeIdr();
                var annex = encoder.Encode(context, texture, stamp, idr);
                if (encoder.Failed || annex == null)
                {
                    if (encoder.Failed)
                    {
                        encoder.Dispose();
                        encoder = null;
                        useJpeg = true;
                    }
                    continue;
                }
                sinceIdr = idr ? 1 : sinceIdr + 1;
                publish(annex, (ulong)StopwatchMicros(), idr ? (byte)1 : (byte)0);
            }
        }
        encoder?.Dispose();
    }

    private static (int Width, int Height) JpegSize(int width, int height)
    {
        width &= ~1;
        height &= ~1;
        if (Math.Max(width, height) <= 960)
            return (Math.Max(2, width), Math.Max(2, height));
        if (width >= height)
            return (960, Math.Max(2, height * 960 / width) & ~1);
        return (Math.Max(2, width * 960 / height) & ~1, 960);
    }

    private static byte[]? ReadScaled(ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height)
    {
        var desc = source.Description;
        var srcW = (int)desc.Width;
        var srcH = (int)desc.Height;
        if (srcW < 2 || srcH < 2)
            return null;
        var stagingDesc = desc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDesc.MiscFlags = ResourceOptionFlags.None;
        using var staging = source.Device.CreateTexture2D(ref stagingDesc);
        context.CopyResource(staging, source);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var row = (int)mapped.RowPitch;
            var src = new byte[row * srcH];
            Marshal.Copy(mapped.DataPointer, src, 0, src.Length);
            var tight = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var sy = Math.Clamp(y * srcH / height, 0, srcH - 1);
                for (var x = 0; x < width; x++)
                {
                    var sx = Math.Clamp(x * srcW / width, 0, srcW - 1);
                    var from = sy * row + sx * 4;
                    var to = (y * width + x) * 4;
                    if (from + 3 >= src.Length)
                        continue;
                    tight[to] = src[from];
                    tight[to + 1] = src[from + 1];
                    tight[to + 2] = src[from + 2];
                    tight[to + 3] = src[from + 3];
                }
            }
            return tight;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static void Stage(string name, Exception ex)
    {
        var hr = ex.HResult;
        DiagLog.Write($"handoff stage {name} 0x{hr:X8} {ex.GetType().Name} {ex.Message}");
    }

    private static long StopwatchMicros() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency);

    private static IDirect3DDevice WinrtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        return CaptureInterop.CreateDevice(dxgi.NativePointer);
    }

    private static ID3D11Texture2D? TextureOf(IDirect3DSurface surface)
    {
        var unknown = Marshal.GetIUnknownForObject(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unknown);
            var iid = Texture2DIid;
            if (access.GetInterface(ref iid, out var ptr) < 0 || ptr == IntPtr.Zero)
                return null;
            return new ID3D11Texture2D(ptr);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static byte[]? Sample(ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height, out int hash, out bool dark)
    {
        hash = 0;
        dark = false;
        var device = source.Device;
        var desc = new Texture2DDescription
        {
            Width = 8,
            Height = 8,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        };
        using var staging = device.CreateTexture2D(ref desc);
        var x = Math.Clamp(width / 2, 0, Math.Max(0, width - 8));
        var y = Math.Clamp(height / 2, 0, Math.Max(0, height - 8));
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, new Box(x, y, 0, x + 8, y + 8, 1));
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var row = (int)mapped.RowPitch;
            var bytes = new byte[row * 8];
            Marshal.Copy(mapped.DataPointer, bytes, 0, bytes.Length);
            var luma = 0;
            var n = 0;
            for (var rowIndex = 0; rowIndex < 8; rowIndex++)
            {
                for (var col = 0; col < 8; col++)
                {
                    var i = rowIndex * row + col * 4;
                    luma += bytes[i] + bytes[i + 1] + bytes[i + 2];
                    n++;
                    hash = unchecked(hash * 16777619) ^ bytes[i] ^ (bytes[i + 1] << 8) ^ (bytes[i + 2] << 16);
                }
            }
            dark = n > 0 && luma / n < 12;
            return bytes;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private sealed class Encoder : IDisposable
    {
        public int Width;
        public int Height;
        public int Fps;
        public int LastHash;
        public bool Failed;
        private readonly IMFTransform _transform;
        private readonly bool _memory;
        private readonly ID3D11VideoDevice? _videoDevice;
        private readonly ID3D11VideoContext? _videoContext;
        private readonly ID3D11VideoProcessorEnumerator? _enumerator;
        private readonly ID3D11VideoProcessor? _processor;
        private ID3D11Texture2D? _nv12;
        private long _time;
        private readonly long _duration;

        private Encoder(IMFTransform transform, bool memory, int width, int height, int fps, ID3D11VideoDevice? videoDevice, ID3D11VideoContext? videoContext, ID3D11VideoProcessorEnumerator? enumerator, ID3D11VideoProcessor? processor)
        {
            _transform = transform;
            _memory = memory;
            Width = width;
            Height = height;
            Fps = fps;
            _duration = 10_000_000L / Math.Max(1, fps);
            _videoDevice = videoDevice;
            _videoContext = videoContext;
            _enumerator = enumerator;
            _processor = processor;
        }

        public static Encoder? Open(ID3D11Device device, int width, int height, int bitrate, int fps, out string path)
        {
            path = "handoffNoEncoder";
            ListMfts(hardware: true);
            ListMfts(hardware: false);
            var gpu = TryHardware(device, width, height, bitrate, fps, gpuInput: true);
            if (gpu != null)
            {
                path = gpu.Value.Path;
                return gpu.Value.Encoder;
            }
            var cpuHardware = TryHardware(device, width, height, bitrate, fps, gpuInput: false);
            if (cpuHardware != null)
            {
                path = "handoffCpu";
                return cpuHardware.Value.Encoder;
            }
            var software = TrySoftware(width, height, bitrate, fps);
            if (software == null)
                return null;
            path = "handoffSw";
            return software;
        }

        public byte[]? Encode(ID3D11DeviceContext context, ID3D11Texture2D source, byte[]? sample, bool idr)
        {
            IMFSample input;
            if (_memory)
            {
                var nv12 = BgraToNv12(ReadBgra(context, source), Width, Height);
                input = MemorySample(nv12);
            }
            else
            {
                var converted = Convert(context, source);
                if (converted == null)
                    return null;
                input = SurfaceSample(converted);
            }
            input.SampleTime = _time;
            input.SampleDuration = _duration;
            _time += _duration;
            if (idr)
                input.Set(ForceKey, 1u);
            try
            {
                _transform.ProcessInput(0, input, 0);
            }
            catch (SharpGenException ex) when (ex.HResult == NeedMoreInput)
            {
                return null;
            }
            catch (Exception ex)
            {
                Stage("ProcessInput", ex);
                Failed = true;
                return null;
            }
            return Drain();
        }

        private ID3D11Texture2D? Convert(ID3D11DeviceContext context, ID3D11Texture2D source)
        {
            if (_videoDevice == null || _videoContext == null || _enumerator == null || _processor == null)
                return null;
            _nv12 ??= CreateNv12(source.Device, Width, Height);
            var inDesc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0 }
            };
            var outDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
            };
            using var input = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, inDesc);
            using var output = _videoDevice.CreateVideoProcessorOutputView(_nv12, _enumerator, outDesc);
            var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
            _videoContext.VideoProcessorBlt(_processor, output, 0, 1, new[] { stream });
            return _nv12;
        }

        public void Dispose()
        {
            _nv12?.Dispose();
            _processor?.Dispose();
            _enumerator?.Dispose();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();
            _transform.Dispose();
        }

        private byte[]? Drain()
        {
            var info = _transform.GetOutputStreamInfo(0);
            var sample = MediaFactory.MFCreateSample();
            var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, Width * Height));
            sample.AddBuffer(buffer);
            var data = new OutputDataBuffer { Sample = sample };
            ProcessOutputStatus status;
            try
            {
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out status);
                if (result.Failure && result.Code != NeedMoreInput)
                {
                    DiagLog.Write("handoff stage ProcessOutput 0x" + result.Code.ToString("X8"));
                    Failed = true;
                    return null;
                }
                if (result.Failure)
                    return null;
            }
            catch (SharpGenException ex) when (ex.HResult == NeedMoreInput)
            {
                return null;
            }
            catch (Exception ex)
            {
                Stage("ProcessOutput", ex);
                Failed = true;
                return null;
            }
            using var contig = sample.ConvertToContiguousBuffer();
            contig.Lock(out var ptr, out _, out var length);
            try
            {
                var raw = new byte[Math.Max(0, length)];
                if (raw.Length > 0)
                    Marshal.Copy(ptr, raw, 0, raw.Length);
                return ToAnnexB(raw);
            }
            finally
            {
                contig.Unlock();
            }
        }

        private static (Encoder Encoder, string Path)? TryHardware(ID3D11Device device, int width, int height, int bitrate, int fps, bool gpuInput)
        {
            var inputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Nv12 };
            var outputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = H264 };
            var flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, inputInfo, outputInfo, out var activates, out var count);
            try
            {
                var ranked = new List<(IMFActivate Activate, int Rank, string Name)>();
                for (var i = 0; i < count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                    var activate = new IMFActivate(ptr);
                    var name = Friendly(activate);
                    ranked.Add((activate, Rank(name), name));
                }
                foreach (var item in ranked.OrderBy(item => item.Rank))
                {
                    var opened = Activate(item.Activate, device, width, height, bitrate, fps, gpuInput, HardwareKey(item.Name));
                    if (opened != null)
                        return opened;
                }
                return null;
            }
            finally
            {
                if (activates != IntPtr.Zero)
                    CoTaskMemFree(activates);
            }
        }

        private static Encoder? TrySoftware(int width, int height, int bitrate, int fps)
        {
            var iid = TransformIid;
            var clsid = SoftwareEncoder;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var punk);
            DiagLog.Write("handoff stage CoCreate CLSID_MSH264EncoderMFT 0x" + hr.ToString("X8"));
            if (hr < 0)
                return null;
            var transform = new IMFTransform(punk);
            UnlockAsync(transform, "handoffSw");
            if (!Configure(transform, width, height, bitrate, fps))
            {
                transform.Dispose();
                return null;
            }
            return new Encoder(transform, memory: true, width, height, fps, null, null, null, null);
        }

        private static void ListMfts(bool hardware)
        {
            try
            {
                var inputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Nv12 };
                var outputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = H264 };
                var flags = hardware
                    ? (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter)
                    : (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
                MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, inputInfo, outputInfo, out var activates, out var count);
                try
                {
                    var names = new List<string>();
                    for (var i = 0; i < count; i++)
                    {
                        var ptr = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                        var activate = new IMFActivate(ptr);
                        var name = Friendly(activate);
                        names.Add(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name);
                    }
                    DiagLog.Write($"handoff stage MFTEnumEx {(hardware ? "hardware" : "software")} count={count} {string.Join(" | ", names)}");
                }
                finally
                {
                    if (activates != IntPtr.Zero)
                        CoTaskMemFree(activates);
                }
            }
            catch (Exception ex)
            {
                Stage(hardware ? "MFTEnumEx hardware" : "MFTEnumEx software", ex);
            }
        }

        private static void UnlockAsync(IMFTransform transform, string path)
        {
            try
            {
                using var attrs = transform.QueryInterface<IMFAttributes>();
                var got = attrs.GetUInt32(TransformAttributeKeys.TransformAsync, out var asyncFlag);
                if (got.Failure || asyncFlag == 0)
                    return;
                attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                DiagLog.Write("handoff stage asyncUnlock " + path);
            }
            catch (Exception ex)
            {
                Stage("asyncUnlock", ex);
            }
        }

        private static (Encoder Encoder, string Path)? Activate(IMFActivate activate, ID3D11Device device, int width, int height, int bitrate, int fps, bool gpuInput, string path)
        {
            var transform = activate.ActivateObject<IMFTransform>();
            ID3D11VideoDevice? videoDevice = null;
            ID3D11VideoContext? videoContext = null;
            ID3D11VideoProcessorEnumerator? enumerator = null;
            ID3D11VideoProcessor? processor = null;
            UnlockAsync(transform, path);
            if (gpuInput)
            {
                try
                {
                    using var manager = CreateManager(device);
                    transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)manager.NativePointer);
                    videoDevice = device.QueryInterface<ID3D11VideoDevice>();
                    videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();
                    var content = new VideoProcessorContentDescription
                    {
                        InputFrameFormat = VideoFrameFormat.Progressive,
                        InputWidth = (uint)width,
                        InputHeight = (uint)height,
                        OutputWidth = (uint)width,
                        OutputHeight = (uint)height,
                        InputFrameRate = new Rational((uint)fps, 1),
                        OutputFrameRate = new Rational((uint)fps, 1),
                        Usage = VideoUsage.PlaybackNormal
                    };
                    enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
                    processor = videoDevice.CreateVideoProcessor(enumerator, 0);
                }
                catch (Exception ex)
                {
                    Stage("d3dManager", ex);
                    transform.Dispose();
                    return null;
                }
            }
            if (!Configure(transform, width, height, bitrate, fps))
            {
                transform.Dispose();
                processor?.Dispose();
                enumerator?.Dispose();
                videoContext?.Dispose();
                videoDevice?.Dispose();
                return null;
            }
            return (new Encoder(transform, memory: !gpuInput, width, height, fps, videoDevice, videoContext, enumerator, processor), path);
        }

        private static bool Configure(IMFTransform transform, int width, int height, int bitrate, int fps)
        {
            try
            {
                var output = MediaType(H264, width, height, fps);
                output.Set(MeanBitrate, (uint)bitrate);
                output.Set(RateMode, 0u);
                output.Set(Gop, (uint)fps);
                output.Set(BFrames, 0u);
                output.Set(LowLatency, 1u);
                var input = MediaType(Nv12, width, height, fps);
                DiagLog.Write($"handoff stage mediaType NV12 {width}x{height} {bitrate}bps {fps}fps");
                transform.SetOutputType(0, output, 0);
                transform.SetInputType(0, input, 0);
                transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                Stage("mediaType", ex);
                return false;
            }
        }

        private static IMFDXGIDeviceManager CreateManager(ID3D11Device device)
        {
            var manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device);
            return manager;
        }

        private static IMFMediaType MediaType(Guid subtype, int width, int height, int fps)
        {
            var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, subtype);
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            return type;
        }

        private static ID3D11Texture2D CreateNv12(ID3D11Device device, int width, int height)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            };
            return device.CreateTexture2D(ref desc);
        }

        private static IMFSample SurfaceSample(ID3D11Texture2D texture)
        {
            var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(Texture2DIid, texture, 0, false);
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            return sample;
        }

        private static IMFSample MemorySample(byte[] nv12)
        {
            var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
            buffer.Lock(out var ptr, out _, out _);
            try
            {
                Marshal.Copy(nv12, 0, ptr, nv12.Length);
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.CurrentLength = nv12.Length;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            return sample;
        }

        private static byte[] ReadBgra(ID3D11DeviceContext context, ID3D11Texture2D source)
        {
            var desc = source.Description;
            var stagingDesc = desc;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.BindFlags = BindFlags.None;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
            stagingDesc.MiscFlags = ResourceOptionFlags.None;
            using var staging = source.Device.CreateTexture2D(ref stagingDesc);
            context.CopyResource(staging, source);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = new byte[mapped.RowPitch * desc.Height];
                Marshal.Copy(mapped.DataPointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }

        private static byte[] BgraToNv12(byte[] bgra, int width, int height)
        {
            var stride = bgra.Length / Math.Max(1, height);
            var ySize = width * height;
            var nv = new byte[ySize + ySize / 2];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = y * stride + x * 4;
                    if (i + 2 >= bgra.Length)
                        break;
                    var b = bgra[i];
                    var g = bgra[i + 1];
                    var r = bgra[i + 2];
                    nv[y * width + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                }
            }
            var uv = ySize;
            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    var i = y * stride + x * 4;
                    if (i + 2 >= bgra.Length)
                        break;
                    var b = bgra[i];
                    var g = bgra[i + 1];
                    var r = bgra[i + 2];
                    var u = Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    var v = Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                    if (uv + 1 < nv.Length)
                    {
                        nv[uv++] = (byte)u;
                        nv[uv++] = (byte)v;
                    }
                }
            }
            return nv;
        }
    }

    private static byte[] ToAnnexB(byte[] src)
    {
        if (src.Length >= 4 && src[0] == 0 && src[1] == 0 && (src[2] == 1 || (src[2] == 0 && src[3] == 1)))
            return src;
        using var stream = new MemoryStream();
        var offset = 0;
        var start = new byte[] { 0, 0, 0, 1 };
        while (offset + 4 <= src.Length)
        {
            var length = (src[offset] << 24) | (src[offset + 1] << 16) | (src[offset + 2] << 8) | src[offset + 3];
            offset += 4;
            if (length <= 0 || offset + length > src.Length)
                return src;
            stream.Write(start);
            stream.Write(src, offset, length);
            offset += length;
        }
        return stream.Length == 0 ? src : stream.ToArray();
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private static string Friendly(IMFActivate activate)
    {
        try
        {
            return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch
        {
            return "";
        }
    }

    private static void Must(int hr)
    {
        if (hr < 0)
            throw new InvalidOperationException(hr.ToString("X8"));
    }

    private static int Rank(string name)
    {
        var text = name.ToLowerInvariant();
        if (text.Contains("nvidia") || text.Contains("nvenc"))
            return 0;
        if (text.Contains("quick sync") || text.Contains("intel"))
            return 1;
        if (text.Contains("amd") || text.Contains("amf"))
            return 2;
        return 3;
    }

    private static string HardwareKey(string name)
    {
        var text = name.ToLowerInvariant();
        if (text.Contains("nvidia") || text.Contains("nvenc") || text.Contains("quick sync") || text.Contains("intel") || text.Contains("amd") || text.Contains("amf") || text.Length > 0)
            return "handoffHw";
        return "handoffHw";
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(ref Guid iid, out IntPtr result);
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);
}
