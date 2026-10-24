using Uo.Net;

namespace Uo.Client;

/// <summary>Offline stand-in for <see cref="ClassicClient"/>: an <see cref="IGameClient"/> that plays a synthetic
/// scenario into the sink and acks or rejects move requests after a configurable latency. Server-side movement uses the
/// same <see cref="WalkMap"/> rules plus optional server-only blocked tiles (to force rejects), and mirrors ModernUO's
/// sequence handling: expected sequence 0 after a reject, wrap 255 → 1, a request in a new direction only turns.</summary>
public sealed class FakeServer : IGameClient
{
    public sealed class Scenario
    {
        public int X = 1495, Y = 1629;
        public int Mobiles = 12, Items = 24;
        public int Radius = 12;
        public int Seed = 1234;
        public int WanderPerSecond = 6;
        public uint PlayerSerial = 0x00001000;
        /// <summary>NPC bodies cycle through humans (male/female, one mounted), a horse, a dragon and monsters; else all humans.</summary>
        public bool MixedBodies = true;
    }

    enum Kind : byte { Ack, Reject, NpcStep }

    struct Event
    {
        public long Due;
        public Kind Kind;
        public byte Seq;
    }

    struct Npc
    {
        public uint Serial;
        public ushort X, Y, Body, Hue;
        public sbyte Z;
        public byte Dir, Noto;
    }

    struct Pending
    {
        public byte Seq;
        public Direction Dir;
        public long Sent;
    }

    readonly IWorldSink _sink;
    readonly WalkMap _map;
    readonly Clock _clock;
    readonly Scenario _sc;
    readonly List<Event> _events = new(1024);
    readonly Pending[] _pending = new Pending[5];
    int _pendingCount;
    byte _seq;          // client side
    int _serverSeq;     // server side (ModernUO NetState.Sequence)
    Npc[] _npcs = [];
    readonly Random _rng;
    long _nextWander;
    bool _connected;
    readonly EquipEntry[] _equip = new EquipEntry[6];
    static readonly ushort[] MixBodies = [0x190, 0x191, 0x190, 0xCC, 0x3B, 0x11, 0x01, 0x09];

    // server truth
    public int ServerX, ServerY;
    public sbyte ServerZ;
    public byte ServerDir;
    public long LatencyTicks;
    public HashSet<int> ServerBlocked = new();
    public int AcksSent, RejectsSent;

    public FakeServer(IWorldSink sink, WalkMap map, Clock clock, Scenario scenario, double latencyMs)
    {
        _sink = sink; _map = map; _clock = clock; _sc = scenario;
        _rng = new Random(scenario.Seed);
        LatencyTicks = Clock.Ms(latencyMs);
    }

    public ClientState State { get; private set; }
    public uint PlayerSerial => _sc.PlayerSerial;
    public int PendingMoves => _pendingCount;

    public void Connect(LoginOptions options) => _connected = true;

