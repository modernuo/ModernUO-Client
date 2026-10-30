// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene : Scene
    {
        // the light composite's blend states and hues, created once (they used to be allocated every frame)
        private static readonly BlendState _darknessBlendState = new BlendState
        {
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.SourceColor,
            ColorBlendFunction = BlendFunction.Add
        };

        private static readonly BlendState _altLightsBlendState = new BlendState
        {
            ColorSourceBlend = Blend.DestinationColor,
            ColorDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add
        };

        private static readonly Func<BlendState> _darknessBlend = () => _darknessBlendState;
        private static readonly Func<BlendState> _altLightsBlend = () => _altLightsBlendState;
        private static readonly Func<BlendState> _noLightsBlend = () => null;
        private static readonly Func<Vector3> _lightsHue = () => new Vector3(0f, 0f, 1f);
        private static readonly Func<Vector3> _altLightsHue = () => new Vector3(0f, 0f, 0.5f);

        private const float MAX_LAYER_DEPTH = 0x8000;
        private uint _time_cleanup = Time.Ticks + 5000;
        private bool _alphaChanged;
        private long _alphaTimer;
        private bool _forceStopScene;
        private HealthLinesManager _healthLinesManager;

        private Point _lastSelectedMultiPositionInHouseCustomization;
        private int _lightCount;
        private readonly LightData[] _lights = new LightData[
            LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
        ];
        private Item _multi;
        private Rectangle _rectangleObj = Rectangle.Empty,
            _rectanglePlayer;
        private long _timePing;

        private uint _timeToPlaceMultiInHouseCustomization;
        private readonly UseItemQueue _useItemQueue;
        private bool _useObjectHandles;
        private AnimatedStaticsManager _animatedStaticsManager;

        private readonly World _world;

        // Track the previously highlighted mesh sprite so we can restore its hue
        private GameObject _prevMeshHighlight;
        // its Incarnation when highlighted: a land or static can be retired and reused for another cell (MapObjectRecycler)
        // before the next draw restores its hue (updates without draws can outlast the recycler's quarantine)
        private uint _prevMeshHighlightIncarnation;

        public GameScene(World world)
        {
            _world = world;
            _useItemQueue = new UseItemQueue(world);
        }

        public bool UpdateDrawPosition { get; set; }
        public bool DisconnectionRequested { get; set; }
        public bool UseLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseCustomLightLevel
                ? _world.Light.Personal < _world.Light.Overall
                : _world.Light.RealPersonal < _world.Light.RealOverall;
        public bool UseAltLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseAlternativeLights;

        public void DoubleClickDelayed(uint serial)
        {
            _useItemQueue.Add(serial);
        }

        public override void Load()
        {
            base.Load();

            Client.Game.Window.AllowUserResizing = true;

            Camera.Zoom = ProfileManager.CurrentProfile.DefaultScale;
            Camera.Bounds.X = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.X);
            Camera.Bounds.Y = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.Y);
            Camera.Bounds.Width = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.X);
            Camera.Bounds.Height = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.Y);

            Client.Game.UO.GameCursor.ItemHold.Clear();

            _world.Macros.Clear();
            _world.Macros.Load();
            _animatedStaticsManager = new AnimatedStaticsManager();
            _animatedStaticsManager.Initialize();
            _world.InfoBars.Load();
            _healthLinesManager = new HealthLinesManager(_world);

            _world.CommandManager.Initialize();

            WorldViewportGump viewport = new WorldViewportGump(_world, this);
            UIManager.Add(viewport, false);

            if (!ProfileManager.CurrentProfile.TopbarGumpIsDisabled)
            {
                TopBarGump.Create(_world);
            }

            NetClient.Socket.Disconnected += SocketOnDisconnected;
            _world.MessageManager.MessageReceived += ChatOnMessageReceived;
            UIManager.ContainerScale = ProfileManager.CurrentProfile.ContainersScale / 100f;
            Data.MovementSpeed.FastRotation = ProfileManager.CurrentProfile.FastRotation;

            SDL.SDL_SetWindowMinimumSize(Client.Game.Window.Handle, Client.Game.ScaleWithDpi(640), Client.Game.ScaleWithDpi(480));

            if (ProfileManager.CurrentProfile.WindowBorderless)
            {
                Client.Game.SetWindowBorderless(true);
            }
            else if (Settings.GlobalSettings.IsWindowMaximized)
            {
                Client.Game.MaximizeWindow();
            }
            else if (Settings.GlobalSettings.WindowSize.HasValue)
            {
                int w = Settings.GlobalSettings.WindowSize.Value.X;
                int h = Settings.GlobalSettings.WindowSize.Value.Y;

                w = Math.Max(Client.Game.ScaleWithDpi(640), w);
                h = Math.Max(Client.Game.ScaleWithDpi(480), h);

                Client.Game.SetWindowSize(w, h);
            }

            Plugin.OnConnected();
        }

        private void ChatOnMessageReceived(object sender, MessageEventArgs e)
        {
            if (e.Type == MessageType.Command)
            {
                return;
            }

            string name;
            string text;

            ushort hue = e.Hue;

            switch (e.Type)
            {
                case MessageType.Regular:
                case MessageType.Limit3Spell:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = ResGeneral.System;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.System:
                case MessageType.GmChat:
                    name =
                        string.IsNullOrEmpty(e.Name)
                        || string.Equals(
                            e.Name,
                            "system",
                            StringComparison.InvariantCultureIgnoreCase
                        )
                            ? ResGeneral.System
                            : e.Name;

                    text = e.Text;

                    break;

                case MessageType.Emote:
                    name = e.Name;
                    text = $"{e.Text}";

                    if (e.Hue == 0)
                    {
                        hue = ProfileManager.CurrentProfile.EmoteHue;
                    }

                    break;

                case MessageType.Label:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = string.Empty;
                    }
                    else if (string.IsNullOrEmpty(e.Name))
                    {
                        name = ResGeneral.YouSee;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.Spell:
                    name = e.Name;
                    text = e.Text;

                    break;

                case MessageType.Party:
                    text = e.Text;
                    name = string.Format(ResGeneral.Party0, e.Name);
                    hue = ProfileManager.CurrentProfile.PartyMessageHue;

                    break;

                case MessageType.Alliance:
                    text = e.Text;
                    name = string.Format(ResGeneral.Alliance0, e.Name);
                    hue = ProfileManager.CurrentProfile.AllyMessageHue;

                    break;

                case MessageType.Guild:
                    text = e.Text;
                    name = string.Format(ResGeneral.Guild0, e.Name);
                    hue = ProfileManager.CurrentProfile.GuildMessageHue;

                    break;

                default:
                    text = e.Text;
                    name = e.Name;
                    hue = e.Hue;

                    Log.Warn($"Unhandled text type {e.Type}  -  text: '{e.Text}'");

                    break;
            }

            if (!string.IsNullOrEmpty(text))
            {
                _world.Journal.Add(text, hue, name, e.Parent?.Serial, e.TextType, e.IsUnicode, e.Type);
            }
        }

        public override void Unload()
        {
            if (IsDestroyed)
            {
                return;
            }

            ProfileManager.CurrentProfile.GameWindowPosition = new Point(
                Camera.Bounds.X,
                Camera.Bounds.Y
            );
            ProfileManager.CurrentProfile.GameWindowSize = new Point(
                Camera.Bounds.Width,
                Camera.Bounds.Height
            );
            ProfileManager.CurrentProfile.DefaultScale = Camera.Zoom;

            Client.Game.Audio?.StopMusic();
            Client.Game.Audio?.StopSounds();

            Client.Game.SetWindowTitle(string.Empty);
            Client.Game.UO.GameCursor.ItemHold.Clear();

            // first-sight art decoded for the chunk loader is drained only by Map.UpdateLoading, which stops with the scene:
            // return the finished decodes' buffers now (workers still running return theirs)
            Client.Game.UO.Arts?.CancelPending();
            Client.Game.UO.Texmaps?.CancelPending();

            try
            {
                Plugin.OnDisconnected();
            }
            catch { }

            _world.TargetManager.Reset();

            // special case for wmap. this allow us to save settings
            UIManager.GetGump<WorldMapGump>()?.SaveSettings();

            ProfileManager.CurrentProfile?.Save(_world, ProfileManager.ProfilePath);

            _world.Macros.Save();
            _world.Macros.Clear();
            _world.InfoBars.Save();
            ProfileManager.UnLoadProfile();

            StaticFilters.CleanCaveTextures();
            StaticFilters.CleanTreeTextures();

            NetClient.Socket.Disconnected -= SocketOnDisconnected;
            NetClient.Socket.Disconnect();

            _world.CommandManager.UnRegisterAll();
            _world.Weather.Reset();
            UIManager.Clear();
            _world.Clear();
            _world.ChatManager.Clear();
            _world.DelayedObjectClickManager.Clear();

            _useItemQueue?.Clear();
            _world.MessageManager.MessageReceived -= ChatOnMessageReceived;

            Settings.GlobalSettings.WindowSize = new Point(
                Client.Game.ClientBounds.Width,
                Client.Game.ClientBounds.Height
            );

            Settings.GlobalSettings.IsWindowMaximized = Client.Game.IsWindowMaximized();
            Client.Game.SetWindowBorderless(false);

            base.Unload();
        }

        private void SocketOnDisconnected(object sender, SocketError e)
        {
            if (Settings.GlobalSettings.Reconnect)
            {
                _forceStopScene = true;
            }
            else
            {
                UIManager.Add(
                    new MessageBoxGump(
                        _world,
                        200,
                        200,
                        string.Format(
                            ResGeneral.ConnectionLost0,
                            StringHelper.AddSpaceBeforeCapital(e.ToString())
                        ),
                        s =>
                        {
                            if (s)
                            {
                                Client.Game.SetScene(new LoginScene(_world));
                            }
                        }
                    )
                );
            }
        }

        public void RequestQuitGame()
        {
            UIManager.Add(
                new QuestionGump(
                    _world,
                    ResGeneral.QuitPrompt,
                    s =>
                    {
                        if (s)
                        {
                            if (
                                (
                                    _world.ClientFeatures.Flags
                                    & CharacterListFlags.CLF_OWERWRITE_CONFIGURATION_BUTTON
                                ) != 0
                            )
                            {
                                DisconnectionRequested = true;
                                NetClient.Socket.Send_LogoutNotification();
                            }
                            else
                            {
                                NetClient.Socket.Disconnect();
                                Client.Game.SetScene(new LoginScene(_world));
                            }
                        }
                    }
                )
            );
        }

        // the light list so far (EcsParity compares the lights the ECS sprite path adds with the view's)
        internal int LightCount => _lightCount;

        internal (byte Id, ushort Color, bool IsHue, int X, int Y) LightAt(int i) =>
            (_lights[i].ID, _lights[i].Color, _lights[i].IsHue, _lights[i].DrawX, _lights[i].DrawY);

        internal void TruncateLights(int count) => _lightCount = Math.Min(_lightCount, Math.Max(count, 0));

        public void AddLight(GameObject obj, GameObject lightObject, int x, int y)
        {
            if (
                _lightCount >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
                || !UseLights && !UseAltLights
                || obj == null
            )
            {
                return;
            }

            bool canBeAdded = true;

            int testX = obj.X + 1;
            int testY = obj.Y + 1;

            GameObject tile = _world.Map.GetTile(testX, testY);

            if (tile != null)
            {
                sbyte z5 = (sbyte)(obj.Z + 5);

                for (GameObject o = tile; o != null; o = o.TNext)
                {
                    if (
                        (!(o is Static s) || s.ItemData.IsTransparent)
                            && (!(o is Multi m) || m.ItemData.IsTransparent)
                        || !o.AllowedToDraw
                    )
                    {
                        continue;
                    }

                    if (o.Z < _maxZ && o.Z >= z5)
                    {
                        canBeAdded = false;

                        break;
                    }
                }
            }

            if (canBeAdded)
            {
                ref LightData light = ref _lights[_lightCount];

                ushort graphic = lightObject.Graphic;

                if (
                    graphic >= 0x3E02 && graphic <= 0x3E0B
                    || graphic >= 0x3914 && graphic <= 0x3929
                    || graphic == 0x0B1D
                )
                {
                    light.ID = 2;
                }
                else
                {
                    if (obj == lightObject && obj is Item item)
                    {
                        light.ID = item.LightID;
                    }
                    else if (lightObject is Item it)
                    {
                        light.ID = (byte)it.ItemData.LightIndex;

                        if (obj is Mobile mob)
                        {
                            switch (mob.Direction)
                            {
                                case Direction.Right:
                                    y += 33;
                                    x += 22;

                                    break;

                                case Direction.Left:
                                    y += 33;
                                    x -= 22;

                                    break;

                                case Direction.East:
                                    x += 22;
                                    y += 55;

                                    break;

                                case Direction.Down:
                                    y += 55;

                                    break;

                                case Direction.South:
                                    x -= 22;
                                    y += 55;

                                    break;
                            }
                        }
                    }
                    else if (obj is Mobile _)
                    {
                        light.ID = 1;
                    }
                    else
                    {
                        ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[obj.Graphic];
                        light.ID = data.Layer;
                    }
                }

                light.Color = 0;
                light.IsHue = false;

                if (ProfileManager.CurrentProfile.UseColoredLights)
                {
                    if (light.ID > 200)
                    {
                        light.Color = (ushort)(light.ID - 200);
                        light.ID = 1;
                    }

                    if (LightColors.GetHue(graphic, out ushort color, out bool ishue))
                    {
                        light.Color = color;
                        light.IsHue = ishue;
                    }
                }

                if (light.ID >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                {
                    return;
                }

                if (light.Color != 0)
                {
                    light.Color++;
                }

                light.DrawX = x;
                light.DrawY = y;
                _lightCount++;
            }
        }

        private void FillGameObjectList()
        {
            _renderLists.Clear();
            _visibleChunks.Clear();

            _foliageCount = 0;
            _drawFrame++;

            if (!_world.InGame)
            {
                return;
            }

            // T6a: the tile lists exactly as this frame's walk (both renderers) is about to read them
            if (Map.TileOrder.Check)
            {
                Map.TileOrder.CheckFrame(_world);
            }

            long setupStart = FrameStats.Stamp();
            // while the world settle gate holds the last frame nobody sees the fades: alphas jump to their targets, so the
            // first frame after the hold doesn't show statics of freshly loaded chunks fading in (they start at 0)
            _snapAlphas = _settle.Holding;
            _alphaChanged = _alphaTimer < Time.Ticks || _snapAlphas;

            if (_alphaChanged)
            {
                _alphaTimer = Time.Ticks + Constants.ALPHA_TIME;
            }

            if (ProfileManager.CurrentProfile.UseCircleOfTransparency)
            {
                float r = ProfileManager.CurrentProfile.CircleOfTransparencyRadius;
                _cotRadiusSq = r * r;
                _cotPlayerScreenPos = _world.Player.GetScreenPosition();
                _cotGradientMode = ProfileManager.CurrentProfile.CircleOfTransparencyType == 1;
            }
            else
            {
                _cotRadiusSq = 0;
                _cotGradientMode = false;
            }

            FoliageIndex++;

            if (FoliageIndex >= 100)
            {
                FoliageIndex = 1;
            }

            GetViewPort();
            GpuPrepareBegin();

            if (UpdateDrawPosition)
            {
                // a camera move: every RealScreenPosition computed before this frame is stale (ScreenPositionGen)
                _screenPositionGen++;
            }

            var ctrlShiftHeld = Keyboard.Ctrl && Keyboard.Shift;
            var useObjectHandles = _world.NameOverHeadManager.IsToggled || ctrlShiftHeld;
            if (useObjectHandles != _useObjectHandles)
            {
                _useObjectHandles = useObjectHandles;
                if (_useObjectHandles)
                {
                    _world.NameOverHeadManager.Open();
                    if (_world.NameOverHeadManager.IsToggled && !ctrlShiftHeld)
                    {
                        _world.NameOverHeadManager.SetMenuVisible(false);
                    }
                }
                else
                {
                    _world.NameOverHeadManager.Close();
                }
            }
            else if (_useObjectHandles && _world.NameOverHeadManager.IsToggled)
            {
                _world.NameOverHeadManager.SetMenuVisible(ctrlShiftHeld);
            }

            _rectanglePlayer.X = (int)(
                _world.Player.RealScreenPosition.X
                - _world.Player.FrameInfo.X
                + 22
                + _world.Player.Offset.X
            );
            _rectanglePlayer.Y = (int)(
                _world.Player.RealScreenPosition.Y
                - _world.Player.FrameInfo.Y
                + 22
                + (_world.Player.Offset.Y - _world.Player.Offset.Z)
            );
            _rectanglePlayer.Width = _world.Player.FrameInfo.Width;
            _rectanglePlayer.Height = _world.Player.FrameInfo.Height;

            int minX = _minTile.X;
            int minY = _minTile.Y;
            int maxX = _maxTile.X;
            int maxY = _maxTile.Y;
            Map.Map map = _world.Map;
            bool use_handles = _useObjectHandles;
            GpuItemCacheBegin(use_handles);
            (var minChunkX, var minChunkY) = (minX >> 3, minY >> 3);
            (var maxChunkX, var maxChunkY) = (maxX >> 3, maxY >> 3);
            long loopStart = FrameStats.AddSince(FramePhase.PrepSetup, setupStart);
            map.DrawPending = 0;

            // FrameStats per chunk (masks, walk, hidden) only for the Gpu world and in detail mode. Legacy times the whole
            // chunk loop as PrepWalk, without two timestamps per chunk (~1,400 chunks at the 4K zoom-out, each
            // Stopwatch.GetTimestamp a P/Invoke with a GC poll after it)
            bool chunkStamps = FrameStats.On && (_gpuWorld != null || FrameStats.Detail);
            int nTiles = 0;

            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                for (var chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
                {
                    var chunk = map.GetChunkForDraw(chunkX, chunkY); // unloaded chunks are queued (Map.UpdateLoading)
                    if (chunk == null || chunk.IsDestroyed)
                        continue;

                    // Build chunk mesh if dirty
                    if (chunk.Mesh.IsDirty)
                    {
                        chunk.Mesh.Build(chunk, _world, Client.Game.GraphicsDevice);
                        // the Gpu prepare's tile masks depend on InChunkMesh, and the chunk's alphas may still move
                        chunk.GpuMaskDirty = true;
                        chunk.GpuFreshSteps = GPU_FADE_STEPS;
                    }

                    // Reset visibility and alpha for this frame
                    if (_gpuWorld == null)
                    {
                        chunk.Mesh.Land.ResetVisibility();
                        chunk.Mesh.Land.ResetAlpha();
                        chunk.Mesh.Statics.ResetVisibility();
                        chunk.Mesh.Statics.ResetAlpha();
                    }

                    _visibleChunks.Add(chunk);
                    long walkStart = chunkStamps ? FrameStats.Stamp() : 0;

                    // Gpu prepare skipping baked objects (GameSceneGpuPrepare): fullTiles are walked in full, walkTiles
                    // without their baked land and statics, the rest not at all
                    ulong fullTiles = ulong.MaxValue, walkTiles = 0, edgeTiles = 0;

                    if (_gpuSkipEnabled)
                    {
                        GpuPrepareChunk(chunk, out fullTiles, out walkTiles, out edgeTiles);
                        walkStart = FrameStats.AddSince(FramePhase.PrepGpuMasks, walkStart);

                        if (_gpuItemCacheOn && walkTiles != 0)
                        {
                            // W4: tiles of settled ground items leave walkTiles for _gpuServeTiles
                            GpuItemCachePrepare(chunk, ref walkTiles);
                            walkStart = FrameStats.AddSince(FramePhase.PrepItemCache, walkStart);
                        }
                    }

                    for (var x = 0; x < 8; x++)
                    {
                        for (var y = 0; y < 8; y++)
                        {
                            ulong bit = 1UL << (x << 3 | y);
                            bool full = (fullTiles & bit) != 0;

                            if (!full && (walkTiles & bit) == 0)
                            {
                                // W4: settled ground items come from the chunk's item cache; a tile whose items changed
                                // since it was extracted is walked instead
                                if ((_gpuServeTiles & bit) == 0 || GpuServeCachedTile(chunk, x << 3 | y))
                                    continue;
                            }
                            else if ((_gpuServeTiles & bit) != 0 && GpuServeCachedTile(chunk, x << 3 | y))
                            {
                                // a served tile with mobiles: the walk visits the mobiles alone (they are all its other
                                // unbaked objects; GpuMobiles is in tile-list order)
                                int b = x << 3 | y;

                                for (int m = chunk.GpuMobileStart[b], mEnd = chunk.GpuMobileStart[b + 1]; m < mEnd; m++)
                                {
                                    GameObject mobile = chunk.GpuMobiles[m];

                                    if (!mobile.IsDestroyed)
                                    {
                                        nTiles++;
                                        AddTileToRenderList(mobile, use_handles, GPU_WALK_MAX_Z, chunk, true, _gpuSkipEnabled, mobile.TNext);
                                    }
                                }

                                continue;
                            }
                            else if ((_gpuCheckTiles & bit) != 0)
                            {
                                // CUO_GPU_ITEM_CHECK: what the cache would serve, compared with the walk after the capture
                                GpuExpectCachedTile(chunk, x << 3 | y);
                            }

                            var firstObj = chunk.GetHeadObject(x, y);
                            if (firstObj == null || firstObj.IsDestroyed)
                                continue;

                            nTiles++;
                            AddTileToRenderList(
                                firstObj,
                                use_handles,
                                GPU_WALK_MAX_Z,
                                chunk,
                                !full,
                                _gpuSkipEnabled
                            );
                        }
                    }

                    if (chunkStamps)
                    {
                        FrameStats.Enter(FrameStats.NoBucket);
                        walkStart = FrameStats.AddSince(FramePhase.PrepWalk, walkStart);
                    }

                    // after the chunk's walk: CollectGpuHidden only reads what the walk stamped on this tile's objects
                    if (_gpuWorld != null)
                    {
                        for (var x = 0; x < 8; x++)
                        {
                            for (var y = 0; y < 8; y++)
                            {
                                ulong bit = 1UL << (x << 3 | y);
                                bool full = (fullTiles & bit) != 0;

                                if (!full && ((walkTiles | edgeTiles) & bit) == 0)
                                    continue;

                                var firstObj = chunk.GetHeadObject(x, y);
                                if (firstObj == null || firstObj.IsDestroyed)
                                    continue;

                                if (full || (walkTiles & bit) != 0)
                                {
                                    CollectGpuHidden(firstObj, !full);
                                }

                                if ((edgeTiles & bit) != 0)
                                {
                                    GpuHideCulledBaked(firstObj);
                                }
                            }
                        }

                        if (chunkStamps)
                        {
                            FrameStats.AddSince(FramePhase.PrepHidden, walkStart);
                        }
                    }
                }
            }

            if (FrameStats.On)
            {
                if (!chunkStamps)
                {
                    FrameStats.AddSince(FramePhase.PrepWalk, loopStart);
                }

                FrameStats.Add(FrameCounter.Tiles, nTiles);
            }

            if (FrameStats.Detail)
            {
                FrameStats.End(FramePhase.WorldPrepare);
                BareWalk(map, minChunkX, minChunkY, maxChunkX, maxChunkY);
                FrameStats.Begin(FramePhase.WorldPrepare);
            }

            long foliageStart = FrameStats.Stamp();
            GpuPrepareEnd();

            if (_alphaChanged)
            {
                for (int i = 0; i < _foliageCount; i++)
                {
                    GameObject f = _foliages[i];

                    if (f.FoliageIndex == FoliageIndex)
                    {
                        CalculateAlpha(ref f.AlphaHue, Constants.FOLIAGE_ALPHA);
                    }
                    else if (f.Z < _maxZ)
                    {
                        CalculateAlpha(ref f.AlphaHue, 0xFF);
                    }
                }
            }

            long textStart = FrameStats.AddSince(FramePhase.PrepFoliageFinish, foliageStart);
            FlushWalkCounters();
            UpdateTextServerEntities();

            if (PrepCheck.On)
            {
                PrepCheck.Report();
            }
            FrameStats.AddSince(FramePhase.PrepText, textStart);

            UpdateDrawPosition = false;
        }

        // FrameStats detail mode: the floor cost of visiting every object of the walked tiles, with no work per object
        private void BareWalk(Map.Map map, int minChunkX, int minChunkY, int maxChunkX, int maxChunkY)
        {
            long start = FrameStats.Stamp();
            int n = 0;

            for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                for (int chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
                {
                    var chunk = map.GetChunk2(chunkX, chunkY, false);

                    if (chunk == null || chunk.IsDestroyed)
                    {
                        continue;
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        for (int y = 0; y < 8; y++)
                        {
                            for (GameObject obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                            {
                                n += obj.Z;
                            }
                        }
                    }
                }
            }

            _bareWalkSink = n;
            FrameStats.AddSince(FramePhase.BareWalk, start);
        }

        private static int _bareWalkSink;

        // the dictionary's value collection, not IEnumerable<T>: that boxed an enumerator twice a frame
        // the text of every mobile, and of corpses, follows the camera (items' text is placed by their view). Only entities
        // that got text are listed (World.TextOwners); scanning all of World.Mobiles and World.Items cost 0.2 ms per frame
        // with 25,000 items in range
        private void UpdateTextServerEntities()
        {
            List<Entity> owners = _world.TextOwners;
            bool skipExpired = Settings.GlobalSettings.TextSkipExpired;

            for (int i = owners.Count - 1; i >= 0; i--)
            {
                Entity e = owners[i];

                if (e.IsDestroyed || e.TextContainer == null || e.TextContainer.IsEmpty)
                {
                    e.InTextOwners = false;
                    owners[i] = owners[owners.Count - 1];
                    owners.RemoveAt(owners.Count - 1);

                    continue;
                }

                if (e is Mobile || e.Graphic == 0x2006)
                {
                    if (skipExpired && e.TextAllExpired())
                    {
                        // W5: UpdateRealScreenPosition without the text layout, which would place nothing
                        e.UpdateRealScreenPositionOnly(_offset.X, _offset.Y);

                        if (World.ActiveCheck)
                        {
                            _world.CheckSkippedTextLayout(e);
                        }
                    }
                    else
                    {
                        e.UpdateRealScreenPosition(_offset.X, _offset.Y);
                    }
                }
            }
        }

        public override void Update()
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            SelectedObject.TranslatedMousePositionByViewport = Camera.MouseToWorldPosition();

            base.Update();

            if (Map.MapObjectRecycler.Check)
            {
                Map.MapObjectRecycler.CheckFrame(_world, _prevMeshHighlight);
            }

            if (_time_cleanup < Time.Ticks)
            {
                _world.Map?.ClearUnusedBlocks();
                _time_cleanup = Time.Ticks + 500;
            }

            PacketHandlers.SendMegaClilocRequests(_world);

            if (_forceStopScene)
            {
                LoginScene loginScene = new LoginScene(_world);
                Client.Game.SetScene(loginScene);
                loginScene.Reconnect = true;

                return;
            }

            if (!_world.InGame)
            {
                return;
            }

            if (Time.Ticks > _timePing)
            {
                NetClient.Socket.Statistics.SendPing();
                _timePing = (long)Time.Ticks + 1000;
            }

            _world.Update();
            _animatedStaticsManager.Process();
            _world.BoatMovingManager.Update();
            _world.Player.Pathfinder.ProcessAutoWalk();
            _world.DelayedObjectClickManager.Update();

            if (!MoveCharacterByMouseInput() && !currentProfile.DisableArrowBtn)
            {
                Direction dir = DirectionHelper.DirectionFromKeyboardArrows(
                    _flags[0],
                    _flags[2],
                    _flags[1],
                    _flags[3]
                );

                if (_world.InGame && !_world.Player.Pathfinder.AutoWalking && dir != Direction.NONE)
                {
                    _world.Player.Walk(dir, currentProfile.AlwaysRun);
                }
            }

            if (
                _followingMode && SerialHelper.IsMobile(_followingTarget) && !_world.Player.Pathfinder.AutoWalking
            )
            {
                Mobile follow = _world.Mobiles.Get(_followingTarget);

                if (follow != null)
                {
                    int distance = follow.Distance;

                    if (distance > _world.ClientViewRange)
                    {
                        StopFollowing();
                    }
                    else if (distance > 3)
                    {
                        _world.Player.Pathfinder.WalkTo(follow.X, follow.Y, follow.Z, 1);
                    }
                }
                else
                {
                    StopFollowing();
                }
            }

            _world.Macros.Update();

            if (
                (currentProfile.CorpseOpenOptions == 1 || currentProfile.CorpseOpenOptions == 3)
                    && _world.TargetManager.IsTargeting
                || (currentProfile.CorpseOpenOptions == 2 || currentProfile.CorpseOpenOptions == 3)
                    && _world.Player.IsHidden
            )
            {
                _useItemQueue.ClearCorpses();
            }

            _useItemQueue.Update();

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            if (
                _world.TargetManager.IsTargeting
                && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement
                && _world.CustomHouseManager == null
                && _world.TargetManager.MultiTargetInfo != null
            )
            {
                if (_multi == null)
                {
                    _multi = Item.Create(_world, 0);
                    _multi.Graphic = _world.TargetManager.MultiTargetInfo.Model;
                    _multi.Hue = _world.TargetManager.MultiTargetInfo.Hue;
                    _multi.IsMulti = true;
                }

                if (SelectedObject.Object is GameObject gobj)
                {
                    ushort x,
                        y;
                    sbyte z;

                    int cellX = gobj.X % 8;
                    int cellY = gobj.Y % 8;

                    GameObject o = _world.Map.GetChunk(gobj.X, gobj.Y)?.Tiles[cellX, cellY];

                    if (o != null)
                    {
                        x = o.X;
                        y = o.Y;
                        z = o.Z;
                    }
                    else
                    {
                        x = gobj.X;
                        y = gobj.Y;
                        z = gobj.Z;
                    }

                    _world.Map.GetMapZ(x, y, out sbyte groundZ, out sbyte _);

                    if (gobj is Static st && st.ItemData.IsWet)
                    {
                        groundZ = gobj.Z;
                    }

                    x = (ushort)(x - _world.TargetManager.MultiTargetInfo.XOff);
                    y = (ushort)(y - _world.TargetManager.MultiTargetInfo.YOff);
                    z = (sbyte)(groundZ - _world.TargetManager.MultiTargetInfo.ZOff);

                    _multi.SetInWorldTile(x, y, z);
                    _multi.CheckGraphicChange();

                    _world.HouseManager.TryGetHouse(_multi.Serial, out House house);

                    foreach (Multi s in house.Components)
                    {
                        s.IsHousePreview = true;
                        s.SetInWorldTile(
                            (ushort)(_multi.X + s.MultiOffsetX),
                            (ushort)(_multi.Y + s.MultiOffsetY),
                            (sbyte)(_multi.Z + s.MultiOffsetZ)
                        );
                    }
                }
            }
            else if (_multi != null)
            {
                _world.HouseManager.RemoveMultiTargetHouse();
                _multi.Destroy();
                _multi = null;
            }

            if (_isMouseLeftDown && !Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                if (
                    _world.CustomHouseManager != null
                    && _world.CustomHouseManager.SelectedGraphic != 0
                    && !_world.CustomHouseManager.SeekTile
                    && !_world.CustomHouseManager.Erasing
                    && Time.Ticks > _timeToPlaceMultiInHouseCustomization
                )
                {
                    if (
                        SelectedObject.Object is GameObject obj
                        && (
                            obj.X != _lastSelectedMultiPositionInHouseCustomization.X
                            || obj.Y != _lastSelectedMultiPositionInHouseCustomization.Y
                        )
                    )
                    {
                        _world.CustomHouseManager.OnTargetWorld(obj);
                        _timeToPlaceMultiInHouseCustomization = Time.Ticks + 50;
                        _lastSelectedMultiPositionInHouseCustomization.X = obj.X;
                        _lastSelectedMultiPositionInHouseCustomization.Y = obj.Y;
                    }
                }
                else if (Time.Ticks - _holdMouse2secOverItemTime >= 1000)
                {
                    if (SelectedObject.Object is Item it && GameActions.PickUp(_world, it.Serial, 0, 0))
                    {
                        _isMouseLeftDown = false;
                        _holdMouse2secOverItemTime = 0;
                    }
                }
            }
        }

        public override bool Draw(UltimaBatcher2D batcher, RenderTargets renderTargets)
        {
            if (!_world.InGame)
            {
                return false;
            }

            if (CheckDeathScreen(batcher))
            {
                return true;
            }

            Viewport r_viewport = batcher.GraphicsDevice.Viewport;
            Viewport camera_viewport = Camera.GetViewport();
            Matrix matrix = Camera.ViewTransformMatrix;

            var gpu = Client.Game.GetGpuWorld();

            if (gpu != null)
            {
                // No Legacy fallback: after a world entry, teleport, facet change or world-pass recreation the last presented
                // world frame (and its lights) stays in the world and light targets while the GPU world streams, until its
                // view is ready or the timeout passes (WorldSettleGate)
                var target = renderTargets.WorldRenderTarget;
                bool targetOk = target != null && !target.IsDisposed && target.RenderTargetUsage == RenderTargetUsage.PreserveContents;
                bool ready = targetOk && gpu.EnsureFacet(_world.MapIndex);
                bool hold = true;

                if (ready)
                {
                    // the player's world pixel, which a teleport moves in Update: the draw offset (the camera) only follows
                    // during this frame's list fill, after the hold decision
                    var player = _world.Player;
                    hold = _settle.Begin(Time.Ticks, gpu.World, _world.MapIndex, (player.X - player.Y) * 22, (player.X + player.Y) * 22 - (player.Z << 2),
                        ReferenceEquals(target, _settleTarget));
                    _settleTarget = target;
                }

                if (!hold)
                {
                    FrameStats.Begin(FramePhase.Lights);
                    PrepareLightsRendering(batcher, ref matrix, renderTargets);
                    FrameStats.End(FramePhase.Lights);
                }
                else
                {
                    // the light target keeps the held frame's lights
                    InitializeRenderTargets(renderTargets);
                    _lightCount = 0;
                }

                batcher.GraphicsDevice.Viewport = camera_viewport;

                if (ready)
                {
                    DrawWorldGpu(batcher, ref matrix, renderTargets, gpu, hold);

                    // ready = the world pass has everything near its view, and the client has loaded every chunk its walk
                    // visited (a chunk still in the ChunkLoadQueue has no dynamic objects or overrides yet)
                    bool visibleReady = gpu.VisibleReady && _world.Map.DrawPending == 0;
                    _settle.End(visibleReady);

                    if (SettleLog && !hold && !visibleReady)
                    {
                        LogUnsettledFrame(gpu);
                    }
                }

                renderTargets.WorldFade = ready ? _settle.Fade : 0f;
            }
            else if (GpuDeviceLost.WorldSetupFailed)
            {
                // the client exits at the end of this frame: no world, and never Legacy in its place
                renderTargets.WorldFade = 0f;
            }
            else
            {
                // the developer comparison mode (-worldrenderer legacy, DevBench world:legacy), never a fallback
                FrameStats.Begin(FramePhase.Lights);
                PrepareLightsRendering(batcher, ref matrix, renderTargets);
                FrameStats.End(FramePhase.Lights);
                batcher.GraphicsDevice.Viewport = camera_viewport;

                DrawWorld(batcher, ref matrix, renderTargets);
                _settle.OnOtherRenderer();
                _settleTarget = renderTargets.WorldRenderTarget;
                renderTargets.WorldFade = 1f;
                FrameStats.Set(FrameCounter.GpuInstances, 0);
                FrameStats.Set(FrameCounter.GpuDynamic, 0);
                FrameStats.Set(FrameCounter.GpuOverrides, 0);
                FrameStats.Set(FrameCounter.GpuDrawCalls, 0);
            }

            batcher.GraphicsDevice.Viewport = r_viewport;

            return base.Draw(batcher, renderTargets);
        }

        private void DrawWorld(UltimaBatcher2D batcher, ref Matrix matrix, RenderTargets renderTargets)
        {
            FrameStats.Begin(FramePhase.WorldPrepare);
            batcher.GraphicsDevice.SetRenderTarget(renderTargets.WorldRenderTarget);

            if (renderTargets.WorldRenderTarget.RenderTargetUsage == RenderTargetUsage.PreserveContents)
            {
                // a GPU-mode target (the GPU pass is unavailable for this map): binding it no longer clears it
                batcher.GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil, Color.Black, 1f, 0);
            }

            SelectedObject.Object = null;
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);
            FillGameObjectList();

            // Restore previous highlight's original hue before applying new one
            if (_prevMeshHighlight != null
                && !_prevMeshHighlight.IsDestroyed
                && _prevMeshHighlight.Incarnation == _prevMeshHighlightIncarnation
                && _prevMeshHighlight.InChunkMesh
                && _prevMeshHighlight.MeshSpriteIndex >= 0)
            {
                var prevChunk = _world.Map.GetChunk(_prevMeshHighlight.X, _prevMeshHighlight.Y);
                if (prevChunk?.Mesh != null)
                {
                    var prevLayer = _prevMeshHighlight is Land ? prevChunk.Mesh.Land : prevChunk.Mesh.Statics;
                    ApplyMeshHue(_prevMeshHighlight, prevLayer);
                }
            }
            _prevMeshHighlight = null;

            // Apply highlight hue to mesh vertex for selected meshed object
            // (instead of redrawing it on top, which breaks z-order for overlapping objects)
            if (ProfileManager.CurrentProfile.HighlightGameObjects
                && SelectedObject.Object is GameObject selObj
                && selObj.InChunkMesh && selObj.MeshSpriteIndex >= 0)
            {
                var chunk = _world.Map.GetChunk(selObj.X, selObj.Y);
                if (chunk?.Mesh != null)
                {
                    var layer = selObj is Land ? chunk.Mesh.Land : chunk.Mesh.Statics;
                    float shaderType = selObj is Land land && land.IsStretched
                        ? ShaderHueTranslator.SHADER_LAND_HUED
                        : ShaderHueTranslator.SHADER_HUED;
                    layer.SetHue(
                        selObj.MeshSpriteIndex,
                        Constants.HIGHLIGHT_CURRENT_OBJECT_HUE - 1,
                        shaderType
                    );
                    _prevMeshHighlight = selObj;
                    _prevMeshHighlightIncarnation = selObj.Incarnation;
                }
            }

            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);
            FrameStats.End(FramePhase.WorldPrepare);
            FrameStats.Begin(FramePhase.WorldDraw);
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);
            batcher.SetSampler(SamplerState.PointClamp);

            batcher.Begin(null, matrix);
            batcher.SetBrightlight(ProfileManager.CurrentProfile.TerrainShadowsLevel * 0.1f);

            if (ProfileManager.CurrentProfile.UseCircleOfTransparency
                && ProfileManager.CurrentProfile.CircleOfTransparencyType != 1) // gradient mode uses CPU alpha, not shader
            {
                batcher.SetCircleOfTransparencyRadius(
                    (float)ProfileManager.CurrentProfile.CircleOfTransparencyRadius / Camera.Zoom
                );
            }
            else
            {
                batcher.SetCircleOfTransparencyRadius(0f);
            }

            // https://shawnhargreaves.com/blog/depth-sorting-alpha-blended-objects.html
            batcher.SetStencil(DepthStencilState.Default);

            RenderedObjectsCount = _renderLists.DrawRenderLists(
                batcher,
                _maxGroundZ,
                _visibleChunks,
                _offset.X,
                _offset.Y
            );


            if (
                _multi != null
                && _world.TargetManager.IsTargeting
                && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement
            )
            {
                _multi.Draw(
                    batcher,
                    _multi.RealScreenPosition.X,
                    _multi.RealScreenPosition.Y,
                    _multi.CalculateDepthZ()
                );
            }

            // draw weather
            _world.Weather.Draw(batcher, 0, 0, MAX_LAYER_DEPTH - 1);

            DrawSelection(batcher, MAX_LAYER_DEPTH);

            batcher.SetSampler(null);
            batcher.SetStencil(null);
            batcher.SetCircleOfTransparencyRadius(0f);
            batcher.End();

            batcher.GraphicsDevice.SetRenderTarget(null);
            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);
            FrameStats.End(FramePhase.WorldDraw);
            FrameStats.Set(FrameCounter.RenderListEntries, RenderedObjectsCount);
        }

        private bool PrepareLightsRendering(UltimaBatcher2D batcher, ref Matrix matrix, RenderTargets renderTargets)
        {
            InitializeRenderTargets(renderTargets);

            if (
                !UseLights && !UseAltLights
                || _world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect
            )
            {
                batcher.GraphicsDevice.SetRenderTarget(renderTargets.LightRenderTarget);
                batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Transparent, 0f, 0);
                batcher.GraphicsDevice.SetRenderTarget(null);

                return false;
            }

            batcher.GraphicsDevice.SetRenderTarget(renderTargets.LightRenderTarget);
            batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);

            if (!UseAltLights)
            {
                float lightColor = _world.Light.IsometricLevel;

                if (ProfileManager.CurrentProfile.UseDarkNights)
                {
                    lightColor -= 0.04f;
                }

                batcher.GraphicsDevice.Clear(
                    ClearOptions.Target,
                    new Vector4(lightColor, lightColor, lightColor, 1),
                    0f,
                    0
                );
            }

            batcher.Begin(null, matrix);
            batcher.SetBlendState(BlendState.Additive);

            Vector3 hue = Vector3.Zero;

            hue.Z = 1f;

            for (int i = 0; i < _lightCount; i++)
            {
                ref LightData l = ref _lights[i];
                ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);

                if (lightInfo.Texture == null)
                {
                    continue;
                }

                hue.X = l.Color;
                hue.Y =
                    hue.X > 1.0f
                        ? l.IsHue
                            ? ShaderHueTranslator.SHADER_HUED
                            : ShaderHueTranslator.SHADER_LIGHTS
                        : ShaderHueTranslator.SHADER_NONE;

                batcher.Draw(
                    lightInfo.Texture,
                    new Vector2(
                        l.DrawX - lightInfo.UV.Width * 0.5f,
                        l.DrawY - lightInfo.UV.Height * 0.5f
                    ),
                    lightInfo.UV,
                    hue,
                    0f
                );
            }

            _lightCount = 0;

            batcher.SetBlendState(null);
            batcher.End();

            batcher.GraphicsDevice.SetRenderTarget(null);

            return true;
        }

        private void InitializeRenderTargets(RenderTargets renderTargets)
        {
            renderTargets.SetLightsConfiguration(
                UseAltLights ? _altLightsBlend : (UseLights ? _darknessBlend : _noLightsBlend),
                UseAltLights ? _altLightsHue : _lightsHue
            );
        }

        public override void DrawUI(UltimaBatcher2D batcher)
        {
            _healthLinesManager.Draw(batcher, 0f);

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            _world.WorldTextManager.ProcessWorldText(true);
            _world.WorldTextManager.Draw(batcher, Camera.Bounds.X, Camera.Bounds.Y, 0);
        }

        public void DrawSelection(UltimaBatcher2D batcher, float layerDepth)
        {
            if (_isSelectionActive)
            {
                Vector3 selectionHue = new()
                {
                    Z = 0.7f
                };

                Point upperLeftInWorld = Camera.ScreenToWorld(new Point(
                    Math.Min(_selectionStart.X, Mouse.Position.X) - Camera.Bounds.X,
                    Math.Min(_selectionStart.Y, Mouse.Position.Y) - Camera.Bounds.Y
                ));

                Point lowerRightInWorld = Camera.ScreenToWorld(new Point(
                    Math.Max(_selectionStart.X, Mouse.Position.X) - Camera.Bounds.X,
                    Math.Max(_selectionStart.Y, Mouse.Position.Y) - Camera.Bounds.Y
                ));

                Rectangle selectionRect = new Rectangle(
                    upperLeftInWorld.X,
                    upperLeftInWorld.Y,
                    lowerRightInWorld.X - upperLeftInWorld.X,
                    lowerRightInWorld.Y - upperLeftInWorld.Y
                );

                batcher.Draw(
                    SolidColorTextureCache.GetTexture(Color.Black),
                    selectionRect,
                    selectionHue,
                    layerDepth
                );

                selectionHue.Z = 0.3f;

                batcher.DrawRectangle(
                    SolidColorTextureCache.GetTexture(Color.DeepSkyBlue),
                    selectionRect.X,
                    selectionRect.Y,
                    selectionRect.Width,
                    selectionRect.Height,
                    selectionHue,
                    layerDepth
                );
            }
        }

        private static readonly RenderedText _youAreDeadText = RenderedText.Create(
            ResGeneral.YouAreDead,
            0xFFFF,
            3,
            false,
            FontStyle.BlackBorder,
            TEXT_ALIGN_TYPE.TS_LEFT
        );

        private bool CheckDeathScreen(UltimaBatcher2D batcher)
        {
            if (
                ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.EnableDeathScreen
            )
            {
                if (_world.InGame)
                {
                    if (_world.Player.IsDead && _world.Player.DeathScreenTimer > Time.Ticks)
                    {
                        batcher.Begin();
                        _youAreDeadText.Draw(
                            batcher,
                            Camera.Bounds.X + (Camera.Bounds.Width / 2 - _youAreDeadText.Width / 2),
                            Camera.Bounds.Bottom / 2,
                            0f
                        );
                        batcher.End();

                        return true;
                    }
                }
            }

            return false;
        }

        private void StopFollowing()
        {
            if (_followingMode)
            {
                _followingMode = false;
                _followingTarget = 0;
                _world.Player.Pathfinder.StopAutoWalk();

                _world.MessageManager.HandleMessage(
                    _world.Player,
                    ResGeneral.StoppedFollowing,
                    string.Empty,
                    0,
                    MessageType.Regular,
                    3,
                    TextType.CLIENT
                );
            }
        }

        private struct LightData
        {
            public byte ID;
            public ushort Color;
            public bool IsHue;
            public int DrawX,
                DrawY;
        }
    }
}
