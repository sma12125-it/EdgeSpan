using System.Runtime.InteropServices;
using System.Text;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Watches the Windows clipboard with AddClipboardFormatListener.
/// Text uses CF_UNICODETEXT. Files use CF_HDROP, and Ctrl+X is the Preferred DropEffect move flag.
/// Setting the clipboard from a remote paste is ignored so it is not sent straight back.
/// </summary>
public sealed class ClipboardWatch : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private readonly Thread _thread;
    private int _threadId;
    private IntPtr _hwnd;
    private WndProc? _proc;
    private long _muteUntil;
    private string _lastText = "";

    public event Action<string>? TextCopied;
    public event Action<string[], bool>? FilesCopied;

    public ClipboardWatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            _thread = new Thread(() => { });
            return;
        }
        _thread = new Thread(Pump) { IsBackground = true, Name = "EdgeSpan.Clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetText(string text)
    {
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero)
            return;
        _muteUntil = Environment.TickCount64 + 1200;
        _lastText = text ?? "";
        Post(_hwnd, 0x8002, IntPtr.Zero, IntPtr.Zero);
        lock (this)
            _pendingText = _lastText;
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Count == 0 || _hwnd == IntPtr.Zero)
            return;
        _muteUntil = Environment.TickCount64 + 1200;
        lock (this)
            _pendingFiles = paths.ToArray();
        Post(_hwnd, 0x8003, IntPtr.Zero, IntPtr.Zero);
    }

    private string? _pendingText;
    private string[]? _pendingFiles;

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private void Pump()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _threadId = GetCurrentThreadId();
        _proc = WindowProc;
        var cls = new WndClass { lpfnWndProc = _proc, lpszClassName = "EdgeSpanClipboard" };
        RegisterClass(ref cls);
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        AddClipboardFormatListener(_hwnd);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == 0x8002)
                WriteText();
            else if (msg.Message == 0x8003)
                WriteFiles();
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        RemoveClipboardFormatListener(_hwnd);
        DestroyWindow(_hwnd);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClipboardUpdate)
            Read();
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Read()
    {
        if (Environment.TickCount64 < _muteUntil)
            return;
        if (!OpenClipboard(_hwnd))
            return;
        try
        {
            var drop = GetClipboardData(CfHdrop);
            if (drop != IntPtr.Zero)
            {
                var count = (int)DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                var paths = new List<string>();
                var buf = new StringBuilder(520);
                for (var i = 0; i < count; i++)
                {
                    buf.Clear();
                    if (DragQueryFile(drop, (uint)i, buf, buf.Capacity) > 0)
                        paths.Add(buf.ToString());
                }
                var cut = ReadMoveEffect();
                if (paths.Count > 0)
                    FilesCopied?.Invoke(paths.ToArray(), cut);
                return;
            }
            var textHandle = GetClipboardData(CfUnicodeText);
            if (textHandle == IntPtr.Zero)
                return;
            var ptr = GlobalLock(textHandle);
            if (ptr == IntPtr.Zero)
                return;
            try
            {
                var text = Marshal.PtrToStringUni(ptr) ?? "";
                if (text.Length == 0 || text == _lastText)
                    return;
                _lastText = text;
                TextCopied?.Invoke(text);
            }
            finally
            {
                GlobalUnlock(textHandle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private bool ReadMoveEffect()
    {
        var format = RegisterClipboardFormat("Preferred DropEffect");
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return false;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
            return false;
        try
        {
            return Marshal.ReadInt32(ptr) == 2;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private void WriteText()
    {
        string? text;
        lock (this)
            text = _pendingText;
        if (text == null || !OpenClipboard(_hwnd))
            return;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var mem = GlobalAlloc(0x0042, (nuint)bytes.Length);
            var ptr = GlobalLock(mem);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(mem);
            SetClipboardData(CfUnicodeText, mem);
        }
        finally
        {
            CloseClipboard();
        }
    }

    private void WriteFiles()
    {
        string[]? paths;
        lock (this)
            paths = _pendingFiles;
        if (paths == null || paths.Length == 0 || !OpenClipboard(_hwnd))
            return;
        try
        {
            EmptyClipboard();
            var list = string.Join("\0", paths) + "\0\0";
            var chars = Encoding.Unicode.GetBytes(list);
            var mem = GlobalAlloc(0x0042, (nuint)(20 + chars.Length));
            var ptr = GlobalLock(mem);
            Marshal.WriteInt32(ptr, 20);
            Marshal.WriteInt32(ptr, 16, 1);
            Marshal.Copy(chars, 0, ptr + 20, chars.Length);
            GlobalUnlock(mem);
            SetClipboardData(CfHdrop, mem);
        }
        finally
        {
            CloseClipboard();
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

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

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr mem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr mem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr mem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, int cch);

    private static void Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) => PostMessage(hwnd, msg, wParam, lParam);
}
