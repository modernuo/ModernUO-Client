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

                    // use near <graphic>: a ground item of that graphic within 2 tiles (T1 part 9, uistate: the corpse)
                    if (args[0] == "near" && args.Length > 1)
                    {
                        useSerial = FindNear(world, player, Convert.ToUInt16(args[1], 16));
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
                        // T1 part 9 (uistate): the first item of a graphic in the backpack or the bank, or what is worn on a layer
                        "pack" when args.Length > 1 => FindChild(backpack, Convert.ToUInt16(args[1], 16)),
                        "bank" when args.Length > 1 => FindChild(player.FindItemByLayer(Layer.Bank), Convert.ToUInt16(args[1], 16)),
                        "layer" when args.Length > 1 => player.FindItemByLayer((Layer)int.Parse(args[1]))?.Serial ?? 0,
                        _ => _held
                    };

                    // pick pack|bank <graphic> <amount>: part of a stack (the split, without the split gump)
                    int pickAmount = (args[0] == "pack" || args[0] == "bank") && args.Length > 2 ? int.Parse(args[2]) : 0;

                    // an explicit amount skips the split-stack gump
                    if (serial != 0 && GameActions.PickUp(world, serial, 0, 0, pickAmount > 0 ? pickAmount : Math.Max(1, (int)(world.Items.Get(serial)?.Amount ?? 1))))
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

                        // T1 part 9 (uistate): into the open bank box, into a container in the backpack, onto a mobile (a trade)
                        case "bank" when player.FindItemByLayer(Layer.Bank) is { } dropBank:
                            GameActions.DropItem(_held, 0xFFFF, 0xFFFF, 0, dropBank.Serial);

                            break;

                        default:
                            if (args.Length > 1 && args[0] == "container" && FindChild(backpack, Convert.ToUInt16(args[1], 16)) is var into && into != 0)
                            {
                                GameActions.DropItem(_held, 0xFFFF, 0xFFFF, 0, into);
                            }
                            else if (args.Length > 2 && args[0] == "mobile" && FindMobileNear(world, player, int.Parse(args[1]), int.Parse(args[2])) is { } onto)
                            {
                                GameActions.DropItem(_held, 0xFFFF, 0xFFFF, 0, onto.Serial);
                            }
                            else
                            {
                                Log.Warn($"[ECS] driver: nowhere to drop ({arg})");
                            }

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

                        // T1 part 9 (uistate cleanup): the first item of a graphic in the backpack / on the ground near / anywhere the
                        // player holds it (pack, bank, worn); a mobile within 8 tiles whose name contains the text
                        case "pack" when args.Length > 1 && FindChild(backpack, Convert.ToUInt16(args[1], 16)) is var tp && tp != 0:
                            world.TargetManager.Target(tp);

                            break;

                        case "near" when args.Length > 1 && FindNear(world, player, Convert.ToUInt16(args[1], 16)) is var tn && tn != 0:
                            world.TargetManager.Target(tn);

                            break;

                        case "mine" when args.Length > 1 && FindMine(world, player, Convert.ToUInt16(args[1], 16)) is var tm && tm != 0:
                            world.TargetManager.Target(tm);

                            break;

                        case "named" when args.Length > 1 && FindNamed(world, player, args[1]) is { } tnm:
                            world.TargetManager.Target(tnm.Serial);

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

                // ---- T1 funnel part 9 (uistate.txt): the UI-state gate's commands. Each logs "[ECS] ui ..." lines that prove a step

                // contents backpack|bank|near <graphic>|held: a container's children (serial, graphic, amount, layer), in list order
                case "contents" when args.Length > 0:
                    Item box = args[0] switch
                    {
                        "backpack" => backpack,
                        "bank" => player.FindItemByLayer(Layer.Bank),
                        "near" when args.Length > 1 => world.Items.Get(FindNear(world, player, Convert.ToUInt16(args[1], 16))),
                        "held" => world.Items.Get(_held),
                        _ => null
                    };

                    if (box == null)
                    {
                        Log.Info($"[ECS] ui contents {arg}: no such container");

                        return false;
                    }

                    var csb = new System.Text.StringBuilder();
                    int cn = 0;

                    for (LinkedObject ci = box.Items; ci != null; ci = ci.Next)
                    {
                        Item c = (Item)ci;
                        csb.Append($" 0x{c.Serial:X8}:0x{c.Graphic:X4}x{c.Amount}");
                        cn++;
                    }

                    Log.Info($"[ECS] ui contents {arg} 0x{box.Serial:X8} g=0x{box.Graphic:X4} opened={box.Opened} children={cn}:{csb}");

                    return false;

                // find <graphic>: every item of that graphic World holds: container, amount, layer, whether the player holds it
                case "find" when args.Length > 0:
                    ushort fg = Convert.ToUInt16(args[0], 16);
                    int fn = 0;

                    foreach (Item fi in world.Items.Values)
                    {
                        if (fi.Graphic == fg && !fi.IsDestroyed)
                        {
                            fn++;
                            Log.Info($"[ECS] ui find 0x{fg:X4}: 0x{fi.Serial:X8} x{fi.Amount} container 0x{fi.Container:X8} layer {fi.Layer} mine={ReferenceEquals(fi.RootContainer, player)} name '{fi.Name}'");
                        }
                    }

                    Log.Info($"[ECS] ui find 0x{fg:X4}: {fn} item(s)");

                    return false;

                // worn: the player's worn items by layer
                case "worn":
                    for (LinkedObject wi = player.Items; wi != null; wi = wi.Next)
                    {
                        Item w = (Item)wi;
                        Log.Info($"[ECS] ui worn {w.Layer}: 0x{w.Serial:X8} g=0x{w.Graphic:X4}");
                    }

                    return false;

                // buy <name text, _ for a space> <amount>: the vendor's buy-list entry whose name contains the text (after "vendor buy" opened the
                // shop), bought as the shop gump's accept does (0x3B)
                case "buy" when args.Length > 1:
                    foreach (Item bi in world.Items.Values)
                    {
                        if (bi.Price > 0 && !bi.IsDestroyed && bi.Name != null && bi.Name.IndexOf(args[0].Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0 &&
                            world.Items.Get(bi.Container) is { } buyBox && (buyBox.Layer == Layer.ShopBuy || buyBox.Layer == Layer.ShopBuyRestock))
                        {
                            Log.Info($"[ECS] ui buy '{bi.Name}' 0x{bi.Serial:X8} price {bi.Price} x{args[1]} from vendor 0x{buyBox.Container:X8}");
                            NetClient.Socket.Send_BuyRequest(buyBox.Container, new[] { Tuple.Create(bi.Serial, ushort.Parse(args[1])) });

                            return true;
                        }
                    }

                    Log.Warn($"[ECS] driver: no buy-list entry named '{args[0]}'");

                    return true;

                // named <text>: the mobiles within 8 tiles whose name contains the text (name, notoriety, renamable)
                case "named" when args.Length > 0:
                    foreach (Mobile nm in world.Mobiles.Values)
                    {
                        if (!nm.IsDestroyed && nm.Name != null && nm.Name.IndexOf(args[0].Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0 &&
                            Math.Max(Math.Abs(nm.X - player.X), Math.Abs(nm.Y - player.Y)) <= 8)
                        {
                            Log.Info($"[ECS] ui named '{args[0]}': 0x{nm.Serial:X8} g=0x{nm.Graphic:X4} name '{nm.Name}' noto {nm.NotorietyFlag} renamable {nm.IsRenamable} hits {nm.Hits}/{nm.HitsMax}");
                        }
                    }

                    return false;

                // status near|<name text>: asks the server for the status (0x34; the answer 0x11 carries the name) of every mobile
                // within 4 tiles, or of the one named
                case "status" when args.Length > 0:
                    foreach (Mobile sm in world.Mobiles.Values)
                    {
                        if (!ReferenceEquals(sm, player) && !sm.IsDestroyed && Math.Max(Math.Abs(sm.X - player.X), Math.Abs(sm.Y - player.Y)) <= 4 &&
                            (args[0] == "near" || ReferenceEquals(sm, FindNamed(world, player, args[0]))))
                        {
                            GameActions.RequestMobileStatus(world, sm.Serial, true);
                        }
                    }

                    return true;

                // rename <name text> <new name>: the pet's rename request (0x75), as the status gump's name field sends it
                case "rename" when args.Length > 1 && FindNamed(world, player, args[0]) is { } rm:
                    Log.Info($"[ECS] ui rename 0x{rm.Serial:X8} '{rm.Name}' -> '{args[1]}'");
                    GameActions.Rename(rm.Serial, args[1]);

                    return true;

                // trade accept|log: the open trade gumps (accept: this side accepts, as the gump's check box)
                case "trade" when args.Length > 0:
                    int tn2 = 0;

                    foreach (var tg in UIManager.Gumps)
                    {
                        if (tg is TradingGump trade)
                        {
                            tn2++;
                            Log.Info($"[ECS] ui trade gump 0x{trade.LocalSerial:X8} id1 0x{trade.ID1:X8} id2 0x{trade.ID2:X8} mine 0x{trade.ID1:X8}");

                            if (args[0] == "accept")
                            {
                                GameActions.AcceptTrade(trade.ID1, true);
                            }
                        }
                    }

                    Log.Info($"[ECS] ui trade {args[0]}: {tn2} trade gump(s)");

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

                // smoothboat on|off: the profile's smooth boat movement (0xF6 queues boat steps instead of jumping the boat)
                case "smoothboat" when args.Length > 0 && ProfileManager.CurrentProfile is { } boatProfile:
                    boatProfile.UseSmoothBoatMovement = args[0] == "on";
                    Log.Info($"[ECS] driver: smooth boat movement {args[0]}");

                    return true;

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

        // T1 part 9: the first child of that graphic (a container's direct children)
        private static uint FindChild(Item container, ushort graphic)
        {
            for (LinkedObject i = container?.Items; i != null; i = i.Next)
            {
                if (i is Item it && it.Graphic == graphic)
                {
                    return it.Serial;
                }
            }

            return 0;
        }

        // the first item of that graphic the player holds anywhere (backpack, bank, worn, nested)
        private static uint FindMine(World world, PlayerMobile player, ushort graphic)
        {
            foreach (Item it in world.Items.Values)
            {
                if (it.Graphic == graphic && !it.IsDestroyed && ReferenceEquals(it.RootContainer, player))
                {
                    return it.Serial;
                }
            }

            return 0;
        }

        // the nearest mobile within 8 tiles (not the player) whose name contains the text
        private static Mobile FindNamed(World world, PlayerMobile player, string text)
        {
            Mobile best = null;
            int bestDist = 9;

            foreach (Mobile m in world.Mobiles.Values)
            {
                int d = Math.Max(Math.Abs(m.X - player.X), Math.Abs(m.Y - player.Y));

                if (!ReferenceEquals(m, player) && !m.IsDestroyed && m.Name != null && m.Name.IndexOf(text.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0 && d < bestDist)
                {
                    best = m;
                    bestDist = d;
                }
            }

            return best;
        }

        // the mobile (not the player) nearest to the player's tile + (dx, dy), within 3 tiles of it
        private static Mobile FindMobileNear(World world, PlayerMobile player, int dx, int dy)
        {
            int tx = player.X + dx, ty = player.Y + dy;
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

            return best;
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
