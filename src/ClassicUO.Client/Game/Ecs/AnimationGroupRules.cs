// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer.Animations;
using ClassicUO.Utility;

namespace ClassicUO.Game
{
    /// <summary>The mobile state ClassicUO's action selection reads; built from a Mobile or from the ECS components.</summary>
    internal struct AnimGroupInput
    {
        public byte ServerGroup;
        public bool FromServer, WarMode, Walking, Running, HasSteps, StepRun, Mounted, GargoyleProp, Flying, Dead;
        public HandItem Hand1, Hand2;

        public static AnimGroupInput From(in AnimationState s, in EquipmentSlots eq)
        {
            AnimStateFlags f = s.Flags;

            return new AnimGroupInput
            {
                ServerGroup = s.ServerGroup,
                FromServer = (f & AnimStateFlags.FromServer) != 0,
                WarMode = (f & AnimStateFlags.WarMode) != 0,
                Walking = (f & AnimStateFlags.Walking) != 0,
                Running = (f & AnimStateFlags.Running) != 0,
                HasSteps = (f & AnimStateFlags.HasSteps) != 0,
                StepRun = (f & AnimStateFlags.StepRun) != 0,
                Mounted = (f & AnimStateFlags.Mounted) != 0,
                GargoyleProp = (f & AnimStateFlags.GargoyleProp) != 0,
                Flying = (f & AnimStateFlags.Flying) != 0,
                Dead = (f & AnimStateFlags.Dead) != 0,
                Hand1 = eq.Hand1,
                Hand2 = eq.Hand2
            };
        }
    }

    /// <summary>The animation table queries <see cref="AnimationGroupRules.Select{TAnims}"/> makes.</summary>
    internal interface IAnimationSource
    {
        int MaxAnimationCount { get; }

        AnimationGroupsType GetAnimType(ushort graphic);

        AnimationFlags GetAnimFlags(ushort graphic);

        void ConvertBodyIfNeeded(ref ushort graphic, bool isParent);

        bool AnimationExists(ushort graphic, byte group);

        // anim1.def / anim2.def: the server action has a replacement for this body family (0 animal, 1 human, 2 monster, 3 sea)
        bool IsReplacedObjectAnimation(byte family, ushort group);
    }

    /// <summary><see cref="IAnimationSource"/> over the client's <see cref="Animations"/>.</summary>
    internal readonly struct AnimationsSource : IAnimationSource
    {
        private readonly Animations _animations;

        public AnimationsSource(Animations animations) => _animations = animations;

        public int MaxAnimationCount => _animations.MaxAnimationCount;

        public AnimationGroupsType GetAnimType(ushort graphic) => _animations.GetAnimType(graphic);

        public AnimationFlags GetAnimFlags(ushort graphic) => _animations.GetAnimFlags(graphic);

        public void ConvertBodyIfNeeded(ref ushort graphic, bool isParent) => _animations.ConvertBodyIfNeeded(ref graphic, isParent);

        public bool AnimationExists(ushort graphic, byte group) => _animations.AnimationExists(graphic, group);

        public bool IsReplacedObjectAnimation(byte family, ushort group) => Mobile.IsReplacedObjectAnimation(family, group);
    }

    /// <summary>
    /// ClassicUO's animation action selection (moved verbatim from Mobile.GetGroupForAnimation, which now delegates
    /// here). The legacy views and the ECS sprite extract both call <see cref="Select"/>.
    /// </summary>
    internal static class AnimationGroupRules
    {
        /// <summary>Counts selections that rolled a random action (LowGroupExtended, server action 5/6): such a result
        /// changes every frame, so MobileFrameCache never reuses it.</summary>
        public static uint RandomRolls;

        private static readonly ushort[] HANDS_BASE_ANIMID =
        {
            0x0263,
            0x0264,
            0x0265,
            0x0266,
            0x0267,
            0x0268,
            0x0269,
            0x026D,
            0x0270,
            0x0272,
            0x0274,
            0x027A,
            0x027C,
            0x027F,
            0x0281,
            0x0286,
            0x0288,
            0x0289,
            0x028B,
            0
        };

