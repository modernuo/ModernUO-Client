// CLEANUP(release): DevBench (scripted runs, CUO_FRAMESTATS, runtime renderer switching) is dev tooling; without
// CUO_DEVTOOLS only the stub at the end of this file is compiled.
#if CUO_DEVTOOLS
using System;
using System.Diagnostics;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Utility.Logging;

namespace ClassicUO
{
    // Opt-in measurement script, driven by environment variables:
    //   CUO_BENCH_GO="x y"  once in game, [go there, zoom 1, then measure
    //   CUO_FRAMESTATS=1    uncap the frame rate, force inactive throttling off, log per-phase timings every 5 s (FrameStats)
    //   CUO_BENCH_PLAN="name:x y:zoom;name:travel;name:walk;name:bodies;name:ltravel;name:glide;say:text;vsync:on|off"  measure each entry for CUO_BENCH_WINDOWS (default 3) windows, then quit
    //     bodies: swap the player to a new body every 50 ms; ltravel: client-only teleports between towns every 2 s;
    //     glide: client-only travel east from Britain at 25 tiles/s (ltravel/glide need no staff account)
    //     lswalk: [go 5156 2150, then walk the large-scale scene's lane (5141..5171, y 2150) end to end (tools/large-scale)
    //     bwalk: [go 1495 1528, then walk to 1495 1542 and back (a region edge, two chunk edges, a z 40 -> 30 slope),
    //     logging each leg's end position against the map's land z and the server's walk denials
    //   CUO_BENCH_NOSHOT=1  no screenshot when a plan entry starts measuring (keeps the PNG encode out of hitch counts)
    //   CUO_BENCH_SIZE=1920x1080  resize the window client area to this many pixels with the game viewport filling it
    //   CUO_BENCH_EXIT=1    walk a few steps, take screenshots and quit when done
    //   CUO_BENCH_SPOTS="x y;x y"  screenshot each spot at zoom 1 and full light, then quit
    //   CUO_BENCH_UI=1      at Britain: paperdoll, backpack, minimap, picking, multi preview, night lights, world map
    //   CUO_BENCH_PICK=1    at Britain: hover and click a static, land, an item and a mobile, under the legacy then the GPU world renderer
    //   CUO_BENCH_SHOTS=dir plan entries also save the world render target as dir/<entry>_<renderer>.png
    //   plan entries world:gpu|legacy switch the world renderer at runtime
    //   plan entries effect:x y z:graphic[:blend] and moving:x y z:x y z:graphic[:blend] spawn client-side effects; shot:name saves a world shot now; season:winter changes the season locally
    //   itemhue:hex,hex,... sets the hues of the nearest ground items, ghost:on|off swaps the player body (local-only render checks)
    //   CUO_BENCH_TOPMOST=1 keep the window on top while benching (occluded windows are throttled)
    //   plan entries size:WxH|max change the CUO_BENCH_SIZE client area mid-plan (max = maximised window)
    //   plan entry gumps opens the paperdoll, backpack, journal and status bar (the UI allocation/parity scene); shots also save the UI render target
    //   plan entries prepdetail:on|off switch FrameStats' world prepare detail buckets (CUO_FRAMESTATS_PREP=1 starts with them on)
    //   plan entries gpuskip:on|off switch the Gpu world prepare's skipping of baked land and statics (gpu_prepare_skip_baked)
    //   plan entries itemcache:on|off switch its ground-item cache (gpu_prepare_item_cache); name:lsreinit is reinit in the large-scale scene
    //   plan entries activeitems:on|off switch World.Update's active item list (world_update_active_items, W5); CUO_W5_CHECK=1 verifies it
    //   plan entries mobcache:on|off switch the Gpu world's mobile frame cache (gpu_mobile_frame_cache, W5); CUO_MOBILE_CACHE_CHECK=1 verifies it
    //   plan entries w5:on|off switch all three W5 shortcuts (activeitems, mobcache, textskip) together
    //   plan entries textskip:on|off switch skipping the text layout of entities whose overhead texts all expired (text_skip_expired, W5)
    //   pickscan:CxR hovers a C x R grid of the game view and compares the selection with that skipping off and on; cot:on|off
    //   switches the circle of transparency
    //   name:reinit drops the GPU world pass every 2 s (recreated next frame, as on a facet change); name:reinitrt also drops
    //   the world render target (as at login). Each plan entry logs the world settle counters (WorldSettleGate) when it ends.
    //   CUO_WORLD_HOLD=0    never hold the last world frame after a discontinuity (observe only: the before/after baseline)
    //   CUO_WORLD_HOLD_TIMEOUT=ms  the hold's timeout (default 1500)
    //   CUO_BENCH_TPSHOTS=dir[|frames]  ltravel/reinit/reinitrt: save the world target (and lights, UI) of each of the first
    //   frames (default 8) after every teleport or recreation as dir/tpNN_fMM.png (what each frame shows: held or new)
    internal static class DevBench
    {
        public static readonly bool FrameStats = Environment.GetEnvironmentVariable("CUO_FRAMESTATS") == "1";
        private static readonly string _go = Environment.GetEnvironmentVariable("CUO_BENCH_GO");
        private static readonly bool _exit = Environment.GetEnvironmentVariable("CUO_BENCH_EXIT") == "1";
        private static readonly string[] _spots = Environment.GetEnvironmentVariable("CUO_BENCH_SPOTS")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        private static readonly bool _ui = Environment.GetEnvironmentVariable("CUO_BENCH_UI") == "1";
        private static readonly string[] _plan = Environment.GetEnvironmentVariable("CUO_BENCH_PLAN")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        private static string _size = Environment.GetEnvironmentVariable("CUO_BENCH_SIZE");
        private static readonly bool _pick = Environment.GetEnvironmentVariable("CUO_BENCH_PICK") == "1";
        private static readonly string _shots = Environment.GetEnvironmentVariable("CUO_BENCH_SHOTS");
        // the screenshot taken as a plan entry starts lands in its first measurement window (a PNG encode on the main thread)
        private static readonly bool _noShot = Environment.GetEnvironmentVariable("CUO_BENCH_NOSHOT") == "1";
        private static readonly int _planWindows = int.TryParse(Environment.GetEnvironmentVariable("CUO_BENCH_WINDOWS"), out int w) ? w : 3;
        private static readonly (int X, int Y)[] _travel =
        {
            (1823, 2821), (2899, 676), (2498, 392), (542, 985), (596, 2138), (4442, 1172), (1495, 1629)
        };
        private static int _planIndex, _travelIndex, _windowsAtStart;
        private static ushort _ghostRestore, _bodyRestore;
        private static int _bodyNext, _bodiesShown, _glideX, _bwalkLegs, _bwalkDenies = -1;

