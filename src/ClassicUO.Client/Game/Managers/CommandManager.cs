// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Input;
using ClassicUO.Resources;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    internal sealed class CommandManager
    {
        private readonly Dictionary<string, Action<string[]>> _commands = new Dictionary<string, Action<string[]>>();
        private readonly World _world;

        public CommandManager(World world)
        {
            _world = world;
        }

        public void Initialize()
        {
            Register
            (
                "info",
                s =>
                {
                    if (_world.TargetManager.IsTargeting)
                    {
                        _world.TargetManager.CancelTarget();
                    }

                    _world.TargetManager.SetTargeting(CursorTarget.SetTargetClientSide, CursorType.Target, TargetType.Neutral);
                }
            );

            Register
            (
                "datetime",
                s =>
                {
                    if (_world.Player != null)
                    {
                        GameActions.Print(_world, string.Format(ResGeneral.CurrentDateTimeNowIs0, DateTime.Now));
                    }
                }
            );

            Register
            (
                "hue",
                s =>
                {
                    if (_world.TargetManager.IsTargeting)
                    {
                        _world.TargetManager.CancelTarget();
                    }

                    _world.TargetManager.SetTargeting(CursorTarget.HueCommandTarget, CursorType.Target, TargetType.Neutral);
                }
            );


#if CUO_DEVTOOLS
            // CLEANUP(release): -worldrenderer [gpu|legacy] switches the world renderer mid-session (no argument toggles), for
            // parity shots, picking and A/B FPS. Not saved; players get the startup choice (world_renderer, RendererStartup).
            Register
            (
                "worldrenderer",
                s =>
                {
                    var settings = Configuration.Settings.GlobalSettings;
                    string target = s.Length > 1 ? s[1] : settings.UseGpuWorldRenderer ? "legacy" : "gpu";

                    GameActions.Print(_world, RendererStartup.TrySwitch(target, out string message)
                        ? $"World renderer: {settings.WorldRenderer} (developer switch, this session only)"
                        : $"World renderer not switched: {message}");
                }
            );
#endif

            // -gpuskip [on|off]: the Gpu world prepare skips baked land and statics (no argument toggles)
            Register
            (
                "gpuskip",
                s =>
                {
                    var settings = Configuration.Settings.GlobalSettings;
                    settings.GpuPrepareSkipBaked = s.Length > 1 ? s[1].ToLowerInvariant() == "on" : !settings.GpuPrepareSkipBaked;
                    GameActions.Print(_world, $"Gpu world prepare skips baked land and statics: {(settings.GpuPrepareSkipBaked ? "on" : "off")}");
                }
            );

            // -uicache [on|off]: the UI render cache (B8). No argument toggles.
            Register
            (
                "uicache",
                s =>
                {
                    var settings = Configuration.Settings.GlobalSettings;
                    settings.UiRenderCache = s.Length > 1 ? s[1].ToLowerInvariant() == "on" : !settings.UiRenderCache;
                    GameActions.Print(_world, $"UI render cache: {(settings.UiRenderCache ? "on" : "off")}");
                }
            );

            // -itemcache [on|off]: the Gpu world prepare caches settled ground items' sprites per chunk (no argument toggles)
            Register
            (
                "itemcache",
                s =>
                {
                    var settings = Configuration.Settings.GlobalSettings;
                    settings.GpuPrepareItemCache = s.Length > 1 ? s[1].ToLowerInvariant() == "on" : !settings.GpuPrepareItemCache;
                    GameActions.Print(_world, $"Gpu world prepare caches ground items: {(settings.GpuPrepareItemCache ? "on" : "off")}");
                }
            );

            Register
            (
                "debug",
                s =>
                {
                    CUOEnviroment.Debug = !CUOEnviroment.Debug;

                }
            );
        }


        public void Register(string name, Action<string[]> callback)
        {
            name = name.ToLower();

            if (!_commands.ContainsKey(name))
            {
                _commands.Add(name, callback);
            }
            else
            {
                Log.Error($"Attempted to register command: '{name}' twice.");
            }
        }

        public void UnRegister(string name)
        {
            name = name.ToLower();

            if (_commands.ContainsKey(name))
            {
                _commands.Remove(name);
            }
        }

        public void UnRegisterAll()
        {
            _commands.Clear();
        }

        public void Execute(string name, params string[] args)
        {
            name = name.ToLower();

            if (_commands.TryGetValue(name, out Action<string[]> action))
            {
                action.Invoke(args);
            }
            else
            {
                Log.Warn($"Command: '{name}' not exists");
            }
        }

        public void OnHueTarget(Entity entity)
        {
            if (entity != null)
            {
                _world.TargetManager.Target(entity);
                Mouse.LastLeftButtonClickTime = 0;
                GameActions.Print(_world, string.Format(ResGeneral.ItemID0Hue1, entity.Graphic, entity.Hue));
            }
            else
            {
                Mouse.LastLeftButtonClickTime = 0;
            }
        }
    }
}