    void Login()
    {
        _map.Preload(_sc.X, _sc.Y);
        ServerX = _sc.X; ServerY = _sc.Y; ServerDir = 4;
        ServerZ = _map.SpawnZ(_sc.X, _sc.Y, out var z) ? z : _map.TileZ(_sc.X, _sc.Y);
        State = ClientState.EnteringWorld;
        _sink.OnStateChanged(State, "fake server");
        _sink.OnLoginConfirm(new PlayerInfo { Serial = PlayerSerial, Body = 0x190, X = (ushort)ServerX, Y = (ushort)ServerY, Z = ServerZ, Direction = (Direction)ServerDir, MapWidth = (ushort)_map.Width, MapHeight = (ushort)_map.Height });
        _sink.OnMapChange(0);
        _sink.OnMobile(new MobileState { Serial = PlayerSerial, Body = 0x190, X = (ushort)ServerX, Y = (ushort)ServerY, Z = ServerZ, Direction = (Direction)ServerDir, Hue = 0x83EA, IsPlayer = true, Source = MobileSource.Update20 },
        [
            new EquipEntry { Serial = 0x40F00000, ItemId = 0x1517, Layer = 0x05, Hue = 0x0035 },
            new EquipEntry { Serial = 0x40F00001, ItemId = 0x152E, Layer = 0x04, Hue = 0x01BB },
            new EquipEntry { Serial = 0x40F00002, ItemId = 0x170F, Layer = 0x03, Hue = 0 },
            new EquipEntry { Serial = 0x40F00003, ItemId = 0x203B, Layer = 0x0B, Hue = 0x044E },
        ]);

        _npcs = new Npc[_sc.Mobiles];
        int r = _sc.Radius;
        for (int i = 0; i < _npcs.Length; i++)
        {
            int x, y;
            sbyte nz;
            int tries = 0;
            do
            {
                x = _sc.X + _rng.Next(-r, r + 1);
                y = _sc.Y + _rng.Next(-r, r + 1);
            } while ((!_map.SpawnZ(x, y, out nz) || x == _sc.X && y == _sc.Y) && ++tries < 50);
            if (tries >= 50) nz = _map.TileZ(x, y);
            ref var n = ref _npcs[i];
            int kind = i & 7;
            ushort body = _sc.MixedBodies ? MixBodies[kind] : (ushort)(0x190 + (i & 1));
            ushort hue = body < 0x190 ? (ushort)(i % 3 == 0 ? 0 : 2 + (i * 13) % 900) : (ushort)(0x83EA + (i % 20));
            n = new Npc { Serial = 0x2000u + (uint)i, X = (ushort)x, Y = (ushort)y, Z = nz, Body = body, Hue = hue, Dir = (byte)_rng.Next(8), Noto = (byte)(1 + _rng.Next(6)) };
            int ne = 0;
            if (body is 0x190 or 0x191)
            {
                uint sb = 0x40000000u + (uint)i * 8;
                bool female = body == 0x191;
                _equip[ne++] = new EquipEntry { Serial = sb, ItemId = 0x1517, Layer = 0x05, Hue = (ushort)(2 + i % 900) };
                _equip[ne++] = new EquipEntry { Serial = sb + 1, ItemId = female ? (ushort)0x1516 : (ushort)0x152E, Layer = female ? (byte)0x17 : (byte)0x04, Hue = (ushort)(2 + (i * 7) % 900) };
                _equip[ne++] = new EquipEntry { Serial = sb + 2, ItemId = 0x170F, Layer = 0x03, Hue = 0 };
                _equip[ne++] = new EquipEntry { Serial = sb + 3, ItemId = female ? (ushort)0x203C : (ushort)0x203B, Layer = 0x0B, Hue = (ushort)(0x44E + i % 40) };
                if (_sc.MixedBodies && kind == 2) _equip[ne++] = new EquipEntry { Serial = sb + 4, ItemId = 0x3EA2, Layer = 0x19, Hue = 0 };
                else if (i % 3 == 0) _equip[ne++] = new EquipEntry { Serial = sb + 4, ItemId = 0x13B9, Layer = 0x01, Hue = 0 };
            }
            _sink.OnMobile(new MobileState { Serial = n.Serial, Body = n.Body, X = n.X, Y = n.Y, Z = n.Z, Direction = (Direction)n.Dir, Hue = n.Hue, Notoriety = n.Noto, Source = MobileSource.Incoming78 }, _equip.AsSpan(0, ne));
        }

        // ground items: a mix of plain, hued and partial-hued art
        ReadOnlySpan<ushort> ids = [0x0E75, 0x0EED, 0x09B1, 0x13B2, 0x0F51, 0x0E21, 0x1F14, 0x0F3F, 0x0E7C, 0x09F1, 0x1BFB, 0x0FAF, 0x0A2A, 0x0B4F, 0x1517, 0x170F];
        ReadOnlySpan<ushort> hues = [0, 0, 0x0021, 0x0059, 0x0044, 0x8035, 0x0481, 0x0000];
        for (int i = 0; i < _sc.Items; i++)
        {
            int x = _sc.X + _rng.Next(-r, r + 1), y = _sc.Y + _rng.Next(-r, r + 1);
            sbyte iz = _map.SpawnZ(x, y, out var sz) ? sz : _map.TileZ(x, y);
            _sink.OnItem(new ItemState { Serial = 0x40100000u + (uint)i, ItemId = ids[i % ids.Length], Amount = 1, X = (ushort)x, Y = (ushort)y, Z = iz, Hue = hues[i % hues.Length], Kind = ItemKind.Item });
        }

        State = ClientState.InWorld;
        _sink.OnStateChanged(State, default);
        _sink.OnLoginComplete();
        _nextWander = _clock.Now;
    }

    public int Poll(int waitMs = 0)
    {
        if (_connected && State == ClientState.Disconnected) { Login(); return 1; }
        if (State != ClientState.InWorld) return 0;
        long now = _clock.Now;
        int n = 0;
        while (_events.Count > 0 && _events[0].Due <= now)
        {
            var e = _events[0];
            _events.RemoveAt(0);
            Deliver(e, now);
            n++;
        }

        if (_sc.WanderPerSecond > 0 && _npcs.Length > 0 && now >= _nextWander)
        {
            _nextWander = now + Clock.Ms(1000.0 / _sc.WanderPerSecond);
            WanderOne(now);
        }
        return n;
    }

    void Schedule(Event e)
    {
        int i = _events.Count;
        while (i > 0 && _events[i - 1].Due > e.Due) i--;
        _events.Insert(i, e);
    }

