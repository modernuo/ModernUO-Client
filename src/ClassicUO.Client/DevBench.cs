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
    //   CUO_BENCH_PLAN="name:x y:zoom;name:travel;name:walk;say:text;vsync:on|off"  measure each entry for CUO_BENCH_WINDOWS (default 3) windows, then quit
    //   CUO_BENCH_SIZE=1920x1080  resize the window client area to this many pixels with the game viewport filling it
    //   CUO_BENCH_EXIT=1    walk a few steps, take screenshots and quit when done
    //   CUO_BENCH_SPOTS="x y;x y"  screenshot each spot at zoom 1 and full light, then quit
    //   CUO_BENCH_UI=1      at Britain: paperdoll, backpack, minimap, picking, multi preview, night lights, world map
    internal static class DevBench
    {
        public static readonly bool FrameStats = Environment.GetEnvironmentVariable("CUO_FRAMESTATS") == "1";
        private static readonly string _go = Environment.GetEnvironmentVariable("CUO_BENCH_GO");
        private static readonly bool _exit = Environment.GetEnvironmentVariable("CUO_BENCH_EXIT") == "1";
        private static readonly string[] _spots = Environment.GetEnvironmentVariable("CUO_BENCH_SPOTS")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        private static readonly bool _ui = Environment.GetEnvironmentVariable("CUO_BENCH_UI") == "1";
        private static readonly string[] _plan = Environment.GetEnvironmentVariable("CUO_BENCH_PLAN")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        private static readonly string _size = Environment.GetEnvironmentVariable("CUO_BENCH_SIZE");
        private static readonly int _planWindows = int.TryParse(Environment.GetEnvironmentVariable("CUO_BENCH_WINDOWS"), out int w) ? w : 3;
        private static readonly (int X, int Y)[] _travel =
        {
            (1823, 2821), (2899, 676), (2498, 392), (542, 985), (596, 2138), (4442, 1172), (1495, 1629)
        };
        private static int _planIndex, _travelIndex, _windowsAtStart;
        private static long _travelAt;
        private static bool _walkBack;
        private static int _planFrames;
        private static long _planStart;
        private static int _spot;
        private static Microsoft.Xna.Framework.Point? _mouse;
        private static string _pickWhat;
        private static Game.GameObjects.GameObject _pickObject;
        private static Microsoft.Xna.Framework.Point? _pickTarget;

        private const int WINDOWS = 6;
        private static int _step;
        private static long _stepAt;

        public static bool Enabled => FrameStats || _go != null || _spots != null || _ui || _plan != null;

        public static void Update(GameController game)
        {
            long now = Stopwatch.GetTimestamp();

            if (Configuration.ProfileManager.CurrentProfile is { } profile)
            {
                profile.ReduceFPSWhenInactive = false;

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

        private static void LogPick()
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
            if (string.IsNullOrEmpty(_size))
            {
                return;
            }

            string[] parts = _size.Split('x');
            int pw = int.Parse(parts[0]), ph = int.Parse(parts[1]);

            if (game.IsWindowMaximized())
            {
                game.RestoreWindow();
            }

            if (game.Window.ClientBounds.Width != pw || game.Window.ClientBounds.Height != ph)
            {
                game.SetWindowSize(pw, ph);
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

            if (_step == 4)
            {
                _planFrames++;
                string mode = _plan[_planIndex].Split(':')[1].Trim();

                if (mode == "travel" && now >= _travelAt)
                {
                    var (tx, ty) = _travel[_travelIndex++ % _travel.Length];
                    GameActions.Say($"[go {tx} {ty}");
                    _travelAt = now + sec * 2;
                }
                else if (mode == "walk" && !world.Player.Pathfinder.AutoWalking && now >= _travelAt)
                {
                    _walkBack = !_walkBack;
                    world.Player.Pathfinder.WalkTo(_walkBack ? 1475 : 1495, _walkBack ? 1650 : 1629, world.Player.Z, 0);
                    _travelAt = now + sec / 2;
                }

                if (FrameStats ? ClassicUO.FrameStats.WindowsReported - _windowsAtStart >= _planWindows : now >= _stepAt)
                {
                    ClassicUO.FrameStats.StopWindow();
                    world.Player.Pathfinder.StopAutoWalk();
                    Log.Info($"[bench] {_plan[_planIndex]} done at {world.Player.X},{world.Player.Y},{world.Player.Z}, mobiles {world.Mobiles.Count}, items {world.Items.Count}, " +
                             $"{_planFrames * (double)sec / (now - _planStart):0} fps (update count)");
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

                    GameActions.Say(mode == "travel" || mode == "walk" ? "[go 1495 1629" : $"[go {mode}");
                    _step = 2;
                    _stepAt = now + sec * 8;

                    break;

                case 2:
                    LogSize(game);
                    game.TakeScreenshot();
                    Log.Info($"[bench] {_plan[_planIndex]} at {world.Player.X},{world.Player.Y},{world.Player.Z} zoom {gs?.Camera.Zoom:0.0}, mobiles {world.Mobiles.Count}, items {world.Items.Count}; measuring");
                    _windowsAtStart = ClassicUO.FrameStats.WindowsReported;
                    _travelAt = now;
                    _planStart = now;
                    _planFrames = 0;
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
