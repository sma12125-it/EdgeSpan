using System.Runtime.InteropServices;
using EdgeSpan.Windows.Services;

namespace EdgeSpan.Windows.Tests;

public class CaptureInteropTests
{
    [Fact]
    public void FromAbi_of_a_null_pointer_is_null()
    {
        Assert.Equal(new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"), CaptureInterop.ItemIid);
        Assert.Null(CaptureInterop.ItemFromAbi(IntPtr.Zero));
        Assert.Null(CaptureInterop.DeviceFromAbi(IntPtr.Zero));
    }

    [Fact]
    public void CreateForWindow_uses_FromAbi_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        IntPtr hwnd;
        try
        {
            hwnd = GetDesktopWindow();
        }
        catch (DllNotFoundException)
        {
            return;
        }
        if (hwnd == IntPtr.Zero)
            return;
        var item = CaptureInterop.CreateItemForWindow(hwnd);
        Assert.NotNull(item);
        Assert.True(item.Size.Width >= 0);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
