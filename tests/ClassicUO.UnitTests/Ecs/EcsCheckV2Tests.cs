using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 9 (ecs-transition-plan.md, T1 Checkers): -ecscheck v2. The funnel commits Name, Hits/HitsMax,
    // NotorietyFlag, every item's Layer, Price and the child order (a stamp per PushToBack) to the mirror; the check compares
    // them with World. A world built through the funnel checks clean; each deliberate divergence is reported.
    [Collection(nameof(TimeTicksCollection))]
    public class EcsCheckV2Tests
    {
        private const uint MOB = 0x0000_3900, VENDOR = 0x0000_3901, STAT = 0x0000_3902, PACK = 0x4000_7900, A = 0x4000_7901, B = 0x4000_7902, C = 0x4000_7903, SHIRT = 0x4000_7904, HELM = 0x4000_7905, CORPSE = 0x4000_7906, LOOT = 0x4000_7907, RET = 0x4000_7909, WEAR = 0x4000_790A, CLOOT = 0x4000_790B;

        // a world built only through funnel operations (and the creating Syncs of GetOrCreate*)
        internal static World BuildFunnelWorld()
        {
            Time.Ticks = 100_000;
            var world = new World();
            world.Writer.ThrowOnViolation = true;

            Mobile m = world.GetOrCreateMobile(MOB);
            world.Writer.SetNotoriety(m, NotorietyFlag.Murderer);
            world.Writer.SetStatus(m, "Moe", 40, 50);
            world.Writer.SetHits(m, 60, 55);
            world.Writer.SetName(m, "Moe the Brave");

            Mobile vendor = world.GetOrCreateMobile(VENDOR);
            world.Writer.SetNotoriety(vendor, NotorietyFlag.Invulnerable);
            world.Writer.SetName(vendor, "Bob");
            world.Writer.SetName(vendor, null);
            world.Writer.SetHits(vendor, 0, 0);

            // a mobile only 0x11 (SetStatus) wrote
            world.Writer.SetStatus(world.GetOrCreateMobile(STAT), "Stan", 9, 12);

            // a pack on the ground, three items in it, the first re-pushed to the back
            Item pack = world.Writer.UpsertWorldItem(PACK, 0xE75, 0, 1, 1001, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(A, 0xEED, 5, 10, 10, 0, PACK, out _);
            world.Writer.PlaceInContainer(B, 0xEEE, 1, 20, 20, 0, PACK, out _);
            world.Writer.PlaceInContainer(C, 0xEEF, 1, 30, 30, 0, PACK, out _);
            world.Writer.PlaceInContainer(A, 0xEED, 6, 11, 11, 0, PACK, out _);
            world.Writer.SetName(world.Items.Get(B), "a gem");
            world.Writer.SetPrice(world.Items.Get(C), 120);
            world.Writer.SetHits(world.Items.Get(C), 10, 7);

            // worn items, a second wear moving one to the back, a 0x78 wear, a held item returned to the paperdoll
            world.Writer.Equip(RET, 0x13C6, Layer.Gloves, MOB, 0, out _, out _);
            world.Writer.Equip(SHIRT, 0x1517, Layer.Shirt, MOB, 0, out _, out _);
            world.Writer.Equip(HELM, 0x1408, Layer.Helmet, MOB, 0, out _, out _);
            world.Writer.Equip(SHIRT, 0x1517, Layer.Shirt, MOB, 0x21, out _, out _);
            world.Writer.WearItem(m, WEAR, 0x1F03, Layer.Robe, 0);
            world.Writer.ReturnHeldItem(RET, 0x13C6, 0, 1, Flags.None, Layer.Gloves, 0, 0, 0, MOB, out _, out WorldWriter.HeldItemReturn returned);
            returned.Should().Be(WorldWriter.HeldItemReturn.Paperdoll);

            // a corpse with an item laid in it
            Item corpse = world.Writer.UpsertWorldItem(CORPSE, 0x2006, 0, 0x190, 1002, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement cp);
            world.Writer.PlaceWorldItem(cp);
            world.Writer.PlaceInContainer(LOOT, 0x1F14, 1, 5, 5, 0, CORPSE, out _);
            world.Writer.PlaceCorpseEquipment(corpse, CLOOT, Layer.Pants);

            return world;
        }

        private static List<string> Check(World world)
        {
            var reported = new List<string>();
            world.Ecs.MismatchObserver = reported.Add;
            world.Ecs.RunCheck(false);
            world.Ecs.MismatchObserver = null;

            return reported;
        }

        [Fact]
        public void A_World_Built_Through_The_Funnel_Checks_Clean_And_Its_Mirror_Has_The_V2_Fields()
        {
            World world = BuildFunnelWorld();

            Check(world).Should().BeEmpty();

            world.Ecs.Mirror.TryGetExtras(MOB, out MirrorExtras m).Should().BeTrue();
            (m.Name, m.Hits, m.HitsMax, m.Notoriety).Should().Be(("Moe the Brave", (ushort)55, (ushort)60, (byte)NotorietyFlag.Murderer));
            world.Ecs.Mirror.TryGetExtras(STAT, out MirrorExtras st).Should().BeTrue();
            (st.Name, st.Hits, st.HitsMax).Should().Be(("Stan", (ushort)9, (ushort)12));
            world.Ecs.Mirror.TryGetExtras(C, out MirrorExtras c).Should().BeTrue();
            (c.Price, c.Hits, c.HitsMax).Should().Be((120u, (ushort)7, (ushort)10));
            world.Ecs.Mirror.TryGetExtras(HELM, out MirrorExtras helm).Should().BeTrue();
            helm.ItemLayer.Should().Be((byte)Layer.Helmet);

            // the child order: the re-pushed item is last, in the list and by stamp
            Children(world, PACK).Should().Equal(B, C, A);
            ByStamp(world, PACK).Should().Equal(B, C, A);
            Children(world, MOB).Should().Equal(HELM, SHIRT, WEAR, RET);
            ByStamp(world, MOB).Should().Equal(HELM, SHIRT, WEAR, RET);
            Children(world, CORPSE).Should().Equal(LOOT, CLOOT);
            ByStamp(world, CORPSE).Should().Equal(LOOT, CLOOT);
        }

        [Fact]
        public void The_Death_Rekey_Carries_The_V2_Fields_And_The_Child_Order()
        {
            World world = BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);
            uint dead = world.Writer.RekeyDeadMobile(m);

            Check(world).Should().BeEmpty();
            world.Ecs.Mirror.TryGetExtras(dead, out MirrorExtras x).Should().BeTrue();
            (x.Name, x.Notoriety).Should().Be(("Moe the Brave", (byte)NotorietyFlag.Murderer));
            ByStamp(world, dead).Should().Equal(HELM, SHIRT, WEAR, RET);
        }

        [Fact]
        public void The_Old_Rekey_Carries_Them_Too()
        {
            World world = BuildFunnelWorld();
            WorldWriterMobileTests.OldRekey(world, world.Mobiles.Get(MOB));

            Check(world).Should().BeEmpty();
        }

        // T9 S5: the item and worn layer cases went (the Layer is the record's only, so it cannot diverge from it)
        public enum Divergence { ItemPercentage, MobilePercentage, Notoriety }

        [Theory]
        [InlineData(Divergence.ItemPercentage, "item 0x40007903 v2")]
        [InlineData(Divergence.MobilePercentage, "mobile 0x00003900 v2")]
        [InlineData(Divergence.Notoriety, "mobile 0x00003901 v2")]
        public void A_Deliberate_Mismatch_Is_Reported(Divergence d, string expected)
        {
            World world = BuildFunnelWorld();
            Check(world).Should().BeEmpty();

            // each a write that bypasses the funnel (or a mirror write World never made)
            switch (d)
            {
                // T9: the percentage the hits operations set, changed without a hits write
                case Divergence.ItemPercentage: world.Items.Get(C).UpdateHits(3); break;
                case Divergence.MobilePercentage: world.Mobiles.Get(MOB).UpdateHits(61); break;
                case Divergence.Notoriety: world.Mobiles.Get(VENDOR).NotorietyFlag = NotorietyFlag.Innocent; break;
            }

            List<string> reported = Check(world);
            reported.Should().NotBeEmpty();
            reported.Should().Contain(s => s.StartsWith(expected, StringComparison.Ordinal), $"the {d} divergence");
        }

        // ---- the Codex audit of part 9

        // P1 (bench-000095: every client crashed at login with "IComponent type not found: MobileNotoriety"): NativeAOT's
        // schema has only what EcsSchema registers, so every component and tag type of ClassicUO.Ecs must be registered. A
        // test process may build Friflo's reflection schema first and so never sees the gap; this compares the lists.
        [Fact]
        public void Every_Component_And_Tag_Type_Is_Registered_In_The_Schema()
        {
            Type[] kinds = typeof(WorldMirror).Assembly.GetTypes()
                .Where(t => t.IsValueType && (typeof(Friflo.Engine.ECS.IComponent).IsAssignableFrom(t) || typeof(Friflo.Engine.ECS.ITag).IsAssignableFrom(t)))
                .ToArray();

            kinds.Should().Contain(new[] { typeof(EntityName), typeof(Vitals), typeof(MobileNotoriety), typeof(ItemPrice), typeof(ItemData) });
            EcsSchema.RegisteredTypes.Should().BeEquivalentTo(kinds);
        }

        private const uint SHOP = 0x4000_7920, S1 = 0x4000_7921, S2 = 0x4000_7922, S3 = 0x4000_7923, S4 = 0x4000_7924;

        // P2: 0x74 sorts a 0x2AF8 shop container's list by X; the mirror's child order follows (arrival order differs)
        [Fact]
        public void The_Vendor_Sort_Restamps_The_Child_Order_In_The_Sorted_Order()
        {
            World world = BuildFunnelWorld();
            Item shop = world.Writer.UpsertWorldItem(SHOP, 0x2AF8, 0, 1, 1003, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(S1, 0xEED, 1, 30, 10, 0, SHOP, out _);
            world.Writer.PlaceInContainer(S2, 0xEED, 1, 10, 10, 0, SHOP, out _);
            world.Writer.PlaceInContainer(S3, 0xEED, 1, 20, 10, 0, SHOP, out _);
            world.Writer.PlaceInContainer(S4, 0xEED, 1, 10, 20, 0, SHOP, out _);
            Children(world, SHOP).Should().Equal(S1, S2, S3, S4);

            world.Writer.SortShopContents(shop);

            Children(world, SHOP).Should().Equal(new[] { S2, S4, S3, S1 }, "by X, equal X in arrival order (a stable sort)");
            ByStamp(world, SHOP).Should().Equal(S2, S4, S3, S1);
            Check(world).Should().BeEmpty();
        }

        [Fact]
        public void The_Vendor_Sort_Nested_Is_Rejected_And_Sorts_Nothing()
        {
            World world = BuildFunnelWorld();
            world.Writer.ThrowOnViolation = false;
            Item shop = world.Writer.UpsertWorldItem(SHOP, 0x2AF8, 0, 1, 1003, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(S1, 0xEED, 1, 30, 10, 0, SHOP, out _);
            world.Writer.PlaceInContainer(S2, 0xEED, 1, 10, 10, 0, SHOP, out _);
            world.Ecs.BeginFold(world.Mobiles.Get(MOB));
            world.Writer.SortShopContents(shop);
            world.Ecs.EndFold(world.Mobiles.Get(MOB));

            world.Writer.Violations.Should().Be(1);
            Children(world, SHOP).Should().Equal(S1, S2);
            Check(world).Should().BeEmpty();
        }

        private const uint LATE = 0x0000_3910, LATE2 = 0x0000_3911, ORPHAN = 0x4000_7930, BOOK = 0x4000_7931, INBOOK = 0x4000_7932;

        // P2: an item equipped before its mobile exists is in no child list (ClassicUO never adopts it); the mirror keeps its
        // Container but not as attached, and the check compares attachment
        [Fact]
        public void An_Orphan_Equip_Is_Not_Attached_And_Checks_Clean_Then_Reattaches()
        {
            World world = BuildFunnelWorld();
            world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out Entity wearer, out _);
            wearer.Should().BeNull();

            Mobile late = world.GetOrCreateMobile(LATE);

            world.State.HasChildren(late).Should().BeFalse("the legacy orphan behaviour is unchanged");
            ((int)world.Ecs.Mirror.GetEquipment(LATE).Count).Should().Be(1, "the mirror has its Container");
            world.Ecs.Mirror.TryGetExtras(ORPHAN, out MirrorExtras x).Should().BeTrue();
            x.ChildStamp.Should().Be(0UL);
            Check(world).Should().BeEmpty();

            // re-equipped now that the wearer exists: linked and attached
            world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out wearer, out _);
            wearer.Should().BeSameAs(late);
            Children(world, LATE).Should().Equal(ORPHAN);
            ByStamp(world, LATE).Should().Equal(ORPHAN);
            Check(world).Should().BeEmpty();

            // an attached item re-equipped on a wearer that does not exist yet: out of its old list, an orphan (detached)
            world.Writer.Equip(SHIRT, 0x1517, Layer.Shirt, LATE2, 0, out wearer, out _);
            wearer.Should().BeNull();
            world.Ecs.Mirror.TryGetExtras(SHIRT, out x).Should().BeTrue();
            x.ChildStamp.Should().Be(0UL);
            world.State.HasChildren(world.GetOrCreateMobile(LATE2)).Should().BeFalse();
            Check(world).Should().BeEmpty();
        }

        [Fact]
        public void An_Attached_Item_Moved_Without_A_Push_Detaches()
        {
            World world = BuildFunnelWorld();

            // to the ground through the funnel: no parent
            Item a = world.Items.Get(A);
            world.Writer.UpsertWorldItem(A, a.Graphic, 0, a.Amount, 1005, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Ecs.Mirror.TryGetExtras(A, out MirrorExtras x).Should().BeTrue();
            x.ChildStamp.Should().Be(0UL);

            // the old Sync path: another parent's Container without a link (as an orphan): detached
            Item b = world.Items.Get(B);
            world.RemoveItemFromContainer(b);
            b.TestSetContainer(CORPSE);
            world.Ecs.Sync(b);
            world.Ecs.Mirror.TryGetExtras(B, out x).Should().BeTrue();
            x.ChildStamp.Should().Be(0UL);
            ((int)world.Ecs.Mirror.GetContents(CORPSE).Count).Should().Be(3);

            // straight from one container to another, and from one wearer to another (no ground state in between): detached
            Item c = world.Items.Get(C);
            c.TestSetContainer(CORPSE);
            world.Ecs.Sync(c);
            world.Ecs.Mirror.TryGetExtras(C, out x).Should().BeTrue();
            x.ChildStamp.Should().Be(0UL);

            Item helm = world.Items.Get(HELM);
            helm.TestSetContainer(VENDOR);
            world.Ecs.Sync(helm);
            world.Ecs.Mirror.TryGetExtras(HELM, out x).Should().BeTrue();
            x.ChildStamp.Should().Be(0UL);

            Check(world).Should().BeEmpty();
        }

        // P2: 0xBF.1B's spellbook clear drops a held child from the list without changing its Container
        [Fact]
        public void The_Spellbook_Clear_Detaches_A_Held_Child_And_A_Later_Push_Reattaches_It()
        {
            World world = BuildFunnelWorld();
            Item book = world.Writer.UpsertWorldItem(BOOK, 0xEFA, 0, 1, 1004, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(INBOOK, 0x1F2E, 1, 0, 0, 0, BOOK, out _);
            Children(world, BOOK).Should().Equal(INBOOK);

            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b101, 0);

            world.Items.Get(INBOOK).Container.Should().Be(BOOK, "the clear keeps the Container (legacy)");
            world.Ecs.Mirror.TryGetExtras(INBOOK, out MirrorExtras x).Should().BeTrue();
            x.ChildStamp.Should().Be(0UL);
            Check(world).Should().BeEmpty();

            world.Writer.PlaceInContainer(INBOOK, 0x1F2E, 1, 0, 0, 0, BOOK, out _);
            ByStamp(world, BOOK).Should().Equal(INBOOK);
            Check(world).Should().BeEmpty();
        }

        // P3: the stamp is 64-bit: past the old 32-bit wrap the order holds; Clear resets the counter (no record is left)
        [Fact]
        public void The_Child_Order_Holds_Past_The_32_Bit_Boundary_And_Clear_Resets_The_Counter()
        {
            World world = BuildFunnelWorld();
            world.Ecs.Mirror.SetLastChildStampForTests(uint.MaxValue - 1);

            world.Writer.PlaceInContainer(B, 0xEEE, 1, 20, 20, 0, PACK, out _);
            world.Writer.PlaceInContainer(C, 0xEEF, 1, 30, 30, 0, PACK, out _);
            world.Writer.PlaceInContainer(A, 0xEED, 6, 11, 11, 0, PACK, out _);

            world.Ecs.Mirror.ChildStamp.Should().Be((ulong)uint.MaxValue + 2);
            Children(world, PACK).Should().Equal(B, C, A);
            ByStamp(world, PACK).Should().Equal(B, C, A);
            Check(world).Should().BeEmpty();

            world.Ecs.Mirror.Clear();
            world.Ecs.Mirror.ChildStamp.Should().Be(0UL);
        }

        // the parent's children as the readers see them (WorldState.Contents, the mirror's cached order)
        private static List<uint> Children(World world, uint parent) => world.State.Contents(parent).ToArray().ToList();

        private static List<uint> ByStamp(World world, uint parent)
        {
            WorldMirror mirror = world.Ecs.Mirror;

            // the attached children (a nonzero stamp), by stamp
            return mirror.GetContents(parent).Concat(mirror.GetEquipment(parent))
                .Select(e => e.GetComponent<ClassicUO.Ecs.Serial>().Value)
                .Select(s => { mirror.TryGetExtras(s, out MirrorExtras x); return (s, x.ChildStamp); })
                .Where(t => t.ChildStamp != 0)
                .OrderBy(t => t.ChildStamp)
                .Select(t => t.s)
                .ToList();
        }
    }
}
