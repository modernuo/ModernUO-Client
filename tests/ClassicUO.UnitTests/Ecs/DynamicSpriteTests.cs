using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;
using System.Runtime.InteropServices;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T7b part 1: the extract split into select (DynamicSprite) and the Gpu resolve. The resolve must give the instances the
    // extract wrote before the split, bit for bit (golden: the pre-split WriteFrame and art code below, verbatim but for the
    // tables argument), and Emit must equal resolve(select). In the client, the golden captures (GoldenCapture) compare the
    // extract's output of base and new builds, and the shots job their frames.
    public partial class DynamicSpriteTests
    {
        private const int HUE_COUNT = 3000;
        private const int ANIM_PAGE_BASE = 40;

        // ---- the resolve against the pre-split code ----

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Resolve_Animation_Equals_The_PreSplit_WriteFrame(bool indexed)
        {
            var rnd = new Random(indexed ? 7 : 3);
            AnimFrameEntry[] frames = RandomFrames(rnd, 512);
            uint[] palette = new uint[frames.Length];

            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = (uint)rnd.Next(0, 5000);
            }

            var t = new GpuSpriteTables(Array.Empty<SpriteEntry>(), frames, palette, HUE_COUNT, ANIM_PAGE_BASE, indexed);

            for (int n = 0; n < 20_000; n++)
            {
                DynamicSprite s = RandomAnim(rnd, frames);
                int ox = rnd.Next(-300, 300), oy = rnd.Next(-300, 300);
                var got = new SpriteInstance[1];
                int gotCount = 0;
                GpuSpriteResolve.Resolve(in t, in s, ox, oy, ref got, ref gotCount);

                var want = new SpriteInstance[1];
                int wantCount = 0;
                OldWriteFrame(frames, palette, indexed, ref want, ref wantCount, s.Id, s.X + ox, s.Y + oy, s.SrcY, s.SrcH, s.Hue, s.Flipped, s.Depth, s.ObjX, s.Rank);

                if ((s.Flags & DynamicSpriteFlags.Sitted) != 0 && wantCount > 0)
                {
                    SitDeform.Split(ref want, ref wantCount, 0, s.Y + oy, s.SrcH, s.SitMod, s.Flipped);
                }

                AssertSame(want, wantCount, got, gotCount, n);
            }
        }

        [Fact]
        public void Resolve_Art_Equals_The_PreSplit_Art_Instance()
        {
            var rnd = new Random(11);
            SpriteEntry[] sprites = RandomSprites(rnd, 0x4000 + 600);
            var t = new GpuSpriteTables(sprites, Array.Empty<AnimFrameEntry>(), null, HUE_COUNT, ANIM_PAGE_BASE, false);

            for (int n = 0; n < 20_000; n++)
            {
                ushort graphic = (ushort)rnd.Next(0, 600);
                int x = rnd.Next(-500, 1500), y = rnd.Next(-500, 1500);
                float depth = (float)(rnd.NextDouble() * 3000.0 - 100.0);
                Vector3 hue = RandomHue(rnd);
                int ox = rnd.Next(-300, 300), oy = rnd.Next(-300, 300), objX = rnd.Next(0, 7000);
                var s = new DynamicSprite
                {
                    Kind = DynamicSpriteKind.Art, Rank = 6, Id = graphic, X = x, Y = y, SrcH = sprites[graphic + 0x4000].OrigH, Hue = hue,
                    Depth = depth + 0.5f, ObjX = objX
                };

                var got = new SpriteInstance[1];
                int gotCount = 0;
                GpuSpriteResolve.Resolve(in t, in s, ox, oy, ref got, ref gotCount);

                var want = new SpriteInstance[1];
                int wantCount = 0;
                OldArt(sprites, ref want, ref wantCount, graphic, x, y, hue, depth, objX, ox, oy);

                AssertSame(want, wantCount, got, gotCount, n);
            }
        }

        // ---- mobile select ----

        private const ushort HUMAN = 400, HORSE = 200, ROBE = 0x01F0, LANTERN = 0x01F5, MISSING_ART = 0x0E77;

        internal static (AnimationsLoader Anims, GpuSpriteTables Tables) AnimFixture(int seed)
        {
            var rnd = new Random(seed);
            var bodies = new AnimBodyEntry[1024];
            var dirs = new System.Collections.Generic.List<AnimDirEntry>();
            var frames = new System.Collections.Generic.List<AnimFrameEntry>();

            for (int b = 0; b < bodies.Length; b++)
            {
                bodies[b].AliasBody = AnimConst.NoBody;
                bodies[b].CorpseBody = AnimConst.NoBody;
            }

            foreach ((ushort body, AnimType type) in new[] { (HUMAN, AnimType.Human), (HORSE, AnimType.Animal), (ROBE, AnimType.Equipment), (LANTERN, AnimType.Equipment) })
            {
                bodies[body].Type = type;
                bodies[body].FirstDir = (uint)dirs.Count;
                bodies[body].ActionCount = 35;

                for (int a = 0; a < 35 * AnimConst.Directions; a++)
                {
                    int count = rnd.Next(1, 6);
                    dirs.Add(new AnimDirEntry { FirstFrame = (uint)frames.Count, FrameCount = (ushort)count });

                    // every frame drawn (a frame without pixels draws nothing; the random resolve test covers that)
                    for (int f = 0; f < count; f++)
                    {
                        AnimFrameEntry fe = RandomFrame(rnd);
                        fe.Sprite = new SpriteEntry
                        {
                            Page = (ushort)rnd.Next(0, 30), X = fe.Sprite.X, Y = fe.Sprite.Y, OrigW = (ushort)rnd.Next(20, 90), OrigH = (ushort)rnd.Next(30, 110),
                            TrimX = 3, TrimY = 5, W = 12, H = 20
                        };
                        frames.Add(fe);
                    }
                }
            }

            var anims = new AnimationsLoader(new UOFileManager(default, Path.GetTempPath(), null));
            AnimFrameEntry[] frameArr = frames.ToArray();
            anims.SetTablesForTests(bodies, dirs.ToArray(), frameArr);

            return (anims, new GpuSpriteTables(Array.Empty<SpriteEntry>(), frameArr, null, HUE_COUNT, ANIM_PAGE_BASE, false));
        }

        private static EcsEntity Mobile(WorldMirror mirror, uint serial, ushort hue, AnimationState s, MobileRenderState rs, EquipmentSlots eq)
        {
            EcsEntity e = mirror.UpsertMobile(serial, HUMAN, hue, 1000, 1200, 0, 2, 0, false);
            e.AddComponent(s);
            e.AddComponent(rs);
            e.AddComponent(eq);

            return e;
        }

        private static AnimationState Standing(AnimStateFlags extra = 0) => new AnimationState
        {
            Flags = AnimStateFlags.Visible | AnimStateFlags.Human | extra, Body = HUMAN, Group = 4, Dir = 2, Facing = 2, Frame = 1
        };

        private static MobileRenderState Opaque => new MobileRenderState { Alpha = 255 };

        public static TheoryData<string> MobileCases => new TheoryData<string> { "plain", "hued", "flipped", "mounted", "sitting", "equipped", "lights-mixed", "translucent" };

        internal static EcsEntity MobileCase(WorldMirror mirror, string name)
        {
            var eq = new EquipmentSlots();
            AnimationState s = Standing(AnimStateFlags.Shadow);
            MobileRenderState rs = Opaque;
            ushort hue = 0;

            switch (name)
            {
                case "hued":
                    hue = 0x0021;
                    rs.OverrideHue = 0;

                    break;

                case "flipped":
                    s.Flags |= AnimStateFlags.Mirror;
                    s.Dir = 1;

                    break;

                case "mounted":
                    s.Flags |= AnimStateFlags.MountDrawn | AnimStateFlags.Mounted;
                    s.MountBody = HORSE;
                    s.MountGroup = 0;
                    s.MountOffsetY = -9;
                    s.Group = 25;
                    eq.MountHue = 0x0044;

                    break;

                case "sitting":
                    s.Flags = (s.Flags | AnimStateFlags.Sitting | AnimStateFlags.CharSitting) & ~AnimStateFlags.Shadow;
                    s.SitX = 3;
                    s.SitY = -4;

                    break;

                case "equipped":
                    eq.Count = 2;
                    eq.Slots[0] = new EquipSlot { Item = 0x40000010, DrawBody = ROBE, Hue = 0x0030, Layer = 22, Group = 0xFF, Bits = EquipSlotBits.PartialHue };
                    eq.Slots[1] = new EquipSlot { Item = 0x40000011, DrawBody = LANTERN, Hue = 0, Layer = 2, Group = 0xFF, Bits = EquipSlotBits.Light };

                    break;

                case "lights-mixed":
                    // two drawn slots (the second a light) with light-only items before the first, between and after the slots
                    eq.Count = 2;
                    eq.Slots[0] = new EquipSlot { Item = 0x40000010, DrawBody = ROBE, Hue = 0, Layer = 22, Group = 0xFF };
                    eq.Slots[1] = new EquipSlot { Item = 0x40000011, DrawBody = LANTERN, Hue = 0, Layer = 2, Group = 0xFF, Bits = EquipSlotBits.Light };
                    eq.LightCount = 3;
                    eq.LightItems[0] = 0x40000020;
                    eq.LightBefore[0] = 0;
                    eq.LightItems[1] = 0x40000021;
                    eq.LightBefore[1] = 1;
                    eq.LightItems[2] = 0x40000022;
                    eq.LightBefore[2] = 2;

                    break;

                case "translucent":
                    rs.Alpha = 120;
                    rs.OverrideHue = 0x038E;

                    break;
            }

            return Mobile(mirror, 0x100, hue, s, rs, eq);
        }

        // the view lights a light-only item where its layer sits in the paint order, which matters when the scene's 100 light
        // limit cuts the list: ahead of the first slot, between, and after the last (here the facing keeps the slots' order)
        [Fact]
        public void Mobile_Lights_Come_In_The_Layer_Order_Of_Drawn_And_Light_Only_Items()
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(5);
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, "lights-mixed");
            var extract = new MobileSpriteExtract(anims);

            Span<byte> order = stackalloc byte[EquipmentSlots.MAX];
            EquipmentSlots eq = e.GetComponent<EquipmentSlots>();
            int n = AnimationRules.PaintOrder(in eq, e.GetComponent<AnimationState>().Facing, order);
            Assert.Equal(2, n);

            // 0x40000020 before slot 0, 0x40000021 before slot 1, then slot 1's own light is submitted with its draw, then 0x40000022
            var want = new List<uint>();

            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (eq.LightBefore[k] == order[i])
                    {
                        want.Add(eq.LightItems[k]);
                    }
                }

                if ((eq.Slots[order[i]].Bits & EquipSlotBits.Light) != 0)
                {
                    want.Add(eq.Slots[order[i]].Item);
                }
            }

            want.Add(0x40000022);

            extract.Begin(in tables, 0, 0);
            extract.Emit(e, 600, 400, 812.25f);
            Assert.Equal(want, extract.Lights.ToArray().Select(l => l.Item).ToList());
            Assert.Equal(4, want.Count);
            Assert.Equal(0x40000020u, want[0]);
            Assert.Equal(0x40000022u, want[^1]);
        }

        [Theory]
        [MemberData(nameof(MobileCases))]
        public void Mobile_Emit_Equals_Resolve_Of_Select(string name)
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(5);
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);

            extract.Begin(in tables, 37, -12);
            extract.Emit(e, 600, 400, 812.25f);
            SpriteInstance[] emitted = extract.Instances.ToArray();
            Rectangle emitFi = extract.FrameInfo;
            MobileSpriteExtract.LightRequest[] emitLights = extract.Lights.ToArray();

            extract.Begin(in tables, 37, -12);
            extract.Select(e, 600, 400, 812.25f);
            Assert.True(extract.Selected.Length > 0, name);
            Assert.Equal(0, extract.Count);
            Assert.Equal(emitFi, extract.FrameInfo);
            Assert.Equal(emitLights.Length, extract.Lights.Length);

            var got = new SpriteInstance[4];
            int count = 0;

            foreach (ref readonly DynamicSprite s in extract.Selected)
            {
                Assert.Equal(DynamicSpriteKind.Animation, s.Kind);
                Assert.Equal(7, s.Rank);
                Assert.Equal(1000, s.ObjX);
                GpuSpriteResolve.Resolve(in tables, in s, extract.OffsetX, extract.OffsetY, ref got, ref count);
            }

            AssertSame(emitted, emitted.Length, got, count, 0);
        }

        // perf-11: the draw paths read the entity's record once (EcsRow) and pass it on; the answers are those of the
        // per-component reads, fresh, recorded into the frame cache and replayed from it
        [Theory]
        [MemberData(nameof(MobileCases))]
        public void Mobile_Emit_From_Record_Data_Equals_Component_Reads(string name)
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(5);
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);

            extract.Begin(in tables, 37, -12);
            extract.Emit(e, in e.GetComponent<AnimationState>(), in e.GetComponent<MobileRenderState>(), 600, 400, 812.25f, null);
            SpriteInstance[] want = extract.Instances.ToArray();
            Rectangle wantFi = extract.FrameInfo;
            int wantLights = extract.Lights.Length;

            var cache = new MobileFrameCache { SelectValid = true };

            for (int pass = 0; pass < 2; pass++)
            {
                ClassicUO.Ecs.EcsRow data = ClassicUO.Ecs.EcsRead.Row(e);
                extract.Begin(in tables, 37, -12);
                extract.Emit(e, data, in data.Get<AnimationState>(), in data.Get<MobileRenderState>(), 600, 400, 812.25f, cache);
                SpriteInstance[] got = extract.Instances.ToArray();

                AssertSame(want, want.Length, got, got.Length, 0);
                Assert.Equal(wantFi, extract.FrameInfo);
                Assert.Equal(wantLights, extract.Lights.Length);
            }

            Assert.True(cache.EmitValid, name);

            extract.Begin(in tables, 37, -12);
            extract.Select(e, ClassicUO.Ecs.EcsRead.Row(e), 600, 400, 812.25f);
            DynamicSprite[] fromData = extract.Selected.ToArray();
            extract.Begin(in tables, 37, -12);
            extract.Select(e, 600, 400, 812.25f);
            Assert.Equal(extract.Selected.ToArray(), fromData);
        }

        [Fact]
        public void Mobile_Select_Records()
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(5);
            var extract = new MobileSpriteExtract(anims);

            DynamicSprite[] Select(string name)
            {
                var mirror = new WorldMirror();
                EcsEntity e = MobileCase(mirror, name);
                extract.Begin(in tables, 0, 0);
                extract.Select(e, 600, 400, 812.25f);

                return extract.Selected.ToArray();
            }

            // plain: the shadow first (depth = base), then the body's strips (base + 1 + 2i), unhued
            DynamicSprite[] plain = Select("plain");
            Assert.Equal(ShaderHueTranslator.SHADER_SHADOW, (int)plain[0].Hue.Y);
            Assert.Equal(812.25f, plain[0].Depth);
            Assert.All(plain, s => Assert.Equal(HUMAN, s.Body));
            Assert.All(plain[1..], s => Assert.Equal(ShaderHueTranslator.SHADER_NONE, (int)s.Hue.Y));
            Assert.Equal(812.25f + 1f, plain[1].Depth);
            Assert.All(plain, s => Assert.False(s.Flipped));

            // hued: the body's hue row
            DynamicSprite[] hued = Select("hued");
            Assert.All(hued[1..], s => Assert.Equal(ShaderHueTranslator.SHADER_HUED, (int)s.Hue.Y));

            // flipped: every record mirrored
            Assert.All(Select("flipped"), s => Assert.True(s.Flipped));

            // mounted: rider shadow, mount shadow, the mount hued with the mount item's hue, then the rider raised by the seat
            DynamicSprite[] mounted = Select("mounted");
            Assert.Equal(HUMAN, mounted[0].Body);
            Assert.Equal(HORSE, mounted[1].Body);
            Assert.Equal(ShaderHueTranslator.SHADER_SHADOW, (int)mounted[1].Hue.Y);
            Assert.Equal(HORSE, mounted[2].Body);
            Assert.Equal(ShaderHueTranslator.SHADER_HUED, (int)mounted[2].Hue.Y);
            Assert.Contains(mounted, s => s.Body == HUMAN && s.Group == 25 && (int)s.Hue.Y != ShaderHueTranslator.SHADER_SHADOW);

            // sitting: no shadow, the body as one seated section record
            DynamicSprite[] sitting = Select("sitting");
            Assert.All(sitting, s => Assert.True((s.Flags & DynamicSpriteFlags.Sitted) != 0));
            Assert.All(sitting, s => Assert.Equal(0, s.SrcY));

            // equipped: the robe partial-hued, the lantern a light request
            DynamicSprite[] equipped = Select("equipped");
            Assert.Contains(equipped, s => s.Body == ROBE && (s.Flags & DynamicSpriteFlags.Equip) != 0 && (int)s.Hue.Y == ShaderHueTranslator.SHADER_PARTIAL_HUED);
            Assert.Contains(equipped, s => s.Body == LANTERN && (s.Flags & DynamicSpriteFlags.Equip) != 0);
            Assert.Contains(extract.Lights.ToArray(), l => l.Item == 0x40000011);

            // translucent: the override hue and the render alpha
            DynamicSprite[] translucent = Select("translucent");
            Assert.All(translucent[1..], s => Assert.Equal(120 / 255f, s.Hue.Z));
        }

        // ---- item select ----

        private static (ArtLoader Arts, GpuSpriteTables Tables) ArtFixture()
        {
            var rnd = new Random(21);
            SpriteEntry[] sprites = RandomSprites(rnd, 0x4000 + 0x4000);

            foreach (int g in new[] { 0x0E75, 0x0EEB, 0x0A15, 0x1F14, 0x0E76 })
            {
                sprites[g + 0x4000] = new SpriteEntry { Page = 3, X = 10, Y = 20, W = 40, H = 50, TrimX = 2, TrimY = 4, OrigW = 44, OrigH = 60 };
            }

            // Arts.GetArt's placeholder (sprite 0), which missing art is drawn as
            sprites[0] = new SpriteEntry { Page = 3, X = 1, Y = 2, W = 30, H = 40, TrimX = 1, TrimY = 3, OrigW = 46, OrigH = 50 };

            // art the pack has no pixels for (the view draws Arts.GetArt's placeholder)
            sprites[MISSING_ART + 0x4000] = default;

            var arts = new ArtLoader(new UOFileManager(default, Path.GetTempPath(), null));
            arts.SetTablesForTests(sprites);

            return (arts, new GpuSpriteTables(sprites, Array.Empty<AnimFrameEntry>(), null, HUE_COUNT, ANIM_PAGE_BASE, false));
        }

        private static ItemSpriteInput ArtItem(ushort graphic, ushort hue = 0) => new ItemSpriteInput
        {
            Serial = 0x40000100, Graphic = graphic, Hue = hue, Amount = 1, X = 1500, OnGround = true, Movable = true,
            AllowedToDraw = true, AlphaHue = 255
        };

        public static TheoryData<string> ItemCases => new TheoryData<string> { "plain", "hued", "partial", "stack", "coins", "hidden", "highlight", "preview", "light", "multi", "fading" };

        private static ItemSpriteInput ItemCase(string name)
        {
            ItemSpriteInput i = ArtItem(0x0E75);

            switch (name)
            {
                case "hued": i.Hue = 0x0021; break;
                case "partial": i.Hue = 0x0030; i.TilePartialHue = true; break;
                case "stack": i.Amount = 3; i.TileStackable = true; break;
                case "coins": i.Graphic = 0x0EEA; i.Amount = 4; i.TileStackable = true; break;
                case "hidden": i.Hidden = true; break;
                case "highlight": i.Selected = true; i.HighlightSelected = true; break;
                case "preview": i.Serial = 0; i.Multi = true; i.MultiGraphic = 0x1F14; i.PlacementPreview = true; break;
                case "light": i.Graphic = 0x0A15; i.TileLight = true; break;
                case "multi": i.Multi = true; i.MultiGraphic = 0x1F14; break;
                case "fading": i.AlphaHue = 90; i.Offset = new Vector3(2, -3, 1); break;
                case "missing": i.Graphic = MISSING_ART; break;
                case "missing-stack": i.Graphic = MISSING_ART; i.Amount = 3; i.TileStackable = true; break;
            }

            return i;
        }

        [Theory]
        [MemberData(nameof(ItemCases))]
        public void Item_Emit_Equals_Resolve_Of_Select(string name)
        {
            (ArtLoader arts, GpuSpriteTables tables) = ArtFixture();
            var extract = new ItemSpriteExtract(arts, null);
            ItemSpriteInput input = ItemCase(name);

            Assert.True(extract.Emit(in input, in tables, 15, -40, 700, 300, 455.5f));
            SpriteInstance[] emitted = extract.Instances.ToArray();
            bool light = extract.Light;

            Assert.True(extract.Select(in input, false, 700, 300, 455.5f));
            Assert.Equal(light, extract.Light);

            var got = new SpriteInstance[2];
            int count = 0;

            foreach (ref readonly DynamicSprite s in extract.Selected)
            {
                Assert.Equal(DynamicSpriteKind.Art, s.Kind);
                Assert.Equal(6, s.Rank);
                Assert.Equal(1500, s.ObjX);
                Assert.Equal(455.5f + 0.5f, s.Depth);
                GpuSpriteResolve.Resolve(in tables, in s, 15, -40, ref got, ref count);
            }

            AssertSame(emitted, emitted.Length, got, count, 0);
        }

        [Fact]
        public void Item_Select_Records()
        {
            (ArtLoader arts, _) = ArtFixture();
            var extract = new ItemSpriteExtract(arts, null);

            DynamicSprite[] Select(string name)
            {
                ItemSpriteInput i = ItemCase(name);
                extract.Select(in i, false, 700, 300, 455.5f);

                return extract.Selected.ToArray();
            }

            // the art entry's draw offset (picking reads it): (44 >> 1) - 22 = 0, 60 - 44 = 16
            DynamicSprite[] plain = Select("plain");
            Assert.Single(plain);
            Assert.Equal(0x0E75u, plain[0].Id);
            Assert.Equal((700, 300 - 16), (plain[0].X, plain[0].Y));
            Assert.Equal(16, arts.GetEntry(0x0E75 + 0x4000).Height);
            Assert.Equal(ShaderHueTranslator.SHADER_NONE, (int)plain[0].Hue.Y);

            Assert.Equal(ShaderHueTranslator.SHADER_HUED, (int)Select("hued")[0].Hue.Y);
            Assert.Equal(ShaderHueTranslator.SHADER_PARTIAL_HUED, (int)Select("partial")[0].Hue.Y);

            // a stack: the second copy 5 pixels up-left, first
            DynamicSprite[] stack = Select("stack");
            Assert.Equal(2, stack.Length);
            Assert.Equal((plain[0].X - 5, plain[0].Y - 5), (stack[0].X, stack[0].Y));

            // coins: one draw of the amount's graphic (DisplayedGraphic), no stack copy
            DynamicSprite[] coins = Select("coins");
            Assert.Single(coins);
            Assert.Equal(0x0EEBu, coins[0].Id);

            // GetHueVector's row is hue - 1
            Assert.Equal(0x038E - 1, (int)Select("hidden")[0].Hue.X);
            Assert.Equal(Constants.HIGHLIGHT_CURRENT_OBJECT_HUE - 1, (int)Select("highlight")[0].Hue.X);

            // the placement preview: the multi's graphic at half alpha
            DynamicSprite[] preview = Select("preview");
            Assert.Equal(0x1F14u, preview[0].Id);
            Assert.Equal(0.5f, preview[0].Hue.Z);
            Assert.Equal(1f, Select("multi")[0].Hue.Z);

            // a light source asks for its light at the tile centre
            Select("light");
            Assert.True(extract.Light);
            Assert.Equal((722, 322), (extract.LightX, extract.LightY));

            // the render offset moves the draw, the render alpha goes to the hue vector
            DynamicSprite[] fading = Select("fading");
            Assert.Equal((plain[0].X + 2, plain[0].Y - 2), (fading[0].X, fading[0].Y));
            Assert.Equal(90 / 255f, fading[0].Hue.Z);

            // not drawn: destroyed, or not allowed to draw (its light is still asked for)
            ItemSpriteInput gone = ItemCase("light");
            gone.AllowedToDraw = false;
            Assert.False(extract.Select(in gone, false, 700, 300, 455.5f));
            Assert.True(extract.Light);
            Assert.Equal(0, extract.Selected.Length);
        }

        // ---- helpers ----

        private static void AssertSame(SpriteInstance[] want, int wantCount, SpriteInstance[] got, int gotCount, int n)
        {
            Assert.Equal(wantCount, gotCount);

            for (int i = 0; i < wantCount; i++)
            {
                ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(new ReadOnlySpan<SpriteInstance>(want, i, 1));
                ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(new ReadOnlySpan<SpriteInstance>(got, i, 1));
                Assert.True(a.SequenceEqual(b), $"case {n} instance {i} differs");
            }
        }

        private static Vector3 RandomHue(Random rnd)
        {
            int[] types = { 0, 1, 2, 7, 8, 10, 3 };
            float[] alphas = { 1f, 0.5f, 0f, 1.47f, 2f, 0.003f };

            return new Vector3(rnd.Next(-2, HUE_COUNT + 5), types[rnd.Next(types.Length)], alphas[rnd.Next(alphas.Length)]);
        }

        private static AnimFrameEntry RandomFrame(Random rnd)
        {
            int ow = rnd.Next(0, 120), oh = rnd.Next(0, 140);
            int tx = ow == 0 ? 0 : rnd.Next(0, ow), ty = oh == 0 ? 0 : rnd.Next(0, oh);

            return new AnimFrameEntry
            {
                Sprite = new SpriteEntry
                {
                    Page = rnd.Next(10) == 0 ? PackConst.NoPage : (ushort)rnd.Next(0, 30), X = (ushort)rnd.Next(0, 4000), Y = (ushort)rnd.Next(0, 4000),
                    W = (ushort)(ow - tx == 0 ? 0 : rnd.Next(0, ow - tx + 1)), H = (ushort)(oh - ty == 0 ? 0 : rnd.Next(0, oh - ty + 1)),
                    TrimX = (short)tx, TrimY = (short)ty, OrigW = (ushort)ow, OrigH = (ushort)oh
                },
                CenterX = (short)rnd.Next(-40, 60), CenterY = (short)rnd.Next(-80, 20)
            };
        }

        private static AnimFrameEntry[] RandomFrames(Random rnd, int n)
        {
            var frames = new AnimFrameEntry[n];

            for (int i = 0; i < n; i++)
            {
                frames[i] = RandomFrame(rnd);
            }

            return frames;
        }

        private static SpriteEntry[] RandomSprites(Random rnd, int n)
        {
            var sprites = new SpriteEntry[n];

            for (int i = 0; i < n; i++)
            {
                sprites[i] = RandomFrame(rnd).Sprite;
            }

            return sprites;
        }

        private static DynamicSprite RandomAnim(Random rnd, AnimFrameEntry[] frames)
        {
            uint id = (uint)rnd.Next(0, frames.Length);
            int h = frames[id].Sprite.OrigH;
            int srcY = rnd.Next(0, h + 2);
            bool sitted = rnd.Next(4) == 0;

            return new DynamicSprite
            {
                Kind = DynamicSpriteKind.Animation,
                Flags = (rnd.Next(2) == 0 ? DynamicSpriteFlags.Flip : 0) | (sitted ? DynamicSpriteFlags.Sitted : 0),
                Rank = (byte)(rnd.Next(2) == 0 ? 6 : 7),
                Id = id,
                X = rnd.Next(-500, 1500),
                Y = rnd.Next(-500, 1500),
                SrcY = sitted ? 0 : srcY,
                SrcH = sitted ? h : rnd.Next(0, h - srcY + 3),
                Hue = RandomHue(rnd),
                Depth = (float)(rnd.NextDouble() * 3000.0 - 100.0) + 1f + rnd.Next(0, 20) * 2,
                ObjX = rnd.Next(0, 7000),
                SitMod = new Vector3(rnd.Next(0, 30), rnd.Next(0, 30), rnd.Next(0, 3))
            };
        }

        // MobileSpriteExtract.WriteFrame before the split (PackSet's tables passed explicitly)
        private static void OldWriteFrame(AnimFrameEntry[] animFrames, uint[] framePalette, bool animIndexed, ref SpriteInstance[] buffer, ref int count, uint index,
            int px, int py, int srcY, int srcH, Vector3 hue, bool flip, float depth, int objX, int rank)
        {
            float alpha = hue.Z > 1f ? hue.Z - 1f : hue.Z;

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

            ref readonly SpriteEntry sp = ref animFrames[index].Sprite;

            if (!sp.Present)
            {
                return;
            }

            int page = ANIM_PAGE_BASE + sp.Page;
            uint palette = 0;

            if (animIndexed)
            {
                flags |= SpriteInstance.FlagIndexed;
                palette = framePalette[index];
            }

            int rx0 = 0, ry0 = srcY;
            int rx1 = sp.OrigW, ry1 = srcY + srcH;
            int ix0 = Math.Max(rx0, sp.TrimX), iy0 = Math.Max(ry0, sp.TrimY);
            int ix1 = Math.Min(rx1, sp.TrimX + sp.W), iy1 = Math.Min(ry1, sp.TrimY + sp.H);

            if (ix1 <= ix0 || iy1 <= iy0)
            {
                return;
            }

            int w = ix1 - ix0, h = iy1 - iy0;
            int dx = flip ? rx1 - ix1 : ix0 - rx0;
            int dy = iy0 - ry0;
            int x, y;

            if (hueMode == HueMode.Shadow)
            {
                int fullH = ry1 - ry0;
                x = px + dx + (fullH - dy - h) / 2;
                y = py + (fullH + dy - h) / 2;
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

            ref SpriteInstance inst = ref buffer[count++];
            inst.X = x;
            inst.Y = y;
            inst.Whf = SpriteInstance.PackWhf(w, h, flags | (flip ? SpriteInstance.FlagMirror : 0));
            inst.Uv = (uint)(sp.X + (ix0 - sp.TrimX)) | ((uint)(sp.Y + (iy0 - sp.TrimY)) << 16);
            inst.PageHue = (uint)page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(depth * 100f) - 50, objX, rank);
            inst.Light = 0;
            inst.Corners = palette;
            inst.Mode = hueMode;

            if (alpha < 1f)
            {
                inst.Alpha = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 1, 254);
            }
        }

        // ItemSpriteExtract.Art before the split, from the draw-offset-adjusted (x, y) on (the side effect is the select's)
        private static void OldArt(SpriteEntry[] sprites, ref SpriteInstance[] buffer, ref int count, ushort graphic, int x, int y, Vector3 hue, float depth,
            int objX, int offsetX, int offsetY)
        {
            uint idx = (uint)graphic + 0x4000;
            ref readonly SpriteEntry sp = ref sprites[idx];
            float alpha = hue.Z > 1f ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f || !sp.Present)
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

            if (count >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            ref SpriteInstance inst = ref buffer[count++];
            inst.X = x + offsetX + sp.TrimX;
            inst.Y = y + offsetY + sp.TrimY;
            inst.Whf = SpriteInstance.PackWhf(sp.W, sp.H, flags);
            inst.Uv = (uint)sp.X | ((uint)sp.Y << 16);
            inst.PageHue = (uint)sp.Page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round((depth + 0.5f) * 100f) - 50, objX, 6);
            inst.Light = 0;
            inst.Corners = 0;
            inst.Mode = hueMode;

            if (hue.Z > 1f)
            {
                inst.SetLightBits(SpriteInstance.LightCot);
            }

            if (alpha < 1f)
            {
                inst.Alpha = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 1, 254);
            }
        }
    }
}