        private static readonly ushort[] HAND2_BASE_ANIMID =
        {
            0x0240,
            0x0241,
            0x0242,
            0x0243,
            0x0244,
            0x0245,
            0x0246,
            0x03E0,
            0x03E1,
            0
        };

        private static void CalculateHight<TAnims>(
            TAnims animations,
            ushort graphic,
            in AnimGroupInput mobile,
            AnimationFlags  flags,
            bool isrun,
            bool iswalking,
            ref byte result
        )
            where TAnims : struct, IAnimationSource
        {
            if ((flags & AnimationFlags.CalculateOffsetByPeopleGroup) != 0)
            {
                if (result == 0xFF)
                {
                    result = 0;
                }
            }
            else if ((flags & AnimationFlags.CalculateOffsetByLowGroup) != 0)
            {
                if (!iswalking)
                {
                    if (result == 0xFF)
                    {
                        result = 2;
                    }
                }
                else if (isrun)
                {
                    result = 1;
                }
                else
                {
                    result = 0;
                }
            }
            else
            {
                if (mobile.Flying)
                {
                    result = 19;
                }
                else if (!iswalking)
                {
                    if (result == 0xFF)
                    {
                        if (
                            (flags & AnimationFlags.IdleAt8Frame) != 0
                            && animations.AnimationExists(graphic, 8)
                        )
                        {
                            result = 8;
                        }
                        else
                        {
                            if (
                                (flags & AnimationFlags.UseUopAnimation) != 0
                                && !mobile.WarMode
                            )
                            {
                                result = 25;
                            }
                            else
                            {
                                result = 1;
                            }
                        }
                    }
                }
                else if (isrun)
                {
                    if (
                        (flags & AnimationFlags.CanFlying) != 0
                        && animations.AnimationExists(graphic, 19)
                    )
                    {
                        result = 19;
                    }
                    else
                    {
                        if ((flags & AnimationFlags.UseUopAnimation) != 0)
                        {
                            result = 24;
                        }
                        else
                        {
                            result = 0;
                        }
                    }
                }
                else
                {
                    if ((flags & AnimationFlags.UseUopAnimation) != 0 && !mobile.WarMode)
                    {
                        result = 22;
                    }
                    else
                    {
                        result = 0;
                    }
                }
            }
        }

        private static void LABEL_222(AnimationFlags  flags, ref ushort v13)
        {
            if ((flags & AnimationFlags.CalculateOffsetLowGroupExtended) != 0)
            {
                switch (v13)
                {
                    case 0:
                        v13 = 0;

                        goto LABEL_243;

                    case 1:
                        v13 = 19;

                        goto LABEL_243;

                    case 5:
                    case 6:

                        if ((flags & AnimationFlags.IdleAt8Frame) != 0)
                        {
                            v13 = 4;
                        }
                        else
                        {
                            v13 = (ushort)(6 - (RandomHelper.GetValue() % 2 != 0 ? 1 : 0));
                            RandomRolls++;
                        }

                        goto LABEL_243;

                    case 8:
                        v13 = 2;

                        goto LABEL_243;

                    case 9:
                        v13 = 17;

                        goto LABEL_243;

                    case 10:
                        v13 = 18;

                        if ((flags & AnimationFlags.IdleAt8Frame) != 0)
                        {
                            v13--;
                        }

                        goto LABEL_243;

                    case 12:
                        v13 = 3;

                        goto LABEL_243;
                }

                // LABEL_241
                v13 = 1;
            }
            else
            {
                if ((flags & AnimationFlags.CalculateOffsetByLowGroup) != 0)
                {
                    switch (v13)
                    {
                        case 0:
                            // LABEL_232
                            v13 = 0;

                            break;

                        case 2:
                            v13 = 8;

                            break;

                        case 3:
                            v13 = 12;

                            break;

                        case 4:
                        case 6:
                        case 7:
                        case 8:
                        case 9:
                        case 12:
                        case 13:
                        case 14:
                            v13 = 5;

                            break;

                        case 5:
                            v13 = 6;

                            break;

                        case 10:
                        case 21:
                            v13 = 7;

                            break;

                        case 11:
                            //LABEL_238:
                            v13 = 3;

                            break;

                        case 17:
                            v13 = 9;

                            break;

                        case 18:
                            v13 = 10;

                            break;

                        case 19:

                            v13 = 1;

                            break;

                        default:
                            //LABEL_242:
                            v13 = 2;

                            break;
                    }
                }
            }

            LABEL_243:
            v13 = (ushort)(v13 & 0x7F);

            //if (v13 > 34)
            //    v13 = 0;
        }

