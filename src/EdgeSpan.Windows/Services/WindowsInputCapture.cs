using System.Runtime.InteropServices;
using EdgeSpan.Input;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Low-level mouse and keyboard hooks plus raw mouse input.
/// The hook procedure only copies the event and returns. Session work, cursor
/// parking, and screen reads run on a worker so a stalled peer cannot freeze Windows.
/// A watchdog removes the hooks if the hook thread stays inside a callback.
/// </summary>
public sealed class WindowsInputCapture : IDisposable
{
    private const uint RidInputSink = 0x00000100;
    private const uint RidNoLegacy = 0x00000030;
    private const uint WmInput = 0x00FF;
    private const uint WmApp = 0x8000;
    private readonly HostController _host;
    private readonly InputMailbox _mail = new();
    private readonly object _releaseGate = new();
    private readonly Thread? _thread;
    private readonly Thread? _worker;
    private readonly Thread? _watchdog;
    private int _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private IntPtr _hwnd;
    private HookProc? _mouseProc;
    private HookProc? _keyboardProc;
    private WndProc? _wndProc;
    private int _rawSeen;
    private bool _cursorHidden;
    private bool _legacyBlocked;
    private int _parkX;
    private int _parkY;
    private long _lastScreenPoll;
    private int _buttonPackets;
    private int _hookDepth;
    private long _hookEnteredAt;
    private int _tripped;
    private readonly object _hookGate = new();
    private volatile bool _stop;
    private bool _desktopAway;

    public bool Running { get; private set; }

    public WindowsInputCapture(HostController host)
    {
        _host = host;
        if (!OperatingSystem.IsWindows())
            return;
        _thread = new Thread(Pump) { IsBackground = true, Name = "EdgeSpan.Input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _worker = new Thread(Work) { IsBackground = true, Name = "EdgeSpan.Input.Worker" };
        _watchdog = new Thread(Watch) { IsBackground = true, Name = "EdgeSpan.Input.Watch" };
        _thread.Start();
        _worker.Start();
        _watchdog.Start();
        Running = true;
    }

    public void Dispose()
    {
        _stop = true;
        _mail.Signal();
        if (_threadId != 0)
            PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(1));
        _worker?.Join(TimeSpan.FromSeconds(1));
        _watchdog?.Join(TimeSpan.FromSeconds(1));
        ShowCursorBalanced(false);
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        _wndProc = WindowProc;
        var cls = new WndClass { lpfnWndProc = _wndProc, lpszClassName = "EdgeSpanRawInput" };
        RegisterClass(ref cls);
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        SetTimer(_hwnd, 1, 400, IntPtr.Zero);
        WTSRegisterSessionNotification(_hwnd, 0);
        SetRawMode(false);
        _mouseProc = MouseHook;
        _keyboardProc = KeyboardHook;
        InstallHooks();
        PublishScreen(force: true);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WmApp)
                SetRawMode(msg.WParam != IntPtr.Zero);
            else if (msg.Message == WmApp + 2)
            {
                InstallHooks();
                Interlocked.Exchange(ref _tripped, 0);
                RecoverModifiers();
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookInstalled();
        if (_hwnd != IntPtr.Zero)
        {
            WTSUnRegisterSessionNotification(_hwnd);
            KillTimer(_hwnd, 1);
            DestroyWindow(_hwnd);
        }
    }

    private void InstallHooks()
    {
        lock (_hookGate)
        {
            var module = GetModuleHandle(null);
            if (_mouseHook == IntPtr.Zero)
                _mouseHook = SetWindowsHookEx(14, _mouseProc!, module, 0);
            if (_keyboardHook == IntPtr.Zero)
                _keyboardHook = SetWindowsHookEx(13, _keyboardProc!, module, 0);
            DiagLog.Write($"input hooks mouse={_mouseHook} key={_keyboardHook} hwnd={_hwnd}");
        }
    }

    private void UnhookInstalled()
    {
        IntPtr mouse;
        IntPtr key;
        lock (_hookGate)
        {
            mouse = _mouseHook;
            key = _keyboardHook;
            _mouseHook = IntPtr.Zero;
            _keyboardHook = IntPtr.Zero;
        }
        if (mouse != IntPtr.Zero)
            UnhookWindowsHookEx(mouse);
        if (key != IntPtr.Zero)
            UnhookWindowsHookEx(key);
    }

