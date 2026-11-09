using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // legacy_ecs_sprites: MobileSpriteExtract.DrawLegacy replays the Legacy calls MobileFrameCache recorded. Golden: the batcher
    // calls of a fresh select + resolve at the same draw origin and depth (which the tests above hold to the view's calls).
    public partial class DynamicSpriteTests
    {
        private sealed class CallRecorder : ISpriteCapture
        {
            public readonly List<LegacyDraw> Calls = new List<LegacyDraw>();

            public bool Draw(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, bool scaled, float depth, SpriteCaptureMode mode)
            {
                Calls.Add(new LegacyDraw { Texture = texture, Position = position, Source = source, Hue = hue, Depth = depth, Flip = flip, Mode = mode });

                return true;
            }

            public bool DrawRotated(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, float rotation, float depth) => true;

            public bool DrawSitted(Texture2D texture, Vector2 position, Rectangle source, Vector3 mod, Vector3 hue, bool flip, float depth)
            {
                Calls.Add(new LegacyDraw
                {
                    Texture = texture, Position = position, Source = source, Hue = hue, Depth = depth, Flip = flip, Mode = SpriteCaptureMode.Sitted,
                    SitMod = mod
                });

                return true;
            }
        }

        // a batcher that only reports its calls (no device: every call returns at the capture)
        private static UltimaBatcher2D RecordingBatcher(out CallRecorder rec)
        {
            var batcher = (UltimaBatcher2D)RuntimeHelpers.GetUninitializedObject(typeof(UltimaBatcher2D));
            rec = new CallRecorder();
            batcher.Capture = rec;

            return batcher;
        }

        private static List<LegacyDraw> FreshLegacyCalls(MobileSpriteExtract extract, FakeAtlas atlas, EcsEntity e, int x, int y, float depth,
            out Rectangle fi, out MobileSpriteExtract.LightRequest[] lights)
        {
            UltimaBatcher2D batcher = RecordingBatcher(out CallRecorder rec);
            extract.BeginSelect(atlas);
            extract.Select(e, x, y, depth);
            LegacySpriteResolve.DrawAll(batcher, atlas, extract.Selected);
            fi = extract.FrameInfo;
            lights = extract.Lights.ToArray();

            return rec.Calls;
        }

        private static List<LegacyDraw> DrawLegacyCalls(MobileSpriteExtract extract, FakeAtlas atlas, EcsEntity e, int x, int y, float depth,
            MobileFrameCache cache)
        {
            UltimaBatcher2D batcher = RecordingBatcher(out CallRecorder rec);
            EcsRow data = EcsRead.Row(e);
            extract.DrawLegacy(batcher, atlas, e, data, in data.Get<AnimationState>(), in data.Get<MobileRenderState>(), x, y, depth, cache);

            return rec.Calls;
        }

        private static void AssertSameLegacy(List<LegacyDraw> want, Rectangle wantFi, MobileSpriteExtract.LightRequest[] wantLights,
            List<LegacyDraw> got, MobileSpriteExtract extract, string name)
        {
            AssertSameCalls(want, got, name);
            Assert.Equal(wantFi, extract.FrameInfo);
            MobileSpriteExtract.LightRequest[] lights = extract.Lights.ToArray();
            Assert.Equal(wantLights.Length, lights.Length);

            for (int i = 0; i < lights.Length; i++)
            {
                Assert.Equal((wantLights[i].Item, wantLights[i].X, wantLights[i].Y), (lights[i].Item, lights[i].X, lights[i].Y));
            }
        }

        [Theory]
        [MemberData(nameof(MobileCases))]
        public void Legacy_Mobile_Replay_Makes_The_Calls_Of_A_Fresh_Select(string name)
        {
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, System.Array.Empty<SpriteEntry>());
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);
            var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };
            // the camera and the base depth move between frames; the walking offset too
            (int X, int Y, float Depth, short Ox)[] frames = { (600, 400, 812.25f, 0), (600, 400, 812.25f, 0), (644, 371, 900.5f, 0), (650, 380, 77f, -3) };
            long hits = MobileFrameCache.LegacyHits;

            foreach ((int x, int y, float depth, short ox) in frames)
            {
                e.GetComponent<MobileRenderState>().OffsetX = ox;
                List<LegacyDraw> want = FreshLegacyCalls(extract, atlas, e, x, y, depth, out Rectangle fi, out MobileSpriteExtract.LightRequest[] lights);
                List<LegacyDraw> got = DrawLegacyCalls(extract, atlas, e, x, y, depth, cache);
                AssertSameLegacy(want, fi, lights, got, extract, name);
            }

            // a seated mobile is not recorded (its equipment reads the last seated body's rows)
            Assert.Equal(name != "sitting", cache.LegacyValid);
            Assert.Equal(name == "sitting" ? 0 : frames.Length - 1, MobileFrameCache.LegacyHits - hits);
        }

        [Theory]
        [InlineData("mounted", HORSE)]
        [InlineData("equipped", ROBE)]
        public void Legacy_Mobile_Select_With_A_Decoding_Layer_Is_Not_Replayed(string name, ushort pending)
        {
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, System.Array.Empty<SpriteEntry>()) { PendingBody = pending };
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);
            var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

            List<LegacyDraw> want = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out Rectangle fi, out MobileSpriteExtract.LightRequest[] lights);
            List<LegacyDraw> got = DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache);

            AssertSameLegacy(want, fi, lights, got, extract, name);
            Assert.False(cache.LegacyValid);
        }

        [Fact]
        public void Legacy_Mobile_Replay_Follows_Selection_Alpha_And_Hues()
        {
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, System.Array.Empty<SpriteEntry>());
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, "equipped");
            var extract = new MobileSpriteExtract(anims);
            var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

            void Frame(string step, bool replayed)
            {
                long hits = MobileFrameCache.LegacyHits;
                List<LegacyDraw> want = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out Rectangle fi, out MobileSpriteExtract.LightRequest[] lights);
                List<LegacyDraw> got = DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache);
                AssertSameLegacy(want, fi, lights, got, extract, step);
                Assert.True(replayed == (MobileFrameCache.LegacyHits != hits), step);
            }

            Frame("first", false);
            Frame("same", true);
            e.GetComponent<MobileRenderState>().Alpha = 120;
            Frame("alpha", false);
            Frame("alpha again", true);
            e.GetComponent<MobileRenderState>().OverrideHue = 0x0035;
            Frame("override hue", false);
            e.GetComponent<Hue>().Value = 0x0021;
            Frame("body hue", false);
            e.GetComponent<AnimationState>().Frame = 2;
            cache.SelectGen++;
            Frame("new selection", false);
            Frame("new selection again", true);
            cache.SelectValid = false;
            cache.SelectGen++;
            Frame("uncacheable selection", false);
            Assert.False(cache.LegacyValid);
            e.GetComponent<AnimationState>().Flags &= ~AnimStateFlags.Visible;
            Assert.Empty(DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache));
            Assert.False(cache.LegacyValid);
        }
    }
}