        private static void LABEL_190(AnimationFlags  flags, ref ushort v13)
        {
            if ((flags & AnimationFlags.Unknown80) != 0 && v13 == 4)
            {
                v13 = 5;
            }

            if ((flags & AnimationFlags.Unknown200) != 0)
            {
                if (v13 - 7 > 9)
                {
                    if (v13 == 19)
                    {
                        //LABEL_196
                        v13 = 0;
                    }
                    else if (v13 > 19)
                    {
                        v13 = 1;
                    }

                    LABEL_222(flags, ref v13);

                    return;
                }
            }
            else
            {
                if ((flags & AnimationFlags.Unknown100) != 0)
                {
                    switch (v13)
                    {
                        case 10:
                        case 15:
                        case 16:
                            v13 = 1;
                            LABEL_222(flags, ref v13);

                            return;

                        case 11:
                            v13 = 17;
                            LABEL_222(flags, ref v13);

                            return;
                    }

                    LABEL_222(flags, ref v13);

                    return;
                }

                if ((flags & AnimationFlags.Unknown1) != 0)
                {
                    if (v13 == 21)
                    {
                        v13 = 10;
                    }

                    LABEL_222(flags, ref v13);

                    return;
                }

                if ((flags & AnimationFlags.CalculateOffsetByPeopleGroup) == 0)
                {
                    //LABEL_222:
                    LABEL_222(flags, ref v13);

                    return;
                }

                switch (v13)
                {
                    case 0:
                        v13 = 0;

                        break;

                    case 2:
                        v13 = 21;
                        LABEL_222(flags, ref v13);

                        return;

                    case 3:
                        v13 = 22;
                        LABEL_222(flags, ref v13);

                        return;

                    case 4:
                    case 9:
                        v13 = 9;
                        LABEL_222(flags, ref v13);

                        return;

                    case 5:
                        v13 = 11;
                        LABEL_222(flags, ref v13);

                        return;

                    case 6:
                        v13 = 13;
                        LABEL_222(flags, ref v13);

                        return;

                    case 7:
                        v13 = 18;
                        LABEL_222(flags, ref v13);

                        return;

                    case 8:
                        v13 = 19;
                        LABEL_222(flags, ref v13);

                        return;

                    case 10:
                    case 21:
                        v13 = 20;
                        LABEL_222(flags, ref v13);

                        return;

                    case 11:
                        v13 = 3;
                        LABEL_222(flags, ref v13);

                        return;

                    case 12:
                    case 14:
                        v13 = 16;
                        LABEL_222(flags, ref v13);

                        return;

                    case 13:
                        //LABEL_202:
                        v13 = 17;
                        LABEL_222(flags, ref v13);

                        return;

                    case 15:
                    case 16:
                        v13 = 30;
                        LABEL_222(flags, ref v13);

                        return;

                    case 17:
                        v13 = 5;
                        LABEL_222(flags, ref v13);

                        return;

                    case 18:
                        v13 = 6;
                        LABEL_222(flags, ref v13);

                        return;

                    case 19:
                        //LABEL_201:
                        v13 = 1;
                        LABEL_222(flags, ref v13);

                        return;
                }
            }

            v13 = 4;

            LABEL_222(flags, ref v13);
        }

        /// <summary>
        /// The action (animation group) to draw <paramref name="graphic"/> with: ClassicUO's Mobile.GetGroupForAnimation,
        /// reading its mobile state from <paramref name="m"/> so the legacy Mobile and the ECS share one rule.
        /// </summary>
        public static byte Select(in AnimGroupInput m, ushort graphic, bool isParent, Animations animations) =>
            Select(in m, graphic, isParent, new AnimationsSource(animations));

