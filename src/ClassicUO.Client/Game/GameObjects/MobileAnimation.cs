// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Assets;
using ClassicUO.Utility;
using ClassicUO.Renderer.Animations;

namespace ClassicUO.Game.GameObjects
{
    internal partial class Mobile
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override ushort GetGraphicForAnimation()
        {
            ushort g = Graphic;

            switch (g)
            {
                case 0x0192:
                case 0x0193:
                {
                    g -= 2;

                    break;
                }

                case 0x02B6:
                    g = 667;

                    break;

                case 0x02B7:
                    g = 666;

                    break;
            }

            return g;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Direction GetDirectionForAnimation()
        {
            if (Steps.Count != 0)
            {
                ref Step step = ref Steps.Front();

                return (Direction)step.Direction;
            }

            return Direction;
        }

        public static byte GetGroupForAnimation(
            Mobile mobile,
            ushort checkGraphic = 0,
            bool isParent = false
        )
        {
            ushort graphic = checkGraphic;

            if (graphic == 0)
            {
                graphic = mobile.GetGraphicForAnimation();
            }

            AnimGroupInput input = mobile.GetAnimGroupInput();

            return AnimationGroupRules.Select(in input, graphic, isParent, Client.Game.UO.Animations);
        }

        internal AnimGroupInput GetAnimGroupInput()
        {
            AnimGroupInput input = new AnimGroupInput
            {
                ServerGroup = _animationGroup,
                FromServer = AnimationFromServer,
                WarMode = InWarMode,
                Walking = IsWalking,
                Running = IsRunning,
                HasSteps = Steps.Count != 0,
                Mounted = IsMounted,
                GargoyleProp = IsGargoyle,
                Flying = IsFlying,
                Dead = IsDead
            };

            if (input.HasSteps)
            {
                input.StepRun = Steps.Front().Run;
            }

            Item hand1 = FindItemByLayer(Layer.OneHanded);
            Item hand2 = FindItemByLayer(Layer.TwoHanded);

            if (hand1 != null)
            {
                input.Hand1 = new HandItem { Present = true, IsLight = hand1.ItemData.IsLight, AnimId = hand1.ItemData.AnimID };
            }

            if (hand2 != null)
            {
                input.Hand2 = new HandItem { Present = true, IsLight = hand2.ItemData.IsLight, AnimId = hand2.ItemData.AnimID };
            }

            return input;
        }

        internal byte AnimationGroupRaw => _animationGroup;


        public static bool IsReplacedObjectAnimation(byte anim, ushort v13)
        {
            if (anim < Client.Game.UO.FileManager.Animations.GroupReplaces.Length)
            {
                foreach (var tuple in Client.Game.UO.FileManager.Animations.GroupReplaces[anim])
                {
                    if (tuple.Item1 == v13)
                    {
                        return tuple.Item2 != 0xFF;
                    }
                }
            }

            return false;
        }

        public static byte GetReplacedObjectAnimation(ushort graphic, ushort index)
        {
            ushort getReplacedGroup(List<(ushort, byte)> list, ushort idx, ushort walkIdx)
            {
                foreach (var item in list)
                {
                    if (item.Item1 == idx)
                    {
                        if (item.Item2 == 0xFF)
                        {
                            return walkIdx;
                        }

                        return item.Item2;
                    }
                }

                return idx;
            }

            AnimationGroups group = Client.Game.UO.FileManager.Animations.GetGroupIndex(
                graphic,
                Client.Game.UO.Animations.GetAnimType(graphic)
            );

            if (group == AnimationGroups.Low)
            {
                return (byte)(
                    getReplacedGroup(
                        Client.Game.UO.FileManager.Animations.GroupReplaces[0],
                        index,
                        (ushort)LowAnimationGroup.Walk
                    ) % (ushort)LowAnimationGroup.AnimationCount
                );
            }

            if (group == AnimationGroups.People)
            {
                return (byte)(
                    getReplacedGroup(
                        Client.Game.UO.FileManager.Animations.GroupReplaces[1],
                        index,
                        (ushort)PeopleAnimationGroup.WalkUnarmed
                    ) % (ushort)PeopleAnimationGroup.AnimationCount
                );
            }

            return (byte)(index % (ushort)HighAnimationGroup.AnimationCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetObjectNewAnimation(
            Mobile mobile,
            ushort type,
            ushort action,
            byte mode
        )
        {
            var animations = Client.Game.UO.Animations;
            if (mobile.Graphic >= animations.MaxAnimationCount)
            {
                return 0;
            }

            switch (type)
            {
                case 0:
                    return GetObjectNewAnimationType_0(animations, mobile, action, mode);

                case 1:
                case 2:
                    return GetObjectNewAnimationType_1_2(animations, mobile, action, mode);

                case 3:
                    return GetObjectNewAnimationType_3(animations, mobile, action, mode);

                case 4:
                    return GetObjectNewAnimationType_4(animations, mobile, action, mode);

                case 5:
                    return GetObjectNewAnimationType_5(animations, mobile, action, mode);

                case 6:
                case 14:
                    return GetObjectNewAnimationType_6_14(animations, mobile, action, mode);

                case 7:
                    return GetObjectNewAnimationType_7(mobile, action, mode);

                case 8:
                    return GetObjectNewAnimationType_8(animations, mobile, action, mode);

                case 9:
                    return GetObjectNewAnimationType_9(animations, mobile, action, mode);
                case 10:
                    return GetObjectNewAnimationType_10(animations, mobile, action, mode);

                case 11:
                    return GetObjectNewAnimationType_11(animations, mobile, action, mode);
            }

            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TestStepNoChangeDirection(Mobile mob, byte group)
        {
            switch ((PeopleAnimationGroup)group)
            {
                case PeopleAnimationGroup.OnmountRideFast:
                case PeopleAnimationGroup.RunUnarmed:
                case PeopleAnimationGroup.RunArmed:
                case PeopleAnimationGroup.OnmountRideSlow:
                case PeopleAnimationGroup.WalkWarmode:
                case PeopleAnimationGroup.WalkArmed:
                case PeopleAnimationGroup.WalkUnarmed:

                    if (mob.Steps.Count != 0)
                    {
                        ref Step s = ref mob.Steps.Front();

                        if (s.X != mob.X || s.Y != mob.Y)
                        {
                            return true;
                        }
                    }

                    break;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_0(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            if (action > 10)
            {
                return 0;
            }

            AnimationFlags flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type == AnimationGroupsType.Monster)
            {
                switch (mode % 4)
                {
                    case 1:
                        return 5;

                    case 2:
                        return 6;

                    case 3:

                        if ((flags & AnimationFlags.Unknown1) != 0)
                        {
                            return 12;
                        }

                        goto case 0;

                    case 0:
                        return 4;
                }
            }
            else if (type == AnimationGroupsType.SeaMonster)
            {
                if (mode % 2 != 0)
                {
                    return 6;
                }

                return 5;
            }
            else if (type != AnimationGroupsType.Animal)
            {
                if (mobile.IsMounted)
                {
                    if (action > 0)
                    {
                        if (action == 1)
                        {
                            return 27;
                        }

                        if (action == 2)
                        {
                            return 28;
                        }

                        return 26;
                    }

                    return 29;
                }

                switch (action)
                {
                    default:
                        if (
                            mobile.IsGargoyle
                            && mobile.IsFlying
                            && animations.AnimationExists(mobile.Graphic, 71)
                        )
                        {
                            return 71;
                        }
                        else if (animations.AnimationExists(mobile.Graphic, 31))
                        {
                            return 31;
                        }

                        break;

                    case 1:
                        return 18;

                    case 2:
                        return 19;

                    case 6:
                        return 12;

                    case 7:
                        if (
                            mobile.IsGargoyle
                            && mobile.IsFlying
                            && animations.AnimationExists(mobile.Graphic, 72)
                        )
                        {
                            return 72;
                        }

                        return 13;

                    case 8:
                        return 14;

                    case 3:
                        return 11;

                    case 4:
                        return 9;

                    case 5:
                        return 10;
                }
            }

            if ((flags & AnimationFlags.Use2IfHittedWhileRunning) != 0)
            {
                return 2;
            }

            if (mode % 2 != 0 && animations.AnimationExists(mobile.Graphic, 6))
            {
                return 6;
            }

            return 5;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_1_2(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (type <= AnimationGroupsType.Animal || mobile.IsMounted)
                {
                    return 0xFF;
                }

                return 30;
            }

            if (mode % 2 != 0)
            {
                return 15;
            }

            return 16;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_3(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (type == AnimationGroupsType.SeaMonster)
                {
                    return 8;
                }

                if (type == AnimationGroupsType.Animal)
                {
                    if (mode % 2 != 0)
                    {
                        return 21;
                    }

                    return 22;
                }

                if (mode % 2 != 0)
                {
                    return 8;
                }

                return 12;
            }

            if (mode % 2 != 0)
            {
                return 2;
            }

            return 3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_4(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (type > AnimationGroupsType.Animal)
                {
                    if (
                        mobile.IsGargoyle
                        && mobile.IsFlying
                        && animations.AnimationExists(mobile.Graphic, 77)
                    )
                    {
                        return 77;
                    }

                    if (mobile.IsMounted)
                    {
                        return 0xFF;
                    }

                    return 20;
                }

                return 7;
            }

            return 10;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_5(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type <= AnimationGroupsType.SeaMonster)
            {
                if (mode % 2 != 0)
                {
                    return 18;
                }

                return 17;
            }

            if (type != AnimationGroupsType.Animal)
            {
                if (mobile.IsMounted)
                {
                    return 0xFF;
                }

                if (mode % 2 != 0)
                {
                    return 6;
                }

                return 5;
            }

            switch (mode % 3)
            {
                case 1:
                    return 10;

                case 2:
                    return 3;
            }

            return 9;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_6_14(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (type != AnimationGroupsType.SeaMonster)
                {
                    if (type == AnimationGroupsType.Animal)
                    {
                        return 3;
                    }

                    if (mobile.IsMounted)
                    {
                        return 0xFF;
                    }

                    return 34;
                }

                return 5;
            }

            return 11;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_7(Mobile mobile, ushort action, byte mode)
        {
            if (mobile.IsMounted)
            {
                return 0xFF;
            }

            if (action > 0)
            {
                if (action == 1)
                {
                    return 33;
                }
            }
            else
            {
                return 32;
            }

            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_8(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (type != AnimationGroupsType.SeaMonster)
                {
                    if (type == AnimationGroupsType.Animal)
                    {
                        return 9;
                    }

                    return mobile.IsMounted ? (byte)0xFF : (byte)33;
                }

                return 3;
            }

            return 11;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_9(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (mobile.IsGargoyle)
                {
                    if (action == 0)
                    {
                        return 60;
                    }
                }

                return 0xFF;
            }

            return 20;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_10(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (mobile.IsGargoyle)
                {
                    if (action == 0)
                    {
                        return 61;
                    }
                }

                return 0xFF;
            }

            return 20;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetObjectNewAnimationType_11(Animations animations, Mobile mobile, ushort action, byte mode)
        {
            AnimationFlags  flags = animations.GetAnimFlags(mobile.Graphic);
            AnimationGroupsType type = AnimationGroupsType.Monster;

            if ((flags & AnimationFlags.Found) != 0)
            {
                type = animations.GetAnimType(mobile.Graphic);
            }

            if (type != AnimationGroupsType.Monster)
            {
                if (type >= AnimationGroupsType.Animal)
                {
                    if (mobile.IsMounted)
                    {
                        return 0xFF;
                    }

                    switch (action)
                    {
                        case 1:
                        case 2:
                            if (mobile.IsGargoyle && mobile.IsFlying)
                            {
                                return 76;
                            }

                            return 17;
                    }

                    if (mobile.IsGargoyle && mobile.IsFlying)
                    {
                        return 75;
                    }

                    return 16;
                }

                return 5;
            }

            return 12;
        }
    }
}
