using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // The Legacy sink draws an item's records as the select makes them (ItemSpriteExtract.SelectDraw). Golden: the
    // batcher calls and side effects of Select + LegacySpriteResolve.DrawAll (which the tests above hold to the view's calls).
    public partial class DynamicSpriteTests
    {
        public static TheoryData<string> SelectDrawItemCases
        {
            get
            {
                var cases = new TheoryData<string>();

                foreach (string name in new[] { "plain", "hued", "partial", "stack", "coins", "hidden", "highlight", "preview", "light", "multi", "fading", "missing", "missing-stack" })
                {
                    cases.Add(name);
                }

                return cases;
            }
        }

        [Theory]
        [MemberData(nameof(SelectDrawItemCases))]
        public void Legacy_Item_SelectDraw_Makes_The_Calls_Of_Select_And_DrawAll(string name)
        {
            (ArtLoader arts, GpuSpriteTables tables) = ArtFixture();
            var atlas = new FakeAtlas(null, tables.Sprites);
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase(name);

            UltimaBatcher2D wantBatcher = RecordingBatcher(out CallRecorder want);
            bool wantDrawn = extract.Select(in input, false, 700, 300, 455.5f);
            LegacySpriteResolve.DrawAll(wantBatcher, atlas, extract.Selected);
            var wantSide = (extract.Light, extract.LightX, extract.LightY, extract.SetsFlipped, extract.HasArtPick, extract.ArtPickGraphic,
                extract.ArtPickW, extract.ArtPickH);

            UltimaBatcher2D gotBatcher = RecordingBatcher(out CallRecorder got);
            bool gotDrawn = extract.SelectDraw(gotBatcher, atlas, in input, false, 700, 300, 455.5f);

            Assert.Equal(wantDrawn, gotDrawn);
            Assert.True(want.Calls.Count > 0, name);
            AssertSameCalls(want.Calls, got.Calls, name);
            Assert.Equal(wantSide, (extract.Light, extract.LightX, extract.LightY, extract.SetsFlipped, extract.HasArtPick, extract.ArtPickGraphic,
                extract.ArtPickW, extract.ArtPickH));
            Assert.Equal(0, extract.Selected.Length);

            // the next select is a plain select again (no batcher left behind)
            int drawnBefore = got.Calls.Count;
            extract.Select(in input, false, 700, 300, 455.5f);
            Assert.True(extract.Selected.Length > 0, name);
            Assert.Equal(drawnBefore, got.Calls.Count);
        }
    }
}
