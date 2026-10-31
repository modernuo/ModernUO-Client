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

        private void CalculateRandomIdleTime()
        {
            const int TIME = 30000;
            FunnelCheck.Write(this, FunnelField.AnimationState);
            _lastAnimationIdleDelay = Time.Ticks + (TIME + RandomHelper.GetValue(0, TIME));

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

            ProcessAnimation(true);
        }

        public void ClearSteps()
        {
            ClearStepQueue();
            Offset = Vector3.Zero;
            PushInputs();
        }

        public bool EnqueueStep(int x, int y, sbyte z, Direction direction, bool run)
        {
            if (Steps.Count >= Constants.MAX_STEP_COUNT)
            {
                return false;
            }

            GetEndPosition(out int endX, out int endY, out sbyte endZ, out Direction endDir);

            if (endX == x && endY == y && endZ == z && endDir == direction)
            {
                return true;
            }

            if (Steps.Count == 0)
            {
                if (!IsWalking)
                {
                    SetAnimation(0xFF);
                }

                LastStepTime = Time.Ticks;
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
                    AddStep(step);
                }

                step.X = x;
                step.Y = y;
                step.Z = z;
                step.Direction = (byte)moveDir;
                step.Run = run;
                AddStep(step);
            }

            if (moveDir != direction)
            {
                step.X = x;
                step.Y = y;
                step.Z = z;
                step.Direction = (byte)direction;
                step.Run = run;
                AddStep(step);
            }

            PushInputs();

            return true;
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

        public void SetIdleAnimation()
        {
            CalculateRandomIdleTime();

            if (!IsMounted && !InWarMode && ExecuteAnimation)
            {
                FunnelCheck.Write(this, FunnelField.AnimationState);
                AnimIndex = 0;
                AnimationFrameCount = 0;
                _animationInterval = 1;
                _animationRepeateMode = 1;
                _animationRepeatModeCount = 1;
                _isAnimationForwardDirection = true;
                _animationRepeat = false;
                AnimationFromServer = true;

                ushort graphic = GetGraphicForAnimation();

                var animations = Client.Game.UO.Animations;
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

                if ((flags & AnimationFlags.UseUopAnimation) != 0)
                {
                    if (animGroup != AnimationGroups.People)
                    {
                        if (InWarMode)
                        {
                            _animationGroup = 28;
                        }
                        else
                        {
                            _animationGroup = 26;
                        }

                        return;
                    }

                    if (IsGargoyle && IsFlying)
                    {
                        if (RandomHelper.GetValue(0, 2) != 0)
                        {
                            _animationGroup = 66;
                        }
                        else
                        {
                            _animationGroup = 67;
                        }

                        return;
                    }
                }

                int first_value = RandomHelper.GetValue(0, 2);

                byte original_value = _animationGroup;

                _animationGroup = _animationIdle[(byte)animGroup - 1, first_value];

                if (isLowExtended && _animationGroup == 18)
                {
                    if (
                        !animations.AnimationExists(graphic, 18)
                        && animations.AnimationExists(graphic, 17)
                    )
                    {
                        _animationGroup = GetReplacedObjectAnimation(graphic, 17);
                    }
                    else
                    {
                        _animationGroup = 1;
                    }
                }

                if (!animations.AnimationExists(graphic, _animationGroup))
                {
                    if (first_value == 0)
                    {
                        first_value = 1;
                    }
                    else
                    {
                        first_value = 0;
                    }

                    _animationGroup = _animationIdle[(byte)animGroup - 1, first_value];

                    if (!animations.AnimationExists(graphic, _animationGroup))
                    {
                        SetAnimation(original_value);
                    }
                }
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

                    StepSoundOffset = (incID + 1) % 2;

                    Client.Game.Audio.PlaySoundWithDistance(World, soundID, step.X, step.Y);
                    LastStepSoundTime = Time.Ticks + delaySound;
                }
            }
        }

        public override void ProcessAnimation(bool evalutate = false)
        {
            ProcessSteps(out var dir, evalutate);
            ProcessFootstepsSound();

            if (LastAnimationChangeTime >= Time.Ticks || NoIterateAnimIndex())
            {
                return;
            }

            var animations = Client.Game.UO.Animations;

            ushort id = GetGraphicForAnimation();
            byte action = GetGroupForAnimation(this, id, true);

            bool mirror = false;
            animations.GetAnimDirection(ref dir, ref mirror);
            int currentDelay = Constants.CHARACTER_ANIMATION_DELAY;

            if (id < animations.MaxAnimationCount && dir < 5)
            {
                var frames = animations.GetAnimationFrames(
                    id,
                    action,
                    dir,
                    out _,
                    out _
                );

                if (frames.Length != 0)
                {
                    // the frame step is a shared pure rule (AnimationTiming) so the ECS can run it too
                    var timer = new AnimationTimer
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

                    currentDelay = AnimationTiming.Advance(ref timer, frames.Length, out AnimationAdvance advance);

                    AnimationFrameCount = timer.FrameCount;
                    FunnelCheck.Write(this, FunnelField.AnimationState);
                    _animationRepeateMode = timer.RepeatMode;
                    _animationRepeatModeCount = timer.RepeatModeCount;
                    _animationRepeat = timer.Repeat;

                    if (advance == AnimationAdvance.Finished)
                    {
                        SetAnimation(0xFF);
                    }
                    else if (advance == AnimationAdvance.Wrapped && (Serial & 0x80000000) != 0)
                    {
                        World.CorpseManager.Remove(0, Serial);
                        World.RemoveMobile(Serial);
                    }

                    AnimIndex = timer.Frame;
                }
                else if ((Serial & 0x80000000) != 0)
                {
                    World.CorpseManager.Remove(0, Serial);
                    World.RemoveMobile(Serial);
                }
            }
            else if ((Serial & 0x80000000) != 0)
            {
                World.CorpseManager.Remove(0, Serial);
                World.RemoveMobile(Serial);
            }

            LastAnimationChangeTime = Time.Ticks + currentDelay;
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
                            OffsetZ = (sbyte)((step.Z - Z) * x * (4.0f / steps));
                            MovementSpeed.GetPixelOffset(step.Direction, ref x, ref y, steps);
                            OffsetX = (sbyte)x;
                            OffsetY = (sbyte)y;
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

                        X = (ushort)step.X;
                        Y = (ushort)step.Y;
                        Z = step.Z;

                        Direction = (Direction)step.Direction;
                        IsRunning = step.Run;
                        OffsetX = 0;
                        OffsetY = 0;
                        OffsetZ = 0;
                        RemoveFirstStep();
                        CalculateRandomIdleTime();
                        PushInputs();

                        if (directionChange)
                        {
                            ProcessSteps(out dir, evalutate);

                            return;
                        }

                        UpdateScreenPosition();

                        if (World.InGame && Serial == World.Player)
                        {
                            World.Player.CloseRangedGumps();
                        }

                        if (IsPlaced)
                        {
                            AddToTile();
                        }

                        LastStepTime = Time.Ticks;
                        PushInputs();
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
            switch (Graphic)
            {
                case 0x0190:
                case 0x0192:
                {
                    IsFemale = false;
                    Race = RaceType.HUMAN;

                    break;
                }

                case 0x0191:
                case 0x0193:
                {
                    IsFemale = true;
                    Race = RaceType.HUMAN;

                    break;
                }

                case 0x025D:
                {
                    IsFemale = false;
                    Race = RaceType.ELF;

                    break;
                }

                case 0x025E:
                {
                    IsFemale = true;
                    Race = RaceType.ELF;

                    break;
                }

                case 0x029A:
                {
                    IsFemale = false;
                    Race = RaceType.GARGOYLE;

                    break;
                }

                case 0x029B:
                {
                    IsFemale = true;
                    Race = RaceType.GARGOYLE;

                    break;
                }
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