        /// <summary><see cref="Select(in AnimGroupInput, ushort, bool, Animations)"/> over any animation table source. A struct
        /// type argument is specialised by the JIT, so the real <see cref="AnimationsSource"/> costs no interface dispatch;
        /// the unit tests pass a stub.</summary>
        public static byte Select<TAnims>(in AnimGroupInput m, ushort graphic, bool isParent, TAnims animations)
            where TAnims : struct, IAnimationSource
        {
            if (graphic >= animations.MaxAnimationCount)
            {
                return 0;
            }

            AnimationGroupsType originalType = animations.GetAnimType(graphic);
            animations.ConvertBodyIfNeeded(ref graphic, isParent);
            AnimationGroupsType type = animations.GetAnimType(graphic);
            AnimationFlags  flags = animations.GetAnimFlags(graphic);

            bool uop = (flags & AnimationFlags.UseUopAnimation) != 0;

            if (m.FromServer && m.ServerGroup != 0xFF)
            {
                ushort v13 = m.ServerGroup;

                if (v13 == 12)
                {
                    if (
                        !(
                            type == AnimationGroupsType.Human
                            || type == AnimationGroupsType.Equipment
                            || (flags & AnimationFlags.Unknown1000) != 0
                        )
                    )
                    {
                        if (type != AnimationGroupsType.Monster)
                        {
                            if (
                                type == AnimationGroupsType.Human
                                || type == AnimationGroupsType.Equipment
                            )
                            {
                                v13 = 16;
                            }
                            else
                            {
                                v13 = 5;
                            }
                        }
                        else
                        {
                            v13 = 4;
                        }
                    }
                }

                if (type != AnimationGroupsType.Monster)
                {
                    if (type != AnimationGroupsType.SeaMonster)
                    {
                        if (type == AnimationGroupsType.Animal)
                        {
                            if (animations.IsReplacedObjectAnimation(0, v13))
                            {
                                originalType = AnimationGroupsType.Unknown;
                            }

                            if (v13 > 12)
                            {
                                switch (v13)
                                {
                                    case 23:
                                        v13 = 0;

                                        break;

                                    case 24:
                                        v13 = 1;

                                        break;

                                    case 26:

                                        if (
                                            !animations.AnimationExists(graphic, 26)
                                            || (
                                                m.WarMode
                                                && animations.AnimationExists(
                                                    graphic,
                                                    9
                                                )
                                            )
                                        )
                                        {
                                            v13 = 9;
                                        }

                                        break;

                                    case 28:

                                        v13 = (ushort)(
                                            animations.AnimationExists(graphic, 10)
                                                ? 10
                                                : 5
                                        );

                                        break;

                                    default:
                                        v13 = 2;

                                        break;
                                }
                            }

                            //if (v13 > 12)
                            //    v13 = 0; // 2
                        }
                        else
                        {
                            if (animations.IsReplacedObjectAnimation(1, v13))
                            {
                                // LABEL_190:

                                LABEL_190(flags, ref v13);

                                return (byte)v13;
                            }
                        }
                    }
                    else
                    {
                        if (animations.IsReplacedObjectAnimation(3, v13))
                        {
                            originalType = AnimationGroupsType.Unknown;
                        }

                        if (v13 > 8)
                        {
                            v13 = 2;
                        }
                    }
                }
                else
                {
                    if (animations.IsReplacedObjectAnimation(2, v13))
                    {
                        originalType = AnimationGroupsType.Unknown;
                    }

                    if (!animations.AnimationExists(graphic, (byte)v13))
                    {
                        v13 = 1;
                    }

                    if ((flags & AnimationFlags.UseUopAnimation) != 0)
                    {
                        // do nothing?
                    }
                    else if (v13 > 21)
                    {
                        v13 = 1;
                    }
                }

                if (originalType == AnimationGroupsType.Unknown)
                {
                    LABEL_190(flags, ref v13);

                    return (byte)v13;
                }

                if (originalType != 0)
                {
                    if (
                        originalType == AnimationGroupsType.Animal
                        && type == AnimationGroupsType.Monster
                    )
                    {
                        switch (v13)
                        {
                            case 0:
                                v13 = 0;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 1:
                                v13 = 19;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 3:
                                v13 = 11;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 5:
                                v13 = 4;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 6:
                                v13 = 5;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 7:
                            case 11:
                                v13 = 10;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 8:
                                v13 = 2;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 9:
                                v13 = 17;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 10:
                                v13 = 18;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 12:
                                v13 = 3;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;
                        }

                        // LABEL_187
                        v13 = 1;
                    }

                    LABEL_190(flags, ref v13);

                    return (byte)v13;
                }

                switch (type)
                {
                    case AnimationGroupsType.Human:

                        switch (v13)
                        {
                            case 0:
                                v13 = 0;

                                goto LABEL_189;

                            case 2:
                                v13 = 21;

                                goto LABEL_189;

                            case 3:
                                v13 = 22;

                                goto LABEL_189;

                            case 4:
                            case 9:
                                v13 = 9;

                                goto LABEL_189;

                            case 5:
                                //LABEL_163:
                                v13 = 11;

                                goto LABEL_189;

                            case 6:
                                v13 = 13;

                                goto LABEL_189;

                            case 7:
                                //LABEL_165:
                                v13 = 18;

                                goto LABEL_189;

                            case 8:
                                //LABEL_172:
                                v13 = 19;

                                goto LABEL_189;

                            case 10:
                            case 21:
                                v13 = 20;

                                goto LABEL_189;

                            case 12:
                            case 14:
                                v13 = 16;

                                goto LABEL_189;

                            case 13:
                                //LABEL_164:
                                v13 = 17;

                                goto LABEL_189;

                            case 15:
                            case 16:
                                v13 = 30;

                                goto LABEL_189;

                            case 17:
                                v13 = 5;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 18:
                                v13 = 6;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 19:
                                v13 = 1;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;
                        }

                        //LABEL_161:
                        v13 = 4;

                        goto LABEL_189;

                    case AnimationGroupsType.Animal:

                        switch (v13)
                        {
                            case 0:
                                v13 = 0;

                                goto LABEL_189;

                            case 2:
                                v13 = 8;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 3:
                                v13 = 12;

                                goto LABEL_189;

                            case 4:
                            case 6:
                            case 7:
                            case 8:
                            case 9:
                            case 12:
                            case 13:
                            case 14:
                                v13 = 5;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 5:
                                v13 = 6;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 10:
                            case 21:
                                v13 = 7;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 11:
                                v13 = 3;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;

                            case 17:
                                //LABEL_170:
                                v13 = 9;

                                goto LABEL_189;

                            case 18:
                                //LABEL_162:
                                v13 = 10;

                                goto LABEL_189;

                            case 19:
                                v13 = 1;
                                LABEL_190(flags, ref v13);

                                return (byte)v13;
                        }

                        v13 = 2;
                        LABEL_190(flags, ref v13);

                        return (byte)v13;

                    case AnimationGroupsType.SeaMonster:

                        switch (v13)
                        {
                            case 0:
                                //LABEL_182:
                                v13 = 0;

                                goto LABEL_189;

                            case 2:
                            case 3:
                                //LABEL_178:
                                v13 = 8;

                                goto LABEL_189;

                            case 4:
                            case 6:
                            case 7:
                            case 8:
                            case 9:
                            case 12:
                            case 13:
                            case 14:
                                //LABEL_183:
                                v13 = 5;

                                goto LABEL_189;

                            case 5:
                                //LABEL_184:
                                v13 = 6;

                                goto LABEL_189;

                            case 10:
                            case 21:
                                //LABEL_185:
                                v13 = 7;

                                goto LABEL_189;

                            case 17:
                                //LABEL_186:
                                v13 = 3;

                                goto LABEL_189;

                            case 18:
                                v13 = 4;

                                goto LABEL_189;

                            case 19:
                                LABEL_190(flags, ref v13);

                                return (byte)v13;
                        }

                        v13 = 2;
                        LABEL_190(flags, ref v13);

                        return (byte)v13;

                    default:
                        LABEL_189:

                        LABEL_190(flags, ref v13);

                        return (byte)v13;
                }
            }

            byte result = m.ServerGroup;

            bool isWalking = m.Walking;
            bool isRun = m.Running;

            if (m.HasSteps)
            {
                isWalking = true;
                isRun = m.StepRun;
            }

            switch (type)
            {
                case AnimationGroupsType.Animal:

                    if ((flags & AnimationFlags.CalculateOffsetLowGroupExtended) != 0)
                    {
                        CalculateHight(animations, graphic, m, flags, isRun, isWalking, ref result);
                    }
                    else
                    {
                        if (!isWalking)
                        {
                            if (result == 0xFF)
                            {
                                if ((flags & AnimationFlags.UseUopAnimation) != 0)
                                {
                                    if (
                                        m.WarMode
                                        && animations.AnimationExists(graphic, 1)
                                    )
                                    {
                                        result = 1;
                                    }
                                    else
                                    {
                                        result = 25;
                                    }
                                }
                                else
                                {
                                    result = 2;
                                }
                            }
                        }
                        else if (isRun)
                        {
                            if ((flags & AnimationFlags.UseUopAnimation) != 0)
                            {
                                result = 24;
                            }
                            else
                            {
                                result = animations.AnimationExists(graphic, 1)
                                    ? (byte)1
                                    : (byte)2;
                            }
                        }
                        else if (
                            (flags & AnimationFlags.UseUopAnimation) != 0
                            && (
                                !m.WarMode
                                || !animations.AnimationExists(graphic, 0)
                            )
                        )
                        {
                            result = 22;
                        }
                        else
                        {
                            result = 0;
                        }
                    }

                    break;

                case AnimationGroupsType.Monster:
                    CalculateHight(animations, graphic, m, flags, isRun, isWalking, ref result);

                    break;

                case AnimationGroupsType.SeaMonster:

                    if (!isWalking)
                    {
                        if (result == 0xFF)
                        {
                            result = 2;
                        }
                    }
                    else if (isRun)
                    {
                        result = 1;
                    }
                    else
                    {
                        result = 0;
                    }

                    break;

                default:
                {
                    HandItem hand2 = m.Hand2;

                    if (!isWalking)
                    {
                        if (result == 0xFF)
                        {
                            bool haveLightAtHand2 =
                                hand2.Present
                                && hand2.IsLight
                                && hand2.AnimId == graphic;

                            if (m.Mounted)
                            {
                                if (haveLightAtHand2)
                                {
                                    result = 28;
                                }
                                else
                                {
                                    result = 25;
                                }
                            }
                            else if (m.GargoyleProp && m.Flying) // TODO: what's up when it is dead?
                            {
                                if (m.WarMode)
                                {
                                    result = 65;
                                }
                                else
                                {
                                    result = 64;
                                }
                            }
                            else if (!m.WarMode || m.Dead)
                            {
                                if (haveLightAtHand2)
                                {
                                    // TODO: UOP EQUIPMENT ?
                                    result = 0;
                                }
                                else
                                {
                                    if (
                                        uop
                                        && type == AnimationGroupsType.Equipment
                                        && animations.AnimationExists(graphic, 37)
                                    )
                                    {
                                        result = 37;
                                    }
                                    else
                                    {
                                        result = 4;
                                    }
                                }
                            }
                            else if (haveLightAtHand2)
                            {
                                // TODO: UOP EQUIPMENT ?

                                result = 2;
                            }
                            else
                            {
                                unsafe
                                {
                                    ushort* handAnimIDs = stackalloc ushort[2];
                                    HandItem hand1 = m.Hand1;

                                    if (hand1.Present)
                                    {
                                        handAnimIDs[0] = hand1.AnimId;
                                    }

                                    if (hand2.Present)
                                    {
                                        handAnimIDs[1] = hand2.AnimId;
                                    }

                                    if (!hand1.Present)
                                    {
                                        if (hand2.Present)
                                        {
                                            if (
                                                uop
                                                && type == AnimationGroupsType.Equipment
                                                && !animations.AnimationExists(
                                                    graphic,
                                                    7
                                                )
                                            )
                                            {
                                                result = 8;
                                            }
                                            else
                                            {
                                                result = 7;
                                            }

                                            for (int i = 0; i < 2; i++)
                                            {
                                                if (
                                                    handAnimIDs[i] >= 0x0263
                                                    && handAnimIDs[i] <= 0x028B
                                                )
                                                {
                                                    for (
                                                        int k = 0;
                                                        k < HANDS_BASE_ANIMID.Length;
                                                        k++
                                                    )
                                                    {
                                                        if (handAnimIDs[i] == HANDS_BASE_ANIMID[k])
                                                        {
                                                            result = 8;
                                                            i = 2;

                                                            break;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else if (m.GargoyleProp && m.Flying)
                                        {
                                            result = 64;
                                        }
                                        else
                                        {
                                            result = 7;
                                        }
                                    }
                                    else
                                    {
                                        result = 7;
                                    }
                                }
                            }
                        }
                    }
                    else if (m.Mounted)
                    {
                        if (isRun)
                        {
                            result = 24;
                        }
                        else
                        {
                            result = 23;
                        }
                    }
                    //else if (EquippedGraphic0x3E96)
                    //{

                    //}
                    else if (isRun || !m.WarMode || m.Dead)
                    {
                        if ((flags & AnimationFlags.UseUopAnimation) != 0)
                        {
                            // i'm not sure here if it's necessary the isgargoyle
                            if (m.GargoyleProp && m.Flying)
                            {
                                if (isRun)
                                {
                                    result = 63;
                                }
                                else
                                {
                                    result = 62;
                                }
                            }
                            else
                            {
                                if (isRun && animations.AnimationExists(graphic, 24))
                                {
                                    result = 24;
                                }
                                else
                                {
                                    if (isRun)
                                    {
                                        if (
                                            uop
                                            && type == AnimationGroupsType.Equipment
                                            && !animations.AnimationExists(graphic, 2)
                                        )
                                        {
                                            result = 3;
                                        }
                                        else
                                        {
                                            result = 2;

                                            if (m.GargoyleProp)
                                            {
                                                hand2 = m.Hand1;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (
                                            uop
                                            && type == AnimationGroupsType.Equipment
                                            && !animations.AnimationExists(graphic, 0)
                                        )
                                        {
                                            result = 1;
                                        }
                                        else
                                        {
                                            result = 0;
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (isRun)
                            {
                                result = (byte)(hand2.Present ? 3 : 2);
                            }
                            else
                            {
                                result = (byte)(hand2.Present ? 1 : 0);
                            }
                        }

                        if (hand2.Present)
                        {
                            ushort hand2Graphic = hand2.AnimId;

                            if (hand2Graphic < 0x0240 || hand2Graphic > 0x03E1)
                            {
                                if (m.GargoyleProp && m.Flying)
                                {
                                    if (isRun)
                                    {
                                        result = 63;
                                    }
                                    else
                                    {
                                        result = 62;
                                    }
                                }
                                else
                                {
                                    if (isRun)
                                    {
                                        result = 3;
                                    }
                                    else
                                    {
                                        result = 1;
                                    }
                                }
                            }
                            else
                            {
                                for (int i = 0; i < HAND2_BASE_ANIMID.Length; i++)
                                {
                                    if (HAND2_BASE_ANIMID[i] == hand2Graphic)
                                    {
                                        if (m.GargoyleProp && m.Flying)
                                        {
                                            if (isRun)
                                            {
                                                result = 63;
                                            }
                                            else
                                            {
                                                result = 62;
                                            }
                                        }
                                        else
                                        {
                                            if (isRun)
                                            {
                                                result = 3;
                                            }
                                            else
                                            {
                                                result = 1;
                                            }
                                        }

                                        break;
                                    }
                                }
                            }
                        }
                    }
                    else if (m.GargoyleProp && m.Flying)
                    {
                        result = 62;
                    }
                    else
                    {
                        result = 15;
                    }

                    break;
                }
            }

            return result;
        }
    }
}
