using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T8: graphic effects as ECS entities (ecs_effects). The same scripted effects run in an old world (the legacy update) and a
    // new one (EffectSystem: the entity owns frame and lifetime) and are compared after every frame; the new world also runs
    // the checker (CUO_EFFECT_CHECK), whose shadow replays the legacy animation rules beside each system step.
    [Collection("ChunkStaticTileDataCollection")] // Time.Ticks / Time.Delta and EffectSystem's counters are process-wide
    public sealed class EffectSystemTests : IDisposable
    {
        private const uint PLAYER = 0x0000_0100, TARGET = 0x0000_0200, CASTER = 0x0000_0300;
        private const uint T0 = 1_000_000;

        private readonly World _old, _new;
        private readonly List<string> _mismatches = new List<string>();
        private readonly Action<string> _savedObserver;
        private readonly long _comparedBefore, _shadowBefore;

        public EffectSystemTests()
        {
            _old = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            _new = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            foreach (World w in new[] { _old, _new })
            {
                w.RangeSize.X = 1000; // Distance is measured from the range centre
                w.RangeSize.Y = 1000;
            }

            _old.EffectsSystemOverride = false;
            _new.EffectsSystemOverride = true;
            _new.EffectsCheckOverride = true;
            _savedObserver = EffectSystem.Observer;
            EffectSystem.Observer = _mismatches.Add;
            _comparedBefore = EffectSystem.Compared;
            _shadowBefore = EffectSystem.ShadowSteps;
            Time.Ticks = T0;
            Time.Delta = 0.025f;
        }

        public void Dispose() => EffectSystem.Observer = _savedObserver;

        private IEnumerable<World> Worlds => new[] { _old, _new };

        private static Mobile AddMobile(World w, uint serial, int x, int y)
        {
            Mobile m = w.GetOrCreateMobile(serial);
            m.Graphic = 0x190;
            m.SetInWorldTile((ushort)x, (ushort)y, 0);
            w.Ecs.Sync(m);

            return m;
        }

        private static void Create(World w, GraphicEffectType type, uint src, uint trg, ushort graphic, int sx, int sy, int tx, int ty,
            byte speed = 5, int duration = 20, bool explodes = false, GraphicEffectBlendMode blend = GraphicEffectBlendMode.Normal) =>
            w.Effects.CreateEffect(type, src, trg, graphic, 0, (ushort)sx, (ushort)sy, 0, (ushort)tx, (ushort)ty, 0, speed, duration, false, explodes, false, blend);

        // an effect with an animation frame list (the unit scene has no anim data): frames 0, 3, 6, ... of `count`
        private static unsafe FixedEffect FixedWithFrames(World w, int x, int y, int count, int duration, bool parked = false)
        {
            var e = new FixedEffect(w, w.Effects, (ushort)x, (ushort)y, 0, 0x3779, 0, duration, 0);
            e.AnimDataFrame.FrameCount = (byte)count;
            e.AnimDataFrame.FrameInterval = 1;

            for (int i = 0; i < count; i++)
            {
                e.AnimDataFrame.FrameData[i] = (sbyte)(i * 3);
            }

            e.IntervalInMs = 40;

            if (parked)
            {
                e.PendingBlock = 77; // waiting for its chunk (Map.Park)
            }

            w.Effects.Add(e);

            return e;
        }

        private static string Snap(World w)
        {
            var sb = new System.Text.StringBuilder();

            for (GameEffect f = (GameEffect)w.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                sb.Append(f.GetType().Name).Append('|').Append(f.X).Append(',').Append(f.Y).Append(',').Append(f.Z)
                    .Append("|o").Append(f.OffsetX.ToString("R")).Append(',').Append(f.OffsetY.ToString("R")).Append("|a").Append(f.AngleToTarget.ToString("R"))
                    .Append("|g").Append(f.Graphic.ToString("X")).Append("|f").Append(f.AnimationGraphic.ToString("X")).Append(" i").Append(f.AnimIndex)
                    .Append(" n").Append(f.NextChangeFrameTime).Append(" d").Append(f.Duration).Append(" b").Append((int)f.Blend)
                    .Append(" dead").Append(f.IsDestroyed).Append('\n');
            }

            return sb.ToString();
        }

        // `frames` updates 25 ms apart; `each` runs before every update on both worlds (a target that moves, dies, ...)
        private void Run(int frames, Action<World, int> each = null)
        {
            Snap(_new).Should().NotBeEmpty("the scenario created effects");

            for (int i = 0; i < frames; i++)
            {
                Time.Ticks = T0 + (uint)(i * 25);

                if (each != null)
                {
                    foreach (World w in Worlds)
                    {
                        each(w, i);
                    }
                }

                _old.Effects.Update();
                _new.Effects.Update();
                Snap(_new).Should().Be(Snap(_old), $"after frame {i}");
                CountEntities(_new).Should().Be(Snap(_new).Count(c => c == '\n'), $"entities equal the live effects after frame {i}");
                CountEntities(_old).Should().Be(0, "the old world has no effect entities");
            }

            _mismatches.Should().BeEmpty();
            (EffectSystem.Compared - _comparedBefore).Should().BeGreaterThan(0, "the checker ran");
        }

        private static int CountEntities(World w) => w.Ecs.Mirror.Store.Query<EffectState>().Count;

        [Fact]
        public void Fixed_Effects_With_Frames_And_Duration_Advance_Like_The_Legacy_Update()
        {
            foreach (World w in Worlds)
            {
                FixedWithFrames(w, 1001, 1001, 4, 300);
                FixedWithFrames(w, 1002, 1000, 3, 0); // no duration: runs until the distance test removes it
                Create(w, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1003, 1003, 0, 0, duration: 4);
            }

            Run(40);
            Snap(_old).Should().Contain("FixedEffect", "the no-duration effect is still running");
        }

        [Fact]
        public void The_Frame_List_Wraps_And_Resets_The_Index()
        {
            foreach (World w in Worlds)
            {
                FixedWithFrames(w, 1001, 1001, 2, 4000);
            }

            Run(30);
            _new.Effects.Items.Should().NotBeNull();
            ((GameEffect)_new.Effects.Items).AnimIndex.Should().BeLessThan(2);
        }

        [Fact]
        public void A_Moving_Effect_To_A_Target_That_Moves_Then_Dies_Matches()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1003, 1003);
                AddMobile(w, TARGET, 1010, 1004);
                Create(w, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 3, duration: 0, explodes: true);
                Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36E4, 1001, 1001, 0, 0, speed: 2, duration: 0, blend: GraphicEffectBlendMode.Screen);
            }

            Run(80, (w, i) =>
            {
                Mobile t = w.Mobiles.Get(TARGET);

                if (i < 10)
                {
                    t.SetInWorldTile((ushort)(1010 + i / 3), (ushort)(1004 + i / 2), 0);
                }
                else if (i == 12)
                {
                    t.Destroy(); // the effects keep the last coordinates
                }
            });
        }

        [Fact]
        public void A_Moving_Effect_To_A_Ground_Point_Explodes_Into_A_Fixed_Effect()
        {
            foreach (World w in Worlds)
            {
                Create(w, GraphicEffectType.Moving, 0, 0, 0x36D4, 1000, 1000, 1008, 1006, speed: 4, duration: 0, explodes: true, blend: GraphicEffectBlendMode.Multiply);
                Create(w, GraphicEffectType.Moving, 0, 0, 0x36D4, 1002, 1000, 1002, 1009, speed: 1, duration: 0, explodes: false);
            }

            Run(60);
            _new.Effects.Items.Should().BeNull("every effect ended");
            CountEntities(_new).Should().Be(0);
        }

        [Fact]
        public void A_Fixed_Effect_Whose_Source_Dies_Is_Destroyed_And_One_That_Follows_A_Mover_Is_Moved()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1004, 1002);
                AddMobile(w, TARGET, 1006, 1006);
                Create(w, GraphicEffectType.FixedFrom, CASTER, 0, 0x3709, 0, 0, 0, 0, duration: 200);
                Create(w, GraphicEffectType.FixedFrom, TARGET, 0, 0x373A, 0, 0, 0, 0, duration: 200);
            }

            Run(40, (w, i) =>
            {
                if (i < 20)
                {
                    w.Mobiles.Get(CASTER).SetInWorldTile((ushort)(1004 + i / 4), 1002, 0);
                }
                else if (i == 25)
                {
                    w.Mobiles.Get(TARGET).Destroy();
                }
            });
        }

        [Fact]
        public void Drag_Effects_Advance_Like_The_Legacy_Update()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1003, 1003);
                Create(w, GraphicEffectType.DragEffect, CASTER, 0, 0xEED, 0, 0, 1007, 1001, speed: 2, duration: 15);
                Create(w, GraphicEffectType.DragEffect, 0, 0, 0xEEF, 1001, 1001, 1004, 1006, speed: 1, duration: 8, blend: GraphicEffectBlendMode.Screen);
            }

            Run(50);
        }

        [Fact]
        public void Lightning_Runs_Its_Ten_Frames_And_Ends()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1002, 1002);
                Create(w, GraphicEffectType.Lightning, CASTER, 0, 0, 0, 0, 0, 0);
                Create(w, GraphicEffectType.Lightning, 0, 0, 0, 1005, 1005, 0, 0);
            }

            Run(90);
            _new.Effects.Items.Should().BeNull();
        }

        [Fact]
        public void Effects_On_A_Parked_Chunk_Advance_Without_A_Tile()
        {
            foreach (World w in Worlds)
            {
                FixedWithFrames(w, 1001, 1001, 4, 4000, parked: true);
                FixedWithFrames(w, 1002, 1002, 4, 4000);
            }

            Run(30);
            ((GameEffect)_new.Effects.Items).PendingBlock.Should().Be(77, "the parked effect stays parked");
        }

        [Fact]
        public void Effects_Created_During_The_Walk_And_Far_Effects_Keep_The_Visiting_Order()
        {
            foreach (World w in Worlds)
            {
                Create(w, GraphicEffectType.Moving, 0, 0, 0x36D4, 1000, 1000, 1004, 1000, speed: 5, duration: 0, explodes: true);
                FixedWithFrames(w, 1001, 1001, 4, 2000);
                Create(w, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1100, 1100, 0, 0, duration: 100); // beyond the view range
                Create(w, GraphicEffectType.Moving, 0, 0, 0x36D4, 1000, 1001, 1004, 1002, speed: 5, duration: 0, explodes: true);
            }

            Run(30);
        }

        [Fact]
        public void The_System_Off_Creates_No_Entities()
        {
            Create(_old, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1001, 1001, 0, 0, duration: 10);
            CountEntities(_old).Should().Be(0);
            Create(_new, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1001, 1001, 0, 0, duration: 10);
            CountEntities(_new).Should().Be(1);
            _new.Effects.Clear();
            CountEntities(_new).Should().Be(0, "clearing destroys the entities");
        }

        [Fact]
        public void The_Checker_Replays_The_Legacy_Rules_And_Flags_A_Bad_State()
        {
            foreach (World w in Worlds)
            {
                FixedWithFrames(w, 1001, 1001, 4, 400);
            }

            Run(5);
            (EffectSystem.ShadowSteps - _shadowBefore).Should().BeGreaterThan(0);

            // a state that drifted from the object (a missed publish) and a missing entity are both reported
            var fx = (GameEffect)_new.Effects.Items;
            fx.EcsEntity.GetComponent<EffectState>().X = 5;
            EffectSystem.Check(_new, _new.Effects);
            _mismatches.Should().Contain(m => m.Contains("position"));

            _mismatches.Clear();
            EffectSystem.Publish(fx);
            fx.EcsEntity.GetComponent<EffectState>().AnimIndex = 9;
            EffectSystem.Check(_new, _new.Effects);
            _mismatches.Should().Contain(m => m.Contains("frame"));
            _mismatches.Clear();
        }

        [Fact]
        public void The_Setting_Defaults_On()
        {
            new ClassicUO.Configuration.Settings().EcsEffects.Should().BeTrue();
        }
    }
}
