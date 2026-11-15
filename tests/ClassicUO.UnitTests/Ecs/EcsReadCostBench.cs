using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using ClassicUO.Ecs;
using Friflo.Engine.ECS;
using Xunit;
using Position = ClassicUO.Ecs.Position;

namespace ClassicUO.UnitTests.Ecs
{
    // Cost per entity of Friflo's component reads (4.0.0-preview.2) against EcsRead. A Stopwatch loop, not a test: it does
    // nothing unless CUO_ECS_READ_BENCH=<output file> is set. Run with the JIT settled:
    //   DOTNET_TieredCompilation=0 CUO_ECS_READ_BENCH=<file> dotnet test tests/ClassicUO.UnitTests -c Release --filter EcsReadCostBench
    // Every round runs every reader once over the same shuffled entities (mixed archetypes, like the mirror's items), in an
    // order rotated per round so machine noise lands on all of them; the table has the median and p10–p90 over the rounds,
    // minus nothing (the "handle only" row is the loop and the entity load).
    [Trait("Category", "Manual")]
    public class EcsReadCostBench
    {
        private delegate ulong Reader(Entity[] es);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong HandleOnly(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                s += (ulong)es[i].Id;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong GetComponent1(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                s += es[i].GetComponent<Hue>().Value;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong TryGetComponent1(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                s += es[i].TryGetComponent(out Hue h) ? h.Value : 1u;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong DataGet1(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                s += es[i].Data.Get<Hue>().Value;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong HelperGet1(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                s += EcsRead.Get<Hue>(es[i]).Value;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong HelperTryGet1(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                s += EcsRead.TryGet(es[i], out Hue h) ? h.Value : 1u;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong GetComponent5(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                Entity e = es[i];
                s += e.GetComponent<Serial>().Value ^ e.GetComponent<Graphic>().Value ^ e.GetComponent<Hue>().Value ^
                     e.GetComponent<Position>().X ^ e.GetComponent<ClassicUO.Ecs.Flags>().Value;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong DataGet5(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                EntityData d = es[i].Data;
                s += d.Get<Serial>().Value ^ d.Get<Graphic>().Value ^ d.Get<Hue>().Value ^ d.Get<Position>().X ^
                     d.Get<ClassicUO.Ecs.Flags>().Value;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong HelperGet5(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                Entity e = es[i];
                s += EcsRead.Get<Serial>(e).Value ^ EcsRead.Get<Graphic>(e).Value ^ EcsRead.Get<Hue>(e).Value ^
                     EcsRead.Get<Position>(e).X ^ EcsRead.Get<ClassicUO.Ecs.Flags>(e).Value;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong HelperRow5(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                EcsRow r = EcsRead.Row(es[i]);
                s += r.Get<Serial>().Value ^ r.Get<Graphic>().Value ^ r.Get<Hue>().Value ^ r.Get<Position>().X ^
                     r.Get<ClassicUO.Ecs.Flags>().Value;
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong DataTags(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                EntityData d = es[i].Data;
                Tags t = d.Tags;
                s += d.Get<Hue>().Value ^ (t.Has<OnGround>() ? 1u : 0u) ^ (t.Has<MultiTag>() ? 2u : 0u);
            }

            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong HelperTags(Entity[] es)
        {
            ulong s = 0;

            for (int i = 0; i < es.Length; i++)
            {
                EcsRow r = EcsRead.Row(es[i]);
                ref readonly Tags t = ref r.Tags;
                s += r.Get<Hue>().Value ^ (t.Has<OnGround>() ? 1u : 0u) ^ (t.Has<MultiTag>() ? 2u : 0u);
            }

            return s;
        }

        private static Entity[] Build(EntityStore store, int n, int seed)
        {
            var rnd = new Random(seed);
            var es = new Entity[n];

            for (int i = 0; i < n; i++)
            {
                Entity e = store.CreateEntity(new Serial { Value = 0x4000_0000u + (uint)i }, new Graphic { Value = (ushort)rnd.Next(0x4000) },
                    new Hue { Value = (ushort)rnd.Next(3000) }, new Position { X = (ushort)rnd.Next(4096), Y = (ushort)rnd.Next(4096) },
                    new ClassicUO.Ecs.Flags { Value = (byte)rnd.Next(256) });

                // several archetypes, as the mirror's items (tags and optional components)
                switch (rnd.Next(4))
                {
                    case 0: e.AddTag<OnGround>(); break;
                    case 1: e.AddTag<OnGround>(); e.AddComponent(new ItemData { Amount = 2 }); break;
                    case 2: e.AddComponent(new ItemData { Amount = 1 }); break;
                }

                if (rnd.Next(30) == 0)
                {
                    e.AddTag<MultiTag>();
                }

                es[i] = e;
            }

            rnd.Shuffle(es);

            return es;
        }

        private static double Pass(Reader r, Entity[] es, ref ulong sink)
        {
            long t0 = Stopwatch.GetTimestamp();
            ulong s = r(es);
            long t1 = Stopwatch.GetTimestamp();
            sink += s;

            return (t1 - t0) * 1e9 / Stopwatch.Frequency / es.Length;
        }

        private static double Pct(List<double> v, double p)
        {
            double[] a = v.ToArray();
            Array.Sort(a);

            return a[Math.Clamp((int)Math.Round(p * (a.Length - 1)), 0, a.Length - 1)];
        }

        [Fact]
        public void Measure()
        {
            string output = Environment.GetEnvironmentVariable("CUO_ECS_READ_BENCH");

            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            (string, Reader)[] readers =
            {
                ("handle only (loop)", HandleOnly), ("GetComponent x1", GetComponent1), ("TryGetComponent x1", TryGetComponent1),
                ("Data.Get x1", DataGet1), ("EcsRead.Get x1", HelperGet1), ("EcsRead.TryGet x1", HelperTryGet1),
                ("GetComponent x5", GetComponent5), ("Data.Get x5", DataGet5), ("EcsRead.Get x5", HelperGet5),
                ("EcsRead.Row x5", HelperRow5), ("Data + 2 tags", DataTags), ("EcsRead.Row + 2 tags", HelperTags),
            };

            var sb = new StringBuilder();
            sb.AppendLine($"EcsReadCostBench, Friflo 4.0.0-preview.2, TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}, {DateTime.Now:s}");
            sb.AppendLine("ns per entity, median (p10–p90) over rounds; entities shuffled over 6 archetypes");
            sb.AppendLine("| reader | entities | ns/entity |");
            sb.AppendLine("|---|---|---|");

            foreach (int n in new[] { 2000, 25000 })
            {
                var store = new EntityStore();
                Entity[] es = Build(store, n, 7);
                ulong sink = 0;

                // the helper's per-type decision (a one-time probe) made before the timing
                EcsRead.Get<Serial>(es[0]);
                EcsRead.Get<Graphic>(es[0]);
                EcsRead.Get<Hue>(es[0]);
                EcsRead.Get<Position>(es[0]);
                EcsRead.Get<ClassicUO.Ecs.Flags>(es[0]);

                // all readers agree
                ulong expect1 = GetComponent1(es), expect5 = GetComponent5(es);
                Assert.Equal(expect1, DataGet1(es));
                Assert.Equal(expect1, HelperGet1(es));
                Assert.Equal(TryGetComponent1(es), HelperTryGet1(es));
                Assert.Equal(expect5, DataGet5(es));
                Assert.Equal(expect5, HelperGet5(es));
                Assert.Equal(expect5, HelperRow5(es));
                Assert.Equal(DataTags(es), HelperTags(es));

                int passes = Math.Max(1, 100_000 / n);
                int rounds = 201;
                var times = new List<double>[readers.Length];

                for (int k = 0; k < readers.Length; k++)
                {
                    times[k] = new List<double>(rounds);

                    for (int w = 0; w < 10; w++)
                    {
                        Pass(readers[k].Item2, es, ref sink);
                    }
                }

                for (int r = 0; r < rounds; r++)
                {
                    for (int j = 0; j < readers.Length; j++)
                    {
                        int k = (j + r) % readers.Length;
                        double t = 0;

                        for (int p = 0; p < passes; p++)
                        {
                            t += Pass(readers[k].Item2, es, ref sink);
                        }

                        times[k].Add(t / passes);
                    }
                }

                for (int k = 0; k < readers.Length; k++)
                {
                    sb.AppendLine($"| {readers[k].Item1} | {n} | {Pct(times[k], 0.5):F2} ({Pct(times[k], 0.1):F2}–{Pct(times[k], 0.9):F2}) |");
                }

                sb.AppendLine($"sink {sink & 1}");
                GC.KeepAlive(store);
            }

            File.WriteAllText(output, sb.ToString());
        }
    }
}
