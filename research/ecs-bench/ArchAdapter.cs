using Arch.Core;
using Schedulers;
using ArchWorld = Arch.Core.World;
using ArchEntity = Arch.Core.Entity;

namespace EcsBench;

public sealed class ArchAdapter : Adapter
{
    public override string Name => "Arch";

    private static JobScheduler _scheduler;
    private readonly ArchWorld _w = ArchWorld.Create();
    private ArchEntity[] _mobiles, _items;
    private readonly Dictionary<uint, ArchEntity> _bySerial = new();
    private readonly List<ArchEntity> _scratch = new(8192);
    private readonly List<ArchEntity>[] _blocks = [new(4096), new(4096), new(4096)];

    private static readonly QueryDescription Render = new QueryDescription().WithAll<Pos, Graphic, Depth>();
    private static readonly QueryDescription MobTick = new QueryDescription().WithAll<Pos, Anim, Motion, Mobile>();
    private static readonly QueryDescription EffectQ = new QueryDescription().WithAll<Pos, Effect, Vel>();
    private static readonly QueryDescription DirtyQ = new QueryDescription().WithAll<Hue, Graphic, Dirty>();
    private static readonly QueryDescription Q1 = new QueryDescription().WithAll<Pos, Serial, Mobile, InRange>();
    private static readonly QueryDescription Q2 = new QueryDescription().WithAll<Pos, Hue, OnGround, InRange>();
    private static readonly QueryDescription Q3 = new QueryDescription().WithAll<Pos, Mobile>().WithNone<Hidden>();
    private static readonly QueryDescription Q4 = new QueryDescription().WithAll<Effect>();
    private static readonly QueryDescription Q5 = new QueryDescription().WithAll<Hue, Item, InRange>();
    private static readonly QueryDescription Q6 = new QueryDescription().WithAll<Depth, Mobile, InRange>();

    public override void Populate()
    {
        _scheduler ??= new JobScheduler(new JobScheduler.Config
        {
            ThreadPrefixName = "Arch", ThreadCount = int.TryParse(Environment.GetEnvironmentVariable("BENCH_THREADS"), out var bt) ? bt : Environment.ProcessorCount / 2, MaxExpectedConcurrentJobs = 256, StrictAllocationMode = false,
        });
        ArchWorld.SharedJobScheduler = _scheduler;

        var spec = WorldSpec.Instance;
        for (int i = 0; i < WorldSpec.MapW * WorldSpec.MapH; i++)
            _w.Create(WorldSpec.LandPos(i), new Graphic { Value = (ushort)(i & 0x3FFF) }, new Depth(), new Land());
        for (int i = 0; i < WorldSpec.Statics; i++)
            _w.Create(spec.StaticPos[i], new Graphic { Value = spec.StaticGfx[i] }, new Hue { Value = (ushort)(i & 0xFF) }, new Depth(), new Static());

        _items = new ArchEntity[WorldSpec.Items];
        for (int i = 0; i < WorldSpec.Items; i++)
        {
            var g = new Graphic { Value = (ushort)(0x0E00 + (i & 0xFF)) }; var s = new Serial { Value = WorldSpec.ItemSerial(i) };
            var e = i < WorldSpec.ItemsOnGround
                ? _w.Create(spec.ItemPos[i], g, new Hue(), new Depth(), s, new Item(), new OnGround())
                : _w.Create(spec.ItemPos[i], g, new Hue(), new Depth(), s, new Item());
            _items[i] = e; _bySerial[WorldSpec.ItemSerial(i)] = e;
        }
        _mobiles = new ArchEntity[WorldSpec.Mobiles];
        for (int i = 0; i < WorldSpec.Mobiles; i++)
        {
            var e = _w.Create(spec.MobilePos[i], new Graphic { Value = (ushort)(0x190 + (i & 3)) }, new Hue(), new Depth(),
                new Serial { Value = WorldSpec.MobileSerial(i) }, new Anim(), new Motion { Speed = 0.05f + (i % 10) * 0.01f }, new Dir(), new Mobile());
            if (i < WorldSpec.HiddenMobiles) _w.Add<Hidden>(e);
            if (i == WorldSpec.Mobiles - 1) { AddMany<N0>(); AddMany<N1>(); AddMany<N2>(); AddMany<N3>(); AddMany<N4>(); AddMany<N5>(); AddMany<N6>(); AddMany<N7>(); AddMany<N8>(); AddMany<N9>(); AddMany<N10>(); AddMany<N11>(); AddMany<N12>(); AddMany<N13>(); AddMany<N14>(); AddMany<N15>(); AddMany<N16>(); AddMany<N17>(); AddMany<N18>(); AddMany<N19>(); }
            _mobiles[i] = e; _bySerial[WorldSpec.MobileSerial(i)] = e;
        }
    }

