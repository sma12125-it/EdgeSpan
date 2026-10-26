namespace EdgeSpan.Session;

public enum InputKind
{
    Button,
    RawButtons,
    Key
}

public readonly record struct InputNote(
    InputKind Kind,
    int A,
    int B,
    int X,
    int Y,
    bool Down,
    bool Ctrl,
    bool Alt,
    bool Shift,
    bool Win,
    bool SampleCursor,
    bool Forward = false,
    bool Hotkey = false);

public readonly struct InputBatch
{
    public bool HasMove { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Dx { get; init; }
    public int Dy { get; init; }
    public InputNote[] Notes { get; init; }
    public bool Any => HasMove || Notes.Length > 0;
}

/// <summary>
/// Hook callbacks only post here. Moves collapse to the latest sample so a
/// high-rate mouse cannot grow the queue. The worker drains it later.
/// </summary>
public sealed class InputMailbox
{
    public const int NoteCap = 96;
    private readonly object _gate = new();
    private readonly Queue<InputNote> _notes = new();
    private readonly AutoResetEvent _pulse = new(false);
    private bool _hasMove;
    private bool _hasAnchor;
    private int _x;
    private int _y;
    private int _dx;
    private int _dy;
    private int _anchorX;
    private int _anchorY;

    public int Dropped { get; private set; }

    public int Depth
    {
        get { lock (_gate) return (_hasMove ? 1 : 0) + _notes.Count; }
    }

    public void PostAbsolute(int x, int y)
    {
        lock (_gate)
        {
            var dx = _hasAnchor ? x - _anchorX : 0;
            var dy = _hasAnchor ? y - _anchorY : 0;
            _anchorX = x;
            _anchorY = y;
            _hasAnchor = true;
            _x = x;
            _y = y;
            _dx += dx;
            _dy += dy;
            _hasMove = true;
        }
        _pulse.Set();
    }

    public void PostSample(int x, int y, int dx, int dy)
    {
        lock (_gate)
        {
            _anchorX = x;
            _anchorY = y;
            _hasAnchor = true;
            _x = x;
            _y = y;
            _dx += dx;
            _dy += dy;
            _hasMove = true;
        }
        _pulse.Set();
    }

    public void PostButton(int message, int mouseData) =>
        Post(new InputNote(InputKind.Button, message, mouseData, 0, 0, false, false, false, false, false, false));

    public void PostRawButtons(int flags, int data) =>
        Post(new InputNote(InputKind.RawButtons, flags, data, 0, 0, false, false, false, false, false, false));

    public void PostKey(int virtualKey, bool down, bool ctrl, bool alt, bool shift, bool win, bool sampleCursor, int x, int y, bool forward = false, bool hotkey = false) =>
        Post(new InputNote(InputKind.Key, virtualKey, 0, x, y, down, ctrl, alt, shift, win, sampleCursor, forward, hotkey));

    public void Signal() => _pulse.Set();

    public void Wait(int milliseconds) => _pulse.WaitOne(milliseconds);

    public InputBatch Take(int maxNotes)
    {
        lock (_gate)
        {
            var notes = new List<InputNote>(Math.Min(maxNotes, _notes.Count));
            while (notes.Count < maxNotes && _notes.Count > 0)
                notes.Add(_notes.Dequeue());
            var batch = new InputBatch
            {
                HasMove = _hasMove,
                X = _x,
                Y = _y,
                Dx = _dx,
                Dy = _dy,
                Notes = notes.ToArray()
            };
            if (_hasMove)
            {
                _dx = 0;
                _dy = 0;
                _hasMove = false;
            }
            if (_notes.Count > 0)
                _pulse.Set();
            return batch;
        }
    }

    private void Post(InputNote note)
    {
        lock (_gate)
        {
            if (_notes.Count >= NoteCap)
            {
                Dropped++;
                return;
            }
            _notes.Enqueue(note);
        }
        _pulse.Set();
    }
}

/// <summary>
/// Decides when a low-level hook has stopped returning. The caller unhooks
/// from a thread other than the hook thread, then reinstalls once it is idle.
/// </summary>
public static class HookWatchdog
{
    public const int StallMs = 80;

    public static bool ShouldUnhook(int depth, long enteredAtMs, long nowMs) =>
        depth > 0 && enteredAtMs > 0 && nowMs - enteredAtMs >= StallMs;

    public static bool ShouldRehook(bool tripped, int depth) => tripped && depth <= 0;
}
