// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;
using Friflo.Engine.ECS;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Position = ClassicUO.Ecs.Position;

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
    internal static partial class EffectSystem
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ECS_EFFECTS=1|0) and the checker
        public static readonly bool GlobalOn = Environment.GetEnvironmentVariable("CUO_ECS_EFFECTS") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings?.EcsEffects ?? true
        };

        public static readonly bool GlobalCheck = Environment.GetEnvironmentVariable("CUO_EFFECT_CHECK") == "1";

        // CLEANUP(release): the gate's override of ecs_effect_endpoints (CUO_ECS_EFFECT_ENDPOINTS=1|0) and its checker
        public static readonly bool GlobalEndpoints = Environment.GetEnvironmentVariable("CUO_ECS_EFFECT_ENDPOINTS") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings?.EcsEffectEndpoints ?? true
        };

        public static readonly bool GlobalEndpointCheck = Environment.GetEnvironmentVariable("CUO_EFFECT_ENDPOINT_CHECK") == "1";

        // CLEANUP(release): the gate's override of ecs_effect_motion (CUO_ECS_EFFECT_MOTION=1|0) and its checker
        public static readonly bool GlobalMotion = Environment.GetEnvironmentVariable("CUO_ECS_EFFECT_MOTION") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings?.EcsEffectMotion ?? true
        };

        public static readonly bool GlobalMotionCheck = Environment.GetEnvironmentVariable("CUO_EFFECT_MOTION_CHECK") == "1";
