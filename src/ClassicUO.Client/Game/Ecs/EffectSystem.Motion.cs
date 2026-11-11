// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Position = ClassicUO.Ecs.Position;

namespace ClassicUO.Game
{
    /// <summary>
    /// T8 ownership phase 2 (<c>ecs_effect_motion</c>): the kinds' motion as steps over <see cref="EffectState"/>. Each step
    /// resolves the endpoints it needs once (the source's gone test, position and offset from one row; a moving effect's
    /// target likewise), runs the base rules (source gone, <see cref="Animate"/>) in the legacy order, then the kind's motion
    /// on the state's fields, and writes the results to the object for the views (position through <c>SetInWorldTile</c>,
    /// which owns the tile placement). The steps replace the per-update <see cref="Publish"/>. No random number is drawn.
    /// CUO_EFFECT_MOTION_CHECK replays the legacy per-object motion (verbatim copies of the old blocks, reading the kept
    /// objects and <c>World.Player</c>) before each step and compares the outcome.
    /// </summary>
    internal static partial class EffectSystem
    {
        /// <summary>Motion counts (all worlds): steps run, steps compared with the legacy replay, mismatches.</summary>
        public static long MotionSteps, MotionCompared, MotionMismatches;

        private static uint s_nextMotionLog;

        /// <summary>The player's position from its mirror record (the object's without one; legacy reads the object).</summary>
        internal static (int x, int y, int z) PlayerPosition(World world)
        {
            PlayerMobile p = world.Player;

            if (p.TryScalarEntity(out EcsEntity rec))
            {
                ref readonly Position pos = ref EcsRead.Get<Position>(rec);

                return (pos.X, pos.Y, pos.Z);
            }

            return (p.X, p.Y, p.Z);
        }

        // ---- endpoints, once per update

        // The source's position (its record's while it lives, the last resolved one once gone, the effect's own for a ground
        // point) and whether it is gone; `offset` the source object's offset when `wantOffset` (FixedEffect). With the endpoint
        // checker compared as SourceGone/SourceOf/SourceOffsetOf were (lightning never tested the source for gone).
        private static (ushort x, ushort y, sbyte z) ResolveSource(GameEffect e, ref EffectState s, bool testsGone, bool wantOffset,
            out bool gone, out Vector3 offset)
        {
            (ushort x, ushort y, sbyte z) r;
            gone = false;
            offset = default;

            if ((s.Ends & EffectState.END_SOURCE) == 0)
            {
                r = (s.X, s.Y, s.Z);
            }
            else
            {
                if (Live(ref s, EffectState.END_SOURCE, EffectState.END_SOURCE_GONE, s.SourceEntity, out EcsRow row))
                {
                    ref readonly Position p = ref row.Get<Position>();
                    s.SourceX = p.X;
                    s.SourceY = p.Y;
                    s.SourceZ = p.Z;

                    if (wantOffset)
                    {
                        offset = SourceOffset(e, ref s, row);
                    }
                }
                else
                {
                    gone = true;
                }

                r = (s.SourceX, s.SourceY, s.SourceZ);
            }

            if (e.EndpointCheck)
            {
                if (testsGone)
                {
                    CompareFlag(e, "source destroyed", gone, e.Source != null && e.Source.IsDestroyed);
                }

                CompareEndpoint(e, "source", r, e.LegacySource());

                if (wantOffset && !gone)
                {
                    CompareFlag(e, "source offset", (s.Ends & EffectState.END_SOURCE) != 0, e.Source != null);

                    if (e.Source != null && !e.Source.IsDestroyed && offset != e.Source.Offset)
                    {
                        EndpointMismatch($"effect 0x{e.Graphic:X4} source offset ECS {offset} | object {e.Source.Offset}");
                    }
                }
            }

            return r;
        }

        // A live source's offset: a mobile's from its Steps while the step system keeps them (otherwise the Mobile World holds
        // under the serial), an item's (a boat passenger's) from its ItemOffset (absent = zero).
        private static Vector3 SourceOffset(GameEffect e, ref EffectState s, EcsRow row)
        {
            if ((s.Ends & EffectState.END_SOURCE_MOBILE) != 0)
            {
                if (e.World.Ecs.StepsEnabled && row.TryGet(out Steps st))
                {
                    return new Vector3(st.OffsetX, st.OffsetY, st.OffsetZ);
                }

                return e.World.FindMobile(s.SourceSerial)?.Offset ?? default;
            }

            return row.TryGet(out ItemOffset io) ? new Vector3(io.X, io.Y, io.Z) : Vector3.Zero;
        }

