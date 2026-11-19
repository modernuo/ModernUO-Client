// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Loop;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static ClassicUO.Game.UI.Gumps.WorldMapGump;
using SpriteFont = ClassicUO.Renderer.SpriteFont;

namespace ClassicUO.Game.UI.Gumps
{

    internal partial class WorldMapGump : ResizableGump, IGumpDeferredDraw
    {
        public const string USER_MARKERS_FILE = "userMarkers";

        private static readonly string _mapFilesPath = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client");
        private static readonly string _mapIconsPath = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", "MapIcons");
        private static readonly string _mapsCachePath = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", "MapsCache");
        private static readonly string UserMarkersFilePath = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", $"{USER_MARKERS_FILE}.usr");
        private static readonly Dictionary<string, string> _mapCache = new();
        public static readonly List<WMapMarkerFile> _markerFiles = new List<WMapMarkerFile>();
        public static readonly Dictionary<string, Texture2D> _markerIcons = new Dictionary<string, Texture2D>();
        private static readonly float[] _zooms = new float[10] { 0.125f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 4f, 6f, 8f };
        private static readonly Color _semiTransparentWhiteForGrid = new Color(255, 255, 255, 56);
        private static Point _last_position = new Point(100, 100);
        // game thread only: adopted in Update, released in ChangeMap, disposed in Dispose, never touched by the load task
        private readonly WorldMapTextureOwner<Texture2D> _mapTexture = new WorldMapTextureOwner<Texture2D>();
        private Map.FacetView _map;

        private Point _center, _lastScroll, _mouseCenter, _scroll;
        private Point? _lastMousePosition = null;
        private readonly WorldMapCoordinateText _playerCoordsText = new WorldMapCoordinateText();
        private readonly WorldMapCoordinateText _mouseCoordsText = new WorldMapCoordinateText();
        private bool _flipMap = true;
        private bool _freeView;
        private List<string> _hiddenMarkerFiles;
        private bool _isScrolling;
        private bool _isTopMost;
        private bool _mapMarkersLoaded;
        private List<string> _hiddenZoneFiles;
        private ZoneSets _zoneSets = new ZoneSets();
        private SpriteFont _markerFont = Fonts.Map1;
        private int _markerFontIndex = 1;
        private readonly Dictionary<string, ContextMenuItemEntry> _options = new Dictionary<string, ContextMenuItemEntry>();
        private bool _showCoordinates;
        private bool _showSextantCoordinates;
        private bool _showMouseCoordinates;
        private bool _showGroupBar = true;
        private bool _showGroupName = true;
        private bool _showMarkerIcons = true;
        private bool _showMarkerNames = true;
        private bool _showMarkers = true;
        private bool _showMobiles = true;
        private bool _showMultis = true;
        private bool _showPartyMembers = true;
        private bool _showPlayerBar = true;
        private bool _showPlayerName = true;
        private int _zoomIndex = 4;
        private bool _showGridIfZoomed = true;
        private bool _allowPositionalTarget = false;
        private WMapMarker _gotoMarker;

        private int _mapLoading;
        // loads started by ChangeMap (game thread) and not finished by their task: worldmap_idle_loop polls while > 0
        private int _loadsRunning;
        private Task _loadingTask;
        private readonly MapImageHandover<WorldMapImage> _mapImages = new MapImageHandover<WorldMapImage>();

        // game thread: constructed and not yet disposed world map gumps. GameScene's loop hook and
        // WorldMapEntityManager.Enabled read it instead of walking the gump list (UIManager.GetGump) every frame and call.
        private static int _openCount;
        private bool _countedOpen;

        public WorldMapGump(World world) : base
        (
            world,
            400,
            400,
            100,
            100,
            0,
            0
        )
        {
            CanMove = true;
            AcceptMouseInput = true;
            CanCloseWithRightClick = false;

            X = _last_position.X;
            Y = _last_position.Y;

            _map = new Map.FacetView(World.Map.Index);
            LoadSettings();

            GameActions.Print(World, ResGumps.WorldMapLoading, 0x35);
            ChangeMap(World.MapIndex);
            OnResize();

            LoadMarkers();
            LoadZones();

            BuildGump();

            NoteOpened(ref _countedOpen);
        }

