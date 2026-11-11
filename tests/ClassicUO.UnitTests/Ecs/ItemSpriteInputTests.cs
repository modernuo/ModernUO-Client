using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T7b: ItemSpriteInput.ReadRecord reads the mirror record (one EntityData, ItemData only when UsesItemData) and falls
    // back to the GameObject without a record. Both must give the select the same values.
    public class ItemSpriteInputTests
    {
        private const ushort STACKABLE = 0x0F0E, PLAIN = 0x0E75, MULTI = 0x0050;

        private static StaticTiles[] Tiles()
        {
            var tiles = new StaticTiles[0x10000];
            tiles[STACKABLE] = new StaticTiles((ulong)(TileFlag.Generic | TileFlag.PartialHue), 1, 0, 0, 0, 0, 0, 1, null);
            tiles[PLAIN] = new StaticTiles((ulong)TileFlag.Container, 120, 0, 0, 0, 0, 0, 1, null);
            tiles[0x0EED] = new StaticTiles((ulong)TileFlag.Generic, 0, 0, 0, 0, 0, 0, 1, null);
            tiles[0x2006] = new StaticTiles((ulong)TileFlag.Container, 0, 0, 0, 0, 0, 0, 1, null);

            return tiles;
        }

        private static Item Make(World world, uint serial, ushort graphic, ushort amount, Layer layer, ushort hue, Flags flags, bool multi, bool sync)
        {
            // without a record: a detached instance (as the house placement preview), which TryScalarEntity rejects
            Item it = sync ? world.GetOrCreateItem(serial) : Item.Create(world, serial);
            it.Graphic = graphic;
            it.TestSetAmount(amount);
            it.TestSetLayer(layer);
            it.Hue = hue;
            it.Flags = flags;
            it.X = (ushort)(1000 + serial % 7);
            it.Y = (ushort)(2000 + serial % 5);
            it.IsMulti = multi;

            if (sync)
            {
                world.Ecs.Sync(it);
            }

            return it;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Record_And_Fallback_Give_The_Select_The_Same_Values(bool record)
        {
            var world = new World();
            StaticTiles[] tiles = Tiles();
            Item[] items =
            {
                Make(world, 0x4000_0001, PLAIN, 1, Layer.Invalid, 0x0021, Flags.Movable, false, record),
                Make(world, 0x4000_0002, STACKABLE, 37, Layer.Invalid, 0, Flags.Hidden, false, record),
                Make(world, 0x4000_0003, 0x0EED, 1200, Layer.Invalid, 0, Flags.None, false, record),
                Make(world, 0x4000_0004, 0x2006, 0x0190, (Layer)5, 0x0455, Flags.None, false, record),
                Make(world, 0x4000_0005, MULTI, 1, Layer.Invalid, 0, Flags.None, true, record)
            };

            foreach (Item item in items)
            {
                item.TryScalarEntity(out _).Should().Be(record);
                ItemSpriteInput i = default;
                ItemSpriteInput.ReadRecord(item, tiles, ref i, out ushort y);

                i.Serial.Should().Be(item.Serial);
                i.Graphic.Should().Be(item.Graphic);
                i.Hue.Should().Be(item.Hue);
                i.X.Should().Be(item.X);
                y.Should().Be(item.Y);
                i.Hidden.Should().Be(item.IsHidden);
                i.Movable.Should().Be((item.Flags & Flags.Movable) != 0);
                i.OnGround.Should().Be(item.OnGround);
                i.Multi.Should().Be(item.IsMulti);
                i.TileStackable.Should().Be(tiles[item.IsMulti ? item.MultiGraphic : item.Graphic].IsStackable);
                i.TileHeavy.Should().Be(tiles[item.IsMulti ? item.MultiGraphic : item.Graphic].Weight > 90);

                if (i.UsesItemData)
                {
                    i.Amount.Should().Be(item.Amount);
                    i.Layer.Should().Be((byte)item.Layer);
                }
            }

            // the coin, the stack and the corpse need Amount/Layer; the plain container and the multi do not
            ItemSpriteInput plain = default, multi = default, stack = default, coin = default, corpse = default;
            ItemSpriteInput.ReadRecord(items[0], tiles, ref plain, out _);
            ItemSpriteInput.ReadRecord(items[1], tiles, ref stack, out _);
            ItemSpriteInput.ReadRecord(items[2], tiles, ref coin, out _);
            ItemSpriteInput.ReadRecord(items[3], tiles, ref corpse, out _);
            ItemSpriteInput.ReadRecord(items[4], tiles, ref multi, out _);
            plain.UsesItemData.Should().BeFalse();
            multi.UsesItemData.Should().BeFalse();
            stack.UsesItemData.Should().BeTrue();
            coin.UsesItemData.Should().BeTrue();
            corpse.UsesItemData.Should().BeTrue();
            coin.DisplayedGraphic.Should().Be(0x0EED + 2);
            corpse.Layer.Should().Be(5);
        }
    }
}
