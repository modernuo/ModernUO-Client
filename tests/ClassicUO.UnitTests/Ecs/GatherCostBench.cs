using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T7b: per-item cost of ItemSpriteInput's record read (the ECS components through the cached handle) against the field
    // reads the Gpu item extract made before T7b part 1 (base 2ec71344f). A Stopwatch loop, not a test: it does nothing unless
    // CUO_GATHER_BENCH=<output file> is set. Run with the JIT settled:
    //   DOTNET_TieredCompilation=0 CUO_GATHER_BENCH=<file> dotnet test tests/ClassicUO.UnitTests -c Release --filter GatherCostBench
    // A and B alternate in many short rounds (the order flips every round) so machine noise lands on both equally. Candidates:
    // T7b part 1's read (d3f6f52c6: one GetComponent per field), ReadRecord before the EcsRead helper (EntityData) and
    // the current ReadRecord (EcsRead.Row). CUO_GATHER_BENCH_PROBES=1 adds
    // breakdown probes; "probe direct" reads the component arrays past Friflo's accessors (EntityData layout mirror, probe
    // only) to size what a direct-access helper would save.
    [Trait("Category", "Manual")]
    public class GatherCostBench
    {
        private const uint ITEM0 = 0x4000_0000;

        private static (World world, Item[] items, StaticTiles[] tiles) Build(int n, int seed)
        {
            var world = new World();
            var tiles = new StaticTiles[0x10000];
            var rnd = new Random(seed);

            for (int g = 0; g < tiles.Length; g++)
            {
                ulong flags = (ulong)(rnd.Next(8) == 0 ? TileFlag.PartialHue : 0) | (ulong)(rnd.Next(6) == 0 ? TileFlag.Generic : 0)
                    | (ulong)(rnd.Next(40) == 0 ? TileFlag.LightSource : 0) | (ulong)(rnd.Next(30) == 0 ? TileFlag.Container : 0);
                tiles[g] = new StaticTiles(flags, (byte)rnd.Next(256), 0, 0, 0, 0, 0, 1, null);
            }

            var items = new Item[n];

            for (int i = 0; i < n; i++)
            {
                Item it = world.GetOrCreateItem(ITEM0 + (uint)i);
                int kind = rnd.Next(100);
                it.TestSetGraphic(kind < 5 ? (ushort)0x0EED : (ushort)(0x0100 + rnd.Next(0x3F00)));
                it.TestSetHue(kind % 3 == 0 ? (ushort)rnd.Next(1, 0x0BB8) : (ushort)0);
                it.TestSetAmount(kind < 20 ? (ushort)rnd.Next(2, 500) : (ushort)1);
                it.X = (ushort)(1400 + rnd.Next(64));
                it.Y = (ushort)(1600 + rnd.Next(64));
                it.Z = (sbyte)rnd.Next(-5, 40);
                it.TestSetFlags(rnd.Next(4) == 0 ? Flags.Movable : Flags.None);
                it.IsMulti = kind >= 97;
                world.Ecs.Sync(it);
                items[i] = it;
            }

            return (world, items, tiles);
        }

        // the pre-T7b reads (2ec71344f ItemSpriteExtract.Emit: item.Hue, item.Graphic, item.IsMulti, item.Amount, item.X,
        // item.ItemData row, …), packed into the same input struct so both sides do the same stores
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OldFields(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            i.Serial = item.Serial;
            i.Graphic = item.Graphic;
            i.Hue = item.Hue;
            i.X = item.X;
            y = item.Y;
            i.Hidden = item.IsHidden;
            i.Movable = (item.Flags & Flags.Movable) != 0;
            i.Amount = item.Amount;
            i.Layer = (byte)item.Layer;
            i.OnGround = item.OnGround;
            i.Multi = item.IsMulti;
            i.MultiGraphic = item.MultiGraphic;
            ref StaticTiles tile = ref staticData[i.Multi ? i.MultiGraphic : i.Graphic];
            i.TileLight = tile.IsLight;
            i.TilePartialHue = tile.IsPartialHue;
            i.TileAnimated = tile.IsAnimated;
            i.TileContainer = tile.IsContainer;
            i.TileStackable = tile.IsStackable;
            i.TileHeavy = tile.Weight > 90;
        }

        // d3f6f52c6 (T7b part 1): one GetComponent per field, ItemData always
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void T7b1Record(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            if (item.TryScalarEntity(out Friflo.Engine.ECS.Entity e))
            {
                i.Serial = e.GetComponent<ClassicUO.Ecs.Serial>().Value;
                i.Graphic = e.GetComponent<ClassicUO.Ecs.Graphic>().Value;
                i.Hue = e.GetComponent<ClassicUO.Ecs.Hue>().Value;
                ref readonly ClassicUO.Ecs.Position p = ref e.GetComponent<ClassicUO.Ecs.Position>();
                i.X = p.X;
                y = p.Y;
                var flags = (Flags)e.GetComponent<ClassicUO.Ecs.Flags>().Value;
                i.Hidden = (flags & Flags.Hidden) != 0;
                i.Movable = (flags & Flags.Movable) != 0;
                ref readonly ClassicUO.Ecs.ItemData d = ref e.GetComponent<ClassicUO.Ecs.ItemData>();
                i.Amount = d.Amount;
                i.Layer = d.Layer;
                Friflo.Engine.ECS.Tags tags = e.Tags;
                i.OnGround = tags.Has<ClassicUO.Ecs.OnGround>();
                i.Multi = tags.Has<ClassicUO.Ecs.MultiTag>();
            }
            else
            {
                y = 0;
            }

            i.MultiGraphic = item.MultiGraphic;
            ref StaticTiles tile = ref staticData[i.Multi ? i.MultiGraphic : i.Graphic];
            i.TileLight = tile.IsLight;
            i.TilePartialHue = tile.IsPartialHue;
            i.TileAnimated = tile.IsAnimated;
            i.TileContainer = tile.IsContainer;
            i.TileStackable = tile.IsStackable;
            i.TileHeavy = tile.Weight > 90;
        }

        // ReadRecord before the EcsRead helper (9028cd040: the record through Entity.Data / EntityData.Get)
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DataRecord(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            bool record = item.TryScalarEntity(out Friflo.Engine.ECS.Entity e);
            Friflo.Engine.ECS.EntityData data = default;

            if (record)
            {
                data = e.Data;
                i.Serial = item.Serial;
                i.Graphic = data.Get<ClassicUO.Ecs.Graphic>().Value;
                i.Hue = data.Get<ClassicUO.Ecs.Hue>().Value;
                ref readonly ClassicUO.Ecs.Position p = ref data.Get<ClassicUO.Ecs.Position>();
                i.X = p.X;
                y = p.Y;
                var flags = (Flags)data.Get<ClassicUO.Ecs.Flags>().Value;
                i.Hidden = (flags & Flags.Hidden) != 0;
                i.Movable = (flags & Flags.Movable) != 0;
                Friflo.Engine.ECS.Tags tags = data.Tags;
                i.OnGround = tags.Has<ClassicUO.Ecs.OnGround>();
                i.Multi = tags.Has<ClassicUO.Ecs.MultiTag>();
            }
            else
            {
                i.Serial = item.Serial;
                i.Graphic = item.Graphic;
                i.Hue = item.Hue;
                i.X = item.X;
                y = item.Y;
                i.Hidden = item.IsHidden;
                i.Movable = (item.Flags & Flags.Movable) != 0;
                i.Amount = item.Amount;
                i.Layer = (byte)item.Layer;
                i.OnGround = item.OnGround;
                i.Multi = item.IsMulti;
            }

            i.MultiGraphic = item.MultiGraphic;
            ref StaticTiles tile = ref staticData[i.Multi ? i.MultiGraphic : i.Graphic];
            i.TileLight = tile.IsLight;
            i.TilePartialHue = tile.IsPartialHue;
            i.TileAnimated = tile.IsAnimated;
            i.TileContainer = tile.IsContainer;
            i.TileStackable = tile.IsStackable;
            i.TileHeavy = tile.Weight > 90;

            if (record && i.UsesItemData)
            {
                ref readonly ClassicUO.Ecs.ItemData d = ref data.Get<ClassicUO.Ecs.ItemData>();
                i.Amount = d.Amount;
                i.Layer = d.Layer;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NewRecord(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y) =>
            ItemSpriteInput.ReadRecord(item, staticData, ref i, out y);

        // breakdown probes (not equal to the others: only for where the time goes)
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeHandle(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            OldFields(item, staticData, ref i, out y);
            item.TryScalarEntity(out Friflo.Engine.ECS.Entity e);
            i.Serial ^= (uint)e.Id;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeOneComponent(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            OldFields(item, staticData, ref i, out y);
            item.TryScalarEntity(out Friflo.Engine.ECS.Entity e);
            i.Serial ^= e.GetComponent<ClassicUO.Ecs.Serial>().Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeTags(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            OldFields(item, staticData, ref i, out y);
            item.TryScalarEntity(out Friflo.Engine.ECS.Entity e);
            i.OnGround ^= e.Tags.Has<ClassicUO.Ecs.OnGround>();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeData(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            OldFields(item, staticData, ref i, out y);
            item.TryScalarEntity(out Friflo.Engine.ECS.Entity e);
            Friflo.Engine.ECS.EntityData d = e.Data;
            i.Serial ^= d.Get<ClassicUO.Ecs.Serial>().Value ^ d.Get<ClassicUO.Ecs.Graphic>().Value ^ d.Get<ClassicUO.Ecs.Hue>().Value
                ^ d.Get<ClassicUO.Ecs.Position>().X ^ d.Get<ClassicUO.Ecs.Flags>().Value ^ d.Get<ClassicUO.Ecs.ItemData>().Amount;
            i.OnGround ^= d.Tags.Has<ClassicUO.Ecs.OnGround>() ^ d.Tags.Has<ClassicUO.Ecs.MultiTag>();
        }

        // EntityData's field layout (heapMap, archetype, Id, compIndex): probe only, checked against GetComponent in Run
        private struct DataMirror
        {
            public object[] HeapMap;
            public object Archetype;
            public int Id, CompIndex;
        }

        private static class Idx<T> where T : struct, Friflo.Engine.ECS.IComponent
        {
            public static readonly int Value = Friflo.Engine.ECS.EntityStore.GetEntitySchema().GetComponentType<T>().StructIndex;

            [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "components")]
            public static extern ref T[] Components([System.Runtime.CompilerServices.UnsafeAccessorType("Friflo.Engine.ECS.StructHeapGen`1[[!0]], Friflo.Engine.ECS")] object heap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref T Comp<T>(object[] heaps, int ci) where T : struct, Friflo.Engine.ECS.IComponent =>
            ref Idx<T>.Components(heaps[Idx<T>.Value])[ci];

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeDirect(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y)
        {
            OldFields(item, staticData, ref i, out y);
            item.TryScalarEntity(out Friflo.Engine.ECS.Entity e);
            Friflo.Engine.ECS.EntityData d = e.Data;
            DataMirror m = Unsafe.As<Friflo.Engine.ECS.EntityData, DataMirror>(ref d);
            object[] h = m.HeapMap;
            int ci = m.CompIndex;
            Friflo.Engine.ECS.Tags tags = d.Tags;
            i.Serial ^= Comp<ClassicUO.Ecs.Serial>(h, ci).Value ^ Comp<ClassicUO.Ecs.Graphic>(h, ci).Value ^ Comp<ClassicUO.Ecs.Hue>(h, ci).Value
                ^ Comp<ClassicUO.Ecs.Position>(h, ci).X ^ Comp<ClassicUO.Ecs.Flags>(h, ci).Value ^ Comp<ClassicUO.Ecs.ItemData>(h, ci).Amount;
            i.OnGround ^= tags.Has<ClassicUO.Ecs.OnGround>() ^ tags.Has<ClassicUO.Ecs.MultiTag>();
        }

        // Amount and Layer only where the select reads them (ReadRecord leaves them 0 elsewhere)
        private static ulong Check(in ItemSpriteInput i, ushort y)
        {
            ItemSpriteInput c = i;

            if (!c.UsesItemData)
            {
                c.Amount = 0;
                c.Layer = 0;
            }

            return Sum(c, y) ^ ((ulong)c.MultiGraphic << 52) ^ (c.TileAnimated ? 1UL << 62 : 0) ^ (c.TileContainer ? 1UL << 63 : 0);
        }

        private static ulong Sum(in ItemSpriteInput i, ushort y) =>
            i.Serial ^ ((ulong)i.Graphic << 8) ^ ((ulong)i.Hue << 16) ^ ((ulong)i.X << 24) ^ ((ulong)y << 32) ^ ((ulong)i.Amount << 40)
            ^ (i.Hidden ? 1UL : 0) ^ (i.Movable ? 2UL : 0) ^ (i.OnGround ? 4UL : 0) ^ (i.Multi ? 8UL : 0) ^ (i.TileLight ? 16UL : 0)
            ^ (i.TilePartialHue ? 32UL : 0) ^ (i.TileStackable ? 64UL : 0) ^ (i.TileHeavy ? 128UL : 0) ^ ((ulong)i.Layer << 48);

        private delegate void Reader(Item item, StaticTiles[] staticData, ref ItemSpriteInput i, out ushort y);

        private static double Pass(Reader read, Item[] order, StaticTiles[] tiles, ref ulong sink)
        {
            long t0 = Stopwatch.GetTimestamp();
            ulong s = 0;

            for (int k = 0; k < order.Length; k++)
            {
                ItemSpriteInput i = default;
                read(order[k], tiles, ref i, out ushort y);
                s += Sum(i, y);
            }

            long t1 = Stopwatch.GetTimestamp();
            sink += s;

            return (t1 - t0) * 1e9 / Stopwatch.Frequency / order.Length;
        }

        private static double Pct(List<double> v, double p)
        {
            var a = v.ToArray();
            Array.Sort(a);

            return a[Math.Clamp((int)Math.Round(p * (a.Length - 1)), 0, a.Length - 1)];
        }

        private static void Run(StringBuilder sb, string name, Reader cand, bool check, int n, bool shuffled, int rounds, int passes)
        {
            (World world, Item[] items, StaticTiles[] tiles) = Build(n, 7);
            Item[] order = (Item[])items.Clone();

            if (shuffled)
            {
                new Random(11).Shuffle(order);
            }

            // both readers see the same values
            for (int k = 0; check && k < order.Length; k++)
            {
                ItemSpriteInput a = default, b = default;
                OldFields(order[k], tiles, ref a, out ushort ya);
                cand(order[k], tiles, ref b, out ushort yb);
                Assert.Equal(Check(a, ya), Check(b, yb));
            }

            Reader oldR = OldFields, newR = cand;
            ulong sink = 0;

            for (int w = 0; w < 20; w++)
            {
                Pass(oldR, order, tiles, ref sink);
                Pass(newR, order, tiles, ref sink);
            }

            var olds = new List<double>(rounds);
            var news = new List<double>(rounds);
            var diffs = new List<double>(rounds);

            for (int r = 0; r < rounds; r++)
            {
                double o = 0, nw = 0;

                for (int p = 0; p < passes; p++)
                {
                    if (((r + p) & 1) == 0)
                    {
                        o += Pass(oldR, order, tiles, ref sink);
                        nw += Pass(newR, order, tiles, ref sink);
                    }
                    else
                    {
                        nw += Pass(newR, order, tiles, ref sink);
                        o += Pass(oldR, order, tiles, ref sink);
                    }
                }

                olds.Add(o / passes);
                news.Add(nw / passes);
                diffs.Add((nw - o) / passes);
            }

            sb.AppendLine(
                $"| {name} | {n} | {(shuffled ? "shuffled" : "sequential")} | {Pct(olds, 0.5):F2} ({Pct(olds, 0.1):F2}–{Pct(olds, 0.9):F2}) | " +
                $"{Pct(news, 0.5):F2} ({Pct(news, 0.1):F2}–{Pct(news, 0.9):F2}) | {Pct(diffs, 0.5):F2} ({Pct(diffs, 0.1):F2}–{Pct(diffs, 0.9):F2}) | {sink & 1} |");
            GC.KeepAlive(world);
        }

        [Fact]
        public void Measure()
        {
            string output = Environment.GetEnvironmentVariable("CUO_GATHER_BENCH");

            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"GatherCostBench, TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}, {DateTime.Now:s}");
            sb.AppendLine("ns/item, median (p10–p90) over rounds; diff = new - old per round");
            sb.AppendLine("| candidate | items | order | old fields | candidate | diff | - |");
            sb.AppendLine("|---|---|---|---|---|---|---|");

            foreach (int n in new[] { 70, 2000, 25000 })
            {
                int passes = Math.Max(1, 200_000 / n);
                Run(sb, "T7b1 (d3f6f52c6)", T7b1Record, true, n, false, 201, passes);
                Run(sb, "ReadRecord, EntityData (9028cd040)", DataRecord, true, n, false, 201, passes);
                Run(sb, "ReadRecord, EcsRead.Row", NewRecord, true, n, false, 201, passes);
                Run(sb, "T7b1 (d3f6f52c6)", T7b1Record, true, n, true, 201, passes);
                Run(sb, "ReadRecord, EntityData (9028cd040)", DataRecord, true, n, true, 201, passes);
                Run(sb, "ReadRecord, EcsRead.Row", NewRecord, true, n, true, 201, passes);
            }

            if (Environment.GetEnvironmentVariable("CUO_GATHER_BENCH_PROBES") == "1")
            {
                (World pw, Item[] pitems, StaticTiles[] _) = Build(500, 3);

                foreach (Item it in pitems)
                {
                    it.TryScalarEntity(out Friflo.Engine.ECS.Entity e);
                    Friflo.Engine.ECS.EntityData d = e.Data;
                    DataMirror m = Unsafe.As<Friflo.Engine.ECS.EntityData, DataMirror>(ref d);
                    Assert.Equal(e.GetComponent<ClassicUO.Ecs.Hue>().Value, Comp<ClassicUO.Ecs.Hue>(m.HeapMap, m.CompIndex).Value);
                    Assert.Equal(e.GetComponent<ClassicUO.Ecs.Position>().Y, Comp<ClassicUO.Ecs.Position>(m.HeapMap, m.CompIndex).Y);
                }

                (string, Reader)[] probes = { ("probe handle", ProbeHandle), ("probe 1 comp", ProbeOneComponent), ("probe tags", ProbeTags), ("probe Data", ProbeData), ("probe direct", ProbeDirect) };

                foreach ((string name, Reader r) in probes)
                {
                    Run(sb, name, r, false, 2000, false, 101, 100);
                }
            }

            File.WriteAllText(output, sb.ToString());
        }
    }
}
