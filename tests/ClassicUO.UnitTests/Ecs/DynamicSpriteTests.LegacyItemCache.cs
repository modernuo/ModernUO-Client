using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // legacy-sprites-fix-2, legacy_item_draw_cache: the Legacy sink replays an art item's recorded batcher calls. Golden: the
    // calls and side outputs of a fresh SelectDraw at the replay's position and depth. Switch off (no record) and on (record)
    // make the same calls; the placement preview and corpses are not recorded; the key and the row version follow the inputs.
    public partial class DynamicSpriteTests
    {
        [Theory]
        [MemberData(nameof(SelectDrawItemCases))]
        public void Legacy_Item_Cache_Replay_Makes_The_Calls_Of_A_Fresh_SelectDraw(string name)
        {
            (ArtLoader arts, GpuSpriteTables tables) = ArtFixture();
            var atlas = new FakeAtlas(null, tables.Sprites);
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase(name);

            // switch off: SelectDraw without a record; switch on: the same calls, recorded
            UltimaBatcher2D offBatcher = RecordingBatcher(out CallRecorder off);
            bool offDrawn = extract.SelectDraw(offBatcher, atlas, in input, false, 700, 300, 455.5f);
            var cache = new ItemDrawCache();
            UltimaBatcher2D onBatcher = RecordingBatcher(out CallRecorder on);
            bool onDrawn = extract.SelectDraw(onBatcher, atlas, in input, false, 700, 300, 455.5f, cache, 7);
            Assert.Equal(offDrawn, onDrawn);
            AssertSameCalls(off.Calls, on.Calls, name);

            if (name == "preview")
            {
                Assert.False(cache.Valid);

                return;
            }

            Assert.True(cache.Valid, name);
            Assert.Equal(7u, cache.Version);
            Assert.Equal(on.Calls.Count, cache.Calls);

            // the replay at another position and depth against a fresh draw there
            UltimaBatcher2D wantBatcher = RecordingBatcher(out CallRecorder want);
            extract.SelectDraw(wantBatcher, atlas, in input, false, 640, 285, 470.25f);
            UltimaBatcher2D gotBatcher = RecordingBatcher(out CallRecorder got);
            cache.Replay(gotBatcher, 640, 285, 470.25f);
            AssertSameCalls(want.Calls, got.Calls, name);

            for (int k = 0; k < cache.Calls; k++)
            {
                Assert.True(cache.Call(k, 640, 285, 470.25f).Same(want.Calls[k]), name);
            }

            Assert.Equal((extract.Light, extract.HasArtPick, extract.ArtPickGraphic, extract.ArtPickW, extract.ArtPickH),
                (cache.Light, cache.HasArtPick, cache.PickGraphic, cache.PickW, cache.PickH));
            // the sink's replay adds the light at the screen position + 22, as the select
            if (extract.Light)
            {
                Assert.Equal((640 + 22, 285 + 22), (extract.LightX, extract.LightY));
            }

            Assert.Equal(name == "light", cache.Light);
            Assert.False(extract.SetsFlipped);
        }

        [Fact]
        public void Legacy_Item_Cache_Key_Follows_The_Inputs()
        {
            (ArtLoader arts, GpuSpriteTables tables) = ArtFixture();
            var atlas = new FakeAtlas(null, tables.Sprites);
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase("fading");
            input.Selected = true;
            input.TileContainer = true;
            var cache = new ItemDrawCache();
            extract.SelectDraw(RecordingBatcher(out _), atlas, in input, false, 700, 300, 455.5f, cache, 3);

            Assert.Equal((byte)90, cache.Alpha);
            Assert.True(cache.TileContainer);
            Assert.Equal(ItemDrawCache.SELECTED, cache.Scene);
            Assert.True(cache.Matches(new Vector3(2, -3, 1), 0, ItemDrawCache.SELECTED));
            Assert.False(cache.Matches(new Vector3(2, -3, 0), 0, ItemDrawCache.SELECTED));
            Assert.False(cache.Matches(new Vector3(2, -3, 1), 0x1F14, ItemDrawCache.SELECTED));
            Assert.False(cache.Matches(new Vector3(2, -3, 1), 0, 0));

            Assert.Equal(ItemDrawCache.HIGHLIGHT | ItemDrawCache.OUT_OF_RANGE | ItemDrawCache.DEAD_GRAY | ItemDrawCache.SELECTED_CONTAINER |
                         ItemDrawCache.REPLACE_FIELDS, ItemSpriteInput.SceneBits(false, true, true, true, true, true));

            // without a version (no record) nothing is cached
            extract.SelectDraw(RecordingBatcher(out _), atlas, in input, false, 700, 300, 455.5f, cache, 0);
            Assert.False(cache.Valid);

            // a corpse is not cached
            ItemSpriteInput corpse = ArtItem(0x2006);
            extract.SelectDraw(RecordingBatcher(out _), atlas, in corpse, false, 700, 300, 455.5f, cache, 3);
            Assert.False(cache.Valid);
        }

        [Fact]
        public void Legacy_Item_Cache_Row_Version_Moves_Only_When_The_Row_Does()
        {
            var world = new World();
            Item a = world.GetOrCreateItem(0x4000_0010), b = world.GetOrCreateItem(0x4000_0011);
            a.Graphic = 0x0E75;
            b.Graphic = 0x0E75;
            world.Ecs.Sync(a);
            world.Ecs.Sync(b);

            uint va = ItemSpriteInput.DrawVersion(a), vb = ItemSpriteInput.DrawVersion(b);
            Assert.NotEqual(0u, va);
            Assert.NotEqual(0u, vb);
            Assert.NotEqual(va, vb);

            world.Ecs.Sync(a);
            Assert.Equal(va, ItemSpriteInput.DrawVersion(a));

            a.Hue = 0x0021;
            world.Ecs.Sync(a);
            uint va2 = ItemSpriteInput.DrawVersion(a);
            Assert.NotEqual(va, va2);

            a.X = 1501;
            world.Ecs.Sync(a);
            Assert.NotEqual(va2, ItemSpriteInput.DrawVersion(a));
            Assert.Equal(vb, ItemSpriteInput.DrawVersion(b));
        }

        [Fact]
        public void Legacy_Item_Cache_Switch_Is_Off_By_Default_And_The_Override_Wins()
        {
            Assert.False(new ClassicUO.Configuration.Settings().LegacyItemDrawCache);
            ItemDrawCache.Override = false;
            Assert.False(ItemDrawCache.On);
            ItemDrawCache.Override = true;
            Assert.True(ItemDrawCache.On);
            ItemDrawCache.Override = null;
        }
    }
}
