// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System.Collections.Generic;

namespace ClassicUO.Game.GameObjects
{
    internal class PlayerMobile : Mobile
    {
        public PlayerMobile(World world, uint serial) : base(world, serial)
        {
            Walker = new WalkerManager(this);
            Pathfinder = new Pathfinder(world);
        }

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
        protected override bool IsWalking => LastStepTime > Time.Ticks - Constants.PLAYER_WALKING_DELAY;

        internal WalkerManager Walker { get; }
        public Pathfinder Pathfinder { get; }


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
                return FindItemInContainerRecursive(backpack, graphic, backpack.Serial, 0);
            }

            return null;
        }

        public Item FindItemByCliloc(int cliloc)
        {
            Item backpack = FindItemByLayer(Layer.Backpack);

            if (backpack != null)
            {
                return FindItemByClilocInContainerRecursive(backpack, cliloc, backpack.Serial, 0);
            }

            return null;
        }

        private Item FindItemInContainerRecursive(Item container, ushort graphic, uint root, int depth)
        {
            Item found = null;

            if (container != null)
            {
                // T2: the children from the ECS (WorldState.Children)
                foreach (Item item in World.State.Children(container))
                {

                    if (item.Graphic == graphic)
                    {
                        return item;
                    }

                    if (depth < WorldState.MaxNestingDepth && item.Serial != root && World.State.HasChildren(item))
                    {
                        found = FindItemInContainerRecursive(item, graphic, root, depth + 1);

                        if (found != null && found.Graphic == graphic)
                        {
                            return found;
                        }
                    }
                }
            }

            return found;
        }

        private Item FindItemByClilocInContainerRecursive(Item container, int cliloc, uint root, int depth)
        {
            Item found = null;

            if (container != null)
            {
                // T2: the children from the ECS (WorldState.Children)
                foreach (Item item in World.State.Children(container))
                {


                    if (cliloc == World.OPL.GetNameCliloc(item.Serial))
                    {
                        return item;
                    }

                    if (depth < WorldState.MaxNestingDepth && item.Serial != root && World.State.HasChildren(item))
                    {
                        found = FindItemByClilocInContainerRecursive(item, cliloc, root, depth + 1);

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

            // T2: the children from the ECS (WorldState.Children)
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
            Ability[] abilities = World.PlayerState.Abilities;
            AbilityData.DefaultItemAbilities.Set(abilities);
            
            if ((FindItemByLayer(Layer.OneHanded) ?? FindItemByLayer(Layer.TwoHanded)) is { Graphic: > 0 } weapon)
            {
                ushort animGraphic = FindAnimNeighbour(
                    Client.Game.UO.FileManager.TileData.StaticData,
                    weapon.Graphic,
                    weapon.ItemData.AnimID
                );

                if (AbilityData.GraphicToAbilitiesMap.TryGetValue(weapon.Graphic, out var weaponAbilities) || AbilityData.GraphicToAbilitiesMap.TryGetValue(animGraphic, out weaponAbilities))
                {
                    weaponAbilities.Set(abilities);
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

                OpenCorpsesInRange(ProfileManager.CurrentProfile.AutoOpenCorpseRange);
            }
        }

        /// <summary>Tests: receives the serials <see cref="OpenCorpsesInRange"/> opens instead of the queued double clicks.</summary>
        internal List<uint> CorpseOpenSink;

        /// <summary>
        /// perf-13: opens every corpse within <paramref name="range"/> not opened yet, in <c>World.Items</c> order, as the
        /// every-item loop did. Every live corpse is in the active item list (<see cref="Item.NeedsFrameUpdate"/>; the mirror
        /// hooks queue it, World.Update's first pass and sweep catch any other), so the candidates are counted there: none
        /// (every step but the first near new corpses) costs O(corpses); one is opened directly; two or more take the
        /// every-item loop, which alone knows the dictionary's order (which gump opens first). Before the list's first build
        /// after World.Clear the loop runs. CUO_STEP_OPEN_CHECK compares the decision with the loop's.
        /// </summary>
        internal void OpenCorpsesInRange(int range)
        {
            int found = 0;
            Item single = null;

            if (World.ActiveItemsListed)
            {
                int count = World.ActiveItemCount;

                for (int i = 0; i < count; i++)
                {
                    Item item = World.ActiveItemAt(i);

                    if (ShouldAutoOpen(item, range) && ReferenceEquals(World.Items.Get(item.Serial), item))
                    {
                        single = item;

                        if (++found > 1)
                        {
                            break;
                        }
                    }
                }
            }
            else
            {
                found = 2;
            }

#if CUO_DEVTOOLS
            if (StepOpenCheck.On)
            {
                StepOpenCheck.Corpses(this, range, found, single);
            }
#endif

            if (found == 1)
            {
                AutoOpenCorpse(single);
            }
            else if (found > 1)
            {
                foreach (Item item in World.Items.Values)
                {
                    if (ShouldAutoOpen(item, range))
                    {
                        AutoOpenCorpse(item);
                    }
                }
            }
        }

        internal bool ShouldAutoOpen(Item item, int range) =>
            !item.IsDestroyed && item.IsCorpse && item.Distance <= range && !AutoOpenedCorpses.Contains(item.Serial);

        private void AutoOpenCorpse(Item item)
        {
            AutoOpenedCorpses.Add(item.Serial);

            if (CorpseOpenSink != null)
            {
                CorpseOpenSink.Add(item.Serial);
            }
            else
            {
                GameActions.DoubleClickQueued(item.Serial);
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

                bool door = DoorAhead(World, x, y, z, Client.Game.UO.FileManager.TileData.StaticData);

#if CUO_DEVTOOLS
                if (StepOpenCheck.On)
                {
                    StepOpenCheck.Door(World, x, y, z, door);
                }
#endif

                if (door)
                {
                    GameActions.OpenDoor();
                }
            }
        }

        /// <summary>
        /// perf-13: whether an item with the door flag stands on tile (<paramref name="x"/>, <paramref name="y"/>) within 15
        /// of <paramref name="z"/>: the rule the every-item LINQ scan applied, read from that tile's objects (the tile list or
        /// the dynamic tile index, <see cref="TileReader"/>). A world item's place is its tile; the scan also matched items in
        /// containers or worn whose container X/Y equalled the tile, which are not doors in front of the player. When the
        /// tile's chunk is not loaded (its items parked) the scan runs. <paramref name="data"/> is the tile data (Item.ItemData's).
        /// CUO_STEP_OPEN_CHECK compares the answer with the scan's.
        /// </summary>
        internal static bool DoorAhead(World world, int x, int y, int z, StaticTiles[] data)
        {
            Chunk chunk = world.Map?.PeekChunk(x, y);

            if (chunk == null || chunk.IsDestroyed)
            {
                return DoorScan(world, x, y, z, data);
            }

            for (GameObject obj = TileReader.Open(chunk, (x & 7) << 3 | (y & 7), DynamicTileIndex.WalkOn, null, out TileReader reader);
                 obj != null;
                 obj = reader.Next())
            {
                if (obj is Item item && IsDoorAt(item, x, y, z, data) && ReferenceEquals(world.Items.Get(item.Serial), item))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The every-item scan <see cref="DoorAhead"/> replaced (its fallback and the checker's reference).</summary>
        internal static bool DoorScan(World world, int x, int y, int z, StaticTiles[] data)
        {
            foreach (Item item in world.Items.Values)
            {
                if (IsDoorAt(item, x, y, z, data))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDoorAt(Item item, int x, int y, int z, StaticTiles[] data) =>
            data[item.IsMulti ? item.MultiGraphic : item.Graphic].IsDoor && item.X == x && item.Y == y && item.Z - 15 <= z && item.Z + 15 >= z;

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            Log.Warn("PlayerMobile disposed!");
            base.Destroy();
        }

        public void CloseBank()
        {
            Item bank = FindItemByLayer(Layer.Bank);

            if (bank != null && bank.Opened)
            {
                // T9 child-list slice A: the ECS's contents
                if (World.State.HasChildren(bank))
                {
                    // T1 (funnel part 9): the removals; a rejected call removes nothing and leaves the bank open
                    if (!World.Writer.RemoveBankContents(this, bank))
                    {
                        return;
                    }
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