        private static (ushort x, ushort y, sbyte z) ResolveTarget(GameEffect e, ref EffectState s, out bool gone)
        {
            gone = false;

            if ((s.Ends & EffectState.END_TARGET) != 0)
            {
                if (Live(ref s, EffectState.END_TARGET, EffectState.END_TARGET_GONE, s.TargetEntity, out EcsRow row))
                {
                    ref readonly Position p = ref row.Get<Position>();
                    s.TargetX = p.X;
                    s.TargetY = p.Y;
                    s.TargetZ = p.Z;
                }
                else
                {
                    gone = true;
                }
            }

            // a ground target is the effect's target coordinates, which Register published into the state
            (ushort x, ushort y, sbyte z) r = (s.TargetX, s.TargetY, s.TargetZ);

            if (e.EndpointCheck)
            {
                CompareFlag(e, "target destroyed", gone, e.Target != null && e.Target.IsDestroyed);
                CompareEndpoint(e, "target", r, e.LegacyTarget());
            }

            return r;
        }

        private static void SetMovingFlag(GameEffect e, ref EffectState s)
        {
            // GameEffect.IsMoving: the target is an object, or the effect's own target coordinates are set
            bool moving = (s.Ends & EffectState.END_TARGET) != 0 || e.TargetX != 0 && e.TargetY != 0;
            s.Flags = (byte)(moving ? s.Flags | EffectState.FLAG_MOVING : s.Flags & ~EffectState.FLAG_MOVING);
        }

        // ---- the steps

        /// <summary>FixedEffect.Update: the source test, the frame step, then following the source (position and offset).</summary>
        public static void StepFixed(GameEffect e)
        {
            MotionExpect expect = default;

            if (e.MotionCheck)
            {
                ReplayFixed(e, ref expect);
            }

            MotionSteps++;
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            bool hasSource = (s.Ends & EffectState.END_SOURCE) != 0;
            (ushort x, ushort y, sbyte z) = ResolveSource(e, ref s, true, true, out bool gone, out Vector3 offset);

            if (gone)
            {
                e.Destroy();

                return;
            }

            Animate(e);

            if (e.IsDestroyed)
            {
                return;
            }

            if (hasSource)
            {
                s.OffsetX = offset.X;
                s.OffsetY = offset.Y;
                s.OffsetZ = offset.Z;
            }

            bool moved = s.X != x || s.Y != y || s.Z != z;

            if (moved)
            {
                s.X = x;
                s.Y = y;
                s.Z = z;
            }

            SetMovingFlag(e, ref s);

            if (expect.On)
            {
                CompareMotion(e, in expect, Actual(in s, x, y, z, false));
            }

            if (hasSource)
            {
                e.Offset = offset;
            }

            if (moved)
            {
                e.SetInWorldTile(x, y, z);
            }
        }

        /// <summary>LightningEffect.Update: the frame step, then following the source (no source test: a gone source leaves
        /// the effect at the source's last position).</summary>
        public static void StepLightning(GameEffect e)
        {
            MotionExpect expect = default;

            if (e.MotionCheck)
            {
                ReplayLightning(e, ref expect);
            }

            MotionSteps++;

            if (!AnimateLightning(e))
            {
                return;
            }

            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            (ushort x, ushort y, sbyte z) = ResolveSource(e, ref s, false, false, out _, out _);
            bool moved = s.X != x || s.Y != y || s.Z != z;

            if (moved)
            {
                s.X = x;
                s.Y = y;
                s.Z = z;
            }

            SetMovingFlag(e, ref s);

            if (expect.On)
            {
                CompareMotion(e, in expect, Actual(in s, x, y, z, false));
            }

            if (moved)
            {
                e.SetInWorldTile(x, y, z);
            }
        }

        /// <summary>DragEffect.Update: every 20 ms the offset grows by 8 on both axes, then the source test and the frame step.
        /// The drag effect never follows its source; its target is not read.</summary>
        public static void StepDrag(DragEffect e)
        {
            MotionExpect expect = default;

            if (e.MotionCheck)
            {
                ReplayDrag(e, ref expect);
            }

            MotionSteps++;
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);

