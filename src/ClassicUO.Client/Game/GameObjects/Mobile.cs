// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Collections;
using Microsoft.Xna.Framework;
using System;

namespace ClassicUO.Game.GameObjects
{
    internal partial class Mobile : Entity
    {
        /// <summary>W5: the Gpu world's reused selection and sprite instances (gpu_mobile_frame_cache).</summary>
        internal MobileFrameCache FrameCache;

        /// <summary>E5: the mirror entity the Gpu world last found for <see cref="EcsEntitySerial"/>, so the per-frame
        /// draw skips the serial dictionary. Valid while the entity is alive and the serial is the mobile's own.</summary>
        internal Friflo.Engine.ECS.Entity EcsEntity;
        internal uint EcsEntitySerial, EcsEntityEpoch;

        //private static readonly QueuedPool<Mobile> _pool = new QueuedPool<Mobile>(
        //    Constants.PREDICTABLE_CHUNKS,
        //    mobile =>
        //    {
        //        mobile.IsDestroyed = false;
        //        mobile.Graphic = 0;
        //        mobile.Steps.Clear();
        //        mobile.Offset = Vector3.Zero;
        //        mobile.SpeedMode = CharacterSpeedType.Normal;
        //        mobile.Race = 0;
        //        mobile.Hits = 0;
        //        mobile.HitsMax = 0;
        //        mobile.Mana = 0;
        //        mobile.ManaMax = 0;
        //        mobile.Stamina = 0;
        //        mobile.StaminaMax = 0;
        //        mobile.NotorietyFlag = 0;
        //        mobile.IsRenamable = false;
        //        mobile.Flags = 0;
        //        mobile.IsFemale = false;
        //        mobile.InWarMode = false;
        //        mobile.IsRunning = false;
        //        mobile._animationInterval = 0;
        //        mobile.AnimationFrameCount = 0;
        //        mobile._animationRepeateMode = 1;
        //        mobile._animationRepeatModeCount = 1;
        //        mobile._animationRepeat = false;
        //        mobile.AnimationFromServer = false;
        //        mobile._isAnimationForwardDirection = false;
        //        mobile.LastStepSoundTime = 0;
        //        mobile.StepSoundOffset = 0;
        //        mobile.Title = string.Empty;
        //        mobile._animationGroup = 0xFF;
        //        mobile._isDead = false;
        //        mobile._isSA_Poisoned = false;
        //        mobile._lastAnimationIdleDelay = 0;
        //        mobile.X = 0;
        //        mobile.Y = 0;
        //        mobile.Z = 0;
        //        mobile.Direction = 0;
        //        mobile.LastAnimationChangeTime = Time.Ticks;
        //        mobile.TextContainer?.Clear();
        //        mobile.HitsPercentage = 0;
        //        mobile.IsFlipped = false;
        //        mobile.FrameInfo = Rectangle.Empty;
        //        mobile.ObjectHandlesStatus = ObjectHandlesStatus.NONE;
        //        mobile.AlphaHue = 0;
        //        mobile.AllowedToDraw = true;
        //        mobile.IsClicked = false;
        //        mobile.RemoveFromTile();
        //        mobile.Clear();
        //        mobile.Next = null;
        //        mobile.Previous = null;
        //        mobile.Name = null;
        //        mobile.ExecuteAnimation = true;
        //        mobile.HitsRequest = HitsRequestStatus.None;

        //        mobile.CalculateRandomIdleTime();
        //    }
        //);

        private static readonly byte[,] _animationIdle =
        {
            {
                (byte)LowAnimationGroup.Fidget1,
                (byte)LowAnimationGroup.Fidget2,
                (byte)LowAnimationGroup.Fidget1
            },
            {
                (byte)HighAnimationGroup.Fidget1,
                (byte)HighAnimationGroup.Fidget2,
                (byte)HighAnimationGroup.Fidget1
            },
            {
                (byte)PeopleAnimationGroup.Fidget1,
                (byte)PeopleAnimationGroup.Fidget2,
                (byte)PeopleAnimationGroup.Fidget3
            }
        };

        private bool _isDead;
        private bool _isSA_Poisoned;

        //
        internal bool _surfaceOverheadCache;
        internal ushort _surfaceOverheadCacheX = ushort.MaxValue;
        internal ushort _surfaceOverheadCacheY = ushort.MaxValue;
        internal int _surfaceOverheadCacheMaxZ = int.MinValue;

        private long _lastAnimationIdleDelay;

        /// <summary>E4: the frame number of World.UpdateMobilesByClock that last updated this mobile (checker).</summary>
        internal int UpdateStamp;

        /// <summary>E10: this mobile's slot in <see cref="MobileTable"/> (its position in <c>World.Mobiles.Values</c>'s enumeration order), kept by the table on add and remove.</summary>
        internal int Ordinal;

        /// <summary>E10: the table this mobile sits in (at most one, under one key); <see cref="Ordinal"/> is its storage address there.</summary>
        internal MobileTable OwnerTable;
        private bool _isAnimationForwardDirection;
        private byte _animationGroup = 0xFF;
        private byte _animationInterval;
        private bool _animationRepeat;
        private ushort _animationRepeateMode = 1;
        private ushort _animationRepeatModeCount = 1;

