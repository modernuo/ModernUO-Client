// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System.Collections.Generic;
using System.Linq;

namespace ClassicUO.Game.GameObjects
{
    internal class PlayerMobile : Mobile
    {
        private readonly Dictionary<BuffIconType, BuffIcon> _buffIcons = new Dictionary<BuffIconType, BuffIcon>();

        public PlayerMobile(World world, uint serial) : base(world, serial)
        {
            Skills = new Skill[Client.Game.UO.FileManager.Skills.SkillsCount];

            for (int i = 0; i < Skills.Length; i++)
            {
                SkillEntry skill = Client.Game.UO.FileManager.Skills.Skills[i];
                Skills[i] = new Skill(skill.Name, skill.Index, skill.HasAction);
            }

            Walker = new WalkerManager(this);
            Pathfinder = new Pathfinder(world);
        }

        public Skill[] Skills { get; }
        private bool _inWarMode;

        public override bool InWarMode
        {
            get => _inWarMode;
            set
            {
                FunnelCheck.Write(this, FunnelField.InWarMode);
                _inWarMode = value;
                PushInputs();
            }
        }
        public IReadOnlyDictionary<BuffIconType, BuffIcon> BuffIcons => _buffIcons;

        public ref Ability PrimaryAbility => ref Abilities[0];
        public ref Ability SecondaryAbility => ref Abilities[1];
        protected override bool IsWalking => LastStepTime > Time.Ticks - Constants.PLAYER_WALKING_DELAY;

        internal WalkerManager Walker { get; }
        public Pathfinder Pathfinder { get; }


        public readonly Ability[] Abilities = [Ability.Invalid, Ability.Invalid];

        //private bool _lastRun, _lastMount;
        //private int _lastDir = -1, _lastDelta, _lastStepTime;


        public readonly HashSet<uint> AutoOpenedCorpses = new HashSet<uint>();
        public readonly HashSet<uint> ManualOpenedCorpses = new HashSet<uint>();

        // Tracks the last direction actually sent to the server via a walk packet.
        private Direction _serverDirection;
        private bool _serverDirectionInitialized;

        /// <summary>
        /// Resyncs the server-tracked direction with the current visual Direction.
        /// Called after DenyWalk/resync when the server resets the player's state.
        /// </summary>
        internal void SyncServerDirection()
        {
            _serverDirection = Direction;
        }

        public short ColdResistance;
        public short DamageIncrease;
        public short DamageMax;
        public short DamageMin;
        public long DeathScreenTimer;
        public short DefenseChanceIncrease;
        public Lock DexLock;
        public ushort Dexterity;
        public short DexterityIncrease;
        public short EnergyResistance;
        public short EnhancePotions;
        public short FasterCasting;
        public short FasterCastRecovery;
        public short FireResistance;
        public byte Followers;
        public byte FollowersMax;
        public uint Gold;
        public short HitChanceIncrease;
        public short HitPointsIncrease;
        public short HitPointsRegeneration;
        public ushort Intelligence;
        public short IntelligenceIncrease;
        public Lock IntLock;
        public short LowerManaCost;
        public short LowerReagentCost;
        public ushort Luck;
        public short ManaIncrease;
        public short ManaRegeneration;
        public short MaxColdResistence;
        public short MaxDefenseChanceIncrease;
        public short MaxEnergyResistence;
        public short MaxFireResistence;
        public short MaxHitPointsIncrease;
        public short MaxManaIncrease;
        public short MaxPhysicResistence;
        public short MaxPoisonResistence;
        public short MaxStaminaIncrease;
        public short PhysicalResistance;
        public short PoisonResistance;
        public short ReflectPhysicalDamage;
        public short SpellDamageIncrease;
        public short StaminaIncrease;
        public short StaminaRegeneration;
        public short StatsCap;
        public ushort Strength;
        public short StrengthIncrease;
        public Lock StrLock;
        public short SwingSpeedIncrease;
        public uint TithingPoints;
        public ushort Weight;
        public ushort WeightMax;

        public Item FindBandage()
        {
            Item backpack = FindItemByLayer(Layer.Backpack);
            Item item = null;

            if (backpack != null)
            {
                item = backpack.FindItem(0x0E21);
            }

            return item;
        }

