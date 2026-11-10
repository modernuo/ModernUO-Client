using System;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // legacy-sprites-fix, ecs_item_draw_row: Gather's record read from the packed ItemDrawRow (switch on) gives the input the
    // separate components give (switch off), for every kind of item and after every kind of record write; the row follows
    // the record through updates, moves into and out of containers and the multi flag.
    public class ItemDrawRowTests
    {
        private const uint ITEM0 = 0x4000_0000;

        private static StaticTiles[] Tiles()
        {
            var tiles = new StaticTiles[0x10000];
            var rnd = new Random(5);

            for (int g = 0; g < tiles.Length; g++)
            {
                ulong flags = (ulong)(rnd.Next(3) == 0 ? TileFlag.Generic : 0) | (ulong)(rnd.Next(8) == 0 ? TileFlag.PartialHue : 0);
                tiles[g] = new StaticTiles(flags, (byte)rnd.Next(256), 0, 0, 0, 0, 0, 1, null);
            }

            return tiles;
        }

        private static void AssertSameInput(Item it, StaticTiles[] tiles)
        {
            ItemSpriteInput a = default, b = default;
            bool ra = ItemSpriteInput.ReadRecord(it, tiles, ref a, out ushort ya, out _, false);
            bool rb = ItemSpriteInput.ReadRecord(it, tiles, ref b, out ushort yb, out _, true);
            Assert.Equal(ra, rb);
            Assert.Equal(ya, yb);
            Assert.Equal(
                (a.Serial, a.Graphic, a.Hue, a.Amount, a.X, a.Layer, a.OnGround, a.Multi, a.Hidden, a.Movable, a.TileLight, a.TilePartialHue,
                    a.TileAnimated, a.TileContainer, a.TileStackable, a.TileHeavy, a.MultiGraphic),
                (b.Serial, b.Graphic, b.Hue, b.Amount, b.X, b.Layer, b.OnGround, b.Multi, b.Hidden, b.Movable, b.TileLight, b.TilePartialHue,
                    b.TileAnimated, b.TileContainer, b.TileStackable, b.TileHeavy, b.MultiGraphic));
        }

        [Fact]
        public void Row_Read_Equals_Component_Read_For_Every_Kind_Of_Item()
        {
            var world = new World();
            StaticTiles[] tiles = Tiles();
            var rnd = new Random(11);
            var items = new Item[300];
            long diffs = ItemDrawRows.Diffs;

            for (int i = 0; i < items.Length; i++)
            {
                Item it = world.GetOrCreateItem(ITEM0 + (uint)i);
                int kind = rnd.Next(100);
                it.Graphic = kind < 10 ? (ushort)0x0EED : kind < 15 ? (ushort)0x2006 : (ushort)(0x0100 + rnd.Next(0x3F00));
                it.Hue = (ushort)rnd.Next(0, 0x0BB8);
                it.Amount = kind < 30 ? (ushort)rnd.Next(2, 500) : (ushort)1;
                it.X = (ushort)(1400 + rnd.Next(64));
                it.Y = (ushort)(1600 + rnd.Next(64));
                it.Z = (sbyte)rnd.Next(-5, 40);
                it.Flags = (rnd.Next(4) == 0 ? Flags.Movable : Flags.None) | (rnd.Next(5) == 0 ? Flags.Hidden : Flags.None);
                it.IsMulti = kind >= 95;
                it.Layer = (Layer)rnd.Next(0, 25);
                world.Ecs.Sync(it);
                items[i] = it;
                AssertSameInput(it, tiles);
            }

            // updates of every value, the multi flag flipped back, the same record written again
            for (int round = 0; round < 3; round++)
            {
                foreach (Item it in items)
                {
                    it.Hue = (ushort)rnd.Next(0, 0x0BB8);
                    it.X++;
                    it.Z = (sbyte)rnd.Next(-5, 40);
                    it.Amount = (ushort)rnd.Next(1, 600);
                    it.Flags ^= Flags.Hidden;
                    it.IsMulti = !it.IsMulti && rnd.Next(10) == 0;
                    world.Ecs.Sync(it);
                    AssertSameInput(it, tiles);
                }
            }

            ItemDrawRows.Override = true;

            try
            {
                Assert.True(ItemDrawRows.On);
            }
            finally
            {
                ItemDrawRows.Override = null;
            }

            Assert.Equal(diffs, ItemDrawRows.Diffs);
        }

        [Fact]
        public void Switch_Is_Off_By_Default_And_The_Override_Wins()
        {
            Assert.False(new ClassicUO.Configuration.Settings().EcsItemDrawRow);
            ItemDrawRows.Override = false;
            Assert.False(ItemDrawRows.On);
            ItemDrawRows.Override = true;
            Assert.True(ItemDrawRows.On);
            ItemDrawRows.Override = null;
        }
    }
}