        public Mobile(World world, uint serial) : base(world, serial)
        {
            LastAnimationChangeTime = Time.Ticks;
            CalculateRandomIdleTime();
        }

        public Mobile(World world) : base(world, 0) { }

        // the walk queue: world state (T1). Read through Steps; changed only through the checked methods below.
        private readonly Deque<Step> _walkSteps = new Deque<Step>(Constants.MAX_STEP_COUNT);

        /// <summary>The walk queue, read-only (T1, Codex audit round 2, #5): changes go through <see cref="AddStep"/>,
        /// <see cref="RemoveFirstStep"/>, <see cref="ReplaceStep"/> and <see cref="ClearStepQueue"/>, which the funnel check sees.</summary>
        public StepQueue Steps => new StepQueue(_walkSteps);

        internal void AddStep(in Step step) { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.AddToBack(step); }

        internal void RemoveFirstStep() { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.RemoveFromFront(); }

        internal void ReplaceStep(int index, in Step step) { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps[index] = step; }

        internal void ClearStepQueue() { FunnelCheck.Write(this, FunnelField.Steps); _walkSteps.Clear(); }
        public bool IsParalyzed => (Flags & Flags.Frozen) != 0;
        public bool IsYellowHits => (Flags & Flags.YellowBar) != 0;
        public bool IsPoisoned =>
            Client.Game.UO.Version >= ClientVersion.CV_7000
                ? _isSA_Poisoned
                : (Flags & Flags.Poisoned) != 0;
        public bool IgnoreCharacters => (Flags & Flags.IgnoreMobiles) != 0;

        public bool IsDead
        {
            get =>
                Graphic == 0x0192
                || Graphic == 0x0193
                || (Graphic >= 0x025F && Graphic <= 0x0260)
                || Graphic == 0x2B6
                || Graphic == 0x02B7
                || _isDead;
            set
            {
                FunnelCheck.Write(this, FunnelField.IsDead);
                _isDead = value;
                PushInputs();
            }
        }

        public bool IsFlying =>
            Client.Game.UO.Version >= ClientVersion.CV_7000 && (Flags & Flags.Poisoned) != 0;

        public virtual bool InWarMode
        {
            get => (Flags & Flags.WarMode) != 0;
            set { }
        }

        public bool IsHuman =>
            Graphic >= 0x0190 && Graphic <= 0x0193
            || Graphic >= 0x00B7 && Graphic <= 0x00BA
            || Graphic >= 0x025D && Graphic <= 0x0260
            || Graphic == 0x029A
            || Graphic == 0x029B
            || Graphic == 0x02B6
            || Graphic == 0x02B7
            || Graphic == 0x03DB
            || Graphic == 0x03DF
            || Graphic == 0x03E2
            || Graphic == 0x02E8
            || Graphic == 0x02E9
            || Graphic == 0x04E5;

        public bool IsGargoyle =>
            Client.Game.UO.Version >= ClientVersion.CV_7000 && Graphic == 0x029A || Graphic == 0x029B;

        public bool IsMounted
        {
            get
            {
                Item it = FindItemByLayer(Layer.Mount);

                if (it != null && !IsDrivingBoat && it.GetGraphicForAnimation() != 0xFFFF)
                {
                    return true;
                }

                return false;
            }
        }

        public bool IsDrivingBoat
        {
            get
            {
                Item it = FindItemByLayer(Layer.Mount);

                return it != null && it.Graphic == 0x3E96;
            }
        }

        protected virtual bool IsWalking => LastStepTime > Time.Ticks - Constants.WALKING_DELAY;

        private byte _animationFrameCount;
        private bool _animationFromServer, _isRenamable;
        private long _lastStepSoundTime;
        public byte AnimationFrameCount { get => _animationFrameCount; set { FunnelCheck.Write(this, FunnelField.AnimationFrameCount); _animationFrameCount = value; } }
        public bool AnimationFromServer { get => _animationFromServer; set { FunnelCheck.Write(this, FunnelField.AnimationFromServer); _animationFromServer = value; } }
        public bool IsRenamable { get => _isRenamable; set { FunnelCheck.Write(this, FunnelField.IsRenamable); _isRenamable = value; } }
        public long LastStepSoundTime { get => _lastStepSoundTime; set { FunnelCheck.Write(this, FunnelField.LastStepSoundTime); _lastStepSoundTime = value; } }

        // W5: these are inputs of the mobile's animation and override hue; every write is pushed to the ECS (MobileInputs)
        private bool _isFemale, _isRunning;
        private NotorietyFlag _notorietyFlag;

        public bool IsFemale
        {
            get => _isFemale;
            set
            {
                FunnelCheck.Write(this, FunnelField.IsFemale);
                _isFemale = value;
                PushInputs();
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                FunnelCheck.Write(this, FunnelField.IsRunning);
                _isRunning = value;
                PushInputs();
            }
        }

        public NotorietyFlag NotorietyFlag
        {
            get => _notorietyFlag;
            set
            {
                FunnelCheck.Write(this, FunnelField.NotorietyFlag);
                _notorietyFlag = value;
                PushInputs();
            }
        }

