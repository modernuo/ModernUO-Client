// SPDX-License-Identifier: BSD-2-Clause

// CLEANUP(release): developer tooling (E11). Compiled only with CUO_DEVTOOLS; the -ecsdriver switch, WorldEcs's attach path
// and CUOEnviroment.EcsDriverScript are guarded the same way.
#if CUO_DEVTOOLS
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
    //           | target self|ground <dx> <dy>|worn <layer> | parity | use self|held|mobile <dx> <dy> | mount
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

                // use self|held|mobile <dx> <dy>|pack <graphic>: double-clicks (mounting an ethereal from the pack, dismounting on self)
                case "use" when args.Length > 0:
                    uint useSerial = args[0] switch
                    {
                        "self" => player.Serial,
                        "held" => _held,
                        _ => 0
                    };

                    // use pack <graphic hex>: the first item of that graphic in the backpack (T1 funnel part 7: the spellbook)
                    if (args[0] == "pack" && args.Length > 1 && backpack != null)
                    {
                        ushort useGraphic = Convert.ToUInt16(args[1], 16);

                        for (LinkedObject ui = backpack.Items; ui != null; ui = ui.Next)
                        {
                            if (ui is Item packItem && packItem.Graphic == useGraphic)
                            {
                                useSerial = packItem.Serial;

                                break;
                            }
                        }
                    }

                    if (args[0] == "mobile" && args.Length > 2)
                    {
                        int ux = player.X + int.Parse(args[1]), uy = player.Y + int.Parse(args[2]);

                        foreach (Mobile um in world.Mobiles.Values)
                        {
                            if (!ReferenceEquals(um, player) && !um.IsDestroyed && Math.Max(Math.Abs(um.X - ux), Math.Abs(um.Y - uy)) <= 1)
                            {
                                useSerial = um.Serial;

                                break;
                            }
                        }
                    }

                    if (useSerial != 0)
                    {
                        GameActions.DoubleClick(world, useSerial);
                    }
                    else
                    {
                        Log.Warn($"[ECS] driver: nothing to use ({arg})");
                    }

                    return true;

                // mount: logs whether the player and the mobiles within 8 tiles ride, from the Mobile and from the ECS slots
                case "mount":
                    foreach (Mobile mm in world.Mobiles.Values)
                    {
                        if (Math.Abs(mm.X - player.X) <= 8 && Math.Abs(mm.Y - player.Y) <= 8 &&
                            world.Ecs.Mirror.TryGetEntity(mm.Serial, out Friflo.Engine.ECS.Entity me) && me.HasComponent<ClassicUO.Ecs.EquipmentSlots>())
                        {
                            // rider offset: what MobileView computes (the pack's seat on the Mounts table offset) and the ECS AnimationState
                            Item mi = mm.FindItemByLayer(Layer.Mount);
                            string rider = "";
                            if (mi != null)
                            {
                                ushort mb = mi.GetGraphicForAnimation();
                                var seat = Client.Game.UO.Animations.GetMountSeat(mb);
                                string ecsOff = me.HasComponent<ClassicUO.Ecs.AnimationState>() ? me.GetComponent<ClassicUO.Ecs.AnimationState>().MountOffsetY.ToString() : "-";
                                rider = $", mount item 0x{mi.Graphic:X4} body {mb} table {AnimationRules.MountOffsetY(mi.Graphic)} shift {seat.Value}{(seat.Absolute ? " absolute" : "")} rider offset {AnimationRules.MountOffsetY(mi.Graphic, seat)} (ecs {ecsOff})";
                            }
                            Log.Info($"[ECS] mount 0x{mm.Serial:X8} body 0x{mm.Graphic:X4}: mobile {mm.IsMounted}, ecs {me.GetComponent<ClassicUO.Ecs.EquipmentSlots>().Mounted} (rebuilt lazily), war {mm.InWarMode}, noto {mm.NotorietyFlag}, flags {mm.Flags}{rider}");
                        }
                    }

                    return false;

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

                        // mobile <dx> <dy>: the mobile (not the player) nearest to that tile, within 3 tiles (e.g. [kill on an NPC)
                        case "mobile" when args.Length > 2:
                            int tx = player.X + int.Parse(args[1]), ty = player.Y + int.Parse(args[2]);
                            Mobile best = null;
                            int bestDist = 4;

                            foreach (Mobile m in world.Mobiles.Values)
                            {
                                int d = Math.Max(Math.Abs(m.X - tx), Math.Abs(m.Y - ty));

                                if (!ReferenceEquals(m, player) && !m.IsDestroyed && d < bestDist)
                                {
                                    best = m;
                                    bestDist = d;
                                }
                            }

                            if (best != null)
                            {
                                world.TargetManager.Target(best.Serial);
                            }
                            else
                            {
                                Log.Warn($"[ECS] driver: no mobile near {tx},{ty}");
                                world.TargetManager.CancelTarget();
                            }

                            break;

                        // held: the item the last pick picked up (e.g. after drop backpack, to change it in the pack)
                        case "held" when _held != 0:
                            world.TargetManager.Target(_held);

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

                // season <spring|summer|fall|winter|desolation>: as the server's season packet (World.ChangeSeason). E12: map
                // statics drawn in another season's graphic replace their baked sprite, so the world pass queues overrides
                case "season" when args.Length > 0:
                    world.ChangeSeason(Enum.Parse<Season>(args[0], true), 0);
                    Log.Info($"[ECS] driver: season {args[0]}");

                    return false;

                // aura <0-3>: the profile's aura under feet (3 = always; E12 checks that the probe pass doesn't draw them)
                case "aura" when args.Length > 0 && ProfileManager.CurrentProfile is { } auraProfile:
                    auraProfile.AuraUnderFeetType = Convert.ToInt32(args[0]);

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
                // shot <name>: saves the world render target as CUO_BENCH_SHOTS/<name>_gpu.png (DevBench's shot:)
                case "shot" when args.Length > 0 && Environment.GetEnvironmentVariable("CUO_BENCH_SHOTS") is { Length: > 0 } shots:
                    Client.Game.RequestWorldShot(Path.Combine(shots, $"{args[0]}_gpu.png"));

                    return true;

                // itemcache on|off: the Gpu world prepare's ground-item cache (gpu_prepare_item_cache)
                case "itemcache" when args.Length > 0:
                    Settings.GlobalSettings.GpuPrepareItemCache = args[0] == "on";
                    Log.Info($"[ECS] driver: item cache {args[0]}");

                    return true;

                // itemmixed on|off: the item cache's mixed tiles (gpu_prepare_item_cache_mixed, E7)
                case "itemmixed" when args.Length > 0:
                    Settings.GlobalSettings.GpuPrepareItemCacheMixed = args[0] == "on";
                    Log.Info($"[ECS] driver: item cache mixed tiles {args[0]}");

                    return true;

