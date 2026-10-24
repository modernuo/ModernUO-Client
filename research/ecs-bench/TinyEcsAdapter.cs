using TinyEcs;
using EcsID = ulong;

namespace EcsBench;

public sealed class TinyEcsAdapter : Adapter
{
    public override string Name => "TinyEcs";

    private readonly World _w = new();
    private EcsID[] _mobiles, _items;
    private readonly Dictionary<uint, EcsID> _bySerial = new();
    private readonly List<EcsID> _effects = new(8192), _scratch = new(8192);
    private readonly List<EcsID>[] _blocks = [new(4096), new(4096), new(4096)];
    private readonly List<Archetype> _archScratch = new();

    private Archetype _landArch, _staticArch, _effectArch;
    private Query _render, _mobileTick, _effectQ, _dirtyQ, _itemHueQ, _snapQ;
    private Query _q1, _q2, _q3, _q4, _q5, _q6;

    public override void Populate()
    {
        var spec = WorldSpec.Instance;
        _landArch = _w.Archetype<Pos, Graphic, Depth, Land>();
        _staticArch = _w.Archetype<Pos, Graphic, Hue, Depth, Static>();
        var itemGround = _w.Archetype<Pos, Graphic, Hue, Depth, Serial, Item, OnGround>();
        var itemCont = _w.Archetype<Pos, Graphic, Hue, Depth, Serial, Item>();
        var mob = _w.Archetype<Pos, Graphic, Hue, Depth, Serial, Anim, Motion, Dir, Mobile>();
        _effectArch = _w.Archetype<Pos, Effect, Vel, Depth>();

        for (int i = 0; i < WorldSpec.MapW * WorldSpec.MapH; i++)
        {
            var e = _w.Entity(_landArch);
            e.Set(WorldSpec.LandPos(i)); e.Set(new Graphic { Value = (ushort)(i & 0x3FFF) });
        }
        for (int i = 0; i < WorldSpec.Statics; i++)
        {
            var e = _w.Entity(_staticArch);
            e.Set(spec.StaticPos[i]); e.Set(new Graphic { Value = spec.StaticGfx[i] }); e.Set(new Hue { Value = (ushort)(i & 0xFF) });
        }
        _items = new EcsID[WorldSpec.Items];
        for (int i = 0; i < WorldSpec.Items; i++)
        {
            var e = _w.Entity(i < WorldSpec.ItemsOnGround ? itemGround : itemCont);
            e.Set(spec.ItemPos[i]); e.Set(new Graphic { Value = (ushort)(0x0E00 + (i & 0xFF)) }); e.Set(new Serial { Value = WorldSpec.ItemSerial(i) });
            _items[i] = e.ID; _bySerial[WorldSpec.ItemSerial(i)] = e.ID;
        }
        _mobiles = new EcsID[WorldSpec.Mobiles];
        for (int i = 0; i < WorldSpec.Mobiles; i++)
        {
            var e = _w.Entity(mob);
            e.Set(spec.MobilePos[i]); e.Set(new Graphic { Value = (ushort)(0x190 + (i & 3)) }); e.Set(new Serial { Value = WorldSpec.MobileSerial(i) });
            e.Set(new Motion { Speed = 0.05f + (i % 10) * 0.01f });
            if (i < WorldSpec.HiddenMobiles) e.Add<Hidden>();
            _mobiles[i] = e.ID; _bySerial[WorldSpec.MobileSerial(i)] = e.ID;
        }

        _render = _w.QueryBuilder().With<Pos>().With<Graphic>().With<Depth>().Build();
        _mobileTick = _w.QueryBuilder().With<Pos>().With<Anim>().With<Motion>().With<Mobile>().Build();
        _snapQ = _mobileTick;
        _effectQ = _w.QueryBuilder().With<Pos>().With<Effect>().With<Vel>().Build();
        _dirtyQ = _w.QueryBuilder().With<Hue>().With<Graphic>().With<Dirty>().Build();
        _itemHueQ = _w.QueryBuilder().With<Hue>().With<Item>().Build();
        _q1 = _w.QueryBuilder().With<Pos>().With<Serial>().With<Mobile>().With<InRange>().Build();
        _q2 = _w.QueryBuilder().With<Pos>().With<Hue>().With<OnGround>().With<InRange>().Build();
        _q3 = _w.QueryBuilder().With<Pos>().With<Mobile>().Without<Hidden>().Build();
        _q4 = _w.QueryBuilder().With<Effect>().Build();
        _q5 = _w.QueryBuilder().With<Hue>().With<Item>().With<InRange>().Build();
        _q6 = _w.QueryBuilder().With<Depth>().With<Mobile>().With<InRange>().Build();
        AddMany<N0>(); AddMany<N1>(); AddMany<N2>(); AddMany<N3>(); AddMany<N4>(); AddMany<N5>(); AddMany<N6>();
        AddMany<N7>(); AddMany<N8>(); AddMany<N9>(); AddMany<N10>(); AddMany<N11>(); AddMany<N12>(); AddMany<N13>();
        AddMany<N14>(); AddMany<N15>(); AddMany<N16>(); AddMany<N17>(); AddMany<N18>(); AddMany<N19>();
    }

