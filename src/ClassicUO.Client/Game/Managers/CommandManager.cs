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


            // -worldrenderer [gpu|legacy]: developer switch of the world renderer for this run (no argument toggles). Legacy is
            // a comparison mode (parity shots, picking, A/B FPS); it isn't saved.
            Register
            (
                "worldrenderer",
                s =>
                {
                    var settings = Configuration.Settings.GlobalSettings;
                    settings.WorldRenderer = s.Length > 1 ? s[1].ToLowerInvariant() : settings.UseGpuWorldRenderer ? "legacy" : "gpu";
                    GameActions.Print(_world, $"World renderer: {settings.WorldRenderer}{(settings.UseGpuWorldRenderer ? "" : " (developer comparison mode, not saved)")}");
                }
            );

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

            // -ecssprites [on|off]: the Gpu world draws mobiles from the ECS sprite extract instead of capturing their views
            Register
            (
                "ecssprites",
                s =>
                {
                    var settings = Configuration.Settings.GlobalSettings;
                    settings.GpuEcsSprites = s.Length > 1 ? s[1].ToLowerInvariant() == "on" : !settings.GpuEcsSprites;
                    GameActions.Print(_world, $"Gpu world mobiles from the ECS sprite extract: {(settings.GpuEcsSprites ? "on" : "off")}");
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