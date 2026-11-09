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

        private readonly World _old, _new, _own, _mot, _bare;
        private readonly List<string> _mismatches = new List<string>();
        private readonly Action<string> _savedObserver;
        private readonly long _comparedBefore, _shadowBefore, _endpointReadsBefore, _motionComparedBefore, _motionStepsBefore;

        public EffectSystemTests()
        {
            _old = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            _new = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            _own = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            _mot = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            _bare = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            foreach (World w in new[] { _old, _new, _own, _mot, _bare })
            {
                w.RangeSize.X = 1000; // Distance is measured from the range centre
                w.RangeSize.Y = 1000;
            }

            _old.EffectsSystemOverride = false;
            _new.EffectsSystemOverride = true;
            _new.EffectsCheckOverride = true;
            _new.EffectEndpointsOverride = false;

            // T8 ownership: the third world references sources and targets by record (ecs_effect_endpoints) with the checker
            _own.EffectsSystemOverride = true;
            _own.EffectsCheckOverride = true;
            _own.EffectEndpointsOverride = true;
            _own.EffectEndpointsCheckOverride = true;
            _own.EffectMotionOverride = false;

            // T8 ownership phase 2: the motion step (ecs_effect_motion) with every checker and item offsets from the ECS, and
            // without any checker (the effects hold no objects; item offsets from the objects)
            _mot.EffectsSystemOverride = true;
            _mot.EffectsCheckOverride = true;
            _mot.EffectEndpointsOverride = true;
            _mot.EffectEndpointsCheckOverride = true;
            _mot.EffectMotionOverride = true;
            _mot.EffectMotionCheckOverride = true;
            _mot.ItemOffsetOverride = true;
            _bare.EffectsSystemOverride = true;
            _bare.EffectsCheckOverride = false;
            _bare.EffectEndpointsOverride = true;
            _bare.EffectEndpointsCheckOverride = false;
            _bare.EffectMotionOverride = true;
            _bare.EffectMotionCheckOverride = false;
            _bare.ItemOffsetOverride = false;
            _savedObserver = EffectSystem.Observer;
            EffectSystem.Observer = _mismatches.Add;
            _comparedBefore = EffectSystem.Compared;
            _shadowBefore = EffectSystem.ShadowSteps;
            _endpointReadsBefore = EffectSystem.EndpointReads;
            _motionComparedBefore = EffectSystem.MotionCompared;
            _motionStepsBefore = EffectSystem.MotionSteps;
            Time.Ticks = T0;
            Time.Delta = 0.025f;
        }

        public void Dispose() => EffectSystem.Observer = _savedObserver;

        private IEnumerable<World> Worlds => new[] { _old, _new, _own, _mot, _bare };

        // the worlds whose effects reference their endpoints by record
        private bool ByRecord(World w) => ReferenceEquals(w, _own) || ReferenceEquals(w, _mot) || ReferenceEquals(w, _bare);

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

        // `frames` updates 25 ms apart; `each` runs before every update on both worlds (a target that moves, dies, ...);
        // `accepted`: a known, documented difference the endpoint checker must report (and nothing else)
        private void Run(int frames, Action<World, int> each = null, string accepted = null, int step = 25)
        {
            Snap(_new).Should().NotBeEmpty("the scenario created effects");

            for (int i = 0; i < frames; i++)
            {
                Time.Ticks = T0 + (uint)(i * step);

                if (each != null)
                {
                    foreach (World w in Worlds)
                    {
                        each(w, i);
                    }
                }

                _old.Effects.Update();
                _new.Effects.Update();
                _own.Effects.Update();
                _mot.Effects.Update();
                _bare.Effects.Update();
                string old = Snap(_old);
                Snap(_new).Should().Be(old, $"after frame {i}");
                Snap(_own).Should().Be(old, $"endpoints from the ECS, after frame {i}");
                Snap(_mot).Should().Be(old, $"motion from the ECS, after frame {i}");
                Snap(_bare).Should().Be(old, $"motion from the ECS without checkers, after frame {i}");

                foreach (World w in new[] { _new, _own, _mot, _bare })
                {
                    CountEntities(w).Should().Be(Snap(w).Count(c => c == '\n'), $"entities equal the live effects after frame {i}");
                }

                CountEntities(_old).Should().Be(0, "the old world has no effect entities");
                StateEqualsObjects(_mot, i);
                StateEqualsObjects(_bare, i);
            }

            if (accepted != null)
            {
                _mismatches.RemoveAll(m => m.Contains(accepted)).Should().BeGreaterThan(0, "the checker reports the accepted difference");
            }

            _mismatches.Should().BeEmpty();
            (EffectSystem.Compared - _comparedBefore).Should().BeGreaterThan(0, "the checker ran");
            (EffectSystem.MotionSteps - _motionStepsBefore).Should().BeGreaterThan(0, "the motion step ran");
            (EffectSystem.MotionCompared - _motionComparedBefore).Should().BeGreaterThan(0, "the motion checker compared");
        }

        // the motion worlds: every live effect uses the motion step, and its state is what its object shows (the object is
        // the state's write-through cache)
        private static void StateEqualsObjects(World w, int frame)
        {
            for (GameEffect f = (GameEffect)w.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                f.UsesMotion.Should().BeTrue();
                ref readonly EffectState s = ref f.EcsEntity.GetComponent<EffectState>();
                (s.X, s.Y, s.Z).Should().Be((f.X, f.Y, f.Z), $"position after frame {frame}");
                new Microsoft.Xna.Framework.Vector3(s.OffsetX, s.OffsetY, s.OffsetZ).Should().Be(f.Offset, $"offset after frame {frame}");
                s.Angle.Should().Be(f.AngleToTarget);

                if (f is DragEffect d)
                {
                    s.MoveTime.Should().Be(d.LastMoveTime);
                }
            }
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

        // ---- T8 ownership (ecs_effect_endpoints)

        private const uint ITEM = 0x4000_0500;

        private static Item AddItem(World w, uint serial, int x, int y)
        {
            Item it = w.GetOrCreateItem(serial);
            it.Graphic = 0x0EED;
            it.SetInWorldTile((ushort)x, (ushort)y, 0);
            w.Ecs.Sync(it);

            return it;
        }

        private static ref EffectState State(GameEffect e) => ref e.EcsEntity.GetComponent<EffectState>();

        [Fact]
        public void Endpoints_Follow_A_Death_Rekeyed_Mobile_And_Not_A_New_Mobile_Under_Its_Old_Serial()
        {
            const uint DEAD = TARGET | 0x8000_0000;

            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1003, 1003);
                AddMobile(w, TARGET, 1012, 1008);
                Create(w, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0, explodes: true);
                Create(w, GraphicEffectType.FixedFrom, TARGET, 0, 0x3709, 0, 0, 0, 0, duration: 600);
                Create(w, GraphicEffectType.Lightning, TARGET, 0, 0, 0, 0, 0, 0);
            }

            Run(70, (w, i) =>
            {
                if (i == 3)
                {
                    w.Writer.RekeyDeadMobile(w.Mobiles.Get(TARGET)).Should().Be(DEAD);
                }
                else if (i == 5)
                {
                    AddMobile(w, TARGET, 1020, 1020); // the serial comes back as another mobile: not the effects' endpoint
                }
                else if (i > 5 && i < 14)
                {
                    Mobile d = w.Mobiles.Get(DEAD);
                    d.SetInWorldTile((ushort)(1012 + i / 4), 1008, 0); // AddToTile syncs the record
                }
                else if (i == 40)
                {
                    w.Mobiles.Get(DEAD).Destroy();
                }
            });

            (EffectSystem.EndpointReads - _endpointReadsBefore).Should().BeGreaterThan(0, "the endpoint checker compared reads");
        }

        // Codex audit (ecs-effects-own) P2: a second death under the same serial displaces the first dying mobile from
        // serial | 0x80000000, and the second mobile is written into that record. The displaced mobile's endpoints end at its
        // last position and let go of the record (a moving effect to it lands there, as legacy, whose orphaned object stays
        // put; the checker reports the gone target, the accepted difference), also when the record is re-keyed once more; the
        // second mobile's effects follow it.
        [Fact]
        public void A_Second_Death_Under_The_Same_Serial_Ends_The_Displaced_Mobiles_Endpoints()
        {
            const uint DEAD = TARGET | 0x8000_0000;

            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1001, 1001);
                AddMobile(w, TARGET, 1014, 1010);
                Create(w, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            }

            GameEffect toFirst = (GameEffect)_own.Effects.Items;

            Run(60, (w, i) =>
            {
                if (i == 2)
                {
                    w.Writer.RekeyDeadMobile(w.Mobiles.Get(TARGET)).Should().Be(DEAD);
                }
                else if (i == 3)
                {
                    AddMobile(w, TARGET, 1003, 1012);
                    Create(w, GraphicEffectType.FixedFrom, TARGET, 0, 0x3709, 0, 0, 0, 0, duration: 1000);
                }
                else if (i == 5)
                {
                    w.Writer.RekeyDeadMobile(w.Mobiles.Get(TARGET)).Should().Be(DEAD);
                }
                else if (i == 12)
                {
                    // the second mobile's record is re-keyed again (a death packet for the dead serial): the old record's
                    // endpoints move to the new one, the displaced mobile's must not come back
                    w.Writer.RekeyDeadMobile(w.Mobiles.Get(DEAD)).Should().Be(DEAD);
                }
                else if (i > 5 && i < 20)
                {
                    // the second dying mobile moves: its fixed effect follows it, the first one's moving effect must not
                    w.Mobiles.Get(DEAD).SetInWorldTile((ushort)(1003 + i), 1012, 0);
                }
            }, accepted: "target destroyed: ECS True | object False");

            toFirst.IsDestroyed.Should().BeFalse("the moving effect was still on its way when the scenario ended");
            ref EffectState s = ref State(toFirst);
            (s.Ends & EffectState.END_TARGET_GONE).Should().Be(EffectState.END_TARGET_GONE, "the displaced mobile's endpoint is gone");
            (s.TargetX, s.TargetY).Should().Be(((ushort)1014, (ushort)1010));
            s.TargetEntity.IsNull.Should().BeTrue("a gone endpoint lets go of a record that lives on as another mobile's");
        }

        // Codex audit (ecs-effects-own) P2: a mobile and an item share a serial and the mirror holds the item's record, so the
        // mobile World resolves cannot be bound. Such an endpoint is a ground point at the object's position when the effect
        // registered, not (0,0,0): a moving effect still heads for it, a fixed effect stays where its source was.
        [Fact]
        public void An_Unbound_Endpoint_Is_A_Ground_Point_At_The_Objects_Position()
        {
            long unbound = EffectSystem.EndpointsUnbound;

            foreach (World w in Worlds)
            {
                AddMobile(w, TARGET, 1012, 1009);
                AddMobile(w, CASTER, 1004, 1006);
                AddItem(w, TARGET, 1002, 1003); // the item's record replaces the mobile's under the shared serial
                AddItem(w, CASTER, 1003, 1002);
                Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36D4, 1001, 1001, 0, 0, speed: 1, duration: 0, explodes: true);
                Create(w, GraphicEffectType.FixedFrom, CASTER, 0, 0x3709, 0, 0, 0, 0, duration: 400);
            }

            Run(60);
            EffectSystem.EndpointsUnbound.Should().BeGreaterThan(unbound, "the endpoints could not be bound");
        }

        private const uint OTHER = 0x0000_0400;

        // Friflo's handle revision is 16 bits: deletes and creates the stale handle's id (the store reuses the last freed id
        // first) until the next entity created there gets the stale handle's id and revision
        private static void WrapTo(World w, Friflo.Engine.ECS.Entity stale)
        {
            for (int k = 0; k < 70_000; k++)
            {
                Friflo.Engine.ECS.Entity t = w.Ecs.Mirror.Store.CreateEntity();
                bool last = t.Id == stale.Id && ((t.Revision + 1) & 0xFFFF) == (stale.Revision & 0xFFFF);
                t.DeleteEntity();

                if (last)
                {
                    return;
                }
            }

            throw new InvalidOperationException("the stale handle's id was not reused");
        }

        // Codex audit (ecs-effects-own) P3: a record removed without a hook, its id then reused until the revision wraps, and a
        // record of another mobile (or an item under the same serial) created there: the stale handle is valid again. The read
        // must still find the endpoint gone (the record is not its object's: other serial, other kind), so the moving effect
        // lands where its target last was.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Stale_Endpoint_Handle_Whose_Revision_Wrapped_Does_Not_Follow_The_New_Record(bool itemUnderTheSerial)
        {
            uint newcomer = itemUnderTheSerial ? TARGET : OTHER;

            foreach (World w in Worlds)
            {
                AddMobile(w, TARGET, 1012, 1009);
                Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36D4, 1001, 1001, 0, 0, speed: 1, duration: 0, explodes: true);
            }

            Run(60, (w, i) =>
            {
                if (i == 3)
                {
                    w.Ecs.Mirror.TryGetEntity(TARGET, out var stale).Should().BeTrue();
                    w.Ecs.Mirror.Remove(TARGET); // no hook sees it

                    if (ByRecord(w))
                    {
                        WrapTo(w, stale);
                    }

                    if (itemUnderTheSerial)
                    {
                        AddItem(w, TARGET, 1003, 1012);
                    }
                    else
                    {
                        AddMobile(w, OTHER, 1003, 1012);
                    }

                    if (ByRecord(w))
                    {
                        w.Ecs.Mirror.TryGetEntity(newcomer, out var rec).Should().BeTrue();
                        (rec.Id, rec.Revision).Should().Be((stale.Id, stale.Revision), "the new record has the stale handle's id and revision");
                    }
                }
            }, accepted: "target destroyed: ECS True | object False");
        }

        // P3, the re-key: an endpoint found gone after an unhooked removal must not come back when a record that wrapped onto its
        // old handle is re-keyed by a death
        [Fact]
        public void A_Gone_Endpoint_Is_Not_Revived_By_A_Rekey_Of_A_Record_On_Its_Old_Handle()
        {
            const uint DEAD = OTHER | 0x8000_0000;
            var stale = new Dictionary<World, Friflo.Engine.ECS.Entity>();

            foreach (World w in Worlds)
            {
                AddMobile(w, TARGET, 1012, 1009);
                Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36D4, 1001, 1001, 0, 0, speed: 1, duration: 0, explodes: true);
            }

            Run(60, (w, i) =>
            {
                if (i == 3)
                {
                    if (ByRecord(w))
                    {
                        w.Ecs.Mirror.TryGetEntity(TARGET, out var rec).Should().BeTrue();
                        stale[w] = rec;
                    }

                    w.Ecs.Mirror.Remove(TARGET); // no hook sees it: the next update finds the endpoint gone
                }
                else if (i == 4)
                {
                    if (ByRecord(w))
                    {
                        WrapTo(w, stale[w]);
                    }

                    AddMobile(w, OTHER, 1003, 1012);
                }
                else if (i == 5)
                {
                    w.Writer.RekeyDeadMobile(w.Mobiles.Get(OTHER)).Should().Be(DEAD);
                }
                else if (i > 5 && i < 20)
                {
                    w.Mobiles.Get(DEAD).SetInWorldTile((ushort)(1003 + i), 1012, 0);
                }
            }, accepted: "target destroyed: ECS True | object False");
        }

        [Fact]
        public void A_Target_Destroyed_And_Its_Serial_Reused_Keeps_The_Last_Position()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, TARGET, 1012, 1009);
                Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36D4, 1001, 1001, 0, 0, speed: 1, duration: 0, explodes: true);
            }

            Run(60, (w, i) =>
            {
                if (i == 4)
                {
                    Mobile t = w.Mobiles.Get(TARGET);
                    t.SetInWorldTile(1013, 1010, 0);
                    w.RemoveMobile(TARGET);
                    t.Destroy();
                    AddMobile(w, TARGET, 1002, 1012);
                }
            });
        }

        [Fact]
        public void A_Fixed_Effect_Takes_Its_Source_Offset_From_The_Steps_Or_The_Item()
        {
            foreach (World w in Worlds)
            {
                w.Ecs.ClockEnabled = true;
                w.Ecs.StepsEnabled = true;
                AddMobile(w, CASTER, 1004, 1002);
                AddItem(w, ITEM, 1005, 1003);
                Create(w, GraphicEffectType.FixedFrom, CASTER, 0, 0x3709, 0, 0, 0, 0, duration: 400);
                Create(w, GraphicEffectType.FixedFrom, ITEM, 0, 0x373A, 0, 0, 0, 0, duration: 400);
            }

            Run(20, (w, i) =>
            {
                Mobile m = w.Mobiles.Get(CASTER);
                m.Offset = new Microsoft.Xna.Framework.Vector3(i, -i, i / 2);
                w.Ecs.PushSteps(m);
                Item it = w.Items.Get(ITEM);
                it.Offset = new Microsoft.Xna.Framework.Vector3(2 * i, i, i % 3);
                ItemOffsets.Push(w, it, it.Offset); // the boat operations' ECS write (ecs_item_offset: the motion world)
            });

            // the mobile's offset is read from its Steps: a component that differs from the object moves the effect, and the
            // checker reports it
            Mobile caster = _own.Mobiles.Get(CASTER);
            _own.Ecs.Mirror.TryGetEntity(CASTER, out var rec).Should().BeTrue();
            rec.GetComponent<ClassicUO.Ecs.Steps>().OffsetX = 77;
            _own.Effects.Update();
            ((GameEffect)_own.Effects.Items).OffsetX.Should().Be(77);
            caster.OffsetX.Should().NotBe(77);
            _mismatches.Should().Contain(m => m.Contains("source offset"));
            _mismatches.Clear();

            // the motion step reads the same: the mobile's Steps, and the item's ItemOffset component (ecs_item_offset); the
            // motion checker's legacy replay reads the objects and reports both
            _mot.Ecs.Mirror.TryGetEntity(CASTER, out rec).Should().BeTrue();
            rec.GetComponent<ClassicUO.Ecs.Steps>().OffsetX = 77;
            _mot.Ecs.Mirror.TryGetEntity(ITEM, out var itemRec).Should().BeTrue();
            itemRec.GetComponent<ClassicUO.Ecs.ItemOffset>().Y = -5;
            _mot.Effects.Update();
            var fixedOnMobile = (GameEffect)_mot.Effects.Items;
            fixedOnMobile.OffsetX.Should().Be(77);
            ((GameEffect)fixedOnMobile.Next).OffsetY.Should().Be(-5);
            _mot.Items.Get(ITEM).OffsetY.Should().NotBe(-5);
            _mismatches.Count(m => m.Contains("offset ECS (77,")).Should().Be(1, "the motion checker reports the mobile's");
            _mismatches.Count(m => m.Contains(",-5,")).Should().Be(1, "and the item's");
            _mismatches.Clear();
        }

        [Fact]
        public void The_Endpoint_Checker_Flags_A_Position_The_ECS_Did_Not_Get()
        {
            AddMobile(_own, CASTER, 1003, 1003);
            AddMobile(_own, TARGET, 1012, 1008);
            Create(_own, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            _own.Effects.Update();
            _mismatches.Should().BeEmpty();

            // a move written to the object only (outside the funnel): the effect follows the record, the checker reports it
            _own.Mobiles.Get(TARGET).X = 1014;
            _own.Effects.Update();
            _mismatches.Should().Contain(m => m.Contains("target ECS (1012,1008,0) | object (1014,1008,0)"));
            _mismatches.Clear();
        }

        [Fact]
        public void Without_The_Checker_An_Effect_Holds_No_Objects_And_An_Unseen_Removal_Leaves_The_Last_Position()
        {
            _own.EffectEndpointsCheckOverride = false;
            AddMobile(_own, CASTER, 1003, 1003);
            AddMobile(_own, TARGET, 1012, 1008);
            Create(_own, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            Create(_own, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1001, 1001, 0, 0, duration: 400);
            Create(_own, GraphicEffectType.FixedFrom, CASTER, 0, 0x3709, 0, 0, 0, 0, duration: 400);

            var fx = (GameEffect)_own.Effects.Items;
            var follower = (GameEffect)fx.Next.Next;
            fx.UsesEndpoints.Should().BeTrue();
            fx.EndpointCheck.Should().BeFalse();
            fx.Source.Should().BeNull("the effect references its source by record only");
            fx.Target.Should().BeNull();
            fx.SourceObject.Should().BeSameAs(_own.Mobiles.Get(CASTER), "the views find the object World holds");
            fx.IsMoving.Should().BeTrue();

            ref EffectState s = ref State(fx);
            (s.Ends & (EffectState.END_SOURCE | EffectState.END_TARGET | EffectState.END_SOURCE_MOBILE | EffectState.END_TARGET_MOBILE))
                .Should().Be(EffectState.END_SOURCE | EffectState.END_TARGET | EffectState.END_SOURCE_MOBILE | EffectState.END_TARGET_MOBILE);
            (s.SourceSerial, s.TargetSerial).Should().Be((CASTER, TARGET));
            var ground = (GameEffect)fx.Next;
            (State(ground).Ends).Should().Be(0, "a fixed effect at a point has no endpoint objects");
            ground.SourceObject.Should().BeNull();

            // the mirror alone loses the target's record (no hook saw it): the target is gone where it last was
            long unhooked = EffectSystem.EndpointsUnhooked;
            _own.Ecs.Mirror.Remove(TARGET);
            _own.Effects.Update();
            s = ref State(fx);
            (s.Ends & EffectState.END_TARGET_GONE).Should().Be(EffectState.END_TARGET_GONE);
            (s.TargetX, s.TargetY).Should().Be(((ushort)1012, (ushort)1008));
            EffectSystem.EndpointsUnhooked.Should().BeGreaterThan(unhooked);

            // and the source's record: the effect that follows it ends as when its source is destroyed
            _own.Ecs.Mirror.Remove(CASTER);
            _own.Effects.Update();
            follower.IsDestroyed.Should().BeTrue();
        }

        [Fact]
        public void With_The_Switch_Off_Effects_Keep_Their_Objects()
        {
            AddMobile(_new, CASTER, 1003, 1003);
            Create(_new, GraphicEffectType.FixedFrom, CASTER, 0, 0x3709, 0, 0, 0, 0, duration: 400);
            var fx = (GameEffect)_new.Effects.Items;
            fx.UsesEndpoints.Should().BeFalse();
            fx.Source.Should().BeSameAs(_new.Mobiles.Get(CASTER));
            State(fx).Ends.Should().Be(0);
            State(fx).SourceSerial.Should().Be(CASTER, "the off path publishes the object's serial");
        }

        [Fact]
        public void The_Endpoint_Setting_Defaults_On()
        {
            new ClassicUO.Configuration.Settings().EcsEffectEndpoints.Should().BeTrue();
        }

        // ---- T8 ownership phase 2 (ecs_effect_motion): every scenario above also runs in _mot and _bare

        [Fact]
        public void A_Moving_Effect_Follows_The_Player_Moving_Under_It()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1003, 1003);
                AddMobile(w, TARGET, 1012, 1001);
                Create(w, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0, explodes: true);
                Create(w, GraphicEffectType.Moving, 0, 0, 0x36E4, 1009, 1009, 995, 1002, speed: 2, duration: 0, explodes: true);
            }

            // the path is computed relative to the player (screen space, truncated to tiles): the player walks across it,
            // onto negative offsets too
            Run(70, (w, i) =>
            {
                if (i % 3 == 0)
                {
                    w.Player.SetInWorldTile((ushort)(1000 + i / 3 - 8), (ushort)(1000 + (i % 2) * 3), (sbyte)(i % 5));
                }
            });
        }

        [Fact]
        public void A_Moving_Effect_Whose_Source_Dies_Before_It_Leaves_Ends_As_Before()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1003, 1003);
                AddMobile(w, TARGET, 1010, 1009);
                AddMobile(w, OTHER, 1004, 1001);

                // two from the caster (one explodes), one from a source that moves away before the effect leaves it
                Create(w, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0, explodes: true);
                Create(w, GraphicEffectType.Moving, CASTER, 0, 0x36E4, 0, 0, 1008, 1003, speed: 1, duration: 0);
                Create(w, GraphicEffectType.Moving, OTHER, TARGET, 0x36F4, 0, 0, 0, 0, speed: 1, duration: 0, explodes: true);
            }

            Run(60, (w, i) =>
            {
                if (i == 0)
                {
                    w.Mobiles.Get(CASTER).Destroy(); // the legacy update still runs UpdateOffset on the destroyed effects
                }
                else if (i < 6)
                {
                    w.Mobiles.Get(OTHER).SetInWorldTile((ushort)(1004 - i), (ushort)(1001 + i), 0);
                }
            });
        }

        [Fact]
        public void Drag_And_Lightning_Follow_Or_End_With_Their_Source()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1003, 1003);
                AddMobile(w, TARGET, 1006, 1004);
                AddItem(w, ITEM, 1002, 1005);
                Create(w, GraphicEffectType.DragEffect, CASTER, TARGET, 0xEED, 0, 0, 0, 0, speed: 2, duration: 30);
                Create(w, GraphicEffectType.DragEffect, ITEM, 0, 0xEEF, 0, 0, 1004, 1006, speed: 1, duration: 30);
                Create(w, GraphicEffectType.Lightning, CASTER, 0, 0, 0, 0, 0, 0);
                Create(w, GraphicEffectType.Lightning, TARGET, 0, 0, 0, 0, 0, 0);
                Create(w, GraphicEffectType.FixedFrom, ITEM, 0, 0x373A, 0, 0, 0, 0, duration: 300);
            }

            Run(60, (w, i) =>
            {
                if (i < 8)
                {
                    w.Mobiles.Get(TARGET).SetInWorldTile((ushort)(1006 + i), 1004, 0);
                    w.Items.Get(ITEM).SetInWorldTile(1002, (ushort)(1005 + i / 2), (sbyte)i);
                }
                else if (i == 9)
                {
                    w.Mobiles.Get(CASTER).Destroy(); // the drag effect ends, the lightning stays where the caster was
                }
                else if (i == 30)
                {
                    w.Items.Get(ITEM).Destroy();
                }
            });
        }

        [Fact]
        public void An_Explosion_Lands_Where_The_Target_Is_On_The_Arrival_Frame()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, TARGET, 1006, 1003);
                Create(w, GraphicEffectType.Moving, 0, TARGET, 0x36D4, 1001, 1001, 0, 0, speed: 3, duration: 0, explodes: true);
            }

            bool exploded = false;

            Run(40, (w, i) =>
            {
                exploded |= ReferenceEquals(w, _bare) && Snap(w).Contains("|g36CB");
                Mobile t = w.Mobiles.Get(TARGET);

                if (t != null && i % 2 == 1 && i < 20)
                {
                    // toward the effect's source, climbing: it arrives while the target moves
                    t.SetInWorldTile((ushort)(t.X + (i < 6 ? 1 : -1)), t.Y, (sbyte)(t.Z + 2));
                }
            });

            exploded.Should().BeTrue("the explosion's fixed effect ran in the motion worlds too");
        }

        [Fact]
        public void The_Motion_Checker_Replays_The_Legacy_Motion_And_Flags_A_State_The_Object_Does_Not_Show()
        {
            AddMobile(_mot, TARGET, 1012, 1008);
            Create(_mot, GraphicEffectType.Moving, 0, TARGET, 0x36D4, 1001, 1001, 0, 0, speed: 1, duration: 0);
            Create(_mot, GraphicEffectType.DragEffect, 0, 0, 0xEED, 1002, 1002, 1004, 1006, speed: 1, duration: 300);
            long compared = EffectSystem.MotionCompared;
            _mot.Effects.Update();
            _mismatches.Should().BeEmpty();
            EffectSystem.MotionCompared.Should().Be(compared + 2);

            // a state that drifted from its object (written around the step): the step moves the state, the replay the object
            var moving = (GameEffect)_mot.Effects.Items;
            State(moving).OffsetX += 3;
            Time.Ticks += 25;
            _mot.Effects.Update();
            _mismatches.Should().ContainSingle(m => m.Contains("offset ECS")).Which.Should().Contain("0x36D4");
            _mismatches.Clear();

            // the drag effect's next move time
            var drag = (DragEffect)moving.Next;
            drag.LastMoveTime = 0;
            State(drag).MoveTime = Time.Ticks + 1000;
            _mot.Effects.Update();
            _mismatches.Should().Contain(m => m.Contains("0x0EED offset ECS"));
            _mismatches.Clear();

            // a target the record and the object disagree on
            _mot.Mobiles.Get(TARGET).X = 1016;
            _mot.Effects.Update();
            _mismatches.Should().Contain(m => m.Contains("target ECS (1012,1008,0) | legacy (1016,1008,0)"));
            _mismatches.Clear();
        }

        [Fact]
        public void Without_The_Checkers_The_Motion_Step_Holds_No_Objects_And_Reads_The_Player_From_The_ECS()
        {
            AddMobile(_bare, CASTER, 1003, 1003);
            AddMobile(_bare, TARGET, 1012, 1008);
            Create(_bare, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            var fx = (GameEffect)_bare.Effects.Items;
            fx.UsesMotion.Should().BeTrue();
            fx.MotionCheck.Should().BeFalse();
            fx.Source.Should().BeNull();
            fx.Target.Should().BeNull();

            // the player's object moved outside the funnel: the path still uses the record's position
            AddMobile(_old, CASTER, 1003, 1003);
            AddMobile(_old, TARGET, 1012, 1008);
            Create(_old, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            // (beyond the target, so the screen offsets change sign and the tile truncation differs)
            _bare.Player.X = 1020;
            _bare.Player.Y = 1015;

            for (int i = 0; i < 15; i++)
            {
                Time.Ticks = T0 + (uint)(i * 25);
                _bare.Effects.Update();
                _old.Effects.Update();
                Snap(_bare).Should().Be(Snap(_old), $"the record still holds the player at 1000,1000 (frame {i})");
            }
        }

        [Fact]
        public void The_Motion_Switch_Needs_The_Endpoints()
        {
            _new.EffectMotionOverride = true; // ecs_effects on, endpoints off
            _new.EffectMotionOn.Should().BeFalse();
            Create(_new, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1001, 1001, 0, 0, duration: 10);
            ((GameEffect)_new.Effects.Items).UsesMotion.Should().BeFalse();

            _old.EffectEndpointsOverride = true;
            _old.EffectMotionOverride = true; // ecs_effects off
            _old.EffectMotionOn.Should().BeFalse();
            _own.EffectMotionOn.Should().BeFalse();
            _mot.EffectMotionOn.Should().BeTrue();
            Create(_own, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1001, 1001, 0, 0, duration: 10);
            ((GameEffect)_own.Effects.Items).UsesMotion.Should().BeFalse("ecs_effect_motion is off in the endpoint world");

            // the motion checker alone keeps the objects its legacy replay reads
            _mot.EffectEndpointsCheckOverride = false;
            AddMobile(_mot, CASTER, 1003, 1003);
            Create(_mot, GraphicEffectType.FixedFrom, CASTER, 0, 0x3709, 0, 0, 0, 0, duration: 400);
            var fx = (GameEffect)_mot.Effects.Items;
            (fx.UsesMotion, fx.MotionCheck, fx.EndpointCheck).Should().Be((true, true, false));
            fx.Source.Should().BeSameAs(_mot.Mobiles.Get(CASTER));
        }

        [Fact]
        public void A_Moving_Effect_Exactly_22_Pixels_From_Its_Target_Arrives()
        {
            foreach (World w in Worlds)
            {
                // one tile east and one north: the screen distance is exactly 22 after the +22 start offset; the second is
                // on its target's tile
                Create(w, GraphicEffectType.Moving, 0, 0, 0x36D4, 1001, 1001, 1002, 1000, speed: 1, duration: 0, explodes: true);
                Create(w, GraphicEffectType.Moving, 0, 0, 0x36E4, 1003, 1003, 1003, 1003, speed: 1, duration: 0, explodes: true);
            }

            Run(20);
        }

        [Fact]
        public void A_Target_Gone_Then_The_Source_Gone_Explodes_At_The_Targets_Last_Position()
        {
            bool exploded = false;

            foreach (World w in Worlds)
            {
                // on the player's tile the first step keeps the effect on its source's tile (it still has its source when the
                // source dies); the target, a tile south-east and 6 up, is about 30 px away: not reached in one update, but
                // within 22 px of the effect's tile
                AddMobile(w, CASTER, 1000, 1000);
                AddMobile(w, TARGET, 1001, 1001).SetInWorldTile(1001, 1001, 6);
                Create(w, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 3, duration: 0, explodes: true);
            }

            // the target goes first (its last coordinates are copied into the effect's target), then the source: the legacy
            // update runs UpdateOffset on the destroyed effect, which arrives there and explodes
            Run(20, (w, i) =>
            {
                if (i == 0)
                {
                    w.Mobiles.Get(TARGET).Destroy();
                }
                else if (i == 1)
                {
                    w.Mobiles.Get(CASTER).Destroy();
                }
                else if (ReferenceEquals(w, _old))
                {
                    exploded |= Snap(w).Contains("|g36CB");
                }
            });

            exploded.Should().BeTrue("the destroyed effect's last update exploded at the copied target");
        }

        [Fact]
        public void Drag_Effects_Move_On_The_Frame_Their_Move_Time_Comes()
        {
            foreach (World w in Worlds)
            {
                AddMobile(w, CASTER, 1003, 1003);
                Create(w, GraphicEffectType.DragEffect, CASTER, 0, 0xEED, 0, 0, 1007, 1001, speed: 2, duration: 15);
            }

            Run(40, step: 10); // 10 ms frames: Time.Ticks lands on the move time
        }

        [Fact]
        public void The_Motion_Setting_Defaults_Off()
        {
            new ClassicUO.Configuration.Settings().EcsEffectMotion.Should().BeFalse();
        }

        [Fact]
        public void The_Setting_Defaults_On()
        {
            new ClassicUO.Configuration.Settings().EcsEffects.Should().BeTrue();
        }
    }
}
