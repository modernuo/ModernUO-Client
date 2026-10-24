using Friflo.Engine.ECS;

namespace EcsBench;

public sealed class FrifloAdapter : Adapter
{
    public override string Name => "Friflo";

    private readonly EntityStore _s = new();
    private Entity[] _mobiles, _items;
    private readonly Dictionary<uint, Entity> _bySerial = new();
    private readonly List<Entity> _scratch = new(8192);
    private readonly List<Entity>[] _blocks = [new(4096), new(4096), new(4096)];
    private ParallelJobRunner _runner;

    private ArchetypeQuery<Pos, Graphic, Depth> _render;
    private ArchetypeQuery<Pos, Anim, Motion> _mobileTick;
    private ArchetypeQuery<Pos, Effect, Vel> _effectQ;
    private ArchetypeQuery<Hue, Graphic> _dirtyQ;
    private ArchetypeQuery<Pos, Serial> _q1;
    private ArchetypeQuery<Pos, Hue> _q2;
    private ArchetypeQuery<Pos> _q3;
    private ArchetypeQuery<Effect> _q4;
    private ArchetypeQuery<Hue> _q5;
    private ArchetypeQuery<Depth> _q6;
    private QueryJob<Pos, Graphic, Depth> _renderJob;
    private static long _parSum;

    // NativeAOT has no reflection-based type discovery: Friflo requires every component/tag up front.
    // Must run before any EntityStore exists; an explicit static ctor guarantees it runs before instance field initializers.
    private static readonly EntitySchema Schema;
    static FrifloAdapter() => Schema = CreateSchema();
    private static EntitySchema CreateSchema()
    {
        var aot = new NativeAOT();
        aot.RegisterComponent<Pos>(); aot.RegisterComponent<Graphic>(); aot.RegisterComponent<Hue>(); aot.RegisterComponent<Depth>();
        aot.RegisterComponent<Serial>(); aot.RegisterComponent<Anim>(); aot.RegisterComponent<Motion>(); aot.RegisterComponent<Dir>();
        aot.RegisterComponent<Effect>(); aot.RegisterComponent<Vel>();
        aot.RegisterTag<Land>(); aot.RegisterTag<Static>(); aot.RegisterTag<Item>(); aot.RegisterTag<Mobile>();
        aot.RegisterTag<OnGround>(); aot.RegisterTag<InRange>(); aot.RegisterTag<Hidden>(); aot.RegisterTag<Dirty>();
        aot.RegisterTag<N0>(); aot.RegisterTag<N1>(); aot.RegisterTag<N2>(); aot.RegisterTag<N3>(); aot.RegisterTag<N4>();
        aot.RegisterTag<N5>(); aot.RegisterTag<N6>(); aot.RegisterTag<N7>(); aot.RegisterTag<N8>(); aot.RegisterTag<N9>();
        aot.RegisterTag<N10>(); aot.RegisterTag<N11>(); aot.RegisterTag<N12>(); aot.RegisterTag<N13>(); aot.RegisterTag<N14>();
        aot.RegisterTag<N15>(); aot.RegisterTag<N16>(); aot.RegisterTag<N17>(); aot.RegisterTag<N18>(); aot.RegisterTag<N19>();
        return aot.CreateSchema();
    }

