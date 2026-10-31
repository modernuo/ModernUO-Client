// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Linq;
using ClassicUO.IO.Audio;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility.Platforms;
using Microsoft.Xna.Framework;
using MathHelper = ClassicUO.Utility.MathHelper;
using ClassicUO.Configuration;
using ClassicUO.Game.Scenes;
using ClassicUO.Utility.Logging;
using ClassicUO.Assets;
using ClassicUO.Network;

namespace ClassicUO.Game
{
    internal sealed class World
    {
        private readonly EffectManager _effectManager;

#if CUO_DEVTOOLS
        /// <summary>The graphic effects (moving, fixed, drag, lightning) alive in this world; DevBench's effect tooling reads it.</summary>
        internal EffectManager Effects => _effectManager;
#endif
        private readonly List<uint> _toRemove = new List<uint>();
        private uint _timeToDelete;

        public World()
        {
            if (WorldEcs.MobileUpdateCheck)
            {
                Mobiles.EnableShadow();
            }

            WMapManager = new WorldMapEntityManager(this);
            CorpseManager = new CorpseManager(this);
            Party = new PartyManager(this);
            HouseManager = new HouseManager(this);
            WorldTextManager = new WorldTextManager(this);
            _effectManager = new EffectManager(this);
            MessageManager = new MessageManager(this);
            ContainerManager = new ContainerManager(this);
            IgnoreManager = new IgnoreManager(this);
            SkillsGroupManager = new SkillsGroupManager(this);
            ChatManager = new ChatManager(this);
            AuraManager = new AuraManager(this);
            UoAssist = new UoAssist(this);
            TargetManager = new TargetManager(this);
            DelayedObjectClickManager = new DelayedObjectClickManager(this);
            BoatMovingManager = new BoatMovingManager(this);
            NameOverHeadManager = new NameOverHeadManager(this);
            Macros = new MacroManager(this);
            CommandManager = new CommandManager(this);
            Weather = new Weather(this);
            InfoBars = new InfoBarManager(this);
            Ecs = new WorldEcs(this);
            MapRecycler = new Map.MapObjectRecycler(this);
            ChunkPool = new Map.ChunkPool(this);
            Writer = new WorldWriter(this);
        }

        public WorldEcs Ecs { get; }

        /// <summary>T1: the mutation funnel (ecs-transition-plan.md); converted packet groups write world state through it.</summary>
        public WorldWriter Writer { get; }

        public Point RangeSize;

        public PlayerMobile Player { get; private set; }

        public HouseCustomizationManager CustomHouseManager;

        public WorldMapEntityManager WMapManager { get; }

        public ActiveSpellIconsManager ActiveSpellIcons = new ActiveSpellIconsManager();

        public uint LastObject, ObjectToRemove;

        public ObjectPropertiesListManager OPL { get; } = new ObjectPropertiesListManager();

        public CorpseManager CorpseManager { get; }

        public PartyManager Party { get; }

        public HouseManager HouseManager { get; }

        public MessageManager MessageManager { get; }

        public ContainerManager ContainerManager { get; }

        public IgnoreManager IgnoreManager { get; }

        public SkillsGroupManager SkillsGroupManager { get; }

        public ChatManager ChatManager { get; }

        public AuraManager AuraManager { get; }

        public UoAssist UoAssist { get; }

        public TargetManager TargetManager { get; }

        public DelayedObjectClickManager DelayedObjectClickManager { get; }

        public BoatMovingManager BoatMovingManager { get; }

        public NameOverHeadManager NameOverHeadManager { get; }

        public MacroManager Macros { get; }

        public CommandManager CommandManager { get; }

        public Weather Weather { get; }

        public InfoBarManager InfoBars { get; }

        public Dictionary<uint, Item> Items { get; } = new Dictionary<uint, Item>();

        public MobileTable Mobiles { get; } = new MobileTable();

        public Map.Map Map { get; private set; }

        /// <summary>Land and statics of evicted chunks, reused by later chunk loads (Chunk.Destroy, Chunk.Load).</summary>
        public Map.MapObjectRecycler MapRecycler { get; }

        /// <summary>Destroyed chunks of this World, reused by Chunk.Create. Owned by the World like <see cref="MapRecycler"/>:
        /// kept across <see cref="Clear"/> (relog), released by <see cref="ReleasePools"/>.</summary>
        public Map.ChunkPool ChunkPool { get; }

        // the slot array of the last destroyed Map, handed to the next one (a ~3.5 MiB LOH array otherwise per login/facet
        // change). Held only while no Map owns it: RentChunkSlots takes it out, ReturnChunkSlots (Map.Destroy) puts it back.
        private Chunk[] _spareSlots;

        /// <summary>A cleared slot array of at least <paramref name="count"/> entries: the spare one if it is large enough
        /// (the caller then owns it exclusively), else a new one.</summary>
        internal Chunk[] RentChunkSlots(int count)
        {
            Chunk[] spare = _spareSlots;

            if (spare != null && spare.Length >= count)
            {
                _spareSlots = null;

                return spare;
            }

            return new Chunk[count];
        }

        /// <summary>The Map that owned <paramref name="slots"/> is destroyed; the array must already be cleared.</summary>
        internal void ReturnChunkSlots(Chunk[] slots)
        {
            if (_spareSlots == null || _spareSlots.Length < slots.Length)
            {
                _spareSlots = slots;
            }
        }

