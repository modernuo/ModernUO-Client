using Flecs.NET.Core;

namespace EcsBench;

/// <summary>
/// Flecs.NET (C# bindings over the flecs C library). Components are the shared plain structs;
/// the empty structs (Land, Mobile, N0..) are registered by Flecs.NET as zero-size tags.
/// All iteration uses cached queries walked per table via <c>Run</c> + <c>while (it.Next())</c>
/// + <c>it.Span&lt;T&gt;(field)</c>. Run delegates are allocated once in Populate (no per-call closures).
/// </summary>
public sealed class FlecsAdapter : Adapter
{
    public override string Name => "Flecs";

    private World _w = World.Create();
    private Entity[] _mobiles, _items;
    private readonly Dictionary<uint, Entity> _bySerial = new();
    private readonly List<Entity> _scratch = new(8192);
    private readonly List<Entity>[] _blocks = [new(4096), new(4096), new(4096)];

    private Table _landTable, _staticTable, _effectTable;
    private static Table Proto(Entity e) { var t = e.Table(); e.Destruct(); return t; }

    private Query<Pos, Graphic, Depth> _render;
    private Query<Pos, Anim, Motion> _mobileTick;
    private Query<Pos, Effect, Vel> _effectQ;
    private Query<Hue, Graphic> _dirtyQ;
    private Query _q1, _q2, _q3, _q4, _q5, _q6;
    private readonly List<Query> _many = new();

    private Ecs.RunCallback _renderRun, _mobileRun, _effectRun, _dirtyRun, _snapOutRun, _snapInRun;
    private long _sum;

    private System<Pos, Graphic, Depth> _renderSys;
    private bool _threadsSet;
    private static long _parSum;
    private static readonly int Threads = int.TryParse(Environment.GetEnvironmentVariable("BENCH_THREADS"), out var t) ? t : Environment.ProcessorCount / 2;

