using Friflo.Engine.ECS;
using Uo.Net;

namespace Uo.Client;

/// <summary>Player movement with client-side prediction (the ClassicUO PlayerMobile.Walk / Walker model):
/// <list type="bullet">
/// <item>a request in a new direction only turns the player (100 ms turn delay); a step is predicted immediately
/// against <see cref="WalkMap"/> and paced 400 ms walking / 200 ms running;</item>
/// <item>at most 5 unacknowledged requests (the client also enforces this);</item>
/// <item>an ack confirms a step; a reject snaps to the server position and drops every pending step;
/// a sequence reset from a player update (teleport) snaps too.</item>
/// </list>
/// The predicted tile is written to the player entity's <see cref="Position"/>, the step interpolation to its <see cref="Motion"/>.</summary>
public sealed class Walker : IPlayerMovement
{
    public const int MaxPending = 5;
    static readonly long TurnDelay = Clock.Ms(100);
    /// <summary>After a reject, rejects for requests already in flight follow; hold input until they have arrived.</summary>
    static readonly long RejectHold = Clock.Ms(250);

    struct PendingStep
    {
        public int Seq;
        public byte Dir;
        public bool Turn;
        public ushort X, Y;
        public sbyte Z;
    }

    readonly IGameClient _client;
    readonly WalkMap _map;
    readonly EcsWorld _world;
    readonly Clock _clock;
    readonly PendingStep[] _pending = new PendingStep[MaxPending];
    int _pendingCount;

    public int X, Y;
    public sbyte Z;
    public byte Facing;               // 0..7
    public int ServerX, ServerY;      // last confirmed position
    public sbyte ServerZ;
    public bool HasPosition;
    long _nextMoveAt;

    // stats
    public int StepsSent, TurnsSent, Acks, Rejects, Blocked, Snaps, CapHits;
    public readonly List<double> Rtts = new(1024);

    public Walker(IGameClient client, WalkMap map, EcsWorld world, Clock clock)
    {
        _client = client; _map = map; _world = world; _clock = clock;
        world.Movement = this;
    }

    public int Pending => _pendingCount;

    /// <summary>Call once per frame after <see cref="IGameClient.Poll"/>. <paramref name="want"/> is the input direction
    /// (0..7) or -1 for none.</summary>
    public void Update(int want, bool run)
    {
        if (!HasPosition || want < 0 || _client.State != ClientState.InWorld) return;
        long now = _clock.Now;
        if (now < _nextMoveAt) return;
        if (_pendingCount >= MaxPending || _client.PendingMoves >= MaxPending) { CapHits++; return; }

        int dir = want, x = X, y = Y;
        sbyte z = Z;
        bool ok = _map.CanWalk(ref dir, ref x, ref y, ref z);
        if (!ok)
        {
            Blocked++;
            if (Facing != want) Turn((byte)want, run, now);
            return;
        }
        if (dir != Facing) { Turn((byte)dir, run, now); return; } // includes a diagonal that slid along a wall

        var d = (Direction)dir | (run ? Direction.Running : 0);
        int seq = _client.RequestMove(d);
        if (seq < 0) { CapHits++; return; }
        long duration = run ? EcsWorld.RunTicks : EcsWorld.WalkTicks;
        _pending[_pendingCount++] = new PendingStep { Seq = seq, Dir = (byte)dir, X = (ushort)x, Y = (ushort)y, Z = z };
        SetPlayer(x, y, z, (byte)dir, interpolateFromCurrent: true, now, duration, run);
        _nextMoveAt = now + duration;
        StepsSent++;
    }

    void Turn(byte dir, bool run, long now)
    {
        int seq = _client.RequestMove((Direction)dir | (run ? Direction.Running : 0));
        if (seq < 0) { CapHits++; return; }
        _pending[_pendingCount++] = new PendingStep { Seq = seq, Dir = dir, Turn = true, X = (ushort)X, Y = (ushort)Y, Z = Z };
        Facing = dir;
        if (!_world.Player.IsNull) _world.Player.GetComponent<Facing>().Value = dir;
        _nextMoveAt = now + TurnDelay;
        TurnsSent++;
    }

    void SetPlayer(int x, int y, sbyte z, byte dir, bool interpolateFromCurrent, long now, long duration, bool run)
    {
        int fx = X, fy = Y;
        sbyte fz = Z;
        X = x; Y = y; Z = z; Facing = dir;
        var e = _world.Player;
        if (e.IsNull) return;
        e.GetComponent<Position>() = new Position { X = (ushort)x, Y = (ushort)y, Z = z };
        e.GetComponent<Facing>().Value = (byte)(dir | (run ? 0x80 : 0));
        e.GetComponent<Motion>() = interpolateFromCurrent
            ? new Motion { FromX = (ushort)fx, FromY = (ushort)fy, FromZ = fz, Start = now, Duration = duration }
            : default;
    }

    void Snap(int x, int y, sbyte z, byte dir)
    {
        _pendingCount = 0;
        ServerX = x; ServerY = y; ServerZ = z;
        SetPlayer(x, y, z, dir, false, 0, 0, false);
        HasPosition = true;
    }

    // ------------------------------------------------------------------ IPlayerMovement

    public void OnServerPosition(ushort x, ushort y, sbyte z, Direction dir)
    {
        if (HasPosition && (x != X || y != Y || z != Z)) Snaps++;
        Snap(x, y, z, (byte)(dir & Direction.Mask));
        _map.Preload(x, y);
    }

    public void OnMoveResult(in MoveResult r)
    {
        if (r.Accepted)
        {
            int i = IndexOf(r.Sequence);
            if (i < 0) return; // ack for a request dropped by a reset
            Acks++;
            Rtts.Add(r.RttMs);
            ref readonly var s = ref _pending[i];
            if (!s.Turn) { ServerX = s.X; ServerY = s.Y; ServerZ = s.Z; }
            int keep = _pendingCount - (i + 1);
            Array.Copy(_pending, i + 1, _pending, 0, keep);
            _pendingCount = keep;
            return;
        }

        Rejects++;
        Snaps++;
        Snap(r.X, r.Y, r.Z, (byte)(r.ServerDirection & Direction.Mask));
        _nextMoveAt = _clock.Now + RejectHold;
    }

    public void OnMoveSequenceReset(MoveResetReason reason, int droppedPending)
    {
        // Rejected: handled by OnMoveResult. PlayerUpdate/MapChange: the 0x20 / map packets carry the new position.
        if (reason != MoveResetReason.Rejected) _pendingCount = 0;
    }

    int IndexOf(int seq)
    {
        for (int i = 0; i < _pendingCount; i++) if (_pending[i].Seq == seq) return i;
        return -1;
    }

    /// <summary>Input direction from a screen-space vector (right, down): the closest UO direction. Screen up is UO
    /// direction 7 (Up, map north-west), then clockwise: 0 North (screen up-right), 1 Right, 2 East, ...</summary>
    public static int DirectionFromScreen(float dx, float dy)
    {
        if (dx * dx + dy * dy < 1e-6f) return -1;
        double a = Math.Atan2(dx, -dy);
        int oct = (int)Math.Round(a / (Math.PI / 4));
        return ((oct + 7) % 8 + 8) % 8;
    }
}