        public Item FindItemByGraphic(ushort graphic)
        {
            Item backpack = FindItemByLayer(Layer.Backpack);

            if (backpack != null)
            {
                return FindItemInContainerRecursive(backpack, graphic);
            }

            return null;
        }

        public Item FindItemByCliloc(int cliloc)
        {
            Item backpack = FindItemByLayer(Layer.Backpack);

            if (backpack != null)
            {
                return FindItemByClilocInContainerRecursive(backpack, cliloc);
            }

            return null;
        }

        private Item FindItemInContainerRecursive(Item container, ushort graphic)
        {
            Item found = null;

            if (container != null)
            {
                // T2: the children from the ECS with ecs_read_contents on (WorldState.Children)
                foreach (Item item in World.State.Children(container))
                {

                    if (item.Graphic == graphic)
                    {
                        return item;
                    }

                    if (World.State.HasChildren(item))
                    {
                        found = FindItemInContainerRecursive(item, graphic);

                        if (found != null && found.Graphic == graphic)
                        {
                            return found;
                        }
                    }
                }
            }

            return found;
        }

        private Item FindItemByClilocInContainerRecursive(Item container, int cliloc)
        {
            Item found = null;

            if (container != null)
            {
                // T2: the children from the ECS with ecs_read_contents on (WorldState.Children)
                foreach (Item item in World.State.Children(container))
                {


                    if (cliloc == World.OPL.GetNameCliloc(item.Serial))
                    {
                        return item;
                    }

                    if (World.State.HasChildren(item))
                    {
                        found = FindItemByClilocInContainerRecursive(item, cliloc);

                        if (found != null && cliloc == World.OPL.GetNameCliloc(found.Serial))
                        {
                            return found;
                        }
                    }
                }
            }

            return found;
        }

        public Item FindPreferredItemByCliloc(System.Span<int> clilocs)
        {
            Item item = null;

            for (int i = 0; i < clilocs.Length; i++)
            {
                item = World.Player.FindItemByCliloc(clilocs[i]);

                if (item != null)
                {
                    break;
                }
            }

            return item;
        }

        public int GetTotalAmountOfItem(ushort graphic, ushort? hue)
        {
            int _amount = 0;

            // T2: the children from the ECS with ecs_read_contents on (WorldState.Children)
            foreach (Item item in World.State.Children(World.Player))
            {
                if (item.Layer < Layer.OneHanded || item.Layer > Layer.Legs)
                {
                    continue;
                }
                if (item.ItemData.IsContainer && World.State.HasChildren(item))
                {
                    item.GetTotalAmount(graphic, hue, ref _amount);
                }
                else if (item.Graphic == graphic && (!hue.HasValue || item.Hue == hue.Value))
                {
                    _amount += item.Amount;
                }

            }

            return _amount;
        }

        public void AddBuff(BuffIconType type, ushort graphic, uint time, string text)
        {
            _buffIcons[type] = new BuffIcon(type, graphic, time, text);
        }


        public bool IsBuffIconExists(BuffIconType graphic)
        {
            return _buffIcons.ContainsKey(graphic);
        }

        public void RemoveBuff(BuffIconType graphic)
        {
            _buffIcons.Remove(graphic);
        }

        /// <summary>
        /// The neighbouring graphic (previous first, then next) that shares <paramref name="animId"/>, or 0 when
        /// neither does. Both lookups are bounds-checked: graphic 0xFFFF has no next entry in a 0x10000-entry table,
        /// and a graphic of 0 has no previous one.
        /// </summary>
        internal static ushort FindAnimNeighbour(StaticTiles[] statics, ushort graphic, ushort animId)
        {
            if (graphic > 0 && graphic - 1 < statics.Length && statics[graphic - 1].AnimID == animId)
            {
                return (ushort)(graphic - 1);
            }

            if (graphic + 1 < statics.Length && statics[graphic + 1].AnimID == animId)
            {
                return (ushort)(graphic + 1);
            }

            return 0;
        }

