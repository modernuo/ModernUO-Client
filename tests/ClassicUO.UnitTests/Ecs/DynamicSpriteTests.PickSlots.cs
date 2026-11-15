using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    public partial class DynamicSpriteTests
    {
        // SpritePickTable entries are not zeroed per record: a slot reused by another object of another kind serves only
        // what that object's record wrote
        [Fact]
        public void Pick_Records_A_Reused_Slot_Serves_Only_Its_New_Record()
        {
            SpritePickTable.Reset();
            var world = new World();
            Mobile m = PickMobile(world, 0x101);
            Item art = Item.Create(world, 0x40000101), corpse = Item.Create(world, 0x40000102), other = Item.Create(world, 0x40000103);

            // draw 1 fills slots 0 and 1 of buffer A with art items, draw 2 slot 0 of buffer B with a mobile
            SpritePickTable.BeginDraw();
            SpritePickTable.RecordItem(art, false, false, true, 0x0E75, 7, 9);
            SpritePickTable.RecordItem(other, false, false, true, 0x0E76, 1, 2);
            SpritePickTable.Commit();
            SpritePickTable.BeginDraw();
            SpritePickTable.RecordMobile(m, new Rectangle(1, 2, 3, 4), true);
            SpritePickTable.Commit();

            // draw 3 reuses buffer A: a corpse in the art item's slot, nothing in the second
            SpritePickTable.BeginDraw();
            SpritePickTable.RecordItem(corpse, true, true, false, 0, 0, 0);
            SpritePickTable.Commit();

            Assert.True(SpritePickTable.TryCorpse(corpse, out bool mirror));
            Assert.True(mirror);
            Assert.False(SpritePickTable.TryItemArt(corpse, 0x0E75, default(ArtEntry), out _, out _));
            Assert.False(SpritePickTable.TryItemArt(art, 0x0E75, default(ArtEntry), out _, out _));
            Assert.False(SpritePickTable.TryItemArt(other, 0x0E76, default(ArtEntry), out _, out _));
            Assert.False(SpritePickTable.TryMobile(m, out _, out _));

            // draw 4 reuses buffer B: an art item in the mobile's slot
            SpritePickTable.BeginDraw();
            SpritePickTable.RecordItem(art, false, false, true, 0x0E75, 7, 9);
            SpritePickTable.Commit();

            Assert.True(SpritePickTable.TryItemArt(art, 0x0E75, default(ArtEntry), out int w, out int h));
            Assert.Equal((7, 9), (w, h));
            Assert.False(SpritePickTable.TryCorpse(art, out _));
            Assert.False(SpritePickTable.TryMobile(m, out _, out _));
            SpritePickTable.Reset();
        }
    }
}
