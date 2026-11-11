// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using Microsoft.Xna.Framework;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Position = ClassicUO.Ecs.Position;

namespace ClassicUO.Game
{
    /// <summary>
    /// T8 ownership phase 2: the kinds' motion as steps over <see cref="EffectState"/> (T9 deleted the object path of
    /// <c>ecs_effect_endpoints</c> and <c>ecs_effect_motion</c>, and their checkers). Each step resolves the endpoints it needs once
    /// (the source's gone test, position and offset from one row; a moving effect's target likewise), runs the base rules
    /// (source gone, <see cref="Animate"/>) in the legacy order, then the kind's motion on the state's fields, and writes the
    /// results to the object for the views (position through <c>SetInWorldTile</c>, which owns the tile placement). The steps
    /// are the only writer of the object's motion fields. No random number is drawn.
    /// </summary>
    internal static partial class EffectSystem
    {
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
        // point) and whether it is gone (lightning never tests it); `offset` the source object's offset when `wantOffset`
        // (FixedEffect).
        private static (ushort x, ushort y, sbyte z) ResolveSource(GameEffect e, ref EffectState s, bool wantOffset, out bool gone,
            out Vector3 offset)
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

        private static (ushort x, ushort y, sbyte z) ResolveTarget(ref EffectState s, out bool gone)
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
            return (s.TargetX, s.TargetY, s.TargetZ);
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
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            bool hasSource = (s.Ends & EffectState.END_SOURCE) != 0;
            (ushort x, ushort y, sbyte z) = ResolveSource(e, ref s, true, out bool gone, out Vector3 offset);

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
            if (!AnimateLightning(e))
            {
                return;
            }

            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            (ushort x, ushort y, sbyte z) = ResolveSource(e, ref s, false, out _, out _);
            bool moved = s.X != x || s.Y != y || s.Z != z;

            if (moved)
            {
                s.X = x;
                s.Y = y;
                s.Z = z;
            }

            SetMovingFlag(e, ref s);

            if (moved)
            {
                e.SetInWorldTile(x, y, z);
            }
        }

        /// <summary>DragEffect.Update: every 20 ms the offset grows by 8 on both axes, then the source test and the frame step.
        /// The drag effect never follows its source; its target is not read.</summary>
        public static void StepDrag(DragEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);

            if (s.MoveTime > Time.Ticks)
            {
                return;
            }

            s.OffsetX += 8;
            s.OffsetY += 8;
            s.MoveTime = Time.Ticks + 20;
            e.OffsetX = s.OffsetX;
            e.OffsetY = s.OffsetY;
            e.LastMoveTime = s.MoveTime;

            ResolveSource(e, ref s, false, out bool gone, out _);

            if (gone)
            {
                e.Destroy();

                return;
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
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            (int sX, int sY, int sZ) = ResolveSource(e, ref s, false, out bool sourceGone, out _);

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

            (ushort tx, ushort ty, sbyte tz) = ResolveTarget(ref s, out bool targetGone);

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

            float angle = s.Angle, ox = s.OffsetX, oy = s.OffsetY;
            ushort x = s.X, y = s.Y;
            sbyte z = s.Z;

            // the object, in UpdateOffset's write order
            e.MarkPositionChanged();
            e.AngleToTarget = angle;

            if (moved)
            {
                e.SetInWorldTile(x, y, z);
            }

            e.OffsetX = ox;
            e.OffsetY = oy;

            return true;
        }
    }
}