        public void UpdateAbilities()
        {
            AbilityData.DefaultItemAbilities.Set(Abilities);
            
            if ((FindItemByLayer(Layer.OneHanded) ?? FindItemByLayer(Layer.TwoHanded)) is { Graphic: > 0 } weapon)
            {
                ushort animGraphic = FindAnimNeighbour(
                    Client.Game.UO.FileManager.TileData.StaticData,
                    weapon.Graphic,
                    weapon.ItemData.AnimID
                );

                if (AbilityData.GraphicToAbilitiesMap.TryGetValue(weapon.Graphic, out var abilities) || AbilityData.GraphicToAbilitiesMap.TryGetValue(animGraphic, out abilities))
                {
                    abilities.Set(Abilities);
                }
                else
                {
                    Log.Warn($"Could not update abilities ${weapon.Graphic} \"${weapon.Name}\" has no GraphicToAbilitiesMap[graphic] data");
                }
            }

            for (LinkedListNode<Gump> gump = UIManager.Gumps.First; gump != null; gump = gump.Next)
            {
                if (gump.Value is UseAbilityButtonGump or CombatBookGump)
                    gump.Value.RequestUpdateContents();
            }
        }

        protected override void OnPositionChanged()
        {
            base.OnPositionChanged();

            Plugin.UpdatePlayerPosition(X, Y, Z);

            TryOpenDoors();
            TryOpenCorpses();
        }

        public void TryOpenCorpses()
        {
            if (ProfileManager.CurrentProfile.AutoOpenCorpses)
            {
                if ((ProfileManager.CurrentProfile.CorpseOpenOptions == 1 || ProfileManager.CurrentProfile.CorpseOpenOptions == 3) && World.TargetManager.IsTargeting)
                {
                    return;
                }

                if ((ProfileManager.CurrentProfile.CorpseOpenOptions == 2 || ProfileManager.CurrentProfile.CorpseOpenOptions == 3) && IsHidden)
                {
                    return;
                }

                foreach (Item item in World.Items.Values)
                {
                    if (!item.IsDestroyed && item.IsCorpse && item.Distance <= ProfileManager.CurrentProfile.AutoOpenCorpseRange && !AutoOpenedCorpses.Contains(item.Serial))
                    {
                        AutoOpenedCorpses.Add(item.Serial);
                        GameActions.DoubleClickQueued(item.Serial);
                    }
                }
            }
        }


        protected override void OnDirectionChanged()
        {
            base.OnDirectionChanged();
            TryOpenDoors();
        }

        private void TryOpenDoors()
        {
            if (!World.Player.IsDead && ProfileManager.CurrentProfile.AutoOpenDoors)
            {
                int x = X, y = Y, z = Z;
                Pathfinder.GetNewXY((byte) Direction, ref x, ref y);

                if (World.Items.Values.Any(s => s.ItemData.IsDoor && s.X == x && s.Y == y && s.Z - 15 <= z && s.Z + 15 >= z))
                {
                    GameActions.OpenDoor();
                }
            }
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            DeathScreenTimer = 0;

            Log.Warn("PlayerMobile disposed!");
            base.Destroy();
        }

        public void CloseBank()
        {
            Item bank = FindItemByLayer(Layer.Bank);

            if (bank != null && bank.Opened)
            {
                if (!bank.IsEmpty)
                {
                    // T1 (funnel part 9): the removals; a rejected call removes nothing and leaves the bank open
                    if (!World.Writer.RemoveBankContents(this, bank))
                    {
                        return;
                    }

                    bank.Items = null;
                }

                UIManager.GetGump<ContainerGump>(bank.Serial)?.Dispose();

                bank.Opened = false;
            }
        }

        public void CloseRangedGumps()
        {
            foreach (Gump gump in UIManager.Gumps)
            {
                switch (gump)
                {
                    case PaperDollGump _:
                    case MapGump _:
                    case SpellbookGump _:

                        if (World.Get(gump.LocalSerial) == null)
                        {
                            gump.Dispose();
                        }

                        break;

                    case TradingGump _:
                    case ShopGump _:

                        Entity ent = World.Get(gump.LocalSerial);
                        int distance = int.MaxValue;

                        if (ent != null)
                        {
                            if (SerialHelper.IsItem(ent.Serial))
                            {
                                Entity top = World.Get(((Item) ent).RootContainer);

                                if (top != null)
                                {
                                    distance = top.Distance;
                                }
                            }
                            else
                            {
                                distance = ent.Distance;
                            }
                        }

                        if (distance > Constants.MIN_VIEW_RANGE)
                        {
                            gump.Dispose();
                        }

                        break;

                    case ContainerGump _:
                        distance = int.MaxValue;

                        ent = World.Get(gump.LocalSerial);

                        if (ent != null)
                        {
                            if (SerialHelper.IsItem(ent.Serial))
                            {
                                Entity top = World.Get(((Item) ent).RootContainer);

                                if (top != null)
                                {
                                    distance = top.Distance;
                                }
                            }
                            else
                            {
                                distance = ent.Distance;
                            }
                        }

                        if (distance > Constants.MAX_CONTAINER_OPENED_ON_GROUND_RANGE)
                        {
                            gump.Dispose();
                        }

                        break;
                }
            }
        }