    public override void Populate()
    {
        var spec = WorldSpec.Instance;
        // Resolve each archetype's table once (prototype entity, then deleted; flecs keeps empty tables),
        // then create entities directly in it: one table insert, components set in place.
        // ecs_new_w_table does NOT zero component memory for hook-less structs, so every column is written.
        _landTable = Proto(_w.Entity().Set(new Pos()).Set(new Graphic()).Set(new Depth()).Add<Land>());
        _staticTable = Proto(_w.Entity().Set(new Pos()).Set(new Graphic()).Set(new Hue()).Set(new Depth()).Add<Static>());
        var itemGround = Proto(_w.Entity().Set(new Pos()).Set(new Graphic()).Set(new Hue()).Set(new Depth()).Set(new Serial()).Add<Item>().Add<OnGround>());
        var itemCont = Proto(_w.Entity().Set(new Pos()).Set(new Graphic()).Set(new Hue()).Set(new Depth()).Set(new Serial()).Add<Item>());
        var mob = Proto(_w.Entity().Set(new Pos()).Set(new Graphic()).Set(new Hue()).Set(new Depth()).Set(new Serial())
            .Set(new Anim()).Set(new Motion()).Set(new Dir()).Add<Mobile>());
        _effectTable = Proto(_w.Entity().Set(new Pos()).Set(new Effect()).Set(new Vel()).Set(new Depth()));

        for (int i = 0; i < WorldSpec.MapW * WorldSpec.MapH; i++)
        {
            var e = _w.Entity(_landTable);
            e.GetMut<Pos>() = WorldSpec.LandPos(i); e.GetMut<Graphic>() = new Graphic { Value = (ushort)(i & 0x3FFF) }; e.GetMut<Depth>() = default;
        }
        for (int i = 0; i < WorldSpec.Statics; i++)
        {
            var e = _w.Entity(_staticTable);
            e.GetMut<Pos>() = spec.StaticPos[i]; e.GetMut<Graphic>() = new Graphic { Value = spec.StaticGfx[i] }; e.GetMut<Hue>() = new Hue { Value = (ushort)(i & 0xFF) }; e.GetMut<Depth>() = default;
        }
        _items = new Entity[WorldSpec.Items];
        for (int i = 0; i < WorldSpec.Items; i++)
        {
            var e = _w.Entity(i < WorldSpec.ItemsOnGround ? itemGround : itemCont);
            e.GetMut<Pos>() = spec.ItemPos[i]; e.GetMut<Graphic>() = new Graphic { Value = (ushort)(0x0E00 + (i & 0xFF)) }; e.GetMut<Hue>() = default;
            e.GetMut<Depth>() = default; e.GetMut<Serial>() = new Serial { Value = WorldSpec.ItemSerial(i) };
            _items[i] = e; _bySerial[WorldSpec.ItemSerial(i)] = e;
        }
        _mobiles = new Entity[WorldSpec.Mobiles];
        for (int i = 0; i < WorldSpec.Mobiles; i++)
        {
            var e = _w.Entity(mob);
            e.GetMut<Pos>() = spec.MobilePos[i]; e.GetMut<Graphic>() = new Graphic { Value = (ushort)(0x190 + (i & 3)) }; e.GetMut<Hue>() = default;
            e.GetMut<Depth>() = default; e.GetMut<Serial>() = new Serial { Value = WorldSpec.MobileSerial(i) }; e.GetMut<Anim>() = default;
            e.GetMut<Motion>() = new Motion { Speed = 0.05f + (i % 10) * 0.01f }; e.GetMut<Dir>() = default;
            if (i < WorldSpec.HiddenMobiles) e.Add<Hidden>();
            _mobiles[i] = e; _bySerial[WorldSpec.MobileSerial(i)] = e;
        }

        _render = _w.QueryBuilder<Pos, Graphic, Depth>().Cached().Build();
        _mobileTick = _w.QueryBuilder<Pos, Anim, Motion>().With<Mobile>().Cached().Build();
        _effectQ = _w.QueryBuilder<Pos, Effect, Vel>().Cached().Build();
        _dirtyQ = _w.QueryBuilder<Hue, Graphic>().With<Dirty>().Cached().Build();
        _q1 = _w.QueryBuilder().With<Pos>().With<Serial>().With<Mobile>().With<InRange>().Cached().Build();
        _q2 = _w.QueryBuilder().With<Pos>().With<Hue>().With<OnGround>().With<InRange>().Cached().Build();
        _q3 = _w.QueryBuilder().With<Pos>().With<Mobile>().Without<Hidden>().Cached().Build();
        _q4 = _w.QueryBuilder().With<Effect>().Cached().Build();
        _q5 = _w.QueryBuilder().With<Hue>().With<Item>().With<InRange>().Cached().Build();
        _q6 = _w.QueryBuilder().With<Depth>().With<Mobile>().With<InRange>().Cached().Build();
        AddMany<N0>(); AddMany<N1>(); AddMany<N2>(); AddMany<N3>(); AddMany<N4>(); AddMany<N5>(); AddMany<N6>();
        AddMany<N7>(); AddMany<N8>(); AddMany<N9>(); AddMany<N10>(); AddMany<N11>(); AddMany<N12>(); AddMany<N13>();
        AddMany<N14>(); AddMany<N15>(); AddMany<N16>(); AddMany<N17>(); AddMany<N18>(); AddMany<N19>();

        _renderRun = it =>
        {
            long sum = 0;
            while (it.Next())
            {
                var ps = it.Span<Pos>(0); var gs = it.Span<Graphic>(1); var ds = it.Span<Depth>(2);
                for (int i = 0; i < ps.Length; i++)
                {
                    var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                    ds[i].Value = d;
                    if (WorldSpec.InView(ps[i])) sum += d + 1_000_000_000L;
                }
            }
            _sum = sum;
        };
        _mobileRun = it =>
        {
            long sum = 0;
            while (it.Next())
            {
                var ps = it.Span<Pos>(0); var an = it.Span<Anim>(1); var mo = it.Span<Motion>(2);
                for (int i = 0; i < ps.Length; i++)
                {
                    TickMobile(ref ps[i], ref an[i], ref mo[i]);
                    sum += ps[i].X + an[i].Frame;
                }
            }
            _sum = sum;
        };
        _effectRun = it =>
        {
            long sum = 0;
            while (it.Next())
            {
                var ps = it.Span<Pos>(0); var ef = it.Span<Effect>(1);
                for (int i = 0; i < ef.Length; i++)
                {
                    ps[i].X = (ushort)((ps[i].X + 1) % WorldSpec.MapW);
                    if (--ef[i].Life == 0) _scratch.Add(it.Entity(i));
                    sum += ef[i].Life;
                }
            }
            _sum = sum;
        };
        _dirtyRun = it =>
        {
            long sum = 0;
            while (it.Next())
            {
                var hs = it.Span<Hue>(0);
                for (int i = 0; i < hs.Length; i++) { sum += hs[i].Value + 1; _scratch.Add(it.Entity(i)); }
            }
            _sum = sum;
        };
        _snapOutRun = it =>
        {
            int n = 0;
            while (it.Next())
            {
                var ps = it.Span<Pos>(0);
                ps.CopyTo(SnapPos.AsSpan(n));
                it.Span<Motion>(2).CopyTo(SnapMotion.AsSpan(n));
                it.Span<Anim>(1).CopyTo(SnapAnim.AsSpan(n));
                n += ps.Length;
            }
        };
        _snapInRun = it =>
        {
            int n = 0; long sum = 0;
            while (it.Next())
            {
                var ps = it.Span<Pos>(0);
                SnapPos.AsSpan(n, ps.Length).CopyTo(ps);
                SnapMotion.AsSpan(n, ps.Length).CopyTo(it.Span<Motion>(2));
                SnapAnim.AsSpan(n, ps.Length).CopyTo(it.Span<Anim>(1));
                for (int i = 0; i < ps.Length; i++) sum += ps[i].X;
                n += ps.Length;
            }
            _sum = sum;
        };
    }

