using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // pa-itemprep (item_prep_rows): the world fill's item branch reads the graphic and the locked test once, from the
    // ItemDrawRow with the switch on and through the getters with it off; both give what the old getters (Graphic, IsCorpse,
    // IsMulti, ItemData, IsLocked) give, for every kind of item, after updates, and without a record. CUO_ITEM_PREP_CHECK
    // counts a disagreement.
    [Collection(nameof(ItemPrepTests))]
    public sealed class ItemPrepTests : IDisposable
    {
        private const uint ITEM0 = 0x4000_0000;

        public ItemPrepTests()
        {
            Reset();
        }

        public void Dispose()
        {
            Reset();
        }

        private static void Reset()
        {
#if CUO_DEVTOOLS
            ItemPrep.ResetForTests();
#else
            ItemPrep.OverrideForTests = null;
#endif
        }

        private static StaticTiles[] Tiles()
        {
            var tiles = new StaticTiles[0x10000];
            var rnd = new Random(7);

            for (int g = 0; g < tiles.Length; g++)
            {
                ulong flags = (ulong)(rnd.Next(4) == 0 ? TileFlag.Container : 0) | (ulong)(rnd.Next(6) == 0 ? TileFlag.Animation : 0);
                tiles[g] = new StaticTiles(flags, (byte)rnd.Next(256), 0, 0, 0, 0, 0, 1, null);
            }

            return tiles;
        }

        // the old getters, as the item branch asked them
        private static (ushort tile, bool corpse, bool locked) Old(Item item) =>
            (item.IsMulti ? item.MultiGraphic : item.Graphic, item.IsCorpse, !item.IsCorpse && !item.IsMulti && LockedOld(item));

        private static StaticTiles[] _tiles;

        private static bool LockedOld(Item item) => (item.Flags & Flags.Movable) == 0 && _tiles[item.Graphic].Weight > 90;

        private static void AssertSame(Item item, StaticTiles[] tiles, bool rows)
        {
            ushort graphic = ItemPrep.Read(item, tiles, rows, out bool locked);
            (ushort tile, bool corpse, bool locked) used = (item.IsMulti ? item.MultiGraphic : graphic, graphic == Item.CORPSE_GRAPHIC, locked);
            used.Should().Be(Old(item), $"item 0x{item.Serial:X8} rows {rows}");
            graphic.Should().Be(item.Graphic);
        }

        private static List<Item> Items(World world, Random rnd)
        {
            var items = new List<Item>();

            for (int i = 0; i < 400; i++)
            {
                Item it = world.GetOrCreateItem(ITEM0 + (uint)i);
                int kind = rnd.Next(100);
                it.TestSetGraphic(kind < 15 ? Item.CORPSE_GRAPHIC : (ushort)(0x0100 + rnd.Next(0x3F00)));
                it.TestSetFlags((rnd.Next(3) == 0 ? Flags.Movable : Flags.None) | (rnd.Next(5) == 0 ? Flags.Hidden : Flags.None));
                it.IsMulti = kind >= 90;
                world.Ecs.Sync(it);
                items.Add(it);
            }

            return items;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_Gives_The_Old_Getters_Values_For_Every_Kind_Of_Item(bool rows)
        {
            var world = new World();
            _tiles = Tiles();
            var rnd = new Random(3);
            List<Item> items = Items(world, rnd);

            foreach (Item it in items)
            {
                AssertSame(it, _tiles, rows);
            }

            // graphic and flag changes, a multi turned back, a corpse turned into an item and back
            for (int round = 0; round < 3; round++)
            {
                foreach (Item it in items)
                {
                    it.TestSetGraphic(rnd.Next(8) == 0 ? Item.CORPSE_GRAPHIC : (ushort)(0x0100 + rnd.Next(0x3F00)));
                    it.TestSetFlags(it.Flags ^ Flags.Movable);
                    it.IsMulti = !it.IsMulti && rnd.Next(10) == 0;
                    world.Ecs.Sync(it);
                    AssertSame(it, _tiles, rows);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_Item_Without_A_Record_Reads_The_Getters(bool rows)
        {
            var world = new World();
            _tiles = Tiles();
            // not in World: no record, the getters' defaults (graphic 0, no flags)
            var item = new Item(world);
            item.Serial = ITEM0 + 0x9000;

            item.TryScalarEntity(out _).Should().BeFalse();
            AssertSame(item, _tiles, rows);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void The_Switch_Follows_The_Override(bool? value, bool expected)
        {
            ItemPrep.OverrideForTests = value;

            if (value == null)
            {
                // the setting's default
                ItemPrep.Enabled.Should().Be(ClassicUO.Configuration.Settings.GlobalSettings.ItemPrepRows);
            }
            else
            {
                ItemPrep.Enabled.Should().Be(expected);
            }
        }

#if CUO_DEVTOOLS
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Checker_Finds_No_Mismatch_On_Consistent_Records(bool rows)
        {
            var world = new World();
            _tiles = Tiles();
            List<Item> items = Items(world, new Random(9));

            foreach (Item it in items)
            {
                ushort g = ItemPrep.Read(it, _tiles, rows, out bool locked);
                ItemPrep.Compare(it, _tiles, g, locked);
            }

            ItemPrep.Mismatches.Should().Be(0);
        }

        [Fact]
        public void The_Checker_Counts_A_Row_That_Disagrees_With_The_Getters()
        {
            var world = new World();
            _tiles = Tiles();
            Item it = world.GetOrCreateItem(ITEM0);
            it.TestSetGraphic(0x0200);
            world.Ecs.Sync(it);
            it.TryScalarEntity(out Friflo.Engine.ECS.Entity e).Should().BeTrue();

            // a row the mirror did not write (the case the checker exists for)
            ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.ItemDrawRow>(e).Graphic = Item.CORPSE_GRAPHIC;

            ushort g = ItemPrep.Read(it, _tiles, false, out bool locked);
            ItemPrep.Compare(it, _tiles, g, locked);
            ItemPrep.Mismatches.Should().Be(1, "the row read disagrees, the used (getter) read does not");

            g = ItemPrep.Read(it, _tiles, true, out locked);
            ItemPrep.Compare(it, _tiles, g, locked);
            ItemPrep.Mismatches.Should().Be(3, "with the switch on both the used and the row read disagree");
        }
#endif
    }
}