    public override void Populate()
    {
        _ = Schema;
        var spec = WorldSpec.Instance;
        var land = Tags.Get<Land>(); var stat = Tags.Get<Static>();
        for (int i = 0; i < WorldSpec.MapW * WorldSpec.MapH; i++)
            _s.CreateEntity(WorldSpec.LandPos(i), new Graphic { Value = (ushort)(i & 0x3FFF) }, new Depth(), land);
        for (int i = 0; i < WorldSpec.Statics; i++)
            _s.CreateEntity(spec.StaticPos[i], new Graphic { Value = spec.StaticGfx[i] }, new Hue { Value = (ushort)(i & 0xFF) }, new Depth(), stat);

        _items = new Entity[WorldSpec.Items];
        var ground = Tags.Get<Item, OnGround>(); var cont = Tags.Get<Item>();
        for (int i = 0; i < WorldSpec.Items; i++)
        {
            var e = _s.CreateEntity(spec.ItemPos[i], new Graphic { Value = (ushort)(0x0E00 + (i & 0xFF)) }, new Hue(), new Depth(),
                new Serial { Value = WorldSpec.ItemSerial(i) }, i < WorldSpec.ItemsOnGround ? ground : cont);
            _items[i] = e; _bySerial[WorldSpec.ItemSerial(i)] = e;
        }
        _mobiles = new Entity[WorldSpec.Mobiles];
        var mob = Tags.Get<Mobile>(); var mobHidden = Tags.Get<Mobile, Hidden>();
        for (int i = 0; i < WorldSpec.Mobiles; i++)
        {
            var e = _s.CreateEntity(spec.MobilePos[i], new Graphic { Value = (ushort)(0x190 + (i & 3)) }, new Hue(), new Depth(),
                new Serial { Value = WorldSpec.MobileSerial(i) }, new Anim(), new Motion { Speed = 0.05f + (i % 10) * 0.01f }, new Dir(),
                i < WorldSpec.HiddenMobiles ? mobHidden : mob);
            _mobiles[i] = e; _bySerial[WorldSpec.MobileSerial(i)] = e;
        }

        _render = _s.Query<Pos, Graphic, Depth>();
        _mobileTick = _s.Query<Pos, Anim, Motion>().AllTags(Tags.Get<Mobile>());
        _effectQ = _s.Query<Pos, Effect, Vel>();
        _dirtyQ = _s.Query<Hue, Graphic>().AllTags(Tags.Get<Dirty>());
        _q1 = _s.Query<Pos, Serial>().AllTags(Tags.Get<Mobile, InRange>());
        _q2 = _s.Query<Pos, Hue>().AllTags(Tags.Get<OnGround, InRange>());
        _q3 = _s.Query<Pos>().AllTags(Tags.Get<Mobile>()).WithoutAllTags(Tags.Get<Hidden>());
        _q4 = _s.Query<Effect>();
        _q5 = _s.Query<Hue>().AllTags(Tags.Get<Item, InRange>());
        _q6 = _s.Query<Depth>().AllTags(Tags.Get<Mobile, InRange>());
        AddMany<N0>(); AddMany<N1>(); AddMany<N2>(); AddMany<N3>(); AddMany<N4>(); AddMany<N5>(); AddMany<N6>();
        AddMany<N7>(); AddMany<N8>(); AddMany<N9>(); AddMany<N10>(); AddMany<N11>(); AddMany<N12>(); AddMany<N13>();
        AddMany<N14>(); AddMany<N15>(); AddMany<N16>(); AddMany<N17>(); AddMany<N18>(); AddMany<N19>();

        _runner = new ParallelJobRunner(int.TryParse(Environment.GetEnvironmentVariable("BENCH_THREADS"), out var bt) ? bt : Environment.ProcessorCount / 2);
        _renderJob = _render.ForEach((pos, gfx, depth, _) =>
        {
            var ps = pos.Span; var gs = gfx.Span; var ds = depth.Span;
            long local = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                ds[i].Value = d;
                if (WorldSpec.InView(ps[i])) local += d + 1_000_000_000L;
            }
            Interlocked.Add(ref _parSum, local);
        });
        _renderJob.JobRunner = _runner;
    }

    public override long RenderExtract()
    {
        long sum = 0;
        foreach (var (pos, gfx, depth, _) in _render.Chunks)
        {
            var ps = pos.Span; var gs = gfx.Span; var ds = depth.Span;
            for (int i = 0; i < ps.Length; i++)
            {
                var d = WorldSpec.ComputeDepth(ps[i], gs[i].Value);
                ds[i].Value = d;
                if (WorldSpec.InView(ps[i])) sum += d + 1_000_000_000L;
            }
        }
        return sum;
    }

    public override long RenderExtractParallel()
    {
        _parSum = 0;
        _renderJob.RunParallel();
        return _parSum;
    }

    public override long MobileTick()
    {
        long sum = 0;
        foreach (var (pos, anim, motion, _) in _mobileTick.Chunks)
        {
            var ps = pos.Span; var an = anim.Span; var mo = motion.Span;
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
            ref var hue = ref e.GetComponent<Hue>();
            hue.Value++;
            sum += e.GetComponent<Pos>().X + hue.Value;
        }
        return sum;
    }

    public override long ChurnFrame() => ChurnCore() + _q1.Count + _q2.Count + _q3.Count + _q4.Count + _q5.Count + _q6.Count;

    public override long ChurnManyQueries()
    {
        var sum = ChurnCore();
        foreach (var q in _many) sum += q.Count;
        return sum;
    }

    private readonly List<ArchetypeQuery> _many = new();
    private void AddMany<TN>() where TN : struct, ITag
    {
        var n = Tags.Get<TN>();
        _many.Add(_s.Query<Pos, Serial>().AllTags(Tags.Get<Mobile, InRange>()).WithoutAnyTags(n));
        _many.Add(_s.Query<Pos, Hue>().AllTags(Tags.Get<OnGround, InRange>()).WithoutAnyTags(n));
        _many.Add(_s.Query<Pos>().AllTags(Tags.Get<Mobile>()).WithoutAnyTags(Tags.Get<Hidden, TN>()));
        _many.Add(_s.Query<Effect>().WithoutAnyTags(n));
        _many.Add(_s.Query<Hue>().AllTags(Tags.Get<Item, InRange>()).WithoutAnyTags(n));
        _many.Add(_s.Query<Depth>().AllTags(Tags.Get<Mobile, InRange>()).WithoutAnyTags(n));
    }

    private long ChurnCore()
    {
        Frame++;
        for (int k = 0; k < WorldSpec.ToggleMobiles; k++) Toggle(_mobiles[Rng.Next(WorldSpec.Mobiles)]);
        for (int k = 0; k < WorldSpec.ToggleItems; k++) Toggle(_items[Rng.Next(WorldSpec.Items)]);

        for (int i = 0; i < WorldSpec.EffectsPerFrame; i++)
            _s.CreateEntity(EffectPos(Frame, i), NewEffect(i), new Vel { X = 0.5f, Y = 0.25f }, new Depth());

        long sum = 0;
        _scratch.Clear();
        foreach (var (pos, eff, _, ents) in _effectQ.Chunks)
        {
            var ps = pos.Span; var ef = eff.Span;
            for (int i = 0; i < ef.Length; i++)
            {
                ps[i].X = (ushort)((ps[i].X + 1) % WorldSpec.MapW);
                if (--ef[i].Life == 0) _scratch.Add(ents.EntityAt(i));
                sum += ef[i].Life;
            }
        }
        foreach (var e in _scratch) e.DeleteEntity();
        return sum;
    }

    private static void Toggle(Entity e)
    {
        if (e.Tags.Has<InRange>()) e.RemoveTag<InRange>(); else e.AddTag<InRange>();
    }

    public override long BlockStream()
    {
        Frame++;
        var fresh = _blocks[Frame % 3];
        var old = _blocks[(Frame + 1) % 3];
        foreach (var e in old) e.DeleteEntity();
        old.Clear();

        var land = Tags.Get<Land>(); var stat = Tags.Get<Static>();
        long sum = 0;
        for (int b = 0; b < WorldSpec.BlocksPerOp; b++)
        {
            int seq = Frame * WorldSpec.BlocksPerOp + b;
            for (int i = 0; i < WorldSpec.LandPerBlock; i++)
            {
                var p = BlockLandPos(seq, i);
                fresh.Add(_s.CreateEntity(p, new Graphic { Value = (ushort)i }, new Depth(), land));
                sum += p.X;
            }
            for (int i = 0; i < WorldSpec.StaticsPerBlock; i++)
            {
                var p = BlockLandPos(seq, i & 63); p.Z = (sbyte)i;
                fresh.Add(_s.CreateEntity(p, new Graphic { Value = (ushort)(i + 100) }, new Hue(), new Depth(), stat));
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
            e.GetComponent<Hue>().Value = (ushort)k;
            if (!e.Tags.Has<Dirty>()) e.AddTag<Dirty>();
        }
        long sum = 0;
        _scratch.Clear();
        foreach (var (hue, _, ents) in _dirtyQ.Chunks)
        {
            var hs = hue.Span;
            for (int i = 0; i < hs.Length; i++) { sum += hs[i].Value + 1; _scratch.Add(ents.EntityAt(i)); }
        }
        foreach (var e in _scratch) e.RemoveTag<Dirty>();
        return sum;
    }

    public override long Snapshot()
    {
        long sum = 0;
        int n = 0;
        foreach (var (pos, anim, motion, _) in _mobileTick.Chunks)
        {
            pos.Span.CopyTo(SnapPos.AsSpan(n));
            motion.Span.CopyTo(SnapMotion.AsSpan(n));
            anim.Span.CopyTo(SnapAnim.AsSpan(n));
            n += pos.Length;
        }
        n = 0;
        foreach (var (pos, anim, motion, _) in _mobileTick.Chunks)
        {
            var ps = pos.Span;
            SnapPos.AsSpan(n, ps.Length).CopyTo(ps);
            SnapMotion.AsSpan(n, ps.Length).CopyTo(motion.Span);
            SnapAnim.AsSpan(n, ps.Length).CopyTo(anim.Span);
            for (int i = 0; i < ps.Length; i++) sum += ps[i].X;
            n += ps.Length;
        }
        return sum;
    }

    public override void Dispose() => _runner?.Dispose();
}