            if (s.MoveTime > Time.Ticks)
            {
                if (expect.On)
                {
                    CompareMotion(e, in expect, Actual(in s, s.X, s.Y, s.Z, false));
                }

                return;
            }

            s.OffsetX += 8;
            s.OffsetY += 8;
            s.MoveTime = Time.Ticks + 20;
            e.OffsetX = s.OffsetX;
            e.OffsetY = s.OffsetY;
            e.LastMoveTime = s.MoveTime;

            ResolveSource(e, ref s, true, false, out bool gone, out _);

            if (gone)
            {
                e.Destroy();

                return;
            }

            if (expect.On)
            {
                CompareMotion(e, in expect, Actual(in s, s.X, s.Y, s.Z, false));
            }

            Animate(e);
        }

        /// <summary>
        /// MovingEffect.Update: the source test and the frame step, then the path toward the target (UpdateOffset's rules on
        /// the state: screen-space step of <c>Interval * Time.Delta</c>, re-anchoring to the tile under the effect, arrival
        /// within 22 px or on the target's tile with the explosion). False when the effect was destroyed before it moved:
        /// the legacy update then still runs UpdateOffset on the destroyed object, which the caller does.
        /// </summary>
        public static bool StepMoving(MovingEffect e)
        {
            MotionExpect expect = default;

            if (e.MotionCheck)
            {
                ReplayMoving(e, ref expect);
            }

            MotionSteps++;
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            (int sX, int sY, int sZ) = ResolveSource(e, ref s, true, false, out bool sourceGone, out _);

            if (sourceGone)
            {
                e.Destroy();

                return false;
            }

            Animate(e);

            if (e.IsDestroyed)
            {
                return false;
            }

            (ushort tx, ushort ty, sbyte tz) = ResolveTarget(e, ref s, out bool targetGone);

            if (targetGone)
            {
                // the legacy copy of the destroyed target's coordinates (IsMoving and the post-destroy update read them)
                e.TargetX = tx;
                e.TargetY = ty;
                e.TargetZ = tz;
            }

            (int playerX, int playerY, int playerZ) = e.Manager.MotionPlayer();
            int tX = tx, tY = ty, tZ = tz;

            int offsetSourceX = sX - playerX;
            int offsetSourceY = sY - playerY;
            int offsetSourceZ = sZ - playerZ;
            int offsetTargetX = tX - playerX;
            int offsetTargetY = tY - playerY;
            int offsetTargetZ = tZ - playerZ;

            Vector2 source = new Vector2((offsetSourceX - offsetSourceY) * 22, (offsetSourceX + offsetSourceY) * 22 - offsetSourceZ * 4);
            source.X += s.OffsetX;
            source.Y += s.OffsetY;

            Vector2 target = new Vector2((offsetTargetX - offsetTargetY) * 22, (offsetTargetX + offsetTargetY) * 22 - offsetTargetZ * 4);

            Vector2 offset = target - source;
            float distance = offset.Length();
            float frameIndependentSpeed = s.Interval * Time.Delta;
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
                if (expect.On)
                {
                    CompareMotion(e, in expect, new MotionExpect { On = true, Arrived = true, TargetX = tx, TargetY = ty, TargetZ = tz });
                }

                // the explosion creates an entity: no state ref is used after it
                if ((s.Flags & EffectState.FLAG_EXPLODES) != 0)
                {
                    e.CreateExplosionAt(tx, ty, tz);
                }

                e.Destroy();

                return true;
            }

            s.Angle = (float)Math.Atan2(-offset.Y, -offset.X);
            bool moved = newX != sX || newY != sY;

            if (moved)
            {
                // the effect leaves its source: its own tile is the source from now on (SetSource(x, y, z))
                s.Ends &= unchecked((byte)~(EffectState.END_SOURCE | EffectState.END_SOURCE_GONE | EffectState.END_SOURCE_MOBILE));
                s.SourceEntity = default;
                s.SourceSerial = 0;
                s.X = (ushort)newX;
                s.Y = (ushort)newY;
                s.Z = (sbyte)sZ;

                Vector2 nextSource = new Vector2((newCoordX - newCoordY) * 22, (newCoordX + newCoordY) * 22 - offsetSourceZ * 4);
                s.OffsetX = source.X - nextSource.X;
                s.OffsetY = source.Y - nextSource.Y;
            }

            s.OffsetX += s0.X;
            s.OffsetY += s0.Y;
            SetMovingFlag(e, ref s);

