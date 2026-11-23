// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Loop;
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
        // visual-due tracking: the last list fill walked an animated static or item (tiledata Animation flag); kept
        // until the next fill, so a frame that reuses the list keeps it
        private bool _animatedArtInView;
        private long _alphaTimer;
        private bool _forceStopScene;
        private HealthLinesManager _healthLinesManager;

        private Point _lastSelectedMultiPositionInHouseCustomization;
        private readonly FrameLights _frameLights = new FrameLights(LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT);
#if CUO_DEVTOOLS
        // LightFrameLog: the light configuration the composite uses and the one the light target was drawn for (0 none,
        // 1 darkness, 2 alternative)
        private int _composeMode, _lightTargetMode;
#endif
        // counts the drawn frames (Draw past the death screen); the light pass draws only its own frame's lights
        private uint _lightsFrame;
        // view_margin (ViewMargin): this frame's light list is ranked, and its view for the radius cull and the priorities
        private bool _vmLights;

        /// <summary>view_margin: this frame's static and multi lights come from the chunks' emitter lists (StaticView and
        /// MultiView add none).</summary>
        internal static bool ChunkStaticLights;
        private uint _lightViewFrame;
        private int _lvLeft, _lvTop, _lvRight, _lvBottom, _lvPx, _lvPy;
        // light_range (LightRange, with chunk_lights): this frame's range limit and budget are on; the range in tiles
        private bool _lightRange;
        private int _lvRange;
        private Item _multi;
        private Rectangle _rectangleObj = Rectangle.Empty,
            _rectanglePlayer;
        private long _timePing;

        /// <summary>The game time after which the next ping is sent (Time.Ticks &gt; this); 0 before the first.</summary>
        internal long PingDueTick => _timePing;

        /// <summary>
        /// present-on-change: GameController sends the ping after its draw decision (ServicePing), so it can hold it until
        /// after a Present that is about to happen; the scene then does not send it from Update.
        /// </summary>
        internal bool LoopSendsPing;

        internal bool PingDue => _world.InGame && Time.Ticks > _timePing;

        internal void SendPing()
        {
            NetClient.Socket.Statistics.SendPing();
            _timePing = (long)Time.Ticks + 1000;
        }

        private uint _timeToPlaceMultiInHouseCustomization;
        private readonly UseItemQueue _useItemQueue;
        private bool _useObjectHandles;
        // the player's race and the profile's name-overhead filter for NameOverHeadManager.IsAllowed, read once per
        // render-list pass (an ECS lookup and a profile read per object)
        private bool _handlesPlayerIsGargoyle;
        private NameOverheadTypeAllowed _handlesTypeAllowed;
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

        /// <summary>
        /// T9 S9: the house placement preview item. Its graphic and hue live in a preview record of its own (owner: the scene,
        /// <see cref="WorldEcs.ReleasePreviews"/> when it goes), as the preview gumps' objects.
        /// </summary>
        internal static Item CreatePlacementPreview(World world, object owner, ushort model, ushort hue)
        {
            Item preview = Item.Create(world, HouseManager.PLACEMENT_PREVIEW_SERIAL);
            preview.IsMulti = true;
            world.Writer.SetPreviewItem(owner, preview, model, hue, Layer.Invalid, 0xFFFF_FFFF);

            return preview;
        }

        public override void Unload()
        {

            if (IsDestroyed)
            {
                return;
            }

            // a login that never reached its hold (disconnect): the warm-up collection must not wait for a later scene
            Map.MapWarmup.Cancel("game scene unloaded");

            // T9 S9: the house placement preview's record, when the scene goes while placing
            _world.Ecs.ReleasePreviews(this);


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

            // first-sight art decoded for the chunk loader is drained only by Map.UpdateLoading, which stops with the scene, and
            // animation directions only by drawing: return the finished decodes' buffers and decode-budget bytes now, take the
            // waiting ones out of the budget (workers still running return theirs when they finish)
            Client.Game.UO.Arts?.CancelPending();
            Client.Game.UO.Texmaps?.CancelPending();
            Client.Game.UO.Animations?.CancelPending();

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
            Log.Warn($"Disconnected from the game server: {e}");

            // a clean close by the server reports Success; say what it is
            if (e == SocketError.Success)
            {
                e = SocketError.ConnectionReset;
            }

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
                            NetClient.Socket.DisconnectReason ?? StringHelper.AddSpaceBeforeCapital(e.ToString())
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

        internal (byte Id, ushort Color, bool IsHue, int X, int Y) LightAt(int i) =>
            (_frameLights[i].ID, _frameLights[i].Color, _frameLights[i].IsHue, _frameLights[i].DrawX, _frameLights[i].DrawY);

        internal int LightCount => _frameLights.Count;

        internal void TruncateLights(int count) => _frameLights.Truncate(count);

        public void AddLight(GameObject obj, GameObject lightObject, int x, int y)
        {
#if CUO_DEVTOOLS
            if (LightSlotDiag.Armed)
            {
                byte outcome = AddLightCore(obj, lightObject, x, y);

                if (outcome != 1)
                {
                    LightSlotDiag.Record(obj, lightObject, x, y, outcome, outcome == LightSlotDiag.Added ? _frameLights[_frameLights.Count - 1].ID : (byte)255);
                }

                return;
            }
#endif
            AddLightCore(obj, lightObject, x, y);
        }

        // view_margin: the visible world in the light list's coordinates (the walk's rectangle without its tile of slack) and
        // the player's light position, once per light frame
        private void SetLightView()
        {
            _lightViewFrame = _lightsFrame;
            Point vMin = Camera.ScreenToWorld(Point.Zero);
            Point vMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            _lvLeft = vMin.X;
            _lvTop = vMin.Y;
            _lvRight = vMax.X;
            _lvBottom = vMax.Y;
            _lvPx = _world.Player != null ? _world.Player.RealScreenPosition.X + 22 : (vMin.X + vMax.X) >> 1;
            _lvPy = _world.Player != null ? _world.Player.RealScreenPosition.Y + 22 : (vMin.Y + vMax.Y) >> 1;

            if (_lightMargin < 0)
            {
                _lightMargin = ViewMargin.LightMargin(Client.Game.UO.FileManager.Lights.MaxExtent(LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT));
            }

            // light_range: the zoom-1 view is the game window's size in screen pixels
            _lvRange = LightRange.Range(Settings.GlobalSettings?.LightRangeTiles ?? 0,
                LightRange.AutoRange(Camera.Bounds.Width, Camera.Bounds.Height, _lightMargin));
        }

        // 0 added, 1 lights off, 2 refused by the cap, 3 hidden by an object above, 4 light index out of range, 5 its halo
        // cannot reach the view (view_margin), 6 past light_range's range (LightSlotDiag)
        private byte AddLightCore(GameObject obj, GameObject lightObject, int x, int y)
        {
            if (
                !UseLights && !UseAltLights
                || obj == null
            )
            {
                return 1;
            }

            if (_frameLights.Full)
            {
                _frameLights.Refused++;

                return 2;
            }

            bool canBeAdded = true;

            int testX = obj.X + 1;
            int testY = obj.Y + 1;

            // light_occlusion keeps this whole-light rejection for now (review P1 #3, round 5): it hides the lit windows under
            // a roof eave (Moonglow house by the street lamps), whose glow the cover test alone drew over the roof in front.
            // It goes when the occluder rule hides glow over intervening faces, not only over the light point
            TileListObject tile = _world.Map.GetTile(testX, testY);

            if (tile != null)
            {
                sbyte z5 = (sbyte)(obj.Z + 5);

                ArtLoader arts = Client.Game.UO.FileManager.Arts;

                for (TileListObject o = tile; o != null; o = o.TNext)
                {
                    // a component without art (empty tiledata entry) is invisible: it cannot shade the light
                    if (
                        (!(o is Static s) || s.ItemData.IsTransparent)
                            && (!(o is Multi m) || m.ItemData.IsTransparent)
                        || !o.AllowedToDraw
                        || !MaxDrawZState.HasDrawableArt(arts, o.Graphic)
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
                ref FrameLights.Entry light = ref _frameLights.Next();
                light.Share = 1f;

                ushort graphic = lightObject.Graphic;

                // the light index rules live in LightEmitters, so chunk_lights' emitter table follows them
                if (LightEmitters.ForcedLight2(graphic))
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
                    if (LightEmitters.ColoredIndex(light.ID, out byte shape, out ushort tint))
                    {
                        light.Color = tint;
                        light.ID = shape;
                    }

                    if (LightColors.GetHue(graphic, out ushort color, out bool ishue))
                    {
                        light.Color = color;
                        light.IsHue = ishue;
                    }
                }

                if (light.ID >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                {
                    return 4;
                }

                if (light.Color != 0)
                {
                    light.Color++;
                }

                light.DrawX = x;
                light.DrawY = y;
                light.Source = obj;
                light.SrcX = obj.X;
                light.SrcY = obj.Y;
                light.SrcZ = obj.Z;

                if (_vmLights)
                {
                    // view_margin: a light whose halo cannot reach the visible world takes no slot
                    if (_lightViewFrame != _lightsFrame)
                    {
                        SetLightView();
                    }

                    Client.Game.UO.FileManager.Lights.Extent(light.ID, out int lw, out int lh);

                    if (!ViewMargin.LightReaches(x, y, lw, lh, _lvLeft, _lvTop, _lvRight, _lvBottom))
                    {
                        return 5;
                    }

                    // light_range: no slot past the range, a fade over its last tiles
                    if (_lightRange || LightRange.Check)
                    {
                        float d = LightRange.TileDistance(x, y, _lvPx, _lvPy);

                        if (!LightRange.InRange(d, _lvRange))
                        {
#if CUO_DEVTOOLS
                            _lrDropped++;
#endif
                            if (_lightRange)
                            {
                                return 6;
                            }
                        }
                        else if (_lightRange)
                        {
                            light.Share = LightRange.RangeShare(d, _lvRange);
                        }
                    }

                    _frameLights.Commit(FrameLights.Priority(x, y, _lvLeft, _lvTop, _lvRight, _lvBottom, _lvPx, _lvPy));

                    return 0;
                }

                _frameLights.Commit();

                return 0;
            }

            return 3;
        }

        private void FillGameObjectList()
        {
            FrameStats.Inc(FrameCounter.PrepareFullRuns);
            FrameStats.Inc(FrameCounter.FillListFullRuns);
            FrameStats.Inc(FrameCounter.PrepareRuns);
            _renderLists.Clear();
            _visibleChunks.Clear();

            _foliageCount = 0;
            _drawFrame++;
            _animatedArtInView = false;

            if (!_world.InGame)
            {
                return;
            }

            PickSkip.BeginFill(UIManager.IsMouseOverWorld);
            PickBox.BeginFill();

            // T6a: the tile lists exactly as this frame's walk (both renderers) is about to read them
            if (Map.TileOrder.Check)
            {
                Map.TileOrder.CheckFrame(_world);
            }

            long setupStart = FrameStats.Stamp();
            BeginMeshHueFrame();
            // while the world settle gate holds the last frame nobody sees the fades: alphas jump to their targets, so the
            // first frame after the hold doesn't show statics of freshly loaded chunks fading in (they start at 0)
            _snapAlphas = _settle.Holding;
            int fadeSteps = FadeSteps(Time.Ticks, ref _alphaTimer, Client.Game.DisplayCapActive);
            _alphaStepSize = 25 * Math.Max(1, fadeSteps);
            _alphaChanged = fadeSteps != 0 || _snapAlphas;

            if (_alphaChanged)
            {
                _alphaMoved = false;
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
            // perf-16 world_fill_diamond: walk only the square's tiles that can reach the screen (FillDiamond)
            FillDiamond.Bands diamond = BeginFillDiamond();
            bool gpuFill = _gpuWorld != null;

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

            Vector3 playerOffset = _world.Player.Offset;
            _rectanglePlayer.X = (int)(
                _world.Player.RealScreenPosition.X
                - _world.Player.FrameInfo.X
                + 22
                + playerOffset.X
            );
            _rectanglePlayer.Y = (int)(
                _world.Player.RealScreenPosition.Y
                - _world.Player.FrameInfo.Y
                + 22
                + (playerOffset.Y - playerOffset.Z)
            );
            _rectanglePlayer.Width = _world.Player.FrameInfo.Width;
            _rectanglePlayer.Height = _world.Player.FrameInfo.Height;

            int minX = _minTile.X;
            int minY = _minTile.Y;
            int maxX = _maxTile.X;
            int maxY = _maxTile.Y;
            Map.Map map = _world.Map;
            bool use_handles = _useObjectHandles;
            _handlesPlayerIsGargoyle = use_handles && GameObject.PlayerIsGargoyle(_world);
            _handlesTypeAllowed = use_handles ? _world.NameOverHeadManager.TypeAllowed : NameOverheadTypeAllowed.None;
            GpuItemCacheBegin(use_handles);
            (var minChunkX, var minChunkY) = (minX >> 3, minY >> 3);
            (var maxChunkX, var maxChunkY) = (maxX >> 3, maxY >> 3);
            long loopStart = FrameStats.AddSince(FramePhase.PrepSetup, setupStart);
            map.DrawPending = 0;

            if (Map.Map.RingPrefetch)
            {
                map.RequestRing(minChunkX, minChunkY, maxChunkX, maxChunkY, Time.Ticks);
            }

            // FrameStats per chunk (masks, walk, hidden) only for the Gpu world and in detail mode. Legacy times the whole
            // chunk loop as PrepWalk, without two timestamps per chunk (~1,400 chunks at the 4K zoom-out, each
            // Stopwatch.GetTimestamp a P/Invoke with a GC poll after it)
            bool chunkStamps = FrameStats.On && (_gpuWorld != null || FrameStats.Detail);
            int nTiles = 0;
            // clip-4: meshes built with the other depth_model setting are rebuilt (their vertices carry the shapes or not)
            bool meshGeo = DepthModel.On;
            // wet_surface_depth changes only the geo meshes' wet flats
            bool meshWet = WetSurfaceDepth.On;
            // atlas_lru: a Legacy mesh drawing from a freed art/texmap page is rebuilt (the Gpu world doesn't draw meshes)
            int atlasEpoch = ClassicUO.Renderer.Animations.AtlasPageClock.FreedEpoch;

            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                for (var chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
                {
                    var chunk = map.GetChunkForDraw(chunkX, chunkY); // unloaded chunks are queued (Map.UpdateLoading)
                    if (chunk == null || chunk.IsDestroyed)
                        continue;

                    // perf-16/perf-18 world_fill_diamond: the tiles this fill walks and the (wider, Gpu) tiles its hidden pass
                    // visits. A chunk with neither can neither draw nor hide anything: no chunk-level work for it.
                    ulong inWalk = FillDiamond.ChunkMasks(gpuFill, chunkX, chunkY, in diamond, out ulong inHidden);
                    inWalk = WalkChunkMask(chunk, chunkX, chunkY, inWalk);

                    if (inHidden == 0)
                        continue;

                    // Build chunk mesh if dirty
                    if (chunk.Mesh.IsDirty || chunk.Mesh.BuiltGeo != meshGeo || (meshGeo && chunk.Mesh.BuiltWet != meshWet) ||
                        (chunk.Mesh.AtlasEpoch != atlasEpoch && _gpuWorld == null && chunk.Mesh.HoldsFreedPage(atlasEpoch)))
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

                    // T6b: the tiles whose walk merges the dynamic index
                    ulong mergeCells = ClassicUO.Game.Map.TileWalk.MergeCells(chunk);
                    // perf-16: the tiles this fill walks (the diamond's); perf-18: the tiles its Gpu hidden pass visits
                    ulong tileMask = inWalk;
                    ulong hiddenMask = inHidden;

                    for (var x = 0; x < 8 && tileMask != 0; x++)
                    {
                        for (var y = 0; y < 8; y++)
                        {
                            ulong bit = 1UL << (x << 3 | y);

                            if ((tileMask & bit) == 0)
                            {
                                continue;
                            }

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
                                        AddTileToRenderList(mobile, use_handles, GPU_WALK_MAX_Z, chunk, true, _gpuSkipEnabled, true);
                                    }
                                }

                                continue;
                            }
                            else if ((_gpuCheckTiles & bit) != 0)
                            {
                                // CUO_GPU_ITEM_CHECK: what the cache would serve, compared with the walk after the capture
                                GpuExpectCachedTile(chunk, x << 3 | y);
                            }

                            // T6b part 3: a tile holding only dynamics has no list head once the list drops them
                            var firstObj = ClassicUO.Game.Map.TileWalk.First(chunk, x, y);
                            if (firstObj == null || firstObj.IsDestroyed)
                                continue;

                            nTiles++;
                            AddTileToRenderList(
                                firstObj,
                                use_handles,
                                GPU_WALK_MAX_Z,
                                chunk,
                                !full,
                                _gpuSkipEnabled,
                                false,
                                (mergeCells & bit) != 0
                            );
                        }
                    }

                    if (chunkStamps)
                    {
                        FrameStats.Enter(FrameStats.NoBucket);
                        walkStart = FrameStats.AddSince(FramePhase.PrepWalk, walkStart);
                    }

                    // after the chunk's walk: CollectGpuHidden only reads what the walk stamped on this tile's objects
                    if (_gpuWorld != null && (hiddenMask | edgeTiles) != 0)
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

                                // perf-16: nothing of a tile outside the hidden bands reaches the view. perf-18: a tile inside
                                // them but outside the walk's was not walked; the walk would have drawn (stamped) nothing there
                                if ((full || (walkTiles & bit) != 0) && (hiddenMask & bit) != 0)
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
#if CUO_DEVTOOLS
            CheckSpriteMargin();
#endif
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
                            for (TileListObject obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
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
                    if (e.TextAllExpired())
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

        // visual-due tracking (evloop-4): the scene's own visual changes that no packet or input causes. The mobiles,
        // corpses and world text report from their own walks (World.Update, TextRenderer.Draw).
        private void NoteVisualDue()
        {
            if (_animatedArtInView && _animatedStaticsManager.HasAnimations)
            {
                VisualDue.At(_animatedStaticsManager.NextChange, VisualSource.StaticAnim);
            }

            if (_alphaMoved)
            {
                VisualDue.At(_alphaTimer, VisualSource.Other);
            }

            if (_world.Weather.IsActive)
            {
                VisualDue.Now(VisualSource.Weather);
            }

            if (_settle.Holding || _isMouseLeftDown || _world.Player.Pathfinder.AutoWalking || _followingMode)
            {
                // the settle hold ends on chunk arrival; a held left button picks up after a delay; auto-walk and follow
                // request steps on their own
                VisualDue.Now(VisualSource.Scene);
            }

            if (ProfileManager.CurrentProfile is { AnimatedWaterEffect: true })
            {
                VisualDue.Now(VisualSource.Other);
            }

            if (_world.Player.IsDead && _world.PlayerState.DeathScreenTimer > Time.Ticks)
            {
                VisualDue.At(_world.PlayerState.DeathScreenTimer, VisualSource.Scene);
            }
        }

        /// <summary>
        /// event_loop_idle_wait: the scene's non-visual work that Update polls, for the wait that follows
        /// (GameController.CollectLoopDue). Held input, walking, a macro, OPL requests, chunk loads and the world map (its
        /// load and the 250 ms party query) poll every Update; the use queue and the delayed click have deadlines.
        /// </summary>
        internal void NoteLoopDue()
        {
            if (!_world.InGame || _world.Player == null)
            {
                LoopDue.Now(LoopDueSource.NotInGame);

                return;
            }

            if (Mouse.LButtonPressed || Mouse.RButtonPressed || Mouse.MButtonPressed || _flags[0] || _flags[1] || _flags[2] || _flags[3]
                || _rightMousePressed || _boatIsMoving)
            {
                LoopDue.Now(LoopDueSource.Input);
            }

            if (_continueRunning || _followingMode || _world.Player.Pathfinder.AutoWalking)
            {
                LoopDue.Now(LoopDueSource.Walk);
            }

            if (_world.Macros.IsRunning)
            {
                LoopDue.Now(LoopDueSource.Macro);
            }

            if (_useItemQueue.HasQueued)
            {
                LoopDue.At(_useItemQueue.Timer, LoopDueSource.UseQueue);
            }

            if (_world.DelayedObjectClickManager.IsEnabled)
            {
                // it fires once Time.Ticks reaches Timer
                LoopDue.At((long)_world.DelayedObjectClickManager.Timer - 1, LoopDueSource.DelayedClick);
            }

            if (PacketHandlers.OplPending(_world))
            {
                LoopDue.Now(LoopDueSource.Opl);
            }

            if (_world.Map is { QueuedChunks: > 0 })
            {
                LoopDue.Now(LoopDueSource.Assets);
            }

            if (WorldMapGump.IsOpen)
            {
                // worldmap_idle_loop off: Now (every Update); on: Now only while a map image loads, else deadlines
                WorldMapGump.NoteLoopDue(_world);
            }
        }

        public override void Update()
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            SelectedObject.TranslatedMousePositionByViewport = MouseToWorld();

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

            if (!LoopSendsPing && PingDue)
            {
                SendPing();
            }

            // the animated statics advance first: World.Update's chunk loader waits for the frame an animated static
            // shows (FirstSightArt.RequestCurrentFrame), which must be the frame this frame's draw uses
            _animatedStaticsManager.Process();
            _world.Update();
            _world.BoatMovingManager.Update();
            _world.Player.Pathfinder.ProcessAutoWalk();
            _world.DelayedObjectClickManager.Update();

            if (VisualDue.Tracking)
            {
                NoteVisualDue();
            }

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
                Mobile follow = _world.FindMobile(_followingTarget);

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
                    _multi = CreatePlacementPreview(_world, this, _world.TargetManager.MultiTargetInfo.Model, _world.TargetManager.MultiTargetInfo.Hue);
                }

                if (SelectedObject.Object is GameObject gobj)
                {
                    ushort x,
                        y;
                    sbyte z;

                    int cellX = gobj.X % 8;
                    int cellY = gobj.Y % 8;

                    // the tile's anchor: its land, or on a landless tile a list object. T6b part 3 (list dropped): never an item,
                    // mobile or effect, so a tile holding only dynamics has none and the selected object's own position is used
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
                _world.Ecs.ReleasePreviews(this);
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

        // skip_draw_when_minimized: the frame's draw is not run. The settle hold's timeout stops counting (nothing streams meanwhile,
        // so the restore must still wait for readiness), and the weather's wind timer and sounds, which live in Draw, keep going.
        public void OnDrawSkipped(double elapsedMs)
        {
            _settle.SkipTime(elapsedMs);

            if (_world.InGame)
            {
                _world.Weather.SimulateSkippedDraw();
            }
        }

        /// <summary>The mouse in the world's screen pixels (relative to the draw offset): its UI position through the inverse of
        /// the world composite (RenderGeometry) to a world-target pixel, then the camera's inverse matrix.</summary>
        internal Point MouseToWorld() => MouseToWorld(Client.Game.Geometry.WithViewport(Camera.Bounds), Camera, Mouse.PositionF);

        internal static Point MouseToWorld(in RenderGeometry geometry, Camera camera, Vector2 mouseUi)
        {
            Vector2 t = geometry.UiToWorldTarget(mouseUi.X, mouseUi.Y);

            return camera.ScreenToWorld(new Point((int)MathF.Floor(t.X), (int)MathF.Floor(t.Y)));
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

            // chunk_lights: a growing list with a safety bound and priorities instead of the classic 100 (ChunkLights)
            _vmLights = ChunkLights.Enabled;
            ChunkStaticLights = _vmLights;
            // light_range: the ranked list keeps at most light_budget lights
            _lightRange = _vmLights && LightRange.Enabled;
            _frameLights.BeginFrame(++_lightsFrame,
                _lightRange ? LightRange.Budget(Settings.GlobalSettings?.LightBudget ?? 0)
                : _vmLights ? FrameLights.SafetyBound : LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT, _vmLights);
            Viewport r_viewport = batcher.GraphicsDevice.Viewport;
            Matrix matrix = Camera.ViewTransformMatrix;

            var gpu = Client.Game.GetGpuWorld();
            // anim_frame_count_tables: the advance reads the frame counts from the tables only while the Gpu world draws
            AnimFrameCount.GpuWorld = gpu != null;

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
                    gpu.ApplyBakeInputs(_world.Season);
                    GpuUpdateStaticSeason(gpu);
                    hold = _settle.Begin(Time.Ticks, gpu.World, _world.MapIndex, (player.X - player.Y) * 22, (player.X + player.Y) * 22 - (player.Z << 2),
                        ReferenceEquals(target, _settleTarget), gpu.Rebakes);
                    _settleTarget = target;
                }

                FrameStats.WorldHeld = hold;
                FrameStats.WorldShown |= !hold;

                if (ready)
                {
                    DrawWorldGpu(batcher, ref matrix, renderTargets, gpu, hold);

                    // ready = the world pass has everything near its view, and the client has loaded every chunk its walk
                    // visited (a chunk still in the ChunkLoadQueue has no dynamic objects or overrides yet)
                    bool visibleReady = gpu.VisibleReady && _world.Map.DrawPending == 0;
                    _settle.End(visibleReady);
                    // asset_ready_wake: a held or partial frame is followed by one as soon as a load it waits for completes
                    var gw = gpu.World;
                    // asset_prefetch_on_packet: a mobile kept its previous look, or drew nothing at first sight, while its pages load
                    bool lookWait = LookPrefetch.FrameHeld != 0 || LookPrefetch.FrameFirstSightWait != 0;
                    AssetWake.SceneIncomplete = (hold || !visibleReady || lookWait) && (gw.LoadsPending || _world.Map.DrawPending != 0);

                    if (AssetWake.SceneIncomplete)
                    {
                        AssetWake.SceneCauses |= (gw.Chunks.PendingChunks != 0 || gw.Chunks.UploadQueueLength != 0 ? AssetWake.CauseChunks : 0)
                            | (gw.Pages.Pending ? AssetWake.CausePages : 0) | (gw.AnimPages != null && gw.AnimPages.Pending ? AssetWake.CauseAnimPages : 0)
                            | (_world.Map.DrawPending != 0 ? AssetWake.CauseMapDraw : 0) | (lookWait ? AssetWake.CauseHeld : 0);
                    }
                    AssetWake.SceneStale = gw.Pages.LoadedNotPlaced + (gw.AnimPages?.LoadedNotPlaced ?? 0);
#if CUO_DEVTOOLS
                    if (hold || _settle.Episode != WorldDiscontinuity.None)
                    {
                        PopInStats.EndRun();
                    }
                    else
                    {
                        PopInStats.OnWalkingFrame(visibleReady, gpu.World, _world.Map.DrawPending);
                    }
#endif

                    if (Map.MapWarmup.CollectPending)
                    {
                        // the login's deliberate collection, in a frame whose world isn't shown (MapWarmup)
                        Map.MapWarmup.OnWorldFrame(true, hold, visibleReady, _settle.Episode == WorldDiscontinuity.Entry, _settle.TimeoutMs);
                    }

                    if (SettleLog && !hold && !visibleReady)
                    {
                        LogUnsettledFrame(gpu);
                    }
                }

                // after the world pass: the lights its walk collected, at this frame's offset (FrameLights)
                if (!hold)
                {
                    FrameStats.Begin(FramePhase.Lights);
                    PrepareLightsRendering(batcher, ref matrix, renderTargets);
                    FrameStats.End(FramePhase.Lights);
                }
                else
                {
                    // the light target keeps the held frame's lights, and the composite keeps the configuration they were
                    // drawn for: setting it from this frame's light state composited the held target in another mode when the
                    // state changed during the hold (leaving a dark dungeon into daylight through a teleporter: the held
                    // darkness map, black with orange torch halos, alpha-blended over the world, light-edges)
#if CUO_DEVTOOLS
                    LightFrameLog.OnHeld();
#endif
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
                DrawWorld(batcher, ref matrix, renderTargets);
                // after the world draw: the lights its walk collected, at this frame's offset (FrameLights)
                FrameStats.Begin(FramePhase.Lights);
                PrepareLightsRendering(batcher, ref matrix, renderTargets);
                FrameStats.End(FramePhase.Lights);
                _settle.OnOtherRenderer();
                FrameStats.WorldShown = true;

                if (Map.MapWarmup.CollectPending)
                {
                    // no settle hold in Legacy: the first world frame is the login transition (MapWarmup)
                    Map.MapWarmup.OnWorldFrame(false, false, true, true, 0);
                }

                _settleTarget = renderTargets.WorldRenderTarget;
                renderTargets.WorldFade = 1f;
                FrameStats.Set(FrameCounter.GpuInstances, 0);
                FrameStats.Set(FrameCounter.GpuDynamic, 0);
                FrameStats.Set(FrameCounter.GpuOverrides, 0);
                FrameStats.Set(FrameCounter.GpuDrawCalls, 0);
            }

            batcher.GraphicsDevice.Viewport = r_viewport;
#if CUO_DEVTOOLS
            LightFrameLog.OnWorldOffset(_offset.X, _offset.Y);
            LightFrameLog.OnCompose(_composeMode, _lightTargetMode, _world.Light.Overall, _world.Light.Personal, _world.Light.RealOverall, _world.Light.RealPersonal, _world.Player.X, _world.Player.Y, _frameLights.Refused);
#endif

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
            // the fill moved the draw offset: the matrix snaps the world origin for it (Camera.SetWorldOffset)
            matrix = Camera.ViewTransformMatrix;

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

            if (DepthModel.On)
            {
                // clip-4: the geometric depth (HueGeoTechnique) for the rest of the world pass, relative to the view's centre
                batcher.SetGeoDepth(true, LegacyGeo.CamSum(_offset.Y + (batcher.GraphicsDevice.Viewport.Height >> 1)));
            }

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
                LegacySpriteSink.DrawPreview(batcher, _multi, _multi.CalculateDepthZ());
            }

            // draw weather
            _world.Weather.Draw(batcher, 0, 0, MAX_LAYER_DEPTH - 1);

            DrawSelection(batcher, MAX_LAYER_DEPTH);

            batcher.SetSampler(null);
            batcher.SetStencil(null);
            batcher.SetCircleOfTransparencyRadius(0f);
            batcher.End();
#if CUO_DEVTOOLS
            if (GeometryProbe.Active)
            {
                GeometryProbe.OnWorldPass(batcher.GraphicsDevice, batcher, renderTargets.WorldRenderTarget, matrix, false, 0, 0, 0, 0, _offset.X, _offset.Y);
            }
#endif

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
#if CUO_DEVTOOLS
                _lightTargetMode = 0;
                LightFrameLog.OnLightPass(false, UseLights, UseAltLights, _world.Light.IsometricLevel, _frameLights.Count, 0, _offset.X, _offset.Y);
#endif

                return false;
            }

            // view_margin: the static lights from the chunks' emitter lists, and the light sources beside the walked view whose
            // halo reaches into it (ViewMargin)
            AddChunkLights();
            int lightCount = _frameLights.CountFor(_lightsFrame);

            // light_range: past the budget the lowest-ranked kept lights fade by rank
            if (_lightRange && _frameLights.Refused > 0 && lightCount > 0)
            {
                _frameLights.FadeLowest(LightRange.FadeRanks);
            }
#if CUO_DEVTOOLS
            LightRangeCheck(lightCount);

            if (LightSlotDiag.Armed)
            {
                Point vMin = Camera.ScreenToWorld(Point.Zero);
                Point vMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
                LightSlotDiag.EndFrame(vMin.X, vMin.Y, vMax.X, vMax.Y, lightCount);
            }

            _lightTargetMode = UseAltLights ? 2 : 1;
#endif
            batcher.GraphicsDevice.SetRenderTarget(renderTargets.LightRenderTarget);
            // light_batch: the batched path runs with light_occlusion (LightBatch)
            bool batch = LightBatch.Enabled;
#if CUO_DEVTOOLS
            // CUO_LIGHT_BATCH_CHECK: the other path first, read back, then the active one over it
            bool compare = LightBatchCompareDue();

            if (compare)
            {
                ClearLightTarget(batcher.GraphicsDevice);
                DrawLightPass(batcher, ref matrix, lightCount, !batch);
                batcher.GraphicsDevice.SetRenderTarget(null);
                LightBatchReadback(renderTargets.LightRenderTarget, true);
                batcher.GraphicsDevice.SetRenderTarget(renderTargets.LightRenderTarget);
            }
#endif
            ClearLightTarget(batcher.GraphicsDevice);

#if CUO_DEVTOOLS
            if (LightFrameLog.Active)
            {
                uint h = 2166136261;

                for (int i = 0; i < lightCount; i++)
                {
                    ref FrameLights.Entry d = ref _frameLights[i];
                    h = (h ^ (uint)(d.ID | d.Color << 8)) * 16777619;
                    h = (h ^ (uint)d.DrawX) * 16777619;
                    h = (h ^ (uint)d.DrawY) * 16777619;
                }

                LightFrameLog.OnLightPass(true, UseLights, UseAltLights, _world.Light.IsometricLevel, lightCount, h, _offset.X, _offset.Y);
            }
#endif
            DrawLightPass(batcher, ref matrix, lightCount, batch);
#if CUO_DEVTOOLS
            LightOcclusionSummary();
            LightAlphaFadeSummary();
            LightRangeSummary();
#endif
#if CUO_DEVTOOLS
            if (GeometryProbe.Active)
            {
                GeometryProbe.OnLightPass(batcher.GraphicsDevice, batcher, renderTargets.LightRenderTarget, matrix, lightCount,
                    lightCount > 0 ? _frameLights[0].DrawX : 0, lightCount > 0 ? _frameLights[0].DrawY : 0);
            }
#endif

            batcher.GraphicsDevice.SetRenderTarget(null);
#if CUO_DEVTOOLS
            if (compare)
            {
                LightBatchReadback(renderTargets.LightRenderTarget, false);
            }

            LightBatchSummary();
#endif

            return true;
        }

        private void InitializeRenderTargets(RenderTargets renderTargets)
        {
#if CUO_DEVTOOLS
            _composeMode = UseAltLights ? 2 : UseLights ? 1 : 0;
#endif
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
                    if (_world.Player.IsDead && _world.PlayerState.DeathScreenTimer > Time.Ticks)
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
    }
}
