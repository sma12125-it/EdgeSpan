namespace EdgeSpan.Session;

public sealed class ReplayWindow
{
    private bool _init;
    private uint _max;
    private ulong _bits;

    public bool Accept(uint sequence)
    {
        if (sequence == 0)
            return false;
        if (!_init)
        {
            _init = true;
            _max = sequence;
            _bits = 1;
            return true;
        }
        if (sequence > _max)
        {
            var shift = sequence - _max;
            if (shift >= 64)
                _bits = 1;
            else
                _bits = (_bits << (int)shift) | 1;
            _max = sequence;
            return true;
        }
        var diff = _max - sequence;
        if (diff >= 64)
            return false;
        var mask = 1UL << (int)diff;
        if ((_bits & mask) != 0)
            return false;
        _bits |= mask;
        return true;
    }
}

public sealed class MouseCoalescer
{
    public bool Dirty { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public long LastFlushMs { get; private set; }
    public bool Force { get; set; }

    public void Set(int x, int y)
    {
        X = x;
        Y = y;
        Dirty = true;
    }

    public void Clear()
    {
        Dirty = false;
        Force = false;
    }

    public bool TryFlush(long nowMs, int minIntervalMs, out int x, out int y)
    {
        x = y = 0;
        if (!Dirty)
            return false;
        if (!Force && LastFlushMs != 0 && nowMs - LastFlushMs < minIntervalMs)
            return false;
        Force = false;
        Dirty = false;
        LastFlushMs = nowMs;
        x = X;
        y = Y;
        return true;
    }
}
