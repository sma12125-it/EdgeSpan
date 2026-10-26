namespace EdgeSpan.Protocol;

public sealed class FragmentAssembler
{
    private sealed class Bucket
    {
        public byte[]?[] Parts = Array.Empty<byte[]?>();
        public int Got;
        public long SeenMs;
    }

    private readonly Dictionary<(byte Type, uint Id), Bucket> _open = new();

    public byte[]? Push(byte type, uint msgId, int index, int count, ReadOnlySpan<byte> chunk, long nowMs)
    {
        Sweep(nowMs);
        if (count is < 1 or > 64 || index < 0 || index >= count || chunk.Length > 4000)
            return null;
        var key = (type, msgId);
        if (!_open.TryGetValue(key, out var bucket) || bucket.Parts.Length != count)
        {
            bucket = new Bucket { Parts = new byte[count][], SeenMs = nowMs };
            _open[key] = bucket;
        }
        if (bucket.Parts[index] == null)
        {
            bucket.Parts[index] = chunk.ToArray();
            bucket.Got++;
        }
        if (bucket.Got < count)
            return null;
        var total = 0;
        foreach (var part in bucket.Parts)
            total += part!.Length;
        var all = new byte[total];
        var o = 0;
        foreach (var part in bucket.Parts)
        {
            part!.CopyTo(all, o);
            o += part.Length;
        }
        _open.Remove(key);
        return all;
    }

    private void Sweep(long nowMs)
    {
        if (_open.Count == 0)
            return;
        List<(byte, uint)>? dead = null;
        foreach (var pair in _open)
        {
            if (nowMs - pair.Value.SeenMs > 5000)
                (dead ??= new()).Add(pair.Key);
        }
        if (dead == null)
            return;
        foreach (var key in dead)
            _open.Remove(key);
    }
}