        // cumulative world settle counters (WorldSettleGate): unsettled frames presented, frames held, hold durations
        private static void LogSettle(Game.Scenes.GameScene gs, string where)
        {
            if (gs != null)
            {
                Log.Info($"[settle] {where}: hold {(gs.Settle.HoldEnabled ? "on" : "off")}; {gs.Settle.Stats}");
            }
        }

        // what the 0x20 player update does for a teleport, without the server: chunks, regions and art load as on a real teleport
        private static void LocalTeleport(World world, Game.Scenes.GameScene gs, int x, int y)
        {
            // a real teleport gets z from the server; here it comes from the map. While the destination region is still
            // being decoded (Map prefetch) the player keeps its z and FixTeleportZ corrects it once the region is in, so
            // the bench does not wait for a decode that a real teleport would not wait for
            _fixZ = null;

            if (!Client.Game.UO.FileManager.Maps.TryGetLandZ(world.Map.Index, x, y, out sbyte z))
            {
                world.Map.Prefetch(x, y);
                z = world.Player.Z;
                _fixZ = (x, y);
            }
            world.RangeSize.X = x;
            world.RangeSize.Y = y;
            world.Player.Walker.DenyWalk(0xFF, -1, -1, -1);

            if (gs != null)
            {
                gs.UpdateDrawPosition = true;
            }

            world.Player.SetInWorldTile((ushort)x, (ushort)y, z == -125 ? (sbyte)0 : z);
        }

        private static (int X, int Y)? _fixZ;

        // LocalTeleport's z, once the destination region is decoded (while the player is still there)
        private static void FixTeleportZ(World world)
        {
            if (_fixZ is not { } t)
            {
                return;
            }

            if (world.Player.X != t.X || world.Player.Y != t.Y)
            {
                _fixZ = null;
            }
            else if (Client.Game.UO.FileManager.Maps.TryGetLandZ(world.Map.Index, t.X, t.Y, out sbyte z))
            {
                _fixZ = null;
                world.Player.SetInWorldTile((ushort)t.X, (ushort)t.Y, z == -125 ? (sbyte)0 : z);
            }
        }

        private static long _travelAt;
        private static readonly string _tpShots = Environment.GetEnvironmentVariable("CUO_BENCH_TPSHOTS");
        private static int _tpShotLeft, _tpShotIndex, _tpShotFrame;

        private static void StartTeleportShots()
        {
            if (!string.IsNullOrEmpty(_tpShots))
            {
                string[] parts = _tpShots.Split('|');
                _tpShotLeft = parts.Length > 1 && int.TryParse(parts[1], out int k) ? k : 8;
                _tpShotIndex++;
                _tpShotFrame = 0;
            }
        }

        private static void TeleportShot(GameController game)
        {
            if (_tpShotLeft > 0)
            {
                _tpShotLeft--;
                game.RequestWorldShot(System.IO.Path.Combine(_tpShots.Split('|')[0], $"tp{_tpShotIndex:00}_f{_tpShotFrame++:00}.png"));
            }
        }
        private static bool _walkBack;
        private static int _planFrames;
        private static bool _gumpsOpened;
        private static long _appliesAtStart;
        private static string _planMode;
        private static long _planStart;
        private static int _spot;
        private static Microsoft.Xna.Framework.Point? _mouse;
        private static string _pickWhat;
        private static Game.GameObjects.GameObject _pickObject;
        private static Microsoft.Xna.Framework.Point? _pickTarget;

        // flip:N[:frames[:walk]] state: world renderer toggles left, frames between toggles, walking while flipping
        private static int _flipLeft, _flipEvery, _flipFrame, _flipTotal;
        private static bool _flipWalk;

        private const int WINDOWS = 6;
        private static int _step;
        private static long _stepAt;

        public static bool Enabled => FrameStats || _go != null || _spots != null || _ui || _plan != null || _pick;

        public static void Update(GameController game)
        {
            long now = Stopwatch.GetTimestamp();

            if (Configuration.ProfileManager.CurrentProfile is { } profile)
            {
                profile.ReduceFPSWhenInactive = false;

                if (_shots != null)
                {
                    // keeps the timestamped "screenshot stored in" line out of the UI shots
                    profile.HideScreenshotStoredInMessage = true;
                }

                if (_spots != null)
                {
                    profile.UseCustomLightLevel = true;
                    profile.LightLevel = 0;
                    profile.LightLevelType = 0;
                }
            }

            if (_spots != null)
            {
                UpdateSpots(game, now);

                return;
            }

            if (_ui)
            {
                UpdateUi(game, now);

                return;
            }

            if (_pick)
            {
                UpdatePick(game, now);

                return;
            }

            if (_plan != null)
            {
                UpdatePlan(game, now);

                return;
            }

            if (_go == null || !(game.UO.World?.InGame ?? false))
            {
                return;
            }

            if (_step == 0)
            {
                _step = 1;
                _stepAt = now + Stopwatch.Frequency * 3;
            }
            else if (_step == 1 && now >= _stepAt)
            {
                GameActions.Say($"[go {_go}");
                _step = 2;
                _stepAt = now + Stopwatch.Frequency * 8;
            }
            else if (_step == 2 && now >= _stepAt)
            {
                if (game.Scene is Game.Scenes.GameScene gs)
                {
                    gs.Camera.Zoom = 1f;
                }

                ApplySize(game);
                game.TakeScreenshot();
                Log.Info($"[bench] at {game.UO.World.Player.X},{game.UO.World.Player.Y},{game.UO.World.Player.Z} zoom 1; measuring");
                _windowsAtStart = ClassicUO.FrameStats.WindowsReported;

                if (FrameStats)
                {
                    ClassicUO.FrameStats.StartWindow("go");
                }

                _step = 3;
            }
            else if (_step == 3 && (!FrameStats || ClassicUO.FrameStats.WindowsReported - _windowsAtStart >= WINDOWS))
            {
                ClassicUO.FrameStats.StopWindow();
                _step = _exit ? 4 : 99;
                _stepAt = now;
            }
            else if (_step >= 4 && _step < 40 && now >= _stepAt)
            {
                // walk: 12 steps east, 12 steps north, screenshot at each corner
                Direction dir = _step < 16 ? Direction.East : Direction.North;
                game.UO.World.Player.Walk(dir, false);
                _step++;
                _stepAt = now + Stopwatch.Frequency / 2;

                if (_step == 16 || _step == 28)
                {
                    game.TakeScreenshot();
                }

                if (_step == 28)
                {
                    _step = 40;
                    _stepAt = now + Stopwatch.Frequency * 2;
                }
            }
            else if (_step == 40 && now >= _stepAt)
            {
                game.TakeScreenshot();
                Log.Info("[bench] done");
                _step = 99;
                game.Exit();
            }
        }

