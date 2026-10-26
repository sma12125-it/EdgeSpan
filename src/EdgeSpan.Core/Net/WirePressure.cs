using EdgeSpan.Protocol;

namespace EdgeSpan.Net;

/// <summary>
/// Caps for the session path. Mouse motion and heartbeats are safe to drop.
/// Reliable packets stay, because the sender already has one in flight and will retry.
/// </summary>
public static class WirePressure
{
    public const int InboxPerPump = 32;
    public const int InboxCap = 256;
    public const int SendCap = 64;
    public const int OutboundCap = 48;
    public const int HookStallMs = 80;

    public static bool IsDroppable(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4 || packet[0] != PacketCodec.Magic)
            return true;
        if ((packet[3] & PacketFlags.Reliable) != 0)
            return false;
        return packet[2] is (byte)PacketType.MouseMove
            or (byte)PacketType.Ping
            or (byte)PacketType.Pong
            or (byte)PacketType.Heartbeat
            or (byte)PacketType.Ack;
    }
}

/// <summary>
/// Bounded inbox. A burst from a LAN peer is trimmed here so the UI pump never
/// walks tens of thousands of datagrams on one tick.
/// </summary>
public sealed class IngressQueue
{
    public const int Cap = WirePressure.InboxCap;
    private readonly Queue<Item> _items = new();
    private readonly object _gate = new();

    public int Dropped { get; private set; }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public bool TryAdd(System.Net.IPEndPoint from, byte[] data, string? relayPeer)
    {
        lock (_gate)
        {
            if (_items.Count >= Cap)
            {
                if (!DropOne() && WirePressure.IsDroppable(data))
                {
                    Dropped++;
                    return false;
                }
                if (_items.Count >= Cap && WirePressure.IsDroppable(data))
                {
                    Dropped++;
                    return false;
                }
            }
            _items.Enqueue(new Item(from, data, relayPeer));
            return true;
        }
    }

    public bool TryTake(out System.Net.IPEndPoint from, out byte[] data, out string? relayPeer)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                data = Array.Empty<byte>();
                relayPeer = null;
                return false;
            }
            var item = _items.Dequeue();
            from = item.From;
            data = item.Data;
            relayPeer = item.RelayPeer;
            return true;
        }
    }

    private bool DropOne()
    {
        var kept = new Queue<Item>();
        var removed = false;
        while (_items.Count > 0)
        {
            var item = _items.Dequeue();
            if (!removed && WirePressure.IsDroppable(item.Data))
            {
                removed = true;
                Dropped++;
                continue;
            }
            kept.Enqueue(item);
        }
        while (kept.Count > 0)
            _items.Enqueue(kept.Dequeue());
        return removed;
    }

    private readonly record struct Item(System.Net.IPEndPoint From, byte[] Data, string? RelayPeer);
}

/// <summary>
/// Socket sends run on one background thread. Callers, including the UI pump,
/// only enqueue. Mouse motion is dropped when the peer is not keeping up.
/// </summary>
public sealed class SendQueue : IDisposable
{
    public const int Cap = WirePressure.SendCap;
    private readonly Queue<Item> _items = new();
    private readonly object _gate = new();
    private readonly AutoResetEvent _pulse = new(false);
    private readonly Thread _thread;
    private readonly Action<System.Net.IPEndPoint, byte[]> _send;
    private volatile bool _stop;

    public int Dropped { get; private set; }

    public int Depth
    {
        get { lock (_gate) return _items.Count; }
    }

    public SendQueue(Action<System.Net.IPEndPoint, byte[]> send)
    {
        _send = send;
        _thread = new Thread(Loop) { IsBackground = true, Name = "EdgeSpan.UdpSend" };
        _thread.Start();
    }

    public void Enqueue(System.Net.IPEndPoint to, byte[] data)
    {
        lock (_gate)
        {
            if (_items.Count >= Cap)
                DropOne();
            if (_items.Count >= Cap && WirePressure.IsDroppable(data))
            {
                Dropped++;
                return;
            }
            _items.Enqueue(new Item(to, data));
        }
        _pulse.Set();
    }

    public void Dispose()
    {
        _stop = true;
        _pulse.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        _pulse.Dispose();
    }

    private void Loop()
    {
        while (!_stop)
        {
            _pulse.WaitOne(50);
            while (!_stop)
            {
                Item item;
                lock (_gate)
                {
                    if (_items.Count == 0)
                        break;
                    item = _items.Dequeue();
                }
                try { _send(item.To, item.Data); }
                catch (System.Net.Sockets.SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }
    }

    private void DropOne()
    {
        var kept = new Queue<Item>();
        var removed = false;
        while (_items.Count > 0)
        {
            var item = _items.Dequeue();
            if (!removed && WirePressure.IsDroppable(item.Data))
            {
                removed = true;
                Dropped++;
                continue;
            }
            kept.Enqueue(item);
        }
        while (kept.Count > 0)
            _items.Enqueue(kept.Dequeue());
    }

    private readonly record struct Item(System.Net.IPEndPoint To, byte[] Data);
}
