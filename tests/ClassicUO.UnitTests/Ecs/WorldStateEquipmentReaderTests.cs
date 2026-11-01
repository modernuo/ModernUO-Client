using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T3a: the equipment readers behind ecs_read_equipment. Every reader goes through Entity.FindItemByLayer (IsMounted,
    // IsDrivingBoat, GetAnimGroupInput's hands, UpdateAbilities, PaperdollOrder.GraphicsFromEntity/BuildInWorld, the paperdoll,
    // Legacy's MobileView) plus MobileView's guard (WorldState.HasWornItems). On a World that holds its objects, every read is
    // the same object with the switch off and on, after every kind of change; a change behind the funnel shows which source
    // a reader takes; an instance World does not hold reads its own list. (GraphicsFromEntity, GetAnimGroupInput and
    // UpdateAbilities read the tile data, which the tests do not load: they are covered through FindItemByLayer here and by
    // the gate's -ecscheck hands/mount compare and the paperdoll shots.)
    [Collection("PlayerWorld")]
    public class WorldStateEquipmentReaderTests
    {
        private const uint MOB = 0x0000_3900, DEAD = MOB | 0x8000_0000, PACK = 0x4000_7900, SHIRT = 0x4000_7904, HELM = 0x4000_7905, WEAR = 0x4000_790A;
        private const uint MOUNT = 0x4000_7970, TILLER = 0x4000_7971, HELM2 = 0x4000_7972, NEWSHIRT = 0x4000_7973, LATE = 0x0000_3970, ORPHAN = 0x4000_7974, PREVIEW_SHIRT = 0x4000_7975;

        private static readonly StaticTiles[] _tiles = new StaticTiles[0x10000];

        private static void BothWays(World world, Action check)
        {
            bool? before = world.State.ReadEquipmentOverride;

            foreach (bool read in new[] { false, true })
            {
                world.State.ReadEquipmentOverride = read;
                check();
            }

            world.State.ReadEquipmentOverride = before;
        }

        // every mobile World holds: every layer's item (the same object), the guard, IsMounted, IsDrivingBoat, the
        // system's IsMounted, with the switch off and on
        internal static void AllReadersEqual(World world)
        {
            foreach (Mobile m in world.Mobiles.Values.ToList())
            {
                var off = new List<object>();
                var on = new List<object>();

                foreach (bool read in new[] { false, true })
                {
                    world.State.ReadEquipmentOverride = read;
                    List<object> got = read ? on : off;

                    for (int l = 0; l <= 0x1E; l++)
                    {
                        got.Add(m.FindItemByLayer((Layer)l));
                    }

                    got.Add(world.State.HasWornItems(m));
                    got.Add(m.IsMounted);
                    got.Add(m.IsDrivingBoat);
                    got.Add(MobileAnimationSystem.IsMounted(m, _tiles));
                }

                world.State.ReadEquipmentOverride = null;

                for (int i = 0; i < off.Count; i++)
                {
                    on[i].Should().BeSameAs(off[i] is bool ? on[i] : off[i], $"reader {i} of 0x{m.Serial:X8}");

                    if (off[i] is bool b)
                    {
                        on[i].Should().Be(b, $"reader {i} of 0x{m.Serial:X8}");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(WorldStateCheckTests.Changes), MemberType = typeof(WorldStateCheckTests))]
        public void Every_Reader_Is_The_Same_Both_Ways_After_Every_Change(WorldStateContentsTests.Change change)
        {
            World world = WorldStateContentsTests.Build();
            AllReadersEqual(world);

            WorldStateContentsTests.Apply(world, change);

            AllReadersEqual(world);
        }

        [Fact]
        public void The_Switch_Is_On_By_Default()
        {
            new ClassicUO.Configuration.Settings().EcsReadEquipment.Should().BeTrue();
        }

        [Fact]
        public void Mount_And_Dismount_Agree_Both_Ways_And_With_The_Slots()
        {
            World world = WorldStateContentsTests.Build();
            Mobile m = world.Mobiles.Get(MOB);

            // an ethereal unicorn (its animation graphic needs no tile data), then a boat's tiller, then nothing
            world.Writer.Equip(MOUNT, 0x3E9B, Layer.Mount, MOB, 0, out _, out _);
            AllReadersEqual(world);
            BothWays(world, () =>
            {
                m.IsMounted.Should().BeTrue();
                SlotsMounted(world, m).Should().BeTrue("EquipmentSlots.Mounted agrees with IsMounted");
            });

            world.Writer.PlaceInContainer(MOUNT, 0x3E9B, 1, 1, 1, 0, PACK, out _);
            AllReadersEqual(world);
            BothWays(world, () =>
            {
                m.IsMounted.Should().BeFalse();
                m.FindItemByLayer(Layer.Mount).Should().BeNull();
                SlotsMounted(world, m).Should().BeFalse();
            });

            world.Writer.Equip(TILLER, 0x3E96, Layer.Mount, MOB, 0, out _, out _);
            AllReadersEqual(world);
            BothWays(world, () =>
            {
                m.IsDrivingBoat.Should().BeTrue();
                m.IsMounted.Should().BeFalse();
                SlotsMounted(world, m).Should().BeFalse();
            });

            world.Writer.RemoveWorldItem(world.Items.Get(TILLER), out _);
            world.Writer.Equip(MOUNT, 0x3E9B, Layer.Mount, MOB, 0, out _, out _);
            AllReadersEqual(world);
            BothWays(world, () => m.IsMounted.Should().BeTrue());
        }

        private static bool SlotsMounted(World world, Mobile m)
        {
            world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e).Should().BeTrue();
            var sys = new MobileAnimationSystem(world.Ecs.Mirror);
            var s = new AnimationState();
            sys.Rebuild(e, in s, m, _tiles, new Dictionary<ushort, Dictionary<ushort, EquipConvData>>(), true);

            return e.GetComponent<EquipmentSlots>().Mounted;
        }

        [Fact]
        public void A_Change_Behind_The_Funnel_Shows_The_Source_Each_Way()
        {
            World world = WorldStateContentsTests.Build();
            Mobile m = world.Mobiles.Get(MOB);

            // the mirror detaches the helmet and removes the robe; the list still has both
            world.Ecs.Mirror.DetachChild(HELM);
            world.Ecs.Mirror.Remove(WEAR);

            world.State.ReadEquipmentOverride = false;
            m.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM);
            m.FindItemByLayer(Layer.Robe).Serial.Should().Be(WEAR);

            world.State.ReadEquipmentOverride = true;
            m.FindItemByLayer(Layer.Helmet).Should().BeNull("the ECS's");
            m.FindItemByLayer(Layer.Robe).Should().BeNull();
            m.FindItemByLayer(Layer.Shirt).Serial.Should().Be(SHIRT);

            // the list emptied behind the funnel: the guard follows the ECS with the switch on, the list with it off
            for (LinkedObject i = m.Items; i != null;)
            {
                LinkedObject next = i.Next;
                m.Remove(i);
                i = next;
            }

            world.State.HasWornItems(m).Should().BeTrue("the ECS still records the shirt");
            world.State.ReadEquipmentOverride = false;
            world.State.HasWornItems(m).Should().BeFalse("the list is empty");
        }

        [Fact]
        public void The_Appearance_Preview_Outside_The_World_Reads_Its_Own_List()
        {
            // CreateCharAppearanceGump's preview: a detached PlayerMobile (serial 1) with detached clothes; serial 1 is also a
            // real mobile here, wearing a helmet through the funnel
            World world = WorldStateContentsTests.Build();
            world.GetOrCreateMobile(1);
            world.Writer.Equip(HELM2, 0x1409, Layer.Helmet, 1, 0, out _, out _);

            var preview = new Mobile(world, 1); // (the gump makes a PlayerMobile, which needs the client loaded; the read is Mobile's)
            Item shirt = Item.Create(world, PREVIEW_SHIRT);
            shirt.Graphic = 0x1517;
            shirt.Layer = Layer.Shirt;
            preview.PushToBack(shirt);

            BothWays(world, () =>
            {
                preview.FindItemByLayer(Layer.Shirt).Should().BeSameAs(shirt);
                preview.FindItemByLayer(Layer.Helmet).Should().BeNull("never the real serial 1's helmet");
                world.State.HasWornItems(preview).Should().BeTrue();
                world.Mobiles.Get(1).FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM2);
                world.Mobiles.Get(1).FindItemByLayer(Layer.Shirt).Should().BeNull();
            });

            var bare = new Mobile(world, 1);
            BothWays(world, () => world.State.HasWornItems(bare).Should().BeFalse("its own empty list"));
        }

        [Fact]
        public void A_Replaced_Mobile_Reads_Its_Own_List_Even_After_A_Cached_Read()
        {
            World world = WorldStateContentsTests.Build();
            Mobile old = world.Mobiles.Get(MOB);
            world.State.ReadEquipmentOverride = true;
            old.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM, "a first read caches the verified record");

            world.Writer.RemoveMobile(old);
            Mobile next = world.GetOrCreateMobile(MOB);
            next.Should().NotBeSameAs(old);
            world.Writer.Equip(HELM2, 0x1409, Layer.Helmet, MOB, 0, out _, out _);

            BothWays(world, () =>
            {
                next.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM2);
                old.FindItemByLayer(Layer.Helmet).Should().BeSameAs(WornLayersTests.ListFind(old, Layer.Helmet), "its own list, never the new record");
                (old.FindItemByLayer(Layer.Helmet)?.Serial ?? 0).Should().NotBe(HELM2);
            });
        }

        [Fact]
        public void A_Bare_Mobile_Removed_Reads_Its_Own_List_After_A_Cached_Read()
        {
            // a removal that changes no slot (the mobile wore nothing) still ends the verified identity (the mirror's epoch)
            World world = WorldStateContentsTests.Build();
            Mobile bare = world.GetOrCreateMobile(LATE);
            world.State.ReadEquipmentOverride = true;
            bare.FindItemByLayer(Layer.Shirt).Should().BeNull();
            long writes = world.Ecs.Mirror.WornWrites;

            world.Writer.RemoveMobile(bare);
            world.Ecs.Mirror.WornWrites.Should().Be(writes, "no slot changed");
            Item stale = Item.Create(world, PREVIEW_SHIRT);
            stale.Layer = Layer.Shirt;
            bare.PushToBack(stale);

            bare.FindItemByLayer(Layer.Shirt).Should().BeSameAs(stale, "not held any more: its own list");
            world.State.HasWornItems(bare).Should().BeTrue();
        }

        [Fact]
        public void A_Slot_Change_Elsewhere_Refreshes_Without_Refilling()
        {
            World world = WorldStateContentsTests.Build();
            Mobile m = world.Mobiles.Get(MOB);
            world.GetOrCreateMobile(LATE);
            world.State.ReadEquipmentOverride = true;
            m.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM);
            long fills = world.State.CacheFills, refreshes = world.State.CacheRefreshes;

            world.Writer.Equip(NEWSHIRT, 0x1517, Layer.Shirt, LATE, 0, out _, out _);
            m.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM);

            (world.State.CacheRefreshes - refreshes).Should().Be(1, "the mirror's slot writes moved");
            (world.State.CacheFills - fills).Should().Be(0, "this mobile's version did not");
            world.Mobiles.Get(LATE).FindItemByLayer(Layer.Shirt).Serial.Should().Be(NEWSHIRT);
        }

        [Fact]
        public void An_Orphan_Is_Not_Worn_Either_Way()
        {
            World world = WorldStateContentsTests.Build();
            world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out _, out _);
            Mobile late = world.GetOrCreateMobile(LATE);

            BothWays(world, () =>
            {
                late.FindItemByLayer(Layer.Shirt).Should().BeNull();
                world.State.HasWornItems(late).Should().BeFalse();
            });

            world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out _, out _);
            BothWays(world, () => late.FindItemByLayer(Layer.Shirt).Serial.Should().Be(ORPHAN));
            AllReadersEqual(world);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void A_Rekey_Onto_An_Occupied_Dead_Serial_Wears_Only_The_Incoming_Items(bool warm, bool incomingWorn)
        {
            World world = WorldStateContentsTests.Build();

            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);
            Mobile displaced = world.Mobiles.Get(DEAD);

            if (warm)
            {
                world.State.ReadEquipmentOverride = true;
                displaced.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM);
                world.State.ReadEquipmentOverride = null;
            }

            world.GetOrCreateMobile(MOB);

            if (incomingWorn)
            {
                world.Writer.Equip(NEWSHIRT, 0x1517, Layer.Shirt, MOB, 0, out _, out _);
            }

            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);
            Mobile dead = world.Mobiles.Get(DEAD);
            dead.Should().NotBeSameAs(displaced);

            BothWays(world, () =>
            {
                dead.FindItemByLayer(Layer.Helmet).Should().BeNull("the displaced mobile's helmet is not the new dead mobile's");
                (dead.FindItemByLayer(Layer.Shirt)?.Serial ?? 0).Should().Be(incomingWorn ? NEWSHIRT : 0);
                world.State.HasWornItems(dead).Should().Be(incomingWorn);
            });

            for (int l = 1; l <= WornLayers.COUNT; l++)
            {
                world.Ecs.Mirror.Worn(DEAD, (byte)l).Should().Be(l == (int)Layer.Shirt && incomingWorn ? NEWSHIRT : 0);
            }

            AllReadersEqual(world);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_Backpack_Moved_To_Another_Wearer_Leaves_Its_First_Wearers_List(bool first)
        {
            // Behaviour fix (Codex audit P2 #2): 0x2E keeps a backpack's link (no unlink for Layer.Backpack); as the old wearer's
            // first child it stayed that wearer's list head after the new wearer's PushToBack, with the old wearer's other
            // children cut off behind it. Legacy ClassicUO and the base had the same quirk; the fix unlinks it from the old list.
            const uint A = 0x0000_3980, B = 0x0000_3981, APACK = 0x4000_7980, AHELM = 0x4000_7981, ASHIRT = 0x4000_7982;
            World world = WorldStateContentsTests.Build();
            Mobile a = world.GetOrCreateMobile(A);
            Mobile b = world.GetOrCreateMobile(B);

            if (!first)
            {
                world.Writer.Equip(AHELM, 0x1408, Layer.Helmet, A, 0, out _, out _);
            }

            world.Writer.Equip(APACK, 0x0E75, Layer.Backpack, A, 0, out _, out _);

            if (first)
            {
                world.Writer.Equip(AHELM, 0x1408, Layer.Helmet, A, 0, out _, out _);
            }

            world.Writer.Equip(ASHIRT, 0x1517, Layer.Shirt, A, 0, out _, out _);
            WorldStateContentsTests.HeldChildren(world, A).Should().Equal(first ? new[] { APACK, AHELM, ASHIRT } : new[] { AHELM, APACK, ASHIRT });

            world.Writer.Equip(APACK, 0x0E75, Layer.Backpack, B, 0, out _, out _);

            // the lists: the pack is B's only, A keeps its other children in order
            WorldStateContentsTests.HeldChildren(world, A).Should().Equal(AHELM, ASHIRT);
            WorldStateContentsTests.HeldChildren(world, B).Should().Equal(APACK);
            world.Items.Get(APACK).Previous.Should().BeNull();
            world.Items.Get(APACK).Next.Should().BeNull();

            // the ECS read and the list read agree, both ways
            BothWays(world, () =>
            {
                a.FindItemByLayer(Layer.Backpack).Should().BeNull();
                a.FindItemByLayer(Layer.Helmet).Serial.Should().Be(AHELM);
                a.FindItemByLayer(Layer.Shirt).Serial.Should().Be(ASHIRT);
                b.FindItemByLayer(Layer.Backpack).Serial.Should().Be(APACK);
            });

            WornLayersTests.AllWornMatch(world);
            AllReadersEqual(world);
        }

        [Fact]
        public void Untracked_Layers_Read_The_List_Both_Ways()
        {
            // layers WornLayers does not track (0: the trade boxes; above 0x1D), attached through the funnel: the list answers
            // either way, and a mobile wearing only those has no worn item for the view's guard (its reads are tracked layers)
            const uint BOX0 = 0x4000_7990, BOX1E = 0x4000_7991, ONLY = 0x0000_3990, ONLYBOX = 0x4000_7992;
            World world = WorldStateContentsTests.Build();
            Mobile m = world.Mobiles.Get(MOB);
            Item box = world.Items.Get(HELM);
            world.Writer.Equip(BOX0, 0x1E5E, Layer.Invalid, MOB, 0, out _, out _);
            world.Writer.Equip(BOX1E, 0x1E5E, (Layer)0x1E, MOB, 0, out _, out _);
            Mobile only = world.GetOrCreateMobile(ONLY);
            world.Writer.Equip(ONLYBOX, 0x1E5E, (Layer)0x1E, ONLY, 0, out _, out _);

            BothWays(world, () =>
            {
                m.FindItemByLayer(Layer.Invalid).Serial.Should().Be(BOX0);
                m.FindItemByLayer((Layer)0x1E).Serial.Should().Be(BOX1E);
                m.FindItemByLayer(Layer.Invalid).Should().BeSameAs(WornLayersTests.ListFind(m, Layer.Invalid));
                only.FindItemByLayer((Layer)0x1E).Serial.Should().Be(ONLYBOX);
                box.FindItemByLayer(Layer.Helmet).Should().BeNull("an item's own list");
            });

            world.Ecs.Mirror.Worn(MOB, 0).Should().Be(0);
            world.Ecs.Mirror.Worn(MOB, 0x1E).Should().Be(0);
            world.State.ReadEquipmentOverride = false;
            world.State.HasWornItems(only).Should().BeTrue("off: !IsEmpty");
            world.State.ReadEquipmentOverride = true;
            world.State.HasWornItems(only).Should().BeFalse("on: no worn item on a tracked layer");
        }

        [Fact]
        public void A_Cached_Missing_Record_Recovers_When_The_Record_Is_Created()
        {
            // Codex audit P3 (#4): a held mobile's record removed, read once (NoRecord cached), then created again over its
            // attached children (a slot write, no epoch change): the read finds its items
            World world = WorldStateContentsTests.Build();
            Mobile m = world.Mobiles.Get(MOB);
            world.State.ReadEquipmentOverride = true;
            m.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM);

            world.Ecs.Mirror.Remove(MOB);
            m.FindItemByLayer(Layer.Helmet).Should().BeNull("no record: nothing worn");
            world.State.HasWornItems(m).Should().BeFalse();
            uint epoch = world.Ecs.Mirror.Epoch;

            world.Ecs.Mirror.UpsertMobile(MOB, m.Graphic, 0, m.X, m.Y, m.Z, 0, 0, false);
            world.Ecs.Mirror.Epoch.Should().Be(epoch, "a creation moves no epoch");
            m.FindItemByLayer(Layer.Helmet).Should().BeSameAs(world.Items.Get(HELM));
            world.State.HasWornItems(m).Should().BeTrue();
            AllReadersEqual(world);
        }

        [Fact]
        public void A_Bare_Record_Created_After_A_Cached_Miss_Reads_Its_Later_Equipment()
        {
            // the same with no children: the creation writes no slot; the first equip then must not keep the cached NoRecord
            World world = WorldStateContentsTests.Build();
            Mobile late = world.GetOrCreateMobile(LATE);
            world.State.ReadEquipmentOverride = true;
            world.Ecs.Mirror.Remove(LATE);
            late.FindItemByLayer(Layer.Shirt).Should().BeNull();

            world.Ecs.Mirror.UpsertMobile(LATE, late.Graphic, 0, late.X, late.Y, late.Z, 0, 0, false);
            world.Writer.Equip(NEWSHIRT, 0x1517, Layer.Shirt, LATE, 0, out _, out _);
            late.FindItemByLayer(Layer.Shirt).Should().BeSameAs(world.Items.Get(NEWSHIRT));
            AllReadersEqual(world);
        }

        [Fact]
        public void A_Version_Past_32_Bits_Still_Refills_The_Cache()
        {
            // Codex audit P3 (#6): a version whose low 32 bits equal the cached one must not pass for it
            World world = WorldStateContentsTests.Build();
            Mobile m = world.Mobiles.Get(MOB);
            world.State.ReadEquipmentOverride = true;
            m.FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM);
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e).Should().BeTrue();
            ulong cached = e.GetComponent<EquipmentSlots>().Worn.Version;

            // the next slot write on this mobile takes 2^32 + cached
            world.Ecs.Mirror.WornWrites = (1L << 32) + (long)cached - 1;
            uint epoch = world.Ecs.Mirror.Epoch;
            // one slot write and no removal (the mirror's detach; the list is not the subject here)
            world.Ecs.Mirror.DetachChild(HELM);
            world.Ecs.Mirror.Epoch.Should().Be(epoch, "no removal: only the version can tell");
            e.GetComponent<EquipmentSlots>().Worn.Version.Should().Be((1UL << 32) + cached);

            m.FindItemByLayer(Layer.Helmet).Should().BeNull("the ECS's helmet left, not the cached one");
        }

        [Fact]
        public void The_Read_Cache_Sizes_Are_What_The_Summary_Counts()
        {
            // ReadCacheMemory's layout-based figures: a WornCache object, and its items array
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var c = new WornCache();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            c.Items = new Item[WornLayers.COUNT];
            long a2 = GC.GetAllocatedBytesForCurrentThread();

            (a1 - a0).Should().Be(80, "T3b: + the LayerDecision reference");
            (a2 - a1).Should().Be(24 + 8 * WornLayers.COUNT);
        }

        [Fact]
        public void A_Read_Allocates_Nothing_Both_Ways()
        {
            World world = WorldStateContentsTests.Build();
            Mobile m = world.Mobiles.Get(MOB);

            BothWays(world, () =>
            {
                for (int l = 0; l < 0x1E; l++)
                {
                    m.FindItemByLayer((Layer)l);
                }

                world.State.HasWornItems(m);

                long least = long.MaxValue;

                for (int round = 0; round < 3; round++)
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread();

                    for (int k = 0; k < 200; k++)
                    {
                        for (int l = 0; l < 0x1E; l++)
                        {
                            m.FindItemByLayer((Layer)l);
                        }

                        world.State.HasWornItems(m);
                    }

                    least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - a0);
                }

                least.Should().Be(0);
            });
        }
    }
}
