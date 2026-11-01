using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Ecs
{
    // T3b: the layering rules as data (Uo.Pack.LayerRules, LayerRulesDefault) give exactly the hard-coded answers
    // (PaperdollOrder.Build + Filter + ApplyDirectionCloak, AnimationRules.IsCovered = MobileView.IsCovered's ECS copy,
    // FixGargoyleEquipment) for every body group, every combination of the values the rules reference (each range's
    // endpoints and their neighbours, singly, in pairs, in the rule groups' joint layers, and in random full outfits) and
    // every direction. Every single-entry mutation of the default file is caught.
    [Collection(nameof(LayerRulesStateCollection))]
    public class LayerRulesTests
    {
        private const int N = PaperdollOrder.N;
        private readonly ITestOutputHelper _out;

        public LayerRulesTests(ITestOutputHelper output) => _out = output;

        // one body per group, plus the gargoyle ghosts, a GM body and neighbours of the gargoyle ids
        private static readonly ushort[] Bodies = { 0x190, 0x191, 0x192, 0x193, 0x25D, 0x25E, 0x25F, 0x260, 0x29A, 0x29B, 0x2B6, 0x2B7, 0x3DB, 0x0C, 0x299, 0x29C, 0x2B5, 0x2B8, 0xB7 };
        private static readonly ushort[] GroupBodies = { 0x190, 0x191, 0x25D, 0x25E, 0x29A, 0x29B, 0x0C };
        private const ushort Ordinary = 0x1234;

        private struct Outfit
        {
            public bool[] Present;
            public ushort[] Gfx, Anim;

            public static Outfit Empty() => new Outfit { Present = new bool[N], Gfx = new ushort[N], Anim = new ushort[N] };

            public Outfit Clone() => new Outfit { Present = (bool[])Present.Clone(), Gfx = (ushort[])Gfx.Clone(), Anim = (ushort[])Anim.Clone() };

            public void Set(int layer, (ushort g, ushort a)? item)
            {
                Present[layer] = item.HasValue;
                Gfx[layer] = item?.g ?? 0;
                Anim[layer] = item?.a ?? 0;
            }

            public ushort[] Key()
            {
                var k = new ushort[N];
                for (int l = 1; l < N; l++) k[l] = Present[l] ? Anim[l] : (ushort)0;
                return k;
            }
        }

        // ---- the candidate values, generated from the rules: every range's lo-1, lo, hi, hi+1 per (layer, field) -------

        private static Dictionary<(int layer, LayerCondKind kind), SortedSet<ushort>> Values(LayerRules rules)
        {
            var v = new Dictionary<(int, LayerCondKind), SortedSet<ushort>>();

            for (int l = 1; l < N; l++)
            {
                v[(l, LayerCondKind.Graphic)] = new SortedSet<ushort> { 0, Ordinary };
                v[(l, LayerCondKind.AnimId)] = new SortedSet<ushort> { 0, Ordinary };
            }

            foreach (LayerCond c in rules.Conds)
            {
                if (c.Kind != (byte)LayerCondKind.Graphic && c.Kind != (byte)LayerCondKind.AnimId || c.Layer >= N) continue;
                var set = v[(c.Layer, (LayerCondKind)c.Kind)];

                for (int i = c.RangeStart; i < c.RangeStart + c.RangeCount; i++)
                {
                    LayerRange r = rules.Ranges[i];
                    foreach (int x in new[] { r.Lo - 1, r.Lo, r.Hi, r.Hi + 1 })
                        if (x >= 0 && x <= 0xFFFF) set.Add((ushort)x);
                }
            }

            return v;
        }

        // one layer's items: absent, every graphic with an ordinary AnimID, every AnimID with an ordinary graphic
        private static List<(ushort, ushort)?> Items(Dictionary<(int, LayerCondKind), SortedSet<ushort>> v, int layer)
        {
            var items = new List<(ushort, ushort)?> { null };
            foreach (ushort g in v[(layer, LayerCondKind.Graphic)]) items.Add((g, Ordinary));
            foreach (ushort a in v[(layer, LayerCondKind.AnimId)]) items.Add((Ordinary, a));
            items.Add((0, 0));
            return items.Distinct().ToList();
        }

        // ---- the comparison --------------------------------------------------------------------------------------------

        private sealed class Compare
        {
            public readonly LayerRules Rules;
            public long Checks, Mismatches;
            public string First;

            public Compare(LayerRules rules) => Rules = rules;

            private void Fail(string what)
            {
                Mismatches++;
                First ??= what;
            }

            public void Covered(in Outfit o, ushort[] bodies)
            {
                foreach (ushort body in bodies)
                {
                    var subject = new LayerSubject(Rules.GroupOf(body), 0);

                    for (int l = 1; l < N; l++)
                    {
                        bool hard = AnimationRules.IsCovered((Layer)l, o.Gfx, o.Anim, o.Present, body);
                        bool data1 = Rules.IsCovered(LayerRuleSet.World, (byte)l, o.Present, o.Gfx, o.Anim, subject);
                        bool data2 = Rules.IsCovered(LayerRuleSet.Paperdoll, (byte)l, o.Present, o.Gfx, o.Anim, subject);
                        Checks += 2;

                        if (hard != data1 || hard != data2)
                        {
                            Fail($"covered layer 0x{l:X2} body 0x{body:X3}: hard {hard} world {data1} paperdoll {data2} | {Describe(o)}");
                            return;
                        }
                    }
                }
            }

            public void Order(ushort[] key, ushort body, bool dirs)
            {
                Span<Layer> raw = stackalloc Layer[N];
                Span<Layer> hard = stackalloc Layer[N];
                Span<Layer> data = stackalloc Layer[N];
                Span<byte> rawData = stackalloc byte[N];

                for (int f = 0; f < 4; f++)
                {
                    bool female = (f & 1) != 0, garg = (f & 2) != 0;
                    var subject = new LayerSubject(Rules.GroupOf(body), (byte)((female ? 1 : 0) | (garg ? 2 : 0)));
                    PaperdollOrder.Build(key, female || garg, raw);

                    foreach (LayerRuleSet set in new[] { LayerRuleSet.Paperdoll, LayerRuleSet.World })
                    {
                        Rules.BuildOrder(set, key, subject, rawData);
                        Checks++;

                        if (!MemoryMarshal.Cast<byte, Layer>(rawData).SequenceEqual(raw))
                        {
                            Fail($"{set} raw order f{f}: hard [{string.Join(",", raw.ToArray().Select(x => ((byte)x).ToString("X2")))}] data [{string.Join(",", rawData.ToArray().Select(x => x.ToString("X2")))}] key {DescribeKey(key)}");
                            return;
                        }
                    }

                    if (!dirs) continue;

                    for (int dir = 0; dir <= 8; dir++)
                    {
                        byte d = dir == 8 ? (byte)0x80 : (byte)dir;
                        int hc = PaperdollOrder.Filter(raw, false, hard);
                        hc = PaperdollOrder.ApplyDirectionCloak(hard, hc, d);
                        int dc = World(Rules, key, subject, d, data);
                        Checks++;

                        if (!data.Slice(0, dc).SequenceEqual(hard.Slice(0, hc)))
                        {
                            Fail($"world order dir {d} f{f}: key {DescribeKey(key)}");
                            return;
                        }
                    }
                }
            }

            // world: FixGargoyleEquipment for the gargoyle draw flag; paperdoll: GetAnimID's gargoyle-ghost shroud (7.0.0.0+)
            public void Remaps()
            {
                foreach (ushort body in new ushort[] { 0x190, 0x191, 0x29A, 0x29B, 0x2B5, 0x2B6, 0x2B7, 0x2B8 })
                for (int a = 0; a <= 0xFFFF; a++)
                {
                    for (byte flags = 0; flags < 8; flags++)
                    {
                        var subject = new LayerSubject(Rules.GroupOf(body), flags, body);
                        ushort hard = (flags & 2) != 0 ? AnimationRules.FixGargoyleEquipment((ushort)a) : (ushort)a;
                        ushort hardPd = LayerRuleEval.RemapPaperdollHard((ushort)a, body, (flags & 4) != 0);
                        Checks += 2;

                        if (Rules.Remap(LayerRuleSet.World, (ushort)a, subject) != hard || Rules.Remap(LayerRuleSet.Paperdoll, (ushort)a, subject) != hardPd)
                        {
                            Fail($"remap 0x{a:X4} body 0x{body:X3} flags {flags}");
                            return;
                        }
                    }
                }
            }
        }

        private static int World(LayerRules rules, ReadOnlySpan<ushort> key, LayerSubject subject, byte dir, Span<Layer> dest)
        {
            Span<byte> raw = stackalloc byte[N];
            rules.BuildOrder(LayerRuleSet.World, key, subject, raw);
            int count = PaperdollOrder.Filter(MemoryMarshal.Cast<byte, Layer>(raw), false, dest);
            return rules.ApplyDirection(LayerRuleSet.World, MemoryMarshal.Cast<Layer, byte>(dest), count, dir, key, subject);
        }

        private static string Describe(in Outfit o)
        {
            var parts = new List<string>();
            for (int l = 1; l < N; l++)
                if (o.Present[l]) parts.Add($"{(Layer)l}=0x{o.Gfx[l]:X4}/0x{o.Anim[l]:X4}");
            return string.Join(" ", parts);
        }

        private static string DescribeKey(ushort[] key) =>
            string.Join(" ", Enumerable.Range(1, N - 1).Where(l => key[l] != 0).Select(l => $"{(Layer)l}=0x{key[l]:X4}"));

        // ---- the corpus ------------------------------------------------------------------------------------------------

        // layers the rules read or move
        private static int[] RuleLayers(LayerRules rules) =>
            rules.Conds.Where(c => c.Kind <= (byte)LayerCondKind.AnimId).Select(c => (int)c.Layer)
                .Concat(rules.Rules.Where(r => r.Kind == (byte)LayerRuleKind.Cover || r.Kind == (byte)LayerRuleKind.Reorder).SelectMany(r => new[] { (int)r.Layer, (int)r.Other }))
                .Where(l => l > 0 && l < N).Distinct().OrderBy(l => l).ToArray();

        private static void Run(Compare cmp, LayerRules source, bool full, Func<bool> stop)
        {
            var v = Values(source);
            int[] layers = RuleLayers(source);
            var items = layers.ToDictionary(l => l, l => Items(v, l));
            // every listed body, also in the mutation runs (a body-map mutant changes one body's group)
            ushort[] bodies = Bodies;

            // singles: every layer, every item, every body; and the order with every direction
            foreach (int l in Enumerable.Range(1, N - 1))
            {
                foreach (var it in items.TryGetValue(l, out var li) ? li : new List<(ushort, ushort)?> { null, (Ordinary, Ordinary) })
                {
                    var o = Outfit.Empty();
                    o.Set(l, it);
                    cmp.Covered(o, bodies);
                    foreach (ushort b in GroupBodies) cmp.Order(o.Key(), b, true);
                    if (stop()) return;
                }
            }

            // pairs of rule layers, every item pair
            for (int i = 0; i < layers.Length; i++)
            for (int j = i + 1; j < layers.Length; j++)
            {
                foreach (var a in items[layers[i]])
                foreach (var b in items[layers[j]])
                {
                    var o = Outfit.Empty();
                    o.Set(layers[i], a);
                    o.Set(layers[j], b);
                    cmp.Covered(o, full ? GroupBodies : new ushort[] { 0x190, 0x29A });
                    cmp.Order(o.Key(), 0x190, full);
                }

                if (stop()) return;
            }

            // the rule groups' joint layers: every item combination (values only, no gfx x anim cross)
            int[][] groups =
            {
                new[] { 0x04, 0x17, 0x16, 0x18 }, // pants, skirt, robe, legs
                new[] { 0x03, 0x04, 0x16, 0x18 }, // shoes, pants, robe, legs
                new[] { 0x11, 0x0D, 0x16 },       // tunic, torso, robe
                new[] { 0x0A, 0x06, 0x16 },       // necklace, helmet, robe
                new[] { 0x0E, 0x13, 0x16 },       // bracelet, arms, robe
                new[] { 0x05, 0x04, 0x03 },       // shirt, pants, shoes
                new[] { 0x11, 0x16, 0x14, 0x06 }, // tunic, robe, cloak, helmet
                new[] { 0x13, 0x0D, 0x06, 0x0A }, // arms, torso, helmet, necklace
            };

            foreach (int[] g in groups)
            {
                var lists = g.Select(l => Items(v, l)).ToArray();

                foreach (var combo in Cross(lists))
                {
                    var o = Outfit.Empty();
                    for (int k = 0; k < g.Length; k++) o.Set(g[k], combo[k]);
                    cmp.Covered(o, new ushort[] { 0x190, 0x29B });
                    cmp.Order(o.Key(), 0x191, false);
                }

                if (stop()) return;
            }

            // random full outfits, the item a cross of any graphic and any AnimID of the layer
            var rnd = new Random(1234);
            int samples = full ? 100_000 : 20_000;

            for (int s = 0; s < samples; s++)
            {
                var o = Outfit.Empty();

                foreach (int l in layers)
                {
                    if (rnd.Next(3) == 0) continue;
                    var gs = v[(l, LayerCondKind.Graphic)];
                    var an = v[(l, LayerCondKind.AnimId)];
                    o.Set(l, (gs.ElementAt(rnd.Next(gs.Count)), an.ElementAt(rnd.Next(an.Count))));
                }

                ushort body = Bodies[rnd.Next(Bodies.Length)];
                cmp.Covered(o, new[] { body });
                cmp.Order(o.Key(), body, (s & 7) == 0);

                if ((s & 1023) == 0 && stop()) return;
            }

            cmp.Remaps();
        }

        private static IEnumerable<(ushort, ushort)?[]> Cross(List<(ushort, ushort)?>[] lists)
        {
            var idx = new int[lists.Length];

            while (true)
            {
                yield return idx.Select((x, k) => lists[k][x]).ToArray();
                int p = 0;
                while (p < lists.Length && ++idx[p] == lists[p].Count) idx[p++] = 0;
                if (p == lists.Length) yield break;
            }
        }

        // ---- tests -----------------------------------------------------------------------------------------------------

        [Fact]
        public void Default_Rules_Equal_The_Hard_Coded_Rules_Exhaustively()
        {
            var cmp = new Compare(LayerRulesDefault.Create());
            Run(cmp, cmp.Rules, full: true, () => false);
            _out.WriteLine($"checks {cmp.Checks}, mismatches {cmp.Mismatches}");
            cmp.First.Should().BeNull();
            cmp.Checks.Should().BeGreaterThan(5_000_000);
        }

        [Fact]
        public void File_Round_Trips_And_Evaluates_The_Same()
        {
            LayerRules a = LayerRulesDefault.Create();
            string path = Path.Combine(Path.GetTempPath(), $"layerrules-{Guid.NewGuid():N}.bin");

            try
            {
                a.Write(path);
                byte[] bytes = File.ReadAllBytes(path);
                LayerRules b = LayerRules.Read(path);
                b.Bodies.Should().Equal(a.Bodies);
                b.Tables.Should().Equal(a.Tables);
                b.Rules.Should().Equal(a.Rules);
                b.Conds.Should().Equal(a.Conds);
                b.Ranges.Should().Equal(a.Ranges);
                _out.WriteLine($"layerrules.bin {bytes.Length} bytes: {a.Rules.Length} rules, {a.Conds.Length} conds, {a.Ranges.Length} ranges");

                // a second write is byte-identical (deterministic converter output)
                b.Write(path);
                File.ReadAllBytes(path).Should().Equal(bytes);

                var cmp = new Compare(b);
                Run(cmp, a, full: false, () => false);
                cmp.First.Should().BeNull();

                // truncated or trailing bytes are refused
                File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 1).ToArray());
                FluentActions.Invoking(() => LayerRules.Read(path)).Should().Throw<Exception>();
                File.WriteAllBytes(path, bytes.Concat(new byte[] { 0 }).ToArray());
                FluentActions.Invoking(() => LayerRules.Read(path)).Should().Throw<InvalidDataException>();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Client_Helpers_Equal_The_Hard_Coded_Order()
        {
            // LayerRuleEval (the client's entry points) with the built-in rules
            LayerRuleEval.Rules.Rules.Should().Equal(LayerRulesDefault.Create().Rules);
            Span<Layer> hard = stackalloc Layer[N];
            Span<Layer> raw = stackalloc Layer[N];
            Span<Layer> data = stackalloc Layer[N];
            var key = new ushort[N];
            key[(int)Layer.Cloak] = 0x380;
            key[(int)Layer.Torso] = 0x39A;
            key[(int)Layer.Helmet] = 0x1A9;
            key[(int)Layer.Necklace] = 0x1C8;

            for (byte dir = 0; dir < 8; dir++)
            {
                PaperdollOrder.Build(key, true, raw);
                int hc = PaperdollOrder.ApplyDirectionCloak(hard, PaperdollOrder.Filter(raw, false, hard), dir);
                int dc = LayerRuleEval.BuildInWorld(key, LayerRuleEval.Subject(0x191, true, false), dir, data);
                data.Slice(0, dc).ToArray().Should().Equal(hard.Slice(0, hc).ToArray());
            }

            int pc = LayerRuleEval.BuildPaperdoll(key, LayerRuleEval.Subject(0x29A, false, true), data);
            PaperdollOrder.Build(key, true, raw);
            int ph = PaperdollOrder.Filter(raw, false, hard);
            data.Slice(0, pc).ToArray().Should().Equal(hard.Slice(0, ph).ToArray());

            LayerRuleEval.Subject(0x29B, false, false).Group.Should().Be((byte)BodyGroup.GargoyleFemale);
            LayerRuleEval.Subject(0x25D, false, false).Group.Should().Be((byte)BodyGroup.ElfMale);
            LayerRuleEval.Subject(0x0C, false, false).Group.Should().Be((byte)BodyGroup.Other);
        }

        [Fact]
        public void Malformed_Rules_Are_Refused()
        {
            LayerRules a = LayerRulesDefault.Create();
            LayerRules With(Action<LayerRule[], LayerCond[], byte[]> change)
            {
                var rules = (LayerRule[])a.Rules.Clone();
                var conds = (LayerCond[])a.Conds.Clone();
                var tables = (byte[])a.Tables.Clone();
                change(rules, conds, tables);
                return new LayerRules(a.Bodies, tables, rules, conds, a.Ranges);
            }

            int lastSelect = Array.FindLastIndex(a.Rules, r => r.Kind == (byte)LayerRuleKind.Select && r.Set == 0);
            FluentActions.Invoking(() => With((r, c, t) => r[lastSelect].CondCount = 1)).Should().Throw<InvalidDataException>("no default table");
            FluentActions.Invoking(() => With((r, c, t) => t[1] = t[2])).Should().Throw<InvalidDataException>("a layer twice");
            FluentActions.Invoking(() => With((r, c, t) => r[0].Value = 99)).Should().Throw<InvalidDataException>("no such table");
            FluentActions.Invoking(() => With((r, c, t) => r[0].Kind = 9)).Should().Throw<InvalidDataException>();
            FluentActions.Invoking(() => With((r, c, t) => c[0].Kind = 9)).Should().Throw<InvalidDataException>();
            int remap = Array.FindIndex(a.Rules, r => r.Kind == (byte)LayerRuleKind.Remap);
            FluentActions.Invoking(() => With((r, c, t) => c[r[remap].CondStart].Kind = (byte)LayerCondKind.AnimId)).Should().Throw<InvalidDataException>("a remap reads no layer");
        }

        // Inventory fixtures: expectations written from the inventory (handoffs/t3-layering.md), not derived from either
        // implementation; both the default data and the hard-coded rules must give them.
        [Fact]
        public void Inventory_Fixtures_Hold_For_Data_And_Hard_Coded_Rules()
        {
            LayerRules rules = LayerRulesDefault.Create();
            var human = new LayerSubject(rules.GroupOf(0x190), 0, 0x190);

            int Pos(ReadOnlySpan<Layer> o, Layer l) => o.IndexOf(l);

            // O5: shirt worn + pants AnimID 0x398 -> Pants drawn before (under) Shirt
            var key = new ushort[N];
            key[(int)Layer.Shirt] = Ordinary;
            key[(int)Layer.Pants] = 0x398;
            Span<Layer> hard = stackalloc Layer[N];
            Span<Layer> data = stackalloc Layer[N];
            PaperdollOrder.Build(key, false, hard);
            rules.BuildOrder(LayerRuleSet.World, key, human, MemoryMarshal.Cast<Layer, byte>(data));
            Pos(hard, Layer.Pants).Should().BeLessThan(Pos(hard, Layer.Shirt));
            Pos(data, Layer.Pants).Should().BeLessThan(Pos(data, Layer.Shirt));

            // D1: cloak on top facing 0, at the bottom facing 3, else just below the helmet
            key = new ushort[N];
            key[(int)Layer.Cloak] = Ordinary;
            key[(int)Layer.Helmet] = Ordinary;

            foreach (byte dir in new byte[] { 0, 3, 5 })
            {
                PaperdollOrder.Build(key, false, hard);
                int hc = PaperdollOrder.ApplyDirectionCloak(hard, PaperdollOrder.Filter(hard.ToArray(), false, hard), dir);
                int dc = World(rules, key, human, dir, data);

                foreach (var (o, c) in new[] { (hard.ToArray(), hc), (data.ToArray(), dc) })
                {
                    int ci = Array.IndexOf(o, Layer.Cloak, 0, c);
                    if (dir == 0) ci.Should().Be(c - 1);
                    else if (dir == 3) ci.Should().Be(0);
                    else o[ci + 1].Should().Be(Layer.Helmet);
                }
            }

            // R1: the gargoyle draw remaps the robe 0x1D5 -> 0x156; a human doesn't
            var garg = new LayerSubject(rules.GroupOf(0x29A), LayerRulesConst.FlagGargoyleDraw, 0x29A);
            rules.Remap(LayerRuleSet.World, 0x1D5, garg).Should().Be(0x156);
            AnimationRules.FixGargoyleEquipment(0x1D5).Should().Be(0x156);
            rules.Remap(LayerRuleSet.World, 0x1D5, human).Should().Be(0x1D5);

            // R2: the paperdoll's gargoyle ghost (0x2B6/0x2B7) shroud 0x3CA -> 0x223 from 7.0.0.0 only; not a living gargoyle
            foreach (var (body, cv, expected) in new (ushort, bool, ushort)[] { (0x2B6, true, 0x223), (0x2B7, true, 0x223), (0x2B6, false, 0x3CA), (0x29A, true, 0x3CA) })
            {
                LayerRuleEval.RemapPaperdollHard(0x3CA, body, cv).Should().Be(expected);
                rules.Remap(LayerRuleSet.Paperdoll, 0x3CA, new LayerSubject(rules.GroupOf(body), cv ? LayerRulesConst.FlagCv7000 : (byte)0, body)).Should().Be(expected);
            }

            // C9: hood 0x4B9D covers the helmet on a human, not on a gargoyle; C1: pants 0x513 covers the shoes
            var o2 = Outfit.Empty();
            o2.Set((int)Layer.Robe, (0x4B9D, Ordinary));
            o2.Set((int)Layer.Helmet, (Ordinary, Ordinary));
            o2.Set((int)Layer.Pants, (Ordinary, 0x513));
            o2.Set((int)Layer.Shoes, (Ordinary, Ordinary));

            foreach (var (body, covered) in new (ushort, bool)[] { (0x190, true), (0x29A, false) })
            {
                AnimationRules.IsCovered(Layer.Helmet, o2.Gfx, o2.Anim, o2.Present, body).Should().Be(covered);
                rules.IsCovered(LayerRuleSet.World, (byte)Layer.Helmet, o2.Present, o2.Gfx, o2.Anim, new LayerSubject(rules.GroupOf(body), 0, body)).Should().Be(covered);
                rules.IsCovered(LayerRuleSet.Paperdoll, (byte)Layer.Shoes, o2.Present, o2.Gfx, o2.Anim, new LayerSubject(rules.GroupOf(body), 0, body)).Should().BeTrue();
            }
        }

        [Theory]
        [InlineData(24, true)]
        [InlineData(25, false)]
        [InlineData(31, false)]
        [InlineData(32, false)]
        public void A_File_Whose_Condition_Reads_A_Layer_Past_The_Tables_Is_Refused(int layer, bool accepted)
        {
            // the evaluator's tables hold LayerCount (25) layers: a layer-reading condition on 25..255 is refused at load
            // (LayerRuleEval then falls back to the built-in rules), not left to throw at evaluation
            LayerRules a = LayerRulesDefault.Create();
            string path = Path.Combine(Path.GetTempPath(), $"layerrules-{Guid.NewGuid():N}.bin");

            try
            {
                a.Write(path);
                byte[] bytes = File.ReadAllBytes(path);
                int condStart = bytes.Length - a.Ranges.Length * Marshal.SizeOf<LayerRange>() - a.Conds.Length * Marshal.SizeOf<LayerCond>();
                int ci = Array.FindIndex(a.Conds, c => c.Kind == (byte)LayerCondKind.AnimId);
                bytes[condStart + ci * Marshal.SizeOf<LayerCond>()].Should().Be((byte)LayerCondKind.AnimId, "the patched entry is the condition");
                bytes[condStart + ci * Marshal.SizeOf<LayerCond>() + 1] = (byte)layer;
                File.WriteAllBytes(path, bytes);

                if (accepted)
                {
                    LayerRules b = LayerRules.Read(path);
                    b.Conds[ci].Layer.Should().Be((byte)layer);
                    Span<bool> present = stackalloc bool[N];
                    Span<ushort> v = stackalloc ushort[N];
                    for (byte l = 1; l < N; l++) b.IsCovered(LayerRuleSet.World, l, present, v, v, default);
                    b.BuildOrder(LayerRuleSet.World, v, default, stackalloc byte[N]);
                }
                else
                {
                    FluentActions.Invoking(() => LayerRules.Read(path)).Should().Throw<InvalidDataException>();
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        // Mutation check: change one entry of the default file at a time (a rule's result, table, operation, layers or
        // remap target; a condition's negation; a range's ends): the comparison must find a difference for every mutant.
        [Fact]
        public void Every_Single_Entry_Mutation_Is_Caught()
        {
            LayerRules a = LayerRulesDefault.Create();
            var mutants = new List<(string name, LayerRules rules)>();

            LayerRules Make(Action<LayerRule[], LayerCond[], LayerRange[]> change)
            {
                var rules = (LayerRule[])a.Rules.Clone();
                var conds = (LayerCond[])a.Conds.Clone();
                var ranges = (LayerRange[])a.Ranges.Clone();
                change(rules, conds, ranges);
                return new LayerRules(a.Bodies, a.Tables, rules, conds, ranges);
            }

            // body map: move each body across the only group partition the default rules read (the gargoyle groups of the
            // hood exception); a move within a side (e.g. human male to elf female) changes no answer, as no rule reads it
            for (int i = 0; i < a.Bodies.Length; i++)
            {
                int k = i;
                var bodies = (BodyGroupEntry[])a.Bodies.Clone();
                bool garg = bodies[k].Group >= (byte)BodyGroup.GargoyleMale;
                bodies[k].Group = garg ? (byte)BodyGroup.HumanMale : (byte)BodyGroup.GargoyleMale;
                mutants.Add(($"body 0x{a.Bodies[k].Body:X3} group", new LayerRules(bodies, a.Tables, a.Rules, a.Conds, a.Ranges)));
            }

            // table entries: swap each pair of neighbours in each table (a table must list each layer once)
            for (int t = 0; t < a.Tables.Length / N; t++)
            for (int i = 0; i < N - 1; i++)
            {
                var tables = (byte[])a.Tables.Clone();
                (tables[t * N + i], tables[t * N + i + 1]) = (tables[t * N + i + 1], tables[t * N + i]);
                mutants.Add(($"table {t} swap {i}/{i + 1} (0x{a.Tables[t * N + i]:X2},0x{a.Tables[t * N + i + 1]:X2})", new LayerRules(a.Bodies, tables, a.Rules, a.Conds, a.Ranges)));
            }

            for (int i = 0; i < a.Rules.Length; i++)
            {
                int k = i;
                LayerRule r = a.Rules[i];

                switch ((LayerRuleKind)r.Kind)
                {
                    case LayerRuleKind.Cover:
                        mutants.Add(($"rule {k} cover result", Make((x, c, g) => x[k].Other ^= 1)));
                        break;
                    case LayerRuleKind.Select:
                        mutants.Add(($"rule {k} table", Make((x, c, g) => x[k].Value = (ushort)((x[k].Value + 1) % 3))));
                        break;
                    case LayerRuleKind.Reorder:
                        // every other operation and the A/B swap; the equivalent ones are listed (with why) in Equivalent
                        for (byte op = 0; op < 3; op++)
                        {
                            byte o = op;
                            if (o != r.Op) mutants.Add(($"rule {k} op {(LayerReorderOp)o}", Make((x, c, g) => x[k].Op = o)));
                        }

                        mutants.Add(($"rule {k} swap a/b", Make((x, c, g) => (x[k].Layer, x[k].Other) = (x[k].Other, x[k].Layer))));
                        mutants.Add(($"rule {k} layer a", Make((x, c, g) => x[k].Layer = LayerRulesDefault.Talisman)));
                        mutants.Add(($"rule {k} layer b", Make((x, c, g) => x[k].Other = LayerRulesDefault.Talisman)));
                        break;
                    case LayerRuleKind.DirMove:
                        mutants.Add(($"rule {k} dir op", Make((x, c, g) => x[k].Op = (byte)((x[k].Op + 1) % 3))));
                        mutants.Add(($"rule {k} dir mask", Make((x, c, g) => x[k].Value &= (ushort)(x[k].Value - 1))));
                        break;
                    case LayerRuleKind.Remap:
                        mutants.Add(($"rule {k} remap", Make((x, c, g) => x[k].Value2++)));
                        break;
                }
            }

            for (int i = 0; i < a.Conds.Length; i++)
            {
                int k = i;
                mutants.Add(($"cond {k} negate", Make((x, c, g) => c[k].Negate ^= 1)));
            }

            for (int i = 0; i < a.Ranges.Length; i++)
            {
                int k = i;
                LayerRange r = a.Ranges[i];
                // widening onto a value another range of the same condition already holds is an equivalent mutant
                LayerCond own = a.Conds.First(c => k >= c.RangeStart && k < c.RangeStart + c.RangeCount);
                bool Held(int v) => Enumerable.Range(own.RangeStart, own.RangeCount).Any(j => j != k && v >= a.Ranges[j].Lo && v <= a.Ranges[j].Hi);
                if (r.Lo > 0 && !Held(r.Lo - 1)) mutants.Add(($"range {k} lo-1", Make((x, c, g) => g[k].Lo--)));
                // a group range ending at the last group can't widen to a body that exists (equivalent mutant)
                bool groupEnd = a.Conds.Any(c => c.Kind == (byte)LayerCondKind.Group && k >= c.RangeStart && k < c.RangeStart + c.RangeCount) && r.Hi >= (ushort)BodyGroup.GargoyleFemale;
                if (r.Hi < 0xFFFF && !groupEnd && !Held(r.Hi + 1)) mutants.Add(($"range {k} hi+1", Make((x, c, g) => g[k].Hi++)));
                if (r.Lo < r.Hi) mutants.Add(($"range {k} lo+1", Make((x, c, g) => g[k].Lo++)));
            }

            var survivors = new List<string>();

            foreach (var (name, rules) in mutants)
            {
                var cmp = new Compare(rules);
                Run(cmp, a, full: false, () => cmp.Mismatches > 0);
                if (cmp.Mismatches == 0) survivors.Add(name);
            }

            _out.WriteLine($"{mutants.Count} mutants, {mutants.Count - survivors.Count} caught");
            foreach (string sv in survivors) _out.WriteLine($"survivor: {sv}");
            survivors.Should().BeEquivalentTo(Equivalent(a).Keys);
        }

        // Mutants shown equivalent one by one (the default rules give the same answer for every input); each entry says why.
        // Anything else that survives is a corpus gap. Two facts carry the arguments: MoveTo and MoveAfter run the same code
        // when B follows A (both shift A to B's index); and when B is A's immediate predecessor, MoveTo(A,B), MoveTo(B,A) and
        // SwapIfAfter(A,B) all swap the two. Positions are those of every base table (T1, T2, T3) after the earlier
        // reorders, which only move what each entry names.
        private static Dictionary<string, string> Equivalent(LayerRules a)
        {
            var d = new Dictionary<string, string>();

            foreach (byte set in new byte[] { 0, 1 })
            {
                int p = Array.FindIndex(a.Rules, r => r.Set == set && r.Kind == (byte)LayerRuleKind.Reorder);
                // O5 MoveTo(Pants, Shirt): the first reorder; Shirt immediately precedes Pants in every table
                d[$"rule {p} op SwapIfAfter"] = "O5: Shirt is Pants' immediate predecessor, so the swap is MoveTo";
                d[$"rule {p} swap a/b"] = "O5: MoveTo(Shirt, Pants) on neighbours is the same swap";
                // O6 SwapIfAfter(Shoes, Pants), pants 0x1EB/0x1FA/0x200 (O5 excluded by pants 0x398): Pants immediately precedes Shoes
                d[$"rule {p + 1} op MoveTo"] = "O6: Pants is Shoes' immediate predecessor (O5 can't apply), so MoveTo(Shoes, Pants) is the swap";
                // O7/O8 MoveTo(Pants, Shoes), pants 0x513/0x514 or 0x3E4 (O5, O6 excluded): Shoes follows Pants
                d[$"rule {p + 2} op MoveAfter"] = "O7: Shoes follows Pants (MoveTo = MoveAfter)";
                d[$"rule {p + 2} swap a/b"] = "O7: Pants and Shoes are neighbours, MoveTo(Shoes, Pants) is the same swap";
                d[$"rule {p + 3} op MoveAfter"] = "O8: Shoes follows Pants (MoveTo = MoveAfter)";
                d[$"rule {p + 3} swap a/b"] = "O8: Pants and Shoes are neighbours, MoveTo(Shoes, Pants) is the same swap";
                // O9 MoveAfter(Tunic, Waist): Waist follows Tunic in every table, no earlier rule moves either
                d[$"rule {p + 4} op MoveTo"] = "O9: Waist follows Tunic (MoveTo = MoveAfter)";
                // O9b MoveTo(Robe, Necklace): Necklace follows Robe (O9 moved only Tunic)
                d[$"rule {p + 5} op MoveAfter"] = "O9b: Necklace follows Robe (MoveTo = MoveAfter)";
                // O10 MoveAfter(Cloak, Robe): Cloak is first after Invalid in every table, nothing earlier moves it
                d[$"rule {p + 6} op MoveTo"] = "O10: Robe follows Cloak (MoveTo = MoveAfter)";
                // O11 MoveAfter(Necklace, Helmet): Helmet follows Necklace (O9b's MoveTo keeps Necklace before Helmet)
                d[$"rule {p + 7} op MoveTo"] = "O11: Helmet follows Necklace (MoveTo = MoveAfter)";
                // O12 MoveTo(Robe, Helmet): Helmet follows Robe (also after O9b)
                d[$"rule {p + 8} op MoveAfter"] = "O12: Helmet follows Robe (MoveTo = MoveAfter)";
            }

            return d;
        }
    }
}
