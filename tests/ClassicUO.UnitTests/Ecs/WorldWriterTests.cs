using System;
using System.Collections.Generic;
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
    // PacketHandlers.UpdateGameObject on one World and through the funnel on another,
    // from identical starting states, and compares every item property, the mirror record and tags, the wearers' equipment
    // dirty flags, the active-items list and the mirror's counters. Multis (type 2) are not covered: CheckGraphicChange loads
    // the multi from the UO files. RemoveWorldItem (0x1D) is not covered either: Entity.Destroy needs a running client
    // (GameActions.SendCloseStatus reads Client.Game); it makes the same calls in the same order as DeleteObject did, and the
    // client runs check it (-ecscheck, CUO_ECS_PARITY).
    public class WorldWriterTests
    {
        private const uint MOB = 0x0000_3000, BAG = 0x4000_7000, ITEM = 0x4000_7001, CHILD = 0x4000_7002;

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

        // the item branch of UpdateGameObject before T1 (UI and cursor side effects left out: they write no world state)
        internal static Item OldUpsert(World world, in Packet p)
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

            if (item.OnGround)
            {
                item.SetInWorldTile(item.X, item.Y, item.Z);
            }

            return item;
        }

        private static Item NewUpsert(World world, in Packet p, out bool created) =>
            world.Writer.UpsertWorldItem(world.Get(p.Serial), p.Serial, p.Graphic, p.GraphicInc, p.Count, p.X, p.Y, p.Z, p.Direction, p.Hue, p.Flags, p.Type, out created);

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

            if (start == Start.None)
            {
                return world;
            }

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

            // a child inside ITEM, so a removal has contents to remove
            Item child = world.GetOrCreateItem(CHILD);
            child.Graphic = 0xF7A;
            child.Container = ITEM;
            it.PushToBack(child);
            world.Ecs.Sync(child);

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
            Item b = NewUpsert(newWorld, p, out bool created);

            created.Should().Be(start == Start.None);
            b.Should().BeSameAs(newWorld.Items.Get(ITEM));
            AssertSameWorld(oldWorld, newWorld);

            // a second identical packet (a resend) on top
            OldUpsert(oldWorld, p);
            NewUpsert(newWorld, p, out created);
            created.Should().BeFalse();
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

        [Fact]
        public void The_Operation_Writes_The_Mirror_Once()
        {
            // the hooks (GetOrCreateItem, RemoveItemFromContainer, the Direction setter, AddToTile) fold into one commit
            World world = Build(Start.InBag);
            world.Ecs.Mirror.TryGetEntity(ITEM, out EcsEntity before).Should().BeTrue();

            NewUpsert(world, Packets[0], out _);

            world.Ecs.Mirror.TryGetEntity(ITEM, out EcsEntity after).Should().BeTrue();
            after.Id.Should().Be(before.Id, "an existing entity is updated in place");
            world.Writer.InScope.Should().BeFalse();
            world.Items.Get(ITEM).Direction = Direction.East; // after the operation, the hooks sync again
            world.Ecs.Mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue();
            r.Facing.Should().Be((byte)Direction.East);
        }

        private static void AssertSameWorld(World a, World b)
        {
            a.Items.Count.Should().Be(b.Items.Count);
            a.ActiveItemCount.Should().Be(b.ActiveItemCount, "the corpse queue follows the same Syncs");

            foreach (uint serial in new[] { ITEM, CHILD, BAG })
            {
                Item x = a.Items.Get(serial), y = b.Items.Get(serial);
                (x == null).Should().Be(y == null, $"0x{serial:X8} exists in both or neither");

                if (x != null)
                {
                    AssertSameItem(x, y);
                }
            }

            WorldMirror ma = a.Ecs.Mirror, mb = b.Ecs.Mirror;
            mb.Count.Should().Be(ma.Count);
            mb.ItemCount.Should().Be(ma.ItemCount);
            mb.MobileCount.Should().Be(ma.MobileCount);
            mb.Epoch.Should().Be(ma.Epoch);

            foreach (uint serial in new[] { ITEM, CHILD, BAG, MOB })
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

            mb.GetContents(BAG).Count.Should().Be(ma.GetContents(BAG).Count);
            mb.GetEquipment(MOB).Count.Should().Be(ma.GetEquipment(MOB).Count);
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
            (y.Items == null).Should().Be(x.Items == null, "child list");
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
        public void Writes_In_Scope_Are_Not_Counted()
        {
            if (!FunnelCheck.Available)
            {
                return;
            }

            var world = new World();
            Item it = world.GetOrCreateItem(ITEM);
            FunnelCheck.Reset();

            using (world.Writer.Enter())
            {
                world.Writer.InScope.Should().BeTrue();
                it.Graphic = 1;
                it.OffsetZ = 2;

                using (world.Writer.Enter())
                {
                    it.Hue = 3;
                }

                world.Writer.InScope.Should().BeTrue("scopes nest");
                it.X = 4;
            }

            world.Writer.InScope.Should().BeFalse();
            FunnelCheck.Total.Should().Be(0);

            it.Graphic = 5;
            FunnelCheck.Total.Should().Be(1);
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
            FunnelCheck.Reset();

            world.Writer.UpsertWorldItem(world.Get(ITEM), ITEM, 0x2006, 0, 0x190, 1000, 1000, 0, Direction.Running | Direction.South, 0x455, Flags.None, 0, out _);
            world.Writer.UpsertWorldItem(null, 0x4000_7102, 0xEED, 1, 5, 1001, 1000, 0, Direction.North, 0, Flags.None, 0, out _);

            FunnelCheck.Total.Should().Be(0);
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
