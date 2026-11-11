using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T7b part 3: the effect extract's input comes from EffectState (with the scene and the source object), not from the
    // effect's fields. Scripted effects (moving with an angle, fixed on a mobile, an explosion, ground effects, lightning) run
    // with the state's endpoints and motion step (the state owns the motion); after every frame each live effect's gathered
    // input must be what GameEffectView.Draw reads from the object.
    [Collection("ChunkStaticTileDataCollection")] // Time.Ticks is process-wide
    public sealed class EffectSpriteGatherTests
    {
        private const uint PLAYER = 0x0000_0100, TARGET = 0x0000_0200, CASTER = 0x0000_0300;
        private const uint T0 = 2_000_000;

        private static World Build()
        {
            World w = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            w.RangeSize.X = 1000;
            w.RangeSize.Y = 1000;
            w.EffectsCheckOverride = false;

            return w;
        }

        private static Mobile AddMobile(World w, uint serial, int x, int y)
        {
            Mobile m = w.GetOrCreateMobile(serial);
            m.Graphic = 0x190;
            m.SetInWorldTile((ushort)x, (ushort)y, 0);
            w.Ecs.Sync(m);

            return m;
        }

        private static void Create(World w, GraphicEffectType type, uint src, uint trg, ushort graphic, int sx, int sy, int tx, int ty, ushort hue = 0,
            byte speed = 5, int duration = 20, bool explodes = false, GraphicEffectBlendMode blend = GraphicEffectBlendMode.Normal) =>
            w.Effects.CreateEffect(type, src, trg, graphic, hue, (ushort)sx, (ushort)sy, 0, (ushort)tx, (ushort)ty, 0, speed, duration, false, explodes, false, blend);

        private static List<GameEffect> Live(World w)
        {
            var list = new List<GameEffect>();

            for (GameEffect f = (GameEffect)w.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                list.Add(f);
            }

            return list;
        }

        [Fact]
        public void Gather_Reads_What_The_View_Reads_From_The_Object()
        {
            World w = Build();
            Time.Ticks = T0; // after the world: BuildPlayer moves the clock
            Time.Delta = 0.025f;
            AddMobile(w, CASTER, 1003, 1003);
            AddMobile(w, TARGET, 1010, 1004);
            Create(w, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, hue: 0x0021, speed: 3, duration: 0, explodes: true,
                blend: GraphicEffectBlendMode.ScreenMore);
            Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36E4, 1001, 1001, 0, 0, speed: 2, duration: 0, blend: GraphicEffectBlendMode.Multiply);
            Create(w, GraphicEffectType.FixedFrom, TARGET, 0, 0x3709, 0, 0, 0, 0, duration: 4000, blend: GraphicEffectBlendMode.ShadowBlue);
            Create(w, GraphicEffectType.FixedXYZ, 0, 0, 0x376A, 1005, 1006, 0, 0, hue: 0x0481, duration: 3000);
            Create(w, GraphicEffectType.Lightning, TARGET, 0, 0x4E20, 0, 0, 0, 0, duration: 3000);
            Create(w, GraphicEffectType.DragEffect, CASTER, TARGET, 0x0F51, 0, 0, 0, 0, hue: 0x0030, speed: 2, duration: 3000);
            int compared = 0, rotated = 0, sourced = 0;

            for (int i = 0; i < 60; i++)
            {
                Time.Ticks = T0 + (uint)(i * 25);

                if (i < 10)
                {
                    w.Mobiles.Get(TARGET).SetInWorldTile((ushort)(1010 + i / 3), (ushort)(1004 + i / 2), 0);
                }

                w.Effects.Update();

                foreach (GameEffect e in Live(w))
                {
                    EffectSpriteInput.Gather(e, out EffectSpriteInput input).Should().BeTrue("a live effect has its state");
                    string at = $"frame {i} {e.GetType().Name} 0x{e.Graphic:X4}";
                    input.Kind.Should().Be(EffectSystem.KindOf(e), at);
                    input.Graphic.Should().Be(e.Graphic, at);
                    // T9 S7: the frame and angle live only in the state; the offset is the effect's ScreenOffset
                    EffectState st = e.EcsEntity.GetComponent<EffectState>();
                    input.AnimGraphic.Should().Be(st.AnimGraphic, at);
                    input.Hue.Should().Be(e.Hue, at);
                    input.Blend.Should().Be(e.Blend, at);
                    input.X.Should().Be(e.X, at);
                    input.OffsetX.Should().Be(e.ScreenOffset.X, at);
                    input.OffsetY.Should().Be(e.ScreenOffset.Y, at);
                    input.OffsetZ.Should().Be(e.ScreenOffset.Z, at);
                    input.Angle.Should().Be(st.Angle, at);
                    input.Drawable.Should().Be(e.AllowedToDraw, at);
                    // DragEffect.Draw reads its source only for a light
                    input.Source.Should().BeSameAs(input.Kind != EffectState.KIND_DRAG || input.TileLight ? e.SourceObject : null, at);

                    if (input.Source != null)
                    {
                        input.SourceDepth.Should().Be(e.SourceObject.CalculateDepthZ() + 1f, at);
                        sourced++;
                    }

                    if (input.Angle != 0f)
                    {
                        rotated++;
                    }

                    compared++;
                }
            }

            compared.Should().BeGreaterThan(200);
            rotated.Should().BeGreaterThan(0, "moving effects aim at their target");
            sourced.Should().BeGreaterThan(0, "effects with a live source");
        }

        [Fact]
        public void Once_Destroyed_The_View_Draws()
        {
            World on = Build();
            Time.Ticks = T0;
            Create(on, GraphicEffectType.FixedXYZ, 0, 0, 0x376A, 1005, 1006, 0, 0, duration: 3000);
            on.Effects.Update();
            GameEffect e = Live(on)[0];
            EffectSpriteInput.Gather(e, out _).Should().BeTrue();
            e.Destroy();
            EffectSpriteInput.Gather(e, out _).Should().BeFalse("a destroyed effect has no state");
        }

        [Fact]
        public void The_Extract_Handles_Every_Kind_But_Lightning()
        {
            World w = Build();
            Time.Ticks = T0;
            AddMobile(w, TARGET, 1010, 1004);
            Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36E4, 1001, 1001, 0, 0, duration: 0);
            Create(w, GraphicEffectType.FixedXYZ, 0, 0, 0x376A, 1005, 1006, 0, 0, duration: 3000);
            Create(w, GraphicEffectType.Lightning, TARGET, 0, 0x4E20, 0, 0, 0, 0, duration: 3000);
            w.Effects.Update();

            foreach (GameEffect e in Live(w))
            {
                EffectSpriteExtract.Handles(e).Should().Be(e is not LightningEffect, e.GetType().Name);
            }
        }
    }
}
