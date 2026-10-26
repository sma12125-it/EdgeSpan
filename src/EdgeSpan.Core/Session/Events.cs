namespace EdgeSpan.Session;

public abstract record SessionEvent
{
    public sealed record HelloReceived(byte Role, byte Capabilities, int Width, int Height, int Port, string Id, string Name) : SessionEvent;
    public sealed record MouseMoved(int X, int Y) : SessionEvent;
    public sealed record MouseButtonChanged(byte Button, bool Down, int X, int Y) : SessionEvent;
    public sealed record Scrolled(int Dx, int Dy, int X, int Y) : SessionEvent;
    public sealed record KeyChanged(ushort Vk, ushort Hid, byte Mods, bool Down, uint Codepoint) : SessionEvent;
    public sealed record ClipboardReceived(uint Id, string Text) : SessionEvent;
    public sealed record FileOfferReceived(uint Id, string Name, long Size, bool Cut) : SessionEvent;
    public sealed record FileChunkReceived(uint Id, long Offset, byte[] Data) : SessionEvent;
    public sealed record FileAckReceived(uint Id, long Next) : SessionEvent;
    public sealed record FileDoneReceived(uint Id, bool Ok) : SessionEvent;
    public sealed record EdgeEntered(byte Edge, int X, int Y, int Width, int Height) : SessionEvent;
    public sealed record EdgeReturned(int HostX, int HostY, int DeviceX, int DeviceY) : SessionEvent;
    public sealed record Locked(bool IsLocked) : SessionEvent;
    public sealed record GoodbyeReceived(byte Reason) : SessionEvent;
    public sealed record PongReceived(ulong Token, double RttMs) : SessionEvent;
    public sealed record HandoffAccepted(uint Id, byte Codec, int ViewW, int ViewH) : SessionEvent;
    public sealed record HandoffGeometryReceived(uint Id, int X, int Y, int PhysW, int PhysH, int DipW, int DipH) : SessionEvent;
    public sealed record HandoffInputReceived(uint Id, byte Kind, int X, int Y, byte Button, bool Down, int WheelX, int WheelY, int Vk, string Text) : SessionEvent;
    public sealed record HandoffStateReceived(uint Id, byte Value) : SessionEvent;
    public sealed record HandoffReturned(uint Id) : SessionEvent;
    public sealed record HandoffVideoConfigReceived(uint Id, uint Bitrate, byte Fps, bool Idr) : SessionEvent;
    public sealed record HandoffOffered(uint Id, byte Direction, uint Dpi, int PhysW, int PhysH, int DipW, int DipH, byte Codec, string Title) : SessionEvent;
}

public sealed class ReceiveResult
{
    public List<SessionEvent> Events { get; } = new();
    public List<byte[]> Replies { get; } = new();
}
