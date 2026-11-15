namespace Uo.Net;

/// <summary>UO direction (0 = north, clockwise). Bit 0x80 = running.</summary>
public enum Direction : byte
{
    North = 0, Right = 1, East = 2, Down = 3, South = 4, Left = 5, West = 6, Up = 7,
    Mask = 0x07, Running = 0x80,
}

public enum ClientState
{
    Disconnected,
    LoginConnecting,
    AwaitingServerList,
    AwaitingRelay,
    GameConnecting,
    AwaitingCharacterList,
    EnteringWorld,
    InWorld,
    Failed,
}

public readonly struct PlayerInfo
{
    public uint Serial { get; init; }
    public ushort Body { get; init; }
    public ushort X { get; init; }
    public ushort Y { get; init; }
    public sbyte Z { get; init; }
    public Direction Direction { get; init; }
    public ushort MapWidth { get; init; }
    public ushort MapHeight { get; init; }
}

/// <summary>Source packet for a mobile state, so the integration layer can tell spawn from update.</summary>
public enum MobileSource : byte { Update20, Moving77, Incoming78, WorldObjF3 }

public readonly struct MobileState
{
    public uint Serial { get; init; }
    public ushort Body { get; init; }
    public ushort X { get; init; }
    public ushort Y { get; init; }
    public sbyte Z { get; init; }
    public Direction Direction { get; init; }
    public ushort Hue { get; init; }
    public byte Flags { get; init; }
    /// <summary>Notoriety (0x77/0x78 only; 0 otherwise).</summary>
    public byte Notoriety { get; init; }
    public bool IsPlayer { get; init; }
    public MobileSource Source { get; init; }
}

public readonly struct EquipEntry
{
    public uint Serial { get; init; }
    public ushort ItemId { get; init; }
    public byte Layer { get; init; }
    public ushort Hue { get; init; }
}

public enum ItemKind : byte { Item = 0, Mobile = 1, Multi = 2, Damageable = 3 }

public readonly struct ItemState
{
    public uint Serial { get; init; }
    public ushort ItemId { get; init; }
    public ushort Amount { get; init; }
    public ushort X { get; init; }
    public ushort Y { get; init; }
    public sbyte Z { get; init; }
    /// <summary>0xF3: light/direction byte; 0x1A: direction when present.</summary>
    public byte Direction { get; init; }
    public ushort Hue { get; init; }
    public byte Flags { get; init; }
    public ItemKind Kind { get; init; }
}

public readonly struct EquipState
{
    public uint Serial { get; init; }
    public ushort ItemId { get; init; }
    public byte Layer { get; init; }
    public uint Parent { get; init; }
    public ushort Hue { get; init; }
}

/// <summary>Result of one move request. Timestamps are <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
public readonly struct MoveResult
{
    public byte Sequence { get; init; }
    public Direction Direction { get; init; }
    public bool Accepted { get; init; }
    /// <summary>Ack: player notoriety. Reject: unused.</summary>
    public byte Notoriety { get; init; }
    /// <summary>Reject only: authoritative position to snap to.</summary>
    public ushort X { get; init; }
    public ushort Y { get; init; }
    public sbyte Z { get; init; }
    public Direction ServerDirection { get; init; }
    public long SentTimestamp { get; init; }
    public long ReceivedTimestamp { get; init; }
    /// <summary>Requests still unacknowledged after this one (reject clears all of them).</summary>
    public int StillPending { get; init; }
    public double RttMs => (ReceivedTimestamp - SentTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}

public enum MoveResetReason : byte { PlayerUpdate, MapChange, Rejected }

/// <summary>
/// Receives the decoded entity stream. Called on the thread that calls <see cref="IGameClient.Poll"/>.
/// Spans are only valid for the duration of the call.
/// </summary>
public interface IWorldSink
{
    void OnStateChanged(ClientState state, ReadOnlySpan<char> detail);
    void OnLoginConfirm(in PlayerInfo player);
    void OnLoginComplete();
    void OnMobile(in MobileState mobile, ReadOnlySpan<EquipEntry> equipment);
    void OnItem(in ItemState item);
    void OnEquip(in EquipState equip);
    void OnRemove(uint serial);
    void OnMapChange(byte map);
    /// <summary>Move ack (0x22) or reject (0x21), matched to the request by sequence.</summary>
    void OnMoveResult(in MoveResult result);
    /// <summary>The server reset its movement sequence (teleport, map change, reject); pending requests were dropped.</summary>
    void OnMoveSequenceReset(MoveResetReason reason, int droppedPending);
    /// <summary>Ping (0x73) round trip.</summary>
    void OnPing(byte seq, double rttMs);
    /// <summary>Speech / system text (0x1C, 0xAE). serial 0xFFFFFFFF = system.</summary>
    void OnMessage(uint serial, byte type, ushort hue, ReadOnlySpan<char> name, ReadOnlySpan<char> text);
    /// <summary>Localized message (0xC1, 0xCC) — cliloc number only.</summary>
    void OnClilocMessage(uint serial, int cliloc);
}

/// <summary>Raw packet tap (both directions; incoming after decompression).</summary>
public delegate void PacketTap(bool outgoing, ReadOnlySpan<byte> packet, bool handled);

/// <summary>No-op base so sinks override only what they need.</summary>
public abstract class WorldSinkBase : IWorldSink
{
    public virtual void OnStateChanged(ClientState state, ReadOnlySpan<char> detail) { }
    public virtual void OnLoginConfirm(in PlayerInfo player) { }
    public virtual void OnLoginComplete() { }
    public virtual void OnMobile(in MobileState mobile, ReadOnlySpan<EquipEntry> equipment) { }
    public virtual void OnItem(in ItemState item) { }
    public virtual void OnEquip(in EquipState equip) { }
    public virtual void OnRemove(uint serial) { }
    public virtual void OnMapChange(byte map) { }
    public virtual void OnMoveResult(in MoveResult result) { }
    public virtual void OnMoveSequenceReset(MoveResetReason reason, int droppedPending) { }
    public virtual void OnPing(byte seq, double rttMs) { }
    public virtual void OnMessage(uint serial, byte type, ushort hue, ReadOnlySpan<char> name, ReadOnlySpan<char> text) { }
    public virtual void OnClilocMessage(uint serial, int cliloc) { }
}
