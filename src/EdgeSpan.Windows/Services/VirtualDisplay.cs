using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// EdgeSpan virtual monitor, backed by the open-source IddCx "Virtual Display Driver"
/// (github.com/VirtualDrivers/Virtual-Display-Driver, MIT, signed upstream; hardware id Root\MttVDD).
/// When it is present, a handed-off window is moved onto that real (virtual) monitor and maximised, the whole
/// monitor is captured with Windows.Graphics.Capture, and tablet input is injected with SendInput at real
/// screen coordinates, so UWP apps, Explorer, menus and popups behave exactly as on a physical display.
/// </summary>
public static class VirtualDisplay
{
    /// <summary>dwExtraInfo stamped on our own SendInput events so the low-level hook lets them through.</summary>
    public static readonly nint InjectTag = 0x45534E31; // "ESN1"

    /// <summary>Tick of our last injected mouse event; raw input without a device inside this window is ours.</summary>
    public static long LastInjectAt;

    public readonly record struct Screen(nint Monitor, string Device, int Left, int Top, int Width, int Height);

    public static string DriverDir => Path.Combine(AppContext.BaseDirectory, "vdd");
    public static bool Bundled => File.Exists(Path.Combine(DriverDir, "MttVDD.inf"));

    private static long _checkedAt;
    private static Screen? _cached;

