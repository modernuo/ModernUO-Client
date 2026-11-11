// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Resources;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;
using StepsComponent = ClassicUO.Ecs.Steps;

namespace ClassicUO.Game
{
    /// <summary>T6c: what <c>Mobile.ProcessSteps</c> reads for the front step (from the components, or from the Mobile for the
    /// checker's shadow).</summary>
    internal struct StepInput
    {
        public StepEntry Front;
        public int Count;
        public uint LastStepTime;
        public float OffsetX, OffsetY;
        public int X, Y;
        public sbyte Z;

        // IsMounted, the fast-unmount speed modes, IsFlying: the ECS's (StepSystem.MountedFromEcs; the Mobile's for the
        // checker's shadow or without the components)
        public bool Mounted;
        public bool IsPlayer;
    }

    /// <summary>T6c: <c>Mobile.ProcessSteps</c>' decision for the front step this frame.</summary>
    internal struct StepDecision
    {
        // the walk offset toward the front step is written (OffsetZ, then OffsetX and OffsetY)
        public bool Interpolate;
        public sbyte X, Y, Z;

        // the front step is done (CompleteStep); a turn in place when DirectionChange (ProcessSteps then goes on with the next)
        public bool Remove;
        public bool DirectionChange;

        public bool Equals(in StepDecision o) =>
            Interpolate == o.Interpolate && X == o.X && Y == o.Y && Z == o.Z && Remove == o.Remove && DirectionChange == o.DirectionChange;

        public override string ToString() =>
            $"interp {Interpolate} ({X},{Y},{Z}) remove {Remove} turn {DirectionChange}";
    }

    /// <summary>T6c: <c>Mobile.ProcessSteps</c>' timing and offset math, unchanged, over <see cref="StepInput"/>.</summary>
    internal static class StepRules
    {
        public static void Decide(in StepInput i, uint ticks, int frameDelay, out StepDecision d)
        {
            d = default;
            ref readonly StepEntry step = ref i.Front;

            int delay = (int)ticks - (int)i.LastStepTime;
            bool mounted = i.Mounted;
            bool run = step.Run;

            // Client auto movements sync (see ProcessSteps)
            if (!mounted && !i.IsPlayer && i.Count > 1 && delay > 0)
            {
                mounted = delay <= (run ? MovementSpeed.STEP_DELAY_MOUNT_RUN : MovementSpeed.STEP_DELAY_MOUNT_WALK);
            }

            int maxDelay = MovementSpeed.TimeToCompleteMovement(run, mounted) - frameDelay;

            bool removeStep = delay >= maxDelay;
            bool directionChange = false;

            if (i.X != step.X || i.Y != step.Y)
            {
                bool badStep = false;

                if (i.OffsetX == 0 && i.OffsetY == 0)
                {
                    int absX = Math.Abs(i.X - step.X);
                    int absY = Math.Abs(i.Y - step.Y);

                    badStep = absX > 1 || absY > 1 || absX + absY == 0;

                    if (!badStep)
                    {
                        absX = i.X;
                        absY = i.Y;

                        Pathfinder.GetNewXY((byte)(step.Direction & 7), ref absX, ref absY);

                        badStep = absX != step.X || absY != step.Y;
                    }
                }

                if (badStep)
                {
                    removeStep = true;
                }
                else
                {
                    float steps = maxDelay / (float)Constants.CHARACTER_ANIMATION_DELAY;
                    float x = delay / (float)Constants.CHARACTER_ANIMATION_DELAY;
                    float y = x;
                    sbyte offsetZ = (sbyte)((step.Z - i.Z) * x * (4.0f / steps));
                    MovementSpeed.GetPixelOffset(step.Direction, ref x, ref y, steps);

                    d.Interpolate = true;
                    d.X = (sbyte)x;
                    d.Y = (sbyte)y;
                    d.Z = offsetZ;
                }
            }
            else
            {
                directionChange = true;
                removeStep = true;
            }

            d.Remove = removeStep;
            d.DirectionChange = directionChange;
        }
    }