        internal bool SAPoisoned => _isSA_Poisoned;

        /// <summary>W5: recomputes the mobile's pushed ECS inputs (MobileInputs) after a change to one of them.</summary>
        protected void PushInputs()
        {
            World?.Ecs.PushInputs(this);
        }

        private RaceType _race;
        private CharacterSpeedType _speedMode = CharacterSpeedType.Normal;
        private ushort _stamina, _staminaMax, _mana, _manaMax;
        private int _stepSoundOffset;
        public RaceType Race { get => _race; set { FunnelCheck.Write(this, FunnelField.Race); _race = value; } }
        public CharacterSpeedType SpeedMode { get => _speedMode; set { FunnelCheck.Write(this, FunnelField.SpeedMode); _speedMode = value; } }
        public ushort Stamina { get => _stamina; set { FunnelCheck.Write(this, FunnelField.Stamina); _stamina = value; } }
        public ushort StaminaMax { get => _staminaMax; set { FunnelCheck.Write(this, FunnelField.StaminaMax); _staminaMax = value; } }
        public ushort Mana { get => _mana; set { FunnelCheck.Write(this, FunnelField.Mana); _mana = value; } }
        public ushort ManaMax { get => _manaMax; set { FunnelCheck.Write(this, FunnelField.ManaMax); _manaMax = value; } }
        public int StepSoundOffset { get => _stepSoundOffset; set { FunnelCheck.Write(this, FunnelField.StepSoundOffset); _stepSoundOffset = value; } }
        public string Title = string.Empty;

        public static Mobile Create(World world, uint serial)
        {
            Mobile mobile = new Mobile(world); // _pool.GetOne();
            mobile.Serial = serial;

            return mobile;
        }

        public Item GetSecureTradeBox()
        {
            for (LinkedObject i = Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                if (it.Graphic == 0x1E5E && it.Layer == 0)
                {
                    return it;
                }
            }

            return null;
        }

        public void SetSAPoison(bool value)
        {
            FunnelCheck.Write(this, FunnelField.IsSAPoisoned);
            _isSA_Poisoned = value;
            PushInputs();
        }

        internal void CalculateRandomIdleTime()
        {
            SetIdleDue(NextIdleDue());
        }

        /// <summary>T1 (funnel part 8): the next idle due time, 30 to 60 s from now (one random draw).</summary>
        internal static long NextIdleDue()
        {
            const int TIME = 30000;

            return Time.Ticks + (TIME + RandomHelper.GetValue(0, TIME));
        }

        // the idle timer's write: the due time, then the clock push
        private void SetIdleDue(long due)
        {
            FunnelCheck.Write(this, FunnelField.AnimationState);
            _lastAnimationIdleDelay = due;

            // E4: the idle due time (and, from SetAnimation, the frame due time written just before) reach the ECS clock
            PushClock();
        }

        /// <summary>E4: the idle timer's due time (<c>Update</c> runs SetIdleAnimation when it is before the tick).</summary>
        internal long IdleDue => _lastAnimationIdleDelay;

        /// <summary>E4 (world_update_mobile_system): writes the due times and flags of <see cref="MobileClock"/> after a change to one.</summary>
        protected void PushClock()
        {
            World?.Ecs.PushClock(this);
        }

        /// <summary>E4 checker: every field <see cref="Update"/> can change, so a skipped Update can be shown to be a no-op.</summary>
        internal int UpdateStateHash()
        {
            var h = new HashCode();
            h.Add(AnimIndex);
            h.Add(AnimationFrameCount);
            h.Add(AnimationFromServer);
            h.Add(LastAnimationChangeTime);
            h.Add(_lastAnimationIdleDelay);
            h.Add(_animationGroup);
            h.Add(_animationInterval);
            h.Add(_animationRepeat);
            h.Add(_animationRepeateMode);
            h.Add(_animationRepeatModeCount);
            h.Add(_isAnimationForwardDirection);
            h.Add(X);
            h.Add(Y);
            h.Add(Z);
            h.Add((byte)Direction);
            h.Add(Offset);
            h.Add(Steps.Count);
            h.Add(LastStepTime);
            h.Add(LastStepSoundTime);
            h.Add(StepSoundOffset);
            h.Add((byte)ObjectHandlesStatus);
            h.Add(HitsPercentage);
            h.Add(IsDestroyed);

            return h.ToHashCode();
        }

        public override void Update()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Update();

            if (_lastAnimationIdleDelay < Time.Ticks)
            {
                SetIdleAnimation();
            }

            // T1 (funnel part 7): ProcessAnimation split. The step processing runs first and on its own (its step completion is
            // the CompleteStep operation), then the frame advance (AdvanceAnimation) and, for a finished corpse animation, the
            // removal (RemoveDeadMobile): each its own operation, none nested in another
            ProcessSteps(out byte dir, true);
            ProcessFootstepsSound();
            AdvanceFrame(dir);
        }

        public void ClearSteps()
        {
            ClearStepQueue();
            Offset = Vector3.Zero;
            PushInputs();
        }