        private static void UpdateSpots(GameController game, long now)
        {
            if (!(game.UO.World?.InGame ?? false))
            {
                return;
            }

            game.UO.World.Light.Overall = 0;

            if (now < _stepAt)
            {
                return;
            }

            if (_step == 0)
            {
                _step = 1;
                _stepAt = now + Stopwatch.Frequency * 5;
            }
            else if (_step == 1)
            {
                if (_spot >= _spots.Length)
                {
                    Log.Info("[bench] spots done");
                    _step = 99;
                    game.Exit();

                    return;
                }

                GameActions.Say($"[go {_spots[_spot].Trim()}");
                _step = 2;
                _stepAt = now + Stopwatch.Frequency * 10;
            }
            else if (_step == 2)
            {
                if (game.Scene is Game.Scenes.GameScene gs)
                {
                    gs.Camera.Zoom = 1f;
                }

                _step = 3;
                _stepAt = now + Stopwatch.Frequency * 2;
            }
            else if (_step == 3)
            {
                game.TakeScreenshot();
                Log.Info($"[bench] spot {_spot} at {game.UO.World.Player.X},{game.UO.World.Player.Y},{game.UO.World.Player.Z}");
                _spot++;
                _step = 1;
                _stepAt = now + Stopwatch.Frequency * 2;
            }
        }

        private static void UpdateUi(GameController game, long now)
        {
            var world = game.UO.World;

            if (!(world?.InGame ?? false))
            {
                return;
            }

            if (_mouse.HasValue && game.Scene is Game.Scenes.GameScene scene)
            {
                Input.Mouse.Position = _mouse.Value;
                var bounds = game.Window.ClientBounds;
                SDL3.SDL.SDL_WarpMouseInWindow(
                    game.Window.Handle,
                    _mouse.Value.X * game.DpiScale * bounds.Width / game.GraphicManager.PreferredBackBufferWidth,
                    _mouse.Value.Y * game.DpiScale * bounds.Height / game.GraphicManager.PreferredBackBufferHeight
                );
                scene.Camera.Update(true, Time.Delta, _mouse.Value);
                SelectedObject.TranslatedMousePositionByViewport = scene.Camera.MouseToWorldPosition();
            }

            if (now < _stepAt)
            {
                return;
            }

            var gs = game.Scene as Game.Scenes.GameScene;
            long sec = Stopwatch.Frequency;

            switch (_step)
            {
                case 0:
                    _step = 1;
                    _stepAt = now + sec * 5;

                    break;

                case 1:
                    GameActions.Say("[go 1495 1629");
                    _step = 2;
                    _stepAt = now + sec * 10;

                    break;

                case 2:
                    if (gs != null)
                    {
                        gs.Camera.Zoom = 1f;
                    }

                    GameActions.OpenPaperdoll(world, world.Player.Serial);
                    GameActions.OpenBackpack(world);
                    GameActions.OpenMiniMap(world);

                    _step = 21;
                    _stepAt = now + sec * 3;

                    break;

                case 21:
                    if (world.Player.FindItemByLayer(Game.Data.Layer.Robe) == null && world.Player.FindItemByLayer(Game.Data.Layer.Backpack) is { } backpack)
                    {
                        for (var i = backpack.Items; i != null; i = i.Next)
                        {
                            if (i is Game.GameObjects.Item it && it.ItemData.IsWearable && it.ItemData.Layer == (byte)Game.Data.Layer.Robe)
                            {
                                GameActions.PickUp(world, it.Serial, 0, 0);
                                Log.Info($"[bench-ui] equipping 0x{it.Graphic:X4} from the backpack");

                                break;
                            }
                        }
                    }

                    _step = 20;
                    _stepAt = now + sec * 2;

                    break;

                case 20:
                    if (Client.Game.UO.GameCursor.ItemHold.Enabled)
                    {
                        GameActions.Equip(world);
                    }

                    _step = 3;
                    _stepAt = now + sec * 3;

                    break;

                case 3:
                    game.TakeScreenshot();
                    Log.Info("[bench-ui] paperdoll, backpack and minimap open");
                    PickAt(gs, world.Player, "player");
                    _step = 4;
                    _stepAt = now + sec;

                    break;

                case 4:
                    LogPick();
                    PickAt(gs, FindStatic(world), "static");
                    _step = 5;
                    _stepAt = now + sec;

                    break;

                case 5:
                    LogPick();

                    world.TargetManager.SetTargetingMulti(0, 0x64, 0, 0, 0, 0);
                    PickAt(gs, world.Map.GetTile(world.Player.X + 4, world.Player.Y + 4), "land for the multi preview");

                    _step = 6;
                    _stepAt = now + sec * 2;

                    break;

                case 6:
                    game.TakeScreenshot();
                    Log.Info("[bench-ui] multi 0x64 placement preview");
                    LogPick();
                    world.TargetManager.Reset();
                    _step = 7;
                    _stepAt = now + sec * 2;

                    break;

                case 7:
                    if (Configuration.ProfileManager.CurrentProfile is { } profile)
                    {
                        profile.UseCustomLightLevel = true;
                        profile.LightLevel = 0x1A;
                    }

                    world.Light.Overall = 0x1A;
                    world.Light.Personal = 0;
                    _step = 8;
                    _stepAt = now + sec * 2;

                    break;

                case 8:
                    game.TakeScreenshot();
                    Log.Info("[bench-ui] night light level 0x1A");
                    world.Light.Overall = 0;
                    GameActions.OpenWorldMap(world);
                    _step = 9;
                    _stepAt = now + sec * 12;

                    break;

                case 9:
                    game.TakeScreenshot();
                    Log.Info("[bench-ui] world map");

                    if (Environment.GetEnvironmentVariable("CUO_BENCH_UNEQUIP") == "1" && world.Player.FindItemByLayer(Game.Data.Layer.Robe) is { } robe)
                    {
                        GameActions.PickUp(world, robe.Serial, 0, 0);
                        Log.Info($"[bench-ui] unequipping 0x{robe.Graphic:X4}");
                    }

                    _step = 10;
                    _stepAt = now + sec * 2;

                    break;

                case 10:
                    if (Client.Game.UO.GameCursor.ItemHold.Enabled && world.Player.FindItemByLayer(Game.Data.Layer.Backpack) is { } pack)
                    {
                        GameActions.DropItem(Client.Game.UO.GameCursor.ItemHold.Serial, 0xFFFF, 0xFFFF, 0, pack.Serial);
                    }

                    _step = 11;
                    _stepAt = now + sec * 2;

                    break;

                case 11:
                    _step = 99;
                    game.Exit();

                    break;
            }
        }

        private static readonly Game.GameObjects.GameObject[] _pickTargets = new Game.GameObjects.GameObject[4];
        private static readonly string[] _pickNames = { "static", "land", "item", "player" };
        private static int _pickIndex;
        private static readonly System.Collections.Generic.Dictionary<string, int> _pickSamples = new System.Collections.Generic.Dictionary<string, int>();

