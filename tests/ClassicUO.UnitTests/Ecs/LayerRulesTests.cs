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

            public void Remaps()
            {
                for (int a = 0; a <= 0xFFFF; a++)
                {
                    foreach (byte flags in new byte[] { 0, 1, 2, 3 })
                    {
                        var subject = new LayerSubject((byte)BodyGroup.GargoyleMale, flags);
                        ushort hard = (flags & 2) != 0 ? AnimationRules.FixGargoyleEquipment((ushort)a) : (ushort)a;
                        Checks++;

                        if (Rules.Remap(LayerRuleSet.World, (ushort)a, subject) != hard || Rules.Remap(LayerRuleSet.Paperdoll, (ushort)a, subject) != (ushort)a)
                        {
                            Fail($"remap 0x{a:X4} flags {flags}");
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
            ushort[] bodies = full ? Bodies : GroupBodies;

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
                        // (no op or A/B swap mutants: MoveTo and MoveAfter agree whenever B follows A, and the rules' A and B
                        // are neighbours in every base table, so those mutants are equivalent; moving another layer is not)
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
            survivors.Should().BeEmpty();
        }
    }
}