    private void AddMany<TN>()
    {
        _many.Add(_w.QueryBuilder().With<Pos>().With<Serial>().With<Mobile>().With<InRange>().Without<TN>().Cached().Build());
        _many.Add(_w.QueryBuilder().With<Pos>().With<Hue>().With<OnGround>().With<InRange>().Without<TN>().Cached().Build());
        _many.Add(_w.QueryBuilder().With<Pos>().With<Mobile>().Without<Hidden>().Without<TN>().Cached().Build());
        _many.Add(_w.QueryBuilder().With<Effect>().Without<TN>().Cached().Build());
        _many.Add(_w.QueryBuilder().With<Hue>().With<Item>().With<InRange>().Without<TN>().Cached().Build());
        _many.Add(_w.QueryBuilder().With<Depth>().With<Mobile>().With<InRange>().Without<TN>().Cached().Build());
    }

    public override long RenderExtract()
    {
        _render.Run(_renderRun);
        return _sum;
    }

    public override long RenderExtractParallel()
    {
        // Native flecs worker threads: a MultiThreaded system splits every matched table's rows across
        // N stages; world.Progress() runs the (single-system) pipeline on the worker pool.
        if (!_threadsSet)
        {
            _w.SetThreads(Threads);
            _renderSys = _w.System<Pos, Graphic, Depth>().MultiThreaded().Run(static it =>
            {
                long local = 0;
                while (it.Next())
                {
                    var ps = it.Span<Pos>(0); var gs = it.Span<Graphic>(1); var ds = it.Span<Depth>(2);
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                        ds[i].Value = d;
                        if (WorldSpec.InView(ps[i])) local += d + 1_000_000_000L;
                    }
                }
                Interlocked.Add(ref _parSum, local);
            });
            _threadsSet = true;
        }
        _parSum = 0;
        _w.Progress();
        return _parSum;
    }

    public override long MobileTick()
    {
        _mobileTick.Run(_mobileRun);
        return _sum;
    }

    public override long PacketLookups()
    {
        long sum = 0;
        var serials = WorldSpec.Instance.LookupSerials;
        for (int i = 0; i < serials.Length; i++)
        {
            var s = serials[i];
            var serial = s < WorldSpec.Items ? WorldSpec.ItemSerial(s) : WorldSpec.MobileSerial(s - WorldSpec.Items);
            var e = _bySerial[serial];
            ref var hue = ref e.GetMut<Hue>();
            hue.Value++;
            sum += e.Get<Pos>().X + hue.Value;
        }
        return sum;
    }

    public override long ChurnFrame() =>
        ChurnCore() + _q1.Count() + _q2.Count() + _q3.Count() + _q4.Count() + _q5.Count() + _q6.Count();

    public override long ChurnManyQueries()
    {
        var sum = ChurnCore();
        foreach (var q in _many) sum += q.Count();
        return sum;
    }

    private long ChurnCore()
    {
        Frame++;
        for (int k = 0; k < WorldSpec.ToggleMobiles; k++) Toggle(_mobiles[Rng.Next(WorldSpec.Mobiles)]);
        for (int k = 0; k < WorldSpec.ToggleItems; k++) Toggle(_items[Rng.Next(WorldSpec.Items)]);

        for (int i = 0; i < WorldSpec.EffectsPerFrame; i++)
        {
            var e = _w.Entity(_effectTable);
            e.GetMut<Pos>() = EffectPos(Frame, i); e.GetMut<Effect>() = NewEffect(i); e.GetMut<Vel>() = new Vel { X = 0.5f, Y = 0.25f }; e.GetMut<Depth>() = default;
        }

        _scratch.Clear();
        _effectQ.Run(_effectRun);
        foreach (var e in _scratch) e.Destruct();
        return _sum;
    }

    private static void Toggle(Entity e)
    {
        if (e.Has<InRange>()) e.Remove<InRange>(); else e.Add<InRange>();
    }

    public override long BlockStream()
    {
        Frame++;
        var fresh = _blocks[Frame % 3];
        var old = _blocks[(Frame + 1) % 3];
        foreach (var e in old) e.Destruct();
        old.Clear();

        long sum = 0;
        for (int b = 0; b < WorldSpec.BlocksPerOp; b++)
        {
            int seq = Frame * WorldSpec.BlocksPerOp + b;
            for (int i = 0; i < WorldSpec.LandPerBlock; i++)
            {
                var p = BlockLandPos(seq, i);
                var e = _w.Entity(_landTable);
                e.GetMut<Pos>() = p; e.GetMut<Graphic>() = new Graphic { Value = (ushort)i }; e.GetMut<Depth>() = default;
                fresh.Add(e);
                sum += p.X;
            }
            for (int i = 0; i < WorldSpec.StaticsPerBlock; i++)
            {
                var p = BlockLandPos(seq, i & 63); p.Z = (sbyte)i;
                var e = _w.Entity(_staticTable);
                e.GetMut<Pos>() = p; e.GetMut<Graphic>() = new Graphic { Value = (ushort)(i + 100) }; e.GetMut<Hue>() = default; e.GetMut<Depth>() = default;
                fresh.Add(e);
                sum += p.Y;
            }
        }
        return sum;
    }

    public override long DirtyTag()
    {
        for (int k = 0; k < WorldSpec.DirtyPerFrame; k++)
        {
            var e = _items[Rng.Next(WorldSpec.Items)];
            e.GetMut<Hue>().Value = (ushort)k;
            if (!e.Has<Dirty>()) e.Add<Dirty>();
        }
        _scratch.Clear();
        _dirtyQ.Run(_dirtyRun);
        foreach (var e in _scratch) e.Remove<Dirty>();
        return _sum;
    }

    // DirtyTicks: flecs change detection (query.Changed()/it.Changed()/it.Skip()) is per table, not per row,
    // so there is no faithful equivalent of TinyEcs's per-row ticks -> base NotSupportedException (n/a).

    public override long Snapshot()
    {
        _mobileTick.Run(_snapOutRun);
        _mobileTick.Run(_snapInRun);
        return _sum;
    }

    public override void Dispose() => _w.Dispose();
}
