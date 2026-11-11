// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
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
    /// <summary>T6c: what the old <c>Mobile.ProcessSteps</c> read for the front step (from the record's components).</summary>
    internal struct StepInput
    {
        public StepEntry Front;
        public int Count;
        public uint LastStepTime;
        public float OffsetX, OffsetY;
        public int X, Y;
        public sbyte Z;

        // IsMounted, the fast-unmount speed modes, IsFlying: the ECS's (StepSystem.MountedFromEcs; the Mobile's without the
        // components)
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
    /// T6c: the old <c>Mobile.ProcessSteps(evaluate: true)</c> as a system over the mobile's
    /// <see cref="StepsComponent"/> and its <see cref="Position"/>/<see cref="Facing"/> record (T9 S8b: the only step
    /// path; ProcessSteps is deleted). Runs per due mobile in the Mobiles stage (<c>MobileAnimationAdvanceSystem</c>), in
    /// table order, where the old Update ran ProcessSteps, so footstep sounds, the frame advance and the random draws keep
    /// their order. The results go through the funnel: <c>WorldWriter.InterpolateStep</c>
    /// (the offset; the system writes the component's offset first), the player's walker bookkeeping (as before) and
    /// <c>WorldWriter.CompleteStep</c> (position, tile list and DynamicTileIndex via AddToTile, queue, idle timer, inputs).
    /// T9 S8c: the component is the queue's only copy (the Mobile's Steps, Offset and LastStepTime read it), so the checkers
    /// that compared it with the Mobile (CUO_STEP_CHECK) and the mounted pace with the record-backed getters
    /// (CUO_STEP_MOUNT_CHECK) are gone.
    /// </summary>
    internal sealed class StepSystem
    {
        public static long Ran, Declined, Interpolated, Completed, Turns;

        public static long MountEcsReads, MountFallbacks, MountSlotRebuilds;

        /// <summary>Tests: rebuilds a dirty mobile's <see cref="EquipmentSlots"/> in place of the client's tiledata
        /// (<c>MobileAnimationSystem.Rebuild</c>'s seam); null in the client.</summary>
        internal Action<EcsEntity, AnimationState, Mobile> RebuildSlotsOverride;

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
        // ecs_step_mount_inputs' off path)
        private static bool MountedInput(Mobile mob, in EcsRow row)
        {
            bool have = row.Has<MobileInputs>() && row.Has<EquipmentSlots>();
            bool ecs = have && MountedFromEcs(in row.Get<MobileInputs>(), in row.Get<EquipmentSlots>());

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
        /// mobile is destroyed, not the one World holds, not mirrored, or without the components). The update loop runs only
        /// held, mirrored mobiles with a clock, so it is never declined there. <paramref name="dir"/> is the old ProcessSteps'
        /// direction out.
        /// </summary>
        public bool TryRun(World world, Mobile mob, uint ticks, int frameDelay, out byte dir)
        {
            dir = 0;

            if (mob.IsDestroyed || !world.Holds(mob) ||
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

            if (row.Get<AnimationTimer>().FromServer)
            {
                // a step ends the server's animation through the funnel (as ProcessSteps)
                world.Writer.EndServerAnimation(mob);
                row = EcsRead.Row(e);
            }

            ref readonly Position p = ref row.Get<Position>();
            inp.X = p.X;
            inp.Y = p.Y;
            inp.Z = p.Z;
            inp.Mounted = MountedInput(mob, in row);
            inp.IsPlayer = IsPlayer(world, mob);

            StepRules.Decide(in inp, ticks, frameDelay, out StepDecision d);

            if (d.Interpolate)
            {
                Interpolated++;
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
                // the queue, step time, offset, idle timer and inputs in the record (the funnel)
                world.Writer.CompleteStep(mob, in done, d.DirectionChange);

                if (d.DirectionChange)
                {
                    Turns++;

                    // the old ProcessSteps went on with the next step (its recursion); the record is the same one (an
                    // operation neither moves nor drops it)
                    Run(world, mob, e, ticks, frameDelay, out dir);

                    return;
                }
            }

            mob.UpdateTextCoordsV();
        }

        /// <summary>
        /// T9 S8b: the old <c>Mobile.ProcessFootstepsSound</c>, unchanged, after the step processing: a walking human's step
        /// sound when due. The values first, then the two writes through the funnel (<c>WorldWriter.StepSound</c>), then the
        /// sound, which reads neither field.
        /// </summary>
        internal static void FootstepSound(World world, Mobile m)
        {
            // no profile only in unit tests
            if (ProfileManager.CurrentProfile?.EnableFootstepsSound != true ||!m.IsHuman || m.IsHidden || m.IsDead || m.IsFlying)
            {
                return;
            }

            Mobile.StepQueue steps = m.Steps;

            if (steps.Count == 0 || m.LastStepSoundTime >= Time.Ticks)
            {
                return;
            }

            Mobile.Step step = steps.Back();
            int incID = m.StepSoundOffset;
            int soundID = 0x012B;
            int delaySound = 400;

            if (m.IsMounted)
            {
                if (step.Run)
                {
                    soundID = 0x0129;
                    delaySound = 150;
                }
                else
                {
                    incID = 0;
                    delaySound = 350;
                }
            }

            delaySound = delaySound * 13 / 10;
            soundID += incID;
            int stepX = step.X, stepY = step.Y;

            if (world.Writer.StepSound(m, (incID + 1) % 2, Time.Ticks + delaySound))
            {
                Client.Game.Audio.PlaySoundWithDistance(world, soundID, stepX, stepY);
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

        /// <summary>The system counters for the periodic log line.</summary>
        internal static string Summary() =>
            $"[step-check] ecs runs {Ran}, declined {Declined}; interpolated {Interpolated}, completed {Completed} (turns {Turns}); mounted pace from the ECS {MountEcsReads}, from the Mobile {MountFallbacks}, slot rebuilds {MountSlotRebuilds}";
    }
}
