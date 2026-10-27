using System.Runtime.InteropServices;
using System.Text;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Keeps a handed-off window's popups (menus, dropdowns, context menus, tooltips, owned dialogs) with it:
/// new popups of the same process are moved next to the parked window (off the PC monitors) and composited
/// into the tablet stream; tablet clicks that land on a popup are delivered to that popup.
/// </summary>
internal sealed class HandoffPopups
{
    public readonly record struct Popup(nint Hwnd, int X, int Y, int W, int H);

    private readonly nint _main;
    private readonly uint _pid;
    private readonly HashSet<uint> _pids = new();
    private readonly HashSet<nint> _before = new();
    private readonly HashSet<nint> _moved = new();
    private List<Popup> _current = new();
    private long _scanned;
    private readonly object _gate = new();

    /// <summary>Screen point of the last tablet click (on the parked window), where a new popup belongs.</summary>
    public volatile int AnchorX;
    public volatile int AnchorY;
    public volatile bool HasAnchor;

    public HandoffPopups(nint main)
    {
        _main = main;
        GetWindowThreadProcessId(main, out _pid);
        _pids.Add(_pid);
        // UWP/WinUI hosts (ApplicationFrameHost) draw content from a child window of another process; its popups
        // belong to that process, so track every process that owns a child of the handed-off window.
        EnumChildWindows(main, (c, _) => { GetWindowThreadProcessId(c, out var p); _pids.Add(p); return true; }, 0);
        foreach (var h in Candidates())
            _before.Add(h);
    }

    /// <summary>Visible popups, rescanned at most every 80 ms; repositions newly shown ones.</summary>
    public List<Popup> Current()
    {
        lock (_gate)
        {
            if (Environment.TickCount64 - _scanned < 80)
                return _current;
            _scanned = Environment.TickCount64;
            var list = new List<Popup>();
            GetWindowRect(_main, out var main);
            foreach (var h in Candidates())
            {
                if (_before.Contains(h))
                    continue;
                if (!GetWindowRect(h, out var r) || r.Right - r.Left < 2 || r.Bottom - r.Top < 2)
                    continue;
                if (!_moved.Contains(h))
                {
                    _moved.Add(h);
                    if (OnSomeMonitor(r))
                    {
                        // The app placed it on a PC monitor (the parked parent is off-screen, so Windows clamped it).
                        // Put it where the user clicked on the tablet, relative to the parked window.
                        var x = HasAnchor ? AnchorX : main.Left + (main.Right - main.Left - (r.Right - r.Left)) / 2;
                        var y = HasAnchor ? AnchorY : main.Top + 40;
                        x = Math.Clamp(x, main.Left, Math.Max(main.Left, main.Right - (r.Right - r.Left)));
                        y = Math.Clamp(y, main.Top, Math.Max(main.Top, main.Bottom - (r.Bottom - r.Top)));
                        var ok = SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
                        DiagLog.Write($"handoff popup 0x{h:X} {ClassOf(h)} moved {ok} from {r.Left},{r.Top} to {x},{y}");
                        GetWindowRect(h, out r);
                    }
                    else
                        DiagLog.Write($"handoff popup 0x{h:X} {ClassOf(h)} at {r.Left},{r.Top}");
                }
                list.Add(new Popup(h, r.Left - main.Left, r.Top - main.Top, r.Right - r.Left, r.Bottom - r.Top));
            }
            _current = list;
            return list;
        }
    }

    /// <summary>Topmost popup under a screen point, or 0.</summary>
    public nint HitTest(int sx, int sy)
    {
        if (!GetWindowRect(_main, out var main))
            return 0;
        var list = Current();
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var x = main.Left + p.X;
            var y = main.Top + p.Y;
            if (sx >= x && sx < x + p.W && sy >= y && sy < y + p.H)
                return p.Hwnd;
        }
        return 0;
    }

    /// <summary>Closes popups (Escape) when the handoff ends, so none is left behind off-screen.</summary>
    public void Dismiss()
    {
        foreach (var p in Current())
            PostMessage(p.Hwnd, 0x0100, (IntPtr)0x1B, (IntPtr)1);
    }

    private IEnumerable<nint> Candidates()
    {
        var found = new List<nint>();
        EnumWindows((h, _) =>
        {
            if (h == _main || !IsWindowVisible(h))
                return true;
            GetWindowThreadProcessId(h, out var pid);
            var cls = ClassOf(h);
            var owner = GetWindow(h, 4);
            var ownedByMain = owner != 0 && (owner == _main || GetAncestor(owner, 3) == _main);
            // Generic for every toolkit (Win32, WPF, WinForms, UWP/WinUI, Electron/Chromium, Office, Java...):
            // any window that appears after the handoff and is owned by the handed-off window, or belongs to one of
            // its processes (menus, dropdowns, tooltips, flyouts, dialogs), travels with it. System menus (#32768)
            // are owned by the shell, so they are matched by class.
            if (ownedByMain || _pids.Contains(pid) || cls == "#32768")
                found.Add(h);
            return true;
        }, 0);
        return found;
    }

    private static string ClassOf(nint h)
    {
        var sb = new StringBuilder(64);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool OnSomeMonitor(RECT r)
    {
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = GetSystemMetrics(78);
        var vh = GetSystemMetrics(79);
        return r.Right > vx + 8 && r.Left < vx + vw - 8 && r.Bottom > vy + 8 && r.Top < vy + vh - 8;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