        public bool EnqueueStep(int x, int y, sbyte z, Direction direction, bool run)
        {
            GetEndPosition(out int endX, out int endY, out sbyte endZ, out Direction endDir);
            PlanStep(x, y, z, direction, run, endX, endY, endZ, endDir, out StepPlan plan);
            ApplyStep(in plan);

            return plan.Accepted;
        }

        /// <summary>
        /// T1 (funnel part 4): what <see cref="EnqueueStep"/> does, computed without a write. <see cref="ApplyStep"/> performs it.
        /// </summary>
        internal struct StepPlan
        {
            /// <summary>False: the queue is full (EnqueueStep returns false and changes nothing).</summary>
            public bool Accepted;
            /// <summary>True: steps are added (false: the step ends where the queue already ends; nothing changes).</summary>
            public bool Changes;
            /// <summary>The queue was empty: the walk starts (LastStepTime = <see cref="StepTime"/>).</summary>
            public bool StartsWalk;
            /// <summary>The walk starts while the mobile was not walking: SetAnimation(0xFF).</summary>
            public bool SetsAnimation;
            public uint StepTime;
            public int Count;
            public Step S0, S1, S2;
        }

        /// <summary>
        /// T1 (funnel part 4): the pure half of <see cref="EnqueueStep"/>. <paramref name="endX"/>..<paramref name="endDir"/>
        /// are where the queue ends (<see cref="GetEndPosition"/>; an operation that writes the position before the step,
        /// as UpdateGameObject's creation branch did, passes the position it computed for an empty queue). Reads the queue's
        /// length, LastStepTime and Time.Ticks; writes nothing.
        /// </summary>
        internal void PlanStep(int x, int y, sbyte z, Direction direction, bool run, int endX, int endY, sbyte endZ, Direction endDir, out StepPlan plan)
        {
            plan = default;

            if (Steps.Count >= Constants.MAX_STEP_COUNT)
            {
                return;
            }

            plan.Accepted = true;

            if (endX == x && endY == y && endZ == z && endDir == direction)
            {
                return;
            }

            plan.Changes = true;

            if (Steps.Count == 0)
            {
                plan.StartsWalk = true;
                plan.SetsAnimation = !IsWalking;
                plan.StepTime = Time.Ticks;
            }

            Direction moveDir = DirectionHelper.CalculateDirection(endX, endY, x, y);
            Step step = new Step();

            if (moveDir != Direction.NONE)
            {
                if (moveDir != endDir)
                {
                    step.X = endX;
                    step.Y = endY;
                    step.Z = endZ;
                    step.Direction = (byte)moveDir;
                    step.Run = run;
                    AddPlanned(ref plan, step);
                }

                step.X = x;
                step.Y = y;
                step.Z = z;
                step.Direction = (byte)moveDir;
                step.Run = run;
                AddPlanned(ref plan, step);
            }

            if (moveDir != direction)
            {
                step.X = x;
                step.Y = y;
                step.Z = z;
                step.Direction = (byte)direction;
                step.Run = run;
                AddPlanned(ref plan, step);
            }
        }

        private static void AddPlanned(ref StepPlan plan, in Step step)
        {
            switch (plan.Count++)
            {
                case 0: plan.S0 = step; break;
                case 1: plan.S1 = step; break;
                default: plan.S2 = step; break;
            }
        }

        /// <summary>T1 (funnel part 4): the mutation half of <see cref="EnqueueStep"/>, in its old order.</summary>
        internal void ApplyStep(in StepPlan plan)
        {
            if (!plan.Changes)
            {
                return;
            }

            if (plan.StartsWalk)
            {
                if (plan.SetsAnimation)
                {
                    SetAnimation(0xFF);
                }

                LastStepTime = plan.StepTime;
            }

            if (plan.Count > 0) AddStep(plan.S0);
            if (plan.Count > 1) AddStep(plan.S1);
            if (plan.Count > 2) AddStep(plan.S2);

            PushInputs();
        }

        internal void GetEndPosition(out int x, out int y, out sbyte z, out Direction dir)
        {
            if (Steps.Count == 0)
            {
                x = X;
                y = Y;
                z = Z;
                dir = Direction;
            }
            else
            {
                ref readonly Step step = ref Steps.Back();
                x = step.X;
                y = step.Y;
                z = step.Z;
                dir = (Direction)step.Direction;
            }
        }

        public void SetAnimation(
            byte id,
            byte interval = 0,
            byte frameCount = 0,
            ushort repeatCount = 0,
            bool repeat = false,
            bool forward = false,
            bool fromServer = false
        )
        {
            FunnelCheck.Write(this, FunnelField.AnimationState);
            _animationGroup = id;
            AnimIndex = (byte)(forward ? 0 : frameCount);
            _animationInterval = interval;
            AnimationFrameCount = (byte)(forward ? 0 : frameCount);
            _animationRepeateMode = repeatCount;
            _animationRepeatModeCount = repeatCount;
            _animationRepeat = repeat;
            _isAnimationForwardDirection = forward;
            AnimationFromServer = fromServer;
            LastAnimationChangeTime = Time.Ticks;

            CalculateRandomIdleTime();
        }

