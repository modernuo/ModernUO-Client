using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 child-list slice A: the readers moved from the LinkedObject child list to the ECS (WorldState.Children/Contents,
    // the worn layers) give the list's answers, order included. Each operation is run with CUO_CONTENTS_CHECK and
    // CUO_EQUIP_READ_CHECK on (no mismatch), and its removals are compared with the order the list walk gave (computed from
    // the list before the operation). The list is still written, so it is the reference here.
    public class ChildListReadersTests
    {
        private const uint MOB = 0x0000_3900, PACK = 0x4000_7900, A = 0x4000_7901, B = 0x4000_7902, C = 0x4000_7903, CORPSE = 0x4000_7906, LOOT = 0x4000_7907, CLOOT = 0x4000_790B;
        private const uint NEW = 0x4000_7940, BAG = 0x4000_7941, SHOP = 0x4000_7942, S1 = 0x4000_7943, S2 = 0x4000_7944, S3 = 0x4000_7945, BOOK = 0x4000_7946, INBOOK = 0x4000_7947;
        private const uint DEEP = 0x4000_7960, PANTS2 = 0x4000_7961;

        private static World Build()
        {
            World world = WorldStateContentsTests.Build();

            // one level deeper: an item inside the bag's item
            world.Writer.PlaceInContainer(DEEP, 0xF3F, 1, 1, 1, 0, NEW, out _);
            world.State.CheckEnabled = true;
            world.State.EquipCheckEnabled = true;

            return world;
        }

        private static void NoMismatches(World world)
        {
            world.State.Mismatches.Should().Be(0);
            world.State.EquipMismatches.Should().Be(0);
            world.State.SnapshotDepth.Should().Be(0, "every snapshot is released");
            WorldStateContentsTests.AllMatch(world);
        }

        // the list walk's destroy order: each held, live child's contents first, then the child (World.RemoveItem by serial)
        private static void LegacyOrder(World world, Entity e, List<uint> order)
        {
            for (LinkedObject i = e.Items; i != null; i = i.Next)
            {
                if (world.Items.Get(((Item)i).Serial) is Item held && !held.IsDestroyed)
                {
                    LegacyOrder(world, held, order);
                }
            }

            order.Add(e.Serial);
        }

        private static List<uint> Removals(World world)
        {
            var removed = new List<uint>();
            world.RemovalObserver = removed.Add;

            return removed;
        }

        [Fact]
        public void Removing_An_Item_Destroys_Its_Subtree_In_The_List_Order()
        {
            World world = Build();
            var expected = new List<uint>();
            LegacyOrder(world, world.Items.Get(PACK), expected);
            expected.Should().Equal(B, C, A, DEEP, NEW, BAG, PACK);
            List<uint> removed = Removals(world);

            world.Writer.RemoveWorldItem(world.Items.Get(PACK), out _);

            removed.Should().Equal(expected);
            NoMismatches(world);
        }

        [Fact]
        public void Removing_A_Mobile_Destroys_What_It_Wears_In_The_List_Order()
        {
            World world = Build();
            var expected = new List<uint>();
            LegacyOrder(world, world.Mobiles.Get(MOB), expected);
            List<uint> removed = Removals(world);

            world.Writer.RemoveMobile(world.Mobiles.Get(MOB));

            removed.Should().Equal(expected);
            removed.Should().HaveCountGreaterThan(1);
            NoMismatches(world);
        }

        [Fact]
        public void Clearing_A_Container_Removes_Its_Children_In_The_List_Order()
        {
            World world = Build();
            var expected = new List<uint>();

            for (LinkedObject i = world.Items.Get(PACK).Items; i != null; i = i.Next)
            {
                LegacyOrder(world, (Item)i, expected);
            }

            List<uint> removed = Removals(world);

            world.Writer.ClearContainer(PACK, keepEquipped: false, itemOnly: true);

            removed.Should().Equal(expected);
            world.Items.Get(PACK).IsEmpty.Should().BeTrue();
            world.State.HasChildren(world.Items.Get(PACK)).Should().BeFalse();
            NoMismatches(world);
        }

        [Fact]
        public void Clearing_A_Corpse_Keeps_Its_Equipment_First_In_The_List()
        {
            World world = Build();
            Item corpse = world.Items.Get(CORPSE);
            world.Writer.PlaceInContainer(0x4000_7962, 0x1F14, 1, 6, 6, 0, CORPSE, out _);
            List<uint> removed = Removals(world);

            world.Writer.ClearContainer(CORPSE, keepEquipped: true, itemOnly: false);

            removed.Should().Equal(LOOT, 0x4000_7962u);
            ((Item)corpse.Items).Serial.Should().Be(CLOOT);
            world.State.Contents(CORPSE).ToArray().Should().Equal(CLOOT);
            NoMismatches(world);
        }

        [Fact]
        public void Clearing_Worn_Items_Removes_Them_In_The_List_Order()
        {
            World world = Build();
            var expected = new List<uint>();

            for (LinkedObject i = world.Mobiles.Get(MOB).Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                if (!it.Opened && it.Layer != Layer.Backpack)
                {
                    LegacyOrder(world, it, expected);
                }
            }

            List<uint> removed = Removals(world);

            world.Writer.ClearWornItems(MOB);

            removed.Should().Equal(expected);
            NoMismatches(world);
        }

        [Fact]
        public void A_Spellbook_Refill_Detaches_The_Book_Contents_And_Keeps_The_Entries_First()
        {
            World world = Build();

            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b1011, 0);

            world.State.Contents(BOOK).ToArray().Should().BeEmpty("the clear detached the item the server put in the book");
            world.Items.Get(INBOOK).Should().NotBeNull();

            // the entries are the book's first children, layer Invalid: the list's first match is the first entry
            Item book = world.Items.Get(BOOK);
            book.FindItemByLayer(Layer.Invalid).Should().BeSameAs(book.FindItemByLayerInList(Layer.Invalid));
            world.State.FirstChild(book).Should().BeSameAs(book.Items);

            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b1, 0);
            world.State.FirstChild(book).Should().BeSameAs(book.Items);
            NoMismatches(world);
        }

        [Fact]
        public void A_Shop_Sort_Orders_The_Ecs_And_The_List_Alike()
        {
            World world = Build();
            Item shop = world.Items.Get(SHOP);

            world.Writer.SortShopContents(shop);

            world.State.Contents(SHOP).ToArray().Should().Equal(S2, S3, S1);
            WorldStateContentsTests.HeldChildren(world, SHOP).Should().Equal(S2, S3, S1);
            NoMismatches(world);
        }

        [Fact]
        public void The_Death_Rekey_Moves_The_Children_In_Their_Order()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);
            List<uint> before = WorldStateContentsTests.HeldChildren(world, MOB);

            uint dead = world.Writer.RekeyDeadMobile(m);

            world.State.Contents(dead).ToArray().Should().Equal(before);
            WorldStateContentsTests.HeldChildren(world, dead).Should().Equal(before);

            foreach (uint s in before)
            {
                world.Items.Get(s).Container.Should().Be(dead);
            }

            NoMismatches(world);
        }

        [Fact]
        public void A_Corpse_Finds_Its_First_Child_On_Each_Layer_In_The_List_Order()
        {
            World world = Build();
            Item corpse = world.Items.Get(CORPSE);

            // a second item on the same layer: the first in child order is found
            world.Writer.PlaceCorpseEquipment(corpse, PANTS2, Layer.Pants);
            world.Writer.PlaceCorpseEquipment(corpse, 0x4000_7963, Layer.Helmet);

            foreach (Item parent in new List<Item>(world.Items.Values))
            {
                for (int l = 0; l <= (int)Layer.Bank; l++)
                {
                    parent.FindItemByLayer((Layer)l).Should().BeSameAs(parent.FindItemByLayerInList((Layer)l), $"0x{parent.Serial:X8} layer {l}");
                }
            }

            corpse.FindItemByLayer(Layer.Pants).Serial.Should().Be(CLOOT);
            NoMismatches(world);
        }

        [Fact]
        public void A_Held_Mobile_Untracked_Layer_Reads_The_Ecs_Children()
        {
            World world = Build();
            Mobile m = world.Mobiles.Get(MOB);

            m.FindItemByLayer(Layer.Invalid).Should().BeSameAs(m.FindItemByLayerInList(Layer.Invalid));
            NoMismatches(world);
        }

        [Fact]
        public void The_Preview_Mobile_Reads_Its_Table_Like_The_List()
        {
            World world = Build();

            // the character creation's preview: detached (not in World.Mobiles), its clothes detached items
            Mobile preview = Mobile.Create(world, 1);
            preview.Preview = new PreviewEquipment();
            world.State.HasWornItems(preview).Should().BeFalse();

            Item Wear(Layer layer, ushort graphic)
            {
                Item it = Item.Create(world, 0x4000_0000 + (uint)layer);
                it.Graphic = graphic;
                it.Layer = layer;
                preview.PushToBack(it);
                preview.Preview.Wear(it);

                return it;
            }

            Wear(Layer.Shirt, 0x1517);
            Item pants = Wear(Layer.Pants, 0x152E);
            Wear(Layer.Hair, 0x203B);

            void Same()
            {
                for (int l = 0; l <= (int)Layer.Bank; l++)
                {
                    preview.FindItemByLayer((Layer)l).Should().BeSameAs(preview.FindItemByLayerInList((Layer)l), $"layer {l}");
                }
            }

            Same();
            world.State.HasWornItems(preview).Should().BeTrue();

            // the gump's swap: the old item off, the new one on
            preview.Remove(pants);
            preview.Preview.TakeOff(pants);
            Wear(Layer.Pants, 0x1539);
            Same();

            preview.Clear();
            preview.Preview.Clear();
            Same();
            world.State.HasWornItems(preview).Should().BeFalse();
            NoMismatches(world);

            // a table that disagrees with the list is reported
            if (WorldState.EquipCheckAvailable)
            {
                Item hat = Item.Create(world, 0x4000_0000 + (uint)Layer.Helmet);
                hat.Layer = Layer.Helmet;
                preview.PushToBack(hat);
                preview.FindItemByLayer(Layer.Helmet);
                world.State.EquipMismatches.Should().Be(1);
            }
        }
    }
}