    /// <summary>
    /// T6c: <c>Mobile.ProcessSteps(evaluate: true)</c> as a system over the mobile's
    /// <see cref="StepsComponent"/> and its <see cref="Position"/>/<see cref="Facing"/> record. Runs per due mobile in the
    /// Mobiles stage, in table order, at the point <c>Mobile.Update</c> ran ProcessSteps, so footstep sounds, the frame
    /// advance and the random draws keep their order. The results go through the funnel: <c>WorldWriter.InterpolateStep</c>
    /// (the offset; the system writes the component's offset first), the player's walker bookkeeping (as before) and
    /// <c>WorldWriter.CompleteStep</c> (position, tile list and DynamicTileIndex via AddToTile, queue, idle timer, inputs;
    /// its commit rewrites the component through the clock push). The Mobile's queue, offset and LastStepTime stay the
    /// write-through cache the views read every frame.
    /// </summary>
    internal sealed class StepSystem
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_STEP_CHECK=1: before each run the component equals the Mobile's step state, the decision from
        // the component equals the decision from the Mobile's fields, and after it the component equals the Mobile again
        public static readonly bool StepCheck = Environment.GetEnvironmentVariable("CUO_STEP_CHECK") == "1";
#else
        public const bool StepCheck = false;
#endif

        public static long Ran, Declined, Interpolated, Completed, Turns;
        public static long Checks, SyncDiffs, DecisionDiffs, AfterDiffs;
        private static int _logged;

#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_STEP_MOUNT_CHECK=1: at every step decision the mounted pace from the ECS is compared with the
        // Mobile's (IsMounted, SpeedMode, IsFlying), which the views still read
        public static readonly bool StepMountCheck = Environment.GetEnvironmentVariable("CUO_STEP_MOUNT_CHECK") == "1";
#else
        public const bool StepMountCheck = false;
#endif

        public static long MountEcsReads, MountFallbacks, MountSlotRebuilds, MountChecks, MountDiffs;
        private static int _mountLogged;

        /// <summary>Tests: rebuilds a dirty mobile's <see cref="EquipmentSlots"/> in place of the client's tiledata
        /// (<c>MobileAnimationSystem.Rebuild</c>'s seam); null in the client.</summary>
        internal Action<EcsEntity, AnimationState, Mobile> RebuildSlotsOverride;

        /// <summary>Tests: <see cref="StepMountCheck"/> for this instance (null: the environment).</summary>
        internal bool? MountCheckOverride;

        private bool MountCheck => MountCheckOverride ?? StepMountCheck;

        /// <summary>Copies the Mobile's walk queue, <c>LastStepTime</c> and walk offset into <paramref name="s"/>.</summary>
        internal static void CopyFrom(Mobile m, ref StepsComponent s)
        {
            Mobile.StepQueue q = m.Steps;
            int n = Math.Min(q.Count, StepsComponent.CAPACITY);

            // the ring is rewritten from slot 0 (the copy is the queue's order; Head moves only in the system's own reads)
            for (int k = 0; k < n; k++)
            {
                Mobile.Step st = q[k];
                s.Ring[k] = new StepEntry { X = st.X, Y = st.Y, Z = st.Z, Direction = st.Direction, Run = st.Run };
            }

            for (int k = n; k < StepsComponent.CAPACITY; k++)
            {
                s.Ring[k] = default;
            }

            s.Head = 0;
            s.Count = (byte)n;
            s.LastStepTime = m.LastStepTime;
            s.OffsetX = m.Offset.X;
            s.OffsetY = m.Offset.Y;
            s.OffsetZ = m.Offset.Z;
        }

        internal static bool Mounted(Mobile m) =>
            m.IsMounted
            || m.SpeedMode == CharacterSpeedType.FastUnmount
            || m.SpeedMode == CharacterSpeedType.FastUnmountAndCantRun
            || m.IsFlying;

        /// <summary><see cref="Mounted(Mobile)"/> from the ECS: the slots' mount (IsMounted), the pushed speed mode and the
        /// pushed Flying bit (IsFlying).</summary>
        internal static bool MountedFromEcs(in MobileInputs inp, in EquipmentSlots eq) =>
            eq.Mounted
            || inp.SpeedMode == (byte)CharacterSpeedType.FastUnmount
            || inp.SpeedMode == (byte)CharacterSpeedType.FastUnmountAndCantRun
            || (inp.Flags & AnimStateFlags.Flying) != 0;

