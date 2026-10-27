using System.Runtime.InteropServices;
using EdgeSpan.Input;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Emergency "panic" key. Registered with RegisterHotKey on its own thread and message loop, independent of the
/// UI thread and of the low-level hook thread, so it still fires when either is hung. (If the hook thread hangs,
/// Windows times the hook out and the chord reaches RegisterHotKey; if the hook is healthy it swallows the chord
/// while the pointer is on a device and reports it through <see cref="Session.HostController.PanicHotkey"/>.)
/// A second watchdog polls the physical key state, so even a lost registration still triggers.
/// </summary>
internal sealed class PanicGuard : IDisposable
{
    private const int HotkeyId = 0x5E11;
    private const uint WmHotkey = 0x0312;
    private const uint WmRebind = 0x8000 + 0x51;
    private readonly Func<string> _chord;
    private readonly Action _panic;
    private readonly Thread _thread;
    private readonly Thread _poll;
    private volatile bool _stop;
    private uint _threadId;
    private long _lastFired;

    public PanicGuard(Func<string> chord, Action panic)
    {
        _chord = chord;
        _panic = panic;
        _thread = new Thread(Loop) { IsBackground = true, Name = "EdgeSpan.Panic", Priority = ThreadPriority.AboveNormal };
        _poll = new Thread(Poll) { IsBackground = true, Name = "EdgeSpan.Panic.Poll", Priority = ThreadPriority.AboveNormal };
        if (!OperatingSystem.IsWindows())
            return;
        _thread.Start();
        _poll.Start();
    }

    /// <summary>Re-registers after the chord changed in Settings.</summary>
    public void Rebind()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmRebind, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Runs the panic once (debounced to 1 s), from whichever path saw the chord first.</summary>
    public void Fire(string via)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFired) < 1000)
            return;
        Interlocked.Exchange(ref _lastFired, now);
        DiagLog.Write("PANIC hotkey pressed via " + via);
        try { _panic(); }
        catch (Exception ex) { DiagLog.Write("panic failed " + ex.Message); }
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        // Make sure the queue exists before anyone posts to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        Bind();
        while (!_stop && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WmHotkey && (int)msg.WParam == HotkeyId)
                ThreadPool.QueueUserWorkItem(_ => Fire("RegisterHotKey"));
            else if (msg.Message == WmRebind)
                Bind();
        }
        UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    private void Bind()
    {
        UnregisterHotKey(IntPtr.Zero, HotkeyId);
        var key = HotkeyParser.Parse(_chord());
        if (key.VirtualKey == 0)
            return;
        uint mods = 0x4000; // MOD_NOREPEAT
        if (key.Alt) mods |= 0x1;
        if (key.Ctrl) mods |= 0x2;
        if (key.Shift) mods |= 0x4;
        if (key.Win) mods |= 0x8;
        var ok = RegisterHotKey(IntPtr.Zero, HotkeyId, mods, (uint)key.VirtualKey);
        DiagLog.Write($"panic hotkey {_chord()} registered={ok}{(ok ? "" : " err=" + Marshal.GetLastWin32Error())}");
    }

    private void Poll()
    {
        var was = false;
        while (!_stop)
        {
            Thread.Sleep(40);
            try
            {
                var key = HotkeyParser.Parse(_chord());
                if (key.VirtualKey == 0)
                    continue;
                bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
                var now = Down(key.VirtualKey)
                    && key.Ctrl == Down(0x11) && key.Alt == Down(0x12) && key.Shift == Down(0x10)
                    && key.Win == (Down(0x5B) || Down(0x5C));
                if (now && !was)
                    ThreadPool.QueueUserWorkItem(_ => Fire("key-state watchdog"));
                was = now;
            }
            catch (Exception)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_threadId != 0)
            PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
