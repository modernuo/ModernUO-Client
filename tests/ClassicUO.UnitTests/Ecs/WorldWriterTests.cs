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

        // ---- the release build's path after a violation (ThrowOnViolation false): an error log, then the pre-T1 code

        [Fact]
        public void Release_A_Nested_Upsert_Takes_The_Pre_T1_Path()
        {
            World oldWorld = Build(Start.InBag), newWorld = Build(Start.InBag);
            newWorld.Writer.ThrowOnViolation = false;
            Item replacement = Item.Create(newWorld, BAG);
            Packet p = Packets[3];

            OldUpsert(oldWorld, p);
            newWorld.Ecs.BeginFold(replacement); // an operation is open
            NewUpsert(newWorld, p, out bool created);
            newWorld.Ecs.EndFold(replacement);

            created.Should().BeFalse();
            newWorld.Writer.Violations.Should().Be(1);
            newWorld.Writer.OutstandingPlacements.Should().Be(0, "a pre-T1 upsert issues no outstanding placement");
            AssertSameWorld(oldWorld, newWorld);
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

        // ---- Codex audit round 3, #1: a fallback while the fold of its own item is open (the hook syncs the old writes rely
        // on are folded): the fold's end syncs the item from its cache

        [Theory]
        [InlineData(Start.Ground, 0)]
        [InlineData(Start.InBag, 3)]
        [InlineData(Start.Worn, 1)]
        [InlineData(Start.GroundCorpse, 4)]
        public void Release_A_Fallback_Inside_The_Fold_Of_Its_Own_Item_Reaches_The_Mirror(Start start, int packet)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            newWorld.Writer.ThrowOnViolation = false;
            Item it = newWorld.Items.Get(ITEM);
            Packet p = Packets[packet];

            OldUpsert(oldWorld, p);
            newWorld.Ecs.BeginFold(it); // an operation on this very instance is open
            NewUpsert(newWorld, p, out bool created).Should().BeSameAs(it);
            newWorld.Ecs.EndFold(it);

            created.Should().BeFalse();
            newWorld.Writer.Violations.Should().Be(1);
            newWorld.Ecs.IsFolding.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void Release_A_Nested_Upsert_Inside_A_Real_Operation_Leaves_The_Mirror_Equal_To_The_Cache()
        {
            // a real nesting: an upsert of the same item started from inside the outer upsert's commit (before its writes). The
            // nested one takes the pre-T1 path under the outer fold; the outer operation's writes come after it, as a pre-T1
            // handler's continuation would; when the outer fold ends, the mirror holds what the cache holds.
            World world = Build(Start.InBag);
            world.Writer.ThrowOnViolation = false;
            Packet outer = Packets[1], inner = Packets[2];
            world.Ecs.CommitObserver = (_, _) =>
            {
                world.Ecs.CommitObserver = null;
                NewUpsert(world, inner, out _);
            };

            Item it = NewUpsert(world, outer, out _);

            world.Writer.Violations.Should().Be(1);
            world.Writer.InScope.Should().BeFalse();
            world.Ecs.IsFolding.Should().BeFalse();
            world.Writer.OutstandingPlacements.Should().Be(0);
            it.Should().BeSameAs(world.Items.Get(ITEM));
            (it.X, it.Y, it.Z, it.Flags, it.Amount).Should().Be((outer.X, outer.Y, outer.Z, outer.Flags, outer.Count), "the outer operation wrote last");

            world.Ecs.Mirror.TryGet(ITEM, out MirrorRecord mirrored).Should().BeTrue();
            world.Ecs.Sync(it); // the record of what the cache holds
            world.Ecs.Mirror.TryGet(ITEM, out MirrorRecord fromCache);
            mirrored.Should().BeEquivalentTo(fromCache, "the mirror holds what the cache holds");
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

        [Fact]
        public void Release_A_Nested_PlaceInContainer_Takes_The_Pre_T1_Path()
        {
            World oldWorld = BuildWithCorpse(Start.Worn), newWorld = BuildWithCorpse(Start.Worn);
            newWorld.Writer.ThrowOnViolation = false;
            Item replacement = Item.Create(newWorld, BAG);

            OldAddToContainer(oldWorld, ITEM, 0x1515, 2, 3, 4, 0x21, BAG);
            newWorld.Ecs.BeginFold(replacement);
            newWorld.Writer.PlaceInContainer(ITEM, 0x1515, 2, 3, 4, 0x21, BAG, out _);
            newWorld.Ecs.EndFold(replacement);

            newWorld.Writer.Violations.Should().Be(1);
            AssertSameWorld(oldWorld, newWorld);
        }

        [Fact]
        public void Release_A_PlaceInContainer_Inside_The_Fold_Of_Its_Own_Item_Reaches_The_Mirror()
        {
            // Codex audit round 3, #1: the same container, so the fallback reuses the folded instance
            World oldWorld = Build(Start.InBag), newWorld = Build(Start.InBag);
            newWorld.Writer.ThrowOnViolation = false;
            Item it = newWorld.Items.Get(ITEM);

            OldAddToContainer(oldWorld, ITEM, 0x1516, 2, 3, 4, 0x21, BAG);
            newWorld.Ecs.BeginFold(it);
            newWorld.Writer.PlaceInContainer(ITEM, 0x1516, 2, 3, 4, 0x21, BAG, out _).Should().BeSameAs(it);
            newWorld.Ecs.EndFold(it);

            newWorld.Writer.Violations.Should().Be(1);
            AssertSameWorld(oldWorld, newWorld);
        }

        [Theory]
        [InlineData(BAG, false)]
        [InlineData(CORPSEBOX, false)]
        [InlineData(BAG, true)]
        public void PlaceInContainer_Of_A_Live_Mobile_Serial_Equals_The_Old_Path(uint containerSerial, bool fallback)
        {
            // Codex audit round 3, #3: a live mobile holding the incoming serial is removed with its equipment and the
            // equipment's contents, by the operation and by its release fallback alike
            World oldWorld = BuildWithCorpse(Start.Worn), newWorld = BuildWithCorpse(Start.Worn);
            Item replacement = Item.Create(newWorld, BAG);

            if (fallback)
            {
                newWorld.Writer.ThrowOnViolation = false;
                newWorld.Ecs.BeginFold(replacement);
            }

            OldAddToContainer(oldWorld, MOB, 0x1515, 1, 2, 3, 0x21, containerSerial);
            newWorld.Writer.PlaceInContainer(MOB, 0x1515, 1, 2, 3, 0x21, containerSerial, out _);

            if (fallback)
            {
                newWorld.Ecs.EndFold(replacement);
                newWorld.Writer.Violations.Should().Be(1);
            }

            oldWorld.Mobiles.Get(MOB).Should().BeNull("the old handler removed the mobile");
            oldWorld.Items.Get(ITEM).Should().BeNull("with its equipment");
            AssertSameWorld(oldWorld, newWorld);
            AssertSameCorpseBox(oldWorld, newWorld);
            (newWorld.Items.Get(MOB) == null).Should().Be(oldWorld.Items.Get(MOB) == null);
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

        private static void AssertSameWorld(World a, World b)
        {
            a.Items.Count.Should().Be(b.Items.Count);
            b.Mobiles.Count.Should().Be(a.Mobiles.Count);
            (b.Mobiles.Get(MOB) == null).Should().Be(a.Mobiles.Get(MOB) == null, "the mobile exists in both or neither");
            b.ActiveItems.Select(i => i.Serial).Should().Equal(a.ActiveItems.Select(i => i.Serial), "the active-items list, in order");

            foreach (uint serial in new[] { ITEM, CHILD, GRANDCHILD, BAG })
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

            foreach (uint serial in new[] { ITEM, CHILD, GRANDCHILD, BAG, MOB })
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

            Serials(mb.GetEquipment(MOB)).Should().Equal(Serials(ma.GetEquipment(MOB)), "the equipment index, in order");
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
