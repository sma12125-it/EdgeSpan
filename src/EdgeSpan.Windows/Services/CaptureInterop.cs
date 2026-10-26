using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Windows.Graphics.Capture and the WinRT Direct3D device are CsWinRT projections.
/// CreateForWindow and CreateDirect3D11DeviceFromDXGIDevice return a COM pointer
/// that must be wrapped with FromAbi. A cast of System.__ComObject throws.
/// </summary>
public static class CaptureInterop
{
    public static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid FactoryIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    public static GraphicsCaptureItem? ItemFromAbi(IntPtr pointer) =>
        pointer == IntPtr.Zero ? null : GraphicsCaptureItem.FromAbi(pointer);

    public static IDirect3DDevice? DeviceFromAbi(IntPtr pointer) =>
        pointer == IntPtr.Zero ? null : MarshalInterface<IDirect3DDevice>.FromAbi(pointer);

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var factoryIid = FactoryIid;
        var itemIid = ItemIid;
        Must(WindowsCreateString(className, className.Length, out var name));
        try
        {
            Must(RoGetActivationFactory(name, ref factoryIid, out var factoryPtr));
            var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            Must(factory.CreateForWindow(hwnd, ref itemIid, out var itemPtr));
            try
            {
                var item = ItemFromAbi(itemPtr);
                if (item == null)
                    throw new InvalidOperationException("handoffNoEncoder");
                return item;
            }
            finally
            {
                if (itemPtr != IntPtr.Zero)
                    Marshal.Release(itemPtr);
            }
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    public static IDirect3DDevice CreateDevice(IntPtr dxgiDevice)
    {
        Must(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var punk));
        try
        {
            var device = DeviceFromAbi(punk);
            if (device == null)
                throw new InvalidOperationException("handoffNoEncoder");
            return device;
        }
        finally
        {
            if (punk != IntPtr.Zero)
                Marshal.Release(punk);
        }
    }

    private static void Must(int hr)
    {
        if (hr < 0)
            throw new InvalidOperationException("0x" + hr.ToString("X8")) { HResult = hr };
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
