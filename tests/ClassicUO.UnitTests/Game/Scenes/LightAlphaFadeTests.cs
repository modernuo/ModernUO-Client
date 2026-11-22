using System;
using System.IO;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Assets;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// light_alpha_fade (light-edges round 6b): surface glows fade with their emitter's drawn alpha, radial glows keep full
    /// light, occluder masks dithered to alpha (LightAlphaFade). The owner's spots: the Moonglow bank's lit windows under the
    /// circle of transparency (gradient radius 171 and full), Deceit's wall torches, the Moonglow street lamps.
    /// </summary>
    public sealed class LightAlphaFadeTests
    {
        private readonly ITestOutputHelper _output;

        public LightAlphaFadeTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            LightAlphaFade.OverrideForTests = on;

            try
            {
                Assert.Equal(on, LightAlphaFade.Enabled);
            }
            finally
            {
                LightAlphaFade.OverrideForTests = null;
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Masks_take_any_drawn_alpha_only_with_the_switch_on(bool on)
        {
            Assert.Equal(on ? 1 : LightOcclusion.MinOccluderAlpha, LightAlphaFade.MinMaskAlpha(on));
        }

        [Fact]
        public void Surface_by_the_table_else_by_the_emitters_wall_or_window_flags()
        {
            Assert.True(LightAlphaFade.IsSurface(LightClass.Surface, false));
            Assert.True(LightAlphaFade.IsSurface(LightClass.Surface, true));
            Assert.False(LightAlphaFade.IsSurface(LightClass.Radial, true));
            Assert.False(LightAlphaFade.IsSurface(LightClass.Radial, false));
            Assert.True(LightAlphaFade.IsSurface(LightClass.Unlisted, true));
            Assert.False(LightAlphaFade.IsSurface(LightClass.Unlisted, false));
        }

        [Fact]
        public void Circle_fade_is_the_shaders()
        {
            Assert.Equal(1f, LightAlphaFade.CotFade(10f, 0f));
            Assert.Equal(0f, LightAlphaFade.CotFade(0f, 171f));
            Assert.Equal(0f, LightAlphaFade.CotFade(0.849f * 171f, 171f));
            Assert.Equal(1f, LightAlphaFade.CotFade(171f, 171f));
            Assert.Equal(1f, LightAlphaFade.CotFade(500f, 171f));

            // IsometricWorld.fx: t = (ratio - 0.85) / 0.15, alpha t³, discarded below 0.02
            float t = (0.95f - 0.85f) / 0.15f;
            Assert.Equal(t * t * t, LightAlphaFade.CotFade(0.95f * 171f, 171f), 4);
            Assert.Equal(0f, LightAlphaFade.CotFade(0.86f * 171f, 171f));
        }

        [Fact]
        public void The_glow_scales_linearly_with_the_fade()
        {
            Assert.Equal(1f, LightAlphaFade.Fade(255, 1f));
            Assert.Equal(0f, LightAlphaFade.Fade(0, 1f));
            Assert.Equal(0f, LightAlphaFade.Fade(255, 0f));
            Assert.Equal(0.5f, LightAlphaFade.Fade(255, 0.5f), 4);

            // the additive blend takes the hue alpha twice (colour x alpha, then source alpha)
            for (float f = 0f; f <= 1f; f += 0.125f)
            {
                float a = LightAlphaFade.HueAlpha(f);
                Assert.Equal(f, a * a, 4);
            }
        }

        [Fact]
        public void Bayer_ranks_are_a_permutation_and_the_mask_covers_alpha()
        {
            Assert.Equal(Enumerable.Range(0, 16), Enumerable.Range(0, 16).Select(i => LightAlphaFade.Bayer(i & 3, i >> 2)).OrderBy(r => r));

            for (int k = 0; k <= 16; k++)
            {
                int kept = 0;

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        kept += LightAlphaFade.DitherKeeps(k / 16f, x + 100, y + 37) ? 1 : 0;
                    }
                }

                Assert.Equal(k * 4, kept);
            }

            // ordered: a pixel kept at one alpha is kept at every higher alpha (a fading roof's mask only shrinks)
            for (int i = 0; i < 16; i++)
            {
                for (int k = 1; k < 255; k++)
                {
                    Assert.True(!LightAlphaFade.DitherKeeps(k / 255f, i & 3, i >> 2) || LightAlphaFade.DitherKeeps((k + 1) / 255f, i & 3, i >> 2));
                }
            }
        }

        // what the light pass draws of a glow: the owner's acceptance cases in both switch states
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Hidden_window_gives_no_glow_half_faded_half_lamps_and_torches_full(bool on)
        {
            float Drawn(LightClass cls, bool wallOrWindow, byte alpha, float cot) =>
                LightAlphaFade.Share(on, LightAlphaFade.IsSurface(cls, wallOrWindow) ? LightAlphaFade.Fade(alpha, cot) : 1f);

            float full = LightAlphaFade.CotFade(0f, 171f), ring = LightAlphaFade.CotFade(171f * 0.97f, 171f);

            // bank window (shape 27) inside the full circle, under the gradient at half, faded out with its roof
            Assert.Equal(on ? 0f : 1f, Drawn(LightClass.Surface, true, 255, full));
            Assert.Equal(on ? 128 / 255f : 1f, Drawn(LightClass.Surface, true, 128, 1f), 4);
            Assert.Equal(on ? 0f : 1f, Drawn(LightClass.Surface, true, 0, 1f));
            Assert.Equal(on ? ring : 1f, Drawn(LightClass.Surface, true, 255, ring), 4);
            Assert.Equal(1f, Drawn(LightClass.Surface, true, 255, 1f));

            // Deceit wall torch, street lamp: radial, full whatever happens to their sprites
            Assert.Equal(1f, Drawn(LightClass.Radial, false, 255, full));
            Assert.Equal(1f, Drawn(LightClass.Radial, false, 0, 1f));
            Assert.Equal(1f, Drawn(LightClass.Radial, true, 40, full));
        }

        // the standard shapes' seed with the pack's tiledata: lit windows fade, every lamp, torch, sconce, candle and brazier
        // keeps its light; without a table the Wall/Window fallback gives the same answer for these
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Seed_classifies_the_pack_emitters_by_their_light_shape(bool withTable)
        {
            TileDataLoader.Read(Path.Combine(TileDrawOracleTests.PackDir(), TableConst.TileDataFile), out _, out StaticTiles[] statics);
            byte[] table = withTable ? LightClassFile.Seed() : Array.Empty<byte>();

            bool Surface(int g)
            {
                ref StaticTiles d = ref statics[g];
                LightClass cls = d.Layer < table.Length ? (LightClass)table[d.Layer] : LightClass.Unlisted;

                return LightAlphaFade.IsSurface(cls, d.IsWall || d.IsWindow);
            }

            // the Moonglow bank's lit windows (shapes 27 and 26)
            Assert.True(Surface(0x0162));
            Assert.True(Surface(0x0163));

            string[] radial = { "wall torch", "wall sconce", "lamp post", "candle", "brazier", "torch", "lantern" };
            int n = 0;

            for (int g = 0; g < statics.Length; g++)
            {
                StaticTiles d = statics[g];
                string name = d.Name;

                if (!d.IsLight || string.IsNullOrEmpty(name) || !radial.Any(r => name.Equals(r, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                n++;
                Assert.False(Surface(g), $"0x{g:X4} '{d.Name}' light {d.Layer} wall {d.IsWall} window {d.IsWindow}");
            }

            _output.WriteLine($"{n} radial emitters checked");
            Assert.True(n > 10);
        }
    }
}