        internal Chunk[] SpareChunkSlots => _spareSlots;

        /// <summary>MapWarmup filled this World's pools for the current login (World.Clear re-arms it).</summary>
        internal bool WarmupDone;

        /// <summary>The World is finished (client unload, or a test's end): drops its chunk, land and static pools, so nothing
        /// keeps its chunks or map objects alive.</summary>
        public void ReleasePools()
        {
            ChunkPool.Close();
            MapRecycler.Close();
            _spareSlots = null;
            WarmupDone = false;
        }

        public byte ClientViewRange { get; set; } = Constants.MAX_VIEW_RANGE;

        public bool SkillsRequested { get; set; }

        public Season Season { get; private set; } = Season.Summer;
        public Season OldSeason { get; set; } = Season.Summer;

        public int OldMusicIndex { get; set; }

        public WorldTextManager WorldTextManager { get; }

        /// <summary>Mobiles and items that got overhead text (GameObject.AddMessage): the scene refreshes their text position
        /// each frame from this list instead of scanning every entity. Entries whose text is gone are dropped there.</summary>
        public readonly List<Entity> TextOwners = new List<Entity>();

        public JournalManager Journal { get; } = new JournalManager();


        public int MapIndex
        {
            get => Map?.Index ?? -1;
            set
            {
                if (MapIndex != value)
                {
                    InternalMapChangeClear(true);

                    if (value < 0 && Map != null)
                    {
                        Map.Destroy();
                        Map = null;

                        return;
                    }

                    if (Map != null)
                    {
                        if (MapIndex >= 0)
                        {
                            Map.Destroy();
                        }

                        ushort x = Player.X;
                        ushort y = Player.Y;
                        sbyte z = Player.Z;

                        Map = null;

                        if (value >= MapLoader.MAPS_COUNT)
                        {
                            value = 0;
                        }

                        Client.Game.UO.FileManager.Maps.LoadMap(value);
                        Map = new Map.Map(this, value);
                        ClassicUO.Game.Map.MapWarmup.OnMapCreated(this);

                        Player.SetInWorldTile(x, y, z);
                        Player.ClearSteps();
                    }
                    else
                    {
                        Client.Game.UO.FileManager.Maps.LoadMap(value);
                        Map = new Map.Map(this, value);
                        ClassicUO.Game.Map.MapWarmup.OnMapCreated(this);
                    }

                    // force cursor update when switching map
                    if (Client.Game.UO.GameCursor != null)
                    {
                        Client.Game.UO.GameCursor.Graphic = 0xFFFF;
                    }

                    UoAssist.SignalMapChanged(value);
                }
            }
        }

        public bool InGame => Player != null && Map != null;

        public IsometricLight Light { get; } = new IsometricLight
        {
            Overall = 0,
            Personal = 0,
            RealOverall = 0,
            RealPersonal = 0
        };

        public LockedFeatures ClientLockedFeatures { get; } = new LockedFeatures();

        public ClientFeatures ClientFeatures { get; } = new ClientFeatures();

        public string ServerName { get; set; } = "_";



        public void CreatePlayer(uint serial)
        {
            if (ProfileManager.CurrentProfile == null)
            {
                string lastChar = LastCharacterManager.GetLastCharacter(LoginScene.Account, ServerName);
                ProfileManager.Load(ServerName, LoginScene.Account, lastChar);
            }

            if (Player != null)
            {
                Clear();
            }

            Player = new PlayerMobile(this, serial);
            Mobiles.Add(Player);
            Ecs.Sync(Player);

            Log.Trace($"Player [0x{serial:X8}] created");
        }

        public void ChangeSeason(Season season, int music)
        {
            Season = season;

            foreach (Chunk chunk in Map.GetUsedChunks())
            {
                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (GameObject obj = chunk?.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                        {
                            obj.UpdateGraphicBySeason();
                        }
                    }
                }
            }

            // the season's graphics change the tiledata flags the max-Z decision reads, without touching the tile lists
            Map.InvalidateDecision();

            //TODO(deccer): refactor this out into _audioPlayer.PlayMusic(...)
            UOMusic currentMusic = Client.Game.Audio.GetCurrentMusic();
            if (currentMusic == null || currentMusic.Index == Client.Game.Audio.LoginMusicIndex)
            {
                Client.Game.Audio.PlayMusic(music, false);
            }
        }


        /*[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CheckToRemove(Entity obj, int distance)
        {
            if (Player == null || obj.Serial == Player.Serial)
                return false;

            return Math.Max(Math.Abs(obj.X - RangeSize.X), Math.Abs(obj.Y - RangeSize.Y)) > distance;
        }
        */

