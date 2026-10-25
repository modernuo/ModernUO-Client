// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    // Debug-only (-ecsdriver <file>): plays a line-based script in game so the ECS mirror can be exercised unattended.
    // Commands: wait <s> | say <text> | walk <dir 0-7> <steps> [run] | backpack | pick worn|backpack|held|near <hex graphic>
    //           | drop backpack|ground|equip | check | logout | quit | log <text>
    //           | target self|ground <dx> <dy>|worn <layer> | parity
    internal sealed class EcsSessionDriver
    {
        private DriverGump _gump;

        private readonly string[] _lines;
        private int _pc;
        private uint _waitUntil;
        private int _walkLeft;
        private Direction _walkDir;
        private bool _walkRun;
        private uint _walkGiveUp;
        private uint _held;
        private uint _logoutAt;

        private EcsSessionDriver(string[] lines)
        {
            _lines = lines;
        }

        public static EcsSessionDriver Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            Log.Info($"[ECS] driver script: {path}");

            return new EcsSessionDriver(File.ReadAllLines(path));
        }

        // Ticks from a hidden gump: UIManager updates after the scene, so the script may swap scenes (logout).
        public void EnsureAttached(World world)
        {
            if (ProfileManager.CurrentProfile != null)
            {
                ProfileManager.CurrentProfile.ReduceFPSWhenInactive = false;
            }

            if (_gump == null || _gump.IsDisposed)
            {
                UIManager.Add(_gump = new DriverGump(world, this));
            }
        }

        private void Update(World world)
        {
            PlayerMobile player = world.Player;

            if (_logoutAt != 0)
            {
                if (Time.Ticks >= _logoutAt)
                {
                    _logoutAt = 0;
                    world.Ecs.RunCheck(true);
                    Client.Game.SetScene(new LoginScene(world));
                    Log.Info($"[ECS] driver: logged out, World items={world.Items.Count} mobiles={world.Mobiles.Count}, ECS entities={world.Ecs.Mirror.Count}");
                    Client.Game.Exit();
                }

                return;
            }

            if (player == null || Time.Ticks < _waitUntil)
            {
                return;
            }

            if (_walkLeft > 0)
            {
                if (player.Walk(_walkDir, _walkRun))
                {
                    _walkLeft--;
                    _walkGiveUp = Time.Ticks + 3000;
                }
                else if (Time.Ticks > _walkGiveUp)
                {
                    Log.Warn($"[ECS] driver: walk blocked, {_walkLeft} steps skipped");
                    _walkLeft = 0;
                }

                return;
            }

            while (_pc < _lines.Length)
            {
                string line = _lines[_pc++].Trim();

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                Log.Info($"[ECS] driver> {line}");

                if (Execute(world, player, line))
                {
                    return;
                }
            }
        }

        // returns true when the script should yield for at least a frame
        private bool Execute(World world, PlayerMobile player, string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line : line.Substring(0, sp);
            string arg = sp < 0 ? string.Empty : line.Substring(sp + 1).Trim();
            string[] args = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Item backpack = player.FindItemByLayer(Layer.Backpack);

            switch (cmd)
            {
                case "wait":
                    _waitUntil = Time.Ticks + (uint)(double.Parse(arg) * 1000);

                    return true;

                case "say":
                    GameActions.Say(arg);

                    return true;

                case "walk":
                    _walkDir = (Direction)int.Parse(args[0]);
                    _walkLeft = int.Parse(args[1]);
                    _walkRun = args.Length > 2 && args[2] == "run";
                    _walkGiveUp = Time.Ticks + 3000;

                    return true;

                case "backpack":
                    GameActions.OpenBackpack(world);

                    return true;

                case "pick":
                    uint serial = args[0] switch
                    {
                        "worn" => FindWorn(player),
                        "backpack" => FindInBackpack(backpack),
                        "near" => FindNear(world, player, Convert.ToUInt16(args[1], 16)),
                        _ => _held
                    };

                    // an explicit amount skips the split-stack gump
                    if (serial != 0 && GameActions.PickUp(world, serial, 0, 0, Math.Max(1, (int)(world.Items.Get(serial)?.Amount ?? 1))))
                    {
                        _held = serial;
                        Log.Info($"[ECS] driver: picked up 0x{serial:X8}");
                    }
                    else
                    {
                        Item it = world.Items.Get(serial);
                        Log.Warn($"[ECS] driver: can't pick ({arg}) 0x{serial:X8}: exists={it != null} onGround={it?.OnGround} dist={it?.Distance} holding={Client.Game.UO.GameCursor.ItemHold.Enabled}");
                    }

                    return true;

                case "drop":
                    switch (arg)
                    {
                        case "backpack" when backpack != null:
                            GameActions.DropItem(_held, 0xFFFF, 0xFFFF, 0, backpack.Serial);

                            break;

                        case "ground":
                            GameActions.DropItem(_held, player.X + 1, player.Y, player.Z, 0xFFFF_FFFF);

                            break;

                        case "equip":
                            GameActions.Equip(world);

                            break;
                    }

                    return true;

                case "check":
                    world.Ecs.RunCheck(true);

                    return false;

                // answers a pending target cursor (e.g. after a staff command): self | ground <dx> <dy> | worn <layer>
                case "target":
                    if (!world.TargetManager.IsTargeting)
                    {
                        Log.Warn("[ECS] driver: no target cursor");

                        return true;
                    }

                    switch (args[0])
                    {
                        case "self":
                            world.TargetManager.Target(player.Serial);

                            break;

                        case "ground":
                            world.TargetManager.Target(0, (ushort)(player.X + int.Parse(args[1])), (ushort)(player.Y + int.Parse(args[2])), player.Z);

                            break;

                        case "worn":
                            Item worn = player.FindItemByLayer((Layer)int.Parse(args[1]));

                            if (worn != null)
                            {
                                world.TargetManager.Target(worn.Serial);
                            }
                            else
                            {
                                world.TargetManager.CancelTarget();
                            }

                            break;

                        // item <hex serial>: a known item, e.g. one in the backpack (cleanup with [remove)
                        case "item" when args.Length > 1 && world.Items.Get(Convert.ToUInt32(args[1], 16)) is { } known:
                            world.TargetManager.Target(known.Serial);

                            break;
                    }

                    return true;

                case "parity":
                    EcsParity.Summary();

                    return false;

                case "log":
                    return false;

                // light <hex level>|off: the profile's custom light level, as DevBench's light: entry (night = lights drawn)
                case "light" when args.Length > 0 && ProfileManager.CurrentProfile is { } lightProfile:
                    lightProfile.UseCustomLightLevel = args[0] != "off";

                    if (lightProfile.UseCustomLightLevel)
                    {
                        lightProfile.LightLevel = (byte)Convert.ToInt32(args[0], 16);
                        lightProfile.LightLevelType = 0;
                        world.Light.Overall = lightProfile.LightLevel;
                        world.Light.Personal = 0;
                    }

                    return false;

                // equip: logs the player's worn items (to check that a scenario cleaned up after itself)
                case "equip":
                    for (Layer l = Layer.OneHanded; l <= Layer.Bank; l++)
                    {
                        if (player.FindItemByLayer(l) is { } worn)
                        {
                            Log.Info($"[ECS] worn {l} ({(int)l}): 0x{worn.Graphic:X4} hue 0x{worn.Hue:X4} serial 0x{worn.Serial:X8} {worn.Name}");
                        }
                    }

                    return false;

                // items: logs the ground items within 8 tiles and the backpack's known contents (cleanup checks)
                case "items":
                    foreach (Item it in world.Items.Values)
                    {
                        bool ground = it.OnGround && Math.Abs(it.X - player.X) <= 8 && Math.Abs(it.Y - player.Y) <= 8;

                        if (ground || backpack != null && it.Container == backpack.Serial)
                        {
                            Log.Info($"[ECS] item {(ground ? "ground" : "backpack")} 0x{it.Graphic:X4} x{it.Amount} serial 0x{it.Serial:X8} at {it.X},{it.Y} {it.Name}");
                        }
                    }

                    return false;

                // window <tag>|stop: a FrameStats measurement window (CUO_FRAMESTATS=1) tagged like a DevBench plan spot
                case "window" when args.Length > 0 && FrameStats.On:
                    FrameStats.StopWindow();

                    if (args[0] != "stop")
                    {
                        FrameStats.StartWindow(args[0]);
                    }

                    return false;

                // ecssprites on|off: gpu_ecs_sprites (mobiles drawn from the ECS extract or captured from their views)
                case "ecssprites" when args.Length > 0:
                    Settings.GlobalSettings.GpuEcsSprites = args[0] == "on";

                    return false;

                // logout: drop the socket, then swap to the login scene (World.Clear) and exit; log back in with a new process
                case "logout":
                    NetClient.Socket.Disconnect();
                    _logoutAt = Time.Ticks + 2000;

                    return true;

                case "quit":
                    world.Ecs.RunCheck(true);
                    EcsParity.Summary();
                    Client.Game.Exit();

                    return true;
            }

            Log.Warn($"[ECS] driver: unknown command '{cmd}'");

            return false;
        }

        private sealed class DriverGump : Gump
        {
            private readonly EcsSessionDriver _driver;

            public DriverGump(World world, EcsSessionDriver driver) : base(world, 0, 0)
            {
                _driver = driver;
                AcceptMouseInput = false;
                CanCloseWithRightClick = false;
                Width = Height = 0;
            }

            public override void Update()
            {
                base.Update();

                if (World.InGame)
                {
                    _driver.Update(World);
                }
            }
        }

        private static uint FindWorn(PlayerMobile player)
        {
            for (LinkedObject i = player.Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                switch (it.Layer)
                {
                    case Layer.Backpack:
                    case Layer.Hair:
                    case Layer.Beard:
                    case Layer.Face:
                    case Layer.Mount:
                    case Layer.Bank:
                    case Layer.Invalid:
                        continue;
                }

                if (it.Layer < Layer.Mount && it.ItemData.IsWearable)
                {
                    return it.Serial;
                }
            }

            return 0;
        }

        // a ground item of that graphic within 2 tiles
        private static uint FindNear(World world, PlayerMobile player, ushort graphic)
        {
            foreach (Item it in world.Items.Values)
            {
                if (it.OnGround && it.Graphic == graphic && it.Distance <= 2)
                {
                    return it.Serial;
                }
            }

            return 0;
        }

        private static uint FindInBackpack(Item backpack)
        {
            if (backpack == null)
            {
                return 0;
            }

            for (LinkedObject i = backpack.Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                if (it.IsEmpty && !it.ItemData.IsContainer)
                {
                    return it.Serial;
                }
            }

            return 0;
        }
    }
}
