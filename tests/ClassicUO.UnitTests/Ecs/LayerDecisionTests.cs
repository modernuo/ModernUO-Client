using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using Entity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T3b cache (LayerDecision, WorldState.WorldLayers): the cached world layering answer equals a fresh evaluation of the
    // rules across every key input: equipment slot changes (WornLayers.Version), in-place writes to worn items (Look: a
    // graphic change keeps the slot), the body, the female / gargoyle-draw / 7.0.0.0 flags, the rules instance and the
    // facing; and every key field on its own invalidates (a cache that ignored it would serve a different answer). The
    // wearer's tables come from the mirror's records here (the client gathers them with FindItemByLayer; no tiledata in
    // the unit tests).
    [Collection(nameof(LayerRulesStateCollection))]
    public class LayerDecisionTests
    {
        private const uint MOB = 0x0000_3000, OTHER = 0x0000_3001;
        private const int N = PaperdollOrder.N;

        // typical worn items per layer (graphic, AnimID), including the values the default rules key on (as the E2 rows)
        private static readonly Dictionary<int, (ushort g, ushort a)[]> Choices = new()
        {
            [(int)Layer.Shirt] = new (ushort, ushort)[] { (0x1517, 0x1D2), (0x1EFD, 0x1E9) },
            [(int)Layer.Pants] = new (ushort, ushort)[] { (0x152E, 0x1EB), (0x1539, 0x200), (0x1411, 0x513), (0x13BE, 0x3E4), (0x1412, 0x398) },
            [(int)Layer.Shoes] = new (ushort, ushort)[] { (0x170F, 0x1F1), (0x1711, 0x1F5) },
            [(int)Layer.Helmet] = new (ushort, ushort)[] { (0x1408, 0x201), (0x1715, 0x1A9), (0xA42B, 0x5E9) },
            [(int)Layer.Necklace] = new (ushort, ushort)[] { (0x13C7, 0x1C8), (0x1088, 0x5EC) },
            [(int)Layer.Hair] = new (ushort, ushort)[] { (0x203B, 0x1F7), (0x203C, 0x1F8) },
            [(int)Layer.Torso] = new (ushort, ushort)[] { (0x1415, 0x21A), (0x1C04, 0x39A), (0x13CC, 0x3A9), (0x782A, 0x3AA) },
            [(int)Layer.Tunic] = new (ushort, ushort)[] { (0x1FFD, 0x238), (0x1541, 0x1E5) },
            [(int)Layer.Arms] = new (ushort, ushort)[] { (0x1410, 0x210), (0x13CD, 0x3B1) },
            [(int)Layer.Bracelet] = new (ushort, ushort)[] { (0xB1C0, 0x3B5), (0x1086, 0x3B6) },
            [(int)Layer.Cloak] = new (ushort, ushort)[] { (0x1515, 0x1E0), (0x2FB9, 0x380) },
            [(int)Layer.Robe] = new (ushort, ushort)[] { (0x1F03, 0x504), (0x2683, 0x1E6), (0x9985, 0x4E8), (0x204E, 0x229), (0x4B9D, 0x1D5), (0x1F04, 0x3CA) },
            [(int)Layer.Skirt] = new (ushort, ushort)[] { (0x1516, 0x1C7), (0x1537, 0x1E4) },
            [(int)Layer.Legs] = new (ushort, ushort)[] { (0x1411, 0x1F2) },
            [(int)Layer.OneHanded] = new (ushort, ushort)[] { (0x0F5E, 0x2E0), (0x0EFA, 0x3D8) },
        };

        private static readonly ushort[] Bodies = { 0x190, 0x191, 0x25D, 0x25E, 0x29A, 0x29B, 0x2B6, 0x2B7, 0x0190, 0x00C8 };

        private readonly Dictionary<ushort, ushort> _animOf = new();
        private readonly WorldMirror _mirror = new();
        private uint _nextItem = 0x4000_1000;

        public LayerDecisionTests()
        {
            foreach (var c in Choices.Values)
            foreach (var (g, a) in c)
            {
                _animOf[g] = a;
            }

            _mirror.UpsertMobile(MOB, 0x190, 0, 10, 10, 0, 0, 0, false);
            _mirror.UpsertMobile(OTHER, 0x190, 0, 11, 10, 0, 0, 0, false);
        }

        private Entity Record(uint serial)
        {
            _mirror.TryGetEntity(serial, out Entity e).Should().BeTrue();
            return e;
        }

        private ulong VersionOf(uint serial) => Record(serial).GetComponent<EquipmentSlots>().Worn.Version;

        private uint Wear(uint mobile, int layer, ushort graphic)
        {
            uint s = _nextItem++;
            _mirror.UpsertItem(s, graphic, 0, 0, 0, 0, 0, 0, 1, mobile, (byte)layer, false, attach: true);
            return s;
        }

        // the wearer's tables from the mirror (what the client's Gather reads through FindItemByLayer)
        private LayerTables Tables(uint mobile)
        {
            LayerTables t = default;
            ref WornLayers w = ref Record(mobile).GetComponent<EquipmentSlots>().Worn;

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                uint s = w.Get((byte)l);

                if (s != 0)
                {
                    ushort g = Record(s).GetComponent<Graphic>().Value;
                    t.Present[l] = true;
                    t.Graphic[l] = g;
                    t.Anim[l] = _animOf.TryGetValue(g, out ushort a) ? a : (ushort)0;
                }
            }

            return t;
        }

        private static LayerSubject Subject(LayerRules rules, ushort body, bool female, bool garg, bool cv7000) =>
            new LayerSubject(rules.GroupOf(body), (byte)((female ? LayerRulesConst.FlagFemale : 0) | (garg ? LayerRulesConst.FlagGargoyleDraw : 0) | (cv7000 ? LayerRulesConst.FlagCv7000 : 0)), body);

        // the client's WorldState.WorldLayers with the tables from the mirror
        private bool Get(LayerDecision d, LayerRules rules, LayerSubject subject, uint mobile = MOB)
        {
            if (d.Lookup(rules, subject, VersionOf(mobile), _mirror, Record(mobile)))
            {
                return true;
            }

            LayerTables t = Tables(mobile);
            d.Fill(ref t);
            return false;
        }

        private static string Answer(LayerRules rules, ref LayerTables t, LayerSubject subject, byte dir)
        {
            Span<byte> raw = stackalloc byte[N];
            Span<Layer> order = stackalloc Layer[N];
            rules.BuildOrder(LayerRuleSet.World, t.Anim, subject, raw);
            int count = PaperdollOrder.Filter(MemoryMarshal.Cast<byte, Layer>(raw), false, order);
            count = rules.ApplyDirection(LayerRuleSet.World, MemoryMarshal.Cast<Layer, byte>(order), count, dir, t.Anim, subject);
            var sb = new System.Text.StringBuilder("order");

            for (int i = 0; i < count; i++) sb.Append(' ').Append((byte)order[i]);

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                if (!t.Present[l]) continue;
                bool cov = rules.IsCovered(LayerRuleSet.World, (byte)l, t.Present, t.Graphic, t.Anim, subject);
                ushort remap = t.Anim[l] == 0 ? (ushort)0 : rules.Remap(LayerRuleSet.World, t.Anim[l], subject);
                sb.Append($" | {l}:{(cov ? "C" : "-")}:{remap:X}");
            }

            return sb.ToString();
        }

        private static string Answer(LayerDecision d, ref LayerTables t, byte dir)
        {
            var sb = new System.Text.StringBuilder("order");

            foreach (Layer l in d.Order(dir)) sb.Append(' ').Append((byte)l);

            for (int l = (int)Layer.OneHanded; l <= (int)Layer.Legs; l++)
            {
                if (!t.Present[l]) continue;
                sb.Append($" | {l}:{(d.IsCovered((Layer)l) ? "C" : "-")}:{d.Remap((Layer)l):X}");
            }

            return sb.ToString();
        }

        [Fact]
        public void The_Cached_Answer_Equals_A_Fresh_Evaluation_Across_Every_Key_Input()
        {
            LayerRules rules = LayerRulesDefault.Create();
            LayerRules other = LayerRulesEcsTests_Custom(rules);
            var rnd = new Random(31);
            var d = new LayerDecision();
            var worn = new List<(uint serial, int layer)>();
            ushort body = 0x190;
            bool female = false, garg = false, cv = true;
            LayerRules using_ = rules;
            long hits0 = LayerDecision.Hits, misses0 = LayerDecision.Misses;
            var seen = new HashSet<string>();
            int steps = 20_000, compared = 0;

            for (int step = 0; step < steps; step++)
            {
                switch (rnd.Next(12))
                {
                    case 0: case 1: case 2: // wear (a new item; another on an occupied layer goes behind the first)
                    {
                        int layer = new List<int>(Choices.Keys)[rnd.Next(Choices.Count)];
                        var c = Choices[layer][rnd.Next(Choices[layer].Length)];
                        worn.Add((Wear(MOB, layer, c.g), layer));
                        break;
                    }
                    case 3: // unequip (the record's removal)
                        if (worn.Count > 0)
                        {
                            int k = rnd.Next(worn.Count);
                            _mirror.Remove(worn[k].serial);
                            worn.RemoveAt(k);
                        }

                        break;
                    case 4: case 5: // a worn item's graphic changes in place (keeps its slot: only Look moves)
                        if (worn.Count > 0)
                        {
                            var (s, layer) = worn[rnd.Next(worn.Count)];
                            var c = Choices[layer][rnd.Next(Choices[layer].Length)];
                            _mirror.UpsertItem(s, c.g, 0, 0, 0, 0, 0, 0, 1, MOB, (byte)layer, false, attach: false);
                        }

                        break;
                    case 6: // the body (the mirror marks the equipment too; the subject carries it)
                        body = Bodies[rnd.Next(Bodies.Length)];
                        _mirror.UpsertMobile(MOB, body, 0, 10, 10, 0, 0, 0, false);
                        break;
                    case 7: female = !female; break;
                    case 8: garg = !garg; break;
                    case 9: cv = !cv; break;
                    case 10: // another wearer's equipment changes (a mark elsewhere: the slow check, a hit)
                        Wear(OTHER, (int)Layer.Shirt, 0x1517);
                        break;
                    case 11: using_ = rnd.Next(4) == 0 ? (using_ == rules ? other : rules) : using_; break;
                }

                LayerSubject subject = Subject(using_, body, female, garg, cv);
                Get(d, using_, subject);
                LayerTables t = Tables(MOB);

                // a few facings each step (the classes fill lazily), every class over the run
                for (int k = 0; k < 3; k++)
                {
                    byte dir = (byte)rnd.Next(16);
                    string want = Answer(using_, ref t, subject, dir);
                    Answer(d, ref t, dir).Should().Be(want, $"step {step} dir {dir}");
                    seen.Add(want);
                    compared++;
                }
            }

            (LayerDecision.Hits - hits0).Should().BeGreaterThan(steps / 10, "steps that leave the key alone, marks elsewhere");
            (LayerDecision.Misses - misses0).Should().BeGreaterThan(steps / 4);
            seen.Count.Should().BeGreaterThan(200, "the walk reaches many distinct answers");
            compared.Should().Be(steps * 3);
        }

        // the default rules with the world set's first hair cover rule (the 0xA42B helmet) negated: a different rules instance whose answers differ
        private static LayerRules LayerRulesEcsTests_Custom(LayerRules a)
        {
            var rules = (LayerRule[])a.Rules.Clone();
            int k = Array.FindIndex(rules, x => x.Set == (byte)LayerRuleSet.World && x.Kind == (byte)LayerRuleKind.Cover && x.Layer == (byte)Layer.Hair);
            rules[k].Other = (byte)(rules[k].Other == 0 ? 1 : 0);
            return new LayerRules(a.Bodies, a.Tables, rules, (LayerCond[])a.Conds.Clone(), a.Ranges);
        }

        [Fact]
        public void Every_Key_Field_On_Its_Own_Invalidates()
        {
            // API level: IsCurrent and Revalidate fail when exactly one field differs from the key they took
            LayerRules rules = LayerRulesDefault.Create(), other = LayerRulesEcsTests_Custom(rules);
            var s = new LayerSubject(rules.GroupOf(0x190), LayerRulesConst.FlagCv7000, 0x190);
            var d = new LayerDecision();
            d.Revalidate(rules, s, 5, 7, 9).Should().BeFalse("a new decision takes the key");
            LayerTables t = default;
            d.Fill(ref t);
            d.IsCurrent(rules, s, 5, 9).Should().BeTrue();
            d.Revalidate(rules, s, 5, 7, 10).Should().BeTrue("only the mirror-wide count moved: a hit");
            d.IsCurrent(rules, s, 5, 10).Should().BeTrue();

            var mutants = new (string field, Func<LayerDecision, bool> check)[]
            {
                ("changes (fast path)", x => x.IsCurrent(rules, s, 5, 11)),
                ("version (fast path)", x => x.IsCurrent(rules, s, 6, 10)),
                ("rules (fast path)", x => x.IsCurrent(other, s, 5, 10)),
                ("body (fast path)", x => x.IsCurrent(rules, new LayerSubject(s.Group, s.Flags, 0x191), 5, 10)),
                ("group (fast path)", x => x.IsCurrent(rules, new LayerSubject(2, s.Flags, s.Body), 5, 10)),
                ("female (fast path)", x => x.IsCurrent(rules, new LayerSubject(s.Group, (byte)(s.Flags | LayerRulesConst.FlagFemale), s.Body), 5, 10)),
                ("gargoyle (fast path)", x => x.IsCurrent(rules, new LayerSubject(s.Group, (byte)(s.Flags | LayerRulesConst.FlagGargoyleDraw), s.Body), 5, 10)),
                ("cv7000 (fast path)", x => x.IsCurrent(rules, new LayerSubject(s.Group, 0, s.Body), 5, 10)),
                ("look", x => x.Revalidate(rules, s, 5, 8, 11)),
                ("version", x => x.Revalidate(rules, s, 6, 7, 11)),
                ("rules", x => x.Revalidate(other, s, 5, 7, 11)),
                ("body", x => x.Revalidate(rules, new LayerSubject(s.Group, s.Flags, 0x191), 5, 7, 11)),
                ("flags", x => x.Revalidate(rules, new LayerSubject(s.Group, 0, s.Body), 5, 7, 11)),
            };

            foreach (var (field, check) in mutants)
            {
                var x = new LayerDecision();
                x.Revalidate(rules, s, 5, 7, 10);
                x.Fill(ref t);
                check(x).Should().BeFalse(field);
            }
        }

        // a scenario per key input in which only that input changes and the answer changes with it: a cache that left the
        // input out of its key would serve the old (wrong) answer; this one misses and serves the new one
        [Fact]
        public void Each_Key_Input_Changes_The_Answer_And_Is_Seen()
        {
            LayerRules rules = LayerRulesDefault.Create();
            var d = new LayerDecision();
            const byte DIR = 2;

            string Run(LayerRules r, LayerSubject subject, string why, bool expectHit = false)
            {
                bool hit = Get(d, r, subject);
                hit.Should().Be(expectHit, why);
                LayerTables t = Tables(MOB);
                string got = Answer(d, ref t, DIR);
                got.Should().Be(Answer(r, ref t, subject, DIR), why);
                return got;
            }

            LayerSubject human = Subject(rules, 0x190, false, false, true);
            uint robe = Wear(MOB, (int)Layer.Robe, 0x1F03); // a closed robe (0x504) covers shoes and pants
            Wear(MOB, (int)Layer.Shoes, 0x170F);
            uint helm = Wear(MOB, (int)Layer.Helmet, 0x1408);
            string a0 = Run(rules, human, "first use");
            Run(rules, human, "nothing changed", expectHit: true).Should().Be(a0);

            // a mark elsewhere: the slow check, still a hit
            Wear(OTHER, (int)Layer.Shirt, 0x1517);
            Run(rules, human, "another wearer changed", expectHit: true).Should().Be(a0);

            // Version: a slot change (the robe leaves)
            ulong v = VersionOf(MOB);
            _mirror.Remove(robe);
            VersionOf(MOB).Should().NotBe(v);
            string a1 = Run(rules, human, "slot change");
            a1.Should().NotBe(a0);

            // Look: a graphic change in place (helmet -> a hood robe graphic is not a helmet; use the hair-covering helmet)
            uint look = Record(MOB).GetComponent<EquipmentSlots>().Worn.Look;
            v = VersionOf(MOB);
            Wear(MOB, (int)Layer.Hair, 0x203B);
            string a2 = Run(rules, human, "hair worn");
            v = VersionOf(MOB);
            look = Record(MOB).GetComponent<EquipmentSlots>().Worn.Look;
            _mirror.UpsertItem(helm, 0xA42B, 0, 0, 0, 0, 0, 0, 1, MOB, (byte)Layer.Helmet, false, attach: false);
            VersionOf(MOB).Should().Be(v, "an in-place graphic change keeps the slot");
            Record(MOB).GetComponent<EquipmentSlots>().Worn.Look.Should().NotBe(look);
            string a3 = Run(rules, human, "in-place graphic change (0xA42B covers the hair)");
            a3.Should().NotBe(a2);

            Wear(MOB, (int)Layer.Torso, 0x1C04); // chest piece 0x39A: the alt torso table for women and gargoyles
            Wear(MOB, (int)Layer.Arms, 0x13CD);
            string b0 = Run(rules, human, "torso and arms");

            // female flag
            string f1 = Run(rules, Subject(rules, 0x190, true, false, true), "female");
            f1.Should().NotBe(b0, "the female torso table");

            // gargoyle-draw flag: the remap of a gargoyle robe AnimID
            Wear(MOB, (int)Layer.Cloak, 0x1515);
            uint grobe = Wear(MOB, (int)Layer.Robe, 0x4B9D); // AnimID 0x1D5: remapped for the gargoyle draw
            string h0 = Run(rules, human, "a hood robe on a human: hair and helmet covered");

            // body (and its group) alone, no mirror write: the hood 0x4B9D covers on every body except gargoyles
            string g0 = Run(rules, Subject(rules, 0x29A, false, false, true), "gargoyle body, no draw flag");
            g0.Should().NotBe(h0, "the hood rule's gargoyle exception");
            string g1 = Run(rules, Subject(rules, 0x29A, false, true, true), "gargoyle draw flag");
            g1.Should().NotBe(g0, "the remap needs the draw flag");

            // cv7000 flag: the rules' Body/Flag conditions (the paperdoll's 0x3CA remap is a paperdoll rule; for the world
            // set the subject still differs: a stale cache can't be told by the answer, so the API test covers the field)
            Run(rules, Subject(rules, 0x29A, false, true, false), "cv7000 off");

            // the rules instance
            LayerRules other = LayerRulesEcsTests_Custom(rules);
            _mirror.Remove(grobe);
            LayerSubject h = Subject(rules, 0x190, false, false, true);
            string r0 = Run(rules, h, "default rules");
            string r1 = Run(other, Subject(other, 0x190, false, false, true), "other rules");
            r1.Should().NotBe(r0, "the custom hair cover rule");

            // direction: the class fills lazily, each facing's answer is its own
            var t2 = Tables(MOB);
            var orders = new HashSet<string>();

            for (byte dir = 0; dir < 16; dir++)
            {
                Get(d, rules, h).Should().Be(dir > 0, "first use after the rules swap, then hits");
                Answer(d, ref t2, dir).Should().Be(Answer(rules, ref t2, h, dir), $"dir {dir}");
                orders.Add(Answer(d, ref t2, dir));
            }

            orders.Count.Should().BeGreaterThan(1, "the cloak moves with the facing");
        }

        [Fact]
        public void A_Decision_Is_The_Size_The_Memory_Summary_Counts_And_A_Hit_Allocates_Nothing()
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var d = new LayerDecision();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            (a1 - a0).Should().Be(LayerDecision.Bytes);

            LayerRules rules = LayerRulesDefault.Create();
            Wear(MOB, (int)Layer.Shirt, 0x1517);
            LayerSubject s = Subject(rules, 0x190, false, false, true);
            Get(d, rules, s);
            Entity e = Record(MOB);

            for (byte dir = 0; dir < 9; dir++)
            {
                d.Order(dir);
            }

            long b0 = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                d.Lookup(rules, s, e.GetComponent<EquipmentSlots>().Worn.Version, _mirror, e);
                d.Order((byte)(i & 7));
            }

            (GC.GetAllocatedBytesForCurrentThread() - b0).Should().Be(0);
        }
    }
}
