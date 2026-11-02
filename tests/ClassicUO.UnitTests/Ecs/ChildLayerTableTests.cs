using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // perf-11: an item's (a corpse's) child on a layer is served from WorldState's child-by-layer tables. Every answer, for
    // every layer and every held item, is the scan's (the first live child on the layer in the ECS's child order) after each
    // kind of write that can change it; a quiet world builds a parent's table once.
    [Collection(nameof(LayerRulesStateCollection))]
    public class ChildLayerTableTests
    {
        private const uint CORPSE = 0x4000_7906, LOOT = 0x4000_7907, CLOOT = 0x4000_790B, PACK = 0x4000_7900;
        private const uint PANTS2 = 0x4000_7970, HELM2 = 0x4000_7971, LOOT2 = 0x4000_7972, BAG2 = 0x4000_7973;

        private static void AllSame(World world, string step)
        {
            foreach (Item parent in new List<Item>(world.Items.Values))
            {
                for (int l = 0; l < 256; l++)
                {
                    parent.FindItemByLayer((Layer)l).Should().BeSameAs(world.State.ScanByLayer(parent, (Layer)l), $"{step}: 0x{parent.Serial:X8} layer {l}");
                }
            }
        }

        [Fact]
        public void Every_Write_Kind_Keeps_The_Tables_Equal_To_The_Scan()
        {
            World world = WorldStateContentsTests.Build();
            Item corpse = world.Items.Get(CORPSE);
            AllSame(world, "built");

            // a second item on a layer goes behind the first; another layer
            world.Writer.PlaceCorpseEquipment(corpse, PANTS2, Layer.Pants);
            world.Writer.PlaceCorpseEquipment(corpse, HELM2, Layer.Helmet);
            AllSame(world, "placed");
            corpse.FindItemByLayer(Layer.Pants).Serial.Should().Be(CLOOT);

            // the first one re-placed on another layer: the second becomes the first on the old layer
            world.Writer.PlaceCorpseEquipment(corpse, CLOOT, Layer.Shirt);
            AllSame(world, "re-layered");
            corpse.FindItemByLayer(Layer.Pants).Serial.Should().Be(PANTS2);
            corpse.FindItemByLayer(Layer.Shirt).Serial.Should().Be(CLOOT);

            // contained loot (layer 0), then an item moved out of the corpse
            world.Writer.PlaceInContainer(LOOT2, 0x1F14, 1, 6, 6, 0, CORPSE, out _);
            AllSame(world, "looted in");
            world.Writer.PlaceInContainer(PANTS2, 0x152E, 1, 7, 7, 0, PACK, out _);
            AllSame(world, "moved out");
            corpse.FindItemByLayer(Layer.Pants).Should().BeNull();

            // a removal
            world.Writer.RemoveWorldItem(world.Items.Get(HELM2), out _);
            AllSame(world, "removed");
            corpse.FindItemByLayer(Layer.Helmet).Should().BeNull();

            // a container cleared
            world.Writer.ClearContainer(CORPSE, keepEquipped: false, itemOnly: false);
            AllSame(world, "cleared");
            corpse.FindItemByLayer(Layer.Invalid).Should().BeNull();
        }

        [Fact]
        public void A_Quiet_World_Builds_A_Parent_Once_And_A_Write_Rebuilds_It()
        {
            World world = WorldStateContentsTests.Build();
            Item corpse = world.Items.Get(CORPSE);

            corpse.FindItemByLayer(Layer.Pants).Serial.Should().Be(CLOOT);
            long builds = world.State.LayerTableBuilds;

            for (int i = 0; i < 10; i++)
            {
                for (int l = 0; l <= (int)Layer.Bank; l++)
                {
                    corpse.FindItemByLayer((Layer)l);
                }
            }

            world.State.LayerTableBuilds.Should().Be(builds, "nothing was written");

            world.Writer.PlaceCorpseEquipment(corpse, PANTS2, Layer.Pants);
            corpse.FindItemByLayer(Layer.Pants).Serial.Should().Be(CLOOT);
            world.State.LayerTableBuilds.Should().Be(builds + 1);
        }

        [Fact]
        public void More_Parents_Than_Tables_Read_In_Turn_Stay_Exact()
        {
            World world = WorldStateContentsTests.Build();
            var bags = new List<Item>();

            for (uint i = 0; i < WorldState.LayerTableCount + 3; i++)
            {
                world.Writer.PlaceInContainer(BAG2 + i * 2, 0xE76, 1, 1, 1, 0, PACK, out _);
                world.Writer.PlaceInContainer(BAG2 + i * 2 + 1, 0xF3F, 1, 1, 1, 0, BAG2 + i * 2, out _);
                bags.Add(world.Items.Get(BAG2 + i * 2));
            }

            for (int round = 0; round < 3; round++)
            {
                foreach (Item bag in bags)
                {
                    bag.FindItemByLayer(Layer.Invalid).Serial.Should().Be(bag.Serial + 1);
                    bag.FindItemByLayer(Layer.Pants).Should().BeNull();
                }
            }

            AllSame(world, "many parents");
        }

        [Fact]
        public void Every_Item_Record_Write_Moves_The_Mirror_Count()
        {
            var mirror = new ClassicUO.Ecs.WorldMirror();
            ulong last = mirror.ItemWrites;

            void Moved(string what)
            {
                mirror.ItemWrites.Should().BeGreaterThan(last, what);
                last = mirror.ItemWrites;
            }

            mirror.UpsertItem(0x4000_0100, 0xE76, 0, 1, 1, 0, 0, 0, 1, 0xFFFF_FFFF, 0, false);
            Moved("create");
            mirror.UpsertItem(0x4000_0101, 0x1517, 0, 0, 0, 0, 0, 0, 1, 0x4000_0100, (byte)Layer.Shirt, false, attach: true);
            Moved("attach");
            mirror.UpsertItem(0x4000_0101, 0x1517, 0, 0, 0, 0, 0, 0, 1, 0x4000_0100, (byte)Layer.Pants, false);
            Moved("layer only");
            mirror.StampChild(0x4000_0101).Should().BeTrue();
            Moved("re-stamp");
            mirror.DetachChild(0x4000_0101).Should().BeTrue();
            Moved("detach");
            mirror.Remove(0x4000_0101).Should().BeTrue();
            Moved("remove");
            mirror.Clear();
            Moved("clear");
        }

        [Fact]
        public void A_World_Clear_Drops_The_Tables()
        {
            World world = WorldStateContentsTests.Build();
            Item corpse = world.Items.Get(CORPSE);
            corpse.FindItemByLayer(Layer.Pants).Serial.Should().Be(CLOOT);

            world.State.OnWorldCleared();
            long builds = world.State.LayerTableBuilds;
            corpse.FindItemByLayer(Layer.Pants).Serial.Should().Be(CLOOT);
            world.State.LayerTableBuilds.Should().Be(builds + 1, "the cleared table is built again");
        }

        [Fact]
        public void A_Child_Mismatch_Is_Reported()
        {
#if CUO_DEVTOOLS
            World world = WorldStateContentsTests.Build();
            Item loot = world.Items.Get(LOOT), cloot = world.Items.Get(CLOOT);
            var reports = new List<string>();
            Action<string> before = LayerCacheCheck.MismatchObserver;
            LayerCacheCheck.MismatchObserver = reports.Add;

            try
            {
                LayerCacheCheck.CheckChild(CORPSE, Layer.Pants, cloot, cloot).Should().BeTrue();
                LayerCacheCheck.CheckChild(CORPSE, Layer.Pants, null, null).Should().BeTrue();
                reports.Should().BeEmpty();

                LayerCacheCheck.CheckChild(CORPSE, Layer.Pants, loot, cloot).Should().BeFalse();
                reports.Should().ContainSingle().Which.Should().StartWith("[layers] cache mismatch 0x4000790");
            }
            finally
            {
                LayerCacheCheck.MismatchObserver = before;
            }
#endif
        }
    }
}