        private static void UpdatePick(GameController game, long now)
        {
            var world = game.UO.World;

            if (!(world?.InGame ?? false))
            {
                return;
            }

            HoldMouse(game);

            if (_step == 4 && _mouse.HasValue)
            {
                var sel = SelectedObject.Object;
                string key = sel is Game.GameObjects.GameObject g ? $"{g.GetType().Name} 0x{g.Graphic:X4} at {g.X},{g.Y},{g.Z}" : sel?.GetType().Name ?? "nothing";
                _pickSamples[key] = _pickSamples.TryGetValue(key, out int c) ? c + 1 : 1;
            }

            if (now < _stepAt)
            {
                return;
            }

            var gs = game.Scene as Game.Scenes.GameScene;
            long sec = Stopwatch.Frequency;

            switch (_step)
            {
                case 0:
                    ApplySize(game);
                    _step = 1;
                    _stepAt = now + sec * 5;

                    break;

                case 1:
                    GameActions.Say("[go 1495 1629");
                    _step = 2;
                    _stepAt = now + sec * 10;

                    break;

                case 2:
                    if (gs != null)
                    {
                        gs.Camera.Zoom = 1f;
                    }

                    _pickTargets[0] = FindStatic(world);
                    _pickTargets[1] = world.Map.GetTile(world.Player.X + 3, world.Player.Y + 3);
                    _pickTargets[2] = FindGroundItem(world);
                    _pickTargets[3] = world.Player;
                    RendererStartup.TrySwitch("legacy", out _);
                    _pickIndex = 0;
                    _step = 3;
                    _stepAt = now + sec * 2;

                    break;

                case 3:
                    if (_pickIndex == 0 || _pickIndex == 4)
                    {
                        Log.Info($"[bench-pick] renderer {Configuration.Settings.GlobalSettings.WorldRenderer}, gpu available {game.GetGpuWorld() != null}");
                        game.TakeScreenshot();
                    }

                    PickAt(gs, _pickTargets[_pickIndex % 4], _pickNames[_pickIndex % 4]);
                    _pickSamples.Clear();
                    _step = 4;
                    _stepAt = now + sec * 2;

                    break;

                case 4:
                    Log.Info($"[bench-pick] {_pickNames[_pickIndex % 4]} selection over 2 s: {string.Join(", ", System.Linq.Enumerable.Select(_pickSamples, kv => $"{kv.Key} x{kv.Value}"))}");
                    LogPick(true);
                    _pickIndex++;

                    if (_pickIndex == 4 && !RendererStartup.TrySwitch("gpu", out string pickRefused))
                    {
                        Log.Info($"[bench-pick] second half stays legacy: {pickRefused}");
                    }

                    _step = _pickIndex >= 8 ? 5 : 3;
                    _stepAt = now + sec * (_pickIndex == 4 ? 3 : 1);

                    break;

                case 5:
                    _step = 99;
                    game.Exit();

                    break;
            }
        }

        private static void HoldMouse(GameController game)
        {
            if (_mouse.HasValue && game.Scene is Game.Scenes.GameScene scene)
            {
                Input.Mouse.Position = _mouse.Value;
                var bounds = game.Window.ClientBounds;
                SDL3.SDL.SDL_WarpMouseInWindow(
                    game.Window.Handle,
                    _mouse.Value.X * game.DpiScale * bounds.Width / game.GraphicManager.PreferredBackBufferWidth,
                    _mouse.Value.Y * game.DpiScale * bounds.Height / game.GraphicManager.PreferredBackBufferHeight
                );
                scene.Camera.Update(true, Time.Delta, _mouse.Value);
                SelectedObject.TranslatedMousePositionByViewport = scene.Camera.MouseToWorldPosition();
            }
        }

        private static Game.GameObjects.GameObject FindGroundItem(World world)
        {
            Game.GameObjects.Item best = null;

            foreach (var item in world.Items.Values)
            {
                if (item.OnGround && !item.IsMulti && item.Distance < 8 && Client.Game.UO.Arts.GetArt(item.DisplayedGraphic).UV.Height > 10
                    && (best == null || item.Distance < best.Distance))
                {
                    best = item;
                }
            }

            return best;
        }

        private static void PickAt(Game.Scenes.GameScene gs, Game.GameObjects.GameObject obj, string what)
        {
            _pickWhat = what;
            _pickObject = obj;

            if (gs == null || obj == null)
            {
                _mouse = null;
                Log.Info($"[bench-ui] pick {what}: no target");

                return;
            }

            // the Gpu prepare may have skipped this object while the camera moved
            gs.RefreshScreenPosition(obj);
            var p = obj.RealScreenPosition;
            var target = obj is Game.GameObjects.Mobile
                ? new Microsoft.Xna.Framework.Point(p.X + 22, p.Y - 15)
                : StaticPoint(obj);
            _pickTarget = target;
            _mouse = gs.Camera.WorldToScreen(target, true);
            Log.Info($"[bench-ui] pick {what}: {obj.GetType().Name} 0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z}, world point {target}");
        }

        private static Microsoft.Xna.Framework.Point StaticPoint(Game.GameObjects.GameObject obj)
        {
            var uv = Client.Game.UO.Arts.GetArt(obj.Graphic).UV;
            var b = Client.Game.UO.Arts.GetRealArtBounds(obj.Graphic);
            int x0 = obj.RealScreenPosition.X - ((uv.Width >> 1) - 22);
            int y0 = obj.RealScreenPosition.Y - (uv.Height - 44);

            return new Microsoft.Xna.Framework.Point(x0 + b.X + b.Width / 2, y0 + b.Y + b.Height / 2);
        }

        private static void LogPick(bool click = false)
        {
            if (_pickObject != null && _pickTarget.HasValue)
            {
                var t = _pickTarget.Value;
                SelectedObject.TranslatedMousePositionByViewport = t;
                bool hit = _pickObject.CheckMouseSelection();
                SelectedObject.TranslatedMousePositionByViewport = new Microsoft.Xna.Framework.Point(t.X, t.Y - 300);
                bool miss = _pickObject.CheckMouseSelection();
                Log.Info($"[bench-ui] pixel pick ({_pickWhat}) on the sprite: {hit}; 300 px above it: {miss}");
            }

            var o = SelectedObject.Object;
            string desc = o is Game.GameObjects.GameObject go ? $"{go.GetType().Name} 0x{go.Graphic:X4} at {go.X},{go.Y},{go.Z}" : o?.GetType().Name ?? "nothing";
            Log.Info($"[bench-ui] under the mouse ({_pickWhat}, world {SelectedObject.TranslatedMousePositionByViewport}, target {_pickTarget}): {desc}" +
                     $" | overWorld {Game.Managers.UIManager.IsMouseOverWorld}, control {Game.Managers.UIManager.MouseOverControl?.GetType().Name}, mouse {Input.Mouse.Position}");

            if (click && o is Game.GameObjects.Entity clicked)
            {
                GameActions.SingleClick(Client.Game.UO.World, clicked.Serial);
                Log.Info($"[bench-pick] clicked 0x{clicked.Serial:X8}");
            }

            _mouse = null;
            _pickTarget = null;
        }

