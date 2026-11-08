using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T7b part 3: the effect extract. Golden: GameEffectView.Draw and DragEffect.Draw transcribed (hue choice, offsets, source
    // depth, View.DrawStaticRotated / View.DrawStatic geometry, light), with the Legacy batcher call over the fake atlas and the
    // Gpu instance through a verbatim copy of GpuWorldRenderer.Emit (art: the source rect is the whole untrimmed sprite). The
    // select's records must resolve to the same call / instance. In the client, CUO_EFFECT_SPRITE_CHECK compares the real
    // views with the extract every frame.
    public partial class DynamicSpriteTests
    {
        private const ushort FX = 0x36D4, FX_LIGHT = 0x3709, FX_DRAG = 0x0F51;

        private static readonly GameObject FakeSource = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(Item));

        public static TheoryData<string> EffectCases => new TheoryData<string>
        {
            "fixed", "moving", "moving-negative", "explosion", "multiply", "screen", "screenless", "half", "shadowblue", "screenred",
            "translucent", "partial", "hued", "outofrange", "deadgray", "outofrange-dead", "source", "light", "light-nosource", "offset-z",
            "drag", "drag-light", "drag-hued"
        };

        private static EffectSpriteInput EffectCase(string name)
        {
            var i = new EffectSpriteInput
            {
                Kind = EffectState.KIND_FIXED, Graphic = FX, AnimGraphic = 0x0E75, Hue = 0, X = 1400, Drawable = true
            };

            switch (name)
            {
                case "moving": i.Kind = EffectState.KIND_MOVING; i.Angle = 0.7f; i.OffsetX = 13.6f; i.OffsetY = -9.2f; break;
                case "moving-negative": i.Kind = EffectState.KIND_MOVING; i.Angle = -2.35f; i.OffsetX = -21.4f; i.OffsetY = 7.9f; i.OffsetZ = -3.5f; break;
                case "explosion": i.AnimGraphic = 0x0EEB; i.Hue = 0x0035; i.Blend = GraphicEffectBlendMode.ScreenMore; break;
                case "multiply": i.Blend = GraphicEffectBlendMode.Multiply; i.Angle = 1.1f; break;
                case "screen": i.Blend = GraphicEffectBlendMode.Screen; break;
                case "screenless": i.Blend = GraphicEffectBlendMode.ScreenLess; break;
                case "half": i.Blend = GraphicEffectBlendMode.NormalHalfTransparent; break;
                case "shadowblue": i.Blend = GraphicEffectBlendMode.ShadowBlue; break;
                case "screenred": i.Blend = GraphicEffectBlendMode.ScreenRed; break;
                case "translucent": i.TileTranslucent = true; break;
                case "partial": i.Hue = 0x0021; i.TilePartialHue = true; break;
                case "hued": i.Hue = 0x0481; break;
                case "outofrange": i.Hue = 0x0021; i.OutOfRange = true; break;
                case "deadgray": i.Hue = 0x0021; i.DeadGray = true; break;
                case "outofrange-dead": i.Hue = 0x0021; i.OutOfRange = true; i.DeadGray = true; break;
                case "source": i.Source = FakeSource; i.SourceDepth = 812.25f; break;
                case "light": i.Graphic = FX_LIGHT; i.TileLight = true; i.Source = FakeSource; i.SourceDepth = 640f; i.OffsetX = 3f; i.OffsetY = 4f; break;
                case "light-nosource": i.Graphic = FX_LIGHT; i.TileLight = true; break;
                case "offset-z": i.OffsetY = 2.6f; i.OffsetZ = 2.6f; break;
                case "drag": i.Kind = EffectState.KIND_DRAG; i.Graphic = FX_DRAG; i.OffsetX = 16f; i.OffsetY = 24f; break;
                case "drag-light":
                    i.Kind = EffectState.KIND_DRAG; i.Graphic = FX_DRAG; i.OffsetX = 8f; i.OffsetY = 8f; i.TileLight = true; i.Source = FakeSource;
                    i.SourceDepth = 99f; break;
                case "drag-hued": i.Kind = EffectState.KIND_DRAG; i.Graphic = FX_DRAG; i.Hue = 0x0021; i.TilePartialHue = true; i.TileTranslucent = true;
                    i.Blend = GraphicEffectBlendMode.Screen; break;
            }

            return i;
        }

        // ---- the views, transcribed ----

        private struct ViewEffect
        {
            public bool Drawn, HasCall, Light;
            public int ArtX, ArtY, LightX, LightY;
            public ushort ArtGraphic;
            public Vector3 Hue;
            public float Depth, Rotation;
            public bool Rotated;
            public BlendState Blend;
        }

        // GameEffectView.Draw / DragEffect.Draw on the input's fields; (ArtX, ArtY) is the position before the art's draw offset
        private static ViewEffect ViewDraw(in EffectSpriteInput i, int posX, int posY, float depth)
        {
            var v = new ViewEffect();

            if (i.Kind == EffectState.KIND_DRAG)
            {
                ushort dhue;

                if (i.OutOfRange)
                {
                    dhue = Constants.OUT_RANGE_COLOR;
                }
                else if (i.DeadGray)
                {
                    dhue = Constants.DEAD_RANGE_COLOR;
                }
                else
                {
                    dhue = i.Hue;
                }

                v.Hue = ShaderHueTranslator.GetHueVector(dhue);
                v.HasCall = true;
                v.ArtGraphic = i.AnimGraphic;
                v.ArtX = posX - ((int)i.OffsetX + 22);
                v.ArtY = posY - ((int)-i.OffsetY + 22);
                v.Depth = depth;

                if (i.TileLight && i.Source != null)
                {
                    v.Light = true;
                    v.LightX = posX + 22;
                    v.LightY = posY + 22;
                }

                v.Drawn = true;

                return v;
            }

            if (!i.Drawable || i.AnimGraphic == 0xFFFF)
            {
                return v;
            }

            posX += (int)i.OffsetX;
            posY += (int)(i.OffsetZ + i.OffsetY);
            ushort hue = i.Hue;

            if (i.OutOfRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (i.DeadGray)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }

            v.Hue = ShaderHueTranslator.GetHueVector(hue, i.TilePartialHue, i.TileTranslucent ? .5f : 1f, effect: true);

            if (i.Source != null)
            {
                depth = i.SourceDepth;
            }

            v.Blend = GameEffect.BlendStateOf(i.Blend);
            v.HasCall = true;
            v.Rotated = true;
            v.Rotation = i.Angle;
            v.ArtGraphic = i.AnimGraphic;
            v.ArtX = posX;
            v.ArtY = posY;
            v.Depth = depth;

            if (i.TileLight && i.Source != null)
            {
                v.Light = true;
                v.LightX = posX + 22;
                v.LightY = posY + 22;
            }

            v.Drawn = true;

            return v;
        }

        // View.DrawStaticRotated / DrawStatic's batcher call over the atlas
        private static LegacyDraw ViewCall(ILegacySpriteSource atlas, in ViewEffect v)
        {
            ref readonly SpriteInfo art = ref atlas.Art(v.ArtGraphic);
            int w = (art.UV.Width >> 1) - 22, h = art.UV.Height - 44;

            return new LegacyDraw
            {
                Texture = art.Texture, Position = new Vector2(v.ArtX - w, v.ArtY - h), Source = art.UV, Hue = v.Hue, Depth = v.Depth + 0.5f,
                Mode = SpriteCaptureMode.Normal, Rotated = v.Rotated, Rotation = v.Rotated ? v.Rotation : 0f
            };
        }

        [Theory]
        [MemberData(nameof(EffectCases))]
        public void Effect_Legacy_Resolve_Of_Select_Equals_The_View(string name)
        {
            (ArtLoader arts, GpuSpriteTables _) = ArtFixture();
            var atlas = new FakeAtlas(null, arts.PackSpriteEntries);
            var extract = new EffectSpriteExtract(arts);
            EffectSpriteInput input = EffectCase(name);
            ViewEffect view = ViewDraw(in input, 700, 300, 455.25f);

            Assert.Equal(view.Drawn, extract.Select(in input, 700, 300, 455.25f));
            Assert.Equal(view.HasCall, extract.HasSprite);
            Assert.False(extract.MissingArt);
            Assert.True(LegacySpriteResolve.ResolveEffect(atlas, in extract.Sprite, out LegacyDraw got));
            LegacyDraw want = ViewCall(atlas, in view);
            Assert.True(want.Same(in got), $"{name}: view {want} | ecs {got}");

            // the sink's blend state: GameEffectView's around its draw, none for DragEffect
            BlendState sinkBlend = extract.Sprite.Rotated ? GameEffect.BlendStateOf(extract.Sprite.Blend) : null;
            Assert.Same(view.Blend, sinkBlend);

            Assert.Equal(view.Light, extract.Light);

            if (view.Light)
            {
                Assert.Equal((view.LightX, view.LightY), (extract.LightX, extract.LightY));
            }

            // the art entry's draw offset the view stores (picking of items and statics with this art reads it)
            ref ArtEntry entry = ref arts.GetEntry(input.AnimGraphic + 0x4000);
            SpriteEntry sp = arts.PackSpriteEntries[input.AnimGraphic + 0x4000];
            Assert.Equal(((sp.OrigW >> 1) - 22, sp.OrigH - 44), ((int)entry.Width, (int)entry.Height));
        }

        [Theory]
        [MemberData(nameof(EffectCases))]
        public void Effect_Gpu_Resolve_Of_Select_Equals_The_View_Capture(string name)
        {
            (ArtLoader arts, GpuSpriteTables tables) = ArtFixture();
            var extract = new EffectSpriteExtract(arts);
            EffectSpriteInput input = EffectCase(name);
            ViewEffect view = ViewDraw(in input, 700, 300, 455.25f);
            extract.Select(in input, 700, 300, 455.25f);

            var got = new SpriteInstance[1];
            int count = 0;
            GpuEffectResolve.Resolve(in tables, in extract.Sprite, 15, -40, ref got, ref count);

            var want = new SpriteInstance[1];
            int wantCount = 0;
            ViewEmit(arts.PackSpriteEntries, in view, input.X, GpuWorldRenderer.BlendOf(input.Blend), 15, -40, ref want, ref wantCount);
            AssertSame(want, wantCount, got, count, 0);
            Assert.Equal(1, count);
        }

        [Fact]
        public void Effect_Gpu_Resolve_Equals_The_View_Capture_For_Random_Sprites()
        {
            var rnd = new Random(31);
            SpriteEntry[] sprites = RandomSprites(rnd, 0x4000 + 600);
            var arts = new ArtLoader(new UOFileManager(default, System.IO.Path.GetTempPath(), null));
            arts.SetTablesForTests(sprites);
            var t = new GpuSpriteTables(sprites, Array.Empty<AnimFrameEntry>(), null, HUE_COUNT, ANIM_PAGE_BASE, false);
            var extract = new EffectSpriteExtract(arts);
            float[] angles = { 0f, 0.25f, -0.25f, 1.5707964f, 3.1415927f, -2.9f, 0.001f };
            int compared = 0;

            for (int n = 0; n < 20_000; n++)
            {
                var i = new EffectSpriteInput
                {
                    Kind = rnd.Next(5) == 0 ? EffectState.KIND_DRAG : EffectState.KIND_MOVING, Graphic = FX, AnimGraphic = (ushort)rnd.Next(0, 600),
                    Hue = (ushort)rnd.Next(0, 3100), X = (ushort)rnd.Next(0, 7000), Drawable = true, Angle = angles[rnd.Next(angles.Length)],
                    OffsetX = (float)(rnd.NextDouble() * 80 - 40), OffsetY = (float)(rnd.NextDouble() * 80 - 40), OffsetZ = (float)(rnd.NextDouble() * 20 - 10),
                    Blend = (GraphicEffectBlendMode)rnd.Next(0, 8), TilePartialHue = rnd.Next(2) == 0, TileTranslucent = rnd.Next(3) == 0,
                    OutOfRange = rnd.Next(6) == 0, DeadGray = rnd.Next(6) == 0
                };

                int posX = rnd.Next(-200, 1800), posY = rnd.Next(-200, 1400), ox = rnd.Next(-300, 300), oy = rnd.Next(-300, 300);
                float depth = (float)(rnd.NextDouble() * 3000.0 - 100.0);
                ViewEffect view = ViewDraw(in i, posX, posY, depth);
                extract.Select(in i, posX, posY, depth);

                if (extract.MissingArt)
                {
                    // the view draws Arts.GetArt's placeholder: the caller draws the view
                    SpriteEntry sp = sprites[i.AnimGraphic + 0x4000];
                    Assert.True(sp.OrigW == 0 || sp.OrigH == 0);
                    Assert.False(extract.HasSprite);
                    Assert.False(extract.Light);

                    continue;
                }

                var got = new SpriteInstance[1];
                int count = 0;
                GpuEffectResolve.Resolve(in t, in extract.Sprite, ox, oy, ref got, ref count);

                var want = new SpriteInstance[1];
                int wantCount = 0;
                ViewEmit(sprites, in view, i.X, GpuWorldRenderer.BlendOf(i.Blend), ox, oy, ref want, ref wantCount);
                AssertSame(want, wantCount, got, count, n);
                compared += count;
            }

            Assert.True(compared > 10_000, $"{compared} instances compared");
        }

        [Fact]
        public void Effect_Select_Records()
        {
            (ArtLoader arts, _) = ArtFixture();
            var extract = new EffectSpriteExtract(arts);

            EffectSprite Select(EffectSpriteInput i, float depth = 455.25f)
            {
                Assert.True(extract.Select(in i, 700, 300, depth));
                Assert.True(extract.HasSprite);

                return extract.Sprite;
            }

            // the art at the offset position less its draw offset: (44 >> 1) - 22 = 0, 60 - 44 = 16; (int)(OffsetZ + OffsetY)
            EffectSpriteInput fx = EffectCase("offset-z");
            EffectSprite s = Select(fx);
            Assert.Equal(DynamicSpriteKind.Art, s.Sprite.Kind);
            Assert.Equal(6, s.Sprite.Rank);
            Assert.Equal(1400, s.Sprite.ObjX);
            Assert.Equal(0x0E75u, s.Sprite.Id);
            Assert.Equal((700, 300 + 5 - 16), (s.Sprite.X, s.Sprite.Y));
            Assert.Equal(455.25f + 0.5f, s.Sprite.Depth);
            Assert.True(s.Rotated);

            // the angle is the rotation; the effect hue (row hue - 1)
            s = Select(EffectCase("moving"));
            Assert.Equal(0.7f, s.Rotation);
            Assert.Equal((700 + 13, 300 - 9 - 16), (s.Sprite.X, s.Sprite.Y));
            Assert.Equal(ShaderHueTranslator.SHADER_EFFECT_HUED, (int)Select(EffectCase("hued")).Sprite.Hue.Y);
            Assert.Equal(0x0481 - 1, (int)Select(EffectCase("hued")).Sprite.Hue.X);
            Assert.Equal(0.5f, Select(EffectCase("translucent")).Sprite.Hue.Z);

            // out of range wins over the dead player's gray
            Assert.Equal(Constants.OUT_RANGE_COLOR - 1, (int)Select(EffectCase("outofrange-dead")).Sprite.Hue.X);
            Assert.Equal(Constants.DEAD_RANGE_COLOR - 1, (int)Select(EffectCase("deadgray")).Sprite.Hue.X);

            // a source replaces the depth (its depth + 1, computed by Gather)
            Assert.Equal(812.25f + 0.5f, Select(EffectCase("source")).Sprite.Depth);

            // a light source asks for its light at the offset position's tile centre, only with a source object
            Select(EffectCase("light"));
            Assert.True(extract.Light);
            Assert.Equal((700 + 3 + 22, 300 + 4 + 22), (extract.LightX, extract.LightY));
            Select(EffectCase("light-nosource"));
            Assert.False(extract.Light);

            // the drag draw: plain hue, no rotation, the drag offset, the render list's depth even with a source
            s = Select(EffectCase("drag-light"), 50f);
            Assert.False(s.Rotated);
            Assert.Equal(0f, s.Rotation);
            Assert.Equal((700 - (8 + 22), 300 - (-8 + 22) - 16), (s.Sprite.X, s.Sprite.Y));
            Assert.Equal(50.5f, s.Sprite.Depth);
            Assert.True(extract.Light);
            Assert.Equal((722, 322), (extract.LightX, extract.LightY));
            s = Select(EffectCase("drag-hued"));
            Assert.Equal(ShaderHueTranslator.SHADER_HUED, (int)s.Sprite.Hue.Y);
            Assert.Equal(1f, s.Sprite.Hue.Z);

            // not drawn: not allowed to draw, no animation graphic yet, lightning (its view draws it)
            EffectSpriteInput gone = EffectCase("light");
            gone.Drawable = false;
            Assert.False(extract.Select(in gone, 700, 300, 1f));
            Assert.False(extract.HasSprite);
            Assert.False(extract.Light);
            EffectSpriteInput unset = EffectCase("fixed");
            unset.AnimGraphic = 0xFFFF;
            Assert.False(extract.Select(in unset, 700, 300, 1f));
            Assert.False(extract.HasSprite);
            EffectSpriteInput lightning = EffectCase("fixed");
            lightning.Kind = EffectState.KIND_LIGHTNING;
            Assert.False(extract.Select(in lightning, 700, 300, 1f));
            Assert.False(extract.HasSprite);

            // art the pack lacks: the caller draws the view (its placeholder)
            EffectSpriteInput missing = EffectCase("light");
            missing.AnimGraphic = MISSING_ART;
            extract.Select(in missing, 700, 300, 1f);
            Assert.True(extract.MissingArt);
            Assert.False(extract.HasSprite);
            Assert.False(extract.Light);
        }

        [Fact]
        public void Effect_Blend_States_Are_The_Views()
        {
            Assert.Null(GameEffect.BlendStateOf(GraphicEffectBlendMode.Normal));
            Assert.Null(GameEffect.BlendStateOf(GraphicEffectBlendMode.ScreenRed));
            Assert.Same(GameEffect.BlendStateOf(GraphicEffectBlendMode.Screen), GameEffect.BlendStateOf(GraphicEffectBlendMode.ScreenMore));

            BlendState multiply = GameEffect.BlendStateOf(GraphicEffectBlendMode.Multiply);
            Assert.Equal((Blend.Zero, Blend.SourceColor), (multiply.ColorSourceBlend, multiply.ColorDestinationBlend));
            BlendState screen = GameEffect.BlendStateOf(GraphicEffectBlendMode.Screen);
            Assert.Equal((Blend.One, Blend.One), (screen.ColorSourceBlend, screen.ColorDestinationBlend));
            BlendState less = GameEffect.BlendStateOf(GraphicEffectBlendMode.ScreenLess);
            Assert.Equal((Blend.DestinationColor, Blend.InverseSourceAlpha), (less.ColorSourceBlend, less.ColorDestinationBlend));
            BlendState half = GameEffect.BlendStateOf(GraphicEffectBlendMode.NormalHalfTransparent);
            Assert.Equal((Blend.DestinationColor, Blend.SourceColor), (half.ColorSourceBlend, half.ColorDestinationBlend));
            BlendState blue = GameEffect.BlendStateOf(GraphicEffectBlendMode.ShadowBlue);
            Assert.Equal((Blend.SourceColor, Blend.InverseSourceColor, BlendFunction.ReverseSubtract),
                (blue.ColorSourceBlend, blue.ColorDestinationBlend, blue.ColorBlendFunction));
        }

        [Fact]
        public void Effect_Sprites_Switch()
        {
            Assert.False(EffectSpriteSink.Resolve(null, null, false));
            Assert.True(EffectSpriteSink.Resolve(null, null, true));
            Assert.True(EffectSpriteSink.Resolve(null, true, false));
            Assert.False(EffectSpriteSink.Resolve(null, false, true));
            Assert.True(EffectSpriteSink.Resolve(true, false, false));
            Assert.False(EffectSpriteSink.Resolve(false, true, true));
            Assert.False(new ClassicUO.Configuration.Settings().EcsEffectSprites);
        }

        [Fact]
        public void Effect_Calls_Differing_Only_In_Rotation_Are_Not_Same()
        {
            var a = new LegacyDraw { Texture = FakeTextures[0], Position = new Vector2(3, 4), Source = new Rectangle(1, 2, 44, 60), Hue = Vector3.One, Depth = 5f, Rotated = true, Rotation = 0.5f };
            LegacyDraw b = a;
            Assert.True(a.Same(in b));
            b.Rotation = 0.25f;
            Assert.False(a.Same(in b));
            b = a;
            b.Rotated = false;
            Assert.False(a.Same(in b));
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Effect_Check_Instance_Comparison_Sees_Every_Field()
        {
            var a = new SpriteInstance { X = 1, Y = 2, Whf = 3, Uv = 4, PageHue = 5, Depth = 6, Light = 7, Corners = 8 };
            Assert.True(EffectSpriteCheck.SameInstance(in a, in a));

            for (int f = 0; f < 8; f++)
            {
                SpriteInstance b = a;

                switch (f)
                {
                    case 0: b.X++; break;
                    case 1: b.Y++; break;
                    case 2: b.Whf++; break;
                    case 3: b.Uv++; break;
                    case 4: b.PageHue++; break;
                    case 5: b.Depth++; break;
                    case 6: b.Light++; break;
                    case 7: b.Corners++; break;
                }

                Assert.False(EffectSpriteCheck.SameInstance(in a, in b), $"field {f}");
            }
        }
#endif

        // GpuWorldRenderer.Emit for the view's art draw (verbatim, the art case: the source is the whole untrimmed sprite, so
        // its rect starts at the untrimmed origin), with the view's position and depth; the capture's blend is BlendOf(blend)
        private static void ViewEmit(SpriteEntry[] sprites, in ViewEffect v, int objX, InstanceBlend blend, int offsetX, int offsetY,
            ref SpriteInstance[] buffer, ref int count)
        {
            if (!v.HasCall)
            {
                return;
            }

            ref readonly SpriteEntry sp = ref sprites[v.ArtGraphic + 0x4000];
            int artW = (sp.OrigW >> 1) - 22, artH = sp.OrigH - 44;
            var position = new Vector2(v.ArtX - artW, v.ArtY - artH);
            var source = new Rectangle(0, 0, sp.OrigW, sp.OrigH);
            Point origin = Point.Zero;
            Vector3 hue = v.Hue;
            float depth = v.Depth + 0.5f;
            float rotation = v.Rotated ? v.Rotation : 0f;
            bool flip = false;

            bool cot = hue.Z > 1f;
            float alpha = cot ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f)
            {
                return;
            }

            uint flags = 0;
            uint hueRow = 0;
            var hueMode = HueMode.Normal;
            int type = (int)hue.Y;
            int row = (int)hue.X;

            switch (type)
            {
                case ShaderHueTranslator.SHADER_NONE:
                    break;

                case ShaderHueTranslator.SHADER_HUED:
                case ShaderHueTranslator.SHADER_PARTIAL_HUED:
                case ShaderHueTranslator.SHADER_EFFECT_HUED:
                    if (row >= 0 && row < HUE_COUNT)
                    {
                        hueRow = (uint)row;
                        flags = SpriteInstance.FlagHued | (type == ShaderHueTranslator.SHADER_PARTIAL_HUED ? SpriteInstance.FlagPartial : 0);
                    }

                    if (type == ShaderHueTranslator.SHADER_EFFECT_HUED)
                    {
                        hueMode = HueMode.EffectHued;
                    }

                    break;

                case ShaderHueTranslator.SHADER_SPECTRAL:
                    hueMode = HueMode.Spectral;

                    break;

                case ShaderHueTranslator.SHADER_SHADOW:
                    hueMode = HueMode.Shadow;

                    break;

                default:
                    return;
            }

            if (!sp.Present)
            {
                return;
            }

            int rx0 = source.X - origin.X, ry0 = source.Y - origin.Y;
            int rx1 = rx0 + source.Width, ry1 = ry0 + source.Height;
            int ix0 = Math.Max(rx0, sp.TrimX), iy0 = Math.Max(ry0, sp.TrimY);
            int ix1 = Math.Min(rx1, sp.TrimX + sp.W), iy1 = Math.Min(ry1, sp.TrimY + sp.H);

            if (ix1 <= ix0 || iy1 <= iy0)
            {
                return;
            }

            int w = ix1 - ix0, h = iy1 - iy0;
            int px = (int)MathF.Round(position.X) + offsetX;
            int py = (int)MathF.Round(position.Y) + offsetY;
            int dx = flip ? rx1 - ix1 : ix0 - rx0;
            int dy = iy0 - ry0;
            int x, y;

            if (hueMode == HueMode.Shadow)
            {
                int fullH = ry1 - ry0;
                x = px + dx + (fullH - dy - h) / 2;
                y = py + (fullH + dy - h) / 2;
            }
            else if (rotation != 0f)
            {
                float sn = MathF.Sin(rotation), cs = MathF.Cos(rotation);
                x = px + (int)MathF.Round(cs * dx - sn * dy);
                y = py + (int)MathF.Round(sn * dx + cs * dy);
            }
            else
            {
                x = px + dx;
                y = py + dy;
            }

            if (count >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            ref var inst = ref buffer[count++];
            inst.X = x;
            inst.Y = y;
            inst.Whf = SpriteInstance.PackWhf(w, h, flags | (flip ? SpriteInstance.FlagMirror : 0));
            inst.Uv = (uint)(sp.X + (ix0 - sp.TrimX)) | ((uint)(sp.Y + (iy0 - sp.TrimY)) << 16);
            inst.PageHue = (uint)sp.Page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(depth * 100f) - 50, objX, 6);
            inst.Light = 0;
            inst.Corners = 0;
            inst.Mode = hueMode;

            if (cot)
            {
                inst.SetLightBits(SpriteInstance.LightCot);
            }

            if (blend != InstanceBlend.Normal)
            {
                inst.Blend = blend;
            }

            if (rotation != 0f)
            {
                inst.SetRotation(rotation);
            }

            if (alpha < 1f)
            {
                inst.Alpha = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 1, 254);
            }
        }
    }
}