    /// <summary>The attached virtual monitor, or null when the driver is absent/disabled. Cached for 2 s.</summary>
    public static Screen? Find(bool fresh = false)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var now = Environment.TickCount64;
        if (!fresh && now - Interlocked.Read(ref _checkedAt) < 2000)
            return _cached;
        Screen? found = null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dev, 0))
                    break;
                if ((dev.StateFlags & 1) == 0) // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                    continue;
                if (!IsVdd(dev.DeviceString, dev.DeviceID))
                    continue;
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(dev.DeviceName, -1, ref mode))
                    continue;
                var rect = new RECT { Left = mode.dmPositionX, Top = mode.dmPositionY, Right = mode.dmPositionX + mode.dmPelsWidth, Bottom = mode.dmPositionY + mode.dmPelsHeight };
                var hmon = MonitorFromRect(ref rect, 0);
                if (hmon == 0)
                    continue;
                found = new Screen(hmon, dev.DeviceName, rect.Left, rect.Top, mode.dmPelsWidth, mode.dmPelsHeight);
                break;
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write("vdd find " + ex.Message);
        }
        _cached = found;
        Interlocked.Exchange(ref _checkedAt, now);
        return found;
    }

    private static bool IsVdd(string text, string id) =>
        text.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts the virtual monitor right of (or left of, matching Arrange) the main monitor, top-aligned, at the tablet resolution when that mode exists.</summary>
    public static void Arrange(bool leftOfMain, int wantW, int wantH)
    {
        var vdd = Find(fresh: true);
        if (vdd is not { } s)
            return;
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(s.Device, -1, ref mode))
            return;
        var mainW = GetSystemMetrics(0);
        var w = s.Width;
        var h = s.Height;
        if (wantW > 0 && wantH > 0 && (wantW != w || wantH != h) && HasMode(s.Device, wantW, wantH))
        {
            w = wantW;
            h = wantH;
        }
        var x = leftOfMain ? -w : mainW;
        if (s.Left == x && s.Top == 0 && w == s.Width && h == s.Height)
            return;
        mode.dmPositionX = x;
        mode.dmPositionY = 0;
        mode.dmPelsWidth = w;
        mode.dmPelsHeight = h;
        mode.dmFields = 0x20 | 0x80000 | 0x100000; // DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
        var r1 = ChangeDisplaySettingsEx(s.Device, ref mode, 0, 0x00000001 | 0x10000000, 0); // UPDATEREGISTRY | NORESET
        var r2 = ChangeDisplaySettingsEx(null, 0, 0, 0, 0);
        DiagLog.Write($"vdd arrange {s.Device} at {x},0 {w}x{h} result={r1}/{r2}");
        Find(fresh: true);
    }

    private static bool HasMode(string device, int w, int h)
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (var i = 0; EnumDisplaySettings(device, i, ref mode); i++)
            if (mode.dmPelsWidth == w && mode.dmPelsHeight == h)
                return true;
        return false;
    }

    /// <summary>Moves the window onto the virtual monitor and maximises it there. Returns the monitor or null.</summary>
    public static Screen? Place(nint hwnd)
    {
        if (Find() is not { } s)
            return null;
        if (IsIconic(hwnd) || IsZoomed(hwnd))
            ShowWindow(hwnd, 9);
        SetWindowPos(hwnd, 0, s.Left + 20, s.Top + 20, Math.Max(200, s.Width - 40), Math.Max(150, s.Height - 40), 0x0004 | 0x0010 | 0x0040);
        ShowWindow(hwnd, 3); // SW_MAXIMIZE on the monitor it now lives on
        DiagLog.Write($"vdd place hwnd=0x{hwnd:X} on {s.Device} {s.Left},{s.Top} {s.Width}x{s.Height}");
        return s;
    }

    /// <summary>Un-maximises a window that is maximised on the virtual monitor so it can be put back home.</summary>
    public static void Leave(nint hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0)
            return;
        if (Find() is { } s && MonitorFromWindow(hwnd, 0) == s.Monitor && IsZoomed(hwnd))
            ShowWindow(hwnd, 9);
    }

    // ---------------- input (real SendInput, absolute on the virtual desktop) ----------------

    public static void MouseMove(int x, int y) => SendMouse(x, y, 0, 0);

    public static void MouseButton(int x, int y, int button, bool down)
    {
        uint flag = button switch
        {
            2 => down ? 0x0008u : 0x0010u, // RIGHT
            3 => down ? 0x0020u : 0x0040u, // MIDDLE
            _ => down ? 0x0002u : 0x0004u  // LEFT
        };
        SendMouse(x, y, flag, 0);
    }

    public static void Wheel(int x, int y, int delta, bool horizontal) =>
        SendMouse(x, y, horizontal ? 0x01000u : 0x0800u, delta);

    private static POINT _home;
    private static bool _borrowed;
    private static int _held;
    private static Timer? _giveBack;

    /// <summary>
    /// Tablet input borrows the one Windows cursor. Remember where the PC user left it and put it back once the
    /// tablet has been idle (no button held) for a moment, so the PC mouse never "jumps" onto the virtual monitor.
    /// </summary>
    private static void Borrow(uint extra)
    {
        lock (typeof(VirtualDisplay))
        {
            if (!_borrowed && GetCursorPos(out var p))
            {
                _home = p;
                _borrowed = true;
            }
            if ((extra & 0x002A) != 0) _held++;
            if ((extra & 0x0054) != 0) _held = Math.Max(0, _held - 1);
            _giveBack ??= new Timer(_ => GiveBack());
            _giveBack.Change(_held > 0 ? Timeout.Infinite : 1200, Timeout.Infinite);
        }
    }

    private static void GiveBack()
    {
        lock (typeof(VirtualDisplay))
        {
            if (!_borrowed || _held > 0)
                return;
            _borrowed = false;
            SetCursorPos(_home.X, _home.Y);
            DiagLog.Write($"vdd cursor returned to {_home.X},{_home.Y}");
        }
    }

    private static void SendMouse(int x, int y, uint extra, int data)
    {
        Borrow(extra);
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = Math.Max(1, GetSystemMetrics(78) - 1);
        var vh = Math.Max(1, GetSystemMetrics(79) - 1);
        var input = new INPUT
        {
            type = 0,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - vx) * 65535.0 / vw),
                    dy = (int)Math.Round((y - vy) * 65535.0 / vh),
                    mouseData = data,
                    dwFlags = 0x0001 | 0x8000 | 0x4000 | extra, // MOVE | ABSOLUTE | VIRTUALDESK
                    dwExtraInfo = InjectTag
                }
            }
        };
        Volatile.Write(ref LastInjectAt, Environment.TickCount64);
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void Key(int vk, bool down, nint focus)
    {
        Focus(focus);
        var input = new INPUT
        {
            type = 1,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey((uint)vk, 0), dwFlags = (down ? 0u : 0x0002u) | (Extended(vk) ? 0x0001u : 0u), dwExtraInfo = InjectTag } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void Text(string text, nint focus)
    {
        Focus(focus);
        var list = new List<INPUT>();
        foreach (var ch in text)
        {
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004, dwExtraInfo = InjectTag } } });
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 | 0x0002, dwExtraInfo = InjectTag } } });
        }
        if (list.Count > 0)
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static void Focus(nint hwnd)
    {
        if (hwnd == 0 || GetForegroundWindow() == hwnd)
            return;
        // A synthetic ALT tap lets SetForegroundWindow succeed from a background process.
        var alt = new[]
        {
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0x12, dwExtraInfo = InjectTag } } },
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0x12, dwFlags = 0x0002, dwExtraInfo = InjectTag } } }
        };
        SendInput(2, alt, Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hwnd);
    }

    private static bool Extended(int vk) => vk is >= 0x21 and <= 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;

    // ---------------- install (elevated helper, only after the user clicks Install and accepts UAC) ----------------

    /// <summary>Starts "EdgeSpan.exe --install-vdd W H" elevated. Returns false when the user declined UAC or files are missing.</summary>
    public static bool RequestInstall(int width, int height, out string error)
    {
        error = "";
        if (!Bundled)
        {
            error = "driver files missing (" + DriverDir + ")";
            return false;
        }
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EdgeSpan.exe");
            var p = Process.Start(new ProcessStartInfo(exe, $"--install-vdd {width} {height}") { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(120000);
            var code = p?.ExitCode ?? -1;
            DiagLog.Write("vdd install exit " + code);
            Interlocked.Exchange(ref _checkedAt, 0);
            if (code != 0)
                error = "installer exit " + code;
            return code == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message; // 1223 = user cancelled UAC
            DiagLog.Write("vdd install start " + ex.Message);
            return false;
        }
    }

    /// <summary>Entry for the elevated helper process. Returns a process exit code.</summary>
    public static int RunInstaller(string[] args)
    {
        var log = new StringBuilder();
        try
        {
            var w = args.Length > 1 && int.TryParse(args[1], out var pw) ? pw : 1920;
            var h = args.Length > 2 && int.TryParse(args[2], out var ph) ? ph : 1200;
            var inf = Path.Combine(DriverDir, "MttVDD.inf");
            if (!File.Exists(inf))
                return 2;
            // 1) settings: one monitor, tablet resolution first.
            const string cfgDir = @"C:\VirtualDisplayDriver";
            Directory.CreateDirectory(cfgDir);
            var xml = File.ReadAllText(Path.Combine(DriverDir, "vdd_settings.xml"));
            xml = Regex.Replace(xml, @"<count>\s*\d+\s*</count>", "<count>1</count>");
            var res = $"<resolutions>\n        <resolution>\n            <width>{w}</width>\n            <height>{h}</height>\n            <refresh_rate>60</refresh_rate>\n        </resolution>";
            if (!xml.Contains($"<width>{w}</width>\n            <height>{h}</height>"))
                xml = xml.Replace("<resolutions>", res);
            File.WriteAllText(Path.Combine(cfgDir, "vdd_settings.xml"), xml);
            Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\MikeTheTech\VirtualDisplayDriver", "VDDPATH", cfgDir);
            // 2) driver package into the store.
            log.AppendLine(Run("pnputil.exe", $"/add-driver \"{inf}\" /install"));
            // 3) root-enumerated device (only once).
            if (!DevicePresent())
                log.AppendLine("create device " + CreateRootDevice());
            if (!UpdateDriverForPlugAndPlayDevices(0, @"Root\MttVDD", inf, 1, out var reboot))
                log.AppendLine("update driver error " + Marshal.GetLastWin32Error());
            else
                log.AppendLine("update driver ok reboot=" + reboot);
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            return 3;
        }
        finally
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "edgespan-vdd-install.log"), log.ToString()); } catch { }
        }
    }

    private static bool DevicePresent() =>
        Run("pnputil.exe", "/enum-devices /class Display").Contains("MttVDD", StringComparison.OrdinalIgnoreCase);

    private static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return $"{file} {args} -> {p.ExitCode}\n{text}";
    }

    private static string CreateRootDevice()
    {
        var cls = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        var set = SetupDiCreateDeviceInfoList(ref cls, 0);
        if (set == -1)
            return "list " + Marshal.GetLastWin32Error();
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfo(set, "Display", ref cls, null, 0, 1, ref data))
                return "info " + Marshal.GetLastWin32Error();
            var ids = Encoding.Unicode.GetBytes("Root\\MttVDD\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, ref data, 1, ids, ids.Length))
                return "hwid " + Marshal.GetLastWin32Error();
            if (!SetupDiCallClassInstaller(0x19, set, ref data))
                return "register " + Marshal.GetLastWin32Error();
            return "ok";
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    // ---------------- interop ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public nint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; public int pad1, pad2; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, nint hwnd, uint flags, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string? device, nint dm, nint hwnd, uint flags, nint param);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern nint SetupDiCreateDeviceInfoList(ref Guid cls, nint hwnd);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiCreateDeviceInfo(nint set, string name, ref Guid cls, string? desc, nint hwnd, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiSetDeviceRegistryProperty(nint set, ref SP_DEVINFO_DATA data, int prop, byte[] buf, int size);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(int fn, nint set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool UpdateDriverForPlugAndPlayDevices(nint hwnd, string hwid, string inf, int flags, out bool reboot);
}