        private static Game.GameObjects.GameObject FindStatic(World world)
        {
            var player = world.Player;

            for (int r = 1; r < 6; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (var o = world.Map.GetTile(player.X + dx, player.Y + dy); o != null; o = o.TNext)
                        {
                            if (o is Game.GameObjects.Static st && Client.Game.UO.Arts.GetArt(st.Graphic).UV.Height > 40)
                            {
                                return st;
                            }
                        }
                    }
                }
            }

            return null;
        }

        private static void ApplySize(GameController game)
        {
            if (Environment.GetEnvironmentVariable("CUO_BENCH_TOPMOST") == "1")
            {
                // an occluded window gets its presents throttled by the compositor
                SDL3.SDL.SDL_SetWindowAlwaysOnTop(game.Window.Handle, true);
                SDL3.SDL.SDL_RaiseWindow(game.Window.Handle);
            }

            if (string.IsNullOrEmpty(_size))
            {
                return;
            }

            if (_size == "max")
            {
                if (!game.IsWindowMaximized())
                {
                    game.MaximizeWindow();
                }
            }
            else
            {
                string[] parts = _size.Split('x');
                int pw = int.Parse(parts[0]), ph = int.Parse(parts[1]);

                if (game.IsWindowMaximized())
                {
                    game.RestoreWindow();
                }

                // after a restore from maximised the window is already back at its old size, but the backbuffer
                // keeps the maximised size until it is set again
                if (game.Window.ClientBounds.Width != pw || game.Window.ClientBounds.Height != ph ||
                    game.GraphicManager.PreferredBackBufferWidth != pw || game.GraphicManager.PreferredBackBufferHeight != ph)
                {
                    game.SetWindowSize(pw, ph);
                }
            }

            if (Configuration.ProfileManager.CurrentProfile is { } profile)
            {
                profile.GameWindowFullSize = true;
            }

            if (Game.Managers.UIManager.GetGump<Game.UI.Gumps.WorldViewportGump>() is { } vp)
            {
                vp.ResizeGameWindow(new Microsoft.Xna.Framework.Point(game.Window.ClientBounds.Width, game.Window.ClientBounds.Height));
                vp.SetGameWindowPosition(new Microsoft.Xna.Framework.Point(-5, -5));
            }
        }

        private static void LogSize(GameController game)
        {
            var cb = game.Window.ClientBounds;
            var cam = (game.Scene as Game.Scenes.GameScene)?.Camera.Bounds;
            Log.Info(
                $"[bench] window client {cb.Width}x{cb.Height}, backbuffer {game.GraphicManager.PreferredBackBufferWidth}x{game.GraphicManager.PreferredBackBufferHeight}, " +
                $"dpi scale {game.DpiScale:0.00}, camera {cam}, vsync {game.GraphicManager.SynchronizeWithVerticalRetrace}, fps setting {Configuration.Settings.GlobalSettings.FPS}"
            );
        }

        // Client-side effects for render checks: effect:x y z:graphic[:blend] (FixedXYZ, endless),
        // moving:x y z:x y z:graphic[:blend] (a moving effect between two points).
        private static void SpawnBenchEffect(World world, string entry)
        {
            string[] p = entry.Split(':');
            bool moving = p[0] == "moving";
            int[] a = Array.ConvertAll(p[1].Trim().Split(' '), int.Parse);
            int[] b = moving ? Array.ConvertAll(p[2].Trim().Split(' '), int.Parse) : a;
            int gi = moving ? 3 : 2;
            ushort graphic = Convert.ToUInt16(p[gi], 16);
            var blend = p.Length > gi + 1 ? (GraphicEffectBlendMode)int.Parse(p[gi + 1]) : GraphicEffectBlendMode.Normal;

            world.SpawnEffect(
                moving ? GraphicEffectType.Moving : GraphicEffectType.FixedXYZ,
                0,
                0,
                graphic,
                0,
                (ushort)a[0],
                (ushort)a[1],
                (sbyte)a[2],
                (ushort)b[0],
                (ushort)b[1],
                (sbyte)b[2],
                (byte)(moving ? 2 : 10),
                0,
                false,
                false,
                false,
                blend
            );

            Log.Info($"[bench] {entry}: spawned");
        }

        private static int _scanCols, _scanRows, _scanLeft, _scanPhase, _scanFrames, _scanSame, _scanDiff, _scanEmpty;
        private static string _scanOff;

        private static string Describe(object o)
        {
            return o is Game.GameObjects.GameObject g ? $"{g.GetType().Name} 0x{g.Graphic:X4} at {g.X},{g.Y},{g.Z}" : o?.GetType().Name ?? "nothing";
        }

        // pickscan: per grid point, 3 frames with skipping off, then skipping on until it is active plus 3 frames
        private static void UpdatePickScan(GameController game, Game.Scenes.GameScene gs)
        {
            if (gs == null)
            {
                _scanLeft = 0;

                return;
            }

            int index = _scanCols * _scanRows - _scanLeft;
            var b = gs.Camera.Bounds;
            _mouse = new Microsoft.Xna.Framework.Point(
                b.X + (int)((index % _scanCols + 0.5f) * b.Width / _scanCols),
                b.Y + (int)((index / _scanCols + 0.5f) * b.Height / _scanRows)
            );
            HoldMouse(game);
            var settings = Configuration.Settings.GlobalSettings;
            _scanFrames++;

            if (_scanPhase == 0)
            {
                settings.GpuPrepareSkipBaked = false;

                if (_scanFrames >= 3)
                {
                    _scanOff = Describe(SelectedObject.Object);
                    settings.GpuPrepareSkipBaked = true;
                    _scanPhase = 1;
                    _scanFrames = 0;
                }
            }
            else if (_scanPhase == 1)
            {
                if (!gs.IsGpuPrepareSkipping)
                {
                    _scanFrames = 0;
                }
                else if (_scanFrames >= 3)
                {
                    string on = Describe(SelectedObject.Object);

                    if (on == _scanOff)
                    {
                        _scanSame++;
                        _scanEmpty += on == "nothing" ? 1 : 0;
                    }
                    else
                    {
                        _scanDiff++;
                        Log.Info($"[bench-scan] point {index} mouse {_mouse}: skip off picks {_scanOff}, skip on picks {on}");
                    }

                    _scanPhase = 0;
                    _scanFrames = 0;

                    if (--_scanLeft == 0)
                    {
                        _mouse = null;
                        Log.Info($"[bench-scan] done: {_scanSame} same ({_scanEmpty} of them nothing), {_scanDiff} different");
                    }
                }
            }
        }

        // name:x y:zoom measures a fixed spot; name:travel teleports between towns every 2 s; name:walk paths back and forth near Britain.
        private static void UpdatePlan(GameController game, long now)
        {
            var world = game.UO.World;

            if (!(world?.InGame ?? false))
            {
                return;
            }

            long sec = Stopwatch.Frequency;
            var gs = game.Scene as Game.Scenes.GameScene;

            if (_flipLeft > 0)
            {
                if (_flipWalk && !world.Player.Pathfinder.AutoWalking && now >= _travelAt)
                {
                    _walkBack = !_walkBack;
                    world.Player.Pathfinder.WalkTo(_walkBack ? 1475 : 1495, _walkBack ? 1650 : 1629, world.Player.Z, 0);
                    _travelAt = now + sec / 2;
                }

                if (++_flipFrame >= _flipEvery)
                {
                    _flipFrame = 0;
                    _flipLeft--;
                    _flipTotal++;
                    var gset = Configuration.Settings.GlobalSettings;
                    RendererStartup.TrySwitch(_flipLeft == 0 || !gset.UseGpuWorldRenderer ? "gpu" : "legacy", out _);

                    if (_flipLeft == 0)
                    {
                        world.Player.Pathfinder.StopAutoWalk();
                        Log.Info($"[bench] flip done: {_flipTotal} world renderer toggles so far, now {gset.WorldRenderer} (gpu available: {game.GetGpuWorld() != null})");
                        _stepAt = now + sec * 2;
                    }
                }

                return;
            }

            if (_scanLeft > 0)
            {
                UpdatePickScan(game, gs);

                return;
            }

            if (_step == 4)
            {
                FixTeleportZ(world);
                _planFrames++;
                string mode = _planMode;

                if (mode == "travel" && now >= _travelAt)
                {
                    var (tx, ty) = _travel[_travelIndex++ % _travel.Length];
                    GameActions.Say($"[go {tx} {ty}");
                    _travelAt = now + sec * 2;
                }
                else if (mode == "ltravel" && now >= _travelAt)
                {
                    // client-only teleports between towns (no staff account needed): the server keeps the real position
                    var (tx, ty) = _travel[_travelIndex++ % _travel.Length];
                    LocalTeleport(world, gs, tx, ty);
                    StartTeleportShots();
                    _travelAt = now + sec * 2;
                }
                else if ((mode == "reinit" || mode == "reinitrt" || mode == "lsreinit") && now >= _travelAt)
                {
                    if (_planFrames > 1)
                    {
                        Log.Info($"[bench] {mode}: dropping the GPU world pass{(mode == "reinitrt" ? " and the world render target" : "")}");
                        game.DebugRecreateGpuWorld(mode == "reinitrt");
                        StartTeleportShots();
                    }

                    _travelAt = now + sec * 2;
                }
                else if (mode == "glide" && now >= _travelAt)
                {
                    // client-only fast travel: one tile east every 40 ms (25 tiles/s) from Britain, a new chunk column every 8 tiles
                    if (_glideX == 0)
                    {
                        _glideX = 1300;
                    }

                    LocalTeleport(world, gs, ++_glideX, 1640);
                    _travelAt = now + sec / 25;
                }
                else if (mode == "bodies" && now >= _travelAt)
                {
                    // first sight of many bodies: swap the player's body (local only) to a body not drawn yet, every 50 ms
                    var anims = game.UO.FileManager.Animations;

                    if (_bodyRestore == 0)
                    {
                        _bodyRestore = world.Player.Graphic;
                    }

                    while (++_bodyNext < anims.BodyCount)
                    {
                        ref readonly var body = ref anims.GetBody((ushort)_bodyNext);

                        if (body.ActionCount > 0 && (int)body.Type != (int)Assets.AnimationGroupsType.Equipment && anims.TryGetDirection((ushort)_bodyNext, 0, 4, out _))
                        {
                            world.Player.Graphic = (ushort)_bodyNext;
                            world.Ecs.Sync(world.Player); // a local body swap: the ECS mirror follows, as for a server body change
                            _bodiesShown++;

                            break;
                        }
                    }

                    _travelAt = now + sec / 20;
                }
                else if (mode == "lswalk" && !world.Player.Pathfinder.AutoWalking && now >= _travelAt)
                {
                    // the large-scale scene's lane (tools/large-scale/make_scene.py): end to end through 2,000 mobiles
                    _walkBack = !_walkBack;
                    world.Player.Pathfinder.WalkTo(_walkBack ? 5141 : 5171, 2150, world.Player.Z, 0);
                    _travelAt = now + sec / 2;
                }
                else if (mode == "walk" && !world.Player.Pathfinder.AutoWalking && now >= _travelAt)
                {
                    _walkBack = !_walkBack;
                    world.Player.Pathfinder.WalkTo(_walkBack ? 1475 : 1495, _walkBack ? 1650 : 1629, world.Player.Z, 0);
                    _travelAt = now + sec / 2;
                }
                else if (mode == "bwalk" && !world.Player.Pathfinder.AutoWalking && now >= _travelAt)
                {
                    // right after the teleport: walk north-south across a region edge (y 1536), two chunk edges and a
                    // 40 -> 30 slope, logging where each leg ends against the map's land z
                    if (_bwalkDenies < 0)
                    {
                        _bwalkDenies = world.Player.Walker.ServerDenies;
                    }
                    else
                    {
                        sbyte land = world.Map.GetTileZ(world.Player.X, world.Player.Y);
                        Log.Info($"[bench] bwalk leg {_bwalkLegs} ended at {world.Player.X},{world.Player.Y},{world.Player.Z} (land z {land}), server denies {world.Player.Walker.ServerDenies - _bwalkDenies}");
                    }

                    _bwalkLegs++;
                    world.Player.Pathfinder.WalkTo(1495, _bwalkLegs % 2 == 1 ? 1542 : 1528, world.Player.Z, 0);
                    _travelAt = now + sec / 4;
                }

                TeleportShot(game);

                if (FrameStats ? ClassicUO.FrameStats.WindowsReported - _windowsAtStart >= _planWindows : now >= _stepAt)
                {
                    ClassicUO.FrameStats.StopWindow();
                    world.Player.Pathfinder.StopAutoWalk();

                    if (_bodyRestore != 0)
                    {
                        world.Player.Graphic = _bodyRestore;
                        world.Ecs.Sync(world.Player);
                        _bodyRestore = 0;
                        Log.Info($"[bench] bodies: {_bodiesShown} bodies shown, up to body {_bodyNext}");
                    }

                    Log.Info($"[bench] {_plan[_planIndex]} done at {world.Player.X},{world.Player.Y},{world.Player.Z}, mobiles {world.Mobiles.Count}, items {world.Items.Count}, " +
                             $"{_planFrames * (double)sec / (now - _planStart):0} fps (update count), {(Renderer.UltimaBatcher2D.EffectApplies - _appliesAtStart) / (double)Math.Max(1, _planFrames):0.0} effect applies/frame");
                    LogSettle(gs, _plan[_planIndex]);
                    _planIndex++;
                    _step = 1;
                    _stepAt = now + sec;
                }

                return;
            }

            if (now < _stepAt)
            {
                return;
            }

            switch (_step)
            {
                case 0:
                    ApplySize(game);
                    _step = 1;
                    _stepAt = now + sec * 5;

                    break;

                case 1:
                    if (_planIndex >= _plan.Length)
                    {
                        LogSettle(gs, "plan done");
                        Log.Info("[bench] plan done");
                        _step = 99;

                        if (_exit)
                        {
                            game.Exit();
                        }

                        return;
                    }

                    if (_plan[_planIndex].StartsWith("vsync:"))
                    {
                        game.SetVSync(_plan[_planIndex] == "vsync:on");
                        Log.Info($"[bench] vsync {game.GraphicManager.SynchronizeWithVerticalRetrace}, present interval {game.GraphicsDevice.PresentationParameters.PresentationInterval}");
                        _planIndex++;
                        _stepAt = now + sec * 2;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("light:") && Configuration.ProfileManager.CurrentProfile is { } lightProfile)
                    {
                        string level = _plan[_planIndex].Substring(6);
                        lightProfile.UseCustomLightLevel = level != "off";

                        if (lightProfile.UseCustomLightLevel)
                        {
                            lightProfile.LightLevel = (byte)Convert.ToInt32(level, 16);
                            lightProfile.LightLevelType = 0;
                            world.Light.Overall = lightProfile.LightLevel;
                            world.Light.Personal = 0;
                        }

                        Log.Info($"[bench] light {level}");
                        _planIndex++;
                        _stepAt = now + sec * 2;

                        break;
                    }

                    if (_plan[_planIndex] == "gumpok")
                    {
                        // answers OK (button 1) on the frontmost server gump, e.g. a staff command's confirmation
                        foreach (var g in Game.Managers.UIManager.Gumps)
                        {
                            if (g.ServerSerial != 0 && g.GetType() == typeof(Game.UI.Gumps.Gump))
                            {
                                Log.Info($"[bench] gump 0x{g.ServerSerial:X8}: OK");
                                g.OnButtonClick(1);

                                break;
                            }
                        }

                        _planIndex++;
                        _stepAt = now + sec * 3;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("size:"))
                    {
                        _size = _plan[_planIndex].Substring(5).Trim();
                        ApplySize(game);
                        Log.Info($"[bench] size {_size}");
                        _planIndex++;
                        _stepAt = now + sec * 3;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("flip:"))
                    {
                        // flip:N[:frames[:walk]] toggles the world renderer N times, every `frames` frames (default 1), ending on gpu
                        string[] f = _plan[_planIndex].Split(':');
                        _flipLeft = Math.Max(1, int.Parse(f[1]));
                        _flipEvery = f.Length > 2 ? Math.Max(1, int.Parse(f[2])) : 1;
                        _flipWalk = f.Length > 3 && f[3] == "walk";
                        _flipFrame = 0;
                        Log.Info($"[bench] flip {_flipLeft} toggles every {_flipEvery} frame(s){(_flipWalk ? ", walking" : "")}");
                        _planIndex++;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("prepdetail:"))
                    {
                        ClassicUO.FrameStats.PrepDetail = ClassicUO.FrameStats.On && _plan[_planIndex] == "prepdetail:on";
                        Log.Info($"[bench] world prepare detail timing {ClassicUO.FrameStats.PrepDetail}");
                        _planIndex++;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("pickscan:") && gs != null)
                    {
                        // pickscan:CxR hovers a C x R grid over the game view; at each point the selection with the Gpu
                        // prepare's skipping off and then on must be the same object
                        string[] g = _plan[_planIndex].Substring(9).Split('x');
                        _scanCols = int.Parse(g[0]);
                        _scanRows = int.Parse(g[1]);
                        _scanLeft = _scanCols * _scanRows;
                        _scanPhase = 0;
                        _scanFrames = 0;
                        _scanSame = _scanDiff = _scanEmpty = 0;
                        Log.Info($"[bench-scan] {_plan[_planIndex]} at {world.Player.X},{world.Player.Y},{world.Player.Z} zoom {gs.Camera.Zoom:0.0}");
                        _planIndex++;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("cot:") && Configuration.ProfileManager.CurrentProfile is { } cotProfile)
                    {
                        cotProfile.UseCircleOfTransparency = _plan[_planIndex] == "cot:on";
                        Log.Info($"[bench] circle of transparency {cotProfile.UseCircleOfTransparency} (type {cotProfile.CircleOfTransparencyType}, radius {cotProfile.CircleOfTransparencyRadius})");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("ecssprites:"))
                    {
                        Configuration.Settings.GlobalSettings.GpuEcsSprites = _plan[_planIndex] == "ecssprites:on";
                        Log.Info($"[bench] gpu world mobiles from the ECS sprite extract: {Configuration.Settings.GlobalSettings.GpuEcsSprites}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("itemcache:"))
                    {
                        Configuration.Settings.GlobalSettings.GpuPrepareItemCache = _plan[_planIndex] == "itemcache:on";
                        Log.Info($"[bench] gpu prepare caches ground items: {Configuration.Settings.GlobalSettings.GpuPrepareItemCache}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("w5:"))
                    {
                        var w5s = Configuration.Settings.GlobalSettings;
                        w5s.WorldUpdateActiveItems = w5s.GpuMobileFrameCache = w5s.TextSkipExpired = _plan[_planIndex] == "w5:on";
                        Log.Info($"[bench] W5 update shortcuts: {w5s.WorldUpdateActiveItems}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("textskip:"))
                    {
                        Configuration.Settings.GlobalSettings.TextSkipExpired = _plan[_planIndex] == "textskip:on";
                        Log.Info($"[bench] overhead text pass skips expired texts: {Configuration.Settings.GlobalSettings.TextSkipExpired}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("mobcache:"))
                    {
                        Configuration.Settings.GlobalSettings.GpuMobileFrameCache = _plan[_planIndex] == "mobcache:on";
                        Log.Info($"[bench] gpu mobile frame cache: {Configuration.Settings.GlobalSettings.GpuMobileFrameCache}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("activeitems:"))
                    {
                        Configuration.Settings.GlobalSettings.WorldUpdateActiveItems = _plan[_planIndex] == "activeitems:on";
                        Log.Info($"[bench] world update visits active items only: {Configuration.Settings.GlobalSettings.WorldUpdateActiveItems}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("gpuskip:"))
                    {
                        Configuration.Settings.GlobalSettings.GpuPrepareSkipBaked = _plan[_planIndex] == "gpuskip:on";
                        Log.Info($"[bench] gpu prepare skips baked land and statics: {Configuration.Settings.GlobalSettings.GpuPrepareSkipBaked}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("world:"))
                    {
                        LogSettle(gs, _plan[_planIndex]);
                        RendererStartup.TrySwitch(_plan[_planIndex].Substring(6), out _);
                        Log.Info($"[bench] world renderer {Configuration.Settings.GlobalSettings.WorldRenderer} (gpu available: {game.GetGpuWorld() != null})");
                        _planIndex++;
                        _stepAt = now + sec * 2;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("itemhue:") || _plan[_planIndex].StartsWith("ghost:"))
                    {
                        // local-only hue checks: the nearest ground items get these hues (comma-separated);
                        // ghost:on|off swaps the player's body for a ghost (the death screen's gray world)
                        string arg = _plan[_planIndex].Substring(_plan[_planIndex].IndexOf(':') + 1);

                        if (_plan[_planIndex].StartsWith("ghost:"))
                        {
                            if (arg == "on" && !world.Player.IsDead)
                            {
                                _ghostRestore = world.Player.Graphic;
                                world.Player.Graphic = 0x0192;
                                world.Ecs.Sync(world.Player);
                            }
                            else if (arg == "off" && _ghostRestore != 0)
                            {
                                world.Player.Graphic = _ghostRestore;
                                world.Ecs.Sync(world.Player);
                            }
                        }
                        else
                        {
                            string[] hues = arg.Split(',');
                            var items = new System.Collections.Generic.List<Game.GameObjects.Item>();

                            foreach (var item in world.Items.Values)
                            {
                                if (item.OnGround && !item.IsMulti && item.Distance < 6)
                                {
                                    items.Add(item);
                                }
                            }

                            items.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Serial.CompareTo(b.Serial));

                            for (int i = 0; i < hues.Length && i < items.Count; i++)
                            {
                                items[i].Hue = Convert.ToUInt16(hues[i], 16);
                                Log.Info($"[bench] item 0x{items[i].Graphic:X4} at {items[i].X},{items[i].Y},{items[i].Z} hue 0x{items[i].Hue:X4}");
                            }
                        }

                        Log.Info($"[bench] {_plan[_planIndex]}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex] == "gumps")
                    {
                        if (!_gumpsOpened)
                        {
                            // the paperdoll and backpack come from the server; place them all on the next pass
                            GameActions.OpenPaperdoll(world, world.Player.Serial);
                            GameActions.OpenBackpack(world);
                            GameActions.OpenJournal(world);
                            GameActions.OpenStatusBar(world);
                            _gumpsOpened = true;
                            _stepAt = now + sec * 3;

                            break;
                        }

                        foreach (var g in Game.Managers.UIManager.Gumps)
                        {
                            switch (g)
                            {
                                case Game.UI.Gumps.PaperDollGump: g.X = 20; g.Y = 150; break;
                                case Game.UI.Gumps.ContainerGump: g.X = 300; g.Y = 150; break;
                                case Game.UI.Gumps.StatusGumpBase: g.X = 520; g.Y = 40; break;
                                case Game.UI.Gumps.JournalGump: g.X = 500; g.Y = 260; break;
                            }
                        }

                        Log.Info("[bench] gumps: paperdoll, backpack, journal, status");
                        _planIndex++;
                        _stepAt = now + sec * 2;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("season:"))
                    {
                        var season = Enum.Parse<Game.Managers.Season>(_plan[_planIndex].Substring(7), true);
                        world.ChangeSeason(season, 0);
                        Log.Info($"[bench] season {season}");
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("shot:"))
                    {
                        if (!string.IsNullOrEmpty(_shots))
                        {
                            string renderer = game.GetGpuWorld() != null ? "gpu" : "legacy";
                            game.RequestWorldShot(System.IO.Path.Combine(_shots, $"{_plan[_planIndex].Substring(5)}_{renderer}.png"));
                        }

                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("effect:") || _plan[_planIndex].StartsWith("moving:"))
                    {
                        SpawnBenchEffect(world, _plan[_planIndex]);
                        _planIndex++;
                        _stepAt = now + sec;

                        break;
                    }

                    if (_plan[_planIndex].StartsWith("say:"))
                    {
                        GameActions.Say(_plan[_planIndex].Substring(4));
                        Log.Info($"[bench] said {_plan[_planIndex].Substring(4)}");
                        _planIndex++;
                        _stepAt = now + sec * 4;

                        break;
                    }

                    ApplySize(game);

                    string[] p = _plan[_planIndex].Split(':');
                    string mode = p[1].Trim();
                    float zoom = p.Length > 2 ? float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;

                    if (gs != null)
                    {
                        gs.Camera.Zoom = zoom;
                    }

                    GameActions.Say(mode == "bwalk" ? "[go 1495 1528" : mode == "lswalk" || mode == "lsreinit" ? "[go 5156 2150" : mode == "travel" || mode == "walk" || mode == "bodies" || mode == "ltravel" || mode == "glide" || mode == "reinit" || mode == "reinitrt" ? "[go 1495 1629" : $"[go {mode}");
                    _step = 2;
                    _stepAt = now + sec * 8;

                    break;

                case 2:
                    LogSize(game);

                    if (!_noShot)
                    {
                        game.TakeScreenshot();
                    }

                    if (!string.IsNullOrEmpty(_shots))
                    {
                        string renderer = game.GetGpuWorld() != null ? "gpu" : "legacy";
                        game.RequestWorldShot(System.IO.Path.Combine(_shots, $"{_plan[_planIndex].Split(':')[0].Trim()}_{renderer}.png"));
                    }

                    Log.Info($"[bench] {_plan[_planIndex]} at {world.Player.X},{world.Player.Y},{world.Player.Z} zoom {gs?.Camera.Zoom:0.0}, mobiles {world.Mobiles.Count}, items {world.Items.Count}; measuring");
                    _windowsAtStart = ClassicUO.FrameStats.WindowsReported;
                    _travelAt = now;
                    _planStart = now;
                    _planFrames = 0;
                    _appliesAtStart = Renderer.UltimaBatcher2D.EffectApplies;
                    _planMode = _plan[_planIndex].Split(':')[1].Trim(); // read every measured frame: split once
                    _stepAt = now + sec * 5 * _planWindows;

                    if (FrameStats)
                    {
                        ClassicUO.FrameStats.StartWindow(_plan[_planIndex].Split(':')[0].Trim());
                    }

                    _step = 4;

                    break;
            }
        }
    }
}
#else
namespace ClassicUO
{
    // CLEANUP(release): the release build's DevBench: never enabled, so FrameStats and every bench hook stay off.
    internal static class DevBench
    {
        public static readonly bool FrameStats = false;

        public static bool Enabled => false;

        public static void Update(GameController game)
        {
        }
    }
}
#endif
