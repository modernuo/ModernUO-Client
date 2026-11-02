// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.Game
{
    /// <summary>The legacy Mobile state <see cref="MobileInputRules.Compute"/> reads, captured at a push.</summary>
    internal struct MobileInputSource
    {
        public ushort Graphic;
        public Flags Flags;

        // Mobile.IsDead (a dead body graphic, or the server's dead flag), Mobile.InWarMode (the player's own property)
        public bool IsDead, WarMode, Female, Running, SAPoisoned, CV7000, PlayerType;
        public NotorietyFlag Notoriety;
        public byte Direction;
        public bool HasSteps, StepRun;
        public byte StepDirection;
        public uint LastStepTime;
        public byte SpeedMode;
    }

    /// <summary>
    /// W5: computes <see cref="MobileInputs"/>, the selection and override-hue inputs that
    /// MobileAnimationSystem's sample read from the legacy Mobile every frame, with the Mobile's own rules
    /// (IsFlying, IsPoisoned, IsGargoyle, GetGraphicForAnimation, ProcessSteps' direction, IsWalking's delay).
    /// WorldEcs runs it on every push; the mutation sites are listed in world-prepare-ecs-plan.md (W5).
    /// </summary>
    internal static class MobileInputRules
    {
        /// <summary>The <see cref="AnimStateFlags"/> bits <see cref="MobileInputs.Flags"/> carries.</summary>
        public const AnimStateFlags PUSHED =
            AnimStateFlags.WarMode | AnimStateFlags.Running | AnimStateFlags.Flying | AnimStateFlags.Dead | AnimStateFlags.Hidden |
            AnimStateFlags.Female | AnimStateFlags.Human | AnimStateFlags.GargoyleBody | AnimStateFlags.GargoyleProp |
            AnimStateFlags.HasSteps | AnimStateFlags.StepRun;

        /// <summary>Mobile.GetGraphicForAnimation: the body the animation is looked up for (dead bodies as the living ones).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort AnimationBody(ushort g)
        {
            switch (g)
            {
                case 0x0192:
                case 0x0193:
                    return (ushort)(g - 2);

                case 0x02B6:
                    return 667;

                case 0x02B7:
                    return 666;
            }

            return g;
        }

        public static void Compute(in MobileInputSource src, ref MobileInputs o)
        {
            ushort g = src.Graphic;
            AnimStateFlags f = 0;

            if (src.WarMode) f |= AnimStateFlags.WarMode;
            if (src.Running) f |= AnimStateFlags.Running;

            // Mobile.IsFlying: CV_7000+ reuses the poisoned bit
            if (src.CV7000 && (src.Flags & Flags.Poisoned) != 0) f |= AnimStateFlags.Flying;
            if (src.IsDead) f |= AnimStateFlags.Dead;
            if ((src.Flags & Flags.Hidden) != 0) f |= AnimStateFlags.Hidden;
            if (src.Female) f |= AnimStateFlags.Female;
            if (AnimationRules.IsHumanBody(g)) f |= AnimStateFlags.Human;
            if (AnimationRules.IsGargoyleDrawBody(g, src.CV7000)) f |= AnimStateFlags.GargoyleBody;
            if (AnimationRules.IsGargoyleProp(g, src.CV7000)) f |= AnimStateFlags.GargoyleProp;

            // ProcessSteps(out dir): the front step's direction, masked only when it runs; else the facing
            byte facing = (byte)(src.Direction & 7);

            if (src.HasSteps)
            {
                f |= AnimStateFlags.HasSteps;
                facing = src.StepDirection;

                if (src.StepRun)
                {
                    f |= AnimStateFlags.StepRun;
                    facing &= 7;
                }
            }

            MobileHueBits hue = 0;

            // Mobile.IsPoisoned: CV_7000+ has its own flag (SetSAPoison)
            if (src.CV7000 ? src.SAPoisoned : (src.Flags & Flags.Poisoned) != 0) hue |= MobileHueBits.Poisoned;
            if ((src.Flags & Flags.Frozen) != 0) hue |= MobileHueBits.Paralyzed;
            if ((src.Flags & Flags.YellowBar) != 0) hue |= MobileHueBits.YellowHits;
            if (src.Notoriety == NotorietyFlag.Invulnerable) hue |= MobileHueBits.Invulnerable;

            o.Flags = f;
            o.Body = AnimationBody(g);
            o.Facing = facing;
            o.Notoriety = (byte)src.Notoriety;
            o.HueBits = hue;
            o.LastStepTime = src.LastStepTime;
            o.WalkingDelay = (uint)(src.PlayerType ? Constants.PLAYER_WALKING_DELAY : Constants.WALKING_DELAY);
            o.SpeedMode = src.SpeedMode;
            o.Pushes++;
        }

        /// <summary>Captures what <see cref="Compute"/> reads from <paramref name="m"/>.</summary>
        public static MobileInputSource From(Mobile m, bool cv7000)
        {
            var src = new MobileInputSource
            {
                Graphic = m.Graphic,
                Flags = m.Flags,
                IsDead = m.IsDead,
                WarMode = m.InWarMode,
                Female = m.IsFemale,
                Running = m.IsRunning,
                SAPoisoned = m.SAPoisoned,
                CV7000 = cv7000,
                PlayerType = m is PlayerMobile,
                Notoriety = m.NotorietyFlag,
                Direction = (byte)m.Direction,
                HasSteps = m.Steps.Count != 0,
                LastStepTime = m.LastStepTime,
                SpeedMode = (byte)m.SpeedMode
            };

            if (src.HasSteps)
            {
                ref readonly Mobile.Step step = ref m.Steps.Front();
                src.StepDirection = step.Direction;
                src.StepRun = step.Run;
            }

            return src;
        }

        /// <summary>IsWalking from the pushed inputs (the Mobile's uint expression).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWalking(in MobileInputs i, uint ticks) => i.LastStepTime > ticks - i.WalkingDelay;
    }
}