        /// <summary>A world map gump exists and is not disposed (the old test was UIManager.GetGump&lt;WorldMapGump&gt;() != null).</summary>
        internal static bool IsOpen => _openCount > 0;

        internal static int OpenCount => _openCount;

        internal static void NoteOpened(ref bool counted)
        {
            if (!counted)
            {
                counted = true;
                _openCount++;
            }
        }

        internal static void NoteClosed(ref bool counted)
        {
            if (counted)
            {
                counted = false;
                _openCount--;
            }
        }

        public override GumpType GumpType => GumpType.WorldMap;
        public float Zoom => _zooms[_zoomIndex];

        public bool TopMost
        {
            get => _isTopMost;
            set
            {
                if (_isTopMost != value)
                {
                    _isTopMost = value;

                    SaveSettings();
                }

                ShowBorder = !_isTopMost;

                LayerOrder = _isTopMost ? UILayer.Over : UILayer.Under;
            }
        }

        public bool FreeView
        {
            get => _freeView;
            set
            {
                if (_freeView != value)
                {
                    _freeView = value;
                    SaveSettings();

                    if (!_freeView)
                    {
                        _isScrolling = false;
                        CanMove = true;
                    }
                }
            }
        }

        public override void Restore(XmlElement xml)
        {
            base.Restore(xml);

            BuildGump();
        }

        private void LoadSettings()
        {
            Width = ProfileManager.CurrentProfile.WorldMapWidth;
            Height = ProfileManager.CurrentProfile.WorldMapHeight;

            SetFont(ProfileManager.CurrentProfile.WorldMapFont);

            ResizeWindow(new Point(Width, Height));

            _flipMap = ProfileManager.CurrentProfile.WorldMapFlipMap;
            _showPartyMembers = ProfileManager.CurrentProfile.WorldMapShowParty;

            World.WMapManager.SetEnable(_showPartyMembers);

            // a hand-edited profile must not index past the zoom table
            _zoomIndex = Math.Clamp(ProfileManager.CurrentProfile.WorldMapZoomIndex, 0, _zooms.Length - 1);

            _showCoordinates = ProfileManager.CurrentProfile.WorldMapShowCoordinates;
            _showSextantCoordinates = ProfileManager.CurrentProfile.WorldMapShowSextantCoordinates;
            _showMouseCoordinates = ProfileManager.CurrentProfile.WorldMapShowMouseCoordinates;
            _showMobiles = ProfileManager.CurrentProfile.WorldMapShowMobiles;

            _showPlayerName = ProfileManager.CurrentProfile.WorldMapShowPlayerName;
            _showPlayerBar = ProfileManager.CurrentProfile.WorldMapShowPlayerBar;
            _showGroupName = ProfileManager.CurrentProfile.WorldMapShowGroupName;
            _showGroupBar = ProfileManager.CurrentProfile.WorldMapShowGroupBar;
            _showMarkers = ProfileManager.CurrentProfile.WorldMapShowMarkers;
            _showMultis = ProfileManager.CurrentProfile.WorldMapShowMultis;
            _showMarkerNames = ProfileManager.CurrentProfile.WorldMapShowMarkersNames;
            _showMarkerIcons = ProfileManager.CurrentProfile.WorldMapShowMarkerIcons;

            _hiddenMarkerFiles = string.IsNullOrEmpty(ProfileManager.CurrentProfile.WorldMapHiddenMarkerFiles) ? new List<string>() : ProfileManager.CurrentProfile.WorldMapHiddenMarkerFiles.Split(',').ToList();
            _hiddenZoneFiles = string.IsNullOrEmpty(ProfileManager.CurrentProfile.WorldMapHiddenZoneFiles) ? new List<string>() : ProfileManager.CurrentProfile.WorldMapHiddenZoneFiles.Split(',').ToList();

            _showGridIfZoomed = ProfileManager.CurrentProfile.WorldMapShowGridIfZoomed;
            _allowPositionalTarget = ProfileManager.CurrentProfile.WorldMapAllowPositionalTarget;
            TopMost = ProfileManager.CurrentProfile.WorldMapTopMost;
            FreeView = ProfileManager.CurrentProfile.WorldMapFreeView;
        }

