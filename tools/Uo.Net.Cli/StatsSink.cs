using Uo.Net;

namespace Uo.Net.Cli;

/// <summary>Counts the entity stream and keeps a tiny player predictor (position from acked steps).</summary>
internal sealed class StatsSink(bool verbose) : WorldSinkBase
{
    private readonly HashSet<uint> _mobiles = new(4096);
    private readonly HashSet<uint> _items = new(16384);
    private readonly int[] _unhandled = new int[256];

    public int MobilePackets, ItemPackets, EquipPackets, Removes, PlayerUpdates;
    public int Acks, Rejects, Resets;
    public readonly List<double> MoveRtts = new(256);
    public readonly List<double> Pings = new(16);
    public ushort PlayerX, PlayerY;
    public sbyte PlayerZ;
    public byte Map;
    public Direction Facing;
    public int PredX, PredY;
    private uint _player;

    public override void OnStateChanged(ClientState state, ReadOnlySpan<char> detail) =>
        Console.WriteLine($"  state: {state}{(detail.IsEmpty ? "" : " (" + detail.ToString() + ")")}");

    public override void OnLoginConfirm(in PlayerInfo p)
    {
        _player = p.Serial;
        SetPlayer(p.X, p.Y, p.Z, p.Direction);
        Console.WriteLine($"  login confirm: serial 0x{p.Serial:X8} body 0x{p.Body:X} at {p.X},{p.Y},{p.Z} map {p.MapWidth}x{p.MapHeight}");
    }

    public override void OnLoginComplete() => Console.WriteLine("  login complete (0x55)");

    public override void OnMobile(in MobileState m, ReadOnlySpan<EquipEntry> equipment)
    {
        MobilePackets++;
        if (m.Serial == _player)
        {
            PlayerUpdates++;
            SetPlayer(m.X, m.Y, m.Z, m.Direction);
            return;
        }
        _mobiles.Add(m.Serial);
        foreach (var e in equipment) _items.Add(e.Serial);
    }

    public override void OnItem(in ItemState item)
    {
        ItemPackets++;
        _items.Add(item.Serial);
    }

    public override void OnEquip(in EquipState e)
    {
        EquipPackets++;
        _items.Add(e.Serial);
    }

    public override void OnRemove(uint serial)
    {
        Removes++;
        if (!_mobiles.Remove(serial)) _items.Remove(serial);
    }

    public override void OnMapChange(byte map) => Map = map;

    public override void OnMoveResult(in MoveResult r)
    {
        if (r.Accepted)
        {
            Acks++;
            MoveRtts.Add(r.RttMs);
        }
        else
        {
            Rejects++;
            SetPlayer(r.X, r.Y, r.Z, r.ServerDirection);
            Console.WriteLine($"  move REJECT seq {r.Sequence} dir {r.Direction}: server says {r.X},{r.Y},{r.Z}");
        }
        if (verbose) Console.WriteLine($"  move {(r.Accepted ? "ack" : "rej")} seq {r.Sequence} {r.Direction} rtt {r.RttMs:F2} ms pending {r.StillPending}");
    }

    public override void OnMoveSequenceReset(MoveResetReason reason, int droppedPending)
    {
        Resets++;
        if (verbose || droppedPending > 0) Console.WriteLine($"  move sequence reset ({reason}), dropped {droppedPending}");
    }

    public override void OnPing(byte seq, double rttMs) => Pings.Add(rttMs);

    public override void OnMessage(uint serial, byte type, ushort hue, ReadOnlySpan<char> name, ReadOnlySpan<char> text)
    {
        if (serial == 0xFFFFFFFF || serial == 0 || verbose)
            Console.WriteLine($"  msg [{name.ToString()}] {text.ToString()}");
    }

    public void CountUnhandled(byte id) => _unhandled[id]++;

    public string UnhandledSummary()
    {
        var parts = new List<string>();
        for (var i = 0; i < 256; i++)
            if (_unhandled[i] > 0) parts.Add($"0x{i:X2}x{_unhandled[i]}");
        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    public void ResetMoveStats()
    {
        Acks = Rejects = Resets = 0;
        MoveRtts.Clear();
        PredX = PlayerX;
        PredY = PlayerY;
    }

    public void PredictTurn(Direction d) => Facing = d;

    public void PredictStep(Direction d)
    {
        Facing = d;
        switch (d)
        {
            case Direction.North: PredY--; break;
            case Direction.South: PredY++; break;
            case Direction.East: PredX++; break;
            case Direction.West: PredX--; break;
            case Direction.Right: PredX++; PredY--; break;
            case Direction.Down: PredX++; PredY++; break;
            case Direction.Left: PredX--; PredY++; break;
            case Direction.Up: PredX--; PredY--; break;
        }
    }

    private void SetPlayer(ushort x, ushort y, sbyte z, Direction d)
    {
        PlayerX = x;
        PlayerY = y;
        PlayerZ = z;
        Facing = d & Direction.Mask;
        PredX = x;
        PredY = y;
    }

    public void PrintCounts(string label) =>
        Console.WriteLine($"[{label}] mobiles {_mobiles.Count} (packets {MobilePackets}), items {_items.Count} (world-item packets {ItemPackets}, equip {EquipPackets}), removes {Removes}");
}
