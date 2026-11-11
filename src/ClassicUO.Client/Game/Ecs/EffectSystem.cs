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
    /// T8: graphic effects as ECS entities. Every effect created through
    /// <c>EffectManager</c> gets an entity with an <see cref="EffectState"/> (no serial, not in the mirror's serial index). The
    /// state owns the animation and lifetime (frame, next change time, duration), which <see cref="Animate"/> and
    /// <see cref="AnimateLightning"/> advance with the legacy rules, and the offset and angle (T9 S7 deleted the object's copies
    /// of the frame, offset and angle: the views read the state). The source and target are references to their
    /// mirror records in the state (T8 ownership), and the kinds' motion is the motion step over the state
    /// (EffectSystem.Motion.cs); T9 deleted the object path of <c>ecs_effect_endpoints</c> and <c>ecs_effect_motion</c>. The
    /// visiting order is <c>EffectManager.Update</c>'s list order and no random number is drawn. Tile placement is the
    /// object's own (<c>SetInWorldTile</c>); the objects are kept in <see cref="DynamicTileIndex"/>.
    /// </summary>
    internal static partial class EffectSystem
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the checker
        public static readonly bool GlobalCheck = Environment.GetEnvironmentVariable("CUO_EFFECT_CHECK") == "1";
#else
        public const bool GlobalCheck = false;
#endif

        /// <summary>Checker counts (all worlds): frames compared, effects compared, mismatches.</summary>
        public static long Frames, Compared, Mismatches;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal static Action<string> Observer;

        private static uint s_nextLog;

        /// <summary>The animation graphic before the first frame step (the views draw nothing).</summary>
        internal const ushort NO_FRAME = 0xFFFF;

        /// <summary>The effect's offset from its state (zero once destroyed or before it registers).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static Microsoft.Xna.Framework.Vector3 OffsetOf(GameEffect e)
        {
            if (e.EcsEntity.IsNull)
            {
                return default;
            }

            ref readonly EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);

            return new Microsoft.Xna.Framework.Vector3(s.OffsetX, s.OffsetY, s.OffsetZ);
        }

        /// <summary>T9 S9: the effect's graphic and hue from its state (0 before it registers and once destroyed, as the
        /// destroyed object's were).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static ushort GraphicOf(GameEffect e) => e.EcsEntity.IsNull ? (ushort)0 : EcsRead.Get<EffectState>(e.EcsEntity).Graphic;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static ushort HueOf(GameEffect e) => e.EcsEntity.IsNull ? (ushort)0 : EcsRead.Get<EffectState>(e.EcsEntity).Hue;

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
            s.Graphic = e.CreatedGraphic;
            s.Hue = e.CreatedHue;
            s.Blend = (byte)e.Blend;
            s.AnimGraphic = NO_FRAME;
            s.Interval = e.IntervalInMs;
            s.Duration = e.Duration;
            s.Flags = (byte)((e.IsEnabled ? EffectState.FLAG_ENABLED : 0) | (e.CanCreateExplosionEffect ? EffectState.FLAG_EXPLODES : 0) |
                             (e is MovingEffect m && m.FixedDir ? EffectState.FLAG_FIXED_DIR : 0));

            BindEndpoints(e, ref s);
            e.Publish(ref s);
            e.EcsEntity = e.World.Ecs.Mirror.Store.CreateEntity(s);

            // the state references the endpoints from now on
            e.Source = null;
            e.Target = null;
        }

        public static void Unregister(GameEffect e)
        {
            if (!e.EcsEntity.IsNull)
            {
                e.EcsEntity.DeleteEntity();
                e.EcsEntity = default;
            }
        }

        /// <summary>The base update's frame and lifetime step on the state (GameEffect.Update, after the source and destroyed
        /// tests). Destroys the effect when its duration ran out.</summary>
        public static void Animate(GameEffect e)
        {
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

            if (destroyed)
            {
                e.Destroy();
            }
        }

        /// <summary>LightningEffect's frame step on the state. False when the effect was destroyed.</summary>
        public static bool AnimateLightning(GameEffect e)
        {
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

            if (destroyed)
            {
                e.Destroy();

                return false;
            }

            return true;
        }

        // ---- T8 ownership: source and target as references in the state

        /// <summary>Endpoint counts (all worlds): endpoints that could not be bound to a record (an object without one: it is
        /// treated as a ground point), records removed without the hook.</summary>
        public static long EndpointsUnbound, EndpointsUnhooked;

        /// <summary>
        /// Register: the constructor's source and target objects become references to their mirror records (T4's checked
        /// lookup: World holds the instance and the record is of its kind). An object without a record is treated as a ground
        /// point at its current position. Register drops the objects once the entity exists.
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
                // a ground point (counted): SetSource(GameObject) already put the effect at the object's position
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
                // dropped (counted).
                e.TargetX = e.Target.X;
                e.TargetY = e.Target.Y;
                e.TargetZ = e.Target.Z;
                e.Target = null;
                EndpointsUnbound++;
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

        /// <summary>The target is an object, live or gone (the legacy <c>Target != null</c>).</summary>
        public static bool HasTarget(GameEffect e) => (EcsRead.Get<EffectState>(e.EcsEntity).Ends & EffectState.END_TARGET) != 0;

        /// <summary>The object World holds for the live source (views: depth and light; the client-side target cursor); null
        /// for a ground source or a gone one.</summary>
        public static GameObject SourceObjectOf(GameEffect e)
        {
            ref EffectState s = ref EcsRead.Get<EffectState>(e.EcsEntity);
            GameObject obj = null;

            if (Live(ref s, EffectState.END_SOURCE, EffectState.END_SOURCE_GONE, s.SourceEntity))
            {
                obj = (s.Ends & EffectState.END_SOURCE_MOBILE) != 0 ? e.World.FindMobile(s.SourceSerial) : e.World.FindItem(s.SourceSerial);
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
                if (f.EcsEntity.IsNull)
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
                if (f.EcsEntity.IsNull)
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

        /// <summary>
        /// CUO_EFFECT_CHECK, end of the effects stage: each live effect has an entity whose position equals the object's (the
        /// object keeps it for its tile placement; T9 S7 deleted the frame, offset and angle copies and their comparison), the
        /// object's tile is the chunk and cell of its position, and the entities are the list's effects.
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
                Log.Info($"[effect-check] {Frames} frames, {Compared} effects compared, {Mismatches} mismatches");
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
}