        // the slots the drawn path rebuilds lazily (as MobileAnimationAdvanceSystem.TryRun): a mobile not drawn since its
        // equipment changed is rebuilt here, before any component ref (removing the tag moves the entity: Run reads the row
        // again)
        private void FreshenSlots(World world, Mobile mob, EcsEntity e, in EcsRow row)
        {
            if (!row.Has<EquipmentSlots>() || !row.Has<MobileInputs>() || !MobileAnimationSystem.TakeDirty(e, in row))
            {
                return;
            }

            MountSlotRebuilds++;
            AnimationState a = default;
            a.Flags = EcsRead.Get<MobileInputs>(e).Flags;

            if (RebuildSlotsOverride != null)
            {
                RebuildSlotsOverride(e, a, mob);
            }
            else
            {
                world.Ecs.Animation.Rebuild(e, in a, mob);
            }
        }

        // the mounted pace the decision uses: from the ECS (the Mobile's when the components are missing; T9 deleted
        // ecs_step_mount_inputs' off path); CUO_STEP_MOUNT_CHECK compares it with the Mobile's
        private bool MountedInput(Mobile mob, EcsEntity e, in EcsRow row)
        {
            bool have = row.Has<MobileInputs>() && row.Has<EquipmentSlots>();
            bool ecs = have && MountedFromEcs(in row.Get<MobileInputs>(), in row.Get<EquipmentSlots>());

            if (MountCheck)
            {
                CheckMount(mob, e, have, ecs);
            }

            if (!have)
            {
                MountFallbacks++;

                return Mounted(mob);
            }

            MountEcsReads++;

            return ecs;
        }

        private static bool IsPlayer(World world, Mobile m) => world.Player != null && m.Serial == world.Player.Serial;

        /// <summary>
        /// Runs <paramref name="mob"/>'s step processing in the ECS: false (nothing changed) when it is not eligible (the
        /// system is off, the mobile is destroyed, not the one World holds, not mirrored, or without the components); the
        /// caller then runs <c>Mobile.ProcessSteps</c>. <paramref name="dir"/> is ProcessSteps' direction out.
        /// </summary>
        public bool TryRun(World world, Mobile mob, uint ticks, int frameDelay, out byte dir)
        {
            dir = 0;

            if (!world.Ecs.StepsEnabled || mob.IsDestroyed || !world.Holds(mob) ||
                !world.Ecs.Mirror.TryGetEntity(mob.Serial, out EcsEntity e))
            {
                Declined++;

                return false;
            }

            // one record read for the eligibility and the slot freshening (perf backlog: was up to 7 entity lookups)
            EcsRow row = EcsRead.Row(e);

            if (row.IsNull || !row.Has<StepsComponent>() || !row.Has<Position>() || !row.Has<Facing>())
            {
                Declined++;

                return false;
            }

            Ran++;

            FreshenSlots(world, mob, e, in row);

            Run(world, mob, e, ticks, frameDelay, out dir);

            return true;
        }