    public override long RenderExtract()
    {
        long sum = 0;
        var it = _render.Iter();
        while (it.Next())
        {
            var ps = it.Data<Pos>(it.GetColumnIndexOf<Pos>());
            var gs = it.Data<Graphic>(it.GetColumnIndexOf<Graphic>());
            var ds = it.Data<Depth>(it.GetColumnIndexOf<Depth>());
            for (int i = 0; i < ps.Length; i++)
            {
                var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                ds[i].Value = d;
                if (WorldSpec.InView(ps[i])) sum += d + 1_000_000_000L;
            }
        }
        return sum;
    }

    private static long _parSum;
    private static readonly ParallelOptions ParOpts = new() { MaxDegreeOfParallelism = int.TryParse(Environment.GetEnvironmentVariable("BENCH_THREADS"), out var t) ? t : Environment.ProcessorCount / 2 };
    public override long RenderExtractParallel()
    {
        // No native intra-query parallelism: collect matched archetypes, split rows manually.
        _archScratch.Clear();
        var it = _render.Iter();
        while (it.Next()) _archScratch.Add(it.Archetype);
        _parSum = 0;
        const int Batch = 16384;
        foreach (var arch in _archScratch)
        {
            int ci = arch.GetComponentIndex<Pos>(), cg = arch.GetComponentIndex<Graphic>(), cd = arch.GetComponentIndex<Depth>();
            int n = arch.Count, batches = (n + Batch - 1) / Batch;
            Parallel.For(0, batches, ParOpts, b =>
            {
                var ps = arch.GetSpan<Pos>(ci); var gs = arch.GetSpan<Graphic>(cg); var ds = arch.GetSpan<Depth>(cd);
                int s = b * Batch, e = Math.Min(n, s + Batch);
                long local = 0;
                for (int i = s; i < e; i++)
                {
                    var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                    ds[i].Value = d;
                    if (WorldSpec.InView(ps[i])) local += d + 1_000_000_000L;
                }
                Interlocked.Add(ref _parSum, local);
            });
        }
        return _parSum;
    }