        public void SaveSettings()
        {
            if (ProfileManager.CurrentProfile == null)
            {
                return;
            }

            ProfileManager.CurrentProfile.WorldMapWidth = Width;
            ProfileManager.CurrentProfile.WorldMapHeight = Height;
            ProfileManager.CurrentProfile.WorldMapFont = _markerFontIndex;

            ProfileManager.CurrentProfile.WorldMapFlipMap = _flipMap;
            ProfileManager.CurrentProfile.WorldMapTopMost = TopMost;
            ProfileManager.CurrentProfile.WorldMapFreeView = FreeView;
            ProfileManager.CurrentProfile.WorldMapShowParty = _showPartyMembers;

            ProfileManager.CurrentProfile.WorldMapZoomIndex = _zoomIndex;

            ProfileManager.CurrentProfile.WorldMapShowCoordinates = _showCoordinates;
            ProfileManager.CurrentProfile.WorldMapShowSextantCoordinates = _showSextantCoordinates;
            ProfileManager.CurrentProfile.WorldMapShowMouseCoordinates = _showMouseCoordinates;
            ProfileManager.CurrentProfile.WorldMapShowMobiles = _showMobiles;

            ProfileManager.CurrentProfile.WorldMapShowPlayerName = _showPlayerName;
            ProfileManager.CurrentProfile.WorldMapShowPlayerBar = _showPlayerBar;
            ProfileManager.CurrentProfile.WorldMapShowGroupName = _showGroupName;
            ProfileManager.CurrentProfile.WorldMapShowGroupBar = _showGroupBar;
            ProfileManager.CurrentProfile.WorldMapShowMarkers = _showMarkers;
            ProfileManager.CurrentProfile.WorldMapShowMultis = _showMultis;
            ProfileManager.CurrentProfile.WorldMapShowMarkersNames = _showMarkerNames;
            ProfileManager.CurrentProfile.WorldMapShowMarkerIcons = _showMarkerIcons;

            ProfileManager.CurrentProfile.WorldMapHiddenMarkerFiles = string.Join(",", _hiddenMarkerFiles);
            ProfileManager.CurrentProfile.WorldMapHiddenZoneFiles = string.Join(",", _hiddenZoneFiles);

            ProfileManager.CurrentProfile.WorldMapShowGridIfZoomed = _showGridIfZoomed;
            ProfileManager.CurrentProfile.WorldMapAllowPositionalTarget = _allowPositionalTarget;
        }

        #region Update

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

#if CUO_DEVTOOLS
            if (WorldMapIdleCheck.On)
            {
                WorldMapIdleCheck.BeforeUpdate(this, World, IdleLoopEnabled);
            }
#endif
            WorldMapImage image = _mapImages.Take();

            if (image != null)
            {
                int width = image.Width, height = image.Height;
                SurfaceFormat format = image.Format;

                // the owner has settled (texture kept, or disposed on failure) before anything is logged
                if (_mapTexture.Adopt(image, _allocateMapTexture, _fillMapTexture, out Exception error))
                {
                    Log.Info($"[worldmap] texture {width}x{height} {format} uploaded (gump {ScreenCoordinateX},{ScreenCoordinateY} {Width}x{Height})");
                    GameActions.Print(World, ResGumps.WorldMapLoaded, 0x48);
                }
                else
                {
                    // Draw skips the map until the next load
                    Log.Error($"error uploading worldmap texture: {error}");
                }
            }

            if (_map.Index != World.MapIndex && !_freeView)
                ChangeMap(World.MapIndex);

            World.WMapManager.RequestServerPartyGuildInfo();
#if CUO_DEVTOOLS
            if (WorldMapIdleCheck.On)
            {
                WorldMapIdleCheck.AfterUpdate(DrawnStateHash());
            }
#endif
        }

#if CUO_DEVTOOLS
        // CUO_WORLDMAP_IDLE_CHECK: the map's own drawn state (facet, view, texture, load, packet-only party/guild entities).
        // Mobiles and entities mirrored from them are world state: they change with packets or the visual deadlines.
        internal int DrawnStateHash()
        {
            var hash = new HashCode();
            hash.Add(_map.Index);
            hash.Add(_zoomIndex);
            hash.Add(_center);
            hash.Add(_flipMap);
            hash.Add(_freeView);
            hash.Add(_mapTexture.Texture == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_mapTexture.Texture));
            hash.Add(Volatile.Read(ref _mapLoading));
            hash.Add(_mapImages.HasPending);
            hash.Add(World.WMapManager.Entities.Count);