    private void Watch()
    {
        while (!_stop)
        {
            Thread.Sleep(20);
            var depth = Volatile.Read(ref _hookDepth);
            var entered = Volatile.Read(ref _hookEnteredAt);
            if (HookWatchdog.ShouldUnhook(depth, entered, Environment.TickCount64))
            {
                if (Interlocked.Exchange(ref _tripped, 1) == 0)
                {
                    UnhookInstalled();
                    DiagLog.Write("watchdog removed a stalled input hook");
                }
            }
            else if (Volatile.Read(ref _tripped) == 1 && HookWatchdog.ShouldRehook(true, depth) && _threadId != 0)
            {
                if (Interlocked.CompareExchange(ref _tripped, 2, 1) == 1)
                    PostThreadMessage(_threadId, WmApp + 2, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private void Work()
    {
        while (!_stop)
        {
            var batch = _mail.Take(32);
            if (!batch.Any)
            {
                _mail.Wait(15);
                continue;
            }
            try
            {
                ApplyBatch(batch);
            }
            catch (Exception ex)
            {
                DiagLog.Write("input worker " + ex.Message);
            }
        }
    }

    private void ApplyBatch(InputBatch batch)
    {
        if (batch.HasMove)
        {
            var had = _host.DeviceHasControl;
            _host.OnPhysicalMouse(batch.X, batch.Y, batch.Dx, batch.Dy);
            AfterPointer(had);
        }
        foreach (var note in batch.Notes)
        {
            switch (note.Kind)
            {
                case InputKind.Button:
                    DispatchHookButton(note.A, note.B);
                    break;
                case InputKind.RawButtons:
                    _buttonPackets++;
                    if (_buttonPackets == 1 || _buttonPackets % 25 == 0)
                        DiagLog.Write($"mouse flags=0x{note.A:X4} data={note.B} remote={_host.DeviceHasControl} n={_buttonPackets}");
                    DispatchButtons((ushort)note.A, (short)note.B);
                    break;
                case InputKind.Key:
                    if (note.SampleCursor && !_host.DeviceHasControl)
                        _host.NoteHostCursor(note.X, note.Y);
                    _host.OnKeyObserved(note.A, note.Down, note.Ctrl, note.Alt, note.Shift, note.Win,
                        new ModifierLedger.Decision(note.Hotkey || note.Forward, note.Forward, note.Hotkey));
                    PublishScreen(force: false);
                    break;
            }
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput)
            CopyRaw(lParam);
        else if (message == WmApp + 1)
            SetCursorPos(Volatile.Read(ref _parkX), Volatile.Read(ref _parkY));
        else if (message == 0x0113)
            WatchDesktop();
        else if (message == 0x02B1)
        {
            var reason = wParam.ToInt32();
            if (SecureDesktop.MarkAway(reason))
                _desktopAway = true;
            else if (SecureDesktop.Recover(reason))
                RecoverModifiers();
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void CopyRaw(IntPtr handle)
    {
        uint size = 0;
        var headerSize = Marshal.SizeOf<RawInputHeader>();
        GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, headerSize);
        if (size < (uint)headerSize || size > 1024)
            return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) == uint.MaxValue)
                return;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != 0)
                return;
            var mouseBytes = new byte[size - headerSize];
            Marshal.Copy(IntPtr.Add(buffer, headerSize), mouseBytes, 0, mouseBytes.Length);
            if (!RawMouseLayout.TryRead(mouseBytes, out var dx, out var dy, out var buttonFlags, out var buttonData))
                return;
            Volatile.Write(ref _rawSeen, 1);
            if (GetCursorPos(out var point))
                _mail.PostSample(point.X, point.Y, dx, dy);
            if (buttonFlags != 0)
                _mail.PostRawButtons(buttonFlags, buttonData);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        EnterHook();
        try
        {
            var data = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var msg = (int)wParam;
            var injected = (data.Flags & 0x01) != 0;
            var remote = _host.DeviceHasControl;
            var raw = Volatile.Read(ref _rawSeen) != 0;
            if (!remote)
            {
                if (!injected && !raw)
                {
                    if (msg == 0x0200)
                        _mail.PostAbsolute(data.X, data.Y);
                    else
                        _mail.PostButton(msg, data.MouseData);
                }
                return CallNextHookEx(_mouseHook, code, wParam, lParam);
            }
            if (injected && msg == 0x0200)
                return CallNextHookEx(_mouseHook, code, wParam, lParam);
            if (!raw && !injected)
            {
                if (msg == 0x0200)
                    _mail.PostAbsolute(data.X, data.Y);
                else
                    _mail.PostButton(msg, data.MouseData);
                if (_hwnd != IntPtr.Zero)
                    PostMessage(_hwnd, WmApp + 1, IntPtr.Zero, IntPtr.Zero);
            }
            if (msg is >= 0x0200 and <= 0x020E)
                return new IntPtr(1);
        }
        catch
        {
        }
        finally
        {
            LeaveHook();
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        EnterHook();
        try
        {
            var data = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
            if ((data.Flags & 0x10) != 0)
                return CallNextHookEx(_keyboardHook, code, wParam, lParam);
            var down = wParam is not ((nint)0x0101 or (nint)0x0105);
            var ctrl = Down(0x11);
            var alt = Down(0x12);
            var shift = Down(0x10);
            var win = Down(0x5B) || Down(0x5C);
            var sample = GetCursorPos(out var point) && !_host.DeviceHasControl;
            var decision = _host.PreviewKey(data.VkCode, down, ctrl, alt, shift, win);
            _mail.PostKey(data.VkCode, down, ctrl, alt, shift, win, sample, point.X, point.Y, decision.Forward, decision.Hotkey);
            return decision.Swallow
                ? new IntPtr(1)
                : CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }
        catch
        {
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }
        finally
        {
            LeaveHook();
        }
    }

    public void ReleaseStuckModifiers()
    {
        if (!OperatingSystem.IsWindows())
            return;
        List<int> down;
        lock (_releaseGate)
        {
            down = new List<int>();
            foreach (var vk in ModifierLedger.All)
            {
                if (Down(vk))
                    down.Add(vk);
            }
            if (down.Count == 0)
                return;
            var plan = ModifierReleasePlan.HostKeyUps(down);
            var inputs = new WinInput[plan.Length];
            for (var i = 0; i < plan.Length; i++)
            {
                var stroke = plan[i];
                var flags = 0u;
                if (!stroke.Down)
                    flags |= KeyUp;
                if (ModifierLedger.IsExtended(stroke.VirtualKey))
                    flags |= ExtendedKey;
                inputs[i] = new WinInput
                {
                    Type = 1,
                    Vk = (ushort)stroke.VirtualKey,
                    Flags = flags
                };
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WinInput>());
        }
        _host.NoteHostModifiersReleased(down);
    }

    private void RecoverModifiers()
    {
        ReleaseStuckModifiers();
        _host.ReleaseRemoteModifiers();
    }

    private void WatchDesktop()
    {
        var away = SecureDesktop.IsAway(InputDesktopName());
        if (SecureDesktop.Returned(_desktopAway, away))
            RecoverModifiers();
        _desktopAway = away;
    }

    private void EnterHook()
    {
        if (Interlocked.Increment(ref _hookDepth) == 1)
            Volatile.Write(ref _hookEnteredAt, Environment.TickCount64);
    }

    private void LeaveHook()
    {
        if (Interlocked.Decrement(ref _hookDepth) <= 0)
            Volatile.Write(ref _hookEnteredAt, 0);
    }

    private void DispatchHookButton(int msg, int mouseData)
    {
        switch (msg)
        {
            case 0x0201:
            case 0x0203:
                _host.OnMouseButton(0, true);
                break;
            case 0x0202:
                _host.OnMouseButton(0, false);
                break;
            case 0x0204:
            case 0x0206:
                _host.OnMouseButton(1, true);
                break;
            case 0x0205:
                _host.OnMouseButton(1, false);
                break;
            case 0x0207:
            case 0x0209:
                _host.OnMouseButton(2, true);
                break;
            case 0x0208:
                _host.OnMouseButton(2, false);
                break;
            case 0x020A:
                _host.OnScroll(0, (short)((mouseData >> 16) & 0xFFFF) / 120);
                break;
            case 0x020E:
                _host.OnScroll((short)((mouseData >> 16) & 0xFFFF) / 120, 0);
                break;
            case 0x020B:
            case 0x020D:
                _host.OnMouseButton((byte)(((mouseData >> 16) & 0xFFFF) == 2 ? 4 : 3), true);
                break;
            case 0x020C:
                _host.OnMouseButton((byte)(((mouseData >> 16) & 0xFFFF) == 2 ? 4 : 3), false);
                break;
        }
    }

    private void DispatchButtons(ushort flags, short data)
    {
        if ((flags & 0x0001) != 0) _host.OnMouseButton(0, true);
        if ((flags & 0x0002) != 0) _host.OnMouseButton(0, false);
        if ((flags & 0x0004) != 0) _host.OnMouseButton(1, true);
        if ((flags & 0x0008) != 0) _host.OnMouseButton(1, false);
        if ((flags & 0x0010) != 0) _host.OnMouseButton(2, true);
        if ((flags & 0x0020) != 0) _host.OnMouseButton(2, false);
        if ((flags & 0x0040) != 0) _host.OnMouseButton(3, true);
        if ((flags & 0x0080) != 0) _host.OnMouseButton(3, false);
        if ((flags & 0x0100) != 0) _host.OnMouseButton(4, true);
        if ((flags & 0x0200) != 0) _host.OnMouseButton(4, false);
        if ((flags & 0x0400) != 0) _host.OnScroll(0, data / 120);
        if ((flags & 0x0800) != 0) _host.OnScroll(data / 120, 0);
    }

    private void AfterPointer(bool hadControl)
    {
        if (_host.DeviceHasControl)
        {
            var park = _host.CursorPark;
            Volatile.Write(ref _parkX, park.X);
            Volatile.Write(ref _parkY, park.Y);
            SetCursorPos(park.X, park.Y);
            ShowCursorBalanced(true);
            if (_threadId != 0)
                PostThreadMessage(_threadId, WmApp, new IntPtr(1), IntPtr.Zero);
        }
        else
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WmApp, IntPtr.Zero, IntPtr.Zero);
            if (hadControl)
            {
                var park = _host.CursorPark;
                Volatile.Write(ref _parkX, park.X);
                Volatile.Write(ref _parkY, park.Y);
                SetCursorPos(park.X, park.Y);
                ShowCursorBalanced(false);
            }
        }
        PublishScreen(force: false);
    }

    private void SetRawMode(bool blockLegacy)
    {
        if (_hwnd == IntPtr.Zero || _legacyBlocked == blockLegacy)
            return;
        _legacyBlocked = blockLegacy;
        var device = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x02,
            Flags = RidInputSink | (blockLegacy ? RidNoLegacy : 0),
            Target = _hwnd
        };
        var ok = RegisterRawInputDevices(new[] { device }, 1, Marshal.SizeOf<RawInputDevice>());
        if (!ok)
            DiagLog.Write("raw mouse register failed legacy=" + blockLegacy);
    }

