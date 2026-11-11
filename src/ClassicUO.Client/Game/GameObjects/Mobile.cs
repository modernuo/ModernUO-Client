// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
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
        /// <summary>W5: the Gpu world's reused selection and sprite instances (MobileFrameCache).</summary>
        internal MobileFrameCache FrameCache;

        /// <summary>T3a: the equipment read cache (WorldState.WornItem), made at the first read.</summary>
        internal WornCache EquipCache;

        /// <summary>T9 child-list slice A: a detached preview mobile's clothes (character creation); null for every other mobile.</summary>
        internal PreviewEquipment Preview;

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

        internal static readonly byte[,] _animationIdle =
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

        /// <summary>E10: this mobile's slot in <see cref="MobileTable"/> (its position in <c>World.Mobiles.Values</c>'s enumeration order), kept by the table on add and remove.</summary>
        internal int Ordinal;

        /// <summary>E10: the table this mobile sits in (at most one, under one key); <see cref="Ordinal"/> is its storage address there.</summary>
        internal MobileTable OwnerTable;

        /// <summary>T9 slice 3: the key <see cref="OwnerTable"/> holds this mobile under (its serial, or the old one during a
        /// death re-key), kept by the table, so <c>MobileTable.Holds</c> answers without a lookup.</summary>
        internal uint TableKey;

        // T9 S8c: the PlayerMobile's timer start (its due time now, an idle delay drawn) is written when its record is created
        // (WorldEcs.InitCreatedMobile), the record being the timer's only copy
        public Mobile(World world, uint serial) : base(world, serial) { }

        public Mobile(World world) : base(world, 0) { }

        // ---- T9 S8c: the animation timer (AnimationTimer), the walk queue, its step time and walk offset (Steps) and the
        // clock (MobileClock) live only in the record (Entity.TryRecord: the world mirror's, or a preview's), which has them
        // from its creation. The getters read it; without one (a destroyed or stale instance, the detached player) they read
        // a new mobile's values (AnimationTimer.MobileDefault, an empty queue) and the writes below are dropped. Only
        // WorldWriter's operations call the writes; each keeps the record's MobileClock.

        /// <summary>T9 S8c: the record's animation timer (a copy; a new mobile's without a record).</summary>
        internal ClassicUO.Ecs.AnimationTimer Timer =>
            TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e) : ClassicUO.Ecs.AnimationTimer.MobileDefault;

        /// <summary>The walk queue, read-only (T1, Codex audit round 2, #5): changes go through <see cref="AddStep"/>,
        /// <see cref="RemoveFirstStep"/>, <see cref="ReplaceStep"/> and <see cref="ClearStepQueue"/>, which the funnel check
        /// sees. T9 S8c: a view of the record's <see cref="ClassicUO.Ecs.Steps"/> (empty without a record).</summary>
        public StepQueue Steps => new StepQueue(TryRecord(out Friflo.Engine.ECS.Entity e) ? e : default);

        public byte AnimIndex => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e).Frame : (byte)0;

        public bool ExecuteAnimation => !TryRecord(out Friflo.Engine.ECS.Entity e) || ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e).ExecuteAnimation;

        internal long LastAnimationChangeTime => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e).AnimDue : 0;

        /// <summary>E4: the idle timer's due time (the mobile update plans the idle animation when it is before the tick).</summary>
        internal long IdleDue => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e).IdleDue : 0;

        public byte AnimationFrameCount => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e).FrameCount : (byte)0;

        public bool AnimationFromServer => TryRecord(out Friflo.Engine.ECS.Entity e) && ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e).FromServer;

        public uint LastStepTime => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e).LastStepTime : 0;

        /// <summary>The walk offset (T9 S7 kept it on the Mobile; S8c: the record's <see cref="ClassicUO.Ecs.Steps"/>).</summary>
        public Vector3 Offset
        {
            get
            {
                if (!TryRecord(out Friflo.Engine.ECS.Entity e))
                {
                    return default;
                }

                ref readonly ClassicUO.Ecs.Steps s = ref ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e);

                return new Vector3(s.OffsetX, s.OffsetY, s.OffsetZ);
            }
        }

        public float OffsetX => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e).OffsetX : 0;

        public float OffsetY => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e).OffsetY : 0;

        public float OffsetZ => TryRecord(out Friflo.Engine.ECS.Entity e) ? ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e).OffsetZ : 0;

        /// <summary>T9 S8c: writes the record's timer and its clock's due times (dropped without a record).</summary>
        internal void WriteTimer(in ClassicUO.Ecs.AnimationTimer t)
        {
            FunnelCheck.Write(this, FunnelField.AnimationState);

            if (TryOwnRecord(out Friflo.Engine.ECS.Entity e))
            {
                ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.AnimationTimer>(e) = t;
                WorldEcs.WriteClock(this, e);
            }
        }

        // the queue writes: the ring kept from slot 0 (Head 0), unused slots default, then the clock's Stepping flag
        private bool TrySteps(out Friflo.Engine.ECS.Entity e)
        {
            FunnelCheck.Write(this, FunnelField.Steps);

            return TryOwnRecord(out e);
        }

        internal void AddStep(in Step step)
        {
            if (!TrySteps(out Friflo.Engine.ECS.Entity e))
            {
                return;
            }

            ref ClassicUO.Ecs.Steps s = ref ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e);

            // unreachable: PlanStep accepts a packet while fewer than MAX_STEP_COUNT are queued (Steps.CAPACITY asserts it)
            if (s.Count >= ClassicUO.Ecs.Steps.CAPACITY)
            {
                return;
            }

            s.Ring[s.Count++] = ToEntry(in step);
            WorldEcs.WriteClock(this, e);
        }

        internal void RemoveFirstStep()
        {
            if (!TrySteps(out Friflo.Engine.ECS.Entity e))
            {
                return;
            }

            ref ClassicUO.Ecs.Steps s = ref ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e);

            if (s.Count == 0)
            {
                return;
            }

            for (int k = 1; k < s.Count; k++)
            {
                s.Ring[k - 1] = s.Ring[k];
            }

            s.Ring[--s.Count] = default;
            WorldEcs.WriteClock(this, e);
        }

        internal void ReplaceStep(int index, in Step step)
        {
            if (TrySteps(out Friflo.Engine.ECS.Entity e))
            {
                ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e).Ring[index] = ToEntry(in step);
            }
        }

        internal void ClearStepQueue()
        {
            if (!TrySteps(out Friflo.Engine.ECS.Entity e))
            {
                return;
            }

            ref ClassicUO.Ecs.Steps s = ref ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e);
            s.Ring = default;
            s.Head = 0;
            s.Count = 0;
            WorldEcs.WriteClock(this, e);
        }

        internal void SetLastStepTime(uint time)
        {
            FunnelCheck.Write(this, FunnelField.LastStepTime);

            if (TryOwnRecord(out Friflo.Engine.ECS.Entity e))
            {
                ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e).LastStepTime = time;
            }
        }

        /// <summary>The walk offset (the step interpolation, its reset, a boat carrying the mobile).</summary>
        internal void SetOffset(float x, float y, float z)
        {
            FunnelCheck.Write(this, FunnelField.Offset);

            if (TryOwnRecord(out Friflo.Engine.ECS.Entity e))
            {
                ref ClassicUO.Ecs.Steps s = ref ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(e);
                s.OffsetX = x;
                s.OffsetY = y;
                s.OffsetZ = z;
            }
        }

        internal void SetOffset(Vector3 offset) => SetOffset(offset.X, offset.Y, offset.Z);

        /// <summary>The frame index (0x6E/0xE2's frame, the advance's).</summary>
        internal void SetFrame(byte frame)
        {
            ClassicUO.Ecs.AnimationTimer t = Timer;
            t.Frame = frame;
            WriteTimer(in t);
        }

        /// <summary>The next frame's due time (the advance's).</summary>
        internal void SetAnimDue(long due)
        {
            ClassicUO.Ecs.AnimationTimer t = Timer;
            t.AnimDue = due;
            WriteTimer(in t);
        }

        internal void SetExecuteAnimation(bool on)
        {
            ClassicUO.Ecs.AnimationTimer t = Timer;
            t.ExecuteAnimation = on;
            WriteTimer(in t);
        }

        private static ClassicUO.Ecs.StepEntry ToEntry(in Step s) =>
            new ClassicUO.Ecs.StepEntry { X = s.X, Y = s.Y, Z = s.Z, Direction = s.Direction, Run = s.Run };
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

        // T6c: false without a client (unit tests run ProcessSteps, which reads it), as WorldEcs.Cv7000
        public bool IsFlying =>
            Client.Game?.UO != null && Client.Game.UO.Version >= ClientVersion.CV_7000 && (Flags & Flags.Poisoned) != 0;

        public virtual bool InWarMode => (Flags & Flags.WarMode) != 0;

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
                // one equipment walk: IsDrivingBoat would look the same Mount-layer item up again
                Item it = FindItemByLayer(Layer.Mount);

                return it != null && it.Graphic != 0x3E96 && it.GetGraphicForAnimation() != 0xFFFF;
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

        private long _lastStepSoundTime;
        // T9 S5: the traits (IsRenamable, IsFemale, Race, SpeedMode, the player's InWarMode) live only in the record (the world
        // mirror's or a preview's: Entity.TryRecord); WorldWriter's operations commit them. Without one: the defaults
        public bool IsRenamable => TryRecord(out Friflo.Engine.ECS.Entity e) && EcsScalars.MobileBit(e, ClassicUO.Ecs.MobileTraitBits.Renamable);
        public long LastStepSoundTime { get => _lastStepSoundTime; set { FunnelCheck.Write(this, FunnelField.LastStepSoundTime); _lastStepSoundTime = value; } }

        // W5: these are inputs of the mobile's animation and override hue; every write is pushed to the ECS (MobileInputs)
        private bool _isRunning;
        private NotorietyFlag _notorietyFlag;

        public bool IsFemale => TryRecord(out Friflo.Engine.ECS.Entity e) && EcsScalars.MobileBit(e, ClassicUO.Ecs.MobileTraitBits.Female);

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
            get
            {
                ScalarReads.Count(ScalarField.NotorietyFlag);

                return _notorietyFlag;
            }
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

        private int _stepSoundOffset;

        public RaceType Race => TryRecord(out Friflo.Engine.ECS.Entity e) ? (RaceType)EcsScalars.Race(e) : 0;

        public CharacterSpeedType SpeedMode =>
            TryRecord(out Friflo.Engine.ECS.Entity e) ? (CharacterSpeedType)EcsScalars.SpeedMode(e) : CharacterSpeedType.Normal;

        public ushort Stamina
        {
            get
            {
                ScalarReads.Count(ScalarField.Stamina);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.Stamina(e) : (ushort)0;
            }
        }

        public ushort StaminaMax
        {
            get
            {
                ScalarReads.Count(ScalarField.StaminaMax);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.StaminaMax(e) : (ushort)0;
            }
        }

        public ushort Mana
        {
            get
            {
                ScalarReads.Count(ScalarField.Mana);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.Mana(e) : (ushort)0;
            }
        }

        public ushort ManaMax
        {
            get
            {
                ScalarReads.Count(ScalarField.ManaMax);

                return TryScalarEntity(out Friflo.Engine.ECS.Entity e) ? EcsScalars.ManaMax(e) : (ushort)0;
            }
        }

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
            // T2: the children from the ECS (WorldState.Children)
            foreach (Item it in World.State.Children(this))
            {
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
            ClassicUO.Ecs.AnimationTimer t = Timer;
            t.IdleDue = NextIdleDue();
            WriteTimer(in t);
        }

        /// <summary>T1 (funnel part 8): the next idle due time, 30 to 60 s from now (one random draw).</summary>
        internal static long NextIdleDue()
        {
            const int TIME = 30000;

            return Time.Ticks + (TIME + RandomHelper.GetValue(0, TIME));
        }

        /// <summary>Tests: a hash of every field the mobile update can change (the update, the steps, the timers), to show an
        /// operation changed nothing.</summary>
        internal int UpdateStateHash()
        {
            var h = new HashCode();
            ClassicUO.Ecs.AnimationTimer t = Timer;
            h.Add(t.Frame);
            h.Add(t.FrameCount);
            h.Add(t.FromServer);
            h.Add(t.AnimDue);
            h.Add(t.IdleDue);
            h.Add(t.Group);
            h.Add(t.Interval);
            h.Add(t.Repeat);
            h.Add(t.RepeatMode);
            h.Add(t.RepeatModeCount);
            h.Add(t.Forward);
            h.Add(X);
            h.Add(Y);
            h.Add(Z);
            h.Add((byte)Direction);
            h.Add(Offset);
            StepQueue steps = Steps;
            h.Add(steps.Count);

            // T6c: the queued steps themselves
            for (int i = 0; i < steps.Count; i++)
            {
                Step s = steps[i];
                h.Add(s.X);
                h.Add(s.Y);
                h.Add(s.Z);
                h.Add(s.Direction);
                h.Add(s.Run);
            }

            h.Add(LastStepTime);
            h.Add(LastStepSoundTime);
            h.Add(StepSoundOffset);
            h.Add((byte)ObjectHandlesStatus);
            h.Add(HitsPercentage);
            h.Add(IsDestroyed);

            return h.ToHashCode();
        }

        /// <summary>Tests: the frame delay the step processing subtracts (null: <c>Client.Game.FrameDelay[1]</c>).</summary>
        internal static int? FrameDelayOverride;

        internal static int StepFrameDelay => FrameDelayOverride ?? (int)Client.Game.FrameDelay[1];

        public void ClearSteps()
        {
            ClearStepQueue();
            SetOffset(0, 0, 0);
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

                SetLastStepTime(plan.StepTime);
            }

            if (plan.Count > 0) AddStep(plan.S0);
            if (plan.Count > 1) AddStep(plan.S1);
            if (plan.Count > 2) AddStep(plan.S2);

            PushInputs();
        }

        internal void GetEndPosition(out int x, out int y, out sbyte z, out Direction dir)
        {
            StepQueue steps = Steps;

            if (steps.Count == 0)
            {
                x = X;
                y = Y;
                z = Z;
                dir = Direction;
            }
            else
            {
                Step step = steps.Back();
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
            // the state, the frame due time (now), then the idle delay's random draw, as before
            ClassicUO.Ecs.AnimationTimer t = Timer;
            AnimationAdvanceRules.SetAnimation(ref t, id, interval, frameCount, repeatCount, repeat, forward, fromServer);
            WriteTimer(in t);
        }

        /// <summary>
        /// T1 (funnel part 8): what the idle animation does (<c>AnimationAdvanceRules.PlanIdle</c>), computed before any write. The random draws come in
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

        /// <summary>T1 (funnel part 8): the mutation half of the idle animation (<c>AnimationAdvanceRules.PlanIdle</c>'s plan), in
        /// its old order.</summary>
        internal void ApplyIdle(in IdlePlan plan)
        {
            ClassicUO.Ecs.AnimationTimer t = Timer;
            AnimationAdvanceRules.ApplyIdle(ref t, in plan);
            WriteTimer(in t);
        }

        /// <summary>
        /// T1 (funnel part 7): the timer fields an advance changes (the frame count and the repeat state; the frame index is
        /// written by the caller, after a finished animation's reset). Called by <see cref="WorldWriter.AdvanceAnimation"/>.
        /// </summary>
        internal void ApplyAnimationTimer(in ClassicUO.Ecs.AnimationTimer timer)
        {
            ClassicUO.Ecs.AnimationTimer t = Timer;
            t.FrameCount = timer.FrameCount;
            t.RepeatMode = timer.RepeatMode;
            t.RepeatModeCount = timer.RepeatModeCount;
            t.Repeat = timer.Repeat;
            WriteTimer(in t);
        }

        /// <summary>
        /// T5b: writes what <c>MobileAnimationAdvanceSystem</c> computed (a copy of the record's timer, advanced) to the
        /// record. <paramref name="frameAndDue"/> false leaves the frame index and the frame due time for
        /// <see cref="WorldWriter.RemoveDeadMobile"/>, which writes them after the removal, as the legacy advance did. Called
        /// by <see cref="WorldWriter.AdvanceAnimationFromEcs"/>.
        /// </summary>
        internal void WriteAnimationTimer(in ClassicUO.Ecs.AnimationTimer t, bool frameAndDue)
        {
            ClassicUO.Ecs.AnimationTimer w = t;

            if (!frameAndDue)
            {
                ClassicUO.Ecs.AnimationTimer now = Timer;
                w.Frame = now.Frame;
                w.AnimDue = now.AnimDue;
            }

            WriteTimer(in w);
        }

        /// <summary>
        /// T9 S8b: the direction the old <c>ProcessSteps(out dir)</c> returned without evaluating (the pick and the sampled draw
        /// path): the front step's (masked to 0..7 when it runs), else the facing masked to 0..7.
        /// </summary>
        internal byte StepFacing()
        {
            StepQueue steps = Steps;

            if (steps.Count == 0 || IsDestroyed)
            {
                return (byte)((byte)Direction & 7);
            }

            Step step = steps.Front();

            return step.Run ? (byte)(step.Direction & 7) : step.Direction;
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

        /// <summary>W5: <see cref="TryGetSittingInfo(out AnimationsLoader.SittingInfoData)"/> with the
        /// human, mounted, flying and action inputs already known from the ECS: the seat test without its equipment walks.</summary>
        internal bool TryGetSittingInfo(bool human, bool mounted, bool flying, byte group, out AnimationsLoader.SittingInfoData data)
        {
            return TryGetSittingInfoIf(human && !mounted && !flying && !TestStepNoChangeDirection(this, group), out data);
        }

        // the first chair-like object (item, static, multi) within 1 Z of this mobile on its tile, in tile order; none when
        // the mobile is not on a tile
        internal bool FindChair(out AnimationsLoader.SittingInfoData data)
        {
            Chunk chunk = TileChunk;
            TileReader reader = default;

            for (GameObject start = chunk == null ? null : TileReader.Open(chunk, TileCell, null, out reader);
                 start != null;
                 start = reader.Next())
            {
                if ((start is Item || start is Static || start is Multi) && Math.Abs(Z - start.Z) <= 1)
                {
                    if (ChairTable.Table.TryGetValue(start.Graphic, out data))
                    {
                        return true;
                    }
                }
            }

            data = AnimationsLoader.SittingInfoData.Empty;

            return false;
        }

        private bool TryGetSittingInfoIf(bool eligible, out AnimationsLoader.SittingInfoData data)
        {
            ushort result = 0;

            if (eligible)
            {
                if (FindChair(out data))
                {
                    return true;
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

            Vector3 offset = Offset;
            p.X += (int)offset.X + 22;
            p.Y += (int)(offset.Y - offset.Z - (height + centerY + 8));
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

            // T9 S8c: the walk queue and offset go with the record (a destroyed instance reads none)
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

        /// <summary>A read-only view of a mobile's walk queue (T9 S8c: its record's <see cref="ClassicUO.Ecs.Steps"/>, read at
        /// each access; empty for a null record). Its steps come back by value.</summary>
        internal readonly struct StepQueue
        {
            private readonly Friflo.Engine.ECS.Entity _record;

            internal StepQueue(Friflo.Engine.ECS.Entity record) => _record = record;

            public int Count => _record.IsNull ? 0 : ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(_record).Count;

            public Step this[int index]
            {
                get
                {
                    ref readonly ClassicUO.Ecs.Steps s = ref ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Steps>(_record);
                    ClassicUO.Ecs.StepEntry e = s.Ring[(s.Head + index) % ClassicUO.Ecs.Steps.CAPACITY];

                    return new Step { X = e.X, Y = e.Y, Z = e.Z, Direction = e.Direction, Run = e.Run };
                }
            }

            public Step Front() => this[0];

            public Step Back() => this[Count - 1];
        }
    }
}