    public override long RenderExtract()
    {
        long sum = 0;
        foreach (ref var chunk in _w.Query(in Render))
        {
            var ps = chunk.GetSpan<Pos>(); var gs = chunk.GetSpan<Graphic>(); var ds = chunk.GetSpan<Depth>();
            int n = chunk.Count;
            for (int i = 0; i < n; i++)
            {
                var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                ds[i].Value = d;
                if (WorldSpec.InView(ps[i])) sum += d + 1_000_000_000L;
            }
        }
        return sum;
    }

    private static long _parSum;
    private struct RenderJob : IChunkJob
    {
        public void Execute(ref Chunk chunk)
        {
            var ps = chunk.GetSpan<Pos>(); var gs = chunk.GetSpan<Graphic>(); var ds = chunk.GetSpan<Depth>();
            int n = chunk.Count;
            long local = 0;
            for (int i = 0; i < n; i++)
            {
                var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                ds[i].Value = d;
                if (WorldSpec.InView(ps[i])) local += d + 1_000_000_000L;
            }
            Interlocked.Add(ref _parSum, local);
        }
    }

    public override long RenderExtractParallel()
    {
        _parSum = 0;
        _w.InlineParallelChunkQuery(in Render, new RenderJob());
        return _parSum;
    }

    public override long MobileTick()
    {
        long sum = 0;
        foreach (ref var chunk in _w.Query(in MobTick))
        {
            var ps = chunk.GetSpan<Pos>(); var an = chunk.GetSpan<Anim>(); var mo = chunk.GetSpan<Motion>();
            int n = chunk.Count;
            for (int i = 0; i < n; i++)
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

    public override long ChurnFrame() => ChurnCore() + _w.CountEntities(in Q1) + _w.CountEntities(in Q2) + _w.CountEntities(in Q3)
             + _w.CountEntities(in Q4) + _w.CountEntities(in Q5) + _w.CountEntities(in Q6);

    public override long ChurnManyQueries()
    {
        var sum = ChurnCore();
        foreach (var q in _many) sum += _w.CountEntities(in q);
        return sum;
    }

    private readonly List<QueryDescription> _many = new();
    private void AddMany<TN>() where TN : struct
    {
        _many.Add(new QueryDescription().WithAll<Pos, Serial, Mobile, InRange>().WithNone<TN>());
        _many.Add(new QueryDescription().WithAll<Pos, Hue, OnGround, InRange>().WithNone<TN>());
        _many.Add(new QueryDescription().WithAll<Pos, Mobile>().WithNone<Hidden, TN>());
        _many.Add(new QueryDescription().WithAll<Effect>().WithNone<TN>());
        _many.Add(new QueryDescription().WithAll<Hue, Item, InRange>().WithNone<TN>());
        _many.Add(new QueryDescription().WithAll<Depth, Mobile, InRange>().WithNone<TN>());
    }

    private long ChurnCore()
    {
        Frame++;
        for (int k = 0; k < WorldSpec.ToggleMobiles; k++) Toggle(_mobiles[Rng.Next(WorldSpec.Mobiles)]);
        for (int k = 0; k < WorldSpec.ToggleItems; k++) Toggle(_items[Rng.Next(WorldSpec.Items)]);

        for (int i = 0; i < WorldSpec.EffectsPerFrame; i++)
            _w.Create(EffectPos(Frame, i), NewEffect(i), new Vel { X = 0.5f, Y = 0.25f }, new Depth());

        long sum = 0;
        _scratch.Clear();
        foreach (ref var chunk in _w.Query(in EffectQ))
        {
            var ps = chunk.GetSpan<Pos>(); var ef = chunk.GetSpan<Effect>(); var ents = chunk.Entities;
            int n = chunk.Count;
            for (int i = 0; i < n; i++)
            {
                ps[i].X = (ushort)((ps[i].X + 1) % WorldSpec.MapW);
                if (--ef[i].Life == 0) _scratch.Add(ents[i]);
                sum += ef[i].Life;
            }
        }
        foreach (var e in _scratch) _w.Destroy(e);
        return sum;
    }

    private void Toggle(ArchEntity e)
    {
        if (_w.Has<InRange>(e)) _w.Remove<InRange>(e); else _w.Add<InRange>(e);
    }

    public override long BlockStream()
    {
        Frame++;
        var fresh = _blocks[Frame % 3];
        var old = _blocks[(Frame + 1) % 3];
        foreach (var e in old) _w.Destroy(e);
        old.Clear();

        long sum = 0;
        for (int b = 0; b < WorldSpec.BlocksPerOp; b++)
        {
            int seq = Frame * WorldSpec.BlocksPerOp + b;
            for (int i = 0; i < WorldSpec.LandPerBlock; i++)
            {
                var p = BlockLandPos(seq, i);
                fresh.Add(_w.Create(p, new Graphic { Value = (ushort)i }, new Depth(), new Land()));
                sum += p.X;
            }
            for (int i = 0; i < WorldSpec.StaticsPerBlock; i++)
            {
                var p = BlockLandPos(seq, i & 63); p.Z = (sbyte)i;
                fresh.Add(_w.Create(p, new Graphic { Value = (ushort)(i + 100) }, new Hue(), new Depth(), new Static()));
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
            _w.Get<Hue>(e).Value = (ushort)k;
            if (!_w.Has<Dirty>(e)) _w.Add<Dirty>(e);
        }
        long sum = 0;
        foreach (ref var chunk in _w.Query(in DirtyQ))
        {
            var hs = chunk.GetSpan<Hue>();
            int n = chunk.Count;
            for (int i = 0; i < n; i++) sum += hs[i].Value + 1;
        }
        _w.Remove<Dirty>(in DirtyQ); // native bulk structural change by query
        return sum;
    }

    public override long Snapshot()
    {
        long sum = 0;
        int n = 0;
        foreach (ref var chunk in _w.Query(in MobTick))
        {
            int c = chunk.Count;
            chunk.GetSpan<Pos>()[..c].CopyTo(SnapPos.AsSpan(n));
            chunk.GetSpan<Motion>()[..c].CopyTo(SnapMotion.AsSpan(n));
            chunk.GetSpan<Anim>()[..c].CopyTo(SnapAnim.AsSpan(n));
            n += c;
        }
        n = 0;
        foreach (ref var chunk in _w.Query(in MobTick))
        {
            int c = chunk.Count;
            var ps = chunk.GetSpan<Pos>();
            SnapPos.AsSpan(n, c).CopyTo(ps);
            SnapMotion.AsSpan(n, c).CopyTo(chunk.GetSpan<Motion>());
            SnapAnim.AsSpan(n, c).CopyTo(chunk.GetSpan<Anim>());
            for (int i = 0; i < c; i++) sum += ps[i].X;
            n += c;
        }
        return sum;
    }

    public override void Dispose() => ArchWorld.Destroy(_w);
}
