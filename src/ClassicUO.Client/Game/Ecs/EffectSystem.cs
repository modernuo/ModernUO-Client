// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;
using Friflo.Engine.ECS;

namespace ClassicUO.Game
{
    /// <summary>
    /// T8 (<c>ecs_effects</c>): graphic effects as ECS entities. With the switch on every effect created through
    /// <c>EffectManager</c> gets an entity with an <see cref="EffectState"/> (no serial, not in the mirror's serial index). The
    /// state owns the animation and lifetime (frame, next change time, duration), which <see cref="Animate"/> and
    /// <see cref="AnimateLightning"/> advance with the legacy rules (the object's <c>AnimIndex</c>, <c>AnimationGraphic</c> and
    /// <c>NextChangeFrameTime</c> become write-through caches for the views); position, offset, angle and target are published
    /// to it after each update. The visiting order is <c>EffectManager.Update</c>'s list order and no random number is drawn.
    /// The motion rules of the kinds (moving's path, fixed's source following, drag's offset) stay on the objects: they read
    /// source and target objects, which the ECS does not own yet. Tile placement is the object's own
    /// (<c>SetInWorldTile</c>); with the switch on the objects are kept in <see cref="DynamicTileIndex"/> too.
    /// </summary>
    internal static class EffectSystem
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ECS_EFFECTS=1|0) and the checker
        public static readonly bool GlobalOn = Environment.GetEnvironmentVariable("CUO_ECS_EFFECTS") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings?.EcsEffects ?? false
        };

        public static readonly bool GlobalCheck = Environment.GetEnvironmentVariable("CUO_EFFECT_CHECK") == "1";
#else
        public static readonly bool GlobalOn = Configuration.Settings.GlobalSettings?.EcsEffects ?? false;
        public const bool GlobalCheck = false;
