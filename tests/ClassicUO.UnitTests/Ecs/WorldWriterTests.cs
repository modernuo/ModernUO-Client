using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 (ecs-transition-plan.md): the world-item operations of WorldWriter must leave the GameObject and the ECS exactly as
    // the handler code they replace did. Each case runs the same packet through a copy of the old item branch of
    // PacketHandlers.UpdateGameObject (or of DeleteObject's item branch) on one World and through the funnel on another, from
    // identical starting states, and compares every item property, the child lists in order, the mirror records and tags, the
    // wearer's equipment dirty tag (cleared before the packet), the mirror's contents and equipment indexes in order, the
    // active-items list in order and the mirror's counters.
    //
    // Not covered here: real tile placement (the test Worlds have no Map: building one needs the UO map files, so tile
    // insertion order and parking are covered by the client runs, -ecscheck and CUO_ECS_PARITY), and multis (type 2:
    // CheckGraphicChange loads the multi from the UO files).
    public class WorldWriterTests
    {
        private const uint MOB = 0x0000_3000, BAG = 0x4000_7000, ITEM = 0x4000_7001, CHILD = 0x4000_7002, GRANDCHILD = 0x4000_7003;

        internal struct Packet
        {
            public uint Serial;
            public ushort Graphic;
            public byte GraphicInc;
            public ushort Count;
            public ushort X, Y;
            public sbyte Z;
            public Direction Direction;
            public ushort Hue;
            public Flags Flags;
            public byte Type;
        }

        // the item branch of UpdateGameObject before T1. The UI and cursor side effects are left out (they write no world
        // state), except the callback point: the new-corpse SingleClick ran between the writes and the tile placement.
        internal static Item OldUpsert(World world, in Packet p, Action<Item> beforePlacement = null)
        {
            Entity obj = world.Get(p.Serial);
            Item item;
            ushort graphic = p.Graphic;
            ushort count = p.Count;

            if (obj == null || obj.IsDestroyed)
            {
                item = world.GetOrCreateItem(p.Serial);
            }
            else
            {
                item = (Item)obj;

                if (SerialHelper.IsValid(item.Container))
                {
                    world.RemoveItemFromContainer(item);
                }
            }

            if (graphic != 0x2006)
            {
                graphic += p.GraphicInc;
            }

            if (p.Type == 2)
            {
                item.IsMulti = true;
                item.WantUpdateMulti = (graphic & 0x3FFF) != item.Graphic || item.X != p.X || item.Y != p.Y || item.Z != p.Z || item.Hue != p.Hue;
                item.Graphic = (ushort)(graphic & 0x3FFF);
            }
            else
            {
                item.IsDamageable = p.Type == 3;
                item.IsMulti = false;
                item.Graphic = graphic;
            }

            item.X = p.X;
            item.Y = p.Y;
            item.Z = p.Z;
            item.LightID = (byte)p.Direction;

            if (graphic == 0x2006)
            {
                item.Layer = (Layer)p.Direction;
            }

            item.FixHue(p.Hue);

            if (count == 0)
            {
                count = 1;
            }

            item.Amount = count;
            item.Flags = p.Flags;
            item.Direction = p.Direction;
            item.CheckGraphicChange(item.AnimIndex);

            beforePlacement?.Invoke(item);

            if (item.OnGround)
            {
                item.SetInWorldTile(item.X, item.Y, item.Z);
            }

            return item;
        }

        private static Item NewUpsert(World world, in Packet p, out bool created, Action<Item> beforePlacement = null)
        {
            Item item = world.Writer.UpsertWorldItem(p.Serial, p.Graphic, p.GraphicInc, p.Count, p.X, p.Y, p.Z, p.Direction, p.Hue, p.Flags, p.Type, out created, out WorldWriter.Placement placement);

            // as the handler: no item, no callbacks and no placement
            if (item == null)
            {
                return null;
            }

            beforePlacement?.Invoke(item);
            world.Writer.PlaceWorldItem(placement);

            return item;
        }

        // DeleteObject's item branch before T1 (the gump refreshes left out)
        internal static void OldRemove(World world, uint serial)
        {
            Item item = (Item)world.Get(serial);

            if (item.IsMulti)
            {
                world.HouseManager.Remove(serial);
            }

            Entity cont = world.Get(item.Container);
            cont?.Remove(item);
            world.RemoveItem(serial, true);
        }

        // the starting states: a mobile wearing ITEM, a bag holding ITEM, ITEM on the ground, or no ITEM
        public enum Start { None, Ground, InBag, Worn, GroundCorpse }

        private static World Build(Start start)
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            m.Graphic = 0x190;
            m.SetInWorldTile(1000, 1000, 0);

            Item bag = world.GetOrCreateItem(BAG);
            bag.Graphic = 0xE75;
            bag.SetInWorldTile(1001, 1000, 0);

            if (start != Start.None)
            {
                Item it = world.GetOrCreateItem(ITEM);
                it.Graphic = start == Start.GroundCorpse ? (ushort)0x2006 : (ushort)0x1515;
                it.Hue = 0x21;
                it.Amount = 3;

                switch (start)
                {
                    case Start.Ground:
                    case Start.GroundCorpse:
                        it.SetInWorldTile(1002, 1003, 5);

                        break;

                    case Start.InBag:
                        // a sibling before ITEM, so the child-list order is visible
                        Item sibling = world.GetOrCreateItem(0x4000_7009);
                        sibling.Container = BAG;
                        bag.PushToBack(sibling);
                        world.Ecs.Sync(sibling);
                        it.Container = BAG;
                        it.X = 40;
                        it.Y = 50;
                        bag.PushToBack(it);
                        world.Ecs.Sync(it);

                        break;

                    case Start.Worn:
                        it.Container = MOB;
                        it.Layer = Layer.Shirt;
                        m.PushToBack(it);
                        world.Ecs.Sync(it);

                        break;
                }

                // contents inside ITEM, two levels, so a removal has a subtree to remove
                Item child = world.GetOrCreateItem(CHILD);
                child.Graphic = 0xF7A;
                child.Container = ITEM;
                it.PushToBack(child);
                world.Ecs.Sync(child);
                Item grandchild = world.GetOrCreateItem(GRANDCHILD);
                grandchild.Graphic = 0xF7B;
                grandchild.Container = CHILD;
                child.PushToBack(grandchild);
                world.Ecs.Sync(grandchild);
            }

            // the wearer's equipment tag starts clear: a missed unequip would leave it clear
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity me).Should().BeTrue();

            if (me.Tags.Has<EquipmentDirty>())
            {
                me.RemoveTag<EquipmentDirty>();
            }

            return world;
        }

        private static readonly Packet[] Packets =
        {
            new Packet { Serial = ITEM, Graphic = 0x0EED, Count = 0, X = 1100, Y = 1200, Z = 3, Direction = Direction.North, Hue = 0x0035, Flags = Flags.None },
            new Packet { Serial = ITEM, Graphic = 0x0EED, GraphicInc = 2, Count = 500, X = 1100, Y = 1200, Z = -4, Direction = Direction.West, Hue = 0x8BB9, Flags = Flags.Hidden },
            new Packet { Serial = ITEM, Graphic = 0x1515, Count = 1, X = 7, Y = 8, Z = 0, Direction = (Direction)3, Hue = 0, Flags = Flags.Movable, Type = 3 },
            // corpses: the direction is the corpse's layer; the running bit sets UsedLayer and is stripped
            new Packet { Serial = ITEM, Graphic = 0x2006, GraphicInc = 1, Count = 0x190, X = 1005, Y = 1005, Z = 0, Direction = Direction.Running | Direction.South, Hue = 0x0455 },
            new Packet { Serial = ITEM, Graphic = 0x2006, Count = 0x191, X = 1005, Y = 1006, Z = 1, Direction = Direction.East, Hue = 0 },
            // graphic + inc lands on the corpse graphic: the old branch tested the incremented local, so must the funnel
            new Packet { Serial = ITEM, Graphic = 0x2005, GraphicInc = 1, Count = 1, X = 1010, Y = 1010, Z = 0, Direction = Direction.Running | Direction.Left },
        };

        public static IEnumerable<object[]> Cases()
        {
            foreach (Start s in Enum.GetValues(typeof(Start)))
            {
                for (int i = 0; i < Packets.Length; i++)
                {
                    yield return new object[] { s, i };
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void UpsertWorldItem_Equals_The_Old_Path(Start start, int packet)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            Packet p = Packets[packet];

            Item a = OldUpsert(oldWorld, p);
            long writes = newWorld.Ecs.ItemMirrorWrites;
            Item b = NewUpsert(newWorld, p, out bool created);

            created.Should().Be(start == Start.None);
            b.Should().BeSameAs(newWorld.Items.Get(ITEM));
            (newWorld.Ecs.ItemMirrorWrites - writes).Should().Be(1, "the operation writes the item's mirror record once");
            AssertSameWorld(oldWorld, newWorld);

            // a second identical packet (a resend) on top
            OldUpsert(oldWorld, p);
            writes = newWorld.Ecs.ItemMirrorWrites;
            NewUpsert(newWorld, p, out created);
            created.Should().BeFalse();
            (newWorld.Ecs.ItemMirrorWrites - writes).Should().Be(1);
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void A_Sequence_Of_Packets_Stays_Identical()
        {
            World oldWorld = Build(Start.InBag), newWorld = Build(Start.InBag);

            foreach (Packet p in Packets)
            {
                OldUpsert(oldWorld, p);
                NewUpsert(newWorld, p, out _);
                AssertSameWorld(oldWorld, newWorld);
            }
        }

        [Theory]
        [InlineData(Start.None)]
        [InlineData(Start.InBag)]
        [InlineData(Start.GroundCorpse)]
        public void The_Callback_Sees_The_Same_State_As_Before_T1(Start start)
        {
            // 0x1A's new-corpse SingleClick sends a packet and plugin send hooks run synchronously: they must see the item
            // written (and its mirror record) but not yet placed, as before the funnel
            World oldWorld = Build(start), newWorld = Build(start);
            Packet p = Packets[3];
            string seenOld = null, seenNew = null;

            OldUpsert(oldWorld, p, it => seenOld = Snapshot(oldWorld, it));
            NewUpsert(newWorld, p, out _, it =>
            {
                newWorld.Writer.InScope.Should().BeFalse("callbacks run outside the operation");
                seenNew = Snapshot(newWorld, it);
            });

            seenNew.Should().Be(seenOld);
            AssertSameWorld(oldWorld, newWorld);
        }

        private static string Snapshot(World world, Item it)
        {
            world.Ecs.Mirror.TryGet(it.Serial, out MirrorRecord r);

            return $"{it.Graphic:X4} {it.Hue:X4} {it.X},{it.Y},{it.Z} {it.Direction} {it.Layer} {it.UsedLayer} {it.AnimIndex} {it.Container:X8} " +
                   $"pos={it.IsPositionChanged} drawn={it.AllowedToDraw} | {r.Graphic:X4} {r.X},{r.Y},{r.Z} {r.Facing} {r.Layer}";
        }

        [Fact]
        public void The_Operation_Leaves_No_Scope_And_The_Hooks_Sync_Again()
        {
            World world = Build(Start.InBag);
            world.Ecs.Mirror.TryGetEntity(ITEM, out EcsEntity before).Should().BeTrue();

            NewUpsert(world, Packets[0], out _);

            world.Ecs.Mirror.TryGetEntity(ITEM, out EcsEntity after).Should().BeTrue();
            after.Id.Should().Be(before.Id, "an existing entity is updated in place");
            world.Writer.InScope.Should().BeFalse();
            world.Ecs.IsFolding.Should().BeFalse();
            world.Items.Get(ITEM).Direction = Direction.East; // after the operation, the hooks sync again
            world.Ecs.Mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue();
            r.Facing.Should().Be((byte)Direction.East);
        }

        private static WorldWriter.Placement Upsert(World world, uint serial, ushort x = 1100) =>
            Upsert(world, serial, out _, x);

        private static WorldWriter.Placement Upsert(World world, uint serial, out Item item, ushort x = 1100)
        {
            item = world.Writer.UpsertWorldItem(serial, 0xEED, 0, 1, x, 1100, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);

            return p;
        }

        [Fact]
        public void Placement_Needs_An_Outstanding_Token_Of_This_Upsert()
        {
            World world = Build(Start.None);
            world.Writer.ThrowOnViolation = true;
            Item other = world.Items.Get(BAG);

            Action noUpsert = () => world.Writer.PlaceWorldItem(new WorldWriter.Placement(other, 1));
            noUpsert.Should().Throw<ArgumentException>();
            Action none = () => world.Writer.PlaceWorldItem(default);
            none.Should().Throw<ArgumentException>();

            WorldWriter.Placement p = Upsert(world, ITEM);
            Action wrong = () => world.Writer.PlaceWorldItem(new WorldWriter.Placement(other, p.Id));
            wrong.Should().Throw<ArgumentException>();
            world.Writer.OutstandingPlacements.Should().Be(1, "a rejected placement changes nothing");
            world.Writer.PlaceWorldItem(p);
            world.Writer.OutstandingPlacements.Should().Be(0);

            Action twice = () => world.Writer.PlaceWorldItem(p);
            twice.Should().Throw<ArgumentException>("one placement per upsert");
        }

        [Fact]
        public void A_Placement_Survives_Upserts_And_Placements_Of_Other_Items_In_Between()
        {
            // Upsert(A), a callback that upserts and places B, then Place(A) (Codex audit round 2, #4)
            World world = Build(Start.None);
            world.Writer.ThrowOnViolation = true;
            WorldWriter.Placement a = Upsert(world, ITEM, out Item ia);
            WorldWriter.Placement b = Upsert(world, 0x4000_7200, out Item ib);
            world.Writer.PlaceWorldItem(b);
            world.Writer.PlaceWorldItem(a);

            world.Writer.OutstandingPlacements.Should().Be(0);
            world.Writer.Violations.Should().Be(0);
            ia.Should().NotBeSameAs(ib);
        }

        [Fact]
        public void A_Placement_Is_Cancelled_When_Superseded_Removed_Or_Abandoned()
        {
            World world = Build(Start.None);
            world.Writer.ThrowOnViolation = true;

            // superseded: a later upsert of the same item issues its own
            WorldWriter.Placement first = Upsert(world, ITEM);
            WorldWriter.Placement second = Upsert(world, ITEM, 1101);
            Action stale = () => world.Writer.PlaceWorldItem(first);
            stale.Should().Throw<ArgumentException>();
            world.Writer.PlaceWorldItem(second);

            // removed
            WorldWriter.Placement removed = Upsert(world, ITEM, out Item it);
            world.Writer.RemoveWorldItem(it, out _);
            world.Writer.OutstandingPlacements.Should().Be(0);
            Action afterRemove = () => world.Writer.PlaceWorldItem(removed);
            afterRemove.Should().Throw<ArgumentException>();

            // abandoned (a callback threw, the handler never placed): dropped once MaxOutstanding newer ones were issued
            WorldWriter.Placement abandoned = Upsert(world, 0x4000_7300);

            for (uint i = 1; i <= WorldWriter.MaxOutstanding; i++)
            {
                Upsert(world, 0x4000_7300 + i);
            }

            world.Writer.OutstandingPlacements.Should().Be(WorldWriter.MaxOutstanding);
            Action late = () => world.Writer.PlaceWorldItem(abandoned);
            late.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Admission_Is_Checked_Before_Any_Creation_Or_Placement_Change(bool destroyed)
        {
            // Codex audit round 2, #3: an absent or destroyed serial while an operation (here: a fold) is open must not be
            // created, and the outstanding placement of another item must stay valid
            const uint SERIAL = 0x4000_7400;
            World world = Build(Start.None);
            world.Writer.ThrowOnViolation = true;
            Item dead = null;

            if (destroyed)
            {
                dead = world.GetOrCreateItem(SERIAL);
                dead.Destroy();
            }

            WorldWriter.Placement pending = Upsert(world, ITEM);
            int items = world.Items.Count;
            long writes = world.Ecs.ItemMirrorWrites;
            Item replacement = Item.Create(world, BAG);

            world.Ecs.BeginFold(replacement);
            Action nested = () => Upsert(world, SERIAL);
            nested.Should().Throw<InvalidOperationException>();
            world.Ecs.EndFold(replacement);

            world.Items.Count.Should().Be(items);
            world.Items.Get(SERIAL).Should().BeSameAs(dead, "nothing created or replaced");
            world.Ecs.ItemMirrorWrites.Should().Be(writes);
            world.Writer.OutstandingPlacements.Should().Be(1);
            world.Writer.PlaceWorldItem(pending);
            world.Writer.OutstandingPlacements.Should().Be(0);
        }

        [Fact]
        public void An_Operation_Inside_An_Operation_Is_Rejected_Before_Any_Change()
        {
            // a real nesting: an upsert started from inside another upsert's commit
            World world = Build(Start.None);
            world.Writer.ThrowOnViolation = true;
            WorldWriter.Placement pending = Upsert(world, 0x4000_7500);
            Exception seen = null;
            world.Ecs.CommitObserver = (_, _) =>
            {
                world.Ecs.CommitObserver = null;

                try
                {
                    Upsert(world, 0x4000_7501);
                }
                catch (InvalidOperationException e)
                {
                    seen = e;
                }
            };

            Upsert(world, ITEM);

            seen.Should().NotBeNull();
            world.Items.Get(0x4000_7501).Should().BeNull();
            world.Writer.OutstandingPlacements.Should().Be(2, "the earlier placement and the outer upsert's");
            world.Writer.PlaceWorldItem(pending);
        }

        // ---- the release build's path after a violation (ThrowOnViolation false): an error log; a nesting is rejected
        // (T1 round 4), a bad placement or removal takes the pre-T1 code

        [Fact]
        public void Release_A_Nested_Upsert_Is_Rejected()
        {
            World untouched = Build(Start.InBag), newWorld = Build(Start.InBag);
            newWorld.Writer.ThrowOnViolation = false;
            Item replacement = Item.Create(newWorld, BAG);
            Packet p = Packets[3];

            newWorld.Ecs.BeginFold(replacement); // an operation is open
            NewUpsert(newWorld, p, out bool created).Should().BeNull("a nested upsert is rejected");
            newWorld.Ecs.EndFold(replacement);

            created.Should().BeFalse();
            newWorld.Writer.Violations.Should().Be(1);
            newWorld.Writer.OutstandingPlacements.Should().Be(0);
            AssertSameWorld(untouched, newWorld);
        }

        [Fact]
        public void Release_A_Bad_Placement_Or_Removal_Takes_The_Pre_T1_Path()
        {
            World oldWorld = Build(Start.InBag), newWorld = Build(Start.InBag), other = Build(Start.InBag);
            newWorld.Writer.ThrowOnViolation = false;
            Packet p0 = Packets[0];

            OldUpsert(oldWorld, p0);
            Item it = newWorld.Writer.UpsertWorldItem(ITEM, p0.Graphic, 0, p0.Count, p0.X, p0.Y, p0.Z, p0.Direction, p0.Hue, p0.Flags, 0, out _, out WorldWriter.Placement p);
            newWorld.Writer.PlaceWorldItem(new WorldWriter.Placement(it, p.Id + 1)); // not outstanding: the old placement
            AssertSameWorld(oldWorld, newWorld);

            // a foreign instance: the old handler removed by serial from its own World
            OldRemove(oldWorld, ITEM);
            newWorld.Writer.RemoveWorldItem(other.Items.Get(ITEM), out Entity parent);
            parent.Should().BeNull();
            newWorld.Items.Get(ITEM).Should().BeNull();
            AssertSameWorld(oldWorld, newWorld);

            // a live mobile's serial: nothing
            newWorld.Writer.UpsertWorldItem(MOB, 0xEED, 0, 1, 1, 1, 0, Direction.North, 0, Flags.None, 3, out _, out _).Should().BeNull();
            newWorld.Writer.Violations.Should().Be(3);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        // ---- T1 round 4 (Codex audit round 4, #1 and #2): production code cannot re-enter the writer during an operation
        // (see WorldWriter.Admitted), so a nested operation is forbidden, not recovered from: it throws in CUO_DEVTOOLS
        // builds, and in a release build it is rejected and changes nothing. Codex's scenarios, from inside an outer
        // operation's commit: a removal of its item (was: a ghost mirror record), a world upsert of its item (was: stale
        // container links), a container placement and a placement. The outer operation completes as without the nested call.

        public enum Outer { Upsert, PlaceInContainer }

        public enum Nested { Upsert, Remove, PlaceInContainer, Place }

        public static IEnumerable<object[]> NestingCases()
        {
            foreach (bool throws in new[] { true, false })
            {
                foreach (Outer o in Enum.GetValues(typeof(Outer)))
                {
                    foreach (Nested n in Enum.GetValues(typeof(Nested)))
                    {
                        yield return new object[] { throws, o, n };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(NestingCases))]
        public void A_Nested_Operation_During_A_Commit_Is_A_Violation_And_Changes_Nothing(bool throws, Outer outer, Nested nested)
        {
            const uint OTHER = 0x4000_7600;
            World oldWorld = Build(Start.InBag), newWorld = Build(Start.InBag);
            newWorld.Writer.ThrowOnViolation = throws;

            // an outstanding placement of another item, for the nested Place (placed after the outer operation)
            OldUpsert(oldWorld, new Packet { Serial = OTHER, Graphic = 0xEED, Count = 1, X = 1050, Y = 1050 });
            WorldWriter.Placement pending = newWorld.Writer.UpsertWorldItem(OTHER, 0xEED, 0, 1, 1050, 1050, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement issued) == null ? default : issued;
            Exception seen = null;
            object nestedResult = "not run";
            newWorld.Ecs.CommitObserver = (_, _) =>
            {
                newWorld.Ecs.CommitObserver = null;

                try
                {
                    switch (nested)
                    {
                        case Nested.Upsert:
                            nestedResult = NewUpsert(newWorld, Packets[2], out _);

                            break;

                        case Nested.Remove:
                            newWorld.Writer.RemoveWorldItem(newWorld.Items.Get(ITEM), out Entity parent);
                            nestedResult = parent;

                            break;

                        case Nested.PlaceInContainer:
                            nestedResult = newWorld.Writer.PlaceInContainer(ITEM, 0x1516, 2, 9, 9, 0, MOB, out Entity container);
                            container.Should().BeNull();

                            break;

                        case Nested.Place:
                            newWorld.Writer.PlaceWorldItem(pending);
                            nestedResult = null;

                            break;
                    }
                }
                catch (InvalidOperationException e)
                {
                    seen = e;
                }
            };

            Item it;

            if (outer == Outer.Upsert)
            {
                OldUpsert(oldWorld, Packets[1]);
                it = NewUpsert(newWorld, Packets[1], out _);
            }
            else
            {
                OldAddToContainer(oldWorld, ITEM, 0x1517, 4, 11, 12, 0x33, BAG);
                it = newWorld.Writer.PlaceInContainer(ITEM, 0x1517, 4, 11, 12, 0x33, BAG, out _);
            }

            newWorld.Ecs.CommitObserver.Should().BeNull("the observer ran: the nested call happened during the commit");
            newWorld.Writer.Violations.Should().Be(1);

            if (throws)
            {
                seen.Should().NotBeNull("CUO_DEVTOOLS: a nested operation throws");
            }
            else
            {
                seen.Should().BeNull();
                nestedResult.Should().BeNull("release: the nested operation is rejected and returns nothing");
            }

            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
            it.Should().BeSameAs(newWorld.Items.Get(ITEM));

            // the nested placement was rejected, so the earlier placement is still outstanding: it places as the old path did
            newWorld.Writer.PlaceWorldItem(pending);
            newWorld.Writer.OutstandingPlacements.Should().Be(0);
            AssertSameWorld(oldWorld, newWorld);

            // no ghost record and no stale link: the mirror holds what the cache holds, and the bag lists the item iff it holds it
            newWorld.Ecs.Mirror.TryGet(ITEM, out MirrorRecord mirrored).Should().BeTrue();
            newWorld.Ecs.Sync(it);
            newWorld.Ecs.Mirror.TryGet(ITEM, out MirrorRecord fromCache);
            mirrored.Should().BeEquivalentTo(fromCache, "the mirror holds what the cache holds");
            ChildSerials(newWorld.Items.Get(BAG)).Contains(ITEM).Should().Be(it.Container == BAG);
        }

        [Theory]
        [InlineData(Start.Ground, 0)]
        [InlineData(Start.InBag, 3)]
        [InlineData(Start.Worn, 1)]
        [InlineData(Start.GroundCorpse, 4)]
        public void Release_Any_Operation_Inside_The_Fold_Of_Its_Own_Item_Is_Rejected(Start start, int packet)
        {
            World untouched = BuildWithCorpse(start), newWorld = BuildWithCorpse(start);
            newWorld.Writer.ThrowOnViolation = false;
            Item it = newWorld.Items.Get(ITEM);
            Packet p = Packets[packet];

            newWorld.Ecs.BeginFold(it); // an operation on this very instance is open
            NewUpsert(newWorld, p, out bool created).Should().BeNull();
            newWorld.Writer.PlaceInContainer(ITEM, 0x1516, 2, 3, 4, 0x21, BAG, out Entity container).Should().BeNull();
            container.Should().BeNull();
            newWorld.Writer.RemoveWorldItem(it, out Entity parent);
            parent.Should().BeNull();
            newWorld.Ecs.EndFold(it);

            created.Should().BeFalse();
            newWorld.Writer.Violations.Should().Be(3);
            newWorld.Ecs.IsFolding.Should().BeFalse();
            it.IsDestroyed.Should().BeFalse();
            AssertSameWorld(untouched, newWorld);
            AssertSameCorpseBox(untouched, newWorld);
        }

        // ---- Codex audit round 2, #1: results are computed, never read back after the operation's own unlink or write

        [Theory]
        [InlineData(Start.InBag, BAG)]
        [InlineData(Start.Worn, MOB)]
        public void The_Container_Is_Computed_Not_Read_Back(Start start, uint oldParent)
        {
            // At the commit the getters still return the previous values, as they will when T4 moves them to the ECS (the
            // unlink no longer writes Container). Reading the Container back there would give the old parent.
            World world = Build(start);
            ItemState committed = default;
            uint getterAtCommit = 0;
            world.Ecs.CommitObserver = (item, v) =>
            {
                committed = v;
                getterAtCommit = item.Container;
            };

            NewUpsert(world, Packets[0], out _);

            getterAtCommit.Should().Be(oldParent, "the cache still holds the old parent at the commit");
            committed.Container.Should().Be(0xFFFF_FFFF);
            Item it = world.Items.Get(ITEM);
            it.Container.Should().Be(0xFFFF_FFFF);
            it.OnGround.Should().BeTrue();
            world.Ecs.Mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue();
            r.OnGround.Should().BeTrue();
            ChildSerials(world.Get(oldParent)).Should().NotContain(ITEM);
        }

        [Fact]
        public void The_Commit_Takes_No_Value_From_The_Getters()
        {
            // every committed field equals what the packet and the pure helpers give, whatever the getters held
            World world = Build(Start.Worn);
            ItemState committed = default;
            world.Ecs.CommitObserver = (_, v) => committed = v;
            Packet p = Packets[3]; // a corpse: direction stripped, layer from the direction

            NewUpsert(world, p, out _);

            Direction dir = p.Direction;
            Item.CorpseState(ref dir, out _, out Layer layer);
            committed.Graphic.Should().Be(0x2006);
            committed.Hue.Should().Be(Entity.FixedHue(p.Hue));
            committed.Amount.Should().Be(p.Count);
            committed.X.Should().Be(p.X);
            committed.Y.Should().Be(p.Y);
            committed.Z.Should().Be(p.Z);
            committed.Direction.Should().Be(dir);
            committed.Layer.Should().Be(layer);
            committed.Flags.Should().Be(p.Flags);
            committed.Container.Should().Be(0xFFFF_FFFF);
            committed.IsMulti.Should().BeFalse();
        }

        [Fact]
        public void A_Live_Mobile_Serial_Is_Rejected_Before_Any_Change()
        {
            World world = Build(Start.None);
            long writes = world.Ecs.ItemMirrorWrites;
            int items = world.Items.Count;

            world.Writer.ThrowOnViolation = true;
            Action upsert = () => world.Writer.UpsertWorldItem(MOB, 0xEED, 0, 1, 1, 1, 0, Direction.North, 0, Flags.None, 3, out _, out _);
            upsert.Should().Throw<ArgumentException>();

            world.Items.Count.Should().Be(items);
            world.Ecs.ItemMirrorWrites.Should().Be(writes);
            world.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void A_Mobile_Valued_Serial_With_Type_3_Is_An_Item_As_Before()
        {
            // the handler routes a mobile-valued serial with type 3 to the item path (no live mobile holds it); World.Get
            // finds the item among the items on the second packet
            const uint MSERIAL = 0x0000_3333;
            World oldWorld = Build(Start.None), newWorld = Build(Start.None);
            var p = new Packet { Serial = MSERIAL, Graphic = 0x1515, Count = 1, X = 1050, Y = 1050, Direction = Direction.North, Type = 3 };

            OldUpsert(oldWorld, p);
            NewUpsert(newWorld, p, out bool created);
            created.Should().BeTrue();
            p.X = 1051;
            OldUpsert(oldWorld, p);
            NewUpsert(newWorld, p, out created);
            created.Should().BeFalse();

            AssertSameItem(oldWorld.Items.Get(MSERIAL), newWorld.Items.Get(MSERIAL));
            AssertSameWorld(oldWorld, newWorld);
        }

        [Theory]
        [InlineData(Start.Ground)]
        [InlineData(Start.InBag)]
        [InlineData(Start.Worn)]
        [InlineData(Start.GroundCorpse)]
        public void RemoveWorldItem_Equals_The_Old_Path(Start start)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            Entity expectedParent = newWorld.Get(newWorld.Items.Get(ITEM).Container);

            OldRemove(oldWorld, ITEM);
            newWorld.Writer.RemoveWorldItem(newWorld.Items.Get(ITEM), out Entity parent);

            parent.Should().BeSameAs(expectedParent);
            newWorld.Items.Get(ITEM).Should().BeNull();
            newWorld.Items.Get(CHILD).Should().BeNull("the contents go with it");
            newWorld.Writer.InScope.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void RemoveWorldItem_Rejects_Foreign_And_Stale_Items_Before_Any_Change()
        {
            World world = Build(Start.InBag), other = Build(Start.InBag);
            Item live = world.Items.Get(ITEM);
            world.Writer.ThrowOnViolation = true;

            // an instance of another World
            Action foreign = () => world.Writer.RemoveWorldItem(other.Items.Get(ITEM), out _);
            foreign.Should().Throw<ArgumentException>();

            // a stale instance whose serial World now holds with a replacement
            Item stale = Item.Create(world, ITEM);
            Action staleRemove = () => world.Writer.RemoveWorldItem(stale, out _);
            staleRemove.Should().Throw<ArgumentException>();

            world.Items.Get(ITEM).Should().BeSameAs(live, "the replacement (here: the live item) stays");
            live.IsDestroyed.Should().BeFalse();
            world.Items.Get(BAG).Items.Should().NotBeNull();
            ChildSerials(world.Items.Get(BAG)).Should().Contain(ITEM);
            world.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void The_Fold_Is_Per_Instance_And_Not_Reentrant()
        {
            World world = Build(Start.InBag);
            Item it = world.Items.Get(ITEM);
            Item replacement = Item.Create(world, ITEM);

            world.Ecs.BeginFold(replacement);
            Action nested = () => world.Ecs.BeginFold(it);
            nested.Should().Throw<InvalidOperationException>();

            // the held instance with the same serial is not folded: its Sync writes the mirror
            long writes = world.Ecs.ItemMirrorWrites;
            world.Ecs.Sync(it);
            world.Ecs.ItemMirrorWrites.Should().Be(writes + 1);

            world.Ecs.EndFold(it); // another instance's end: no effect
            world.Ecs.IsFolding.Should().BeTrue();
            world.Ecs.EndFold(replacement);
            world.Ecs.IsFolding.Should().BeFalse();

            // a fold left open makes the next operation throw before it changes anything
            world.Writer.ThrowOnViolation = true;
            world.Ecs.BeginFold(replacement);
            Action op = () => world.Writer.UpsertWorldItem(ITEM, 0xEED, 0, 1, 1, 1, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            op.Should().Throw<InvalidOperationException>();
            world.Writer.InScope.Should().BeFalse();
            it.Container.Should().Be(BAG, "nothing was changed");
            world.Ecs.EndFold(replacement);
        }

        // ---- the containers group: AddItemToContainer (0x25, 0x3C's items, 0x27's deny path)

        private const uint CORPSEBOX = 0x4000_7020;

        // AddItemToContainer before T1, its world-state part (the ItemHold clear and the gump refreshes left out)
        internal static Item OldAddToContainer(World world, uint serial, ushort graphic, ushort amount, ushort x, ushort y, ushort hue, uint containerSerial)
        {
            Entity container = world.Get(containerSerial);

            if (container == null)
            {
                return null;
            }

            Item item = world.Items.Get(serial);

            // a mobile holding the serial goes, with its equipment (Codex audit round 3, #3: the oracle had left this out)
            if (SerialHelper.IsMobile(serial))
            {
                world.RemoveMobile(serial, true);
            }

            if (item != null && item.Container != containerSerial && (container.Graphic != 0x2006 || item.Layer == Layer.Invalid))
            {
                world.RemoveItem(item, true);
            }

            item = world.GetOrCreateItem(serial);
            item.Graphic = graphic;
            item.CheckGraphicChange();
            item.Amount = amount;
            item.FixHue(hue);
            item.X = x;
            item.Y = y;
            item.Z = 0;

            if (item.Container != containerSerial)
            {
                world.RemoveItemFromContainer(item);
                item.Container = containerSerial;
            }

            container.PushToBack(item);
            world.Ecs.Sync(item);

            return item;
        }

        // a corpse lying next to the mobile, holding one item, so the corpse equipment exception has a target
        private static World BuildWithCorpse(Start start)
        {
            World world = Build(start);
            Item box = world.GetOrCreateItem(CORPSEBOX);
            box.Graphic = 0x2006;
            box.SetInWorldTile(1003, 1000, 0);
            Item inside = world.GetOrCreateItem(0x4000_7021);
            inside.Container = CORPSEBOX;
            box.PushToBack(inside);
            world.Ecs.Sync(inside);

            return world;
        }

        internal struct ContainerPacket
        {
            public ushort Graphic, Amount, X, Y, Hue;
            public uint Container;
        }

        private static readonly ContainerPacket[] ContainerPackets =
        {
            new ContainerPacket { Graphic = 0x0EED, Amount = 1, X = 40, Y = 60, Hue = 0x0035, Container = BAG },
            new ContainerPacket { Graphic = 0x1515, Amount = 3, X = 0, Y = 0, Hue = 0x8BB9, Container = MOB },
            new ContainerPacket { Graphic = 0x1515, Amount = 1, X = 10, Y = 20, Hue = 0, Container = CORPSEBOX },
            // a corpse graphic: CheckGraphicChange's corpse branch on the old direction
            new ContainerPacket { Graphic = 0x2006, Amount = 0x190, X = 5, Y = 6, Hue = 0x455, Container = BAG },
            // a container nobody holds: nothing happens
            new ContainerPacket { Graphic = 0x0EED, Amount = 1, X = 1, Y = 1, Hue = 0, Container = 0x4000_7FFF },
        };

        public static IEnumerable<object[]> ContainerCases()
        {
            foreach (Start s in Enum.GetValues(typeof(Start)))
            {
                for (int i = 0; i < ContainerPackets.Length; i++)
                {
                    yield return new object[] { s, i };
                }
            }
        }

        private static void AssertSameCorpseBox(World a, World b)
        {
            ChildSerials(b.Items.Get(CORPSEBOX)).Should().Equal(ChildSerials(a.Items.Get(CORPSEBOX)), "the corpse's child list, in order");
            Serials(b.Ecs.Mirror.GetContents(CORPSEBOX)).Should().Equal(Serials(a.Ecs.Mirror.GetContents(CORPSEBOX)), "the corpse's contents index, in order");
        }

        [Theory]
        [MemberData(nameof(ContainerCases))]
        public void PlaceInContainer_Equals_The_Old_Path(Start start, int packet)
        {
            World oldWorld = BuildWithCorpse(start), newWorld = BuildWithCorpse(start);
            ContainerPacket p = ContainerPackets[packet];

            // a worn item's direction with the running bit, so a corpse graphic exercises UsedLayer
            foreach (World w in new[] { oldWorld, newWorld })
            {
                Item it = w.Items.Get(ITEM);

                if (it != null)
                {
                    it.Direction = Direction.Running | Direction.West;
                }
            }

            Item a = OldAddToContainer(oldWorld, ITEM, p.Graphic, p.Amount, p.X, p.Y, p.Hue, p.Container);
            Item b = newWorld.Writer.PlaceInContainer(ITEM, p.Graphic, p.Amount, p.X, p.Y, p.Hue, p.Container, out Entity container);

            (b == null).Should().Be(a == null);
            container.Should().BeSameAs(newWorld.Get(p.Container));

            if (b != null)
            {
                b.Should().BeSameAs(newWorld.Items.Get(ITEM));
            }

            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);
            AssertSameCorpseBox(oldWorld, newWorld);

            // the same packet again (a resend: a re-push into the same container)
            OldAddToContainer(oldWorld, ITEM, p.Graphic, p.Amount, p.X, p.Y, p.Hue, p.Container);
            long writes = newWorld.Ecs.ItemMirrorWrites;
            newWorld.Writer.PlaceInContainer(ITEM, p.Graphic, p.Amount, p.X, p.Y, p.Hue, p.Container, out _);

            if (b != null)
            {
                (newWorld.Ecs.ItemMirrorWrites - writes).Should().Be(1, "the operation writes the item's mirror record once");
            }

            AssertSameWorld(oldWorld, newWorld);
            AssertSameCorpseBox(oldWorld, newWorld);
        }

        [Fact]
        public void PlaceInContainer_Re_Pushes_To_The_End_Of_The_Child_List()
        {
            // the bag holds a sibling, then ITEM; a second item after ITEM, then ITEM again: ITEM moves to the end
            World oldWorld = Build(Start.InBag), newWorld = Build(Start.InBag);
            const uint OTHER = 0x4000_7030;

            OldAddToContainer(oldWorld, OTHER, 0xEED, 1, 1, 1, 0, BAG);
            newWorld.Writer.PlaceInContainer(OTHER, 0xEED, 1, 1, 1, 0, BAG, out _);
            OldAddToContainer(oldWorld, ITEM, 0xEED, 2, 2, 2, 0, BAG);
            newWorld.Writer.PlaceInContainer(ITEM, 0xEED, 2, 2, 2, 0, BAG, out _);

            ChildSerials(newWorld.Items.Get(BAG)).Should().EndWith(new[] { OTHER, ITEM });
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void PlaceInContainer_A_Sequence_Of_Packets_Stays_Identical()
        {
            World oldWorld = BuildWithCorpse(Start.Worn), newWorld = BuildWithCorpse(Start.Worn);

            foreach (ContainerPacket p in ContainerPackets)
            {
                OldAddToContainer(oldWorld, ITEM, p.Graphic, p.Amount, p.X, p.Y, p.Hue, p.Container);
                newWorld.Writer.PlaceInContainer(ITEM, p.Graphic, p.Amount, p.X, p.Y, p.Hue, p.Container, out _);
                AssertSameWorld(oldWorld, newWorld);
                AssertSameCorpseBox(oldWorld, newWorld);
            }

            // and a 0x1A ground packet in between, then back into the bag
            OldUpsert(oldWorld, Packets[0]);
            NewUpsert(newWorld, Packets[0], out _);
            OldAddToContainer(oldWorld, ITEM, 0xEED, 1, 7, 7, 0, BAG);
            newWorld.Writer.PlaceInContainer(ITEM, 0xEED, 1, 7, 7, 0, BAG, out _);
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void PlaceInContainer_Computes_The_Container_And_Cancels_A_Placement()
        {
            World world = Build(Start.None);
            world.Writer.ThrowOnViolation = true;
            WorldWriter.Placement pending = Upsert(world, ITEM);
            ItemState committed = default;
            uint getterAtCommit = 0;
            world.Ecs.CommitObserver = (item, v) =>
            {
                committed = v;
                getterAtCommit = item.Container;
            };

            world.Writer.PlaceInContainer(ITEM, 0xEED, 1, 1, 1, 0, BAG, out _);

            getterAtCommit.Should().Be(0xFFFF_FFFF, "the getter still says the ground at the commit");
            committed.Container.Should().Be(BAG);
            world.Items.Get(ITEM).Container.Should().Be(BAG);
            world.Writer.OutstandingPlacements.Should().Be(0, "the ground placement of the earlier upsert is cancelled");
            Action late = () => world.Writer.PlaceWorldItem(pending);
            late.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(BAG)]
        [InlineData(CORPSEBOX)]
        public void PlaceInContainer_Of_A_Live_Mobile_Serial_Equals_The_Old_Path(uint containerSerial)
        {
            // Codex audit round 3, #3: a live mobile holding the incoming serial is removed with its equipment and the
            // equipment's contents. Codex audit round 4, #5: the replacement item (serial MOB) is compared directly, cache and
            // mirror (AssertSameWorld now visits every item either World holds), and the removed instances are destroyed.
            World oldWorld = BuildWithCorpse(Start.Worn), newWorld = BuildWithCorpse(Start.Worn);
            var removedOld = new Entity[] { oldWorld.Mobiles.Get(MOB), oldWorld.Items.Get(ITEM), oldWorld.Items.Get(CHILD), oldWorld.Items.Get(GRANDCHILD) };
            var removedNew = new Entity[] { newWorld.Mobiles.Get(MOB), newWorld.Items.Get(ITEM), newWorld.Items.Get(CHILD), newWorld.Items.Get(GRANDCHILD) };

            Item a = OldAddToContainer(oldWorld, MOB, 0x1515, 1, 2, 3, 0x21, containerSerial);
            Item b = newWorld.Writer.PlaceInContainer(MOB, 0x1515, 1, 2, 3, 0x21, containerSerial, out _);

            oldWorld.Mobiles.Get(MOB).Should().BeNull("the old handler removed the mobile");
            oldWorld.Items.Get(ITEM).Should().BeNull("with its equipment");
            removedOld.Should().OnlyContain(e => e.IsDestroyed, "the old path destroyed the mobile, its equipment and contents");
            removedNew.Should().OnlyContain(e => e.IsDestroyed, "the operation destroys the mobile, its equipment and contents");

            b.Should().NotBeNull().And.BeSameAs(newWorld.Items.Get(MOB));
            a.Should().BeSameAs(oldWorld.Items.Get(MOB));
            AssertSameItem(a, b);
            AssertSameWorld(oldWorld, newWorld);
            AssertSameCorpseBox(oldWorld, newWorld);

            // negative fixture: a replacement whose cache differs after its commit (the mirror record still equal) is found
            b.Hue ^= 1;
            Action corrupted = () => AssertSameWorld(oldWorld, newWorld);
            corrupted.Should().Throw<Exception>("the replacement item's cache is compared");
            b.Hue ^= 1;
        }

        private static List<uint> ChildSerials(Entity e)
        {
            var list = new List<uint>();

            for (LinkedObject i = e?.Items; i != null; i = i.Next)
            {
                list.Add(((Entity)i).Serial);
            }

            return list;
        }

        private static List<uint> Serials(Friflo.Engine.ECS.Entities entities) => entities.Select(x => x.GetComponent<Serial>().Value).ToList();

        private static void AssertSameWorld(World a, World b, bool equipmentIndexOrder = true)
        {
            a.Items.Count.Should().Be(b.Items.Count);
            b.Mobiles.Count.Should().Be(a.Mobiles.Count);
            (b.Mobiles.Get(MOB) == null).Should().Be(a.Mobiles.Get(MOB) == null, "the mobile exists in both or neither");
            b.ActiveItems.Select(i => i.Serial).Should().Equal(a.ActiveItems.Select(i => i.Serial), "the active-items list, in order");

            // every item either World holds (Codex audit round 4, #5: a replacement item under another serial, e.g. MOB, too)
            List<uint> serials = a.Items.Keys.Union(b.Items.Keys).Union(new[] { ITEM, CHILD, GRANDCHILD, BAG }).OrderBy(s => s).ToList();

            foreach (uint serial in serials)
            {
                Item x = a.Items.Get(serial), y = b.Items.Get(serial);
                (x == null).Should().Be(y == null, $"0x{serial:X8} exists in both or neither");

                if (x != null)
                {
                    AssertSameItem(x, y);
                }
            }

            ChildSerials(b.Mobiles.Get(MOB)).Should().Equal(ChildSerials(a.Mobiles.Get(MOB)), "the wearer's child list, in order");

            WorldMirror ma = a.Ecs.Mirror, mb = b.Ecs.Mirror;
            mb.Count.Should().Be(ma.Count);
            mb.ItemCount.Should().Be(ma.ItemCount);
            mb.MobileCount.Should().Be(ma.MobileCount);
            mb.Epoch.Should().Be(ma.Epoch);

            foreach (uint serial in serials.Union(new[] { MOB }))
            {
                bool ha = ma.TryGet(serial, out MirrorRecord ra), hb = mb.TryGet(serial, out MirrorRecord rb);
                hb.Should().Be(ha, $"0x{serial:X8} mirrored in both or neither");
                rb.Should().BeEquivalentTo(ra, $"the mirror record of 0x{serial:X8}");

                if (ha)
                {
                    ma.TryGetEntity(serial, out EcsEntity ea);
                    mb.TryGetEntity(serial, out EcsEntity eb);
                    eb.Tags.Has<EquipmentDirty>().Should().Be(ea.Tags.Has<EquipmentDirty>(), $"0x{serial:X8} equipment dirty");
                    eb.Tags.Has<OnGround>().Should().Be(ea.Tags.Has<OnGround>());
                    eb.Tags.Has<MultiTag>().Should().Be(ea.Tags.Has<MultiTag>());
                    eb.Id.Should().Be(ea.Id, "the same entities are created in the same order");
                }
            }

            foreach (uint parent in new[] { BAG, ITEM, CHILD })
            {
                Serials(mb.GetContents(parent)).Should().Equal(Serials(ma.GetContents(parent)), $"the contents index of 0x{parent:X8}, in order");
            }

            // T3a: ReturnHeldItem's paperdoll branch commits once (the equipment-order stamp), so it leaves the index's own order
            // (which nothing reads any more) where the old path's two Syncs moved it: its members are compared in any order
            if (equipmentIndexOrder)
            {
                Serials(mb.GetEquipment(MOB)).Should().Equal(Serials(ma.GetEquipment(MOB)), "the equipment index, in order");
            }
            else
            {
                Serials(mb.GetEquipment(MOB)).Should().BeEquivalentTo(Serials(ma.GetEquipment(MOB)), "the equipment index's members");
            }
        }

        private static void AssertSameItem(Item x, Item y)
        {
            y.Graphic.Should().Be(x.Graphic);
            y.Hue.Should().Be(x.Hue);
            (y.X, y.Y, y.Z).Should().Be((x.X, x.Y, x.Z));
            y.Offset.Should().Be(x.Offset);
            y.Direction.Should().Be(x.Direction);
            y.Flags.Should().Be(x.Flags);
            y.Amount.Should().Be(x.Amount);
            y.Container.Should().Be(x.Container);
            y.Layer.Should().Be(x.Layer);
            y.LightID.Should().Be(x.LightID);
            y.IsMulti.Should().Be(x.IsMulti);
            y.IsDamageable.Should().Be(x.IsDamageable);
            y.UsedLayer.Should().Be(x.UsedLayer);
            y.AnimIndex.Should().Be(x.AnimIndex);
            y.AllowedToDraw.Should().Be(x.AllowedToDraw);
            y.WantUpdateMulti.Should().Be(x.WantUpdateMulti);
            y.IsDestroyed.Should().Be(x.IsDestroyed);
            y.InUpdateList.Should().Be(x.InUpdateList);
            y.IsPositionChanged.Should().Be(x.IsPositionChanged);
            ChildSerials(y).Should().Equal(ChildSerials(x), "the child list, in order");
            (y.Next == null).Should().Be(x.Next == null);
            (y.Previous == null).Should().Be(x.Previous == null);
        }

        // ---- T1 funnel-2: the bulk clear (ClearContainerAndRemoveItems: 0x3C before its first item, 0x24)

        // ClearContainerAndRemoveItems before T1
        internal static void OldClear(World world, Entity container, bool removeUnequippedOnly)
        {
            if (container == null || container.IsEmpty)
            {
                return;
            }

            LinkedObject first = container.Items;
            LinkedObject newFirst = null;

            while (first != null)
            {
                LinkedObject next = first.Next;
                Item it = (Item)first;

                if (removeUnequippedOnly && it.Layer != 0)
                {
                    newFirst ??= first;
                }
                else
                {
                    world.RemoveItem(it, true);
                }

                first = next;
            }

            container.Items = removeUnequippedOnly ? newFirst : null;
        }

        private const uint KEPT1 = 0x4000_7022, KEPT2 = 0x4000_7023;

        // the corpse holds: an equipped item, the plain one BuildWithCorpse puts there, a second equipped item, and ITEM's
        // world as Build made it (so the corpse case also removes nothing outside the corpse)
        private static World BuildClear(Start start)
        {
            World world = BuildWithCorpse(start);
            Item box = world.Items.Get(CORPSEBOX);
            Item plain = world.Items.Get(0x4000_7021);
            plain.Graphic = 0x1F03;
            world.Ecs.Sync(plain);

            foreach ((uint serial, bool first) in new[] { (KEPT1, true), (KEPT2, false) })
            {
                Item k = world.GetOrCreateItem(serial);
                k.Graphic = 0x1515;
                k.Layer = Layer.Shirt;
                k.Container = CORPSEBOX;

                if (first)
                {
                    box.Remove(plain);
                    box.PushToBack(k);
                    box.PushToBack(plain);
                }
                else
                {
                    box.PushToBack(k);
                }

                world.Ecs.Sync(k);
            }

            return world;
        }

        public static IEnumerable<object[]> ClearCases()
        {
            foreach (Start s in Enum.GetValues(typeof(Start)))
            {
                foreach (uint c in new[] { BAG, MOB, CORPSEBOX, ITEM, 0x4000_7FFFu })
                {
                    foreach (bool keep in new[] { false, true })
                    {
                        yield return new object[] { s, c, keep };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(ClearCases))]
        public void ClearContainer_Equals_The_Old_Path(Start start, uint containerSerial, bool keepEquipped)
        {
            World oldWorld = BuildClear(start), newWorld = BuildClear(start);
            Entity before = newWorld.Get(containerSerial);
            List<uint> kept = before == null ? new List<uint>() : ChildSerials(before).Where(s => keepEquipped && newWorld.Items.Get(s).Layer != 0).ToList();

            OldClear(oldWorld, oldWorld.Get(containerSerial), keepEquipped);
            newWorld.Writer.ClearContainer(containerSerial, keepEquipped, itemOnly: false);

            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();

            if (before != null)
            {
                ChildSerials(before).Should().Equal(kept, "only the kept equipment stays, in order");
                ChildSerials(before).Should().Equal(ChildSerials(oldWorld.Get(containerSerial)));
            }

            AssertSameWorld(oldWorld, newWorld);
            AssertSameCorpseBox(oldWorld, newWorld);
        }

        // an upserted item (with an outstanding placement) moved under parent as a test shortcut: World.Get finds it there
        private static WorldWriter.Placement PendingIn(World world, uint serial, uint parent)
        {
            Item it = world.Writer.UpsertWorldItem(serial, 0xEED, 0, 1, 1010, 1010, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            it.Container = parent;
            world.Get(parent).PushToBack(it);

            return p;
        }

        [Fact]
        public void ClearContainer_Cancels_The_Removed_Items_Placements()
        {
            // Codex audit of funnel-2, #3: every removed item's placement at any depth is cancelled (a late one is rejected,
            // not consumed); BAG holds ITEM > CHILD > GRANDCHILD
            World world = Build(Start.InBag);
            world.Writer.ThrowOnViolation = true;

            WorldWriter.Placement direct = PendingIn(world, 0x4000_7040, BAG);
            WorldWriter.Placement grand = PendingIn(world, 0x4000_7042, CHILD);      // BAG > ITEM > CHILD > it
            WorldWriter.Placement greatGrand = PendingIn(world, 0x4000_7043, GRANDCHILD);
            Item other = world.Writer.UpsertWorldItem(0x4000_7041, 0xEED, 0, 1, 1011, 1010, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement q);
            world.Writer.OutstandingPlacements.Should().Be(4);

            world.Writer.ClearContainer(BAG, false, itemOnly: false);

            world.Writer.OutstandingPlacements.Should().Be(1, "the removed items' placements are cancelled at every depth, the other stays");

            foreach (WorldWriter.Placement p in new[] { direct, grand, greatGrand })
            {
                Action stale = () => world.Writer.PlaceWorldItem(p);
                stale.Should().Throw<ArgumentException>();
            }

            world.Writer.PlaceWorldItem(q);
            other.IsDestroyed.Should().BeFalse();
        }

        [Fact]
        public void ClearContainer_Keeps_The_Kept_Equipments_Placements()
        {
            // a corpse clear keeps its equipment with its contents: their placements stay; the removed plain item's go
            World world = BuildClear(Start.InBag);
            world.Writer.ThrowOnViolation = true;

            WorldWriter.Placement underKept = PendingIn(world, 0x4000_7044, KEPT1);
            WorldWriter.Placement underKeptDeep = PendingIn(world, 0x4000_7045, 0x4000_7044);
            WorldWriter.Placement underRemoved = PendingIn(world, 0x4000_7046, 0x4000_7021);
            WorldWriter.Placement underRemovedDeep = PendingIn(world, 0x4000_7047, 0x4000_7046);
            world.Writer.OutstandingPlacements.Should().Be(4);

            world.Writer.ClearContainer(CORPSEBOX, true, itemOnly: false);

            world.Writer.OutstandingPlacements.Should().Be(2);
            ChildSerials(world.Items.Get(CORPSEBOX)).Should().Equal(KEPT1, KEPT2);

            foreach (WorldWriter.Placement p in new[] { underRemoved, underRemovedDeep })
            {
                Action stale = () => world.Writer.PlaceWorldItem(p);
                stale.Should().Throw<ArgumentException>();
            }

            world.Writer.PlaceWorldItem(underKept);
            world.Writer.PlaceWorldItem(underKeptDeep);
            world.Writer.OutstandingPlacements.Should().Be(0, "both kept placements were consumed");
            world.Items.Get(0x4000_7045).IsDestroyed.Should().BeFalse();
        }

        [Fact]
        public void ClearContainer_Of_0x24_Clears_The_Item_Under_A_Mobiles_Serial()
        {
            // Codex audit of funnel-2, #1: 0x24 found its container with World.Items.Get (the old OpenContainer lookup), 0x3C
            // with World.Get, which prefers a mobile. An item under a live mobile's serial (the unconverted EquipItem path can
            // make one) is what 0x24 clears; the mobile's equipment stays
            static World BuildTwin()
            {
                World w = Build(Start.Worn); // MOB wears ITEM (> CHILD > GRANDCHILD)
                Item twin = w.GetOrCreateItem(MOB, out _);
                twin.Graphic = 0xE75;
                Item inner = w.GetOrCreateItem(0x4000_7061, out _);
                inner.Graphic = 0xF7C;
                inner.Container = MOB;
                twin.PushToBack(inner);

                return w;
            }

            World oldWorld = BuildTwin(), newWorld = BuildTwin();

            // OpenContainer before T1: `Item it = world.Items.Get(serial); ... ClearContainerAndRemoveItems(world, it)`
            OldClear(oldWorld, oldWorld.Items.Get(MOB), false);
            newWorld.Writer.ClearContainer(MOB, false, itemOnly: true);

            newWorld.Items.Get(0x4000_7061).Should().BeNull("the item's contents are cleared");
            newWorld.Items.Get(MOB).Items.Should().BeNull();
            ChildSerials(newWorld.Mobiles.Get(MOB)).Should().Equal(new[] { ITEM }, "the mobile's equipment is not touched");
            newWorld.Items.Get(ITEM).IsDestroyed.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);

            // the fixture tells the lookups apart: World.Get (0x3C's) clears the mobile instead, as the old 0x3C did
            World oldMobile = BuildTwin(), newMobile = BuildTwin();
            OldClear(oldMobile, oldMobile.Get(MOB), false);
            newMobile.Writer.ClearContainer(MOB, false, itemOnly: false);
            newMobile.Items.Get(ITEM).Should().BeNull("World.Get found the mobile and removed its equipment");
            newMobile.Items.Get(0x4000_7061).Should().NotBeNull();
            AssertSameWorld(oldMobile, newMobile);
        }

        [Fact]
        public void ClearContainer_Inside_An_Operation_Is_Rejected()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = Build(Start.InBag);
                world.Writer.ThrowOnViolation = throws;
                bool ran = false;
                world.Ecs.CommitObserver = (item, v) =>
                {
                    if (!ran)
                    {
                        ran = true;
                        Action nested = () => world.Writer.ClearContainer(BAG, false, itemOnly: false);

                        // asserted here, inside the operation: the nesting is rejected before any removal
                        if (throws)
                        {
                            nested.Should().Throw<InvalidOperationException>();
                        }
                        else
                        {
                            nested();
                        }

                        world.Items.Get(ITEM).Should().NotBeNull("a rejected clear removes nothing");
                    }
                };

                world.Writer.UpsertWorldItem(0x4000_7050, 0xEED, 0, 1, 1010, 1010, 0, Direction.North, 0, Flags.None, 0, out _, out _);
                world.Ecs.CommitObserver = null;

                ran.Should().BeTrue();
                world.Writer.Violations.Should().Be(1);
                world.Items.Get(ITEM).Should().NotBeNull("a rejected clear removes nothing");
                ChildSerials(world.Items.Get(BAG)).Should().Contain(ITEM);
            }
        }

        // ---- T1 funnel-2: 0x27's other deny branches (paperdoll, the "something wrong" removal, terrain)

        internal struct HeldPacket
        {
            public ushort Graphic, Hue, Amount;
            public Flags Flags;
            public Layer Layer;
            public ushort X, Y;
            public sbyte Z;
            public uint Container;
        }

        // DenyMoveItem's non-container branch before T1 (the console lines and the paperdoll refresh left out)
        internal static Item OldDeny(World world, uint serial, in HeldPacket h)
        {
            Item item = world.GetOrCreateItem(serial);

            item.Graphic = h.Graphic;
            item.Hue = h.Hue;
            item.Amount = h.Amount;
            item.Flags = h.Flags;
            item.Layer = h.Layer;
            item.X = h.X;
            item.Y = h.Y;
            item.Z = h.Z;
            item.CheckGraphicChange();

            Entity container = world.Get(h.Container);

            if (container != null)
            {
                if (SerialHelper.IsMobile(container.Serial))
                {
                    world.RemoveItemFromContainer(item);
                    container.PushToBack(item);
                    item.Container = container.Serial;
                    world.Ecs.Sync(item);
                }
                else
                {
                    world.RemoveItem(item, true);
                }
            }
            else
            {
                world.RemoveItemFromContainer(item);
                item.SetInWorldTile(item.X, item.Y, item.Z);
            }

            return item;
        }

        private static readonly HeldPacket[] HeldPackets =
        {
            // back onto the wearer (an equipped item dragged off the paperdoll): the hue as given, no FixHue
            new HeldPacket { Graphic = 0x1515, Hue = 0x8BB9, Amount = 1, Flags = Flags.Movable, Layer = Layer.Shirt, X = 1000, Y = 1000, Z = 0, Container = MOB },
            // its container is an item: the old code removed it
            new HeldPacket { Graphic = 0x1515, Hue = 0x21, Amount = 2, Flags = Flags.None, Layer = Layer.Shirt, X = 40, Y = 50, Z = 0, Container = BAG },
            // from the ground: back on its tile
            new HeldPacket { Graphic = 0x0EED, Hue = 0x35, Amount = 500, Flags = Flags.Hidden, Layer = Layer.Invalid, X = 1004, Y = 1005, Z = 2, Container = 0xFFFF_FFFF },
            // its container is gone: on the tile at the held position
            new HeldPacket { Graphic = 0x0EED, Hue = 0, Amount = 3, Flags = Flags.None, Layer = Layer.Invalid, X = 1006, Y = 1007, Z = -3, Container = 0x4000_7FFF },
            // a corpse graphic: CheckGraphicChange's corpse branch on the old direction (running bit: UsedLayer)
            new HeldPacket { Graphic = 0x2006, Hue = 0x455, Amount = 0x190, Flags = Flags.None, Layer = Layer.Invalid, X = 1008, Y = 1008, Z = 0, Container = 0xFFFF_FFFF },
            // a corpse graphic back onto the wearer
            new HeldPacket { Graphic = 0x2006, Hue = 0, Amount = 1, Flags = Flags.None, Layer = Layer.Cloak, X = 1, Y = 1, Z = 0, Container = MOB },
        };

        public static IEnumerable<object[]> HeldCases()
        {
            foreach (Start s in Enum.GetValues(typeof(Start)))
            {
                for (int i = 0; i < HeldPackets.Length; i++)
                {
                    foreach (bool crowded in new[] { false, true })
                    {
                        yield return new object[] { s, i, crowded };
                    }
                }
            }
        }

        private const uint WORN_BEFORE = 0x4000_7030, WORN_AFTER = 0x4000_7031;

        // the wearer with more equipment (Codex audit of funnel-2: one equipped item cannot show the sibling order): one item
        // before ITEM's place and one after it, in the child list and the mirror's equipment index
        private static void Crowd(World world)
        {
            Mobile m = world.Mobiles.Get(MOB);
            Item item = world.Items.Get(ITEM);
            bool worn = item != null && item.Container == MOB;

            Item before = world.GetOrCreateItem(WORN_BEFORE);
            before.Graphic = 0x1F7B;
            before.Layer = Layer.Pants;
            before.Container = MOB;

            if (worn)
            {
                m.Remove(item);
            }

            m.PushToBack(before);
            world.Ecs.Sync(before);

            if (worn)
            {
                m.PushToBack(item);
                world.Ecs.Sync(item);
            }

            Item after = world.GetOrCreateItem(WORN_AFTER);
            after.Graphic = 0x1F7C;
            after.Layer = Layer.Helmet;
            after.Container = MOB;
            m.PushToBack(after);
            world.Ecs.Sync(after);

            ChildSerials(m).Should().Equal(worn ? new[] { WORN_BEFORE, ITEM, WORN_AFTER } : new[] { WORN_BEFORE, WORN_AFTER });
        }

        [Theory]
        [MemberData(nameof(HeldCases))]
        public void ReturnHeldItem_Equals_The_Old_Path(Start start, int packet, bool crowded)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            HeldPacket h = HeldPackets[packet];

            foreach (World w in new[] { oldWorld, newWorld })
            {
                if (crowded)
                {
                    Crowd(w);
                }

                Item it = w.Items.Get(ITEM);

                if (it != null)
                {
                    it.Direction = Direction.Running | Direction.West;
                }
            }

            Item a = OldDeny(oldWorld, ITEM, h);
            Item b = newWorld.Writer.ReturnHeldItem(ITEM, h.Graphic, h.Hue, h.Amount, h.Flags, h.Layer, h.X, h.Y, h.Z, h.Container, out Entity container, out WorldWriter.HeldItemReturn result);

            b.Should().NotBeNull();
            container.Should().BeSameAs(newWorld.Get(h.Container));
            result.Should().Be(container == null ? WorldWriter.HeldItemReturn.Terrain
                : SerialHelper.IsMobile(h.Container) ? WorldWriter.HeldItemReturn.Paperdoll : WorldWriter.HeldItemReturn.Removed);
            b.IsDestroyed.Should().Be(a.IsDestroyed);
            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld, equipmentIndexOrder: false);

            if (crowded && result == WorldWriter.HeldItemReturn.Paperdoll)
            {
                ChildSerials(newWorld.Mobiles.Get(MOB)).Should().Equal(new[] { WORN_BEFORE, WORN_AFTER, ITEM }, "re-equipped at the end, the siblings in order");
            }

            // the same packet again
            OldDeny(oldWorld, ITEM, h);
            newWorld.Writer.ReturnHeldItem(ITEM, h.Graphic, h.Hue, h.Amount, h.Flags, h.Layer, h.X, h.Y, h.Z, h.Container, out _, out _);
            AssertSameWorld(oldWorld, newWorld, equipmentIndexOrder: false);
        }

        [Fact]
        public void ReturnHeldItem_Computes_Its_Values_Before_The_Writes()
        {
            // the commit sees the getters still on the previous values (as T4's ECS-backed getters would); T3a: the paperdoll
            // branch commits once, the wearer with the equipment-order stamp (no detached commit before it)
            World world = Build(Start.Worn);
            var seen = new List<(uint Container, ushort Graphic, uint CacheContainer, ushort CacheGraphic)>();
            world.Ecs.CommitObserver = (item, v) => seen.Add((v.Container, v.Graphic, item.Container, item.Graphic));

            world.Writer.ReturnHeldItem(ITEM, 0x1517, 0x22, 1, Flags.None, Layer.Shirt, 1000, 1000, 0, MOB, out _, out _);
            world.Ecs.CommitObserver = null;

            seen.Should().Equal((MOB, (ushort)0x1517, MOB, (ushort)0x1515));
            world.Items.Get(ITEM).Container.Should().Be(MOB);
            ChildSerials(world.Mobiles.Get(MOB)).Should().Equal(ITEM);
        }

        [Fact]
        public void ReturnHeldItem_To_The_Paperdoll_Commits_Once_At_The_Back_Of_The_Equipment_Order()
        {
            // T3a (the equipment-order stamp): one mirror write, the next child-order stamp, so the item is last in the wearer's
            // stamp order and its layer's first item is the next one, as in the child list
            World world = BuildEquip(Start.Worn, crowded: true, packLayer: false);
            world.Writer.Equip(0x4000_7F01, 0x1517, Layer.Shirt, MOB, 0, out _, out _);
            ulong before = world.Ecs.Mirror.ChildStamp;
            long writes = world.Ecs.ItemMirrorWrites;

            world.Writer.ReturnHeldItem(ITEM, 0x1515, 0, 1, Flags.None, Layer.Shirt, 0, 0, 0, MOB, out _, out WorldWriter.HeldItemReturn r);

            r.Should().Be(WorldWriter.HeldItemReturn.Paperdoll);
            (world.Ecs.ItemMirrorWrites - writes).Should().Be(1, "one commit");
            world.Ecs.Mirror.ChildStamp.Should().Be(before + 1, "one stamp");
            world.Ecs.Mirror.Contents(MOB).ToArray().Should().EndWith(new[] { 0x4000_7F01u, ITEM }, "the stamps (this world's siblings were linked without the funnel)");
            ChildSerials(world.Mobiles.Get(MOB)).Last().Should().Be(ITEM);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Shirt).Should().Be(0x4000_7F01u, "the other shirt is first now");
        }

        [Fact]
        public void ReturnHeldItem_Removal_Unlinks_From_The_Captured_Parent()
        {
            // Codex audit of funnel-2, #2: the removal branch must not read item.Container back after the detached commit.
            // The observer makes the cache's Container the committed value (what T4's ECS-backed getter would return); the
            // item still leaves its old parent's child list and is destroyed with its contents
            World world = Build(Start.InBag);
            world.Ecs.CommitObserver = (item, v) =>
            {
                if (item.Serial == ITEM)
                {
                    item.Container = v.Container;
                }
            };

            world.Writer.ReturnHeldItem(ITEM, 0x1515, 0, 1, Flags.None, Layer.Shirt, 40, 50, 0, BAG, out _, out WorldWriter.HeldItemReturn result);
            world.Ecs.CommitObserver = null;

            result.Should().Be(WorldWriter.HeldItemReturn.Removed);
            world.Items.Get(ITEM).Should().BeNull();
            world.Items.Get(CHILD).Should().BeNull();
            world.Items.Get(GRANDCHILD).Should().BeNull();
            ChildSerials(world.Items.Get(BAG)).Should().Equal(new[] { 0x4000_7009u }, "unlinked from the captured parent, the sibling stays");
        }

        [Fact]
        public void ReturnHeldItem_Removal_Cancels_The_Subtrees_Placements()
        {
            // Codex audit of funnel-2, #3: the deny removal cancels the placements of the item's contents at every depth; the
            // paperdoll and terrain returns keep the item's contents, so their placements stay
            foreach (bool removal in new[] { true, false })
            {
                World world = Build(Start.InBag); // BAG > ITEM > CHILD > GRANDCHILD
                world.Writer.ThrowOnViolation = true;
                WorldWriter.Placement child = PendingIn(world, 0x4000_7042, ITEM);
                WorldWriter.Placement deep = PendingIn(world, 0x4000_7043, GRANDCHILD);
                world.Writer.OutstandingPlacements.Should().Be(2);

                world.Writer.ReturnHeldItem(ITEM, 0x1515, 0, 1, Flags.None, Layer.Shirt, 40, 50, 0, removal ? BAG : MOB, out _, out WorldWriter.HeldItemReturn result);

                result.Should().Be(removal ? WorldWriter.HeldItemReturn.Removed : WorldWriter.HeldItemReturn.Paperdoll);

                if (removal)
                {
                    world.Writer.OutstandingPlacements.Should().Be(0);

                    foreach (WorldWriter.Placement p in new[] { child, deep })
                    {
                        Action stale = () => world.Writer.PlaceWorldItem(p);
                        stale.Should().Throw<ArgumentException>();
                    }
                }
                else
                {
                    world.Writer.OutstandingPlacements.Should().Be(2, "the re-equipped item's contents keep their placements");
                    world.Writer.PlaceWorldItem(child);
                    world.Writer.PlaceWorldItem(deep);
                    world.Writer.OutstandingPlacements.Should().Be(0);
                }
            }
        }

        [Fact]
        public void ReturnHeldItem_Inside_An_Operation_Is_Rejected()
        {
            World world = Build(Start.Worn);
            world.Writer.ThrowOnViolation = false;
            Item nestedResult = null;
            bool ran = false;
            world.Ecs.CommitObserver = (item, v) =>
            {
                if (!ran)
                {
                    ran = true;
                    nestedResult = world.Writer.ReturnHeldItem(ITEM, 0x1517, 0, 1, Flags.None, Layer.Shirt, 1, 1, 0, 0xFFFF_FFFF, out _, out WorldWriter.HeldItemReturn r);
                    r.Should().Be(WorldWriter.HeldItemReturn.None);
                }
            };

            world.Writer.UpsertWorldItem(0x4000_7050, 0xEED, 0, 1, 1010, 1010, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            world.Ecs.CommitObserver = null;

            ran.Should().BeTrue();
            nestedResult.Should().BeNull();
            world.Writer.Violations.Should().Be(1);
            world.Items.Get(ITEM).Container.Should().Be(MOB, "the rejected return changed nothing");
        }

        // ---- T1 funnel-3: equipment (0x2E EquipItem; 0x78/0xD3 UpdateObject's equipment list)

        internal struct EquipPacket
        {
            public uint Serial;
            public ushort Graphic, Hue;
            public Layer Layer;
            public uint Container;
        }

        // EquipItem before T1 (the gump refreshes and the abilities left out; the gumps' container is returned)
        internal static Item OldEquip(World world, in EquipPacket e, out Entity entity, out uint shown)
        {
            Item item = world.GetOrCreateItem(e.Serial);

            if (item.Graphic != 0 && item.Layer != Layer.Backpack)
            {
                world.RemoveItemFromContainer(item);
            }
            else if (item.Container != e.Container)
            {
                // the behaviour fix (Codex audit P2 #2, WorldWriter.Equip): the kept link leaves the old parent's list; the
                // handler before it left a backpack that was its old parent's first child as that parent's list head
                world.Get(item.Container)?.Remove(item);
            }

            shown = item.Container;

            item.Graphic = e.Graphic;
            item.Layer = e.Layer;
            item.Container = e.Container;
            item.FixHue(e.Hue);
            item.Amount = 1;

            entity = world.Get(item.Container);
            entity?.PushToBack(item);
            world.Ecs.Sync(item);

            return item;
        }

        private const uint PACK = 0x4000_7040, NEWITEM = 0x4000_7041, NOBODY = 0x0000_3FFF, DEAD = MOB | 0x8000_0000;

        private static readonly EquipPacket[] EquipPackets =
        {
            // onto the wearer: FixHue (0x0BB9 becomes 1, the top bit stays)
            new EquipPacket { Serial = ITEM, Graphic = 0x1516, Hue = 0x8BB9, Layer = Layer.Shirt, Container = MOB },
            // onto the wearer as a backpack (its next equip keeps its old container link)
            new EquipPacket { Serial = ITEM, Graphic = 0x0E75, Hue = 0, Layer = Layer.Backpack, Container = MOB },
            // into an item (a vendor's restock box layer)
            new EquipPacket { Serial = ITEM, Graphic = 0x1517, Hue = 0x21, Layer = Layer.ShopBuyRestock, Container = BAG },
            // onto a wearer nobody knows: the Container and Layer without a link
            new EquipPacket { Serial = ITEM, Graphic = 0x1518, Hue = 0x35, Layer = Layer.Pants, Container = NOBODY },
            // a corpse graphic: no CheckGraphicChange in 0x2E
            new EquipPacket { Serial = ITEM, Graphic = 0x2006, Hue = 0, Layer = Layer.Cloak, Container = MOB },
            // a new item
            new EquipPacket { Serial = NEWITEM, Graphic = 0x1F03, Hue = 0x455, Layer = Layer.Robe, Container = MOB },
            // a mobile's serial: found among the items (created there), as the old GetOrCreateItem did
            new EquipPacket { Serial = MOB, Graphic = 0x1515, Hue = 0, Layer = Layer.Shirt, Container = MOB },
        };

        public static IEnumerable<object[]> EquipCases()
        {
            foreach (Start s in Enum.GetValues(typeof(Start)))
            {
                for (int i = 0; i < EquipPackets.Length; i++)
                {
                    foreach (bool crowded in new[] { false, true })
                    {
                        foreach (bool packLayer in new[] { false, true })
                        {
                            yield return new object[] { s, i, crowded, packLayer };
                        }
                    }
                }
            }
        }

        // the starting state of the equipment tests: Build, the crowded wearer, ITEM facing west with the running bit (a
        // corpse graphic takes its state from it), optionally ITEM's layer set to the backpack's
        private static World BuildEquip(Start start, bool crowded, bool packLayer)
        {
            World world = Build(start);

            if (crowded)
            {
                Crowd(world);
            }

            Item it = world.Items.Get(ITEM);

            if (it != null)
            {
                it.Direction = Direction.Running | Direction.West;

                if (packLayer)
                {
                    it.Layer = Layer.Backpack;
                    world.Ecs.Sync(it);
                }
            }

            return world;
        }

        [Theory]
        [MemberData(nameof(EquipCases))]
        public void Equip_Equals_The_Old_Path(Start start, int packet, bool crowded, bool packLayer)
        {
            World oldWorld = BuildEquip(start, crowded, packLayer), newWorld = BuildEquip(start, crowded, packLayer);
            EquipPacket e = EquipPackets[packet];

            Item a = OldEquip(oldWorld, e, out Entity oldEntity, out uint oldShown);
            Item b = newWorld.Writer.Equip(e.Serial, e.Graphic, e.Layer, e.Container, e.Hue, out Entity entity, out uint shown);

            b.Should().BeSameAs(newWorld.Items.Get(e.Serial));
            shown.Should().Be(oldShown, "the gumps the handler refreshes");
            (entity == null).Should().Be(oldEntity == null);

            if (entity != null)
            {
                entity.Serial.Should().Be(oldEntity.Serial);
                entity.GetType().Should().Be(oldEntity.GetType());
            }

            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);
            AssertSameItem(a, b);

            // the same packet again, then another one on top
            OldEquip(oldWorld, e, out _, out oldShown);
            newWorld.Writer.Equip(e.Serial, e.Graphic, e.Layer, e.Container, e.Hue, out _, out shown);
            shown.Should().Be(oldShown);
            AssertSameWorld(oldWorld, newWorld);

            EquipPacket next = EquipPackets[(packet + 1) % EquipPackets.Length];
            OldEquip(oldWorld, next, out _, out oldShown);
            newWorld.Writer.Equip(next.Serial, next.Graphic, next.Layer, next.Container, next.Hue, out _, out shown);
            shown.Should().Be(oldShown);
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void Equip_Re_Equips_At_The_End_Of_The_Equipment_Index()
        {
            // the old path published the detached state before the wearer's: a re-equip on the same wearer and layer moves the
            // item to the end of the mirror's equipment index (one commit of the final state would leave it in place)
            World world = BuildEquip(Start.Worn, crowded: true, packLayer: false);
            Serials(world.Ecs.Mirror.GetEquipment(MOB)).Should().Equal(ITEM, WORN_BEFORE, WORN_AFTER);

            world.Writer.Equip(ITEM, 0x1515, Layer.Shirt, MOB, 0, out _, out _);

            Serials(world.Ecs.Mirror.GetEquipment(MOB)).Should().Equal(WORN_BEFORE, WORN_AFTER, ITEM);
            ChildSerials(world.Mobiles.Get(MOB)).Should().Equal(WORN_BEFORE, WORN_AFTER, ITEM);
        }

        [Fact]
        public void Equip_Computes_Its_Values_Before_The_Writes()
        {
            // each commit sees the cache still on the previous values (as T4's ECS-backed getters would)
            World world = Build(Start.InBag);
            var seen = new List<(uint Container, ushort Graphic, ushort Amount, uint CacheContainer, ushort CacheGraphic)>();
            world.Ecs.CommitObserver = (item, v) => seen.Add((v.Container, v.Graphic, v.Amount, item.Container, item.Graphic));

            world.Writer.Equip(ITEM, 0x1517, Layer.Shirt, MOB, 0x22, out Entity wearer, out uint shown);
            world.Ecs.CommitObserver = null;

            seen.Should().Equal((0xFFFF_FFFFu, (ushort)0x1515, (ushort)3, BAG, (ushort)0x1515), (MOB, (ushort)0x1517, (ushort)1, BAG, (ushort)0x1515));
            wearer.Should().BeSameAs(world.Mobiles.Get(MOB));
            shown.Should().Be(0xFFFF_FFFFu);
            world.Items.Get(ITEM).Container.Should().Be(MOB);
            ChildSerials(world.Items.Get(BAG)).Should().Equal(0x4000_7009u);
        }

        // UpdateObject's equipment part before T1: the clear of the unopened non-backpack children, then each entry
        internal static void OldWear(World world, uint serial, (uint Serial, ushort Graphic, Layer Layer, ushort Hue)[] entries)
        {
            Entity obj = world.Get(serial);

            if (obj == null)
            {
                return;
            }

            if (!obj.IsEmpty)
            {
                LinkedObject o = obj.Items;

                while (o != null)
                {
                    LinkedObject next = o.Next;
                    Item it = (Item)o;

                    if (!it.Opened && it.Layer != Layer.Backpack)
                    {
                        world.RemoveItem(it.Serial, true);
                    }

                    o = next;
                }
            }

            foreach (var e in entries)
            {
                Item item = world.GetOrCreateItem(e.Serial);
                item.Graphic = e.Graphic;
                item.FixHue(e.Hue);
                item.Amount = 1;
                world.RemoveItemFromContainer(item);
                item.Container = serial;
                item.Layer = e.Layer;

                item.CheckGraphicChange();

                obj.PushToBack(item);
                world.Ecs.Sync(item);
            }
        }

        private static void NewWear(World world, uint serial, (uint Serial, ushort Graphic, Layer Layer, ushort Hue)[] entries)
        {
            // as the handler: it returns when World.Get finds nothing, and passes the owner it found to every entry
            Entity obj = world.Get(serial);

            if (obj == null)
            {
                return;
            }

            world.Writer.ClearWornItems(serial);

            foreach (var e in entries)
            {
                world.Writer.WearItem(obj, e.Serial, e.Graphic, e.Layer, e.Hue).Should().BeSameAs(world.Items.Get(e.Serial));
            }
        }

        private static readonly (uint Serial, ushort Graphic, Layer Layer, ushort Hue)[][] WearLists =
        {
            new[] { (ITEM, (ushort)0x1515, Layer.Shirt, (ushort)0x21) },
            // an equipped item again, a corpse graphic (its state from the old direction: running, west), a new item
            new[] { (WORN_BEFORE, (ushort)0x1F7B, Layer.Pants, (ushort)0), (ITEM, (ushort)0x2006, Layer.Cloak, (ushort)0x455), (NEWITEM, (ushort)0x1F03, Layer.Robe, (ushort)0x8BB9) },
            // the same item twice
            new[] { (ITEM, (ushort)0x1515, Layer.Shirt, (ushort)0), (ITEM, (ushort)0x1516, Layer.Pants, (ushort)1) },
            // the kept children (the opened container, the backpack) re-listed on their own layers: each moves to the end of
            // the equipment index (the detached commit), as before T1
            new[] { (WORN_AFTER, (ushort)0x1F7C, Layer.Helmet, (ushort)0), (PACK, (ushort)0x0E75, Layer.Backpack, (ushort)0) },
            // the clear alone
            Array.Empty<(uint, ushort, Layer, ushort)>(),
            // a mobile's serial as an entry: found among the items (created there)
            new[] { (MOB, (ushort)0x1515, Layer.Shirt, (ushort)0) },
        };

        public static IEnumerable<object[]> WearCases()
        {
            foreach (Start s in Enum.GetValues(typeof(Start)))
            {
                // NOBODY: the handler returns before the list, so those 60 cases only check that nothing changes; DEAD (MOB
                // rekeyed as a dying mobile) is a second owner-present mobile, its serial not a mobile's
                foreach (uint owner in new[] { MOB, BAG, NOBODY, DEAD })
                {
                    for (int i = 0; i < WearLists.Length; i++)
                    {
                        foreach (bool crowded in new[] { false, true })
                        {
                            yield return new object[] { s, owner, i, crowded };
                        }
                    }
                }
            }
        }

        // the crowded wearer also has a backpack (kept) and an opened container (kept)
        private static World BuildWear(Start start, bool crowded)
        {
            World world = BuildEquip(start, crowded, packLayer: false);

            if (crowded)
            {
                Mobile m = world.Mobiles.Get(MOB);
                Item pack = world.GetOrCreateItem(PACK);
                pack.Graphic = 0x0E75;
                pack.Layer = Layer.Backpack;
                pack.Container = MOB;
                m.PushToBack(pack);
                world.Ecs.Sync(pack);
                world.Items.Get(WORN_AFTER).Opened = true;
            }

            return world;
        }

        [Theory]
        [MemberData(nameof(WearCases))]
        public void WearItem_Equals_The_Old_Path(Start start, uint owner, int list, bool crowded)
        {
            World oldWorld = BuildWear(start, crowded), newWorld = BuildWear(start, crowded);
            var entries = WearLists[list];

            if (owner == DEAD)
            {
                Rekey(oldWorld);
                Rekey(newWorld);
            }

            OldWear(oldWorld, owner, entries);
            NewWear(newWorld, owner, entries);

            newWorld.Writer.InScope.Should().BeFalse();
            newWorld.Ecs.IsFolding.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);

            // the same packet again
            OldWear(oldWorld, owner, entries);
            NewWear(newWorld, owner, entries);
            AssertSameWorld(oldWorld, newWorld);
        }

        // MOB rekeyed as DisplayDeath (0xAF) does for a dying mobile: its serial gets the high bit, its children follow
        private static void Rekey(World world)
        {
            Mobile owner = world.Mobiles.Get(MOB);
            world.Mobiles.Remove(owner.Serial).Should().BeTrue();

            for (LinkedObject i = owner.Items; i != null; i = i.Next)
            {
                ((Item)i).Container = DEAD;
            }

            world.Mobiles[DEAD] = owner;
            owner.Serial = DEAD;
            world.Ecs.OnSerialChanged(MOB, owner);
        }

        private static World BuildDeadOwner(out uint dead)
        {
            World world = BuildWear(Start.Worn, crowded: true);
            Rekey(world);
            dead = DEAD;

            return world;
        }

        [Fact]
        public void WearItem_Keeps_The_Captured_Owner_When_An_Entry_Shares_Its_Serial()
        {
            // Codex audit of part 3 (P2): a death-rekeyed owner (0x8000_3000) whose list names its own serial first. That entry
            // creates an item under the mobile's serial, and World.Get prefers the items for a serial that is not a mobile's,
            // so a lookup per entry would put the following entry inside that item; the old loop used the obj it captured
            // before the list, and the handler now passes that instance to every entry
            World oldWorld = BuildDeadOwner(out uint dead), newWorld = BuildDeadOwner(out _);
            var entries = new[] { (dead, (ushort)0x1515, Layer.Shirt, (ushort)0), (NEWITEM, (ushort)0x1F03, Layer.Robe, (ushort)0) };

            OldWear(oldWorld, dead, entries);
            NewWear(newWorld, dead, entries);

            foreach (World w in new[] { oldWorld, newWorld })
            {
                w.Get(dead).Should().BeOfType<Item>("the entry's item now shadows the mobile for World.Get");
                ChildSerials(w.Mobiles.Get(dead)).Should().EndWith(new[] { dead, NEWITEM });
                w.Items.Get(NEWITEM).Container.Should().Be(dead);
                ChildSerials(w.Items.Get(dead)).Should().BeEmpty("the following entry is the mobile's, not the new item's");
            }

            newWorld.Writer.InScope.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);

            // the same packet again: now the handler's own World.Get finds the item, so that item is the captured owner for
            // the whole list (as before T1; the entry naming it is pushed into itself, as the old loop did)
            OldWear(oldWorld, dead, entries);
            NewWear(newWorld, dead, entries);
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void WearItem_Rejects_An_Owner_This_World_Does_Not_Hold()
        {
            foreach (bool throws in new[] { true, false })
            {
                World world = BuildEquip(Start.Worn, crowded: false, packLayer: false);
                World other = BuildEquip(Start.Worn, crowded: false, packLayer: false);
                world.Writer.ThrowOnViolation = throws;
                Item before = world.Items.Get(ITEM);
                (uint Container, Layer Layer, ushort Graphic) was = (before.Container, before.Layer, before.Graphic);

                // another World's mobile, and a stale instance (not the one the table holds) under the right serial
                Entity stale = new World().GetOrCreateMobile(MOB);

                foreach (Entity owner in new[] { other.Mobiles.Get(MOB), stale })
                {
                    Func<Item> wear = () => world.Writer.WearItem(owner, ITEM, 0x1516, Layer.Pants, 0);

                    if (throws)
                    {
                        wear.Should().Throw<ArgumentException>();
                    }
                    else
                    {
                        wear().Should().BeNull();
                    }

                    Item it = world.Items.Get(ITEM);
                    (it.Container, it.Layer, it.Graphic).Should().Be(was, "rejected before any change");
                    world.Writer.InScope.Should().BeFalse();
                }

                world.Writer.Violations.Should().Be(2);
            }
        }

        [Fact]
        public void WearItem_Computes_Its_Values_Before_The_Writes()
        {
            // the detached commit carries the entry's graphic with the old layer; the final one the owner and the corpse's
            // layer from the old direction; the cache still holds the previous container, layer and direction at both
            World world = BuildEquip(Start.Worn, crowded: false, packLayer: false);
            var seen = new List<(uint Container, ushort Graphic, Layer Layer, Direction Direction, uint CacheContainer, ushort CacheGraphic, Layer CacheLayer)>();
            world.Ecs.CommitObserver = (item, v) => seen.Add((v.Container, v.Graphic, v.Layer, v.Direction, item.Container, item.Graphic, item.Layer));

            world.Writer.WearItem(world.Get(MOB), ITEM, 0x2006, Layer.Cloak, 0);
            world.Ecs.CommitObserver = null;

            seen.Should().Equal(
                (0xFFFF_FFFFu, (ushort)0x2006, Layer.Shirt, Direction.Running | Direction.West, MOB, (ushort)0x1515, Layer.Shirt),
                (MOB, (ushort)0x2006, (Layer)Direction.West, Direction.West, MOB, (ushort)0x2006, Layer.Shirt)); // the graphic is written before the unlink, as before T1
            Item it = world.Items.Get(ITEM);
            (it.Layer, it.Direction, it.UsedLayer, it.AnimIndex).Should().Be(((Layer)Direction.West, Direction.West, true, (byte)99));
        }

        [Fact]
        public void ClearWornItems_Cancels_The_Removed_Items_Placements()
        {
            // the removed equipment's contents lose their placements at any depth; the backpack's and the opened container's
            // contents keep theirs
            World world = BuildWear(Start.Worn, crowded: true); // MOB > ITEM > CHILD > GRANDCHILD; PACK; WORN_AFTER opened
            world.Writer.ThrowOnViolation = true;
            WorldWriter.Placement deep = PendingIn(world, 0x4000_7042, GRANDCHILD);
            WorldWriter.Placement inPack = PendingIn(world, 0x4000_7043, PACK);
            WorldWriter.Placement inOpened = PendingIn(world, 0x4000_7044, WORN_AFTER);

            world.Writer.ClearWornItems(MOB);

            ChildSerials(world.Mobiles.Get(MOB)).Should().Equal(WORN_AFTER, PACK);
            world.Writer.OutstandingPlacements.Should().Be(2);
            Action stale = () => world.Writer.PlaceWorldItem(deep);
            stale.Should().Throw<ArgumentException>();
            world.Writer.PlaceWorldItem(inPack);
            world.Writer.PlaceWorldItem(inOpened);
            world.Writer.OutstandingPlacements.Should().Be(0);
        }

        [Fact]
        public void Equip_And_WearItem_Supersede_The_Items_Placement()
        {
            // an item written by 0x1A and equipped before its placement is not placed later: the equip cancels the token
            foreach (bool wear in new[] { false, true })
            {
                World world = Build(Start.None);
                world.Writer.ThrowOnViolation = true;
                WorldWriter.Placement p = Upsert(world, ITEM);
                world.Writer.OutstandingPlacements.Should().Be(1);

                if (wear)
                {
                    world.Writer.WearItem(world.Get(MOB), ITEM, 0x1515, Layer.Shirt, 0);
                }
                else
                {
                    world.Writer.Equip(ITEM, 0x1515, Layer.Shirt, MOB, 0, out _, out _);
                }

                world.Writer.OutstandingPlacements.Should().Be(0);
                Action stale = () => world.Writer.PlaceWorldItem(p);
                stale.Should().Throw<ArgumentException>();
                world.Items.Get(ITEM).Container.Should().Be(MOB);
            }
        }

        [Fact]
        public void The_Equipment_Operations_Inside_An_Operation_Are_Rejected()
        {
            foreach (bool throws in new[] { true, false })
            {
                foreach (int op in new[] { 0, 1, 2 })
                {
                    World world = BuildEquip(Start.Worn, crowded: true, packLayer: false);
                    world.Writer.ThrowOnViolation = throws;
                    bool ran = false;
                    world.Ecs.CommitObserver = (item, v) =>
                    {
                        if (!ran)
                        {
                            ran = true;
                            Action nested = op switch
                            {
                                0 => () => world.Writer.Equip(ITEM, 0x1517, Layer.Pants, BAG, 0, out _, out _).Should().BeNull(),
                                1 => () => world.Writer.ClearWornItems(MOB),
                                _ => () => world.Writer.WearItem(world.Get(MOB), ITEM, 0x1517, Layer.Pants, 0).Should().BeNull()
                            };

                            // asserted inside the operation: rejected before any change
                            if (throws)
                            {
                                nested.Should().Throw<InvalidOperationException>();
                            }
                            else
                            {
                                nested();
                            }

                            Item it = world.Items.Get(ITEM);
                            (it.Container, it.Layer, it.Graphic).Should().Be((MOB, Layer.Shirt, (ushort)0x1515), "a rejected operation changes nothing");
                            ChildSerials(world.Mobiles.Get(MOB)).Should().Equal(WORN_BEFORE, ITEM, WORN_AFTER);
                        }
                    };

                    world.Writer.UpsertWorldItem(0x4000_7050, 0xEED, 0, 1, 1010, 1010, 0, Direction.North, 0, Flags.None, 0, out _, out _);
                    world.Ecs.CommitObserver = null;

                    ran.Should().BeTrue();
                    world.Writer.Violations.Should().Be(1);
                }
            }
        }
    }

    // T1: CUO_FUNNEL_CHECK. FunnelCheck's counters are process-wide, so these tests run alone.
    [CollectionDefinition(nameof(FunnelCheckCollection), DisableParallelization = true)]
    public class FunnelCheckCollection { }

    [Collection(nameof(FunnelCheckCollection))]
    public class FunnelCheckTests : IDisposable
    {
        private const uint MOB = 0x0000_3100, ITEM = 0x4000_7100;

        public FunnelCheckTests()
        {
            FunnelCheck.Reset();
            FunnelCheck.Enabled = true;
        }

        public void Dispose()
        {
            FunnelCheck.Enabled = false;
            FunnelCheck.Reset();
        }

        [Fact]
        public void Direct_Writes_To_Held_Objects_Are_Counted_Per_Field()
        {
            if (!FunnelCheck.Available)
            {
                return; // -p:CuoDevTools=false: the check is compiled out
            }

            var world = new World();
            Item it = world.GetOrCreateItem(ITEM);
            Mobile m = world.GetOrCreateMobile(MOB);
            FunnelCheck.Reset();

            it.Graphic = 1;
            it.Hue = 2;
            it.Amount = 3;
            m.X = 4;
            m.NotorietyFlag = NotorietyFlag.Ally;

            FunnelCheck.Count(FunnelField.Graphic).Should().Be(1);
            FunnelCheck.Count(FunnelField.Hue).Should().Be(1);
            FunnelCheck.Count(FunnelField.Amount).Should().Be(1);
            FunnelCheck.Count(FunnelField.X).Should().Be(1);
            FunnelCheck.Count(FunnelField.NotorietyFlag).Should().Be(1);
            FunnelCheck.Total.Should().Be(5);
            FunnelCheck.Sites.Keys.Should().Contain(k => k.Item1 == FunnelField.Graphic && k.Item2.Contains(nameof(Direct_Writes_To_Held_Objects_Are_Counted_Per_Field)),
                "the caller is the test method, not the setter");
        }

        [Fact]
        public void All_Four_Offset_Setters_Are_Covered()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            FunnelCheck.Reset();

            m.Offset = default;
            m.OffsetX = 1;
            m.OffsetY += 2;
            m.OffsetZ = 4;

            FunnelCheck.Count(FunnelField.Offset).Should().Be(1);
            FunnelCheck.Count(FunnelField.OffsetX).Should().Be(1);
            FunnelCheck.Count(FunnelField.OffsetY).Should().Be(1);
            FunnelCheck.Count(FunnelField.OffsetZ).Should().Be(1);
        }

        [Fact]
        public void Dev_Write_Scope_Exempts_Inside_Nests_And_Reports_Outside()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            FunnelCheck.Reset();

            using (FunnelCheck.DevWrite())
            {
                m.X = 10;

                using (FunnelCheck.DevWrite())
                {
                    m.Y = 11;
                }

                // still inside the outer scope after the inner one closed
                m.Z = 12;
            }

            if (!FunnelCheck.Available)
            {
                return; // -p:CuoDevTools=false: the scope is empty and the check is compiled out
            }

            FunnelCheck.Total.Should().Be(0);
            FunnelCheck.DevExempt.Should().Be(3);
            FunnelCheck.Sites.Should().BeEmpty();

            m.X = 13;

            FunnelCheck.Total.Should().Be(1);
            FunnelCheck.Count(FunnelField.X).Should().Be(1);
            FunnelCheck.DevExempt.Should().Be(3);
            FunnelCheck.Sites.Keys.Should().Contain(k => k.Item1 == FunnelField.X && k.Item2.Contains(nameof(Dev_Write_Scope_Exempts_Inside_Nests_And_Reports_Outside)));
        }

        [Fact]
        public void The_Mutation_Methods_Are_Covered()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            m.X = 100;
            m.Y = 100;
            FunnelCheck.Reset();

            // SA poison: a private field behind a method
            m.SetSAPoison(true);
            FunnelCheck.Count(FunnelField.IsSAPoisoned).Should().Be(1);

            // the step queue: a second step on a populated queue writes no property, only the queue
            m.EnqueueStep(101, 100, 0, Direction.East, false).Should().BeTrue();
            long steps = FunnelCheck.Count(FunnelField.Steps);
            steps.Should().BeGreaterThan(0);
            long props = FunnelCheck.Total - steps - FunnelCheck.Count(FunnelField.AnimationState);
            m.EnqueueStep(102, 100, 0, Direction.East, false).Should().BeTrue();
            FunnelCheck.Count(FunnelField.Steps).Should().BeGreaterThan(steps);
            m.ClearSteps();
            FunnelCheck.Count(FunnelField.Steps).Should().BeGreaterThan(steps + 1);
            props.Should().BeGreaterThanOrEqualTo(0);

            // the private animation timer
            long anim = FunnelCheck.Count(FunnelField.AnimationState);
            m.SetAnimation(3, 1, 2);
            FunnelCheck.Count(FunnelField.AnimationState).Should().BeGreaterThan(anim);
        }

        [Fact]
        public void A_ReKey_Serial_Write_Is_Counted()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            // 0xAF (DisplayDeath): the dictionary moves to the new key first, then the Serial setter runs
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            FunnelCheck.Reset();

            uint newSerial = MOB | 0x8000_0000;
            world.Mobiles.Remove(m.Serial);
            world.Mobiles[newSerial] = m;
            m.Serial = newSerial;

            FunnelCheck.Count(FunnelField.Serial).Should().Be(1);
        }

        [Fact]
        public void The_World_Item_Operations_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            var world = new World();
            Item bag = world.GetOrCreateItem(0x4000_7101);
            Item it = world.GetOrCreateItem(ITEM);
            it.Container = bag.Serial;
            bag.PushToBack(it);
            world.Ecs.Sync(it);
            Item child = world.GetOrCreateItem(0x4000_7103);
            child.Container = ITEM;
            it.PushToBack(child);
            world.Ecs.Sync(child);
            FunnelCheck.Reset();

            Item a = world.Writer.UpsertWorldItem(ITEM, 0x2006, 0, 0x190, 1000, 1000, 0, Direction.Running | Direction.South, 0x455, Flags.None, 0, out _, out WorldWriter.Placement pa);
            world.Writer.PlaceWorldItem(pa);
            Item b = world.Writer.UpsertWorldItem(0x4000_7102, 0xEED, 1, 5, 1001, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement pb);
            world.Writer.PlaceWorldItem(pb);
            FunnelCheck.Total.Should().Be(0);

            // a removal owns its subtree (the contents' Container writes)
            world.Writer.RemoveWorldItem(a, out _);
            FunnelCheck.Total.Should().Be(0);

            // outside an operation the same writes count again
            b.Hue = 3;
            FunnelCheck.Total.Should().Be(1);
        }

        [Fact]
        public void The_Clear_And_Deny_Operations_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            var world = new World();
            Mobile m = world.GetOrCreateMobile(0x0000_3100);
            Item bag = world.GetOrCreateItem(0x4000_7101);
            Item it = world.GetOrCreateItem(ITEM);
            it.Container = bag.Serial;
            bag.PushToBack(it);
            world.Ecs.Sync(it);
            Item child = world.GetOrCreateItem(0x4000_7103);
            child.Container = ITEM;
            it.PushToBack(child);
            world.Ecs.Sync(child);
            Item worn = world.GetOrCreateItem(0x4000_7104);
            worn.Container = m.Serial;
            worn.Layer = Layer.Shirt;
            m.PushToBack(worn);
            world.Ecs.Sync(worn);
            FunnelCheck.Reset();

            // the bulk clear owns the removed subtrees (their Container writes)
            world.Writer.ClearContainer(bag.Serial, false, itemOnly: false);
            FunnelCheck.Total.Should().Be(0);

            // each deny branch: paperdoll, terrain, the removal (of an item holding contents)
            // (each asserted right after its branch: the fixture setup below makes direct writes, and the Reset clears them)
            world.Writer.ReturnHeldItem(worn.Serial, 0x1517, 0x21, 1, Flags.None, Layer.Shirt, 1, 1, 0, m.Serial, out _, out WorldWriter.HeldItemReturn r1);
            r1.Should().Be(WorldWriter.HeldItemReturn.Paperdoll);
            FunnelCheck.Total.Should().Be(0, "the paperdoll branch");
            world.Writer.ReturnHeldItem(worn.Serial, 0x1517, 0x21, 1, Flags.None, Layer.Invalid, 1000, 1000, 0, 0xFFFF_FFFF, out _, out WorldWriter.HeldItemReturn r2);
            r2.Should().Be(WorldWriter.HeldItemReturn.Terrain);
            FunnelCheck.Total.Should().Be(0, "the terrain branch");
            Item box = world.GetOrCreateItem(0x4000_7105);
            Item held = world.GetOrCreateItem(0x4000_7106);
            held.Container = box.Serial;
            box.PushToBack(held);
            Item inner = world.GetOrCreateItem(0x4000_7107);
            inner.Container = held.Serial;
            held.PushToBack(inner);
            FunnelCheck.Reset();
            world.Writer.ReturnHeldItem(held.Serial, 0x1517, 0, 1, Flags.None, Layer.Shirt, 1, 1, 0, box.Serial, out _, out _);
            FunnelCheck.Total.Should().Be(0);
            world.Items.Get(inner.Serial).Should().BeNull();
        }

        [Fact]
        public void The_Equipment_Operations_Make_No_Direct_Writes()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            var world = new World();
            Mobile m = world.GetOrCreateMobile(0x0000_3100);
            Item bag = world.GetOrCreateItem(0x4000_7101);
            Item worn = world.GetOrCreateItem(0x4000_7104);
            worn.Graphic = 0x1515;
            worn.Container = m.Serial;
            worn.Layer = Layer.Shirt;
            m.PushToBack(worn);
            world.Ecs.Sync(worn);
            Item child = world.GetOrCreateItem(0x4000_7103);
            child.Container = worn.Serial;
            worn.PushToBack(child);
            world.Ecs.Sync(child);
            FunnelCheck.Reset();

            // 0x2E: a re-equip (the unlink), into an item, a new item
            world.Writer.Equip(worn.Serial, 0x1516, Layer.Shirt, m.Serial, 0x21, out _, out _).Should().BeSameAs(worn);
            world.Writer.Equip(worn.Serial, 0x1516, Layer.ShopBuy, bag.Serial, 0, out _, out _);
            world.Writer.Equip(0x4000_7108, 0x1517, Layer.Pants, m.Serial, 0, out _, out _).Should().NotBeNull();
            FunnelCheck.Total.Should().Be(0, "0x2E");

            // 0x78: the clear (owns the removed subtrees: worn's contents moved with it into the bag, the new pants go), then
            // the entries, one a corpse graphic with the running bit
            world.Writer.ClearWornItems(m.Serial);
            world.Items.Get(0x4000_7108).Should().BeNull();
            world.Writer.WearItem(m, worn.Serial, 0x1515, Layer.Shirt, 0).Should().BeSameAs(worn);
            world.Writer.WearItem(m, 0x4000_7109, 0x2006, Layer.Cloak, 0).Should().NotBeNull();
            FunnelCheck.Total.Should().Be(0, "0x78");
            world.Writer.ClearWornItems(m.Serial);
            FunnelCheck.Total.Should().Be(0, "the clear of worn items with contents");
            world.Items.Get(child.Serial).Should().BeNull();
        }

        [Fact]
        public void Detached_And_Stale_Objects_Are_Not_World_State()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            var world = new World();
            FunnelCheck.Reset();

            // the house placement preview and the character-creation mobiles: objects no World dictionary holds
            Item preview = Item.Create(world, 0);
            preview.Graphic = 0x4000;
            preview.X = 1;
            Mobile fake = Mobile.Create(world, 0x0000_3200);
            fake.Graphic = 0x190;
            fake.IsFemale = true;

            // a stale instance whose serial World now holds with another object
            Item stale = Item.Create(world, ITEM);
            world.GetOrCreateItem(ITEM);
            FunnelCheck.Reset();
            stale.Hue = 1;

            FunnelCheck.Total.Should().Be(0);
        }

        [Fact]
        public void Off_Counts_Nothing()
        {
            var world = new World();
            Item it = world.GetOrCreateItem(ITEM);
            FunnelCheck.Enabled = false;
            FunnelCheck.Reset();

            it.Graphic = 1;
            it.OffsetX = 1;

            FunnelCheck.Total.Should().Be(0);
        }
    }
}
