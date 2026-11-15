using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// mesh_hue_on_change: MeshHueEpoch.Apply (the code GameScene.ApplyMeshHue runs for every visible meshed sprite) on a
    /// real MeshLayer, with the switch off (the per-frame recompute) and on (recompute only on an input change).
    /// </summary>
    public sealed class MeshHueTests
    {
        private sealed class Counter
        {
            public int Computes;
            public float X, Y;
        }

        private struct FakeSource : IMeshHueSource
        {
            public Counter C;

            public void HueOf(out float hueX, out float hueY)
            {
                C.Computes++;
                hueX = C.X;
                hueY = C.Y;
            }
        }

        private static MeshLayer Layer(int count)
        {
            var layer = new MeshLayer();
            layer.EnsureCapacity(count);
            layer.Count = count;

            return layer;
        }

        private static float HueX(MeshLayer layer, int index)
        {
            layer.GetHue(index, out float x, out _);

            return x;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Hue_follows_its_inputs_in_both_switch_states(bool onChange)
        {
            var layer = Layer(4);
            var epoch = new MeshHueEpoch();
            var c = new Counter { X = 10, Y = 1 };
            var src = new FakeSource { C = c };

            epoch.Update(false, 0, 0, 18, false);
            MeshHueEpoch.Apply(layer, 2, onChange, epoch.Epoch, 0x10, 11, ref src);
            HueX(layer, 2).Should().Be(10);

            // a stationary frame: nothing changed
            epoch.Update(false, 0, 0, 18, false);
            bool kept = MeshHueEpoch.Apply(layer, 2, onChange, epoch.Epoch, 0x10, 11, ref src);
            kept.Should().Be(onChange);
            c.Computes.Should().Be(onChange ? 1 : 2);

            // the object's hue changes
            c.X = 20;
            MeshHueEpoch.Apply(layer, 2, onChange, epoch.Epoch, 0x10, 21, ref src);
            HueX(layer, 2).Should().Be(20);

            // a frame-wide input changes (the player died with the black-and-white effect on)
            c.X = 30;
            epoch.Update(false, 0, 0, 18, true);
            MeshHueEpoch.Apply(layer, 2, onChange, epoch.Epoch, 0x10, 21, ref src);
            HueX(layer, 2).Should().Be(30);
        }

        [Fact]
        public void A_keyless_set_like_the_highlight_is_recomputed_next_time()
        {
            var layer = Layer(2);
            var epoch = new MeshHueEpoch();
            var c = new Counter { X = 5, Y = 1 };
            var src = new FakeSource { C = c };

            MeshHueEpoch.Apply(layer, 0, true, epoch.Epoch, 0x10, 6, ref src);
            layer.SetHue(0, 0x34, 1);
            layer.HueKeyAt(0).Should().Be(0);

            MeshHueEpoch.Apply(layer, 0, true, epoch.Epoch, 0x10, 6, ref src).Should().BeFalse();
            HueX(layer, 0).Should().Be(5);
        }

        [Fact]
        public void Turning_the_switch_off_and_on_again_recomputes()
        {
            var layer = Layer(2);
            var epoch = new MeshHueEpoch();
            var c = new Counter { X = 5, Y = 1 };
            var src = new FakeSource { C = c };

            MeshHueEpoch.Apply(layer, 0, true, epoch.Epoch, 0x10, 6, ref src);

            // off (BeginMeshHueFrame bumps on every switch change): the per-frame path writes, the key stays
            epoch.Bump();
            c.X = 7;
            MeshHueEpoch.Apply(layer, 0, false, epoch.Epoch, 0x10, 6, ref src);
            HueX(layer, 0).Should().Be(7);

            // on again with the inputs as they were in the first on period
            epoch.Bump();
            c.X = 5;
            MeshHueEpoch.Apply(layer, 0, true, epoch.Epoch, 0x10, 6, ref src).Should().BeFalse();
            HueX(layer, 0).Should().Be(5);
        }

        // MeshHueOf's choice for a hued static: partial-hue shader if the graphic's tiledata says so. Static.ItemData reads
        // Client.Game's tiledata, so the flag comes from this table, indexed by the real object's current graphic.
        private struct StaticSource : IMeshHueSource
        {
            public Static Obj;
            public bool[] Partial;

            public void HueOf(out float hueX, out float hueY)
            {
                hueX = Obj.Hue - 1;
                hueY = Partial[Obj.Graphic] ? ShaderHueTranslator.SHADER_PARTIAL_HUED : ShaderHueTranslator.SHADER_HUED;
            }
        }

        [Fact]
        public void An_in_place_graphic_swap_that_flips_partial_hue_is_recomputed()
        {
            // World.ChangeSeason -> Static.UpdateGraphicBySeason -> SetGraphic changes a placed static's graphic without a
            // mesh rebuild; the new graphic can differ in the partial-hue flag (Codex review of bg/pa-meshhue, P2)
            const ushort Summer = 0x0CA0, Winter = 0x0CA1;
            var partial = new bool[0x10000];
            partial[Winter] = true;
            var s = new Static(new World()) { Hue = 0x21 };
            s.SetGraphic(Summer);

            var layer = Layer(1);
            var epoch = new MeshHueEpoch();
            epoch.Update(false, 0, 0, 18, false);
            var src = new StaticSource { Obj = s, Partial = partial };

            MeshHueEpoch.Apply(layer, 0, true, epoch.Epoch, s.Graphic, s.Hue, ref src);
            layer.GetHue(0, out _, out float y);
            y.Should().Be(ShaderHueTranslator.SHADER_HUED);
            MeshHueEpoch.Apply(layer, 0, true, epoch.Epoch, s.Graphic, s.Hue, ref src).Should().BeTrue();

            s.SetGraphic(Winter);
            epoch.Update(false, 0, 0, 18, false); // no frame-wide input changed
            MeshHueEpoch.Apply(layer, 0, true, epoch.Epoch, s.Graphic, s.Hue, ref src).Should().BeFalse();
            layer.GetHue(0, out float x, out y);
            x.Should().Be(0x20);
            y.Should().Be(ShaderHueTranslator.SHADER_PARTIAL_HUED);
        }

        [Fact]
        public void A_rebuild_resets_the_keys()
        {
            var layer = Layer(2);
            var epoch = new MeshHueEpoch();
            var src = new FakeSource { C = new Counter { X = 5, Y = 1 } };

            MeshHueEpoch.Apply(layer, 1, true, epoch.Epoch, 0x10, 6, ref src);
            layer.HueKeyAt(1).Should().NotBe(0);

            layer.Reset();
            layer.HueKeyAt(1).Should().Be(0);

            MeshHueEpoch.Apply(layer, 1, true, epoch.Epoch, 0x10, 6, ref src);
            layer.SoftReset();
            layer.HueKeyAt(1).Should().Be(0);
        }

        [Fact]
        public void Growing_the_layer_keeps_the_keys()
        {
            var layer = Layer(2);
            var epoch = new MeshHueEpoch();
            var src = new FakeSource { C = new Counter { X = 5, Y = 1 } };

            MeshHueEpoch.Apply(layer, 1, true, epoch.Epoch, 0x10, 6, ref src);
            long key = layer.HueKeyAt(1);
            layer.EnsureCapacity(1000);

            layer.HueKeyAt(1).Should().Be(key);
            layer.HueKeyAt(999).Should().Be(0);
        }

        [Fact]
        public void The_range_centre_counts_only_with_the_out_of_range_tint()
        {
            var epoch = new MeshHueEpoch();
            epoch.Update(false, 100, 100, 18, false);
            int e = epoch.Epoch;

            epoch.Update(false, 101, 100, 17, false);
            epoch.Epoch.Should().Be(e);

            epoch.Update(true, 101, 100, 17, false);
            epoch.Epoch.Should().NotBe(e);
            e = epoch.Epoch;

            epoch.Update(true, 101, 100, 17, false);
            epoch.Epoch.Should().Be(e);

            epoch.Update(true, 101, 101, 17, false);
            epoch.Epoch.Should().NotBe(e);
            e = epoch.Epoch;

            epoch.Update(true, 101, 101, 16, false);
            epoch.Epoch.Should().NotBe(e);
        }

        [Fact]
        public void Keys_are_never_zero()
        {
            MeshLayer.HueKey(1, 0x10, 0).Should().NotBe(0);
            MeshLayer.HueKey(int.MinValue, 0x10, 0).Should().NotBe(0);
            MeshLayer.HueKey(1, 0x10, 5).Should().NotBe(MeshLayer.HueKey(2, 0x10, 5));
            MeshLayer.HueKey(1, 0x10, 5).Should().NotBe(MeshLayer.HueKey(1, 0x10, 6));
        }
    }
}