    public int RequestMove(Direction direction)
    {
        if (State != ClientState.InWorld || _pendingCount >= 5) return -1;
        byte seq = _seq;
        _pending[_pendingCount++] = new Pending { Seq = seq, Dir = direction, Sent = _clock.Now };
        _seq = seq == 255 ? (byte)1 : (byte)(seq + 1);

        // ---- server side, evaluated on arrival (ModernUO MovementReq)
        int d = (int)(direction & Direction.Mask);
        bool ok;
        if (_serverSeq == 0 && seq != 0) ok = false;
        else if (d != ServerDir) { ServerDir = (byte)d; ok = true; }          // turn
        else
        {
            int x = ServerX, y = ServerY, dir = d;
            sbyte z = ServerZ;
            var saved = _map.ExtraBlocked;
            _map.ExtraBlocked = ServerBlocked;
            ok = _map.CanWalk(ref dir, ref x, ref y, ref z) && dir == d;
            _map.ExtraBlocked = saved;
            if (ok) { ServerX = x; ServerY = y; ServerZ = z; }
        }
        if (ok)
        {
            _serverSeq = seq == 255 ? 1 : seq + 1;
            AcksSent++;
        }
        else
        {
            _serverSeq = 0;
            RejectsSent++;
        }
        Schedule(new Event { Due = _clock.Now + LatencyTicks, Kind = ok ? Kind.Ack : Kind.Reject, Seq = seq });
        return seq;
    }

    void Deliver(in Event e, long now)
    {
        switch (e.Kind)
        {
            case Kind.Ack:
                {
                    int i = IndexOf(e.Seq);
                    if (i < 0) { _sink.OnMoveResult(new MoveResult { Sequence = e.Seq, Accepted = true, SentTimestamp = now, ReceivedTimestamp = now, StillPending = _pendingCount }); return; }
                    var pm = _pending[i];
                    int keep = _pendingCount - (i + 1);
                    Array.Copy(_pending, i + 1, _pending, 0, keep);
                    _pendingCount = keep;
                    _sink.OnMoveResult(new MoveResult { Sequence = e.Seq, Direction = pm.Dir, Accepted = true, Notoriety = 1, SentTimestamp = pm.Sent, ReceivedTimestamp = now, StillPending = _pendingCount });
                    break;
                }
            case Kind.Reject:
                {
                    int i = IndexOf(e.Seq);
                    var pm = i >= 0 ? _pending[i] : new Pending { Seq = e.Seq, Sent = now };
                    int dropped = _pendingCount - (i >= 0 ? 1 : 0);
                    _pendingCount = 0;
                    _seq = 0;
                    _sink.OnMoveResult(new MoveResult
                    {
                        Sequence = e.Seq, Direction = pm.Dir, Accepted = false, X = (ushort)ServerX, Y = (ushort)ServerY, Z = ServerZ,
                        ServerDirection = (Direction)ServerDir, SentTimestamp = pm.Sent, ReceivedTimestamp = now,
                    });
                    _sink.OnMoveSequenceReset(MoveResetReason.Rejected, dropped);
                    break;
                }
        }
    }

    void WanderOne(long now)
    {
        int i = _rng.Next(_npcs.Length);
        ref var n = ref _npcs[i];
        int dir = _rng.Next(8), x = n.X, y = n.Y;
        sbyte z = n.Z;
        if (!_map.CanWalk(ref dir, ref x, ref y, ref z)) return;
        if (Math.Abs(x - _sc.X) > _sc.Radius + 4 || Math.Abs(y - _sc.Y) > _sc.Radius + 4) return; // stay near the start
        n.X = (ushort)x; n.Y = (ushort)y; n.Z = z; n.Dir = (byte)dir;
        var d = (Direction)n.Dir | (_rng.Next(4) == 0 ? Direction.Running : 0);
        _sink.OnMobile(new MobileState { Serial = n.Serial, Body = n.Body, X = n.X, Y = n.Y, Z = n.Z, Direction = d, Hue = n.Hue, Notoriety = n.Noto, Source = MobileSource.Moving77 }, default);
    }

    int IndexOf(byte seq)
    {
        for (int i = 0; i < _pendingCount; i++) if (_pending[i].Seq == seq) return i;
        return -1;
    }

    /// <summary>Teleports the player server-side (0x20 + sequence reset), like a [go command.</summary>
    public void Teleport(int x, int y)
    {
        ServerX = x; ServerY = y;
        ServerZ = _map.SpawnZ(x, y, out var z) ? z : _map.TileZ(x, y);
        int dropped = _pendingCount;
        _pendingCount = 0; _seq = 0; _serverSeq = 0;
        _sink.OnMoveSequenceReset(MoveResetReason.PlayerUpdate, dropped);
        _sink.OnMobile(new MobileState { Serial = PlayerSerial, Body = 0x190, X = (ushort)x, Y = (ushort)y, Z = ServerZ, Direction = (Direction)ServerDir, Hue = 0x83EA, IsPlayer = true, Source = MobileSource.Update20 }, default);
    }

    public void Say(ReadOnlySpan<char> text) { }

    public void Disconnect() { State = ClientState.Disconnected; _connected = false; }

    public void Dispose() { }
}