    public void ApplyControl(bool deviceHasControl)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var park = _host.CursorPark;
        Volatile.Write(ref _parkX, park.X);
        Volatile.Write(ref _parkY, park.Y);
        SetCursorPos(park.X, park.Y);
        ShowCursorBalanced(deviceHasControl);
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmApp, (IntPtr)(deviceHasControl ? 1 : 0), IntPtr.Zero);
    }

    private void ShowCursorBalanced(bool hide)
    {
        lock (this)
        {
            if (hide == _cursorHidden)
            {
                if (!hide)
                    SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512)));
                return;
            }
            ShowCursor(!hide);
            _cursorHidden = hide;
            if (!hide)
                SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512)));
        }
    }

    private void PublishScreen(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastScreenPoll < 2000)
            return;
        _lastScreenPoll = now;
        var x = GetSystemMetrics(76);
        var y = GetSystemMetrics(77);
        var w = GetSystemMetrics(78);
        var h = GetSystemMetrics(79);
        if (w > 0 && h > 0)
            _host.SetHostScreen(x, y, w, h);
    }

    private static bool Down(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static string? InputDesktopName()
    {
        var desk = OpenInputDesktop(0, false, 0x0001);
        if (desk == IntPtr.Zero)
            return null;
        try
        {
            GetUserObjectInformation(desk, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 2)
                return null;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetUserObjectInformation(desk, 2, buf, needed, out _))
                    return null;
                return Marshal.PtrToStringUni(buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            CloseDesktop(desk);
        }
    }

    private const uint KeyUp = 0x0002;
    private const uint ExtendedKey = 0x0001;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct WinInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public IntPtr Extra;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public int X;
        public int Y;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public int Type;
        public int Size;
        public IntPtr Device;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public WndProc? lpfnWndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string? lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string? cls, string? name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, WinInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, IntPtr info, int length, out int needed);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, int size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, int headerSize);
}
