using EdgeSpan.Protocol;

namespace EdgeSpan.Session;

/// <summary>
/// Encrypted file copies ride the same reliable packets as keyboard and clipboard text.
/// One chunk is in flight per peer. A cut is deleted only after the other side confirms the paste.
/// </summary>
public sealed class FileSync
{
    public const int ChunkBytes = 800;
    public const long MaxBytes = 512L * 1024 * 1024;

    public readonly record struct Packet(string PeerId, PacketType Type, byte[] Payload);
    public readonly record struct Note(string Name, long Done, long Total, bool Incoming, string Phase, string Path);

    private readonly List<Outgoing> _out = new();
    private readonly Dictionary<(string Peer, uint Id), Incoming> _in = new();
    private readonly List<Packet> _ready = new();
    private readonly List<Note> _notes = new();
    public void Queue(string peerId, string path, bool cut)
    {
        if (string.IsNullOrWhiteSpace(peerId) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            _notes.Add(new Note(info.Name, 0, info.Length, false, "failed", path));
            return;
        }
        _out.Add(new Outgoing
        {
            PeerId = peerId,
            Path = path,
            Name = info.Name,
            Cut = cut,
            Size = info.Length,
            Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        });
        _notes.Add(new Note(info.Name, 0, info.Length, false, "copy", path));
    }

    public void OnOffer(string peerId, uint id, string name, long size, bool cut, string directory)
    {
        if (size < 0 || size > MaxBytes)
            return;
        var safe = SafeName(name);
        Directory.CreateDirectory(directory);
        var path = Unique(directory, safe);
        var job = new Incoming
        {
            Name = safe,
            Path = path,
            Size = size,
            Cut = cut,
            Stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
        };
        _in[(peerId, id)] = job;
        _notes.Add(new Note(safe, 0, size, true, "copy", path));
        if (size == 0)
            FinishIncoming(peerId, id, job);
    }

    public void OnChunk(string peerId, uint id, long offset, ReadOnlySpan<byte> data)
    {
        if (!_in.TryGetValue((peerId, id), out var job))
            return;
        if (offset != job.Got)
        {
            _ready.Add(new Packet(peerId, PacketType.FileAck, Payloads.FileAck(id, job.Got)));
            return;
        }
        job.Stream.Write(data);
        job.Got += data.Length;
        _notes.Add(new Note(job.Name, job.Got, job.Size, true, "copy", job.Path));
            _ready.Add(new Packet(peerId, PacketType.FileAck, Payloads.FileAck(id, job.Got)));
        if (job.Got >= job.Size)
            FinishIncoming(peerId, id, job);
    }

    public void OnAck(string peerId, uint id, long next)
    {
        var job = _out.FirstOrDefault(j => j.PeerId == peerId && j.Id == id);
        if (job == null)
            return;
        job.Acked = Math.Max(job.Acked, next);
        _notes.Add(new Note(job.Name, job.Acked, job.Size, false, "copy", job.Path));
    }

    public void OnDone(string peerId, uint id, bool ok)
    {
        var job = _out.FirstOrDefault(j => j.PeerId == peerId && j.Id == id);
        if (job == null)
            return;
        job.Stream.Dispose();
        _out.Remove(job);
        if (ok && job.Cut)
        {
            try { File.Delete(job.Path); } catch (IOException) { }
            _notes.Add(new Note(job.Name, job.Size, job.Size, false, "moved", job.Path));
        }
        else
            _notes.Add(new Note(job.Name, job.Size, job.Size, false, ok ? "ready" : "failed", job.Path));
    }

    public List<Packet> Drain(Func<uint> nextId)
    {
        foreach (var job in _out.Where(j => !j.OfferSent).ToArray())
        {
            job.Id = nextId();
            job.OfferSent = true;
            _ready.Add(new Packet(job.PeerId, PacketType.FileOffer, Payloads.FileOffer(job.Id, job.Cut, job.Size, job.Name)));
        }
        foreach (var job in _out)
        {
            if (!job.OfferSent || job.Queued > job.Acked || job.Queued >= job.Size)
                continue;
            var n = (int)Math.Min(ChunkBytes, job.Size - job.Queued);
            var buf = new byte[n];
            job.Stream.Position = job.Queued;
            var read = job.Stream.Read(buf, 0, n);
            if (read <= 0)
                continue;
            if (read != n)
                Array.Resize(ref buf, read);
            _ready.Add(new Packet(job.PeerId, PacketType.FileChunk, Payloads.FileChunk(job.Id, job.Queued, buf)));
            job.Queued += read;
        }
        var list = _ready.ToList();
        _ready.Clear();
        return list;
    }

    public List<Note> Notes()
    {
        var list = _notes.ToList();
        _notes.Clear();
        return list;
    }

    public void Dispose()
    {
        foreach (var job in _out)
            job.Stream.Dispose();
        foreach (var job in _in.Values)
            job.Stream.Dispose();
        _out.Clear();
        _in.Clear();
    }

    private void FinishIncoming(string peerId, uint id, Incoming job)
    {
        job.Stream.Dispose();
        _in.Remove((peerId, id));
        _ready.Add(new Packet(peerId, PacketType.FileDone, Payloads.FileDone(id, true)));
        _notes.Add(new Note(job.Name, job.Size, job.Size, true, "ready", job.Path));
    }

    private static string SafeName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(file) || file is "." or "..")
            file = "file";
        foreach (var c in Path.GetInvalidFileNameChars())
            file = file.Replace(c, '_');
        return file;
    }

    private static string Unique(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            path = Path.Combine(directory, stem + " " + i + ext);
            if (!File.Exists(path))
                return path;
        }
        return Path.Combine(directory, stem + "-" + Guid.NewGuid().ToString("N") + ext);
    }

    private sealed class Outgoing
    {
        public string PeerId = "";
        public uint Id;
        public string Path = "";
        public string Name = "";
        public bool Cut;
        public long Size;
        public long Queued;
        public long Acked;
        public bool OfferSent;
        public FileStream Stream = null!;
    }

    private sealed class Incoming
    {
        public string Name = "";
        public string Path = "";
        public long Size;
        public long Got;
        public bool Cut;
        public FileStream Stream = null!;
    }
}