        /// <summary>
        /// T1 (funnel part 8): what <see cref="SetIdleAnimation"/> does, computed before any write. The random draws come in
        /// the old order: the idle delay first, then the group's (a gargoyle's flight, the idle pair); a
        /// <see cref="IdleGroup.SetAnimation"/> outcome draws its own idle delay inside <c>SetAnimation</c>, last, as before.
        /// </summary>
        internal struct IdlePlan
        {
            /// <summary>The idle timer's next due time (<see cref="NextIdleDue"/>).</summary>
            public long IdleDue;
            /// <summary>Not mounted, not in war mode and animating: the timer fields are reset to an idle animation.</summary>
            public bool Resets;
            /// <summary>What happens to the animation group after the reset.</summary>
            public IdleGroup Group;
            /// <summary>The group (<see cref="IdleGroup.Set"/>) or <c>SetAnimation</c>'s id (<see cref="IdleGroup.SetAnimation"/>).</summary>
            public byte Value;
        }

        internal enum IdleGroup : byte
        {
            /// <summary>The group is not touched (no idle animation for this body).</summary>
            None,
            /// <summary>The group becomes <see cref="IdlePlan.Value"/>.</summary>
            Set,
            /// <summary>Neither idle action exists: <c>SetAnimation(Value)</c>, the group it had.</summary>
            SetAnimation
        }

        public void SetIdleAnimation()
        {
            // T1 (funnel part 8): the plan from the animation data, then its writes through the funnel
            PlanIdleAnimation(new LiveIdleAnimationData(Client.Game.UO.Animations), out IdlePlan plan);
            World.Writer.IdleAnimation(this, in plan);
        }

        /// <summary>T1 (funnel part 8): the animation data <see cref="PlanIdleAnimation"/> reads (tests pass their own).</summary>
        internal interface IIdleAnimationData
        {
            int MaxAnimationCount { get; }
            AnimationGroupsType GetAnimType(ushort graphic);
            AnimationFlags GetAnimFlags(ushort graphic);
            bool AnimationExists(ushort graphic, byte group);
            byte GetReplacedObjectAnimation(ushort graphic, ushort index);
        }

        private readonly struct LiveIdleAnimationData : IIdleAnimationData
        {
            private readonly ClassicUO.Renderer.Animations.Animations _animations;

            public LiveIdleAnimationData(ClassicUO.Renderer.Animations.Animations animations) => _animations = animations;

            public int MaxAnimationCount => _animations.MaxAnimationCount;
            public AnimationGroupsType GetAnimType(ushort graphic) => _animations.GetAnimType(graphic);
            public AnimationFlags GetAnimFlags(ushort graphic) => _animations.GetAnimFlags(graphic);
            public bool AnimationExists(ushort graphic, byte group) => _animations.AnimationExists(graphic, group);
            public byte GetReplacedObjectAnimation(ushort graphic, ushort index) => Mobile.GetReplacedObjectAnimation(graphic, index);
        }

        /// <summary>T1 (funnel part 8): the mutation half of <see cref="SetIdleAnimation"/>, in its old order.</summary>
        internal void ApplyIdle(in IdlePlan plan)
        {
            SetIdleDue(plan.IdleDue);

            if (!plan.Resets)
            {
                return;
            }

            FunnelCheck.Write(this, FunnelField.AnimationState);
            AnimIndex = 0;
            AnimationFrameCount = 0;
            _animationInterval = 1;
            _animationRepeateMode = 1;
            _animationRepeatModeCount = 1;
            _isAnimationForwardDirection = true;
            _animationRepeat = false;
            AnimationFromServer = true;

            if (plan.Group == IdleGroup.Set)
            {
                _animationGroup = plan.Value;
            }
            else if (plan.Group == IdleGroup.SetAnimation)
            {
                SetAnimation(plan.Value);
            }
        }

