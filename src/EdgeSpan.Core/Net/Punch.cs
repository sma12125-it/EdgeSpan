using System.Net;

namespace EdgeSpan.Net;

public enum LinkChoice
{
    Offline = 0,
    Pending = 1,
    Lan = 2,
    Direct = 3,
    Relay = 4
}

public sealed class CandidateExchange
{
    public IPEndPoint? LocalMapped { get; set; }
    public IPEndPoint? PeerMapped { get; set; }
    public IPEndPoint? PeerLan { get; set; }
    public bool SawLan { get; private set; }
    public bool SawMapped { get; private set; }
    public long StartedMs { get; set; }

    public IReadOnlyList<IPEndPoint> Targets()
    {
        var list = new List<IPEndPoint>();
        if (PeerLan != null)
            list.Add(PeerLan);
        if (PeerMapped != null && !list.Any(e => Same(e, PeerMapped)))
            list.Add(PeerMapped);
        return list;
    }

    public void NoteInbound(IPEndPoint from)
    {
        if (PeerMapped != null && Same(from, PeerMapped))
            SawMapped = true;
        if (PeerLan != null && Same(from, PeerLan))
            SawLan = true;
    }

    public LinkChoice Evaluate(long nowMs, bool relayEnabled, int timeoutMs = 1200)
    {
        if (SawLan && PeerLan != null && NetAddresses.IsPrivate(PeerLan.Address))
            return LinkChoice.Lan;
        if (SawMapped || SawLan)
            return LinkChoice.Direct;
        if (nowMs - StartedMs >= timeoutMs)
            return relayEnabled ? LinkChoice.Relay : LinkChoice.Offline;
        return LinkChoice.Pending;
    }

    private static bool Same(IPEndPoint a, IPEndPoint b) =>
        a.Port == b.Port && a.Address.Equals(b.Address);
}