            foreach (WMapEntity e in World.WMapManager.Entities.Values)
            {
                if (World.FindMobile(e.Serial) == null)
                {
                    hash.Add(e.Serial);
                    hash.Add(e.X);
                    hash.Add(e.Y);
                    hash.Add(e.HP);
                    hash.Add(e.Map);
                }
            }

            return hash.ToHashCode();
        }

        internal long PostedAt => _mapImages.PostedAt;

        internal bool LoadPendingImage => _mapImages.HasPending;
#endif

        /// <summary>A map image load is running, or its image is posted and not uploaded yet.</summary>
        internal bool LoadPending => Volatile.Read(ref _loadsRunning) != 0 || _mapImages.HasPending;

#if CUO_DEVTOOLS
        // the gates' override of worldmap_idle_loop (CUO_WORLDMAP_IDLE_LOOP=1|0)
        private static readonly bool? _idleLoopEnv = Environment.GetEnvironmentVariable("CUO_WORLDMAP_IDLE_LOOP") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _idleLoopEnv = null;
#endif

        internal static bool IdleLoopEnabled => _idleLoopEnv ?? Settings.GlobalSettings.WorldMapIdleLoop;

        /// <summary>GameScene.NoteLoopDue while a world map is open (event_loop_idle_wait).</summary>
        internal static void NoteLoopDue(World world)
        {
            if (!IdleLoopEnabled)
            {
                LoopDue.Now(LoopDueSource.WorldMap);

                return;
            }

            WorldMapGump gump = UIManager.GetGump<WorldMapGump>();
            WorldMapEntityManager entities = world.WMapManager;

            NoteLoopDue(true, gump == null || gump.LoadPending, entities.Enabled, entities.NextQueryTick, entities.Entities.Count != 0, entities.NextPruneTick);
        }

        /// <summary>
        /// worldmap_idle_loop off: poll every Update (the old cadence). On: poll only while a load is pending (the task's
        /// post does not wake the loop); otherwise the party/guild query and the stale-entity prune are deadlines, and
        /// packets and input wake the loop for the rest.
        /// </summary>
        internal static void NoteLoopDue(bool idleLoop, bool loadPending, bool queryEnabled, long queryTick, bool hasEntities, long pruneTick)
        {
            if (!idleLoop || loadPending)
            {
                LoopDue.Now(LoopDueSource.WorldMap);

                return;
            }

            if (queryEnabled)
            {
                LoopDue.At(queryTick, LoopDueSource.WorldMap);
            }

            if (hasEntities)
            {
                LoopDue.At(pruneTick, LoopDueSource.WorldMap);
            }
        }

        public void ChangeMap(int index)
        {
            Client.Game.UO.FileManager.Maps.LoadMap(index);
            _map = new Map.FacetView(index); // no Map (its slot array is not needed here)

            // the old facet's image is wrong for the new one; Update uploads the new image when the task posts it
            _mapTexture.Release();

            // read on the game thread: LoadMap runs on a task, and the race is ECS storage
            bool gargoyle = GameObject.PlayerIsGargoyle(World);
            int load = _mapImages.Request();
            Interlocked.Increment(ref _loadsRunning);

            // loads run one after another; a superseded one skips its decode (MapImageHandover.IsCurrent)
            _loadingTask = _loadingTask == null
                ? Task.Run(() => RunLoad(index, gargoyle, load))
                : _loadingTask.ContinueWith(_ => RunLoad(index, gargoyle, load), TaskScheduler.Default);
        }

        private void RunLoad(int index, bool gargoyle, int load)
        {
            try
            {
                LoadMap(index, gargoyle, load);
            }
            finally
            {
                // after the post: once this reaches 0 a posted image is visible through HasPending
                Interlocked.Decrement(ref _loadsRunning);
            }
        }

        #endregion

        public override void Dispose()
        {
            SaveSettings();
            World.WMapManager.SetEnable(false);

            Client.Game.UO.GameCursor.IsDraggingCursorForced = false;

            // a load still running finds the handover closed and drops its image
            _mapImages.Close();
            _mapTexture.Dispose();

            base.Dispose();

            // after base.Dispose, as GetGump stopped seeing the gump once IsDisposed was set
            NoteClosed(ref _countedOpen);
        }

    }

}
