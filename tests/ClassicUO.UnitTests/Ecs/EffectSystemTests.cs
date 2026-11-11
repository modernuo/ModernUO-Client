using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T8: graphic effects as ECS entities (EffectSystem: the entity owns frame, lifetime, endpoints and motion). The same
    // scripted effects run in two worlds, with the checker CUO_EFFECT_CHECK and item offsets from the ECS (_fx) and without
    // (_bare), compared after every frame. Since T9 deleted the off paths of ecs_effects, ecs_effect_endpoints and
    // ecs_effect_motion, _fx's updates are compared with a copy of the legacy rules kept here (Legacy: the frame and lifetime
    // rules, formerly CUO_EFFECT_CHECK's shadow, and the per-object motion with source and target objects, formerly
    // CUO_EFFECT_MOTION_CHECK's replay): before every update each live effect's legacy outcome is computed from its object's
    // fields and the objects World held for its endpoints, and compared with the object after the update.
    [Collection("ChunkStaticTileDataCollection")] // Time.Ticks / Time.Delta and EffectSystem's counters are process-wide
    public sealed class EffectSystemTests : IDisposable
    {
        private const uint PLAYER = 0x0000_0100, TARGET = 0x0000_0200, CASTER = 0x0000_0300;
        private const uint T0 = 1_000_000;

        private readonly World _fx, _bare;
        private readonly List<string> _mismatches = new List<string>();
        private readonly Action<string> _savedObserver;
        private readonly long _comparedBefore;

        public EffectSystemTests()
        {
            _fx = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            _bare = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            foreach (World w in new[] { _fx, _bare })
            {
                w.RangeSize.X = 1000; // Distance is measured from the range centre
                w.RangeSize.Y = 1000;
            }

            _fx.EffectsCheckOverride = true;
            _bare.EffectsCheckOverride = false;
            _savedObserver = EffectSystem.Observer;
            EffectSystem.Observer = _mismatches.Add;
            _comparedBefore = EffectSystem.Compared;
            Time.Ticks = T0;
            Time.Delta = 0.025f;
        }

        public void Dispose() => EffectSystem.Observer = _savedObserver;

        private IEnumerable<World> Worlds => new[] { _fx, _bare };

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

        // the effect's state (default once destroyed: the views draw nothing then)
        private static EffectState St(GameEffect f) => f.EcsEntity.IsNull ? default : f.EcsEntity.GetComponent<EffectState>();

        private static string Snap(World w)
        {
            var sb = new System.Text.StringBuilder();

            for (GameEffect f = (GameEffect)w.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                sb.Append(f.GetType().Name).Append('|').Append(f.X).Append(',').Append(f.Y).Append(',').Append(f.Z)
                    .Append("|o").Append(St(f).OffsetX.ToString("R")).Append(',').Append(St(f).OffsetY.ToString("R")).Append("|a").Append(St(f).Angle.ToString("R"))
                    .Append("|g").Append(f.Graphic.ToString("X")).Append("|f").Append(St(f).AnimGraphic.ToString("X")).Append(" i").Append(St(f).AnimIndex)
                    .Append(" n").Append(St(f).NextChange).Append(" d").Append(f.Duration).Append(" b").Append((int)f.Blend)
                    .Append(" dead").Append(f.IsDestroyed).Append('\n');
            }

            return sb.ToString();
        }

        // `frames` updates 25 ms apart; `each` runs before every update on both worlds (a target that moves, dies, ...);
        // `legacy`: compare _fx with the legacy rules (off where the record and the object knowingly part: an unhooked removal)
        private void Run(int frames, Action<World, int> each = null, int step = 25, bool legacy = true)
        {
            Snap(_fx).Should().NotBeEmpty("the scenario created effects");

            if (legacy)
            {
                for (GameEffect f = (GameEffect)_fx.Effects.Items; f != null; f = (GameEffect)f.Next)
                {
                    Seen(f);
                }
            }

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

                if (legacy)
                {
                    LegacyBefore();
                }

                _fx.Effects.Update();
                _bare.Effects.Update();

                if (legacy)
                {
                    LegacyAfter(i);
                }

                Snap(_bare).Should().Be(Snap(_fx), $"without the checker and with item offsets from the objects, after frame {i}");

                foreach (World w in Worlds)
                {
                    CountEntities(w).Should().Be(Snap(w).Count(c => c == '\n'), $"entities equal the live effects after frame {i}");
                    StateEqualsObjects(w, i);
                }
            }

            _mismatches.Should().BeEmpty();
            (EffectSystem.Compared - _comparedBefore).Should().BeGreaterThan(0, "the checker ran");

            if (legacy)
            {
                _legacyCompared.Should().BeGreaterThan(0, "updates were compared with the legacy rules");
            }
        }

        // every live effect's position is its object's (the object keeps it for its tile placement; T9 S7 deleted its frame,
        // offset and angle copies), its screen offset is the state's, and it holds no endpoint objects
        private static void StateEqualsObjects(World w, int frame)
        {
            for (GameEffect f = (GameEffect)w.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                ref readonly EffectState s = ref f.EcsEntity.GetComponent<EffectState>();
                (s.X, s.Y, s.Z).Should().Be((f.X, f.Y, f.Z), $"position after frame {frame}");
                f.ScreenOffset.Should().Be(new Vector3(s.OffsetX, s.OffsetY, s.OffsetZ), $"offset after frame {frame}");
                (f.Source, f.Target).Should().Be((null, null), "the state references the endpoints");
            }
        }

        // ---- the legacy rules (GameEffect.Update, LightningEffect.Update and the kinds' motion before T8, verbatim on copies):
        // each of _fx's live effects is stepped on its copy before every update and compared with the object after it. The
        // source and target are the objects World held under the endpoints' serials when the effect was first seen (the
        // constructor's World.Get), followed as objects: through a death re-key, and to their last coordinates once destroyed.
        // An effect created during the walk (an explosion) is checked against the expected explosions, then first compared on
        // the frame after.

        private sealed class Legacy
        {
            public GameObject Source, Target;

            // frame and lifetime
            public byte AnimIndex;
            public ushort AnimGraphic;
            public long Next;
            public bool FrameSteps, Destroys;

            // motion: what the update leaves
            public bool SourceEnds, Arrived;
            public ushort X, Y;
            public sbyte Z;
            public Vector3 Offset;
            public float Angle;
            public uint MoveTime;
        }

        private readonly Dictionary<GameEffect, Legacy> _legacy = new Dictionary<GameEffect, Legacy>(ReferenceEqualityComparer.Instance);
        private readonly List<GameEffect> _stepped = new List<GameEffect>();
        private readonly List<(ushort, ushort, sbyte)> _explosions = new List<(ushort, ushort, sbyte)>();
        private int _legacyCompared;

        private static GameObject EndpointObject(World w, bool set, bool mobile, uint serial) =>
            !set ? null : mobile ? w.FindMobile(serial) : w.FindItem(serial);

        // the copy of an effect seen for the first time (Run sees the scenario's effects before its first frame, so the
        // endpoint objects are the ones the effects were created with)
        private Legacy Seen(GameEffect f)
        {
            if (!_legacy.TryGetValue(f, out Legacy r))
            {
                ref readonly EffectState s = ref f.EcsEntity.GetComponent<EffectState>();
                r = new Legacy
                {
                    Source = EndpointObject(_fx, (s.Ends & EffectState.END_SOURCE) != 0, (s.Ends & EffectState.END_SOURCE_MOBILE) != 0, s.SourceSerial),
                    Target = EndpointObject(_fx, (s.Ends & EffectState.END_TARGET) != 0, (s.Ends & EffectState.END_TARGET_MOBILE) != 0, s.TargetSerial),
                    AnimIndex = St(f).AnimIndex,
                    AnimGraphic = St(f).AnimGraphic,
                    Next = St(f).NextChange
                };
                _legacy.Add(f, r);
            }

            return r;
        }

        private void LegacyBefore()
        {
            _stepped.Clear();
            _explosions.Clear();

            for (GameEffect f = (GameEffect)_fx.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                if (f.IsDestroyed)
                {
                    continue;
                }

                Legacy r = Seen(f);
                r.X = f.X;
                r.Y = f.Y;
                r.Z = f.Z;
                r.Offset = f.ScreenOffset;
                r.Angle = St(f).Angle;
                r.MoveTime = f is DragEffect ? St(f).MoveTime : 0;
                r.SourceEnds = r.Arrived = r.Destroys = false;
                r.FrameSteps = !(f is DragEffect && St(f).MoveTime > Time.Ticks);

                switch (f)
                {
                    case MovingEffect m:
                        r.SourceEnds = r.Source != null && r.Source.IsDestroyed;
                        r.Destroys = !r.SourceEnds && LegacyBase(f, r);

                        if (r.SourceEnds || r.Destroys)
                        {
                            // the legacy update still ran UpdateOffset on the destroyed effect, without its objects
                            r.Source = r.Target = null;
                            LegacyMoving(m, r, (m.TargetX, m.TargetY, m.TargetZ), true);
                        }
                        else
                        {
                            LegacyMoving(m, r, r.Target == null ? (m.TargetX, m.TargetY, m.TargetZ) : (r.Target.X, r.Target.Y, r.Target.Z), false);
                        }

                        break;

                    case DragEffect drag:
                        if (r.FrameSteps)
                        {
                            r.Offset.X += 8;
                            r.Offset.Y += 8;
                            r.MoveTime = Time.Ticks + 20;
                            r.SourceEnds = r.Source != null && r.Source.IsDestroyed;
                            r.Destroys = !r.SourceEnds && LegacyBase(f, r);
                        }

                        break;

                    case LightningEffect:
                        r.Destroys = LegacyLightning(f, r);

                        if (!r.Destroys)
                        {
                            (r.X, r.Y, r.Z) = r.Source == null ? (f.X, f.Y, f.Z) : (r.Source.X, r.Source.Y, r.Source.Z);
                        }

                        break;

                    default:
                        r.SourceEnds = r.Source != null && r.Source.IsDestroyed;
                        r.Destroys = !r.SourceEnds && LegacyBase(f, r);

                        if (!r.SourceEnds && !r.Destroys && r.Source != null)
                        {
                            r.Offset = r.Source.ScreenOffset;
                            (r.X, r.Y, r.Z) = (r.Source.X, r.Source.Y, r.Source.Z);
                        }

                        break;
                }

                _stepped.Add(f);
            }
        }

        private void LegacyAfter(int frame)
        {
            foreach (GameEffect f in _stepped)
            {
                Legacy r = _legacy[f];
                string at = $"0x{f.Graphic:X4} after frame {frame}";

                if (f.IsDestroyed)
                {
                    (r.Destroys || r.SourceEnds || r.Arrived || f.Distance > _fx.ClientViewRange).Should().BeTrue($"the legacy rules end {at}");

                    continue;
                }

                r.Destroys.Should().BeFalse($"the legacy rules end {at} (frame and lifetime)");
                r.SourceEnds.Should().BeFalse($"the legacy rules end {at} (its source is destroyed)");
                r.Arrived.Should().BeFalse($"the legacy rules end {at} (it arrived)");

                if (r.FrameSteps)
                {
                    (St(f).AnimIndex, St(f).AnimGraphic, St(f).NextChange).Should().Be((r.AnimIndex, r.AnimGraphic, r.Next), $"{at}: the frame");
                }

                (f.X, f.Y, f.Z).Should().Be((r.X, r.Y, r.Z), $"{at}: the position");
                f.ScreenOffset.Should().Be(r.Offset, $"{at}: the offset");
                St(f).Angle.Should().Be(r.Angle, $"{at}: the angle");

                if (f is DragEffect)
                {
                    St(f).MoveTime.Should().Be(r.MoveTime, $"{at}: the next move time");
                }

                _legacyCompared++;
            }

            var created = new List<(ushort, ushort, sbyte)>();

            for (GameEffect f = (GameEffect)_fx.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                if (!_legacy.ContainsKey(f))
                {
                    f.Graphic.Should().Be(0x36CB, "only explosions are created by an update");
                    created.Add((f.X, f.Y, f.Z));
                }
            }

            created.Should().BeEquivalentTo(_explosions, $"the explosions land where the legacy rules put them (frame {frame})");
        }

        // GameEffect.Update's frame block before T8; true when it would destroy the effect
        private static unsafe bool LegacyBase(GameEffect e, Legacy r)
        {
            if (e.IsEnabled)
            {
                if (e.Duration < Time.Ticks && e.Duration >= 0)
                {
                    return true;
                }

                if (r.Next < Time.Ticks)
                {
                    if (e.AnimDataFrame.FrameCount != 0)
                    {
                        r.AnimGraphic = (ushort)(e.Graphic + e.AnimDataFrame.FrameData[r.AnimIndex]);
                        r.AnimIndex++;

                        if (r.AnimIndex >= e.AnimDataFrame.FrameCount)
                        {
                            r.AnimIndex = 0;
                        }
                    }
                    else
                    {
                        r.AnimGraphic = e.Graphic;
                    }

                    r.Next = (long)Time.Ticks + e.IntervalInMs;
                }
            }
            else
            {
                r.AnimGraphic = e.Graphic;
            }

            return false;
        }

        // LightningEffect.Update's frame block before T8
        private static bool LegacyLightning(GameEffect e, Legacy r)
        {
            if (r.AnimIndex >= 10 || (e.Duration < Time.Ticks && e.Duration >= 0))
            {
                return true;
            }

            r.AnimGraphic = (ushort)(e.Graphic + r.AnimIndex);

            if (r.Next < Time.Ticks)
            {
                r.AnimIndex++;
                r.Next = (long)Time.Ticks + e.IntervalInMs;
            }

            return false;
        }

        // MovingEffect.UpdateOffset before T8 (after GameEffect.Update), on the copy; the player is the object World holds.
        // `destroyed`: the update the legacy code still ran after the source ended the effect (only its explosion is seen).
        private void LegacyMoving(MovingEffect e, Legacy r, (int x, int y, int z) t, bool destroyed)
        {
            int playerX = _fx.Player.X;
            int playerY = _fx.Player.Y;
            int playerZ = _fx.Player.Z;

            (int sX, int sY, int sZ) = r.Source == null ? (e.X, e.Y, e.Z) : (r.Source.X, r.Source.Y, r.Source.Z);
            int offsetSourceX = sX - playerX;
            int offsetSourceY = sY - playerY;
            int offsetSourceZ = sZ - playerZ;

            (int tX, int tY, int tZ) = t;
            int offsetTargetX = tX - playerX;
            int offsetTargetY = tY - playerY;
            int offsetTargetZ = tZ - playerZ;

            Vector2 source = new Vector2((offsetSourceX - offsetSourceY) * 22, (offsetSourceX + offsetSourceY) * 22 - offsetSourceZ * 4);
            source.X += r.Offset.X;
            source.Y += r.Offset.Y;

            Vector2 target = new Vector2((offsetTargetX - offsetTargetY) * 22, (offsetTargetX + offsetTargetY) * 22 - offsetTargetZ * 4);

            var offset = target - source;
            var distance = offset.Length();
            var frameIndependentSpeed = e.IntervalInMs * Time.Delta;
            Vector2 s0;

            if (distance > frameIndependentSpeed)
            {
                offset.Normalize();
                s0 = offset * frameIndependentSpeed;
            }
            else
            {
                s0 = target;
            }

            bool arrived = distance <= 22;
            int newCoordX = 0, newCoordY = 0, newX = 0, newY = 0;

            if (!arrived)
            {
                int newOffsetX = (int)(source.X / 22f);
                int newOffsetY = (int)(source.Y / 22f);
                MovingEffect.TileOffsetOnMonitorToXY(ref newOffsetX, ref newOffsetY, out newCoordX, out newCoordY);
                newX = playerX + newCoordX;
                newY = playerY + newCoordY;
                arrived = newX == tX && newY == tY;
            }

            if (arrived)
            {
                r.Arrived = !destroyed;

                if (e.CanCreateExplosionEffect)
                {
                    _explosions.Add(((ushort)tX, (ushort)tY, (sbyte)tZ));
                }

                return;
            }

            if (destroyed)
            {
                return;
            }

            r.Angle = (float)Math.Atan2(-offset.Y, -offset.X);

            if (newX != sX || newY != sY)
            {
                // SetSource(x, y, z): the effect's own tile is its source from now on
                r.Source = null;
                r.X = (ushort)newX;
                r.Y = (ushort)newY;
                r.Z = (sbyte)sZ;

                Vector2 nextSource = new Vector2((newCoordX - newCoordY) * 22, (newCoordX + newCoordY) * 22 - offsetSourceZ * 4);
                r.Offset.X = source.X - nextSource.X;
                r.Offset.Y = source.Y - nextSource.Y;
            }
            else
            {
                (r.X, r.Y, r.Z) = (e.X, e.Y, e.Z);
            }

            r.Offset.X += s0.X;
            r.Offset.Y += s0.Y;
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
            Snap(_fx).Should().Contain("FixedEffect", "the no-duration effect is still running");
        }

        [Fact]
        public void The_Frame_List_Wraps_And_Resets_The_Index()
        {
            foreach (World w in Worlds)
            {
                FixedWithFrames(w, 1001, 1001, 2, 4000);
            }

            Run(30);
            _fx.Effects.Items.Should().NotBeNull();
            St((GameEffect)_fx.Effects.Items).AnimIndex.Should().BeLessThan(2);
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
            _fx.Effects.Items.Should().BeNull("every effect ended");
            CountEntities(_fx).Should().Be(0);
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
            _fx.Effects.Items.Should().BeNull();
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
            ((GameEffect)_fx.Effects.Items).PendingBlock.Should().Be(77, "the parked effect stays parked");
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
        public void Every_Effect_Gets_An_Entity_And_Clearing_Destroys_Them()
        {
            Create(_fx, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1001, 1001, 0, 0, duration: 10);
            CountEntities(_fx).Should().Be(1);
            _fx.Effects.Clear();
            CountEntities(_fx).Should().Be(0, "clearing destroys the entities");
        }

        [Fact]
        public void The_Checker_Flags_A_Bad_State()
        {
            foreach (World w in Worlds)
            {
                FixedWithFrames(w, 1001, 1001, 4, 400);
            }

            Run(5);

            // a position that drifted from the object's (a missed tile placement) is reported; the frame, offset and angle live
            // only in the state (T9 S7), so a changed frame is no mismatch
            var fx = (GameEffect)_fx.Effects.Items;
            fx.EcsEntity.GetComponent<EffectState>().X = 5;
            EffectSystem.Check(_fx, _fx.Effects);
            _mismatches.Should().Contain(m => m.Contains("position"));

            _mismatches.Clear();
            fx.EcsEntity.GetComponent<EffectState>().X = fx.X;
            fx.EcsEntity.GetComponent<EffectState>().AnimIndex = 9;
            EffectSystem.Check(_fx, _fx.Effects);
            _mismatches.Should().BeEmpty();
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
        }

        // Codex audit (ecs-effects-own) P2: a second death under the same serial displaces the first dying mobile from
        // serial | 0x80000000, and the second mobile is written into that record. The displaced mobile's endpoints end at its
        // last position and let go of the record (a moving effect to it lands there, as legacy, whose orphaned object stays
        // put), also when the record is re-keyed once more; the second mobile's effects follow it.
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

            GameEffect toFirst = (GameEffect)_fx.Effects.Items;

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
            });

            toFirst.IsDestroyed.Should().BeFalse("the moving effect was still on its way when the scenario ended");
            ref EffectState s = ref State(toFirst);
            (s.Ends & EffectState.END_TARGET_GONE).Should().Be(EffectState.END_TARGET_GONE, "the displaced mobile's endpoint is gone");
            (s.TargetX, s.TargetY).Should().Be(((ushort)1014, (ushort)1010));
            s.TargetEntity.IsNull.Should().BeTrue("a gone endpoint lets go of a record that lives on as another mobile's");
        }

        // Codex audit (ecs-effects-own) P2: a mobile and an item share a serial and the mirror holds the item's record. Before
        // world_entity_index, World resolved the serial to the mobile, which could not be bound (the endpoint became a ground
        // point). Since (owner decision 2026-11-08) World resolves the serial to the object holding the record, the item, so
        // both endpoints bind to the items' records and nothing is unbound
        [Fact]
        public void A_Shared_Serial_Endpoint_Binds_To_The_Item_Holding_The_Record()
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

            foreach (World w in Worlds)
            {
                int bound = 0;

                for (GameEffect f = (GameEffect)w.Effects.Items; f != null; f = (GameEffect)f.Next)
                {
                    ref EffectState st = ref State(f);

                    if ((st.Ends & EffectState.END_TARGET) != 0)
                    {
                        (st.Ends & EffectState.END_TARGET_MOBILE).Should().Be(0, "the target is the item holding the record");
                        (st.TargetSerial, st.TargetX, st.TargetY).Should().Be((TARGET, (ushort)1002, (ushort)1003));
                        bound++;
                    }

                    if ((st.Ends & EffectState.END_SOURCE) != 0)
                    {
                        (st.Ends & EffectState.END_SOURCE_MOBILE).Should().Be(0, "the source is the item holding the record");
                        (st.SourceSerial, st.SourceX, st.SourceY).Should().Be((CASTER, (ushort)1003, (ushort)1002));
                        bound++;
                    }
                }

                bound.Should().Be(2, "both endpoints are bound");
            }

            Run(60);

            EffectSystem.EndpointsUnbound.Should().Be(unbound, "World resolves both serials to the items, whose records bind");
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
                    WrapTo(w, stale);

                    if (itemUnderTheSerial)
                    {
                        AddItem(w, TARGET, 1003, 1012);
                    }
                    else
                    {
                        AddMobile(w, OTHER, 1003, 1012);
                    }

                    w.Ecs.Mirror.TryGetEntity(newcomer, out var rec).Should().BeTrue();
                    (rec.Id, rec.Revision).Should().Be((stale.Id, stale.Revision), "the new record has the stale handle's id and revision");
                }
            });
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
                    w.Ecs.Mirror.TryGetEntity(TARGET, out var rec).Should().BeTrue();
                    stale[w] = rec;

                    w.Ecs.Mirror.Remove(TARGET); // no hook sees it: the next update finds the endpoint gone
                }
                else if (i == 4)
                {
                    WrapTo(w, stale[w]);

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
            });
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
                ItemOffsets.Push(w, it, new Microsoft.Xna.Framework.Vector3(2 * i, i, i % 3)); // the boat operations' ECS write
            });

            // the offsets are read from the ECS, not from the objects: the mobile's Steps, and with item offsets from the ECS
            // (ecs_item_offset) the item's ItemOffset component
            Mobile caster = _fx.Mobiles.Get(CASTER);
            _fx.Ecs.Mirror.TryGetEntity(CASTER, out var rec).Should().BeTrue();
            rec.GetComponent<ClassicUO.Ecs.Steps>().OffsetX = 77;
            _fx.Ecs.Mirror.TryGetEntity(ITEM, out var itemRec).Should().BeTrue();
            itemRec.GetComponent<ClassicUO.Ecs.ItemOffset>().Y = -5;
            _fx.Effects.Update();
            var fixedOnMobile = (GameEffect)_fx.Effects.Items;
            fixedOnMobile.ScreenOffset.X.Should().Be(77);
            caster.OffsetX.Should().NotBe(77);
            ((GameEffect)fixedOnMobile.Next).ScreenOffset.Y.Should().Be(-5);
            _mismatches.Should().BeEmpty();
        }

        [Fact]
        public void An_Effect_Holds_No_Objects_And_An_Unseen_Removal_Leaves_The_Last_Position()
        {
            AddMobile(_fx, CASTER, 1003, 1003);
            AddMobile(_fx, TARGET, 1012, 1008);
            Create(_fx, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            Create(_fx, GraphicEffectType.FixedXYZ, 0, 0, 0x3779, 1001, 1001, 0, 0, duration: 400);
            Create(_fx, GraphicEffectType.FixedFrom, CASTER, 0, 0x3709, 0, 0, 0, 0, duration: 400);

            var fx = (GameEffect)_fx.Effects.Items;
            var follower = (GameEffect)fx.Next.Next;
            fx.Source.Should().BeNull("the effect references its source by record only");
            fx.Target.Should().BeNull();
            fx.SourceObject.Should().BeSameAs(_fx.Mobiles.Get(CASTER), "the views find the object World holds");
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
            _fx.Ecs.Mirror.Remove(TARGET);
            _fx.Effects.Update();
            s = ref State(fx);
            (s.Ends & EffectState.END_TARGET_GONE).Should().Be(EffectState.END_TARGET_GONE);
            (s.TargetX, s.TargetY).Should().Be(((ushort)1012, (ushort)1008));
            EffectSystem.EndpointsUnhooked.Should().BeGreaterThan(unhooked);

            // and the source's record: the effect that follows it ends as when its source is destroyed
            _fx.Ecs.Mirror.Remove(CASTER);
            _fx.Effects.Update();
            follower.IsDestroyed.Should().BeTrue();
        }

        // ---- T8 ownership phase 2: the motion step

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
        public void The_Motion_Step_Holds_No_Objects_And_Reads_The_Player_From_The_ECS()
        {
            AddMobile(_bare, CASTER, 1003, 1003);
            AddMobile(_bare, TARGET, 1012, 1008);
            Create(_bare, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            var fx = (GameEffect)_bare.Effects.Items;
            fx.Source.Should().BeNull();
            fx.Target.Should().BeNull();

            // the player's object moved outside the funnel: the path still uses the record's position
            AddMobile(_fx, CASTER, 1003, 1003);
            AddMobile(_fx, TARGET, 1012, 1008);
            Create(_fx, GraphicEffectType.Moving, CASTER, TARGET, 0x36D4, 0, 0, 0, 0, speed: 1, duration: 0);
            // (beyond the target, so the screen offsets change sign and the tile truncation differs)
            _bare.Player.X = 1020;
            _bare.Player.Y = 1015;

            for (int i = 0; i < 15; i++)
            {
                Time.Ticks = T0 + (uint)(i * 25);
                _bare.Effects.Update();
                _fx.Effects.Update();
                Snap(_bare).Should().Be(Snap(_fx), $"the record still holds the player at 1000,1000 (frame {i})");
            }
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
                else if (ReferenceEquals(w, _fx))
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
    }
}