        //public override void Update()
        //{
        //    base.Update();

        //    //const int TIME_TURN_TO_LASTTARGET = 2000;

        //    //if (TargetManager.LastAttack != 0 &&
        //    //    InWarMode &&
        //    //    Walker.LastStepRequestTime + TIME_TURN_TO_LASTTARGET < Time.Ticks)
        //    //{
        //    //    Mobile enemy = World.Mobiles.Get(TargetManager.LastAttack);

        //    //    if (enemy != null && enemy.Distance <= 1)
        //    //    {
        //    //        Direction pdir = DirectionHelper.GetDirectionAB(World.Player.X,
        //    //                                                        World.Player.Y,
        //    //                                                        enemy.X,
        //    //                                                        enemy.Y);

        //    //        if (Direction != pdir)
        //    //            Walk(pdir, false);
        //    //    }
        //    //}
        //}

        // ############# DO NOT DELETE IT! #############
        //protected override bool NoIterateAnimIndex()
        //{
        //    return false;
        //}
        // #############################################

        /// <summary>
        /// T1 (funnel part 8): the walk queue's part of <see cref="Walk"/>, computed without a write: one step to
        /// (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) facing <paramref name="direction"/>; when the queue
        /// is empty the walk starts now (<c>LastStepTime</c>), with <c>SetAnimation(0xFF)</c> if the player is not walking.
        /// </summary>
        internal void PlanWalkStep(int x, int y, sbyte z, Direction direction, bool run, out StepPlan plan)
        {
            bool emptyStack = Steps.Count == 0;
            plan = default;
            plan.Accepted = true;
            plan.Changes = true;
            plan.StartsWalk = emptyStack;
            plan.SetsAnimation = emptyStack && !IsWalking;
            plan.StepTime = Time.Ticks;
            plan.Count = 1;
            plan.S0 = new Step
            {
                X = x,
                Y = y,
                Z = z,
                Direction = (byte) direction,
                Run = run
            };
        }

        /// <summary>
        /// T1 (funnel part 8): <see cref="Walk"/>'s coalesced turn (three unconfirmed requests: the direction alone, no packet)
        /// through the funnel (<c>TurnPlayer</c>: SetMobileDirection's writes, unfolded for the door check, with the walk's
        /// admission, so a walk from a position callback joins the open operation on the player), then the turn's cooldown.
        /// Rejected (Codex audit of part 8, P2 #1): nothing changed, no cooldown, false.
        /// </summary>
        internal bool CoalescedTurn(Direction direction, ushort walkTime)
        {
            if (!World.Writer.TurnPlayer(this, direction))
            {
                return false;
            }

            Walker.LastStepRequestTime = Time.Ticks + walkTime;

            return true;
        }