            if (expect.On)
            {
                MotionExpect actual = Actual(in s, s.X, s.Y, s.Z, moved);
                actual.TargetX = tx;
                actual.TargetY = ty;
                actual.TargetZ = tz;
                CompareMotion(e, in expect, actual);
            }

            float angle = s.Angle, ox = s.OffsetX, oy = s.OffsetY;
            ushort x = s.X, y = s.Y;
            sbyte z = s.Z;

            // the object, in UpdateOffset's write order
            e.MarkPositionChanged();
            e.AngleToTarget = angle;

            if (moved)
            {
                e.Source = null;
                e.SetInWorldTile(x, y, z);
            }

            e.OffsetX = ox;
            e.OffsetY = oy;

            return true;
        }

        // ---- CUO_EFFECT_MOTION_CHECK: the legacy per-object motion, replayed on the object's fields before the step

        /// <summary>What an update leaves: position, offset, angle, the target the path aimed at (moving), whether the effect
        /// arrived (moving), left its source (moving), or ended by its source (not compared: the endpoint checker's).</summary>
        private struct MotionExpect
        {
            public bool On, Arrived, LeftSource, SourceEnds;
            public ushort X, Y, TargetX, TargetY;
            public sbyte Z, TargetZ;
            public Vector3 Offset;
            public float Angle;
            public uint MoveTime;
        }

        private static MotionExpect Actual(in EffectState s, ushort x, ushort y, sbyte z, bool leftSource) => new MotionExpect
        {
            On = true,
            X = x,
            Y = y,
            Z = z,
            Offset = new Vector3(s.OffsetX, s.OffsetY, s.OffsetZ),
            Angle = s.Angle,
            MoveTime = s.MoveTime,
            LeftSource = leftSource
        };

        private static void Start(GameEffect e, ref MotionExpect x)
        {
            x.On = true;
            x.X = e.X;
            x.Y = e.Y;
            x.Z = e.Z;
            x.Offset = e.Offset;
            x.Angle = e.AngleToTarget;
            x.MoveTime = e is DragEffect d ? d.LastMoveTime : 0;
        }

        // FixedEffect.Update (GameEffect.Update's source test, then the follow block), verbatim on the copy
        private static void ReplayFixed(GameEffect e, ref MotionExpect x)
        {
            Start(e, ref x);

            if (e.Source != null && e.Source.IsDestroyed)
            {
                x.SourceEnds = true;

                return;
            }

            (var sx, var sy, var sz) = e.LegacySource();

            if (e.Source != null)
            {
                x.Offset = e.Source.Offset;
            }

            x.X = sx;
            x.Y = sy;
            x.Z = sz;
        }

        // LightningEffect.Update's follow block
        private static void ReplayLightning(GameEffect e, ref MotionExpect x)
        {
            Start(e, ref x);
            (x.X, x.Y, x.Z) = e.LegacySource();
        }

        // DragEffect.Update's offset block and GameEffect.Update's source test
        private static void ReplayDrag(DragEffect e, ref MotionExpect x)
        {
            Start(e, ref x);

            if (e.LastMoveTime > Time.Ticks)
            {
                return;
            }

            x.Offset.X += 8;
            x.Offset.Y += 8;
            x.MoveTime = Time.Ticks + 20;
            x.SourceEnds = e.Source != null && e.Source.IsDestroyed;
        }

        // MovingEffect.UpdateOffset before phase 2, verbatim on the copy (after GameEffect.Update's source test)
        private static void ReplayMoving(MovingEffect e, ref MotionExpect x)
        {
            Start(e, ref x);

            if (e.Source != null && e.Source.IsDestroyed)
            {
                x.SourceEnds = true;

                return;
            }

            int playerX = e.World.Player.X;
            int playerY = e.World.Player.Y;
            int playerZ = e.World.Player.Z;

            (int sX, int sY, int sZ) = e.LegacySource();
            int offsetSourceX = sX - playerX;
            int offsetSourceY = sY - playerY;
            int offsetSourceZ = sZ - playerZ;

            (int tX, int tY, int tZ) = e.LegacyTarget();
            int offsetTargetX = tX - playerX;
            int offsetTargetY = tY - playerY;
            int offsetTargetZ = tZ - playerZ;
            x.TargetX = (ushort)tX;
            x.TargetY = (ushort)tY;
            x.TargetZ = (sbyte)tZ;

            Vector2 source = new Vector2((offsetSourceX - offsetSourceY) * 22, (offsetSourceX + offsetSourceY) * 22 - offsetSourceZ * 4);

            source.X += e.Offset.X;
            source.Y += e.Offset.Y;

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

            if (distance <= 22)
            {
                x.Arrived = true;

                return;
            }

            int newOffsetX = (int)(source.X / 22f);
            int newOffsetY = (int)(source.Y / 22f);

            MovingEffect.TileOffsetOnMonitorToXY(ref newOffsetX, ref newOffsetY, out int newCoordX, out int newCoordY);

            int newX = playerX + newCoordX;
            int newY = playerY + newCoordY;

            if (newX == tX && newY == tY)
            {
                x.Arrived = true;

                return;
            }

            x.Angle = (float)Math.Atan2(-offset.Y, -offset.X);

            if (newX != sX || newY != sY)
            {
                x.LeftSource = true;
                x.X = (ushort)newX;
                x.Y = (ushort)newY;
                x.Z = (sbyte)sZ;

                Vector2 nextSource = new Vector2((newCoordX - newCoordY) * 22, (newCoordX + newCoordY) * 22 - offsetSourceZ * 4);

                x.Offset.X = source.X - nextSource.X;
                x.Offset.Y = source.Y - nextSource.Y;
            }

            x.Offset.X += s0.X;
            x.Offset.Y += s0.Y;
        }

        private static void CompareMotion(GameEffect e, in MotionExpect legacy, in MotionExpect ecs)
        {
            MotionCompared++;

            if (legacy.SourceEnds)
            {
                MotionMismatch($"effect 0x{e.Graphic:X4}: legacy ends it by its source, the ECS step moved it");

                return;
            }

            if (legacy.Arrived != ecs.Arrived)
            {
                MotionMismatch($"effect 0x{e.Graphic:X4} arrival ECS {ecs.Arrived} | legacy {legacy.Arrived} (target ECS ({ecs.TargetX},{ecs.TargetY},{ecs.TargetZ}) | legacy ({legacy.TargetX},{legacy.TargetY},{legacy.TargetZ}))");

                return;
            }

            if (legacy.TargetX != ecs.TargetX || legacy.TargetY != ecs.TargetY || legacy.TargetZ != ecs.TargetZ)
            {
                MotionMismatch($"effect 0x{e.Graphic:X4} target ECS ({ecs.TargetX},{ecs.TargetY},{ecs.TargetZ}) | legacy ({legacy.TargetX},{legacy.TargetY},{legacy.TargetZ})");
            }

            if (ecs.Arrived)
            {
                return;
            }

            if (legacy.X != ecs.X || legacy.Y != ecs.Y || legacy.Z != ecs.Z || legacy.LeftSource != ecs.LeftSource)
            {
                MotionMismatch($"effect 0x{e.Graphic:X4} position ECS ({ecs.X},{ecs.Y},{ecs.Z}) left source {ecs.LeftSource} | legacy ({legacy.X},{legacy.Y},{legacy.Z}) {legacy.LeftSource}");
            }

            if (legacy.Offset != ecs.Offset || legacy.Angle != ecs.Angle || legacy.MoveTime != ecs.MoveTime)
            {
                MotionMismatch($"effect 0x{e.Graphic:X4} offset ECS ({ecs.Offset.X:R},{ecs.Offset.Y:R},{ecs.Offset.Z:R}) a={ecs.Angle:R} t={ecs.MoveTime} | legacy ({legacy.Offset.X:R},{legacy.Offset.Y:R},{legacy.Offset.Z:R}) a={legacy.Angle:R} t={legacy.MoveTime}");
            }
        }

        internal static void MotionMismatch(string text)
        {
            MotionMismatches++;
            Observer?.Invoke(text);

            if (MotionMismatches <= 10)
            {
                Log.Warn($"[effect-motion-check] mismatch: {text}");
            }
        }

        /// <summary>CUO_EFFECT_MOTION_CHECK: the 5 s summary (end of the effects stage).</summary>
        public static void LogMotionCheck()
        {
            if (Time.Ticks >= s_nextMotionLog)
            {
                s_nextMotionLog = Time.Ticks + 5000;
                Log.Info($"[effect-motion-check] {MotionSteps} steps, {MotionCompared} compared, {MotionMismatches} mismatches");
            }
        }
    }
}