    public override long MobileTick()
    {
        long sum = 0;
        var it = _mobileTick.Iter();
        while (it.Next())
        {
            var ps = it.Data<Pos>(it.GetColumnIndexOf<Pos>());
            var an = it.Data<Anim>(it.GetColumnIndexOf<Anim>());
            var mo = it.Data<Motion>(it.GetColumnIndexOf<Motion>());
            for (int i = 0; i < ps.Length; i++)
            {
                TickMobile(ref ps[i], ref an[i], ref mo[i]);
                sum += ps[i].X + an[i].Frame;
            }
        }
        return sum;
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
            ref var hue = ref _w.Get<Hue>(e);
            hue.Value++;
            sum += _w.Get<Pos>(e).X + hue.Value;
        }
        return sum;
    }

    public override long ChurnFrame() => ChurnCore() + Sum(_q1) + Sum(_q2) + Sum(_q3) + Sum(_q4) + Sum(_q5) + Sum(_q6);

    public override long ChurnManyQueries()
    {
        var sum = ChurnCore();
        foreach (var q in _many) sum += Sum(q);
        return sum;
    }

    private readonly List<Query> _many = new();
    private void AddMany<TN>() where TN : struct
    {
        _many.Add(_w.QueryBuilder().With<Pos>().With<Serial>().With<Mobile>().With<InRange>().Without<TN>().Build());
        _many.Add(_w.QueryBuilder().With<Pos>().With<Hue>().With<OnGround>().With<InRange>().Without<TN>().Build());
        _many.Add(_w.QueryBuilder().With<Pos>().With<Mobile>().Without<Hidden>().Without<TN>().Build());
        _many.Add(_w.QueryBuilder().With<Effect>().Without<TN>().Build());
        _many.Add(_w.QueryBuilder().With<Hue>().With<Item>().With<InRange>().Without<TN>().Build());
        _many.Add(_w.QueryBuilder().With<Depth>().With<Mobile>().With<InRange>().Without<TN>().Build());
    }

    private long ChurnCore()
    {
        Frame++;
        for (int k = 0; k < WorldSpec.ToggleMobiles; k++) Toggle(_mobiles[Rng.Next(WorldSpec.Mobiles)]);
        for (int k = 0; k < WorldSpec.ToggleItems; k++) Toggle(_items[Rng.Next(WorldSpec.Items)]);

        for (int i = 0; i < WorldSpec.EffectsPerFrame; i++)
        {
            var e = _w.Entity(_effectArch);
            e.Set(EffectPos(Frame, i)); e.Set(NewEffect(i)); e.Set(new Vel { X = 0.5f, Y = 0.25f });
        }

        long sum = 0;
        _scratch.Clear();
        var it = _effectQ.Iter();
        while (it.Next())
        {
            var ps = it.Data<Pos>(it.GetColumnIndexOf<Pos>());
            var ef = it.Data<Effect>(it.GetColumnIndexOf<Effect>());
            var ents = it.Entities();
            for (int i = 0; i < ef.Length; i++)
            {
                ps[i].X = (ushort)((ps[i].X + 1) % WorldSpec.MapW);
                if (--ef[i].Life == 0) _scratch.Add(ents[i].ID);
                sum += ef[i].Life;
            }
        }
        foreach (var e in _scratch) _w.Delete(e);
        return sum;
    }

    private void Toggle(EcsID e)
    {
        if (_w.Has<InRange>(e)) _w.Unset<InRange>(e); else _w.Add<InRange>(e);
    }

    private static long Sum(Query q)
    {
        long n = 0;
        var it = q.Iter();
        while (it.Next()) n += it.Count;
        return n;
    }

    public override long BlockStream()
    {
        Frame++;
        var fresh = _blocks[Frame % 3];
        var old = _blocks[(Frame + 1) % 3];
        foreach (var e in old) _w.Delete(e);
        old.Clear();

        long sum = 0;
        for (int b = 0; b < WorldSpec.BlocksPerOp; b++)
        {
            int seq = Frame * WorldSpec.BlocksPerOp + b;
            for (int i = 0; i < WorldSpec.LandPerBlock; i++)
            {
                var e = _w.Entity(_landArch);
                var p = BlockLandPos(seq, i);
                e.Set(p); e.Set(new Graphic { Value = (ushort)i });
                fresh.Add(e.ID); sum += p.X;
            }
            for (int i = 0; i < WorldSpec.StaticsPerBlock; i++)
            {
                var e = _w.Entity(_staticArch);
                var p = BlockLandPos(seq, i & 63); p.Z = (sbyte)i;
                e.Set(p); e.Set(new Graphic { Value = (ushort)(i + 100) });
                fresh.Add(e.ID); sum += p.Y;
            }
        }
        return sum;
    }

    public override long DirtyTag()
    {
        for (int k = 0; k < WorldSpec.DirtyPerFrame; k++)
        {
            var e = _items[Rng.Next(WorldSpec.Items)];
            _w.Get<Hue>(e).Value = (ushort)k;
            if (!_w.Has<Dirty>(e)) _w.Add<Dirty>(e);
        }
        long sum = 0;
        _scratch.Clear();
        var it = _dirtyQ.Iter();
        while (it.Next())
        {
            var hs = it.Data<Hue>(it.GetColumnIndexOf<Hue>());
            var ents = it.Entities();
            for (int i = 0; i < hs.Length; i++) { sum += hs[i].Value + 1; _scratch.Add(ents[i].ID); }
        }
        foreach (var e in _scratch) _w.Unset<Dirty>(e);
        return sum;
    }

    private uint _lastScan;
    public override long DirtyTicks()
    {
        // Bevy-style change detection: no structural change, consumer scans per-row ticks.
        _w.BumpTick();
        for (int k = 0; k < WorldSpec.DirtyPerFrame; k++)
        {
            var e = _items[Rng.Next(WorldSpec.Items)];
            _w.Get<Hue>(e).Value = (ushort)k;
            _w.SetChanged<Hue>(e);
        }
        var now = _w.BumpTick();
        long sum = 0;
        var it = _itemHueQ.Iter();
        while (it.Next())
        {
            var col = it.GetColumnIndexOf<Hue>();
            var hs = it.Data<Hue>(col);
            var ticks = it.GetChangedTicks(col);
            for (int i = 0; i < hs.Length; i++)
                if (ChangeTick.InWindow(ticks[i], _lastScan, now)) sum += hs[i].Value + 1;
        }
        _lastScan = now;
        return sum;
    }

    public override long Snapshot()
    {
        long sum = 0;
        int n = 0;
        var it = _snapQ.Iter();
        while (it.Next())
        {
            var ps = it.Data<Pos>(it.GetColumnIndexOf<Pos>());
            ps.CopyTo(SnapPos.AsSpan(n));
            it.Data<Motion>(it.GetColumnIndexOf<Motion>()).CopyTo(SnapMotion.AsSpan(n));
            it.Data<Anim>(it.GetColumnIndexOf<Anim>()).CopyTo(SnapAnim.AsSpan(n));
            n += ps.Length;
        }
        n = 0;
        it = _snapQ.Iter();
        while (it.Next())
        {
            var ps = it.Data<Pos>(it.GetColumnIndexOf<Pos>());
            SnapPos.AsSpan(n, ps.Length).CopyTo(ps);
            SnapMotion.AsSpan(n, ps.Length).CopyTo(it.Data<Motion>(it.GetColumnIndexOf<Motion>()));
            SnapAnim.AsSpan(n, ps.Length).CopyTo(it.Data<Anim>(it.GetColumnIndexOf<Anim>()));
            for (int i = 0; i < ps.Length; i++) sum += ps[i].X;
            n += ps.Length;
        }
        return sum;
    }

    public override void Dispose() => _w.Dispose();
}