        public void Update()
        {
            if (Player != null)
            {
                // region prefetch around the player and this frame's share of the queued chunk loads (Map.UpdateLoading)
                Map?.UpdateLoading(Player.X, Player.Y);

                if (SerialHelper.IsValid(ObjectToRemove))
                {
                    Item rem = Items.Get(ObjectToRemove);
                    ObjectToRemove = 0;

                    if (rem != null)
                    {
                        Entity container = Get(rem.Container);

                        RemoveItem(rem, true);

                        if (rem.Layer == Layer.OneHanded || rem.Layer == Layer.TwoHanded)
                        {
                            Player.UpdateAbilities();
                        }

                        if (container != null)
                        {
                            if (SerialHelper.IsMobile(container.Serial))
                            {
                                UIManager.GetGump<PaperDollGump>(container.Serial)?.RequestUpdateContents();
                            }
                            else if (SerialHelper.IsItem(container.Serial))
                            {
                                UIManager.GetGump<ContainerGump>(container.Serial)?.RequestUpdateContents();

                                if (container.Graphic == 0x2006)
                                {
                                    UIManager.GetGump<GridLootGump>(container)?.RequestUpdateContents();
                                }
                            }
                        }
                    }
                }

                bool do_delete = _timeToDelete < Time.Ticks;

                if (do_delete)
                {
                    _timeToDelete = Time.Ticks + 50;
                }

                if (WorldEcs.MobileUpdateCheck)
                {
                    // E10: the table against its shadow dictionary every frame, whichever mobile loop runs
                    WorldEcs.TableChecks++;
                    WorldEcs.TableDiffs += Mobiles.VerifyAgainstShadow();
                }

                FrameStats.Begin(FramePhase.WorldMobiles);

                // W5: AddOrUpdate's own early-outs (no recent party/guild packet, the world map closed), asked once per frame;
                // nothing in the loop changes them
                bool wmap = !Settings.GlobalSettings.WorldUpdateActiveItems || WMapManager.AcceptsLocalUpdates;

                if (Settings.GlobalSettings.WorldUpdateMobileSystem && Settings.GlobalSettings.WorldUpdateActiveItems)
                {
                    UpdateMobilesByClock(do_delete, wmap);
                }
                else
                {
                    _mobileClocksSynced = false;
                    Ecs.ClockEnabled = false;
                    _destroyedMobiles.Clear();

                    foreach (Mobile mob in Mobiles.Values)
                    {
                        mob.Update();

                        if (do_delete && mob.Distance > ClientViewRange /*CheckToRemove(mob, ClientViewRange)*/)
                        {
                            RemoveMobile(mob);
                        }

                        if (mob.IsDestroyed)
                        {
                            _toRemove.Add(mob.Serial);
                        }
                        else if (wmap)
                        {
                            if (mob.NotorietyFlag == NotorietyFlag.Ally)
                            {
                                WMapManager.AddOrUpdate
                                (
                                    mob.Serial,
                                    mob.X,
                                    mob.Y,
                                    MathHelper.PercetangeOf(mob.Hits, mob.HitsMax),
                                    MapIndex,
                                    true,
                                    mob.Name
                                );
                            }
                            else if (Party.Leader != 0 && Party.Contains(mob))
                            {
                                WMapManager.AddOrUpdate
                                (
                                    mob.Serial,
                                    mob.X,
                                    mob.Y,
                                    MathHelper.PercetangeOf(mob.Hits, mob.HitsMax),
                                    MapIndex,
                                    false,
                                    mob.Name
                                );
                            }
                        }
                    }

                    if (_toRemove.Count != 0)
                    {
                        for (int i = 0; i < _toRemove.Count; i++)
                        {
                            Mobiles.Remove(_toRemove[i]);
                        }

                        _toRemove.Clear();
                    }
                }

                FrameStats.End(FramePhase.WorldMobiles);
                FrameStats.Begin(FramePhase.WorldItems);

                if (Settings.GlobalSettings.WorldUpdateActiveItems)
                {
                    UpdateActiveItems(do_delete);
                }
                else
                {
                    UpdateAllItems(do_delete);
                }

                FrameStats.End(FramePhase.WorldItems);
                FrameStats.Begin(FramePhase.WorldEffectsText);
                _effectManager.Update();
                WorldTextManager.Update();
                WMapManager.RemoveUnupdatedWEntity();
                FrameStats.End(FramePhase.WorldEffectsText);
                FrameStats.Begin(FramePhase.WorldEcs);
                Ecs.Update();
                FrameStats.End(FramePhase.WorldEcs);
                FrameStats.Set(FrameCounter.Mobiles, Mobiles.Count);
                FrameStats.Set(FrameCounter.Items, Items.Count);
            }
        }

        // E4 (world_update_mobile_system) ---------------------------------------------------------------------------
        private readonly List<uint> _dueMobiles = new List<uint>();
        private readonly List<Mobile> _destroyedMobiles = new List<Mobile>();
        private bool _mobileClocksSynced;
        private int _mobileUpdateStamp;

        internal void OnMobileDestroyed(Mobile mobile)
        {
            // only the clock path reads the queue; with it off the legacy loop finds destroyed mobiles itself
            if (Ecs.ClockEnabled)
            {
                _destroyedMobiles.Add(mobile);
            }
        }

        private readonly List<Mobile> _dueObjects = new List<Mobile>();
        private static readonly System.Comparison<Mobile> s_byOrdinal = (a, b) => a.Ordinal.CompareTo(b.Ordinal);

        /// <summary>
        /// E4/E10: sorts <paramref name="list"/> into the order <c>Mobiles.Values</c> enumerates, which is the order the legacy
        /// loop updated mobiles in and which tile lists, picking ties and footstep sounds depend on. <see cref="MobileTable"/>
        /// keeps each mobile's slot in <see cref="Mobile.Ordinal"/> on add and remove, so this is a plain sort; every mobile in
        /// <paramref name="list"/> must be in <c>Mobiles</c> (the caller fetched it from there this frame).
        /// </summary>
        internal void SortInMobilesOrder(List<Mobile> list)
        {
            list.Sort(s_byOrdinal);
        }