        private void Run(World world, Mobile mob, EcsEntity e, uint ticks, int frameDelay, out byte dir)
        {
            if (StepCheck)
            {
                CheckSync(mob, e, "before");
            }

            // read after FreshenSlots (its tag removal moves the entity)
            EcsRow row = EcsRead.Row(e);
            ref readonly StepsComponent s = ref row.Get<StepsComponent>();
            dir = (byte)(row.Get<Facing>().Value & 7);

            if (s.Count == 0)
            {
                return;
            }

            StepEntry front = s.Ring[s.Head];
            dir = front.Direction;

            if (front.Run)
            {
                dir &= 7;
            }

            // the step state before EndServerAnimation, whose commit may move the entity (the ref and the row go stale)
            StepInput inp;
            inp.Front = front;
            inp.Count = s.Count;
            inp.LastStepTime = s.LastStepTime;
            inp.OffsetX = s.OffsetX;
            inp.OffsetY = s.OffsetY;

            if (mob.AnimationFromServer)
            {
                // a step ends the server's animation through the funnel (as ProcessSteps)
                world.Writer.EndServerAnimation(mob);
                row = EcsRead.Row(e);
            }

            ref readonly Position p = ref row.Get<Position>();
            inp.X = p.X;
            inp.Y = p.Y;
            inp.Z = p.Z;
            inp.Mounted = MountedInput(mob, e, in row);
            inp.IsPlayer = IsPlayer(world, mob);

            StepRules.Decide(in inp, ticks, frameDelay, out StepDecision d);

            if (StepCheck)
            {
                CheckDecision(mob, in inp, in d, ticks, frameDelay);
            }

            if (d.Interpolate)
            {
                Interpolated++;
                ref StepsComponent sw = ref row.Get<StepsComponent>();
                sw.OffsetZ = d.Z;
                sw.OffsetX = d.X;
                sw.OffsetY = d.Y;
                world.Writer.InterpolateStep(mob, d.X, d.Y, d.Z);
            }

            if (d.Remove)
            {
                if (inp.IsPlayer)
                {
                    if (inp.Z - front.Z >= 22)
                    {
                        // oUCH!!!!
                        mob.AddMessage(MessageType.Label, ResGeneral.Ouch, TextType.CLIENT);
                    }

                    WalkerBookkeeping(world.Player.Walker);
                }

                Completed++;
                Mobile.Step done = new Mobile.Step { X = front.X, Y = front.Y, Z = front.Z, Direction = front.Direction, Run = front.Run };
                // its input commit (CommitMobileInputs, through the clock push) rewrites the component from the Mobile
                world.Writer.CompleteStep(mob, in done, d.DirectionChange);

                if (d.DirectionChange)
                {
                    Turns++;

                    // ProcessSteps goes on with the next step (its recursion); CompleteStep's commit rewrote the component
                    if (!TryRun(world, mob, ticks, frameDelay, out dir))
                    {
                        mob.ProcessSteps(out dir, true);
                    }

                    return;
                }
            }

            mob.UpdateTextCoordsV();

            if (StepCheck)
            {
                CheckSync(mob, e, "after");
            }
        }

        // the player's walker after a completed step (ProcessSteps' block, unchanged)
        internal static void WalkerBookkeeping(WalkerManager walker)
        {
            if (walker.StepInfos[walker.CurrentWalkSequence].Accepted)
            {
                int sequence = walker.CurrentWalkSequence + 1;

                if (sequence < walker.StepsCount)
                {
                    int count = walker.StepsCount - sequence;

                    for (int i = 0; i < count; i++)
                    {
                        walker.StepInfos[sequence - 1] = walker.StepInfos[sequence];

                        sequence++;
                    }
                }

                walker.StepsCount--;
            }
            else
            {
                walker.CurrentWalkSequence++;
            }
        }

        // ---- CUO_STEP_CHECK

        /// <summary>Differences between the component (and the record's position and facing) and the Mobile; null when equal.</summary>
        internal static string Diff(Mobile m, EcsEntity e)
        {
            ref readonly StepsComponent s = ref EcsRead.Get<StepsComponent>(e);
            Mobile.StepQueue q = m.Steps;

            if (s.Count != q.Count)
            {
                return $"count {s.Count} vs {q.Count}";
            }

            for (int k = 0; k < q.Count; k++)
            {
                StepEntry a = s.Ring[(s.Head + k) % StepsComponent.CAPACITY];
                Mobile.Step b = q[k];

                if (a.X != b.X || a.Y != b.Y || a.Z != b.Z || a.Direction != b.Direction || a.Run != b.Run)
                {
                    return $"step {k}: ({a.X},{a.Y},{a.Z},{a.Direction},{a.Run}) vs ({b.X},{b.Y},{b.Z},{b.Direction},{b.Run})";
                }
            }

            if (s.LastStepTime != m.LastStepTime)
            {
                return $"last step time {s.LastStepTime} vs {m.LastStepTime}";
            }

            if (s.OffsetX != m.Offset.X || s.OffsetY != m.Offset.Y || s.OffsetZ != m.Offset.Z)
            {
                return $"offset ({s.OffsetX},{s.OffsetY},{s.OffsetZ}) vs {m.Offset}";
            }

            Position p = EcsRead.Get<Position>(e);

            if (p.X != m.X || p.Y != m.Y || p.Z != m.Z)
            {
                return $"position ({p.X},{p.Y},{p.Z}) vs ({m.X},{m.Y},{m.Z})";
            }

            if ((EcsRead.Get<Facing>(e).Value & 7) != ((byte)m.Direction & 7))
            {
                return $"facing {e.GetComponent<Facing>().Value} vs {m.Direction}";
            }

            return null;
        }