#else
        public static readonly bool GlobalOn = Configuration.Settings.GlobalSettings?.EcsEffects ?? true;
        public const bool GlobalCheck = false;
        public static readonly bool GlobalEndpoints = Configuration.Settings.GlobalSettings?.EcsEffectEndpoints ?? true;
        public const bool GlobalEndpointCheck = false;
        public static readonly bool GlobalMotion = Configuration.Settings.GlobalSettings?.EcsEffectMotion ?? true;
        public const bool GlobalMotionCheck = false;
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

            if (e.World.EffectEndpointsOn)
            {
                BindEndpoints(e, ref s);
                e.UsesMotion = e.World.EffectMotionOn;
                e.MotionCheck = e.UsesMotion && e.World.EffectMotionCheckOn;
            }

            if (e is DragEffect drag)
            {
                s.MoveTime = drag.LastMoveTime;
            }

            e.Publish(ref s);
            e.EcsEntity = e.World.Ecs.Mirror.Store.CreateEntity(s);
            DropObjects(e);

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
                e.Publish(ref EcsRead.Get<EffectState>(e.EcsEntity));
            }
        }

        /// <summary>The base update's frame and lifetime step on the state (GameEffect.Update, after the source and destroyed
        /// tests). Destroys the effect when its duration ran out.</summary>
        public static void Animate(GameEffect e)
        {
            EffectShadow sh = e.Shadow;
            bool shadowDestroy = sh != null && sh.StepBase(e);

            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
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

            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
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

        // ---- T8 ownership (ecs_effect_endpoints): source and target as references in the state

        /// <summary>Endpoint checker counts (all worlds): reads compared, mismatches; endpoints that could not be bound to a
        /// record (an object without one: it is treated as a ground point), records removed without the hook.</summary>
        public static long EndpointReads, EndpointMismatches, EndpointsUnbound, EndpointsUnhooked;

        private static uint s_nextEndpointLog;

        /// <summary>
        /// Register: the constructor's source and target objects become references to their mirror records (T4's checked
        /// lookup: World holds the instance and the record is of its kind). An object without a record is treated as a ground
        /// point at its current position. The objects are kept only for the checker.
        /// </summary>
        private static void BindEndpoints(GameEffect e, ref EffectState s)
        {
            s.Ends = 0;

            if (e.Source is GameObjects.Entity src && src.TryScalarEntity(out EcsEntity se))
            {
                ref readonly Position p = ref EcsRead.Get<Position>(se);
                s.Ends |= (byte)(EffectState.END_SOURCE | (src is Mobile ? EffectState.END_SOURCE_MOBILE : 0));
                s.SourceEntity = se;
                s.SourceSerial = src.Serial;
                s.SourceX = p.X;
                s.SourceY = p.Y;
                s.SourceZ = p.Z;
            }
            else if (e.Source != null)
            {
                // a ground point on both of the checker's paths too (counted, not compared): SetSource(GameObject) already put
                // the effect at the object's position
                e.Source = null;
                EndpointsUnbound++;
            }

            if (e.Target is GameObjects.Entity trg && trg.TryScalarEntity(out EcsEntity te))
            {
                ref readonly Position p = ref EcsRead.Get<Position>(te);
                s.Ends |= (byte)(EffectState.END_TARGET | (trg is Mobile ? EffectState.END_TARGET_MOBILE : 0));
                s.TargetEntity = te;
                s.TargetSerial = trg.Serial;
                s.TargetX = p.X;
                s.TargetY = p.Y;
                s.TargetZ = p.Z;
            }
            else if (e.Target != null)
            {
                // Codex audit (ecs-effects-own) P2: the target becomes a ground point at the object's position; without this
                // the effect's own target coordinates (0,0,0 when a target object was given) would be read once the object is
                // dropped. The object goes now, so the checker's two paths agree on the ground point (counted, not compared).
                e.TargetX = e.Target.X;
                e.TargetY = e.Target.Y;
                e.TargetZ = e.Target.Z;
                e.Target = null;
                EndpointsUnbound++;
            }

            e.UsesEndpoints = true;
            e.EndpointCheck = e.World.EffectEndpointsCheckOn;
        }

        /// <summary>After Register created the entity: without the checkers the effect no longer holds its endpoint objects (the
        /// motion checker's legacy replay reads them too).</summary>
        private static void DropObjects(GameEffect e)
        {
            if (e.UsesEndpoints && !e.EndpointCheck && !e.MotionCheck)
            {
                e.Source = null;
                e.Target = null;
            }
        }

        private static bool Same(EcsEntity a, EcsEntity b) => a.Id == b.Id && a.Revision == b.Revision;

        // false when the endpoint is not an object or is gone; a removal the hooks did not see (Mirror.Clear, a record
        // replaced by one of the other kind) marks it gone here, keeping the last resolved position and letting go of the
        // record.
        // Codex audit (ecs-effects-own) P3: Friflo's handle revision is 16 bits, so after an unseen removal the id reused
        // 65,536 times makes the stale handle valid for whatever record sits there now. The record must also carry the
        // endpoint's serial and kind (one component read on the row already fetched). What is left: a record of the same
        // serial and kind on exactly that id and revision, i.e. 65,536 deletes of one id between two updates of the effect
        // (sources and moving targets are read every update), with the serial reused by the same kind meanwhile. Not
        // reachable from a server that is not hostile; a per-record generation token would close it but widens every mirror
        // record (Serial, streamed by the per-frame sweep, clock and extract) for this alone.
        // Codex re-review P3 (accepted, same bound): OnRecordRemoving/OnRecordRekeyed match by handle only, so a wrapped
        // handle that is removed or rekeyed before the next read of the effect moves the endpoint to the other record
        // without this serial/kind check.
        private static bool Live(ref EffectState s, byte end, byte gone, EcsEntity entity) => Live(ref s, end, gone, entity, out _);

        // the motion step reads the position from the row the test fetched (one node lookup per endpoint)
        private static bool Live(ref EffectState s, byte end, byte gone, EcsEntity entity, out EcsRow row)
        {
            row = default;

            if ((s.Ends & end) == 0 || (s.Ends & gone) != 0)
            {
                return false;
            }

            bool source = end == EffectState.END_SOURCE;
            row = EcsRead.Row(entity);

            if (row.IsNull || !row.TryGet(out ClassicUO.Ecs.Serial serial) || serial.Value != (source ? s.SourceSerial : s.TargetSerial) ||
                row.Tags.Has<MobileTag>() != ((s.Ends & (source ? EffectState.END_SOURCE_MOBILE : EffectState.END_TARGET_MOBILE)) != 0))
            {
                EndpointsUnhooked++;
                s.Ends |= gone;

                if (source)
                {
                    s.SourceEntity = default;
                }
                else
                {
                    s.TargetEntity = default;
                }

                return false;
            }

            return true;
        }

        /// <summary>The source's position: its record's while it lives, the last resolved one once it is gone, the effect's own
        /// for a ground point.</summary>
        public static (ushort x, ushort y, sbyte z) SourceOf(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            (ushort x, ushort y, sbyte z) r;

            if ((s.Ends & EffectState.END_SOURCE) == 0)
            {
                r = (e.X, e.Y, e.Z);
            }
            else
            {
                if (Live(ref s, EffectState.END_SOURCE, EffectState.END_SOURCE_GONE, s.SourceEntity))
                {
                    ref readonly Position p = ref EcsRead.Get<Position>(s.SourceEntity);
                    s.SourceX = p.X;
                    s.SourceY = p.Y;
                    s.SourceZ = p.Z;
                }

                r = (s.SourceX, s.SourceY, s.SourceZ);
            }

            if (e.EndpointCheck)
            {
                CompareEndpoint(e, "source", r, e.LegacySource());
            }

            return r;
        }

        /// <summary>The target's position: as <see cref="SourceOf"/>; a ground target is the effect's target coordinates.</summary>
        public static (ushort x, ushort y, sbyte z) TargetOf(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            (ushort x, ushort y, sbyte z) r;

            if ((s.Ends & EffectState.END_TARGET) == 0)
            {
                r = (e.TargetX, e.TargetY, e.TargetZ);
            }
            else
            {
                if (Live(ref s, EffectState.END_TARGET, EffectState.END_TARGET_GONE, s.TargetEntity))
                {
                    ref readonly Position p = ref EcsRead.Get<Position>(s.TargetEntity);
                    s.TargetX = p.X;
                    s.TargetY = p.Y;
                    s.TargetZ = p.Z;
                }

                r = (s.TargetX, s.TargetY, s.TargetZ);
            }

            if (e.EndpointCheck)
            {
                CompareEndpoint(e, "target", r, e.LegacyTarget());
            }

            return r;
        }

        /// <summary>The source is an object whose record is gone (the legacy <c>Source.IsDestroyed</c>).</summary>
        public static bool SourceGone(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            bool gone = (s.Ends & EffectState.END_SOURCE) != 0 && !Live(ref s, EffectState.END_SOURCE, EffectState.END_SOURCE_GONE, s.SourceEntity);

            if (e.EndpointCheck)
            {
                CompareFlag(e, "source destroyed", gone, e.Source != null && e.Source.IsDestroyed);
            }

            return gone;
        }

        /// <summary>The target is an object whose record is gone (the legacy <c>Target.IsDestroyed</c>).</summary>
        public static bool TargetGone(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            bool gone = (s.Ends & EffectState.END_TARGET) != 0 && !Live(ref s, EffectState.END_TARGET, EffectState.END_TARGET_GONE, s.TargetEntity);

            if (e.EndpointCheck)
            {
                CompareFlag(e, "target destroyed", gone, e.Target != null && e.Target.IsDestroyed);
            }

            return gone;
        }

        /// <summary>The target is an object, live or gone (the legacy <c>Target != null</c>).</summary>
        public static bool HasTarget(GameEffect e)
        {
            bool has = (EcsRead.Get<EffectState>(e.EcsEntity).Ends & EffectState.END_TARGET) != 0;

            if (e.EndpointCheck)
            {
                CompareFlag(e, "has target", has, e.Target != null);
            }

            return has;
        }

        /// <summary>
        /// The source object's offset (FixedEffect follows it). A mobile's comes from its <see cref="Steps"/> while the step
        /// system keeps them; an item's (a boat passenger's) offset is not ECS state yet, so it is read from the object World
        /// holds under the serial. False for a ground source.
        /// </summary>
        public static bool SourceOffsetOf(GameEffect e, out Microsoft.Xna.Framework.Vector3 offset)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            bool has = (s.Ends & EffectState.END_SOURCE) != 0;
            offset = default;

            if (has)
            {
                if ((s.Ends & EffectState.END_SOURCE_MOBILE) != 0 && e.World.Ecs.StepsEnabled &&
                    Live(ref s, EffectState.END_SOURCE, EffectState.END_SOURCE_GONE, s.SourceEntity) &&
                    EcsRead.TryGet(s.SourceEntity, out Steps st))
                {
                    offset = new Microsoft.Xna.Framework.Vector3(st.OffsetX, st.OffsetY, st.OffsetZ);
                }
                else
                {
                    offset = SourceObjectOf(e)?.Offset ?? default;
                }
            }

            if (e.EndpointCheck)
            {
                CompareFlag(e, "source offset", has, e.Source != null);

                if (e.Source != null && !e.Source.IsDestroyed && offset != e.Source.Offset)
                {
                    EndpointMismatch($"effect 0x{e.Graphic:X4} source offset ECS {offset} | object {e.Source.Offset}");
                }
            }

            return has;
        }

        /// <summary>The object World holds for the live source (views: depth and light; the client-side target cursor); null
        /// for a ground source or a gone one.</summary>
        public static GameObject SourceObjectOf(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            GameObject obj = null;

            if (Live(ref s, EffectState.END_SOURCE, EffectState.END_SOURCE_GONE, s.SourceEntity))
            {
                obj = (s.Ends & EffectState.END_SOURCE_MOBILE) != 0 ? e.World.Mobiles.Get(s.SourceSerial) : e.World.Items.Get(s.SourceSerial);
            }

            if (e.EndpointCheck)
            {
                EndpointReads++;
                GameObject legacy = e.Source != null && !e.Source.IsDestroyed ? e.Source : null;

                if (!ReferenceEquals(obj, legacy))
                {
                    EndpointMismatch($"effect 0x{e.Graphic:X4} source object ECS 0x{s.SourceSerial:X8} {(obj == null ? "none" : obj.GetType().Name)} | object {(legacy == null ? "none" : legacy.GetType().Name)}");
                }
            }

            return obj;
        }

        /// <summary>SetSource(x, y, z) after Register (a moving effect's source becomes its own position).</summary>
        public static void DropSource(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            s.Ends &= unchecked((byte)~(EffectState.END_SOURCE | EffectState.END_SOURCE_GONE | EffectState.END_SOURCE_MOBILE));
            s.SourceEntity = default;
            s.SourceSerial = 0;
        }

        /// <summary>SetTarget(x, y, z) after Register.</summary>
        public static void DropTarget(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            s.Ends &= unchecked((byte)~(EffectState.END_TARGET | EffectState.END_TARGET_GONE | EffectState.END_TARGET_MOBILE));
            s.TargetEntity = default;
            s.TargetSerial = 0;
        }

        /// <summary>
        /// WorldEcs, before a record is removed (an object destroyed, the old record of the death re-key): every endpoint on it
        /// is gone and keeps the record's position (the destroyed object kept its coordinates). A walk of the live effects.
        /// Outside the re-key (<paramref name="rekey"/> false) a gone endpoint lets go of the record: the record may live on as
        /// another object's (a displaced dying mobile's record is written for the next one), and only
        /// <see cref="OnRecordRekeyed"/> matches gone endpoints by their record.
        /// </summary>
        public static void OnRecordRemoving(World world, EcsEntity record, bool rekey = false)
        {
            for (GameEffect f = (GameEffect)world.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                if (!f.UsesEndpoints || f.EcsEntity.IsNull)
                {
                    continue;
                }

                ref EffectState s = ref EcsRead.Get<EffectState>(f.EcsEntity);

                if ((s.Ends & (EffectState.END_SOURCE | EffectState.END_SOURCE_GONE)) == EffectState.END_SOURCE && Same(s.SourceEntity, record))
                {
                    ref readonly Position p = ref EcsRead.Get<Position>(record);
                    s.Ends |= EffectState.END_SOURCE_GONE;
                    s.SourceX = p.X;
                    s.SourceY = p.Y;
                    s.SourceZ = p.Z;

                    if (!rekey)
                    {
                        s.SourceEntity = default;
                    }
                }

                if ((s.Ends & (EffectState.END_TARGET | EffectState.END_TARGET_GONE)) == EffectState.END_TARGET && Same(s.TargetEntity, record))
                {
                    ref readonly Position p = ref EcsRead.Get<Position>(record);
                    s.Ends |= EffectState.END_TARGET_GONE;
                    s.TargetX = p.X;
                    s.TargetY = p.Y;
                    s.TargetZ = p.Z;

                    if (!rekey)
                    {
                        s.TargetEntity = default;
                    }
                }
            }
        }

        /// <summary>
        /// WorldEcs.CommitRekey, after the dying mobile's record was written under <paramref name="serial"/>: the endpoints
        /// <see cref="OnRecordRemoving"/> marked gone on the old record follow the mobile to the new one (the object was
        /// never destroyed, so the old reference kept following it). With no new record (a destroyed mobile) they stay gone
        /// and let go of the old one: a gone endpoint holds a record only between these two calls, so no later re-key can
        /// match a stale handle (Codex audit P3: a 16-bit revision can wrap onto a live record).
        /// </summary>
        public static void OnRecordRekeyed(World world, EcsEntity old, EcsEntity record, uint serial)
        {
            for (GameEffect f = (GameEffect)world.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                if (!f.UsesEndpoints || f.EcsEntity.IsNull)
                {
                    continue;
                }

                ref EffectState s = ref EcsRead.Get<EffectState>(f.EcsEntity);

                if ((s.Ends & EffectState.END_SOURCE_GONE) != 0 && Same(s.SourceEntity, old))
                {
                    s.SourceEntity = record;

                    if (!record.IsNull)
                    {
                        s.Ends &= unchecked((byte)~EffectState.END_SOURCE_GONE);
                        s.SourceSerial = serial;
                    }
                }

                if ((s.Ends & EffectState.END_TARGET_GONE) != 0 && Same(s.TargetEntity, old))
                {
                    s.TargetEntity = record;

                    if (!record.IsNull)
                    {
                        s.Ends &= unchecked((byte)~EffectState.END_TARGET_GONE);
                        s.TargetSerial = serial;
                    }
                }
            }
        }

        private static void CompareEndpoint(GameEffect e, string what, (ushort x, ushort y, sbyte z) ecs, (ushort x, ushort y, sbyte z) legacy)
        {
            EndpointReads++;

            if (ecs != legacy)
            {
                EndpointMismatch($"effect 0x{e.Graphic:X4} {what} ECS ({ecs.x},{ecs.y},{ecs.z}) | object ({legacy.x},{legacy.y},{legacy.z})");
            }
        }

        private static void CompareFlag(GameEffect e, string what, bool ecs, bool legacy)
        {
            EndpointReads++;

            if (ecs != legacy)
            {
                EndpointMismatch($"effect 0x{e.Graphic:X4} {what}: ECS {ecs} | object {legacy}");
            }
        }

        /// <summary>CUO_EFFECT_ENDPOINT_CHECK, end of the effects stage: the serials equal the objects', and the 5 s summary.</summary>
        public static void CheckEndpoints(EffectManager manager)
        {
            for (GameEffect e = (GameEffect)manager.Items; e != null; e = (GameEffect)e.Next)
            {
                if (e.IsDestroyed || !e.UsesEndpoints || !e.EndpointCheck || e.EcsEntity.IsNull)
                {
                    continue;
                }

                ref readonly EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
                uint src = (e.Source as GameObjects.Entity)?.Serial ?? 0;
                uint trg = (e.Target as GameObjects.Entity)?.Serial ?? 0;
                EndpointReads++;

                if (s.SourceSerial != src || s.TargetSerial != trg)
                {
                    EndpointMismatch($"effect 0x{e.Graphic:X4} serials ECS 0x{s.SourceSerial:X8} -> 0x{s.TargetSerial:X8} | object 0x{src:X8} -> 0x{trg:X8}");
                }
            }

            if (Time.Ticks >= s_nextEndpointLog)
            {
                s_nextEndpointLog = Time.Ticks + 5000;
                Log.Info($"[effect-endpoint-check] {EndpointReads} reads compared, {EndpointMismatches} mismatches, {EndpointsUnbound} unbound, {EndpointsUnhooked} unhooked removals");
            }
        }

        internal static void EndpointMismatch(string text)
        {
            EndpointMismatches++;
            Observer?.Invoke(text);

            if (EndpointMismatches <= 10)
            {
                Log.Warn($"[effect-endpoint-check] mismatch: {text}");
            }
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

                ref readonly EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);

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
