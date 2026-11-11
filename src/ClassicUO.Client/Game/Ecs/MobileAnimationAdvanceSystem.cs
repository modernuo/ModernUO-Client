// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer.Animations;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>T5b: the animation data the ECS advance reads: the action selection's queries, the idle planner's
    /// replacement table, the stored direction and the frame count of an action.</summary>
    internal interface IAnimationAdvanceSource : IAnimationSource
    {
        byte GetReplacedObjectAnimation(ushort graphic, ushort index);

        void GetAnimDirection(ref byte dir, ref bool mirror);

        /// <summary>The frames of <paramref name="action"/> in stored direction <paramref name="dir"/> (Mobile.AdvanceFrame's
        /// <c>GetAnimationFrames(...).Length</c>).</summary>
        int FrameCount(ushort body, byte action, byte dir);
    }

    /// <summary><see cref="IAnimationAdvanceSource"/> over the client's <see cref="Animations"/>.</summary>
    internal readonly struct LiveAdvanceSource : IAnimationAdvanceSource
    {
        private readonly Animations _animations;

        public LiveAdvanceSource(Animations animations) => _animations = animations;

        public int MaxAnimationCount => _animations.MaxAnimationCount;
        public AnimationGroupsType GetAnimType(ushort graphic) => _animations.GetAnimType(graphic);
        public AnimationFlags GetAnimFlags(ushort graphic) => _animations.GetAnimFlags(graphic);
        public void ConvertBodyIfNeeded(ref ushort graphic, bool isParent) => _animations.ConvertBodyIfNeeded(ref graphic, isParent);
        public bool AnimationExists(ushort graphic, byte group) => _animations.AnimationExists(graphic, group);
        public bool IsReplacedObjectAnimation(byte family, ushort group) => Mobile.IsReplacedObjectAnimation(family, group);
        public byte GetReplacedObjectAnimation(ushort graphic, ushort index) => Mobile.GetReplacedObjectAnimation(graphic, index);
        public void GetAnimDirection(ref byte dir, ref bool mirror) => _animations.GetAnimDirection(ref dir, ref mirror);
        public int FrameCount(ushort body, byte action, byte dir) => _animations.GetAnimationFrames(body, action, dir, out _, out _).Length;
    }

    internal enum AnimAdvanceOutcome : byte
    {
        /// <summary>The frame was not due (or the mobile does not iterate its frames): only the idle part may have run.</summary>
        NotAdvanced,

        /// <summary>The frame advanced; the timer holds the new frame and due time.</summary>
        Advanced,

        /// <summary>A dying (death-rekeyed) mobile's animation is over: the caller removes it through the funnel
        /// (<c>WorldWriter.RemoveDeadMobile</c>) and writes the frame and due time after the removal.</summary>
        Remove
    }

    /// <summary>
    /// T5b: <c>Mobile.Update</c> for a mobile without queued steps or a handles opening, over the ECS state only: the
    /// <see cref="AnimationTimer"/> component, the pushed <see cref="MobileInputs"/> and the <see cref="EquipmentSlots"/>.
    /// Each method mirrors one legacy piece in its old order, including every random draw (the idle delay, the idle pair, a
    /// gargoyle's flight, the selection's LowGroupExtended roll), so a caller that runs the mobiles in table order draws the
    /// same sequence as the legacy loop.
    /// </summary>
    internal static class AnimationAdvanceRules
    {
        /// <summary><c>Mobile.SetAnimation</c> on the timer: the state, the frame due time (now) and the next idle delay (one
        /// random draw, last, as <c>CalculateRandomIdleTime</c>).</summary>
        public static void SetAnimation(ref AnimationTimer t, byte id, byte interval = 0, byte frameCount = 0, ushort repeatCount = 0, bool repeat = false, bool forward = false, bool fromServer = false)
        {
            t.Group = id;
            t.Frame = (byte)(forward ? 0 : frameCount);
            t.Interval = interval;
            t.FrameCount = (byte)(forward ? 0 : frameCount);
            t.RepeatMode = repeatCount;
            t.RepeatModeCount = repeatCount;
            t.Repeat = repeat;
            t.Forward = forward;
            t.FromServer = fromServer;
            t.AnimDue = Time.Ticks;
            t.IdleDue = Mobile.NextIdleDue();
        }

        /// <summary>
        /// <c>Mobile.PlanIdleAnimation</c> over the ECS state: mounted from the slots, war mode, the gargoyle and flying flags
        /// and the body from the pushed inputs, the group and <c>ExecuteAnimation</c> from the timer. Draws in the old order
        /// (the idle delay, then a gargoyle's flight or the idle pair); writes nothing.
        /// </summary>
        public static void PlanIdle<TAnims>(in AnimationTimer t, in MobileInputs inp, bool mounted, in TAnims animations, out Mobile.IdlePlan plan)
            where TAnims : struct, IAnimationAdvanceSource
        {
            plan = default;
            plan.IdleDue = Mobile.NextIdleDue();
            bool warMode = (inp.Flags & AnimStateFlags.WarMode) != 0;

            if (mounted || warMode || !t.ExecuteAnimation)
            {
                return;
            }

            plan.Resets = true;
            ushort graphic = inp.Body;

            if (graphic >= animations.MaxAnimationCount)
            {
                return;
            }

            AnimationGroupsType type = animations.GetAnimType(graphic);
            AnimationFlags flags = animations.GetAnimFlags(graphic);
            AnimationGroups animGroup = AnimationGroups.None;
            bool isLowExtended = false;

            if ((flags & AnimationFlags.CalculateOffsetLowGroupExtended) != 0)
            {
                isLowExtended = true;
                type = AnimationGroupsType.Monster;
            }
            else if ((flags & AnimationFlags.CalculateOffsetByLowGroup) != 0)
            {
                type = AnimationGroupsType.Animal;
            }

            switch (type)
            {
                case AnimationGroupsType.SeaMonster:
                case AnimationGroupsType.Monster:
                    animGroup = AnimationGroups.High;

                    break;

                case AnimationGroupsType.Animal:
                    animGroup = AnimationGroups.Low;

                    break;

                case AnimationGroupsType.Human:
                case AnimationGroupsType.Equipment:
                    animGroup = AnimationGroups.People;

                    break;
            }

            if (animGroup == 0)
            {
                return;
            }

            plan.Group = Mobile.IdleGroup.Set;

            if ((flags & AnimationFlags.UseUopAnimation) != 0)
            {
                if (animGroup != AnimationGroups.People)
                {
                    // war mode returned above, so this is always 26 (the legacy planner's 28 is unreachable too)
                    plan.Value = (byte)(warMode ? 28 : 26);

                    return;
                }

                if ((inp.Flags & AnimStateFlags.GargoyleProp) != 0 && (inp.Flags & AnimStateFlags.Flying) != 0)
                {
                    plan.Value = (byte)(RandomHelper.GetValue(0, 2) != 0 ? 66 : 67);

                    return;
                }
            }

            int first = RandomHelper.GetValue(0, 2);
            byte original = t.Group;
            byte group = Mobile._animationIdle[(byte)animGroup - 1, first];

            if (isLowExtended && group == 18)
            {
                if (!animations.AnimationExists(graphic, 18) && animations.AnimationExists(graphic, 17))
                {
                    group = animations.GetReplacedObjectAnimation(graphic, 17);
                }
                else
                {
                    group = 1;
                }
            }

            if (!animations.AnimationExists(graphic, group))
            {
                first = first == 0 ? 1 : 0;
                group = Mobile._animationIdle[(byte)animGroup - 1, first];

                if (!animations.AnimationExists(graphic, group))
                {
                    plan.Group = Mobile.IdleGroup.SetAnimation;
                    group = original;
                }
            }

            plan.Value = group;
        }

        /// <summary><c>Mobile.ApplyIdle</c> on the timer: the idle due time, the idle reset, then the group or
        /// <c>SetAnimation</c> (which draws its own idle delay).</summary>
        public static void ApplyIdle(ref AnimationTimer t, in Mobile.IdlePlan plan)
        {
            t.IdleDue = plan.IdleDue;

            if (!plan.Resets)
            {
                return;
            }

            t.Frame = 0;
            t.FrameCount = 0;
            t.Interval = 1;
            t.RepeatMode = 1;
            t.RepeatModeCount = 1;
            t.Forward = true;
            t.Repeat = false;
            t.FromServer = true;

            if (plan.Group == Mobile.IdleGroup.Set)
            {
                t.Group = plan.Value;
            }
            else if (plan.Group == Mobile.IdleGroup.SetAnimation)
            {
                SetAnimation(ref t, plan.Value);
            }
        }

        /// <summary><c>Mobile.GetAnimGroupInput</c> from the ECS: the server action from the timer, the rest from the pushed
        /// inputs (walking against the tick) and the slots (mounted, the hands).</summary>
        public static AnimGroupInput InputOf(in AnimationTimer t, in MobileInputs inp, in EquipmentSlots eq, uint ticks)
        {
            AnimStateFlags f = inp.Flags;

            return new AnimGroupInput
            {
                ServerGroup = t.Group,
                FromServer = t.FromServer,
                WarMode = (f & AnimStateFlags.WarMode) != 0,
                Walking = MobileInputRules.IsWalking(in inp, ticks),
                Running = (f & AnimStateFlags.Running) != 0,
                HasSteps = (f & AnimStateFlags.HasSteps) != 0,
                StepRun = (f & AnimStateFlags.StepRun) != 0,
                Mounted = eq.Mounted,
                GargoyleProp = (f & AnimStateFlags.GargoyleProp) != 0,
                Flying = (f & AnimStateFlags.Flying) != 0,
                Dead = (f & AnimStateFlags.Dead) != 0,
                Hand1 = eq.Hand1,
                Hand2 = eq.Hand2
            };
        }

        /// <summary>
        /// <c>Mobile.AdvanceFrame</c> and the <c>AdvanceAnimation</c> operation on the timer: the due and iterate checks, the
        /// action (<see cref="AnimationGroupRules.Select{TAnims}"/>, which may roll), the frame count of the action in the stored
        /// direction, the timer step, a finished server animation's <c>SetAnimation(0xFF)</c> (an idle draw), then the frame
        /// index and due time, unless the dying mobile is to be removed: then <paramref name="frame"/> and
        /// <paramref name="due"/> are for the caller to write after the removal.
        /// </summary>
        public static AnimAdvanceOutcome Advance<TAnims>(ref AnimationTimer t, in MobileInputs inp, in EquipmentSlots eq, uint serial, in TAnims animations, out int frame, out long due)
            where TAnims : struct, IAnimationAdvanceSource =>
            Advance(ref t, in inp, in eq, serial, in animations, inp.Facing, out frame, out due);

        /// <summary><see cref="Advance{TAnims}(ref AnimationTimer, in MobileInputs, in EquipmentSlots, uint, in TAnims, out int, out long)"/>
        /// in direction <paramref name="dir"/>: a stepping mobile advances in the direction the step processing returned (the
        /// step it just completed), not the facing its inputs hold after the completion.</summary>
        public static AnimAdvanceOutcome Advance<TAnims>(ref AnimationTimer t, in MobileInputs inp, in EquipmentSlots eq, uint serial, in TAnims animations, byte dir, out int frame, out long due)
            where TAnims : struct, IAnimationAdvanceSource
        {
            frame = -1;
            due = 0;

            // NoIterateAnimIndex: not animating, or walking recently (Constants.WALKING_DELAY, not IsWalking's) with no step
            if (t.AnimDue >= Time.Ticks || !t.ExecuteAnimation ||
                (inp.LastStepTime > Time.Ticks - Constants.WALKING_DELAY && (inp.Flags & AnimStateFlags.HasSteps) == 0))
            {
                return AnimAdvanceOutcome.NotAdvanced;
            }

            ushort id = inp.Body;
            AnimGroupInput input = InputOf(in t, in inp, in eq, Time.Ticks);
            byte action = AnimationGroupRules.Select(in input, id, true, animations);

            bool mirror = false;
            animations.GetAnimDirection(ref dir, ref mirror);
            int frameCount = 0;

            if (id < animations.MaxAnimationCount && dir < 5)
            {
                frameCount = animations.FrameCount(id, action, dir);
            }

            bool dead = (serial & 0x8000_0000) != 0;
            int delay = Constants.CHARACTER_ANIMATION_DELAY;
            bool remove = dead;

            if (frameCount > 0)
            {
                AnimationTimer step = t;
                delay = AnimationTiming.Advance(ref step, frameCount, out AnimationAdvance advance);
                remove = dead && advance == AnimationAdvance.Wrapped;
                frame = step.Frame;

                // ApplyAnimationTimer: the frame count and the repeat state (the frame index comes last)
                t.FrameCount = step.FrameCount;
                t.RepeatMode = step.RepeatMode;
                t.RepeatModeCount = step.RepeatModeCount;
                t.Repeat = step.Repeat;

                if (advance == AnimationAdvance.Finished)
                {
                    SetAnimation(ref t, 0xFF);
                }
            }

            due = Time.Ticks + delay;

            if (remove)
            {
                return AnimAdvanceOutcome.Remove;
            }

            if (frame >= 0)
            {
                t.Frame = (byte)frame;
            }

            t.AnimDue = due;

            return AnimAdvanceOutcome.Advanced;
        }

        /// <summary><c>Mobile.Update</c> for a mobile without steps or a handles opening: the idle animation when its timer is
        /// due, then the frame advance (the step processing and the footstep sound do nothing without steps).</summary>
        public static AnimAdvanceOutcome Step<TAnims>(ref AnimationTimer t, in MobileInputs inp, in EquipmentSlots eq, uint serial, in TAnims animations, out int frame, out long due)
            where TAnims : struct, IAnimationAdvanceSource
        {
            if (t.IdleDue < Time.Ticks)
            {
                PlanIdle(in t, in inp, eq.Mounted, in animations, out Mobile.IdlePlan plan);
                ApplyIdle(ref t, in plan);
            }

            return Advance(ref t, in inp, in eq, serial, in animations, out frame, out due);
        }
    }

    /// <summary>
    /// T5b/T9 S8b: a due mobile's update, in the ECS (<c>Mobile.Update</c> is gone). Without queued steps or a handles opening
    /// (its <see cref="MobileClock"/> flags are clear): <see cref="AnimationAdvanceRules.Step{TAnims}"/> over its
    /// <see cref="AnimationTimer"/>. Otherwise the old Update's order: the handles opening (<c>Entity.Update</c>), the idle
    /// animation, the step processing (<see cref="StepSystem"/>), the footstep sound, then the frame advance in the step's
    /// direction. The results go through the funnel (<c>WorldWriter.IdleAnimation</c>, the step operations,
    /// <c>WorldWriter.AdvanceAnimationFromEcs</c>: the record's timer, a dying mobile's removal). The caller runs the due
    /// mobiles in table order, so the random draws keep the legacy sequence. T9 S8c: the timer is the record's only copy, so
    /// the mobile half of CUO_ANIM_ADVANCE_CHECK (the component against the Mobile's timer) is gone; the switch now only
    /// prints this system's counters with the corpse checker's (<see cref="ItemAnimationAdvanceSystem"/>).
    /// </summary>
    internal sealed class MobileAnimationAdvanceSystem
    {
        public static long Ran, RanStepping, Declined;
        private static int _logged;

        /// <summary>
        /// Updates <paramref name="mob"/> in the ECS; false (nothing changed) when it is destroyed (the old Update did nothing
        /// either) or, which the due list cannot produce (it holds clock entities of held mobiles, and the clock, timer and step
        /// components are written together from the first update on), not mirrored or without its components.
        /// </summary>
        public bool TryRun(World world, Mobile mob) =>
            TryRun(world, mob, new LiveAdvanceSource(Client.Game.UO.Animations));

        /// <summary><see cref="TryRun(World, Mobile)"/> over any animation data (the unit tests pass a stub).</summary>
        internal bool TryRun<TAnims>(World world, Mobile mob, in TAnims source) where TAnims : struct, IAnimationAdvanceSource
        {
            if (mob.IsDestroyed || !world.Ecs.Mirror.TryGetEntity(mob.Serial, out EcsEntity e))
            {
                Decline(mob, "destroyed or not mirrored");

                return false;
            }

            // one record read for the eligibility, the tag and the components (perf backlog: was up to 7 entity lookups)
            EcsRow row = EcsRead.Row(e);

            if (row.IsNull || !row.TryGet(out MobileClock clock) ||
                !row.Has<AnimationTimer>() || !row.Has<MobileInputs>() || !row.Has<EquipmentSlots>())
            {
                Decline(mob, "without its clock, timer, inputs or slots");

                return false;
            }

            // the slots the drawn path rebuilds lazily: a mobile not drawn since its equipment changed is rebuilt here (the
            // tag removal moves the entity, so before any component ref, and the row is read again)
            if (MobileAnimationSystem.TakeDirty(e, in row))
            {
                AnimationState s = default;
                s.Flags = EcsRead.Get<MobileInputs>(e).Flags;
                world.Ecs.Animation.Rebuild(e, in s, mob);
                row = EcsRead.Row(e);
            }

            Ran++;

            if (clock.Flags != MobileClockFlags.None)
            {
                RunStepping(world, mob, e, in source);

                return true;
            }

            AnimationTimer t = row.Get<AnimationTimer>();
            MobileInputs inp = row.Get<MobileInputs>();
            EquipmentSlots eq = row.Get<EquipmentSlots>();

            AnimAdvanceOutcome outcome = AnimationAdvanceRules.Step(ref t, in inp, in eq, mob.Serial, in source, out int frame, out long due);
            world.Writer.AdvanceAnimationFromEcs(mob, in t, outcome == AnimAdvanceOutcome.Remove, frame, due);

            return true;
        }

        // the old Mobile.Update of a mobile with queued steps or a handles opening, in its order
        private static void RunStepping<TAnims>(World world, Mobile mob, EcsEntity e, in TAnims source)
            where TAnims : struct, IAnimationAdvanceSource
        {
            RanStepping++;

            // Entity.Update: the handles opening (the old Update's base call, first)
            mob.Update();

            EcsRow row = EcsRead.Row(e);
            AnimationTimer t = row.Get<AnimationTimer>();

            if (t.IdleDue < Time.Ticks)
            {
                AnimationAdvanceRules.PlanIdle(in t, in row.Get<MobileInputs>(), row.Get<EquipmentSlots>().Mounted, in source, out Mobile.IdlePlan plan);
                world.Writer.IdleAnimation(mob, in plan);
            }

            if (!world.StepSystem.TryRun(world, mob, Time.Ticks, Mobile.StepFrameDelay, out byte dir))
            {
                // unreachable: a held, mirrored mobile with a clock has its Steps, Position and Facing (StepSystem.TryRun)
                dir = EcsRead.Get<MobileInputs>(e).Facing;
            }

            StepSystem.FootstepSound(world, mob);

            // the advance reads the timer and the inputs after the step operations (SetAnimation, CompleteStep), which wrote
            // them to the record
            row = EcsRead.Row(e);

            if (row.IsNull)
            {
                return;
            }

            t = row.Get<AnimationTimer>();
            MobileInputs inp = row.Get<MobileInputs>();
            EquipmentSlots eq = row.Get<EquipmentSlots>();
            AnimAdvanceOutcome outcome = AnimationAdvanceRules.Advance(ref t, in inp, in eq, mob.Serial, in source, dir, out int frame, out long due);
            world.Writer.AdvanceAnimationFromEcs(mob, in t, outcome == AnimAdvanceOutcome.Remove, frame, due);
        }

        private static void Decline(Mobile mob, string why)
        {
            Declined++;

            if (!mob.IsDestroyed && _logged++ < 25)
            {
                Log.Warn($"[mobile-update-declined] 0x{mob.Serial:X8} {why}: not updated");
            }
        }

        internal static bool Same(in AnimationTimer a, in AnimationTimer b) =>
            a.Frame == b.Frame && a.FrameCount == b.FrameCount && a.Interval == b.Interval && a.RepeatMode == b.RepeatMode &&
            a.RepeatModeCount == b.RepeatModeCount && a.Repeat == b.Repeat && a.Forward == b.Forward && a.FromServer == b.FromServer &&
            a.Group == b.Group && a.ExecuteAnimation == b.ExecuteAnimation && a.AnimDue == b.AnimDue && a.IdleDue == b.IdleDue;

        internal static string Describe(in AnimationTimer t) =>
            $"group {t.Group} frame {t.Frame}/{t.FrameCount} interval {t.Interval} repeat {t.RepeatMode}/{t.RepeatModeCount}/{t.Repeat} fwd {t.Forward} server {t.FromServer} exec {t.ExecuteAnimation} due {t.AnimDue} idle {t.IdleDue}";

        /// <summary>The system counters for the periodic log line.</summary>
        internal static string Summary() =>
            $"[anim-advance-check] ecs advances {Ran} (stepping or handles {RanStepping}), declined {Declined}";
    }
}