        /// <summary>
        /// The mobile loop of <see cref="Update"/> driven by the ECS clocks. What <c>Mobile.Update</c> would do for a mobile that
        /// is not due is nothing (see <see cref="MobileClockRules.IsDue"/>), so only due mobiles are updated; the rest of the
        /// legacy loop body is split out: the range removal runs in its own pass every 50 ms, destroyed mobiles come from
        /// <see cref="OnMobileDestroyed"/>, and the world-map pass runs only while the map accepts updates.
        /// </summary>
        private void UpdateMobilesByClock(bool do_delete, bool wmap)
        {
            Ecs.ClockEnabled = true;

            if (!_mobileClocksSynced)
            {
                // the clock was not kept while the setting was off (no component, no pushes): build it from the Mobiles
                Ecs.ResyncClocks();
                _mobileClocksSynced = true;
            }

            bool check = WorldEcs.MobileUpdateCheck;

            if (check)
            {
                Ecs.CheckClocks();
            }

            _dueMobiles.Clear();
            long t0 = WorldEcs.MobileUpdateStats ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            Ecs.Clock.CollectDue(Time.Ticks, _dueMobiles);

            if (t0 != 0)
            {
                WorldEcs.ClockCollectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            _mobileUpdateStamp++;
            WorldEcs.ClockFrames++;
            WorldEcs.ClockDue += _dueMobiles.Count;
            WorldEcs.ClockScanned += Ecs.Clock.Scanned;

            // the due list follows archetype order, which equipment tags reshuffle; the legacy loop's order decides tile lists,
            // picking ties and which footstep wins, so the due mobiles run in Mobiles.Values order
            _dueObjects.Clear();

            for (int i = 0; i < _dueMobiles.Count; i++)
            {
                Mobile due = Mobiles.Get(_dueMobiles[i]);

                if (due != null)
                {
                    _dueObjects.Add(due);
                }
            }

            SortInMobilesOrder(_dueObjects);

            if (check)
            {
                Ecs.CheckOrder(Mobiles.Values, _dueObjects);
            }

            for (int i = 0; i < _dueObjects.Count; i++)
            {
                Mobile mob = _dueObjects[i];

                mob.UpdateStamp = _mobileUpdateStamp;
                mob.Update();

                // Update rewrote the timers (and may have emptied the step queue or opened the handles)
                Ecs.PushClock(mob);
            }

            if (t0 != 0)
            {
                WorldEcs.ClockUpdateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }

            if (check)
            {
                Ecs.CheckSkipped(Mobiles.Values, _mobileUpdateStamp);
            }

            if (do_delete)
            {
                foreach (Mobile mob in Mobiles.Values)
                {
                    if (mob.Distance > ClientViewRange)
                    {
                        RemoveMobile(mob);
                    }
                }
            }

            for (int i = 0; i < _destroyedMobiles.Count; i++)
            {
                Mobile dead = _destroyedMobiles[i];

                // a newer mobile may hold the serial by now
                if (ReferenceEquals(Mobiles.Get(dead.Serial), dead))
                {
                    Mobiles.Remove(dead.Serial);
                }
            }

            _destroyedMobiles.Clear();

            if (wmap)
            {
                foreach (Mobile mob in Mobiles.Values)
                {
                    if (mob.IsDestroyed)
                    {
                        continue;
                    }

                    if (mob.NotorietyFlag == NotorietyFlag.Ally)
                    {
                        WMapManager.AddOrUpdate(mob.Serial, mob.X, mob.Y, MathHelper.PercetangeOf(mob.Hits, mob.HitsMax), MapIndex, true, mob.Name);
                    }
                    else if (Party.Leader != 0 && Party.Contains(mob))
                    {
                        WMapManager.AddOrUpdate(mob.Serial, mob.X, mob.Y, MathHelper.PercetangeOf(mob.Hits, mob.HitsMax), MapIndex, false, mob.Name);
                    }
                }
            }
        }

        // W5 (world_update_active_items): items whose Update does something this frame (Item.NeedsFrameUpdate)
        private readonly List<Item> _activeItems = new List<Item>();
        // items destroyed since the last update; World.Update drops them from Items as the full loop did
        private readonly List<Item> _destroyedItems = new List<Item>();
        private bool _activeItemsValid;

        /// <summary>Items in the per-frame update list (corpses, items whose handles just opened).</summary>
        public int ActiveItemCount => _activeItems.Count;

        /// <summary>T1 tests: the per-frame update list in order.</summary>
        internal IReadOnlyList<Item> ActiveItems => _activeItems;

        /// <summary>W5: <paramref name="item"/> needs <see cref="Item.Update"/> from now on (a corpse, or handles opened).</summary>
        public void QueueItemUpdate(Item item)
        {
            if (!item.InUpdateList)
            {
                item.InUpdateList = true;
                _activeItems.Add(item);
            }
        }

        // corpses and handle openings keep queuing while the setting is off (WorldEcs, the scene), so the full loop drains the
        // list each frame: it must not keep destroyed items alive. Turning the setting on rebuilds it from a scan
        private void ClearActiveItems()
        {
            if (_activeItems.Count == 0)
            {
                return;
            }

            foreach (Item item in _activeItems)
            {
                item.InUpdateList = false;
            }

            _activeItems.Clear();
        }

        internal void OnItemDestroyed(Item item)
        {
            _destroyedItems.Add(item);
            _destroyedSeen++;
        }

        // world_update_active_items off: every item, every frame (ClassicUO's loop)
        private void UpdateAllItems(bool do_delete)
        {
            _activeItemsValid = false;
            _destroyedItems.Clear();
            ClearActiveItems();

            foreach (Item item in Items.Values)
            {
                item.Update();

                if (do_delete && item.OnGround && item.Distance > ClientViewRange /*CheckToRemove(item, ClientViewRange)*/)
                {
                    if (item.IsMulti)
                    {
                        if (HouseManager.TryToRemove(item, ClientViewRange))
                        {
                            RemoveItem(item);
                        }
                    }
                    else
                    {
                        RemoveItem(item);
                    }
                }

                if (item.IsDestroyed)
                {
                    _toRemove.Add(item.Serial);
                }
            }

            if (_toRemove.Count != 0)
            {
                for (int i = 0; i < _toRemove.Count; i++)
                {
                    Items.Remove(_toRemove[i]);
                }

                _toRemove.Clear();
            }
        }

        // W5: the same work as UpdateAllItems. Item.Update is a no-op for every item not in the active list
        // (Item.NeedsFrameUpdate), the range removal only runs every 50 ms (do_delete), and destroyed items are known
        // from Entity.Destroy, so the full pass runs only on do_delete frames.
        private void UpdateActiveItems(bool do_delete)
        {
            if (ActiveCheck)
            {
                CheckActiveItems();
            }

            if (!_activeItemsValid)
            {
                // first frame on (or after the setting was off): find every item that needs updates
                _activeItemsValid = true;

                foreach (Item item in Items.Values)
                {
                    if (item.NeedsFrameUpdate)
                    {
                        QueueItemUpdate(item);
                    }
                }
            }

            for (int i = 0; i < _activeItems.Count; i++)
            {
                Item item = _activeItems[i];

                item.Update();

                if (!item.NeedsFrameUpdate || !ReferenceEquals(Items.Get(item.Serial), item))
                {
                    item.InUpdateList = false;
                    _activeItems[i] = _activeItems[_activeItems.Count - 1];
                    _activeItems.RemoveAt(_activeItems.Count - 1);
                    i--;
                }
            }

            if (do_delete)
            {
                foreach (Item item in Items.Values)
                {
                    if (item.OnGround && item.Distance > ClientViewRange)
                    {
                        if (item.IsMulti)
                        {
                            if (HouseManager.TryToRemove(item, ClientViewRange))
                            {
                                RemoveItem(item);
                            }
                        }
                        else
                        {
                            RemoveItem(item);
                        }
                    }

                    if (item.IsDestroyed)
                    {
                        _toRemove.Add(item.Serial);
                    }
                    else if (!item.InUpdateList && item.NeedsFrameUpdate)
                    {
                        // a corpse the change hooks missed: update it from now on
                        QueueItemUpdate(item);
                        MissedActiveItems++;
                    }
                }
            }

            QueueDestroyedForRemoval();

            if (_toRemove.Count != 0)
            {
                for (int i = 0; i < _toRemove.Count; i++)
                {
                    Items.Remove(_toRemove[i]);
                }

                _toRemove.Clear();
            }

            if (ActiveCheck)
            {
                // the full loop left no destroyed item in Items
                foreach (Item item in Items.Values)
                {
                    if (item.IsDestroyed)
                    {
                        _checkDestroyedLeft++;
                    }
                }
            }
        }

        // the destroyed items World still holds (not a newer object with the same serial) go to _toRemove
        private void QueueDestroyedForRemoval()
        {
            for (int i = 0; i < _destroyedItems.Count; i++)
            {
                Item item = _destroyedItems[i];

                if (item.IsDestroyed && Items.TryGetValue(item.Serial, out Item current) && ReferenceEquals(current, item))
                {
                    _toRemove.Add(item.Serial);
                }
            }

            _destroyedItems.Clear();
        }

        /// <summary>W5: drops the items destroyed since the last update from <see cref="Items"/>, as World.Update does
        /// (exposed for the unit tests). Returns how many were dropped.</summary>
        internal int DropDestroyedItems()
        {
            QueueDestroyedForRemoval();
            int n = _toRemove.Count;

            for (int i = 0; i < _toRemove.Count; i++)
            {
                Items.Remove(_toRemove[i]);
            }

            _toRemove.Clear();

            return n;
        }

        /// <summary>Items the 50 ms sweep found needing updates that no hook had queued (should stay 0).</summary>
        public int MissedActiveItems;

        // CUO_W5_CHECK=1: every frame, before the active update, every item is checked against what the full loop would
        // have done: an item whose Update does something must be listed, and every entity's hits percentage must equal
        // the per-frame rule's. Logged every 5 s as [w5-check]. Costs a full pass per frame; don't measure with it on.
        public static readonly bool ActiveCheck = System.Environment.GetEnvironmentVariable("CUO_W5_CHECK") == "1";
        private long _checkFrames, _checkItems, _checkUnlisted, _checkHits, _checkHitsWrong, _checkCorpses, _checkHandles, _checkDestroyedLeft;
        private int _destroyedSeen;
        private uint _checkNextLog;

        private void CheckActiveItems()
        {
            _checkFrames++;

            foreach (Item item in Items.Values)
            {
                _checkItems++;

                if (item.NeedsFrameUpdate)
                {
                    if (item.IsCorpse)
                    {
                        _checkCorpses++;
                    }
                    else
                    {
                        _checkHandles++;
                    }

                    if (!item.InUpdateList && _activeItemsValid)
                    {
                        _checkUnlisted++;

                        if (_checkUnlisted <= 10)
                        {
                            Log.Warn($"[w5-check] unlisted item 0x{item.Serial:X8} graphic 0x{item.Graphic:X4} corpse={item.IsCorpse} handles={item.ObjectHandlesStatus}");
                        }
                    }
                }

                CheckHits(item);
            }

            foreach (Mobile mob in Mobiles.Values)
            {
                CheckHits(mob);
            }

            if (Time.Ticks >= _checkNextLog)
            {
                _checkNextLog = Time.Ticks + 5000;
                Log.Info($"[w5-check] {_checkFrames} frames, {_checkItems} item checks: {_checkUnlisted} needing an update but unlisted, {MissedActiveItems} caught by the sweep; {_checkCorpses} corpse and {_checkHandles} handle updates seen; {_checkHits} hits checks, {_checkHitsWrong} wrong; {_destroyedSeen} items destroyed, {_checkDestroyedLeft} left in Items after the update; active list {_activeItems.Count}; text layouts skipped {_checkTextSkips}, {_checkTextMoved} would have moved a text");
            }
        }

        private long _checkTextSkips, _checkTextMoved;

        /// <summary>CUO_W5_CHECK: the text layout the overhead text pass skipped (text_skip_expired) must place nothing.</summary>
        internal void CheckSkippedTextLayout(Entity e)
        {
            System.Span<(int X, int Y, int OffsetY)> before = stackalloc (int, int, int)[16];
            int n = 0;

            for (TextObject t = (TextObject)e.TextContainer.Items; t != null && n < before.Length; t = (TextObject)t.Next)
            {
                before[n++] = (t.RealScreenPosition.X, t.RealScreenPosition.Y, t.OffsetY);
            }

            e.UpdateTextCoordsV();
            _checkTextSkips++;
            int i = 0;
            bool moved = false;

            for (TextObject t = (TextObject)e.TextContainer.Items; t != null && i < n; t = (TextObject)t.Next, i++)
            {
                if (t.RealScreenPosition.X != before[i].X || t.RealScreenPosition.Y != before[i].Y || t.OffsetY != before[i].OffsetY)
                {
                    moved = true;
                    t.RealScreenPosition.X = before[i].X;
                    t.RealScreenPosition.Y = before[i].Y;
                    t.OffsetY = before[i].OffsetY;
                }
            }

            if (moved && _checkTextMoved++ < 10)
            {
                Log.Warn($"[w5-check] skipped text layout of 0x{e.Serial:X8} would have moved a text");
            }
        }

        private void CheckHits(Entity e)
        {
            if (e.HitsMax > 0 && !e.IsDestroyed)
            {
                _checkHits++;

                if (e.HitsPercentage != Entity.HitsPercent(e.Hits, e.HitsMax))
                {
                    _checkHitsWrong++;

                    if (_checkHitsWrong <= 10)
                    {
                        Log.Warn($"[w5-check] hits 0x{e.Serial:X8} {e.Hits}/{e.HitsMax} percentage {e.HitsPercentage}");
                    }
                }
            }
        }

        public bool Contains(uint serial)
        {
            if (SerialHelper.IsItem(serial))
            {
                return Items.Contains(serial);
            }

            return SerialHelper.IsMobile(serial) && Mobiles.Contains(serial);
        }

        public Entity Get(uint serial)
        {
            Entity ent;

            if (SerialHelper.IsMobile(serial))
            {
                ent = Mobiles.Get(serial);

                if (ent == null)
                {
                    ent = Items.Get(serial);
                }
            }
            else
            {
                ent = Items.Get(serial);

                if (ent == null)
                {
                    ent = Mobiles.Get(serial);
                }
            }

            if (ent != null && ent.IsDestroyed)
            {
                ent = null;
            }

            return ent;
        }

        public Item GetOrCreateItem(uint serial)
        {
            Item item = GetOrCreateItem(serial, out bool isNew);

            if (isNew)
            {
                Ecs.Sync(item);
            }

            return item;
        }

        /// <summary>T1: <see cref="GetOrCreateItem(uint)"/> without the ECS write; the funnel commits the new item itself.</summary>
        internal Item GetOrCreateItem(uint serial, out bool isNew)
        {
            Item item = Items.Get(serial);

            if (item != null && item.IsDestroyed)
            {
                Items.Remove(serial);
                item = null;
            }

            isNew = item == null;

            if (isNew /*|| item.IsDestroyed*/)
            {
                item = Item.Create(this, serial);
                Items.Add(item);
            }

            return item;
        }

        public Mobile GetOrCreateMobile(uint serial)
        {
            Mobile mob = Mobiles.Get(serial);

            if (mob != null && mob.IsDestroyed)
            {
                Mobiles.Remove(serial);
                mob = null;
            }

            if (mob == null /*|| mob.IsDestroyed*/)
            {
                mob = Mobile.Create(this, serial);
                Mobiles.Add(mob);
                Ecs.Sync(mob);
            }

            return mob;
        }

        public void RemoveItemFromContainer(uint serial)
        {
            Item it = Items.Get(serial);

            if (it != null)
            {
                RemoveItemFromContainer(it);
            }
        }

        public void RemoveItemFromContainer(Item obj)
        {
            uint containerSerial = obj.Container;
            UnlinkFromContainer(obj, containerSerial);

            // if entity is running the "dying" animation we have to reset container too.
            // SerialHelper.IsValid(containerSerial) is not ideal in this case
            if (containerSerial != 0xFFFF_FFFF)
            {
                obj.Container = 0xFFFF_FFFF;
            }

            Ecs.Sync(obj);
        }

        /// <summary>
        /// The link half of <see cref="RemoveItemFromContainer(Item)"/>: the parent's gump, the parent's child list, the item's
        /// list links and its tile. It writes no world state of the item (its Container stays as it is, and nothing syncs): the
        /// caller writes the resulting Container itself (T1: <see cref="WorldWriter"/> computes it, commits it to the ECS and the
        /// cache, never reading it back).
        /// </summary>
        internal void UnlinkFromContainer(Item obj, uint containerSerial)
        {
            if (containerSerial != 0xFFFF_FFFF)
            {
                if (SerialHelper.IsMobile(containerSerial))
                {
                    UIManager.GetGump<PaperDollGump>(containerSerial)?.RequestUpdateContents();
                }
                else if (SerialHelper.IsItem(containerSerial))
                {
                    UIManager.GetGump<ContainerGump>(containerSerial)?.RequestUpdateContents();
                }

                Entity container = Get(containerSerial);

                if (container != null)
                {
                    container.Remove(obj);
                }
            }

            obj.Next = null;
            obj.Previous = null;
            obj.RemoveFromTile();
        }

        public bool RemoveItem(uint serial, bool forceRemove = false)
        {
            Item item = Items.Get(serial);

            if (item == null || item.IsDestroyed)
            {
                return false;
            }

            LinkedObject first = item.Items;
            RemoveItemFromContainer(item);

            while (first != null)
            {
                LinkedObject next = first.Next;

                RemoveItem(first as Item, forceRemove);

                first = next;
            }

            OPL.Remove(serial);
            item.Destroy();

            if (forceRemove)
            {
                Items.Remove(serial);
            }

            return true;
        }

        public bool RemoveMobile(uint serial, bool forceRemove = false)
        {
            Mobile mobile = Mobiles.Get(serial);

            if (mobile == null || mobile.IsDestroyed)
            {
                return false;
            }

            LinkedObject first = mobile.Items;

            while (first != null)
            {
                LinkedObject next = first.Next;

                RemoveItem(first as Item, forceRemove);

                first = next;
            }

            OPL.Remove(serial);
            mobile.Destroy();

            if (forceRemove)
            {
                Mobiles.Remove(serial);
            }

            return true;
        }

        public void SpawnEffect
        (
            GraphicEffectType type,
            uint source,
            uint target,
            ushort graphic,
            ushort hue,
            ushort srcX,
            ushort srcY,
            sbyte srcZ,
            ushort targetX,
            ushort targetY,
            sbyte targetZ,
            byte speed,
            int duration,
            bool fixedDir,
            bool doesExplode,
            bool hasparticles,
            GraphicEffectBlendMode blendmode
        )
        {
            _effectManager.CreateEffect
            (
                type,
                source,
                target,
                graphic,
                hue,
                srcX,
                srcY,
                srcZ,
                targetX,
                targetY,
                targetZ,
                speed,
                duration,
                fixedDir,
                doesExplode,
                hasparticles,
                blendmode
            );
        }

        public uint FindNearest(ScanTypeObject scanType)
        {
            int distance = int.MaxValue;
            uint serial = 0;

            if (scanType == ScanTypeObject.Objects)
            {
                foreach (Item item in Items.Values)
                {
                    if (item.IsMulti || item.IsDestroyed || !item.OnGround)
                    {
                        continue;
                    }

                    if (item.Distance < distance)
                    {
                        distance = item.Distance;
                        serial = item.Serial;
                    }
                }
            }
            else
            {
                foreach (Mobile mobile in Mobiles.Values)
                {
                    if (mobile.IsDestroyed || mobile == Player)
                    {
                        continue;
                    }

                    switch (scanType)
                    {
                        case ScanTypeObject.Party:
                            if (!Party.Contains(mobile))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Followers:
                            if (!(mobile.IsRenamable && mobile.NotorietyFlag != NotorietyFlag.Invulnerable && mobile.NotorietyFlag != NotorietyFlag.Enemy))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Hostile:
                            if (mobile.NotorietyFlag == NotorietyFlag.Ally || mobile.NotorietyFlag == NotorietyFlag.Innocent || mobile.NotorietyFlag == NotorietyFlag.Invulnerable)
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Objects:
                            /* This was handled separately above */
                            continue;
                    }

                    if (mobile.Distance < distance)
                    {
                        distance = mobile.Distance;
                        serial = mobile.Serial;
                    }
                }
            }

            return serial;
        }

        private readonly List<Item> _scanItems = new List<Item>();
        private readonly List<Mobile> _scanMobiles = new List<Mobile>();

        public uint FindNext(ScanTypeObject scanType, uint lastSerial, bool reverse)
        {
            try
            {
                return FindNextCore(scanType, lastSerial, reverse);
            }
            finally
            {
                // don't keep the world's objects alive between scans
                _scanItems.Clear();
                _scanMobiles.Clear();
            }
        }

        private uint FindNextCore(ScanTypeObject scanType, uint lastSerial, bool reverse)
        {
            bool found = false;

            if (scanType == ScanTypeObject.Objects)
            {
                // W5: the same order as Items.Values / .Reverse(), without LINQ's per-call copy and the boxed enumerator
                List<Item> items = _scanItems;
                items.Clear();
                items.AddRange(Items.Values);

                if (reverse)
                {
                    items.Reverse();
                }

                foreach (Item item in items)
                {
                    if (item.IsMulti || item.IsDestroyed || !item.OnGround)
                    {
                        continue;
                    }

                    if (lastSerial == 0)
                    {
                        return item.Serial;
                    }
                    else if (item.Serial == lastSerial)
                    {
                        found = true;
                    }
                    else if (found)
                    {
                        return item.Serial;
                    }
                }
            }
            else
            {
                List<Mobile> mobiles = _scanMobiles;
                mobiles.Clear();
                mobiles.Capacity = System.Math.Max(mobiles.Capacity, Mobiles.Count);

                foreach (Mobile m in Mobiles.Values)
                {
                    mobiles.Add(m);
                }

                if (reverse)
                {
                    mobiles.Reverse();
                }

                foreach (Mobile mobile in mobiles)
                {
                    if (mobile.IsDestroyed || mobile == Player)
                    {
                        continue;
                    }

                    switch (scanType)
                    {
                        case ScanTypeObject.Party:
                            if (!Party.Contains(mobile))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Followers:
                            if (!(mobile.IsRenamable && mobile.NotorietyFlag != NotorietyFlag.Invulnerable && mobile.NotorietyFlag != NotorietyFlag.Enemy))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Hostile:
                            if (mobile.NotorietyFlag == NotorietyFlag.Ally || mobile.NotorietyFlag == NotorietyFlag.Innocent || mobile.NotorietyFlag == NotorietyFlag.Invulnerable)
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Objects:
                            /* This was handled separately above */
                            continue;
                    }

                    if (lastSerial == 0)
                    {
                        return mobile.Serial;
                    }
                    else if (mobile.Serial == lastSerial)
                    {
                        found = true;
                    }
                    else if (found)
                    {
                        return mobile.Serial;
                    }
                }
            }

            if (lastSerial != 0)
            {
                /* If we get here, it means we didn't find anything but we started with a serial number. That means
                 * if we restart the search from the beginning it may find something again. */
                return FindNext(scanType, 0, reverse);
            }

            return 0;
        }


        public void Clear()
        {
            TextOwners.Clear();

            foreach (Mobile mobile in Mobiles.Values)
            {
                RemoveMobile(mobile);
            }

            foreach (Item item in Items.Values)
            {
                RemoveItem(item);
            }

            UIManager.GetGump<BaseHealthBarGump>(Player?.Serial)?.Dispose();

            ObjectToRemove = 0;
            LastObject = 0;
            Items.Clear();
            Mobiles.Clear();
            _destroyedMobiles.Clear();
            Player?.Destroy();
            Player = null;
            Map?.Destroy();
            Map = null;
            Light.Overall = Light.RealOverall = 0;
            Light.Personal = Light.RealPersonal = 0;
            ClientLockedFeatures.SetFlags(0);
            Party?.Clear();
            TargetManager.LastAttack = 0;
            MessageManager.PromptData = default;
            _effectManager.Clear();
            _toRemove.Clear();

            ClearActiveItems();
            _destroyedItems.Clear();
            _activeItemsValid = false;
            CorpseManager.Clear();
            OPL.Clear();
            WMapManager.Clear();
            HouseManager?.Clear();

            Season = Season.Summer;
            OldSeason = Season.Summer;

            Journal.Clear();
            WorldTextManager.Clear();
            ActiveSpellIcons.Clear();

            SkillsRequested = false;
            WarmupDone = false; // the next login tops the pools up (they stay: same World, bounded)
            Ecs.OnWorldCleared();
        }

        private void InternalMapChangeClear(bool noplayer)
        {
            if (!noplayer)
            {
                Map.Destroy();
                Map = null;
                Player.Destroy();
                Player = null;
            }

            foreach (Item item in Items.Values)
            {
                if (noplayer && Player != null && !Player.IsDestroyed)
                {
                    if (item.RootContainer == Player)
                    {
                        continue;
                    }
                }

                if (item.OnGround && item.IsMulti)
                {
                    HouseManager.Remove(item.Serial);
                }

                _toRemove.Add(item);
            }

            foreach (uint serial in _toRemove)
            {
                RemoveItem(serial, true);
            }

            _toRemove.Clear();

            foreach (Mobile mob in Mobiles.Values)
            {
                if (noplayer && Player != null && !Player.IsDestroyed)
                {
                    if (mob == Player)
                    {
                        continue;
                    }
                }

                _toRemove.Add(mob);
            }

            foreach (uint serial in _toRemove)
            {
                RemoveMobile(serial, true);
            }

            _toRemove.Clear();
        }
    }
}