#if CUO_DEVTOOLS
                // fx N graphic [blend [radius [z [f|m|l]]]]: N client-side effects round the player (BenchEffects), fx 0 removes them all
                case "fx" when args.Length > 0:
                    BenchEffects.Spawn(world, "effects:" + string.Join(":", args));

                    return true;

                // fxserve on|off: the item cache serves tiles holding effects (gpu_prepare_item_cache_effects, E6)
                case "fxserve" when args.Length > 0:
                    Settings.GlobalSettings.GpuPrepareItemCacheEffects = args[0] == "on";
                    Log.Info($"[ECS] driver: item cache serves effect tiles {args[0]}");

                    return true;
#endif

                // handles on|off: the object handles (name overhead) toggle, for every object type; handles count: the
                // name gumps open and the items/mobiles showing handles (W5's active item check)
                case "handles" when args.Length > 0:
                    if (args[0] == "count")
                    {
                        int gumps = 0, items = 0, mobiles = 0;

                        foreach (Gump g in UIManager.Gumps)
                        {
                            if (g is NameOverheadGump && !g.IsDisposed)
                            {
                                gumps++;
                            }
                        }

                        foreach (Item it in world.Items.Values)
                        {
                            if (it.ObjectHandlesStatus == ObjectHandlesStatus.DISPLAYING)
                            {
                                items++;
                            }
                        }

                        foreach (Mobile mob in world.Mobiles.Values)
                        {
                            if (mob.ObjectHandlesStatus == ObjectHandlesStatus.DISPLAYING)
                            {
                                mobiles++;
                            }
                        }

                        Log.Info($"[ECS] driver: handles: {gumps} name gumps, {items} items and {mobiles} mobiles displaying");
                    }
                    else
                    {
                        if (args[0] == "on")
                        {
                            world.NameOverHeadManager.TypeAllowed = NameOverheadTypeAllowed.All;
                        }

                        world.NameOverHeadManager.IsToggled = args[0] == "on";
                        Log.Info($"[ECS] driver: handles {args[0]}");
                    }

                    return true;

                // mobiles: hits, max and percentage of the mobiles within 8 tiles (health bars read the percentage)
                case "mobiles":
                    foreach (Mobile mob in world.Mobiles.Values)
                    {
                        if (Math.Abs(mob.X - player.X) <= 8 && Math.Abs(mob.Y - player.Y) <= 8)
                        {
                            Log.Info($"[ECS] mobile 0x{mob.Serial:X8} body 0x{mob.Graphic:X4} hits {mob.Hits}/{mob.HitsMax} = {mob.HitsPercentage}% at {mob.X},{mob.Y} {mob.Name}");
                        }
                    }

                    return true;

                // w5 on|off: all W5 update shortcuts (world_update_active_items, gpu_mobile_frame_cache, text_skip_expired,
                // mobile_push_inputs)
                case "w5" when args.Length > 0:
                    Settings.GlobalSettings.WorldUpdateActiveItems = Settings.GlobalSettings.GpuMobileFrameCache =
                        Settings.GlobalSettings.TextSkipExpired = Settings.GlobalSettings.MobilePushInputs = args[0] == "on";
                    Log.Info($"[ECS] driver: W5 shortcuts {args[0]}");

                    return true;

                // mobsys on|off: World.Update's mobile loop by the ECS clocks (world_update_mobile_system, E4)
                case "mobsys" when args.Length > 0:
                    Settings.GlobalSettings.WorldUpdateMobileSystem = args[0] == "on";
                    Log.Info($"[ECS] driver: mobile update by clock {args[0]}");

                    return true;

                // pushinputs on|off: mobiles' pushed ECS inputs (mobile_push_inputs, W5)
                case "pushinputs" when args.Length > 0:
                    Settings.GlobalSettings.MobilePushInputs = args[0] == "on";
                    Log.Info($"[ECS] driver: mobile pushed inputs {args[0]}");

                    return true;

                // mobcache on|off: the Gpu world's mobile frame cache (gpu_mobile_frame_cache, W5)
                case "mobcache" when args.Length > 0:
                    Settings.GlobalSettings.GpuMobileFrameCache = args[0] == "on";
                    Log.Info($"[ECS] driver: mobile frame cache {args[0]}");

                    return true;

                // activeitems on|off: World.Update's active item list (world_update_active_items, W5)
                case "activeitems" when args.Length > 0:
                    Settings.GlobalSettings.WorldUpdateActiveItems = args[0] == "on";
                    Log.Info($"[ECS] driver: active items {args[0]}");

                    return true;

                // answers OK (button 1) on the frontmost server gump, e.g. a staff command's confirmation (as DevBench's gumpok)
                case "gumpok":
                    foreach (var g in UIManager.Gumps)
                    {
                        if (g.ServerSerial != 0 && g.GetType() == typeof(Gump))
                        {
                            Log.Info($"[ECS] driver: gump 0x{g.ServerSerial:X8}: OK");
                            g.OnButtonClick(1);

                            break;
                        }
                    }

                    return true;

                // journal [n]: the last n (default 6) journal lines, e.g. a staff command's server answer ([Global Count)
                case "journal":
                    int jn = Math.Min(args.Length > 0 ? int.Parse(args[0]) : 6, JournalManager.Entries.Count);

                    for (int ji = JournalManager.Entries.Count - jn; ji < JournalManager.Entries.Count; ji++)
                    {
                        JournalEntry je = JournalManager.Entries[ji];
                        Log.Info($"[ECS] journal {je.Name}: {je.Text}");
                    }

                    return false;

                // gumps: the open gumps' types and server serials (w5boat: did the spellbook open)
                case "gumps":
                    foreach (var og in UIManager.Gumps)
                    {
                        Log.Info($"[ECS] gump {og.GetType().Name} local 0x{og.LocalSerial:X8} server 0x{og.ServerSerial:X8}");
                    }

                    return false;

                // boat: the player's tile and the multis within 24 tiles (T1 part 7, w5boat: did the boat move and turn)
                case "boat":
                    Log.Info($"[ECS] boat: player at {player.X},{player.Y},{player.Z} offset ({player.Offset.X},{player.Offset.Y},{player.Offset.Z})");

                    foreach (Item multi in world.Items.Values)
                    {
                        if (multi.IsMulti && !multi.IsDestroyed && Math.Abs(multi.X - player.X) <= 24 && Math.Abs(multi.Y - player.Y) <= 24)
                        {
                            Log.Info($"[ECS] boat: multi 0x{multi.Serial:X8} graphic 0x{multi.Graphic:X4} at {multi.X},{multi.Y},{multi.Z} offset ({multi.Offset.X},{multi.Offset.Y},{multi.Offset.Z})");
                        }
                    }

                    return false;

                case "items":
                    foreach (Item it in world.Items.Values)
                    {
                        bool ground = it.OnGround && Math.Abs(it.X - player.X) <= 8 && Math.Abs(it.Y - player.Y) <= 8;

                        if (ground || backpack != null && it.Container == backpack.Serial)
                        {
                            Log.Info($"[ECS] item {(ground ? "ground" : "backpack")} 0x{it.Graphic:X4} x{it.Amount} hue 0x{it.Hue:X4} flags {it.Flags} serial 0x{it.Serial:X8} at {it.X},{it.Y},{it.Z} {it.Name}");
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

#endif