#endif

        /// <summary>Checker counts (all worlds): frames compared, effects compared, mismatches, shadow animation steps.</summary>
        public static long Frames, Compared, Mismatches, ShadowSteps;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal static Action<string> Observer;

        private static uint s_nextLog;

        public static byte KindOf(GameEffect e) => e switch
        {
            MovingEffect => EffectState.KIND_MOVING,
            DragEffect => EffectState.KIND_DRAG,
            LightningEffect => EffectState.KIND_LIGHTNING,
            _ => EffectState.KIND_FIXED
        };

        /// <summary>Creates the effect's entity from its fields (called when it joins the manager's list, once its constructor,
        /// blend and explosion flag are set).</summary>
        public static void Register(GameEffect e)
        {
            EffectState s = default;
            s.Kind = KindOf(e);
            s.Graphic = e.Graphic;
            s.AnimGraphic = e.AnimationGraphic;
            s.Hue = e.Hue;
            s.Blend = (byte)e.Blend;
            s.AnimIndex = e.AnimIndex;
            s.Interval = e.IntervalInMs;
            s.Duration = e.Duration;
            s.NextChange = e.NextChangeFrameTime;
            s.Flags = (byte)((e.IsEnabled ? EffectState.FLAG_ENABLED : 0) | (e.CanCreateExplosionEffect ? EffectState.FLAG_EXPLODES : 0) |
                             (e is MovingEffect m && m.FixedDir ? EffectState.FLAG_FIXED_DIR : 0));
            e.Publish(ref s);
            e.EcsEntity = e.World.Ecs.Mirror.Store.CreateEntity(s);

            if (e.World.EffectsCheckOn)
            {
                e.Shadow = new EffectShadow { AnimIndex = s.AnimIndex, AnimGraphic = s.AnimGraphic, Next = s.NextChange };
            }
        }

        public static void Unregister(GameEffect e)
        {
            if (!e.EcsEntity.IsNull)
            {
                e.EcsEntity.DeleteEntity();
                e.EcsEntity = default;
            }
        }

        /// <summary>Copies the object's position, offset, angle and target to its state (after its update).</summary>
        public static void Publish(GameEffect e)
        {
            if (!e.EcsEntity.IsNull)
            {
                e.Publish(ref e.EcsEntity.GetComponent<EffectState>());
            }
        }

        /// <summary>The base update's frame and lifetime step on the state (GameEffect.Update, after the source and destroyed
        /// tests). Destroys the effect when its duration ran out.</summary>
        public static void Animate(GameEffect e)
        {
            EffectShadow sh = e.Shadow;
            bool shadowDestroy = sh != null && sh.StepBase(e);

            ref EffectState s = ref e.EcsEntity.GetComponent<EffectState>();
            long now = Time.Ticks;
            bool destroyed = false;

            if ((s.Flags & EffectState.FLAG_ENABLED) != 0)
            {
                if (s.Duration < now && s.Duration >= 0)
                {
                    destroyed = true;
                }
                else if (s.NextChange < now)
                {
                    if (e.AnimDataFrame.FrameCount != 0)
                    {
                        unsafe
                        {
                            s.AnimGraphic = (ushort)(s.Graphic + e.AnimDataFrame.FrameData[s.AnimIndex]);
                        }

                        s.AnimIndex++;

                        if (s.AnimIndex >= e.AnimDataFrame.FrameCount)
                        {
                            s.AnimIndex = 0;
                        }
                    }
                    else
                    {
                        s.AnimGraphic = s.Graphic;
                    }

                    s.NextChange = now + s.Interval;
                }
            }
            else
            {
                s.AnimGraphic = s.Graphic;
            }

            if (sh != null)
            {
                sh.Compare(e, destroyed, shadowDestroy, in s);
            }

            if (destroyed)
            {
                e.Destroy();

                return;
            }

            e.AnimationGraphic = s.AnimGraphic;
            e.AnimIndex = s.AnimIndex;
            e.NextChangeFrameTime = s.NextChange;
        }

        /// <summary>LightningEffect's frame step on the state. False when the effect was destroyed.</summary>
        public static bool AnimateLightning(GameEffect e)
        {
            EffectShadow sh = e.Shadow;
            bool shadowDestroy = sh != null && sh.StepLightning(e);

            ref EffectState s = ref e.EcsEntity.GetComponent<EffectState>();
            long now = Time.Ticks;
            bool destroyed = s.AnimIndex >= 10 || (s.Duration < now && s.Duration >= 0);

            if (!destroyed)
            {
                s.AnimGraphic = (ushort)(s.Graphic + s.AnimIndex);

                if (s.NextChange < now)
                {
                    s.AnimIndex++;
                    s.NextChange = now + s.Interval;
                }
            }

            if (sh != null)
            {
                sh.Compare(e, destroyed, shadowDestroy, in s);
            }

            if (destroyed)
            {
                e.Destroy();

                return false;
            }

            e.AnimationGraphic = s.AnimGraphic;
            e.AnimIndex = s.AnimIndex;
            e.NextChangeFrameTime = s.NextChange;

            return true;
        }

        /// <summary>
        /// CUO_EFFECT_CHECK, end of the effects stage: each live effect has an entity whose state equals the object (position,
        /// offset, angle, frame, graphic, duration), the object's tile is the chunk and cell of its position, and the entities
        /// are the list's effects. (The animation step is compared with the legacy rules inside Animate.)
        /// </summary>
        public static void Check(World world, EffectManager manager)
        {
            Frames++;
            int listed = 0;

            for (GameEffect e = (GameEffect)manager.Items; e != null; e = (GameEffect)e.Next)
            {
                if (e.IsDestroyed)
                {
                    continue;
                }

                listed++;
                Compared++;

                if (e.EcsEntity.IsNull)
                {
                    Mismatch($"effect 0x{e.Graphic:X4} at ({e.X},{e.Y},{e.Z}) has no entity");

                    continue;
                }

                ref readonly EffectState s = ref e.EcsEntity.GetComponent<EffectState>();

                if (s.X != e.X || s.Y != e.Y || s.Z != e.Z)
                {
                    Mismatch($"effect 0x{e.Graphic:X4} position ECS ({s.X},{s.Y},{s.Z}) | object ({e.X},{e.Y},{e.Z})");
                }

                if (s.OffsetX != e.OffsetX || s.OffsetY != e.OffsetY || s.Angle != e.AngleToTarget)
                {
                    Mismatch($"effect 0x{e.Graphic:X4} offset ECS ({s.OffsetX},{s.OffsetY},{s.Angle}) | object ({e.OffsetX},{e.OffsetY},{e.AngleToTarget})");
                }

                if (s.AnimGraphic != e.AnimationGraphic || s.AnimIndex != e.AnimIndex || s.NextChange != e.NextChangeFrameTime ||
                    s.Graphic != e.Graphic || s.Duration != e.Duration || s.Blend != (byte)e.Blend)
                {
                    Mismatch($"effect 0x{e.Graphic:X4} frame ECS (g=0x{s.AnimGraphic:X4} i={s.AnimIndex} next={s.NextChange}) | object (g=0x{e.AnimationGraphic:X4} i={e.AnimIndex} next={e.NextChangeFrameTime})");
                }

                if (e.PendingBlock < 0 && e.TileChunk != null)
                {
                    Game.Map.Chunk chunk = world.Map?.PeekChunk(e.X, e.Y);

                    if (!ReferenceEquals(chunk, e.TileChunk) || e.TileCell != (byte)(((e.X & 7) << 3) | (e.Y & 7)))
                    {
                        Mismatch($"effect 0x{e.Graphic:X4} at ({e.X},{e.Y}) is on the wrong tile (cell {e.TileCell})");
                    }
                }
            }

            int entities = world.Ecs.Mirror.Store.Query<EffectState>().Count;

            if (entities != listed)
            {
                Mismatch($"{entities} effect entities | {listed} live effects in the list");
            }

            if (Time.Ticks >= s_nextLog)
            {
                s_nextLog = Time.Ticks + 5000;
                Log.Info($"[effect-check] {Frames} frames, {Compared} effects compared, {ShadowSteps} shadow steps, {Mismatches} mismatches");
            }
        }

        internal static void Mismatch(string text)
        {
            Mismatches++;
            Observer?.Invoke(text);

            if (Mismatches <= 10)
            {
                Log.Warn($"[effect-check] mismatch: {text}");
            }
        }
    }

    /// <summary>The legacy animation rules on a private copy of the three fields they write (CUO_EFFECT_CHECK).</summary>
    internal sealed class EffectShadow
    {
        public byte AnimIndex;
        public ushort AnimGraphic;
        public long Next;

        /// <summary>GameEffect.Update's block before T8, verbatim on the copy. True when it would destroy the effect.</summary>
        public bool StepBase(GameEffect e)
        {
            EffectSystem.ShadowSteps++;

            if (e.IsEnabled)
            {
                if (e.Duration < Time.Ticks && e.Duration >= 0)
                {
                    return true;
                }

                if (Next < Time.Ticks)
                {
                    if (e.AnimDataFrame.FrameCount != 0)
                    {
                        unsafe
                        {
                            AnimGraphic = (ushort)(e.Graphic + e.AnimDataFrame.FrameData[AnimIndex]);
                        }

                        AnimIndex++;

                        if (AnimIndex >= e.AnimDataFrame.FrameCount)
                        {
                            AnimIndex = 0;
                        }
                    }
                    else
                    {
                        AnimGraphic = e.Graphic;
                    }

                    Next = (long)Time.Ticks + e.IntervalInMs;
                }
            }
            else
            {
                AnimGraphic = e.Graphic;
            }

            return false;
        }

        /// <summary>LightningEffect.Update's frame block before T8, verbatim on the copy.</summary>
        public bool StepLightning(GameEffect e)
        {
            EffectSystem.ShadowSteps++;

            if (AnimIndex >= 10 || (e.Duration < Time.Ticks && e.Duration >= 0))
            {
                return true;
            }

            AnimGraphic = (ushort)(e.Graphic + AnimIndex);

            if (Next < Time.Ticks)
            {
                AnimIndex++;
                Next = (long)Time.Ticks + e.IntervalInMs;
            }

            return false;
        }

        public void Compare(GameEffect e, bool destroyed, bool shadowDestroyed, in EffectState s)
        {
            if (destroyed != shadowDestroyed)
            {
                EffectSystem.Mismatch($"effect 0x{e.Graphic:X4}: ECS {(destroyed ? "destroys" : "keeps")} it, legacy rules {(shadowDestroyed ? "destroy" : "keep")} it");

                return;
            }

            if (!destroyed && (s.AnimIndex != AnimIndex || s.AnimGraphic != AnimGraphic || s.NextChange != Next))
            {
                EffectSystem.Mismatch($"effect 0x{e.Graphic:X4} frame ECS (g=0x{s.AnimGraphic:X4} i={s.AnimIndex} next={s.NextChange}) | legacy (g=0x{AnimGraphic:X4} i={AnimIndex} next={Next})");
            }
        }
    }
}