        /// <summary>
        /// T1 (funnel part 8): the pure half of <see cref="SetIdleAnimation"/>: reads the mobile and the animation data, draws
        /// the random numbers, writes nothing. The old code read nothing it had written before these reads (the reset writes
        /// the timer fields; the body, the flags and the group are read).
        /// </summary>
        internal void PlanIdleAnimation<TData>(in TData animations, out IdlePlan plan) where TData : struct, IIdleAnimationData
        {
            plan = default;
            plan.IdleDue = NextIdleDue();

            if (!IsMounted && !InWarMode && ExecuteAnimation)
            {
                plan.Resets = true;

                ushort graphic = GetGraphicForAnimation();

                if (graphic >= animations.MaxAnimationCount)
                {
                    return;
                }

                //byte action = 0;
                //ushort hue = 0;

                //Client.Game.UO.Animations.ReplaceAnimationValues(
                //    ref graphic,
                //    ref action,
                //    ref hue,
                //    out var useUOP
                //);

                AnimationGroupsType type = animations.GetAnimType(graphic);
                AnimationFlags  flags = animations.GetAnimFlags(graphic);
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

                plan.Group = IdleGroup.Set;

                if ((flags & AnimationFlags.UseUopAnimation) != 0)
                {
                    if (animGroup != AnimationGroups.People)
                    {
                        plan.Value = (byte)(InWarMode ? 28 : 26);

                        return;
                    }

                    if (IsGargoyle && IsFlying)
                    {
                        plan.Value = (byte)(RandomHelper.GetValue(0, 2) != 0 ? 66 : 67);

                        return;
                    }
                }

                int first_value = RandomHelper.GetValue(0, 2);

                // the reset does not write the group, so this is the group before SetIdleAnimation, as the old read was
                byte original_value = _animationGroup;

                byte group = _animationIdle[(byte)animGroup - 1, first_value];

                if (isLowExtended && group == 18)
                {
                    if (
                        !animations.AnimationExists(graphic, 18)
                        && animations.AnimationExists(graphic, 17)
                    )
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
                    if (first_value == 0)
                    {
                        first_value = 1;
                    }
                    else
                    {
                        first_value = 0;
                    }

                    group = _animationIdle[(byte)animGroup - 1, first_value];

                    if (!animations.AnimationExists(graphic, group))
                    {
                        // SetAnimation overwrites the group the old code had written there: only its own writes remain
                        plan.Group = IdleGroup.SetAnimation;
                        group = original_value;
                    }
                }

                plan.Value = group;
            }
        }

        private bool NoIterateAnimIndex()
        {
            return !ExecuteAnimation
                || (LastStepTime > Time.Ticks - Constants.WALKING_DELAY && Steps.Count == 0);
        }

        private void ProcessFootstepsSound()
        {
            if (
                ProfileManager.CurrentProfile.EnableFootstepsSound
                && IsHuman
                && !IsHidden
                && !IsDead
                && !IsFlying
            )
            {
                if (Steps.Count != 0 && LastStepSoundTime < Time.Ticks)
                {
                    ref readonly Step step = ref Steps.Back();

                    // T1 (funnel part 8): the values first, then the two writes through the funnel (StepSound), then the
                    // sound, which reads neither field (it played between the two writes before)
                    int incID = StepSoundOffset;
                    int soundID = 0x012B;
                    int delaySound = 400;

                    if (IsMounted)
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

                    if (World.Writer.StepSound(this, (incID + 1) % 2, Time.Ticks + delaySound))
                    {
                        Client.Game.Audio.PlaySoundWithDistance(World, soundID, stepX, stepY);
                    }
                }
            }
        }

        public override void ProcessAnimation(bool evalutate = false)
        {
            ProcessSteps(out var dir, evalutate);
            ProcessFootstepsSound();
            AdvanceFrame(dir);
        }

        // the frame advance (T1 funnel part 7): the frame count comes from the animation data here; the timer step, its writes
        // and the frame's due time are the AdvanceAnimation operation, and a finished corpse animation (a death-rekeyed
        // mobile whose animation wrapped, or that has none) is then removed by the RemoveDeadMobile operation, which writes
        // the frame index and the due time after the removal, where the old code wrote them
        private void AdvanceFrame(byte dir)
        {
            if (LastAnimationChangeTime >= Time.Ticks || NoIterateAnimIndex())
            {
                return;
            }

            var animations = Client.Game.UO.Animations;

            ushort id = GetGraphicForAnimation();
            byte action = GetGroupForAnimation(this, id, true);

            bool mirror = false;
            animations.GetAnimDirection(ref dir, ref mirror);
            int frameCount = 0;

            if (id < animations.MaxAnimationCount && dir < 5)
            {
                frameCount = animations.GetAnimationFrames(id, action, dir, out _, out _).Length;
            }

            if (World.Writer.AdvanceAnimation(this, frameCount, out int frame, out long due))
            {
                World.Writer.RemoveDeadMobile(this, frame, due);
            }
        }

        /// <summary>T1 (funnel part 7): the animation timer as <c>AnimationTiming.Advance</c> takes it (a pure read).</summary>
        internal AnimationTimer AnimationTimerState => new AnimationTimer
        {
            Frame = AnimIndex,
            FrameCount = AnimationFrameCount,
            Interval = _animationInterval,
            RepeatMode = _animationRepeateMode,
            RepeatModeCount = _animationRepeatModeCount,
            Repeat = _animationRepeat,
            Forward = _isAnimationForwardDirection,
            FromServer = AnimationFromServer
        };

        /// <summary>
        /// T1 (funnel part 7): the timer fields an advance changes (the frame count and the repeat state; the frame index is
        /// written by the caller, after a finished animation's reset). Called by <see cref="WorldWriter.AdvanceAnimation"/>.
        /// </summary>
        internal void ApplyAnimationTimer(in AnimationTimer timer)
        {
            AnimationFrameCount = timer.FrameCount;
            FunnelCheck.Write(this, FunnelField.AnimationState);
            _animationRepeateMode = timer.RepeatMode;
            _animationRepeatModeCount = timer.RepeatModeCount;
            _animationRepeat = timer.Repeat;
        }

