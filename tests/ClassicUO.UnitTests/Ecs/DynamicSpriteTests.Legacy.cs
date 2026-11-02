using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T7b part 2a: the Legacy resolve. Golden: the views' batcher calls, transcribed from MobileView.Draw/DrawInternal and
    // View.DrawStaticAnimated (geometry, strips, shadow, seated draw, the hue choice of DrawInternal), over a fake atlas made
    // from the same pack tables the select reads. The resolve of the select's records must make the same calls in the same
    // order. In the client, CUO_LEGACY_ECS_PARITY compares the real views' calls with the sink's.
    public partial class DynamicSpriteTests
    {
        // ---- fake atlas ----

        private static readonly Texture2D[] FakeTextures =
        {
            (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D)),
            (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D)),
            (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D))
        };

        private sealed class FakeAtlas : ILegacySpriteSource
        {
            private readonly AnimationsLoader _anims;
            private readonly SpriteEntry[] _art;
            private readonly Dictionary<(ushort, byte, byte, bool, bool), SpriteInfo[]> _dirs = new();
            private SpriteInfo _artInfo;

            /// <summary>Frames whose atlas texture is not uploaded yet (a direction decoding on a worker).</summary>
            public ushort PendingBody = 0xFFFF;

            public FakeAtlas(AnimationsLoader anims, SpriteEntry[] art)
            {
                _anims = anims;
                _art = art;
            }

            public ref readonly SpriteInfo Art(uint graphic)
            {
                SpriteEntry sp = _art[graphic + 0x4000];
                _artInfo = sp.OrigW == 0 || sp.OrigH == 0
                    ? SpriteInfo.Empty
                    : new SpriteInfo { Texture = FakeTextures[graphic % 3], UV = new Rectangle(100 + (int)graphic % 50, 7, sp.OrigW, sp.OrigH) };

                return ref _artInfo;
            }

            public Span<SpriteInfo> Frames(ushort body, byte group, byte dir, bool isEquip, bool isCorpse)
            {
                var key = (body, group, dir, isEquip, isCorpse);

                if (!_dirs.TryGetValue(key, out SpriteInfo[] sprites))
                {
                    sprites = Array.Empty<SpriteInfo>();

                    if (MobileSpriteExtract.TryGetFrames(_anims, body, group, dir, isEquip, isCorpse, out uint first, out int count, out _))
                    {
                        sprites = new SpriteInfo[count];

                        for (int i = 0; i < count; i++)
                        {
                            AnimFrameEntry fe = _anims.PackFrames[first + i];

                            if (fe.Sprite.OrigW == 0 || fe.Sprite.OrigH == 0)
                            {
                                continue;
                            }

                            sprites[i].Center = new Point(fe.CenterX, fe.CenterY);
                            sprites[i].UV = new Rectangle(13 * (int)((first + i) % 40), 300 + 7 * i, fe.Sprite.OrigW, fe.Sprite.OrigH);
                            sprites[i].Texture = body == PendingBody ? null : FakeTextures[(first + i) % 3];
                        }
                    }

                    _dirs[key] = sprites;
                }

                return sprites;
            }
        }

        private static List<LegacyDraw> ResolveAll(ILegacySpriteSource src, ReadOnlySpan<DynamicSprite> sprites)
        {
            var list = new List<LegacyDraw>();

            foreach (ref readonly DynamicSprite s in sprites)
            {
                if (LegacySpriteResolve.Resolve(src, in s, out LegacyDraw d))
                {
                    list.Add(d);
                }
            }

            return list;
        }

        private static void AssertSameCalls(List<LegacyDraw> want, List<LegacyDraw> got, string name)
        {
            Assert.True(want.Count > 0, name);
            Assert.Equal(want.Count, got.Count);

            for (int i = 0; i < want.Count; i++)
            {
                Assert.True(want[i].Same(got[i]), $"{name} call {i}: view {want[i]} | sink {got[i]}");
            }
        }

        // ---- the view, transcribed (MobileView.Draw → DrawInternal) ----

        private static void ViewDrawInternal(List<LegacyDraw> calls, ILegacySpriteSource src, ushort id, byte group, byte dir, byte frameIndex,
            bool isEquip, int x, int y, bool mirror, bool hasShadow, float depth, int mountOffset, Vector3 hueVec, bool sitting, Vector3 sitMod)
        {
            Span<SpriteInfo> frames = src.Frames(id, group, dir, isEquip, false);

            if (frames.Length == 0)
            {
                return;
            }

            if (frameIndex >= frames.Length)
            {
                frameIndex = (byte)(frames.Length - 1);
            }

            SpriteInfo si = frames[frameIndex % frames.Length];

            if (si.Texture == null)
            {
                return;
            }

            x -= mirror ? si.UV.Width - si.Center.X : si.Center.X;
            y -= si.UV.Height + si.Center.Y;

            void Add(Vector2 p, Rectangle r, Vector3 h, float d, SpriteCaptureMode m, Vector3 mod)
            {
                if (r.Width > 0 && r.Height > 0)
                {
                    calls.Add(new LegacyDraw { Texture = si.Texture, Position = p, Source = r, Hue = h, Depth = d, Flip = mirror, Mode = m, SitMod = mod });
                }
            }

            if (hasShadow)
            {
                Add(new Vector2(x, y), si.UV, new Vector3(0, ShaderHueTranslator.SHADER_SHADOW, 1f), depth, SpriteCaptureMode.Shadow, default);

                return;
            }

            Vector2 pos = new Vector2(x, y);
            Rectangle rect = si.UV;

            if (sitting)
            {
                Add(pos, rect, hueVec, depth + 1f, SpriteCaptureMode.Sitted, sitMod);

                return;
            }

            int diffY = si.UV.Height + si.Center.Y - mountOffset;
            int value = Math.Max(1, diffY);
            int count = Math.Max(si.UV.Height / value + 1, 2);
            rect.Height = Math.Min(value, rect.Height);
            int remains = si.UV.Height - rect.Height;

            for (int i = 0; i < count; ++i)
            {
                Add(pos, rect, hueVec, depth + 1f + i * 2, SpriteCaptureMode.Normal, default);
                pos.Y += rect.Height;
                rect.Y += rect.Height;
                rect.Height = remains;
                remains -= rect.Height;
            }
        }

        // DrawInternal's hue choice (no equipment conversion in the fixture; the fixture's bodies have no file hue)
        private static Vector3 ViewHue(ushort overrided, bool hasEntity, ushort entityHue, bool entityPartial, ushort ownerHue, bool isMount, float alpha)
        {
            ushort hue = overrided;
            bool partial = false;

            if (hue == 0)
            {
                hue = hasEntity ? entityHue : ownerHue;
                partial = !isMount && hasEntity && entityPartial;

                if ((hue & 0x8000) != 0)
                {
                    partial = true;
                    hue &= 0x7FFF;
                }

                if (hue == 0)
                {
                    hue = overrided; // hueFromFile is 0, so DrawInternal set it to the override hue
                    partial = false;
                }
            }

            return ShaderHueTranslator.GetHueVector(hue, partial, alpha);
        }

        private static List<LegacyDraw> ViewMobile(ILegacySpriteSource src, EcsEntity e, int posX, int posY, float depth, DynamicSprite[] selected)
        {
            var calls = new List<LegacyDraw>();
            AnimationState s = e.GetComponent<AnimationState>();
            MobileRenderState rs = e.GetComponent<MobileRenderState>();
            EquipmentSlots eq = e.GetComponent<EquipmentSlots>();
            ushort ownerHue = e.GetComponent<ClassicUO.Ecs.Hue>().Value;
            int drawX = posX + rs.OffsetX + 22, drawY = posY - 3 + rs.OffsetY + 22;
            float alpha = rs.Alpha / 255f;
            bool mirror = (s.Flags & AnimStateFlags.Mirror) != 0;
            bool shadow = (s.Flags & AnimStateFlags.Shadow) != 0;
            bool sitting = (s.Flags & AnimStateFlags.CharSitting) != 0;
            ushort over = rs.OverrideHue;
            int mountOffset = 0;

            // the seated sections are CalculateSitAnimation's (not transcribed here): the select's, in order
            int sitIndex = 0;
            Vector3 NextSitMod()
            {
                while (sitIndex < selected.Length && (selected[sitIndex].Flags & DynamicSpriteFlags.Sitted) == 0)
                {
                    sitIndex++;
                }

                return sitIndex < selected.Length ? selected[sitIndex++].SitMod : default;
            }

            if ((s.Flags & AnimStateFlags.MountDrawn) != 0)
            {
                mountOffset = s.MountOffsetY;

                if (shadow)
                {
                    ViewDrawInternal(calls, src, s.Body, s.Group, s.Dir, s.Frame, false, drawX, drawY + 10, mirror, true, depth, mountOffset, default, false, default);
                    ViewDrawInternal(calls, src, s.MountBody, s.MountGroup, s.Dir, s.Frame, false, drawX, drawY, mirror, true, depth, mountOffset, default, false, default);
                }

                ViewDrawInternal(calls, src, s.MountBody, s.MountGroup, s.Dir, s.Frame, false, drawX, drawY, mirror, false, depth, mountOffset,
                    ViewHue(over, true, eq.MountHue, false, ownerHue, true, alpha), false, default);
                drawY += mountOffset;
            }
            else if ((s.Flags & AnimStateFlags.Sitting) != 0)
            {
                drawX += s.SitX;
                drawY += s.SitY;
            }
            else if (shadow)
            {
                ViewDrawInternal(calls, src, s.Body, s.Group, s.Dir, s.Frame, false, drawX, drawY, mirror, true, depth, mountOffset, default, false, default);
            }

            ViewDrawInternal(calls, src, s.Body, s.Group, s.Dir, s.Frame, false, drawX, drawY, mirror, false, depth, mountOffset,
                ViewHue(over, false, 0, false, ownerHue, false, alpha), sitting, sitting ? NextSitMod() : default);

            Span<byte> order = stackalloc byte[EquipmentSlots.MAX];
            int n = AnimationRules.PaintOrder(in eq, s.Facing, order);

            for (int i = 0; i < n; i++)
            {
                EquipSlot slot = eq.Slots[order[i]];
                byte group = slot.Group == 0xFF ? s.Group : slot.Group;
                ViewDrawInternal(calls, src, slot.DrawBody, group, s.Dir, s.Frame, true, drawX, drawY, mirror, false, depth, mountOffset,
                    ViewHue(over, true, slot.Hue, (slot.Bits & EquipSlotBits.PartialHue) != 0, ownerHue, false, alpha), sitting, sitting ? NextSitMod() : default);
            }

            return calls;
        }

        [Theory]
        [MemberData(nameof(MobileCases))]
        public void Legacy_Mobile_Resolve_Makes_The_Views_Batcher_Calls(string name)
        {
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>());
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);

            extract.BeginSelect();
            extract.Select(e, 600, 400, 812.25f);
            DynamicSprite[] selected = extract.Selected.ToArray();

            List<LegacyDraw> got = ResolveAll(atlas, selected);
            List<LegacyDraw> want = ViewMobile(atlas, e, 600, 400, 812.25f, selected);

            AssertSameCalls(want, got, name);

            switch (name)
            {
                case "plain":
                    Assert.Equal(SpriteCaptureMode.Shadow, got[0].Mode);
                    Assert.All(got.GetRange(1, got.Count - 1), d => Assert.Equal(SpriteCaptureMode.Normal, d.Mode));

                    break;

                case "flipped":
                    Assert.All(got, d => Assert.True(d.Flip));

                    break;

                case "sitting":
                    Assert.All(got, d => Assert.Equal(SpriteCaptureMode.Sitted, d.Mode));

                    break;

                case "mounted":
                    Assert.Equal(2, got.FindAll(d => d.Mode == SpriteCaptureMode.Shadow).Count);

                    break;
            }
        }

        [Fact]
        public void Legacy_Mobile_Resolve_Draws_Nothing_For_A_Direction_Still_Decoding()
        {
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>()) { PendingBody = HUMAN };
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, "mounted");
            var extract = new MobileSpriteExtract(anims);

            extract.BeginSelect();
            extract.Select(e, 600, 400, 812.25f);
            List<LegacyDraw> got = ResolveAll(atlas, extract.Selected);

            // the rider's records resolve to nothing, the mount's (shadow and body) are drawn, as the view does
            Assert.Contains(extract.Selected.ToArray(), s => s.Body == HUMAN);
            Assert.NotEmpty(got);
            Assert.Equal(ViewMobile(atlas, e, 600, 400, 812.25f, extract.Selected.ToArray()).Count, got.Count);
            // and the extract's FrameInfo still has the rider (picking: see the handoff)
            Assert.NotEqual(Rectangle.Empty, extract.FrameInfo);
        }

        [Fact]
        public void Legacy_Resolve_Skips_Empty_Strips_And_Missing_Frames()
        {
            (AnimationsLoader anims, _) = AnimFixture(5);
            var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>());
            Assert.True(MobileSpriteExtract.TryGetFrames(anims, HUMAN, 4, 2, false, false, out uint first, out int count, out _));
            var s = new DynamicSprite
            {
                Kind = DynamicSpriteKind.Animation, Body = HUMAN, Group = 4, Dir = 2, Frame = 0, Id = first, X = 10, Y = 20, SrcY = 5,
                SrcH = 0, Hue = new Vector3(0, ShaderHueTranslator.SHADER_NONE, 1f), Depth = 3f
            };

            Assert.False(LegacySpriteResolve.Resolve(atlas, in s, out _));

            s.SrcH = 7;
            Assert.True(LegacySpriteResolve.Resolve(atlas, in s, out LegacyDraw d));
            Rectangle uv = atlas.Frames(HUMAN, 4, 2, false, false)[0].UV;
            Assert.Equal(new Rectangle(uv.X, uv.Y + 5, uv.Width, 7), d.Source);
            Assert.Equal(new Vector2(10, 20), d.Position);

            s.Frame = (byte)count;
            Assert.False(LegacySpriteResolve.Resolve(atlas, in s, out _));
        }

        // ---- items (View.DrawStaticAnimated, transcribed) ----

        private static void ViewArt(List<LegacyDraw> calls, ILegacySpriteSource src, ushort graphic, int x, int y, Vector3 hue, float depth)
        {
            SpriteInfo artInfo = src.Art(graphic);

            if (artInfo.Texture == null)
            {
                return;
            }

            int w = (artInfo.UV.Width >> 1) - 22, h = artInfo.UV.Height - 44;
            calls.Add(new LegacyDraw
            {
                Texture = artInfo.Texture, Position = new Vector2(x - w, y - h), Source = artInfo.UV, Hue = hue, Depth = depth + 0.5f,
                Mode = SpriteCaptureMode.Normal
            });
        }

        [Theory]
        [MemberData(nameof(ItemCases))]
        public void Legacy_Item_Resolve_Makes_The_Views_Batcher_Calls(string name)
        {
            (ArtLoader arts, GpuSpriteTables tables) = ArtFixture();
            var atlas = new FakeAtlas(null, tables.Sprites);
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase(name);

            Assert.True(extract.Select(in input, false, 700, 300, 455.5f));
            List<LegacyDraw> got = ResolveAll(atlas, extract.Selected);

            // ItemView.Draw: the render offset, the stack's second copy 5 pixels up-left first; the hue is the select's
            // (ChooseHue, compared with the view by the Gpu parity and ItemHueTests)
            var want = new List<LegacyDraw>();
            int posX = 700 + (int)input.Offset.X, posY = 300 + (int)(input.Offset.Y + input.Offset.Z);
            Vector3 hue = extract.Selected[extract.Selected.Length - 1].Hue;
            ushort graphic = (ushort)extract.Selected[0].Id;

            if (!input.Multi && !input.IsCoin && input.Amount > 1 && input.TileStackable)
            {
                ViewArt(want, atlas, graphic, posX - 5, posY - 5, hue, 455.5f);
            }

            ViewArt(want, atlas, graphic, posX, posY, hue, 455.5f);

            AssertSameCalls(want, got, name);
        }

        // ---- the switch and the checker ----

        [Theory]
        [InlineData(null, null, false, false)]
        [InlineData(null, null, true, true)]
        [InlineData(null, true, false, true)]
        [InlineData(null, false, true, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, true, false)]
        public void Legacy_Switch_Resolves_Override_Then_Env_Then_Setting(bool? over, bool? env, bool setting, bool want)
        {
            Assert.Equal(want, LegacySpriteSink.Resolve(over, env, setting));
        }

        [Fact]
        public void Legacy_Switch_Env_Parse_And_Default_Off()
        {
            Assert.True(LegacySpriteSink.ParseSwitch("1"));
            Assert.False(LegacySpriteSink.ParseSwitch("0"));
            Assert.Null(LegacySpriteSink.ParseSwitch(null));
            Assert.Null(LegacySpriteSink.ParseSwitch("yes"));
            Assert.False(new ClassicUO.Configuration.Settings().LegacyEcsSprites);
        }

        [Fact]
        public void Legacy_Switch_Off_Routes_Nothing_Through_The_Sink()
        {
            // the render list takes DrawRenderList (every object's view.Draw) unless the frame routes through the sink
            LegacySpriteSink.EndFrame();
            Assert.False(LegacySpriteSink.Routes);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Legacy_Parity_Recorder_Drops_Empty_Calls_And_Suppresses_One_Side()
        {
            Texture2D tex = FakeTextures[0];
            ISpriteCapture drawn = LegacyEcsParity.NewRecorder(false), dropped = LegacyEcsParity.NewRecorder(true);
            var hue = new Vector3(5, 1, 1);

            Assert.False(drawn.Draw(tex, new Vector2(1, 2), new Rectangle(0, 0, 10, 4), hue, true, false, 3f, SpriteCaptureMode.Normal));
            Assert.False(drawn.Draw(tex, new Vector2(1, 6), new Rectangle(0, 4, 10, 0), hue, true, false, 5f, SpriteCaptureMode.Normal));
            Assert.False(drawn.DrawSitted(tex, new Vector2(1, 2), new Rectangle(0, 0, 10, 4), new Vector3(0.3f, 0.3f, 0.4f), hue, false, 4f));
            Assert.True(dropped.Draw(tex, new Vector2(1, 2), new Rectangle(0, 0, 10, 4), hue, true, false, 3f, SpriteCaptureMode.Normal));
            Assert.True(dropped.DrawSitted(tex, new Vector2(1, 2), new Rectangle(0, 0, 10, 4), new Vector3(0.3f, 0.3f, 0.4f), hue, false, 4f));

            ReadOnlySpan<LegacyDraw> a = ((LegacyEcsParity)drawn).Recorded, b = ((LegacyEcsParity)dropped).Recorded;
            Assert.Equal(2, a.Length);
            Assert.Equal(-1, LegacyEcsParity.FirstDifference(a, b));

            LegacyDraw[] c = b.ToArray();
            c[1].SitMod.Z = 0.5f;
            Assert.Equal(1, LegacyEcsParity.FirstDifference(a, c));
            c = b.ToArray();
            c[0].Texture = FakeTextures[1];
            Assert.Equal(0, LegacyEcsParity.FirstDifference(a, c));
            Assert.Equal(1, LegacyEcsParity.FirstDifference(a, b.Slice(0, 1)));
        }

        [Fact]
        public void Legacy_Parity_On_Loads_Animations_Synchronously()
        {
            bool saved = ClassicUO.Renderer.Animations.Animations.AsyncFirstSight;

            try
            {
                ClassicUO.Renderer.Animations.Animations.AsyncFirstSight = true;
                LegacyEcsParity.ConfigureLoaders("0");
                Assert.True(ClassicUO.Renderer.Animations.Animations.AsyncFirstSight);
                LegacyEcsParity.ConfigureLoaders("1");
                Assert.False(ClassicUO.Renderer.Animations.Animations.AsyncFirstSight);
            }
            finally
            {
                ClassicUO.Renderer.Animations.Animations.AsyncFirstSight = saved;
            }
        }
#endif
    }
}