        private static void CheckSync(Mobile m, EcsEntity e, string when)
        {
            Checks++;
            string diff = Diff(m, e);

            if (diff != null)
            {
                if (when == "before") SyncDiffs++; else AfterDiffs++;
                LogDiff($"0x{m.Serial:X8} {when} the run: {diff} (tick {Time.Ticks})");
            }
        }

        // the shadow: ProcessSteps' inputs read from the Mobile, as the legacy code reads them
        private static void CheckDecision(Mobile m, in StepInput fromEcs, in StepDecision d, uint ticks, int frameDelay)
        {
            if (m.Steps.Count == 0)
            {
                DecisionDiffs++;
                LogDiff($"0x{m.Serial:X8}: the component has {fromEcs.Count} steps, the Mobile none");

                return;
            }

            Mobile.Step f = m.Steps.Front();
            StepInput legacy = fromEcs;
            legacy.Front = new StepEntry { X = f.X, Y = f.Y, Z = f.Z, Direction = f.Direction, Run = f.Run };
            legacy.Count = m.Steps.Count;
            legacy.LastStepTime = m.LastStepTime;
            legacy.OffsetX = m.Offset.X;
            legacy.OffsetY = m.Offset.Y;
            legacy.X = m.X;
            legacy.Y = m.Y;
            legacy.Z = m.Z;
            legacy.Mounted = Mounted(m);
            StepRules.Decide(in legacy, ticks, frameDelay, out StepDecision ld);

            if (!ld.Equals(in d))
            {
                DecisionDiffs++;
                LogDiff($"0x{m.Serial:X8} decision: ecs {d} | mobile {ld}");
            }
        }

        // ---- CUO_STEP_MOUNT_CHECK

        private static void CheckMount(Mobile m, EcsEntity e, bool have, bool ecs)
        {
            MountChecks++;
            bool mobile = Mounted(m);

            if (have && ecs == mobile)
            {
                return;
            }

            MountDiffs++;

            if (_mountLogged++ < 10)
            {
                string from = have
                    ? $"slots {e.GetComponent<EquipmentSlots>().Mounted} (dirty {e.Tags.Has<EquipmentDirty>()}), speed {(CharacterSpeedType)e.GetComponent<MobileInputs>().SpeedMode}, flying {(e.GetComponent<MobileInputs>().Flags & AnimStateFlags.Flying) != 0}"
                    : "no MobileInputs/EquipmentSlots";
                Log.Warn($"[step-mount-check] 0x{m.Serial:X8} g=0x{m.Graphic:X4}: ecs {ecs} ({from}) | mobile {mobile} (mounted {m.IsMounted}, speed {m.SpeedMode}, flying {m.IsFlying}) (tick {Time.Ticks})");
            }
        }

        /// <summary>The mount-input counters for the periodic log line.</summary>
        internal static string MountSummary() =>
            $"[step-mount-check] ecs reads {MountEcsReads}, fallbacks to the Mobile {MountFallbacks}, slot rebuilds {MountSlotRebuilds}; checked {MountChecks}, different {MountDiffs}";

        private static void LogDiff(string message)
        {
            if (_logged++ < 10)
            {
                Log.Warn($"[step-check] {message}");
            }
        }

        /// <summary>The checker and system counters for the periodic log line.</summary>
        internal static string Summary() =>
            $"[step-check] ecs runs {Ran}, declined to ProcessSteps {Declined}; interpolated {Interpolated}, completed {Completed} (turns {Turns}); checked {Checks}, before-run differences {SyncDiffs}, decisions different {DecisionDiffs}, after-run differences {AfterDiffs}; component writes {WorldEcs.StepWrites}";
    }
}
