// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Resources;
using ClassicUO.Utility;

namespace ClassicUO.UnitTests.Ecs
{
    /// <summary>
    /// T9 S8b deleted <c>Mobile.Update</c>, <c>Mobile.ProcessSteps</c> and <c>Mobile.PlanIdleAnimation</c>: the ECS systems
    /// (<c>MobileAnimationAdvanceSystem</c>, <c>StepSystem</c>, <c>AnimationAdvanceRules.PlanIdle</c>) are the only path. This is
    /// a copy of the deleted rules, kept as the reference the systems and the operations are compared with. They read the
    /// Mobile's fields and write through the same operations as the deleted code did.
    /// </summary>
    internal static class LegacyMobileUpdate
    {
        /// <summary>The animation data the deleted idle planner read (tests pass their own).</summary>
        internal interface IIdleAnimationData
        {
            int MaxAnimationCount { get; }
            AnimationGroupsType GetAnimType(ushort graphic);
            AnimationFlags GetAnimFlags(ushort graphic);
            bool AnimationExists(ushort graphic, byte group);
            byte GetReplacedObjectAnimation(ushort graphic, ushort index);
        }

        /// <summary>The deleted <c>Mobile.PlanIdleAnimation</c>: reads the mobile and the animation data, draws the random
        /// numbers in the old order, writes nothing.</summary>
        internal static void PlanIdleAnimation<TData>(this Mobile m, in TData animations, out Mobile.IdlePlan plan) where TData : struct, IIdleAnimationData
        {
            plan = default;
            plan.IdleDue = Mobile.NextIdleDue();

            if (m.IsMounted || m.InWarMode || !m.ExecuteAnimation)
            {
                return;
            }

            plan.Resets = true;
            ushort graphic = m.GetGraphicForAnimation();

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
                    plan.Value = (byte)(m.InWarMode ? 28 : 26);

                    return;
                }

                if (m.IsGargoyle && m.IsFlying)
                {
                    plan.Value = (byte)(RandomHelper.GetValue(0, 2) != 0 ? 66 : 67);

                    return;
                }
            }

            int first = RandomHelper.GetValue(0, 2);
            byte original = m.AnimationGroupRaw;
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

        /// <summary>The deleted <c>Mobile.ProcessSteps</c>, unchanged: the front step's direction out and, with
        /// <paramref name="evalutate"/>, the step processing over the Mobile's queue, offset and LastStepTime.</summary>
        internal static void ProcessSteps(this Mobile m, out byte dir, bool evalutate = false)
        {
            World world = m.World;
            dir = (byte)m.Direction;
            dir &= 7;

            if (m.Steps.Count == 0 || m.IsDestroyed)
            {
                return;
            }

            ref readonly Mobile.Step step = ref m.Steps.Front();
            dir = step.Direction;

            if (step.Run)
            {
                dir &= 7;
            }

            if (!evalutate)
            {
                return;
            }

            if (m.AnimationFromServer)
            {
                world.Writer.EndServerAnimation(m);
            }

            int delay = (int)Time.Ticks - (int)m.LastStepTime;
            bool mounted = m.IsMounted || m.SpeedMode == CharacterSpeedType.FastUnmount ||
                           m.SpeedMode == CharacterSpeedType.FastUnmountAndCantRun || m.IsFlying;
            bool run = step.Run;
            // the client always has a player; a test World may not (World.Player compared by serial)
            bool isPlayer = world.Player != null && m.Serial == world.Player.Serial;

            if (!mounted && !isPlayer && m.Steps.Count > 1 && delay > 0)
            {
                mounted = delay <= (run ? MovementSpeed.STEP_DELAY_MOUNT_RUN : MovementSpeed.STEP_DELAY_MOUNT_WALK);
            }

            int maxDelay = MovementSpeed.TimeToCompleteMovement(run, mounted) - Mobile.StepFrameDelay;
            bool removeStep = delay >= maxDelay;
            bool directionChange = false;

            if (m.X != step.X || m.Y != step.Y)
            {
                bool badStep = false;

                if (m.Offset.X == 0 && m.Offset.Y == 0)
                {
                    int absX = Math.Abs(m.X - step.X);
                    int absY = Math.Abs(m.Y - step.Y);

                    badStep = absX > 1 || absY > 1 || absX + absY == 0;

                    if (!badStep)
                    {
                        absX = m.X;
                        absY = m.Y;

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
                    sbyte offsetZ = (sbyte)((step.Z - m.Z) * x * (4.0f / steps));
                    MovementSpeed.GetPixelOffset(step.Direction, ref x, ref y, steps);

                    world.Writer.InterpolateStep(m, (sbyte)x, (sbyte)y, offsetZ);
                }
            }
            else
            {
                directionChange = true;
                removeStep = true;
            }

            if (removeStep)
            {
                if (isPlayer)
                {
                    if (m.Z - step.Z >= 22)
                    {
                        m.AddMessage(MessageType.Label, ResGeneral.Ouch, TextType.CLIENT);
                    }

                    StepSystem.WalkerBookkeeping(world.Player.Walker);
                }

                Mobile.Step done = step;
                world.Writer.CompleteStep(m, in done, directionChange);

                if (directionChange)
                {
                    ProcessSteps(m, out dir, evalutate);

                    return;
                }
            }

            m.UpdateTextCoordsV();
        }
    }
}
