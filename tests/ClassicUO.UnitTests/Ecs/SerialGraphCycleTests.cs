using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 child-list slice B2 (Codex audit of slice B, P1): the mirror keeps one record per serial, so an item and a mobile
    // sharing a serial read merged contents, and an attach that the old per-object lists held acyclic could close a loop in
    // the merged serial graph (FindItem, GetTotalAmount and the destroy cascades then recursed until the stack overflowed).
    // WorldWriter refuses an attach that would close a loop (the item keeps what it had: no write); the recursive readers
    // and cascades stop at their root and at WorldState.MaxNestingDepth if a loop ever gets in some other way.
    [Collection(nameof(TimeTicksCollection))]
    public class SerialGraphCycleTests
    {
        private const uint MOB = 0x0000_3300, DEAD = 0x8000_3300;
        private const uint B = 0x4000_8001, C = 0x4000_8002, PACK = 0x4000_8010, BAG = 0x4000_8011, X = 0x4000_8012, Y = 0x4000_8013, GROUND = 0x4000_8020;

        private static World NewWorld()
        {
            Time.Ticks = 100_000;
            var world = new World();
            world.Writer.ThrowOnViolation = true;

            return world;
        }

        // MOB wears B (with C inside it), then dies: re-keyed to DEAD, still wearing B
        private static World DeadWearer()
        {
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);
            world.Writer.PlaceInContainer(C, 0xEED, 1, 10, 10, 0, B, out _);
            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);
            world.State.Contents(DEAD).ToArray().Should().Equal(B);

            return world;
        }

        // a pack on the ground holding a bag holding X
        private static World Nested()
        {
            World world = NewWorld();
            world.Writer.UpsertWorldItem(PACK, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(BAG, 0xE76, 1, 10, 10, 0, PACK, out _);
            world.Writer.PlaceInContainer(X, 0xEED, 1, 10, 10, 0, BAG, out _);

            return world;
        }

        // the merged serial graph (every record's parent, attached or not) has no loop; a record that is its own parent is
        // the shared case (an item worn by the mobile under its serial) and is allowed only when a mobile holds the serial
        internal static void AssertAcyclic(World world)
        {
            WorldMirror mirror = world.Ecs.Mirror;

            foreach (uint start in new List<uint>(mirror.Serials))
            {
                uint s = start;

                for (int i = 0; i < 10_000; i++)
                {
                    mirror.TryGet(s, out MirrorRecord r).Should().BeTrue();

                    if (r.ParentKind == ParentKind.None)
                    {
                        break;
                    }

                    if (r.Parent == s)
                    {
                        world.Mobiles.Get(s).Should().NotBeNull($"0x{s:X8} is its own parent with no mobile under its serial");

                        break;
                    }

                    r.Parent.Should().NotBe(start, $"0x{start:X8} is its own ancestor");

                    if (!mirror.TryGet(r.Parent, out _))
                    {
                        break;
                    }

                    s = r.Parent;
                    i.Should().BeLessThan(9_999, "the walk ends");
                }
            }

            world.State.SnapshotDepth.Should().Be(0);
        }

        // every recursive reader and cascade, which must end
        private static void RunReaders(World world, uint serial)
        {
            foreach (Entity e in new Entity[] { world.Items.Get(serial), world.Mobiles.Get(serial) })
            {
                if (e == null)
                {
                    continue;
                }

                e.FindItem(0xFFFE);
                e.FindItem(0xFFFE, 0x21);
                e.GetItemByGraphic(0xFFFE, true);

                if (e is Item it)
                {
                    int amount = 0;
                    it.GetTotalAmount(0xFFFE, null, ref amount);
                    _ = it.RootContainer;
                }
            }
        }

        [Fact]
        public void Equip_Under_A_Dead_Mobiles_Serial_Onto_What_It_Wears_Is_Refused()
        {
            // the Codex scenario: MOB wears B; MOB dies (DEAD); 0x2E equips an item with DEAD's serial onto B. The old lists
            // were acyclic (mobile > B > item); merged, the item would read B, and B the item
            World world = DeadWearer();

            Item a = world.Writer.Equip(DEAD, 0x1F03, Layer.Talisman, B, 0, out Entity wearer, out _);

            a.Should().BeNull("the attach would close a loop: refused");
            wearer.Should().BeNull();
            world.Items.Get(DEAD).Should().BeNull("a refused attach creates nothing");
            world.State.Contents(DEAD).ToArray().Should().Equal(B);
            world.State.Contents(B).ToArray().Should().Equal(C);
            world.Writer.CycleRefusals.Should().Be(1);
            world.Writer.Violations.Should().Be(0, "a server's cyclic packet is not a caller bug");
            AssertAcyclic(world);
            RunReaders(world, DEAD);
            RunReaders(world, B);
        }

        [Fact]
        public void Equip_Under_A_Dead_Mobiles_Serial_Onto_A_Deeper_Child_Is_Refused()
        {
            World world = DeadWearer();

            world.Writer.Equip(DEAD, 0x1F03, Layer.Talisman, C, 0, out _, out _).Should().BeNull();
            world.Writer.PlaceInContainer(DEAD, 0xEED, 1, 1, 1, 0, C, out Entity container).Should().BeNull("0x25 too");
            container.Should().BeNull("the handler returns as for a missing container");
            world.Items.Get(DEAD).Should().BeNull();
            world.Writer.CycleRefusals.Should().Be(2);
            AssertAcyclic(world);
            RunReaders(world, DEAD);
        }

        [Fact]
        public void An_Existing_Item_Under_A_Dead_Mobiles_Serial_Keeps_Its_Place_When_Refused()
        {
            // the item with the serial exists (on the ground) before the mobile dies onto it; then 0x2E moves it onto B
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);
            world.Writer.UpsertWorldItem(DEAD, 0xEED, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB));
            AssertAcyclic(world);

            Item item = world.Items.Get(DEAD);
            ushort graphic = item.Graphic;
            uint container = item.Container;

            world.Writer.Equip(DEAD, 0x1F04, Layer.Talisman, B, 0, out _, out _).Should().BeNull();

            item.IsDestroyed.Should().BeFalse();
            world.Items.Get(DEAD).Should().BeSameAs(item);
            item.Graphic.Should().Be(graphic, "a refused attach writes nothing");
            item.Container.Should().Be(container);
            world.State.Contents(DEAD).ToArray().Should().Equal(B);
            AssertAcyclic(world);
            RunReaders(world, DEAD);
        }

        [Fact]
        public void An_Item_Equipped_Under_A_Serial_Before_A_Death_Rekeys_Onto_It_Leaves_No_Loop()
        {
            // the other order: the item under DEAD's serial is equipped onto B first, then MOB dies onto that serial (the rekey
            // replaces the item's record with the mobile's, so the item is no longer attached under B)
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);
            world.Writer.Equip(DEAD, 0x1F04, Layer.Talisman, B, 0, out _, out _).Should().NotBeNull();

            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);

            AssertAcyclic(world);
            RunReaders(world, DEAD);
            RunReaders(world, B);

            // and a later equip of that item back onto B is refused
            world.Writer.Equip(DEAD, 0x1F04, Layer.Talisman, B, 0, out _, out _).Should().BeNull();
            AssertAcyclic(world);
        }

        [Fact]
        public void A_Death_Rekey_Onto_A_Serial_Its_Own_Item_Holds_Leaves_No_Loop()
        {
            // rekey into an existing item's serial: the item with DEAD's serial is inside B, which MOB wears
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);
            world.Writer.PlaceInContainer(DEAD, 0xEED, 1, 1, 1, 0, B, out _).Should().NotBeNull();

            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);

            AssertAcyclic(world);
            RunReaders(world, DEAD);
            RunReaders(world, B);

            // the clears and removals end, and a clear never destroys what it clears
            world.Writer.ClearContainer(DEAD, false, itemOnly: true);
            AssertAcyclic(world);
            world.Writer.ClearWornItems(DEAD);
            world.Writer.RemoveMobile(world.Mobiles.Get(DEAD));
            AssertAcyclic(world);
        }

        [Fact]
        public void Equip_Onto_Its_Own_Subtree_Is_Refused()
        {
            World world = Nested();

            world.Writer.Equip(PACK, 0xE75, Layer.Backpack, X, 0, out _, out _).Should().BeNull("X is inside PACK (two levels)");
            world.Writer.Equip(PACK, 0xE75, Layer.Backpack, BAG, 0, out _, out _).Should().BeNull("BAG is inside PACK");
            world.Writer.Equip(BAG, 0xE76, Layer.Backpack, BAG, 0, out _, out _).Should().BeNull("an item is not its own parent");
            world.Writer.CycleRefusals.Should().Be(3);

            world.Items.Get(PACK).OnGround.Should().BeTrue("PACK stays where it was");
            world.State.Contents(PACK).ToArray().Should().Equal(BAG);
            world.State.Contents(BAG).ToArray().Should().Equal(X);
            AssertAcyclic(world);
            RunReaders(world, PACK);
        }

        [Fact]
        public void WearItem_Onto_Its_Own_Subtree_Is_Refused()
        {
            World world = Nested();

            world.Writer.WearItem(world.Items.Get(X), PACK, 0xE75, Layer.Backpack, 0).Should().BeNull();
            world.Writer.WearItem(world.Items.Get(X), X, 0xEED, Layer.Robe, 0).Should().BeNull("an item does not wear itself");

            world.Items.Get(PACK).OnGround.Should().BeTrue();
            AssertAcyclic(world);
            RunReaders(world, PACK);
        }

        [Fact]
        public void A_Mobile_Still_Wears_An_Item_Under_Its_Own_Serial()
        {
            // the shared case slice B kept: 0x78 for the dead mobile lists an entry with its own serial (an item under it)
            World world = DeadWearer();
            Mobile dead = world.Mobiles.Get(DEAD);

            world.Writer.WearItem(dead, DEAD, 0x1F05, Layer.Cloak, 0).Should().NotBeNull("a mobile can wear the item under its serial");
            world.Writer.CycleRefusals.Should().Be(0);
            world.State.Contents(DEAD).ToArray().Should().Equal(B, DEAD);

            // and nothing can then go under it inside the mobile's subtree
            world.Writer.Equip(B, 0x1F03, Layer.Robe, DEAD, 0, out _, out _).Should().NotBeNull("B back onto the mobile: no loop");
            world.Writer.Equip(DEAD, 0x1F05, Layer.Cloak, C, 0, out _, out _).Should().BeNull("the shared item into C, which is under it");
            AssertAcyclic(world);
            RunReaders(world, DEAD);
        }

        [Fact]
        public void PlaceInContainer_Of_Equipment_Into_A_Corpse_Inside_It_Is_Refused()
        {
            // 0x25 moves (does not recreate) an equipped item going into a corpse; the corpse sits inside that item
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(X, 0x1F03, Layer.Robe, MOB, 0, out _, out _);
            world.Writer.PlaceInContainer(Y, 0x2006, 1, 1, 1, 0, X, out _);

            world.Writer.PlaceInContainer(X, 0x1F03, 1, 1, 1, 0, Y, out Entity container).Should().BeNull();

            container.Should().BeNull();
            world.Items.Get(X).Container.Should().Be(MOB, "X stays where it was");
            world.State.Contents(Y).ToArray().Should().BeEmpty();
            AssertAcyclic(world);
            RunReaders(world, X);
            RunReaders(world, MOB);
        }

        [Fact]
        public void PlaceInContainer_Into_Its_Own_Subtree_Destroys_And_Recreates_Without_A_Loop()
        {
            // the destroy-vs-reuse branch: PACK leaves the ground for X (inside it): PACK and its subtree (X too) are destroyed
            // first, so the new PACK goes under a serial no record holds (as before T1)
            World world = Nested();

            world.Writer.PlaceInContainer(PACK, 0xE75, 1, 1, 1, 0, X, out _);

            world.Writer.CycleRefusals.Should().Be(0);
            AssertAcyclic(world);
            RunReaders(world, PACK);
        }

        // ---- T9 slice B3 (Codex audit of B2, P2 #1): the refusal is decided before PlaceInContainer's removals, against
        // the graph those removals leave

        [Fact]
        public void PlaceInContainer_Of_A_Ground_Container_Into_Itself_Is_Refused_Before_Anything_Is_Removed()
        {
            // PACK is still waiting for its placement (an outstanding token), holds BAG > X; 0x25 puts PACK into PACK
            World world = NewWorld();
            world.Writer.UpsertWorldItem(PACK, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceInContainer(BAG, 0xE76, 1, 10, 10, 0, PACK, out _);
            world.Writer.PlaceInContainer(X, 0xEED, 1, 10, 10, 0, BAG, out _);
            Item pack = world.Items.Get(PACK);
            world.Writer.OutstandingPlacements.Should().Be(1);

            world.Writer.PlaceInContainer(PACK, 0xE75, 1, 1, 1, 0, PACK, out Entity container).Should().BeNull("an item is never inside itself");

            container.Should().BeNull();
            world.Writer.CycleRefusals.Should().Be(1);
            pack.IsDestroyed.Should().BeFalse("a refused attach removes nothing");
            world.Items.Get(PACK).Should().BeSameAs(pack);
            world.State.Contents(PACK).ToArray().Should().Equal(BAG);
            world.State.Contents(BAG).ToArray().Should().Equal(X);
            world.Writer.OutstandingPlacements.Should().Be(1, "its placement is not cancelled");
            world.Writer.PlaceWorldItem(p);
            pack.OnGround.Should().BeTrue();
            AssertAcyclic(world);
            RunReaders(world, PACK);
        }

        [Fact]
        public void PlaceInContainer_Of_A_Mobile_Serial_Into_Itself_Is_Refused_Before_The_Mobile_Is_Removed()
        {
            World world = NewWorld();
            Mobile mob = world.GetOrCreateMobile(MOB);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);

            world.Writer.PlaceInContainer(MOB, 0xEED, 1, 1, 1, 0, MOB, out Entity container).Should().BeNull();

            container.Should().BeNull();
            world.Writer.CycleRefusals.Should().Be(1);
            mob.IsDestroyed.Should().BeFalse();
            world.Mobiles.Get(MOB).Should().BeSameAs(mob);
            world.Items.Get(MOB).Should().BeNull();
            world.State.Contents(MOB).ToArray().Should().Equal(B);
            AssertAcyclic(world);
        }

        [Fact]
        public void PlaceInContainer_Refused_Through_An_Unattached_Link_Removes_Nothing()
        {
            // Y names W as its wearer but is not attached (0x2E before W existed); W then appears on the ground. 0x25 puts W
            // into Y: W's replacement would not remove Y (not in W's contents), so the loop W > Y > W would remain: refused
            // before W is destroyed
            World world = NewWorld();
            world.Writer.Equip(Y, 0x1F03, Layer.Robe, B, 0, out _, out _).Should().NotBeNull("no wearer: an orphan");
            world.Writer.UpsertWorldItem(B, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(C, 0xEED, 1, 1, 1, 0, B, out _);
            Item b = world.Items.Get(B);

            world.Writer.PlaceInContainer(B, 0xE75, 1, 1, 1, 0, Y, out _).Should().BeNull();

            world.Writer.CycleRefusals.Should().Be(1);
            b.IsDestroyed.Should().BeFalse("a refused attach removes nothing");
            world.Items.Get(B).Should().BeSameAs(b);
            b.OnGround.Should().BeTrue();
            world.State.Contents(B).ToArray().Should().Equal(C);
            AssertAcyclic(world);
            RunReaders(world, B);
        }

        [Fact]
        public void PlaceInContainer_Of_A_Mobile_Serial_Into_What_It_Wears_Removes_The_Mobile_Then_Places()
        {
            // the mobile branch removes MOB with B (what it wears) first, so the item goes under a serial no record holds (as
            // before T1): not a loop in the graph the removal leaves
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);

            world.Writer.PlaceInContainer(MOB, 0xEED, 1, 1, 1, 0, B, out _).Should().NotBeNull();

            world.Writer.CycleRefusals.Should().Be(0);
            world.Mobiles.Get(MOB).Should().BeNull();
            world.Items.Get(B).Should().BeNull();
            AssertAcyclic(world);
            RunReaders(world, MOB);
        }

        // ---- T9 slice B3 (Codex audit of B2, P2 #2): no accepted attach makes a chain the destroy cascades cannot finish

        private const uint DEEP = 0x4001_0000;

        // DEEP + 0 on the ground, DEEP + i inside DEEP + i - 1 (i = 1..levels): DEEP + levels is levels deep
        private static void Chain(World world, int levels)
        {
            world.Writer.UpsertWorldItem(DEEP, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);

            for (uint i = 1; i <= levels; i++)
            {
                world.Writer.PlaceInContainer(DEEP + i, 0xE75, 1, 1, 1, 0, DEEP + i - 1, out _).Should().NotBeNull($"level {i}");
            }
        }

        [Fact]
        public void Equip_Of_A_Subtree_That_Would_Nest_Past_The_Bound_Is_Refused()
        {
            // the Codex scenario: an accepted chain WorldState.MaxNestingDepth deep, its root then equipped under a fresh
            // parent: its deepest item would sit one level past what the destroy cascade reaches
            World world = NewWorld();
            Chain(world, WorldState.MaxNestingDepth);
            world.Writer.UpsertWorldItem(GROUND, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);

            world.Writer.Equip(DEEP, 0xE75, Layer.Robe, GROUND, 0, out _, out _).Should().BeNull("too deep: refused");

            world.Writer.DepthRefusals.Should().Be(1);
            world.Writer.CycleRefusals.Should().Be(0, "not a loop");
            world.Items.Get(DEEP).OnGround.Should().BeTrue("the chain stays where it was");
            world.State.Contents(GROUND).ToArray().Should().BeEmpty();

            // and a leaf one level below the deepest item
            world.Writer.PlaceInContainer(Y, 0xEED, 1, 1, 1, 0, DEEP + (uint)WorldState.MaxNestingDepth, out _).Should().BeNull();
            world.Writer.DepthRefusals.Should().Be(2);
            AssertAcyclic(world);
        }

        [Fact]
        public void A_Subtree_Moved_To_Exactly_The_Bound_Is_Destroyed_Whole()
        {
            World world = NewWorld();
            Chain(world, WorldState.MaxNestingDepth - 1);
            world.Writer.UpsertWorldItem(GROUND, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);

            world.Writer.Equip(DEEP, 0xE75, Layer.Robe, GROUND, 0, out _, out _).Should().NotBeNull("its deepest item is MaxNestingDepth deep");
            world.Writer.PlaceInContainer(Y, 0xEED, 1, 1, 1, 0, DEEP + (uint)WorldState.MaxNestingDepth - 1, out _).Should().BeNull("one more is too deep");
            world.Writer.DepthRefusals.Should().Be(1);

            world.Writer.RemoveWorldItem(world.Items.Get(GROUND), out _);

            for (uint i = 0; i < WorldState.MaxNestingDepth; i++)
            {
                world.Items.Get(DEEP + i).Should().BeNull($"DEEP + {i} is removed with GROUND");
            }

            world.Ecs.Mirror.Serials.Should().BeEmpty("no record survives the cascade");
        }

        [Fact]
        public void A_Wearers_Chain_Through_The_Item_Under_Its_Serial_Is_Destroyed_Whole()
        {
            // the shared case: the mobile's cascade reaches what it wears a second time through the item under its serial, one
            // level deeper than the record graph says; the bound counts that level
            World world = DeadWearer();
            Mobile dead = world.Mobiles.Get(DEAD);
            world.Writer.WearItem(dead, DEAD, 0x1F05, Layer.Cloak, 0).Should().NotBeNull();
            world.Writer.Equip(B, 0x1F03, Layer.Robe, DEAD, 0, out _, out _).Should().NotBeNull("B after the shared item");

            uint parent = C;
            uint next = DEEP;

            while (world.Writer.PlaceInContainer(next, 0xEED, 1, 1, 1, 0, parent, out _) != null)
            {
                parent = next++;
            }

            world.Writer.DepthRefusals.Should().Be(1);
            var removed = new HashSet<uint>();
            world.RemovalObserver = s => removed.Add(s);

            world.Writer.RemoveMobile(dead);

            for (uint s = DEEP; s < next; s++)
            {
                world.Items.Get(s).Should().BeNull($"0x{s:X8} is removed with the mobile");
            }

            removed.Should().Contain(parent);
            world.State.SnapshotDepth.Should().Be(0);
        }

        // ---- T9 slice B4 (Codex audit of B3)

        // C's chain: DEEP, DEEP + 1, ... inside C until the bound refuses one; returns the deepest accepted item
        private static uint ChainUnderC(World world, out uint next)
        {
            uint parent = C;
            next = DEEP;

            while (world.Writer.PlaceInContainer(next, 0xEED, 1, 1, 1, 0, parent, out _) != null)
            {
                parent = next++;
            }

            return parent;
        }

        private static void AssertChainRemoved(World world, uint next)
        {
            world.Items.Get(B).Should().BeNull();
            world.Items.Get(C).Should().BeNull();

            for (uint s = DEEP; s < next; s++)
            {
                world.Items.Get(s).Should().BeNull($"0x{s:X8} is removed with the mobile");
            }

            world.State.SnapshotDepth.Should().Be(0);
        }

        [Fact]
        public void WearItem_Of_The_Shared_Item_Over_A_Chain_At_The_Bound_Is_Refused()
        {
            // P2 #1, the Codex scenario: the dead mobile's chain reaches MaxNestingDepth; the item under its serial would put
            // it one level deeper (re-pushing B after it makes the cascade reach the chain through it first)
            World world = DeadWearer();
            Mobile dead = world.Mobiles.Get(DEAD);
            ChainUnderC(world, out uint next);
            world.Writer.DepthRefusals.Should().Be(1);

            world.Writer.WearItem(dead, DEAD, 0x1F05, Layer.Cloak, 0).Should().BeNull("the merged subtree would nest past the bound");

            world.Writer.DepthRefusals.Should().Be(2);
            world.Writer.CycleRefusals.Should().Be(0);
            world.Items.Get(DEAD).Should().BeNull("a refused attach creates nothing");
            world.Writer.Equip(B, 0x1F03, Layer.Robe, DEAD, 0, out _, out _).Should().NotBeNull("B re-pushed");
            world.Writer.RemoveMobile(dead);
            AssertChainRemoved(world, next);
        }

        [Fact]
        public void WearItem_Of_The_Shared_Item_Over_A_Chain_One_Short_Of_The_Bound_Is_Destroyed_Whole()
        {
            World world = DeadWearer();
            Mobile dead = world.Mobiles.Get(DEAD);
            uint deepest = ChainUnderC(world, out uint next);
            world.Writer.RemoveWorldItem(world.Items.Get(deepest), out _);

            world.Writer.WearItem(dead, DEAD, 0x1F05, Layer.Cloak, 0).Should().NotBeNull("the deepest item then sits at the bound");
            world.Writer.WearItem(dead, DEAD, 0x1F05, Layer.Cloak, 0).Should().NotBeNull("a re-push of the shared item");
            world.Writer.DepthRefusals.Should().Be(1);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, DEAD, 0, out _, out _).Should().NotBeNull("B after the shared item");

            world.Writer.RemoveMobile(dead);

            AssertChainRemoved(world, next);
            world.Items.Get(DEAD).Should().BeNull();
        }

        [Fact]
        public void A_Death_Rekey_Onto_The_Item_It_Wears_Over_A_Chain_At_The_Bound_Leaves_That_Item_Unattached()
        {
            // the same class at the death rekey: MOB wears the item under DEAD's serial (first) and B, whose chain reaches the
            // bound; after the rekey the mobile under DEAD would wear the item under its own serial: refused (an orphan)
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(DEAD, 0x1F05, Layer.Cloak, MOB, 0, out _, out _).Should().NotBeNull();
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);
            world.Writer.PlaceInContainer(C, 0xEED, 1, 10, 10, 0, B, out _);
            ChainUnderC(world, out uint next);
            world.Writer.DepthRefusals.Should().Be(1);

            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);

            world.Writer.DepthRefusals.Should().Be(2);
            world.State.Contents(DEAD).ToArray().Should().Equal(B);
            AssertAcyclic(world);
            world.Writer.RemoveMobile(world.Mobiles.Get(DEAD));
            AssertChainRemoved(world, next);
        }

        [Fact]
        public void PlaceInContainer_Of_A_Swept_Items_Serial_Into_What_Its_Mobile_Wears_Is_Refused()
        {
            // P2 #2, the Codex scenario: the ground item under DEAD's serial is swept (its destroyed instance stays in the
            // items table); MOB, wearing B, dies onto DEAD. 0x25 puts DEAD into B: the destroyed instance removes nothing, so
            // B stays under DEAD and the new item would close DEAD > B > DEAD
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.Equip(B, 0x1F03, Layer.Robe, MOB, 0, out _, out _);
            world.Writer.UpsertWorldItem(DEAD, 0xEED, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            Item swept = world.Items.Get(DEAD);
            world.Writer.SweepItem(swept);
            swept.IsDestroyed.Should().BeTrue();
            world.Items.Get(DEAD).Should().BeSameAs(swept, "the sweep leaves it in the table");
            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);

            world.Writer.PlaceInContainer(DEAD, 0xEED, 1, 1, 1, 0, B, out Entity container).Should().BeNull();

            container.Should().BeNull();
            world.Writer.CycleRefusals.Should().Be(1);
            world.Items.Get(B).IsDestroyed.Should().BeFalse("a refused attach removes nothing");
            world.State.Contents(DEAD).ToArray().Should().Equal(B);
            AssertAcyclic(world);
            RunReaders(world, DEAD);
            RunReaders(world, B);

            // elsewhere it is placed (the destroyed instance is replaced by a new one)
            world.Writer.UpsertWorldItem(GROUND, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement g);
            world.Writer.PlaceWorldItem(g);
            Item placed = world.Writer.PlaceInContainer(DEAD, 0xEED, 1, 1, 1, 0, GROUND, out _);
            placed.Should().NotBeNull();
            placed.Should().NotBeSameAs(swept);
            AssertAcyclic(world);
        }

        [Fact]
        public void PlaceCorpseEquipment_Onto_A_Corpse_Inside_The_Item_Is_Refused()
        {
            World world = NewWorld();
            world.Writer.UpsertWorldItem(X, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            Item corpse = world.Writer.PlaceInContainer(Y, 0x2006, 1, 1, 1, 0, X, out _);

            world.Writer.PlaceCorpseEquipment(corpse, X, Layer.Robe).Should().BeNull();

            world.Items.Get(X).OnGround.Should().BeTrue();
            AssertAcyclic(world);
            RunReaders(world, X);
        }

        [Fact]
        public void ReturnHeldItem_To_A_Paperdoll_Whose_Serial_Is_Under_The_Item_Is_Refused()
        {
            // 0x2E made an item under the live mobile's serial, equipped onto X on the ground; 0x27 returns X to that mobile
            World world = NewWorld();
            world.GetOrCreateMobile(MOB);
            world.Writer.UpsertWorldItem(X, 0xE75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.Equip(MOB, 0x1F03, Layer.Robe, X, 0, out _, out _).Should().NotBeNull();

            Item back = world.Writer.ReturnHeldItem(X, 0xE75, 0, 1, Flags.None, Layer.Gloves, 0, 0, 0, MOB, out _, out WorldWriter.HeldItemReturn result);

            back.Should().BeNull();
            result.Should().Be(WorldWriter.HeldItemReturn.None);
            world.Items.Get(X).OnGround.Should().BeTrue();
            AssertAcyclic(world);
            RunReaders(world, X);
            RunReaders(world, MOB);
        }

        // ---- defence in depth: a loop forced into the mirror behind the funnel (no packet sequence can make one)

        private static World Forced(out Item a, out Item b)
        {
            World world = Nested();
            a = world.Items.Get(PACK);
            b = world.Items.Get(X);

            // PACK > BAG > X > PACK, attached in the mirror (and in the cache, for RootContainer)
            world.Ecs.Mirror.UpsertItem(PACK, 0xE75, 0, 1, 1, 0, 0, 0, 1, X, 0, false, attach: true);
            a.TestSetContainer(X);
            world.State.Contents(X).ToArray().Should().Equal(PACK);

            return world;
        }

        [Fact]
        public void The_Recursive_Readers_End_On_A_Forced_Loop()
        {
            World world = Forced(out Item pack, out Item x);

            pack.FindItem(0xFFFE).Should().BeNull();
            pack.FindItem(0xEED).Should().BeSameAs(x);
            int amount = 0;
            pack.GetTotalAmount(0xEED, null, ref amount);
            amount.Should().Be(1, "each item of the loop is counted once from its root");
            pack.GetItemByGraphic(0xFFFE, true).Should().BeNull();
            pack.RootContainer.Should().Be(0u, "a loop has no root");
        }

        [Fact]
        public void A_Clear_On_A_Forced_Loop_Keeps_Its_Root()
        {
            World world = Forced(out Item pack, out _);
            world.Writer.UpsertWorldItem(GROUND, 0xEED, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            world.Writer.OutstandingPlacements.Should().Be(1, "CancelSubtreePlacements walks");

            world.Writer.ClearContainer(PACK, false, itemOnly: true);

            pack.IsDestroyed.Should().BeFalse("a clear never destroys the container it clears");
            world.Items.Get(PACK).Should().BeSameAs(pack);
            world.Items.Get(BAG).Should().BeNull();
            world.Items.Get(X).Should().BeNull();
            world.State.SnapshotDepth.Should().Be(0);
        }

        [Fact]
        public void A_Removal_On_A_Forced_Loop_Ends()
        {
            World world = Forced(out Item pack, out _);
            world.Writer.UpsertWorldItem(GROUND, 0xEED, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            var removed = new List<uint>();
            world.RemovalObserver = removed.Add;

            world.Writer.RemoveWorldItem(pack, out _);

            removed.Should().Equal(X, BAG, PACK);
            world.Items.Get(PACK).Should().BeNull();
            world.State.SnapshotDepth.Should().Be(0);
        }

        [Fact]
        public void A_Wearers_Clear_On_A_Forced_Loop_Keeps_The_Wearer()
        {
            World world = DeadWearer();

            // DEAD's record replaced by an item attached under C: DEAD > B > C > DEAD
            Item twin = world.GetOrCreateItem(DEAD, out _);
            world.Ecs.Mirror.UpsertItem(DEAD, 0xEED, 0, 1, 1, 0, 0, 0, 1, C, 0, false, attach: true);
            world.Writer.UpsertWorldItem(GROUND, 0xEED, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            RunReaders(world, DEAD);

            world.Writer.ClearWornItems(DEAD);
            world.Writer.ClearContainer(DEAD, false, itemOnly: true);

            twin.IsDestroyed.Should().BeFalse("the cleared item is kept");
            world.Writer.RemoveMobile(world.Mobiles.Get(DEAD));
            world.State.SnapshotDepth.Should().Be(0);
        }
    }
}
