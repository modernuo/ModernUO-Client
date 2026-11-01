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
    // T3a (ecs-transition-plan.md, T3): WornLayers, a mobile's first attached item per layer, maintained by the mirror from
    // the funnel's attaches. After every kind of change, for every mobile World holds and every tracked layer, the mirror's
    // slot is what the legacy FindItemByLayer walk finds first in the mobile's child list.
    [Collection("PlayerWorld")]
    public class WornLayersTests
    {
        private const uint MOB = 0x0000_3900, PACK = 0x4000_7900, SHIRT = 0x4000_7904, HELM = 0x4000_7905, RET = 0x4000_7909, WEAR = 0x4000_790A;
        private const uint HELM2 = 0x4000_7960, HELM3 = 0x4000_7961, LATE = 0x0000_3960, ORPHAN = 0x4000_7962, OTHER = 0x0000_3961;

        // the legacy walk (Entity.FindItemByLayer before T3a): the first non-destroyed child on the layer
        internal static Item ListFind(Entity e, Layer layer)
        {
            for (LinkedObject i = e?.Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                if (!it.IsDestroyed && it.Layer == layer)
                {
                    return it;
                }
            }

            return null;
        }

        internal static void AllWornMatch(World world)
        {
            foreach (Mobile m in world.Mobiles.Values.ToList())
            {
                for (int l = 1; l <= WornLayers.COUNT; l++)
                {
                    Item it = ListFind(m, (Layer)l);
                    world.Ecs.Mirror.Worn(m.Serial, (byte)l).Should().Be(it?.Serial ?? 0, $"layer 0x{l:X2} of 0x{m.Serial:X8}");

                    // the layer's count of attached items (what spares the rescan) is the list's count of held items on it
                    int listed = 0;

                    for (LinkedObject i = m.Items; i != null; i = i.Next)
                    {
                        if (i is Item c && c.Layer == (Layer)l && ReferenceEquals(world.Items.Get(c.Serial), c))
                        {
                            listed++;
                        }
                    }

                    world.Ecs.Mirror.WornCount(m.Serial, (byte)l).Should().Be(listed, $"count of layer 0x{l:X2} of 0x{m.Serial:X8}");
                }
            }
        }

        [Theory]
        [InlineData(WorldStateContentsTests.Change.Push)]
        [InlineData(WorldStateContentsTests.Change.RePush)]
        [InlineData(WorldStateContentsTests.Change.NewChild)]
        [InlineData(WorldStateContentsTests.Change.MoveToOtherContainer)]
        [InlineData(WorldStateContentsTests.Change.MoveToGround)]
        [InlineData(WorldStateContentsTests.Change.Remove)]
        [InlineData(WorldStateContentsTests.Change.RemoveContainer)]
        [InlineData(WorldStateContentsTests.Change.Equip)]
        [InlineData(WorldStateContentsTests.Change.ReEquip)]
        [InlineData(WorldStateContentsTests.Change.Unequip)]
        [InlineData(WorldStateContentsTests.Change.OrphanThenAttach)]
        [InlineData(WorldStateContentsTests.Change.SpellbookClear)]
        [InlineData(WorldStateContentsTests.Change.VendorSort)]
        [InlineData(WorldStateContentsTests.Change.ClearContainer)]
        [InlineData(WorldStateContentsTests.Change.ClearCorpseKeepEquipped)]
        [InlineData(WorldStateContentsTests.Change.Rekey)]
        [InlineData(WorldStateContentsTests.Change.OldRekey)]
        [InlineData(WorldStateContentsTests.Change.RemoveMobile)]
        [InlineData(WorldStateContentsTests.Change.RemoveMiddleThenAdd)]
        public void Every_Change_Keeps_The_Worn_Layers_Equal_To_The_Lists(WorldStateContentsTests.Change change)
        {
            World world = WorldStateContentsTests.Build();
            AllWornMatch(world);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(HELM);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Gloves).Should().Be(RET, "the returned held item is worn again");

            WorldStateContentsTests.Apply(world, change);

            AllWornMatch(world);
        }

        [Fact]
        public void The_Mirror_Clear_Leaves_No_Worn_Layer()
        {
            World world = WorldStateContentsTests.Build();
            world.Ecs.Mirror.Clear();

            for (int l = 0; l <= 0xFF; l++)
            {
                world.Ecs.Mirror.Worn(MOB, (byte)l).Should().Be(0);
            }
        }

        [Fact]
        public void Two_Items_On_A_Layer_The_First_In_The_List_Is_Worn()
        {
            World world = WorldStateContentsTests.Build();

            // 0x2E on an occupied layer: the client keeps both (the server removes the old one); the first is found
            world.Writer.Equip(HELM2, 0x1409, Layer.Helmet, MOB, 0, out _, out _);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(HELM);
            world.Writer.Equip(HELM3, 0x140A, Layer.Helmet, MOB, 0, out _, out _);
            AllWornMatch(world);

            // the first re-equipped goes to the back: the next is first
            world.Writer.Equip(HELM, 0x1408, Layer.Helmet, MOB, 0, out _, out _);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(HELM2);
            AllWornMatch(world);

            // the first removed: the next one by list order
            world.Writer.RemoveWorldItem(world.Items.Get(HELM2), out _);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(HELM3);
            AllWornMatch(world);

            // the first moved into the pack: the last one left
            world.Writer.PlaceInContainer(HELM3, 0x140A, 1, 2, 2, 0, PACK, out _);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(HELM);
            AllWornMatch(world);
        }

        [Fact]
        public void A_Layer_Change_On_The_Same_Wearer_Moves_The_Slot()
        {
            World world = WorldStateContentsTests.Build();

            world.Writer.Equip(SHIRT, 0x1517, Layer.Tunic, MOB, 0, out _, out _);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Shirt).Should().Be(0);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Tunic).Should().Be(SHIRT);
            AllWornMatch(world);
        }

        [Fact]
        public void A_Move_To_Another_Wearer_Moves_The_Slot()
        {
            World world = WorldStateContentsTests.Build();
            world.GetOrCreateMobile(OTHER);

            world.Writer.Equip(HELM, 0x1408, Layer.Helmet, OTHER, 0, out _, out _);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(0);
            world.Ecs.Mirror.Worn(OTHER, (byte)Layer.Helmet).Should().Be(HELM);
            AllWornMatch(world);
        }

        [Fact]
        public void Equipment_Before_Its_Mobile_Stays_Unworn_Until_Attached()
        {
            World world = WorldStateContentsTests.Build();

            world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out Entity wearer, out _);
            wearer.Should().BeNull();
            world.GetOrCreateMobile(LATE);
            world.Ecs.Mirror.Worn(LATE, (byte)Layer.Shirt).Should().Be(0, "ClassicUO never adopts an orphan");
            ListFind(world.Mobiles.Get(LATE), Layer.Shirt).Should().BeNull();
            AllWornMatch(world);

            world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out _, out _);
            world.Ecs.Mirror.Worn(LATE, (byte)Layer.Shirt).Should().Be(ORPHAN);
            AllWornMatch(world);
        }

        [Fact]
        public void The_Returned_Held_Item_Goes_To_The_Back_Of_Its_Layer()
        {
            World world = WorldStateContentsTests.Build();
            world.Writer.Equip(HELM2, 0x1409, Layer.Helmet, MOB, 0, out _, out _);

            // the first helmet denied and returned: it goes to the back of the list, so the second is found first
            world.Writer.ReturnHeldItem(HELM, 0x1408, 0, 1, Flags.None, Layer.Helmet, 0, 0, 0, MOB, out _, out WorldWriter.HeldItemReturn r);
            r.Should().Be(WorldWriter.HeldItemReturn.Paperdoll);
            world.Ecs.Mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(HELM2);
            AllWornMatch(world);
        }

        [Fact]
        public void Untracked_Layers_And_Unknown_Mobiles_Read_Zero()
        {
            World world = WorldStateContentsTests.Build();

            world.Ecs.Mirror.Worn(MOB, 0).Should().Be(0);
            world.Ecs.Mirror.Worn(MOB, 0x1E).Should().Be(0);
            world.Ecs.Mirror.Worn(MOB, 0xFF).Should().Be(0);
            world.Ecs.Mirror.Worn(0x0000_3FFF, (byte)Layer.Helmet).Should().Be(0);
            world.Ecs.Mirror.Worn(HELM, (byte)Layer.Helmet).Should().Be(0, "an item has no worn layers");
            WornLayers.Tracked(0).Should().BeFalse();
            WornLayers.Tracked(1).Should().BeTrue();
            WornLayers.Tracked(0x1D).Should().BeTrue();
            WornLayers.Tracked(0x1E).Should().BeFalse();
        }

        [Fact]
        public void A_Mobile_Record_Created_Over_Attached_Children_Rebuilds_Its_Layers()
        {
            var mirror = new WorldMirror();
            mirror.UpsertMobile(MOB, 0x190, 0, 1, 1, 0, 0, 0, false);
            mirror.UpsertItem(HELM, 0x1408, 0, 1, 1, 0, 0, 0, 1, MOB, (byte)Layer.Helmet, false, attach: true);
            mirror.UpsertItem(HELM2, 0x1409, 0, 1, 1, 0, 0, 0, 1, MOB, (byte)Layer.Helmet, false, attach: true);
            mirror.UpsertItem(SHIRT, 0x1517, 0, 1, 1, 0, 0, 0, 1, MOB, (byte)Layer.Shirt, false, attach: true);
            mirror.UpsertItem(WEAR, 0x1517, 0, 1, 1, 0, 0, 0, 1, MOB, (byte)Layer.Robe, false);

            // the record goes, its children stay attached under the serial, and a record comes back
            mirror.Remove(MOB);
            mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(0);
            mirror.UpsertMobile(MOB, 0x190, 0, 1, 1, 0, 0, 0, false);

            mirror.Worn(MOB, (byte)Layer.Helmet).Should().Be(HELM, "the lowest stamp");
            mirror.Worn(MOB, (byte)Layer.Shirt).Should().Be(SHIRT);
            mirror.Worn(MOB, (byte)Layer.Robe).Should().Be(0, "not attached");
        }

        [Fact]
        public void A_Mobile_Record_Has_The_Components_It_Had_And_Its_Layers()
        {
            var mirror = new WorldMirror();
            Friflo.Engine.ECS.Entity e = mirror.UpsertMobile(MOB, 0x190, 0x21, 3, 4, 5, 6, 7, true);

            e.GetComponent<Serial>().Value.Should().Be(MOB);
            e.GetComponent<Graphic>().Value.Should().Be(0x190);
            e.GetComponent<Hue>().Value.Should().Be(0x21);
            e.GetComponent<Position>().X.Should().Be(3);
            e.GetComponent<Position>().Y.Should().Be(4);
            e.GetComponent<Position>().Z.Should().Be(5);
            e.GetComponent<Facing>().Value.Should().Be(6);
            e.GetComponent<ClassicUO.Ecs.Flags>().Value.Should().Be(7);
            e.GetComponent<AnimationState>().ServerGroup.Should().Be(0xFF);
            e.GetComponent<MobileRenderState>().Alpha.Should().Be(0xFF);
            e.HasComponent<EquipmentSlots>().Should().BeTrue();
            e.HasComponent<MobileInputs>().Should().BeTrue();
            e.HasComponent<WornLayers>().Should().BeTrue();
            e.Tags.Has<MobileTag>().Should().BeTrue();
            e.Tags.Has<PlayerTag>().Should().BeTrue();
            e.Tags.Has<EquipmentDirty>().Should().BeTrue();
            mirror.UpsertMobile(0x0000_1001, 0x190, 0, 0, 0, 0, 0, 0, false).Tags.Has<PlayerTag>().Should().BeFalse();
            mirror.WornLayersBytes.Should().Be(2 * 152, "4 B serials and 1 B counts per layer, a 4 B version, aligned");
        }
    }
}