        public void ProcessSteps(out byte dir, bool evalutate = false)
        {
            dir = (byte)Direction;
            dir &= 7;

            if (Steps.Count != 0 && !IsDestroyed)
            {
                ref readonly Step step = ref Steps.Front();
                dir = step.Direction;

                if (step.Run)
                {
                    dir &= 7;
                }

                if (evalutate)
                {
                    if (AnimationFromServer)
                    {
                        SetAnimation(0xFF);
                    }

                    int delay = (int)Time.Ticks - (int)LastStepTime;
                    bool mounted =
                        IsMounted
                        || SpeedMode == CharacterSpeedType.FastUnmount
                        || SpeedMode == CharacterSpeedType.FastUnmountAndCantRun
                        || IsFlying;
                    bool run = step.Run;

                    // Client auto movements sync.
                    // When server sends more than 1 packet in an amount of time less than 100ms if mounted (or 200ms if walking mount)
                    // we need to remove the "teleport" effect.
                    // When delay == 0 means that we received multiple movement packets in a single frame, so the patch becomes quite useless.
                    if (!mounted && Serial != World.Player && Steps.Count > 1 && delay > 0)
                    {
                        mounted =
                            delay
                            <= (
                                run
                                    ? MovementSpeed.STEP_DELAY_MOUNT_RUN
                                    : MovementSpeed.STEP_DELAY_MOUNT_WALK
                            );
                    }

                    int maxDelay =
                        MovementSpeed.TimeToCompleteMovement(run, mounted)
                        - (int)Client.Game.FrameDelay[1];

                    bool removeStep = delay >= maxDelay;
                    bool directionChange = false;

                    if (X != step.X || Y != step.Y)
                    {
                        bool badStep = false;

                        if (Offset.X == 0 && Offset.Y == 0)
                        {
                            int absX = Math.Abs(X - step.X);
                            int absY = Math.Abs(Y - step.Y);

                            badStep = absX > 1 || absY > 1 || absX + absY == 0;

                            if (!badStep)
                            {
                                absX = X;
                                absY = Y;

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
                            sbyte offsetZ = (sbyte)((step.Z - Z) * x * (4.0f / steps));
                            MovementSpeed.GetPixelOffset(step.Direction, ref x, ref y, steps);

                            // T1 (funnel part 7, group 4): the walk offset through the funnel
                            World.Writer.InterpolateStep(this, (sbyte)x, (sbyte)y, offsetZ);
                        }
                    }
                    else
                    {
                        directionChange = true;
                        removeStep = true;
                    }

                    if (removeStep)
                    {
                        if (Serial == World.Player)
                        {
                            //if (Position.X != step.X || Position.Y != step.Y || Position.Z != step.Z)
                            //{
                            //}

                            if (Z - step.Z >= 22)
                            {
                                // oUCH!!!!
                                AddMessage(MessageType.Label, ResGeneral.Ouch, TextType.CLIENT);
                            }

                            if (
                                World.Player.Walker.StepInfos[
                                    World.Player.Walker.CurrentWalkSequence
                                ].Accepted
                            )
                            {
                                int sequence = World.Player.Walker.CurrentWalkSequence + 1;

                                if (sequence < World.Player.Walker.StepsCount)
                                {
                                    int count = World.Player.Walker.StepsCount - sequence;

                                    for (int i = 0; i < count; i++)
                                    {
                                        World.Player.Walker.StepInfos[sequence - 1] = World
                                            .Player
                                            .Walker
                                            .StepInfos[sequence];

                                        sequence++;
                                    }
                                }

                                World.Player.Walker.StepsCount--;
                            }
                            else
                            {
                                World.Player.Walker.CurrentWalkSequence++;
                            }
                        }

                        // T1 (funnel part 5): the position, direction, run state, offset, queue, idle timer, then (not for a
                        // turn in place) the screen position, the player's ranged gumps, the tile and LastStepTime, through
                        // the funnel. A copy of the step: the operation removes it from the queue.
                        Step done = step;
                        World.Writer.CompleteStep(this, in done, directionChange);

                        if (directionChange)
                        {
                            ProcessSteps(out dir, evalutate);

                            return;
                        }
                    }

                    UpdateTextCoordsV();
                }
            }
        }

        public bool TryGetSittingInfo(out AnimationsLoader.SittingInfoData data)
        {
            return TryGetSittingInfoIf(
                IsHuman
                && !IsMounted
                && !IsFlying
                && !TestStepNoChangeDirection(this, GetGroupForAnimation(this, isParent: true)),
                out data
            );
        }

        /// <summary>W5 (mobile_push_inputs): <see cref="TryGetSittingInfo(out AnimationsLoader.SittingInfoData)"/> with the
        /// human, mounted, flying and action inputs already known from the ECS: the seat test without its equipment walks.</summary>
        internal bool TryGetSittingInfo(bool human, bool mounted, bool flying, byte group, out AnimationsLoader.SittingInfoData data)
        {
            return TryGetSittingInfoIf(human && !mounted && !flying && !TestStepNoChangeDirection(this, group), out data);
        }

        private bool TryGetSittingInfoIf(bool eligible, out AnimationsLoader.SittingInfoData data)
        {
            ushort result = 0;

            if (eligible)
            {
                GameObject start = this;

                while (start?.TPrevious != null)
                {
                    start = start.TPrevious;
                }

                while (start != null && result == 0)
                {
                    if (
                        (start is Item || start is Static || start is Multi)
                        && Math.Abs(Z - start.Z) <= 1
                    )
                    {
                        if (ChairTable.Table.TryGetValue(start.Graphic, out data))
                        {
                            return true;
                        }
                    }

                    start = (GameObject)start.TNext;
                }
            }

            data = AnimationsLoader.SittingInfoData.Empty;
            return false;
        }

        public override void UpdateTextCoordsV()
        {
            if (TextContainer == null)
            {
                return;
            }

            TextObject last = (TextObject)TextContainer.Items;

            while (last?.Next != null)
            {
                last = (TextObject)last.Next;
            }

            if (last == null)
            {
                return;
            }

            int offY = 0;

            bool health = ProfileManager.CurrentProfile.ShowMobilesHP;
            int alwaysHP = ProfileManager.CurrentProfile.MobileHPShowWhen;
            int mode = ProfileManager.CurrentProfile.MobileHPType;

            Point p = RealScreenPosition;

            if (IsGargoyle && IsFlying)
            {
                p.Y -= 22;
            }
            else if (!IsMounted)
            {
                p.Y += 22;
            }

            Client.Game.UO.Animations.GetAnimationDimensions(
                AnimIndex,
                GetGraphicForAnimation(),
                /*(byte) m.GetDirectionForAnimation()*/
                0,
                /*Mobile.GetGroupForAnimation(m, isParent:true)*/
                0,
                IsMounted,
                /*(byte) m.AnimIndex*/
                0,
                out _,
                out int centerY,
                out _,
                out int height
            );

            p.X += (int)Offset.X + 22;
            p.Y += (int)(Offset.Y - Offset.Z - (height + centerY + 8));
            p = Client.Game.Scene.Camera.WorldToScreen(p, true);

            if (ObjectHandlesStatus == ObjectHandlesStatus.DISPLAYING)
            {
                p.Y -= Constants.OBJECT_HANDLES_GUMP_HEIGHT
                    + (ProfileManager.CurrentProfile.NameOverheadShowHpBar
                        ? Constants.OBJECT_HANDLES_HP_BAR_HEIGHT + 1 : 0);
            }

            if (
                health
                && HitsTexture != null
                && mode != 1
                && (alwaysHP >= 1 && Hits != HitsMax || alwaysHP == 0)
            )
            {
                p.Y -= HitsTexture.Height;
            }

            for (; last != null; last = (TextObject)last.Previous)
            {
                if (last.RenderedText != null && !last.RenderedText.IsDestroyed)
                {
                    if (offY == 0 && last.Time < Time.Ticks)
                    {
                        continue;
                    }

                    last.OffsetY = offY;
                    offY += last.RenderedText.Height;

                    last.RealScreenPosition.X = p.X - (last.RenderedText.Width >> 1);
                    last.RealScreenPosition.Y = p.Y - offY;
                }
            }

            FixTextCoordinatesInScreen();
        }

        public override void CheckGraphicChange(byte animIndex = 0)
        {
            if (BodyState(Graphic, out bool isFemale, out RaceType race))
            {
                IsFemale = isFemale;
                Race = race;
            }
        }

        /// <summary>
        /// T1 (funnel part 4): the sex and race <see cref="CheckGraphicChange"/> stores for a body graphic (pure; the funnel
        /// writes the result itself). False: a body that changes neither.
        /// </summary>
        internal static bool BodyState(ushort graphic, out bool isFemale, out RaceType race)
        {
            switch (graphic)
            {
                case 0x0190:
                case 0x0192: isFemale = false; race = RaceType.HUMAN; return true;
                case 0x0191:
                case 0x0193: isFemale = true; race = RaceType.HUMAN; return true;
                case 0x025D: isFemale = false; race = RaceType.ELF; return true;
                case 0x025E: isFemale = true; race = RaceType.ELF; return true;
                case 0x029A: isFemale = false; race = RaceType.GARGOYLE; return true;
                case 0x029B: isFemale = true; race = RaceType.GARGOYLE; return true;
                default: isFemale = false; race = 0; return false;
            }
        }

        public override void Destroy()
        {
            uint serial = Serial & 0x3FFFFFFF;

            ClearSteps();

            base.Destroy();

            if (!(this is PlayerMobile))
            {
                UIManager.GetGump<PaperDollGump>(serial)?.Dispose();

                //_pool.ReturnOne(this);
            }
        }

        internal struct Step
        {
            public int X,
                Y;
            public sbyte Z;
            public byte Direction;
            public bool Run;
        }

        /// <summary>A read-only view of a mobile's walk queue (its steps come back by readonly reference or by value).</summary>
        internal readonly struct StepQueue
        {
            private readonly Deque<Step> _queue;

            internal StepQueue(Deque<Step> steps) => _queue = steps;

            public int Count => _queue.Count;

            public Step this[int index] => _queue[index];

            public ref readonly Step Front() => ref _queue.Front();

            public ref readonly Step Back() => ref _queue.Back();
        }
    }
}