        public bool Walk(Direction direction, bool run)
        {
            if (Walker.WalkingFailed || Walker.LastStepRequestTime > Time.Ticks || Walker.StepsCount >= Constants.MAX_STEP_COUNT || Client.Game.UO.Version >= ClientVersion.CV_60142 && IsParalyzed)
            {
                return false;
            }

            run |= ProfileManager.CurrentProfile.AlwaysRun;

            if (SpeedMode >= CharacterSpeedType.CantRun || Stamina <= 1 && !IsDead || IsHidden && ProfileManager.CurrentProfile.AlwaysRunUnlessHidden)
            {
                run = false;
            }

            int x = X;
            int y = Y;
            sbyte z = Z;

            if (!_serverDirectionInitialized)
            {
                _serverDirection = Direction;
                _serverDirectionInitialized = true;
            }

            // Use the server-synced direction for walk calculations, not the visual Direction,
            // which may have been updated by coalescing without sending a packet.
            Direction oldDirection = _serverDirection;

            bool emptyStack = Steps.Count == 0;

            if (!emptyStack)
            {
                ref readonly Step walkStep = ref Steps.Back();
                x = walkStep.X;
                y = walkStep.Y;
                z = walkStep.Z;
                oldDirection = (Direction) walkStep.Direction;
            }

            sbyte oldZ = z;
            ushort walkTime = (ushort)MovementSpeed.TurnDelay;

            if ((oldDirection & Direction.Mask) == (direction & Direction.Mask))
            {
                Direction newDir = direction;
                int newX = x;
                int newY = y;
                sbyte newZ = z;

                if (!Pathfinder.CanWalk(ref newDir, ref newX, ref newY, ref newZ))
                {
                    return false;
                }

                if ((direction & Direction.Mask) != newDir)
                {
                    direction = newDir;
                }
                else
                {
                    direction = newDir;
                    x = newX;
                    y = newY;
                    z = newZ;

                    walkTime = (ushort) MovementSpeed.TimeToCompleteMovement(run, IsMounted || SpeedMode == CharacterSpeedType.FastUnmount || SpeedMode == CharacterSpeedType.FastUnmountAndCantRun || IsFlying);
                }
            }
            else
            {
                Direction newDir = direction;
                int newX = x;
                int newY = y;
                sbyte newZ = z;

                if (!Pathfinder.CanWalk(ref newDir, ref newX, ref newY, ref newZ))
                {
                    if ((oldDirection & Direction.Mask) == newDir)
                    {
                        return false;
                    }
                }

                if ((oldDirection & Direction.Mask) == newDir)
                {
                    x = newX;
                    y = newY;
                    z = newZ;

                    walkTime = (ushort) MovementSpeed.TimeToCompleteMovement(run, IsMounted || SpeedMode == CharacterSpeedType.FastUnmount || SpeedMode == CharacterSpeedType.FastUnmountAndCantRun || IsFlying);
                }

                direction = newDir;
            }

            // Adaptive coalescing: only suppress direction-only packets when the server
            // is falling behind (3+ unconfirmed packets). This allows full-speed spinning
            // when the connection is healthy and automatically backs off under congestion.
            if (walkTime == (ushort)MovementSpeed.TurnDelay && Walker.UnacceptedPacketsCount >= 3)
            {
                return CoalescedTurn(direction, walkTime);
            }

            // T1 (funnel part 8): admission first, then CloseBank, then the walk queue's writes through the funnel
            // (QueuePlayerStep: PlanWalkStep, then SetAnimation(0xFF) when the walk starts while not walking, LastStepTime,
            // the step, the inputs), written before the walker's step record; the walker is not world state and neither side
            // reads the other. A walk from the player's position callback joins the operation that runs it (Codex audit of
            // part 8, P2 #1). Rejected: nothing changed, the bank stays open
            if (!World.Writer.QueuePlayerStep(this, x, y, z, direction, run))
            {
                return false;
            }

            ref StepInfo step = ref Walker.StepInfos[Walker.StepsCount];
            step.Sequence = Walker.WalkSequence;
            step.Accepted = false;
            step.Running = run;
            step.OldDirection = (byte) (oldDirection & Direction.Mask);
            step.Direction = (byte) direction;
            step.Timer = Time.Ticks;
            step.X = (ushort) x;
            step.Y = (ushort) y;
            step.Z = z;
            step.NoRotation = step.OldDirection == (byte) direction && oldZ - z >= 11;

            Walker.StepsCount++;


            NetClient.Socket.Send_WalkRequest(direction, Walker.WalkSequence, run, Walker.FastWalkStack.GetValue());

            _serverDirection = direction;


            if (Walker.WalkSequence == 0xFF)
            {
                Walker.WalkSequence = 1;
            }
            else
            {
                Walker.WalkSequence++;
            }

            Walker.UnacceptedPacketsCount++;

            AddToTile();

            Walker.LastStepRequestTime = Time.Ticks + walkTime;
            GetGroupForAnimation(this, 0, true);

            return true;
        }
    }
}