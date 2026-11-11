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
        private bool _ownsMouse;

        private EcsSessionDriver(string[] lines)
        {
            _lines = lines;
        }

        // Tests: a driver over in-memory lines (no file, no world).
        internal static EcsSessionDriver FromLines(string[] lines) => new EcsSessionDriver(lines);

        // The script's `mouse` override is the driver's: set only by `mouse`, released when the script ends (EOF, quit,
        // logout), when the driver gump goes away or the world leaves the game (disconnect), and if a line throws, so a
        // frozen scripted cursor never outlives the script. An override the driver did not set (DevBench) is left alone.
        internal bool OwnsMouse => _ownsMouse;

        internal void ReleaseMouse()
        {
            if (_ownsMouse)
            {
                _ownsMouse = false;
                Input.Mouse.Override = null;
            }
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

            RunScript(world, player);
        }

        // The lambda lives here, not in Update: a closure over Update's parameters is allocated at Update's entry,
        // which put 32 B on every frame's FrameStats alloc/frame even while the script was waiting.
        private void RunScript(World world, PlayerMobile player)
        {
            Step(line => Execute(world, player, line));
        }

        // Runs script lines until one yields (true) or the script ends (false; the scripted cursor is released).
        internal bool Step(Func<string, bool> execute)
        {
            try
            {
                while (_pc < _lines.Length)
                {
                    string line = _lines[_pc++].Trim();

                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    Log.Info($"[ECS] driver> {line}");

                    if (line.StartsWith("mouse ", StringComparison.Ordinal) ? ExecuteMouse(line.Substring(6)) : execute(line))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                ReleaseMouse();

                throw;
            }

            ReleaseMouse();

            return false;
        }

        // mouse <x> <y> | mouse off: the scripted cursor (Input.Mouse.Override; the OS cursor is not moved), so the
        // shots hover the same spot
        private bool ExecuteMouse(string arg)
        {
            string[] args = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (args.Length > 1 && int.TryParse(args[0], out int mx) && int.TryParse(args[1], out int my))
            {
                Input.Mouse.Override = new Microsoft.Xna.Framework.Point(mx, my);
                _ownsMouse = true;
            }
            else
            {
                ReleaseMouse();
            }

            Log.Info($"[ECS] ui mouse {string.Join(" ", args)}");

            return true;
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

                        foreach (Item packItem in world.State.Children(backpack))
                        {
                            if (packItem.Graphic == useGraphic)
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
                    if (serial != 0 && GameActions.PickUp(world, serial, 0, 0, pickAmount > 0 ? pickAmount : Math.Max(1, (int)(world.FindItem(serial)?.Amount ?? 1))))
                    {
                        _held = serial;
                        Log.Info($"[ECS] driver: picked up 0x{serial:X8}");
                    }
                    else
                    {
                        Item it = world.FindItem(serial);
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
                        case "item" when args.Length > 1 && world.FindItem(Convert.ToUInt32(args[1], 16)) is { } known:
                            world.TargetManager.Target(known.Serial);

                            break;

                        default:
                            Log.Warn($"[ECS] driver: nothing to target ({string.Join(' ', args)}); the cursor is cancelled");
                            world.TargetManager.CancelTarget();

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
                        "near" when args.Length > 1 => world.FindItem(FindNear(world, player, Convert.ToUInt16(args[1], 16))),
                        "held" => world.FindItem(_held),
                        _ => null
                    };

                    if (box == null)
                    {
                        Log.Info($"[ECS] ui contents {arg}: no such container");

                        return false;
                    }

                    var csb = new System.Text.StringBuilder();
                    int cn = 0;

                    // T2: the reader path (the ECS's contents)
                    foreach (Item c in world.State.Children(box))
                    {
                        csb.Append($" 0x{c.Serial:X8}:0x{c.Graphic:X4}x{c.Amount}");
                        cn++;
                    }

                    Log.Info($"[ECS] ui contents {arg} 0x{box.Serial:X8} g=0x{box.Graphic:X4} opened={box.Opened} children={cn}:{csb}");
                    // perf-8: the window's content updates so far (full rebuilds vs reused controls) and its checker
                    Log.Info($"[ECS] ui container-controls {arg}: window {(UIManager.GetGump<UI.Gumps.ContainerGump>(box.Serial) != null ? "open" : "closed")}, " +
                             $"updates full {UI.Gumps.ContainerItemControls.FullRebuilds} incremental {UI.Gumps.ContainerItemControls.IncrementalUpdates}, " +
                             $"controls reused {UI.Gumps.ContainerItemControls.ControlsReused} created {UI.Gumps.ContainerItemControls.ControlsCreated}, " +
                             $"check {(UI.Gumps.ContainerItemControls.CheckOn ? "on" : "off")} checked {UI.Gumps.ContainerItemControls.Checked} mismatched {UI.Gumps.ContainerItemControls.Mismatched}");

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
                            Log.Info($"[ECS] ui find 0x{fg:X4}: 0x{fi.Serial:X8} x{fi.Amount} container 0x{fi.Container:X8} layer {fi.Layer} mine={fi.RootContainer == player.Serial} name '{fi.Name}'");
                        }
                    }

                    Log.Info($"[ECS] ui find 0x{fg:X4}: {fn} item(s)");

                    return false;

                // worn: the player's worn items by layer
                case "worn":
                    foreach (Item w in world.State.Children(player))
                    {
                        Log.Info($"[ECS] ui worn {w.Layer}: 0x{w.Serial:X8} g=0x{w.Graphic:X4}");
                    }

                    // perf-12: the paperdoll slots' prepared worn items against their own lookups
                    Log.Info($"[ECS] ui paperdoll-slots: check {(UI.Gumps.PaperDollGump.SlotsCheckOn ? "on" : "off")} " +
                             $"checked {UI.Gumps.PaperDollGump.SlotsChecked} mismatched {UI.Gumps.PaperDollGump.SlotsMismatched}");

                    return false;

                // buy <name text, _ for a space> <amount>: the vendor's buy-list entry whose name contains the text (after "vendor buy" opened the
                // shop), bought as the shop gump's accept does (0x3B)
                case "buy" when args.Length > 1:
                    foreach (Item bi in world.Items.Values)
                    {
                        if (bi.Price > 0 && !bi.IsDestroyed && bi.Name != null && bi.Name.IndexOf(args[0].Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0 &&
                            world.FindItem(bi.Container) is { } buyBox && (buyBox.Layer == Layer.ShopBuy || buyBox.Layer == Layer.ShopBuyRestock))
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
                    Scenes.SpritePickTable.Summary();

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
                // war on|off: the war mode request (0x72), as the war-mode toggle sends it; the server answers with 0x72
                case "war" when args.Length > 0:
                    GameActions.RequestWarMode(player, args[0] == "on");
                    Log.Info($"[ECS] driver: war {args[0]}");

                    return true;

                // small: the player's small fields and counts over every held object, read through the
                // getters (with CUO_SMALL_FIELDS_CHECK each read is compared with the record)
                case "small":
                    int lights = 0, damageable = 0, usedLayer = 0, multis = 0, female = 0, renamable = 0, fast = 0;
                    int[] races = new int[4];

                    foreach (Item si in world.Items.Values)
                    {
                        lights += si.LightID != 0 ? 1 : 0;
                        damageable += si.IsDamageable ? 1 : 0;
                        usedLayer += si.UsedLayer ? 1 : 0;
                        multis += si.IsMulti ? 1 : 0;
                    }

                    foreach (Mobile sm in world.Mobiles.Values)
                    {
                        races[(int)sm.Race & 3]++;
                        female += sm.IsFemale ? 1 : 0;
                        renamable += sm.IsRenamable ? 1 : 0;
                        fast += sm.SpeedMode != CharacterSpeedType.Normal ? 1 : 0;
                    }

                    Log.Info($"[ECS] small player race {player.Race} female {player.IsFemale} renamable {player.IsRenamable} speed {player.SpeedMode} war {player.InWarMode} body 0x{player.Graphic:X4}; items {world.Items.Count} lights {lights} damageable {damageable} usedlayer {usedLayer} multis {multis}; mobiles {world.Mobiles.Count} races {races[0]}/{races[1]}/{races[2]}/{races[3]} female {female} renamable {renamable} speedmode {fast}; {SmallFields.Summary()}");

                    return false;

                case "season" when args.Length > 0:
                    world.ChangeSeason(Enum.Parse<Season>(args[0], true), 0);
                    Log.Info($"[ECS] driver: season {args[0]}");

                    return false;

                // aura <0-3>: the profile's aura under feet (3 = always; E12 checks that the probe pass doesn't draw them)
                case "aura" when args.Length > 0 && ProfileManager.CurrentProfile is { } auraProfile:
                    auraProfile.AuraUnderFeetType = Convert.ToInt32(args[0]);

                    return false;

                // gridloot <0-2>: the profile's grid loot type (gump-handles: 1 opens a corpse in a GridLootGump)
                case "gridloot" when args.Length > 0 && ProfileManager.CurrentProfile is { } lootProfile:
                    lootProfile.GridLootType = Convert.ToInt32(args[0]);

                    return false;

                // autoopen doors|corpses on|off [range]: the profile's auto-open doors / corpses (perf-13's step rules); range is
                // the corpse range
                case "autoopen" when args.Length > 1 && ProfileManager.CurrentProfile is { } openProfile:
                    if (args[0] == "doors")
                    {
                        openProfile.AutoOpenDoors = args[1] == "on";
                    }
                    else
                    {
                        openProfile.AutoOpenCorpses = args[1] == "on";

                        if (args.Length > 2)
                        {
                            openProfile.AutoOpenCorpseRange = Convert.ToInt32(args[2]);
                        }
                    }

                    Log.Info($"[ECS] driver: autoopen doors {openProfile.AutoOpenDoors}, corpses {openProfile.AutoOpenCorpses} range {openProfile.AutoOpenCorpseRange} options {openProfile.CorpseOpenOptions}");

                    return false;

                // inspect self|near <graphic>: opens an InspectorGump on that entity, as the inspect target does.
                // inspect log: each open inspector's entity, looked up by its serial (gone = removed from the world).
                // inspect dump: presses each open inspector's Dump button (a removed entity prints "gone" to the journal)
                case "inspect" when args.Length > 0:
                    if (args[0] == "log" || args[0] == "dump")
                    {
                        foreach (Gump g in UIManager.Gumps)
                        {
                            if (g is InspectorGump inspector && !inspector.IsDisposed)
                            {
                                Entity shown = inspector.InspectedEntity();
                                Log.Info($"[ECS] ui inspect {(shown == null ? "gone" : $"0x{shown.Serial:X8} 0x{shown.Graphic:X4} destroyed {shown.IsDestroyed}")}");

                                if (args[0] == "dump")
                                {
                                    inspector.OnButtonClick(0);
                                }
                            }
                        }

                        return false;
                    }

                    uint inspectSerial = args[0] == "self" ? player.Serial : args[0] == "near" && args.Length > 1 ? FindNear(world, player, Convert.ToUInt16(args[1], 16)) : 0;

                    if (world.Get(inspectSerial) is { } inspected)
                    {
                        UIManager.Add(new InspectorGump(world, inspected));
                        Log.Info($"[ECS] ui inspect open 0x{inspectSerial:X8}");
                    }
                    else
                    {
                        Log.Info($"[ECS] ui inspect {arg}: no entity");
                    }

                    return true;

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

                // T2: without CUO_BENCH_SHOTS a shot is skipped on purpose (part 9's w5push logged it as an unknown command)
                case "shot":
                    Log.Info($"[ECS] driver: shot {arg} skipped (CUO_BENCH_SHOTS is not set)");

                    return false;

                // close <gump type>: disposes the open gumps of that type (T2's shots: the same containers reopened, so both
                // shots show the same items at the same server positions)
                case "close" when args.Length > 0:
                    var toClose = new System.Collections.Generic.List<Gump>();

                    foreach (Gump g in UIManager.Gumps)
                    {
                        if (!g.IsDisposed && g.GetType().Name == args[0])
                        {
                            toClose.Add(g);
                        }
                    }

                    foreach (Gump g in toClose)
                    {
                        g.Dispose();
                    }

                    Log.Info($"[ECS] ui close {args[0]}: {toClose.Count}");

                    return true;

                // place <GumpType> <x> <y> [dx]: the open gumps of that type at fixed positions, in serial order, dx apart (the
                // T2 shots: a reopened container cascades to a new position)
                case "place" when args.Length > 2 && int.TryParse(args[1], out int px) && int.TryParse(args[2], out int py):
                {
                    int dx = args.Length > 3 && int.TryParse(args[3], out int d) ? d : 0;
                    var toPlace = new System.Collections.Generic.List<Gump>();

                    foreach (Gump g in UIManager.Gumps)
                    {
                        if (!g.IsDisposed && g.GetType().Name == args[0])
                        {
                            toPlace.Add(g);
                        }
                    }

                    toPlace.Sort((a, b) => a.LocalSerial.CompareTo(b.LocalSerial));

                    for (int k = 0; k < toPlace.Count; k++)
                    {
                        toPlace[k].X = px + k * dx;
                        toPlace[k].Y = py;
                    }

                    Log.Info($"[ECS] ui place {args[0]}: {toPlace.Count}");

                    return true;
                }

                // options [page]: opens the Options gump (page 3 = Video, where the World renderer choice is); optionsdrop opens
                // that choice's dropdown. Both for the renderer-options screenshot job (shot <name> saves <name>_gpu_screen.png)
                case "options":
                    GameActions.OpenSettings(world, args.Length > 0 && int.TryParse(args[0], out int optPage) ? optPage : 3);
                    Log.Info("[ECS] ui options opened");

                    return true;

                case "optionsdrop":
                    UIManager.GetGump<UI.Gumps.OptionsGump>()?.OpenWorldRendererListForDriver();
                    Log.Info("[ECS] ui optionsdrop");

                    return true;

                // paperdoll: opens the player's own paperdoll (T3a's shots; a double-click on a mounted self would dismount)
                case "paperdoll":
                    GameActions.OpenPaperdoll(world, player.Serial);
                    Log.Info($"[ECS] ui paperdoll 0x{player.Serial:X8} mounted={player.IsMounted}");

                    return true;

                // open status|skills|buffs|combatbook|worldmap: the player-stat gumps, which read World.PlayerState (player-state gate)
                case "open" when args.Length > 0:
                    switch (args[0])
                    {
                        case "status": GameActions.OpenStatusBar(world); break;
                        case "skills": GameActions.OpenSkills(world); break;
                        case "buffs":
                            if (UIManager.GetGump<BuffGump>() == null)
                            {
                                UIManager.Add(new BuffGump(world, 100, 100));
                            }

                            break;
                        case "combatbook": GameActions.OpenAbilitiesBook(world); break;
                        // perf-14: the world map builds its image on a task with the race read on the game thread
                        case "worldmap": GameActions.OpenWorldMap(world); break;
                    }

                    Log.Info($"[ECS] ui open {args[0]}");

                    return true;

                // playerstate: World.PlayerState's status, skill, buff and ability values (player-state gate)
                case "playerstate":
                {
                    PlayerState ps = world.PlayerState;
                    int skillsSet = 0, skillSum = 0;

                    foreach (Skill sk in ps.Skills)
                    {
                        if (sk.BaseFixed != 0)
                        {
                            skillsSet++;
                            skillSum += sk.BaseFixed;
                        }
                    }

                    Log.Info($"[ECS] playerstate str {ps.Strength} dex {ps.Dexterity} int {ps.Intelligence} gold {ps.Gold} weight {ps.Weight}/{ps.WeightMax} " +
                             $"followers {ps.Followers}/{ps.FollowersMax} cap {ps.StatsCap} luck {ps.Luck} res {ps.PhysicalResistance},{ps.FireResistance},{ps.ColdResistance},{ps.PoisonResistance},{ps.EnergyResistance} " +
                             $"dmg {ps.DamageMin}-{ps.DamageMax} tithing {ps.TithingPoints} locks {ps.StrLock},{ps.DexLock},{ps.IntLock} " +
                             $"skills {ps.Skills.Length} set {skillsSet} sum {skillSum} buffs {ps.BuffIcons.Count} abilities {ps.PrimaryAbility},{ps.SecondaryAbility}");

                    return false;
                }

                // renderer gpu|legacy: switches the world renderer for this session (T3a's shots of Legacy's MobileView)
                case "renderer" when args.Length > 0:
                {
                    bool switched = RendererStartup.TrySwitch(args[0], out string message);
                    Log.Info($"[ECS] driver: renderer {args[0]}: {(switched ? "switched" : "not switched: " + message)}");

                    return true;
                }

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
                // fx N graphic [blend [radius [z [f|m|l|e]]]]: N client-side effects round the player (BenchEffects; e: on mobiles and items), fx 0 removes them all
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

                // healthbars: a health bar gump (classic and custom, alternating) for the player and each mobile within 4 tiles,
                // with the hits it draws and the percentage the overhead line draws (cold-caches gate)
                case "healthbars":
                {
                    bool custom = false;

                    foreach (Mobile hb in world.Mobiles.Values)
                    {
                        if (hb.IsDestroyed || Math.Max(Math.Abs(hb.X - player.X), Math.Abs(hb.Y - player.Y)) > 4)
                        {
                            continue;
                        }

                        if (UIManager.GetGump<BaseHealthBarGump>(hb.Serial) == null)
                        {
                            UIManager.Add(custom ? new HealthBarGumpCustom(world, hb) { X = 100, Y = 100 } : new HealthBarGump(world, hb) { X = 100, Y = 100 });
                            custom = !custom;
                        }

                        Log.Info($"[ECS] healthbar 0x{hb.Serial:X8} hits {hb.Hits}/{hb.HitsMax} = {hb.HitsPercentage}% stam {hb.Stamina}/{hb.StaminaMax} mana {hb.Mana}/{hb.ManaMax} {hb.Name}");
                    }

                    return true;
                }

                // w5 on|off: the W5 shortcut that still has a switch (text_skip_expired)
                case "w5" when args.Length > 0:
                    Settings.GlobalSettings.TextSkipExpired = args[0] == "on";
                    Log.Info($"[ECS] driver: W5 shortcuts {args[0]}");

                    return true;

                // T9: switches whose off path is deleted; older session scripts still name them, so they are accepted and ignored
                case "mobcache":
                case "pushinputs":
                case "readcontents":
                case "readequipment":
                case "layerrules":
                    Log.Info($"[ECS] driver: '{cmd}' has no off path any more (T9), ignored");

                    return false;

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

                // overhead: the player's overhead texts, oldest first, walked both ways (linked-object: TextContainer's own list),
                // and the effect list's length both ways
                case "overhead":
                {
                    TextContainer tc = player.TextContainer;
                    int fwd = 0, back = 0;
                    TextObject last = null;
                    var sb = new System.Text.StringBuilder();

                    for (TextObject t = tc?.Items; t != null; t = t.Next)
                    {
                        fwd++;
                        last = t;
                        sb.Append(" | ").Append(t.RenderedText?.Text);
                    }

                    for (TextObject t = last; t != null; t = t.Previous)
                    {
                        back++;
                    }

                    int fxFwd = 0, fxBack = 0;
                    GameEffect lastFx = null;

                    for (GameEffect f = world.Effects.Items; f != null; f = f.Next)
                    {
                        fxFwd++;
                        lastFx = f;
                    }

                    for (GameEffect f = lastFx; f != null; f = f.Previous)
                    {
                        fxBack++;
                    }

                    Log.Info($"[ECS] overhead size {tc?.Size ?? 0} max {tc?.MaxSize ?? 0} linked {fwd}/{back} effects {fxFwd}/{fxBack}:{sb}");

                    return false;
                }

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

                // golden <name> [frames]: T9 golden capture (GoldenCapture, CUO_GOLDEN_DIR): the equipment reads and contents
                // now, the world's sprites on the next <frames> frames (1) of the current renderer
                case "golden" when args.Length > 0:
                {
                    int gf = args.Length > 1 && int.TryParse(args[1], out int gn) ? gn : 1;

                    if (!GoldenCapture.Request(world, args[0], gf))
                    {
                        Log.Info($"[ECS] driver: golden {arg} skipped (CUO_GOLDEN_DIR is not set)");

                        return false;
                    }

                    return true;
                }

                // goldenmobiles on|off: off leaves mobiles out of the world draw (GoldenCapture.HideMobiles), for world shots of
                // a scene whose NPCs turn on their own
                case "goldenmobiles" when args.Length > 0:
                    GoldenCapture.HideMobiles = args[0] == "off";
                    Log.Info($"[ECS] driver: goldenmobiles {args[0]}");

                    return false;

                // logout: drop the socket, then swap to the login scene (World.Clear) and exit; log back in with a new process
                case "logout":
                    ReleaseMouse();
                    NetClient.Socket.Disconnect();
                    _logoutAt = Time.Ticks + 2000;

                    return true;

                case "quit":
                    ReleaseMouse();
                    world.Ecs.RunCheck(true);
                    Scenes.SpritePickTable.Summary();
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
                else
                {
                    _driver.ReleaseMouse(); // disconnected: the driver no longer advances, so its cursor must not stay
                }
            }

            public override void Dispose()
            {
                _driver.ReleaseMouse();
                base.Dispose();
            }
        }

        private static uint FindWorn(PlayerMobile player)
        {
            foreach (Item it in player.World.State.Children(player))
            {
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
            if (container == null)
            {
                return 0;
            }

            foreach (Item it in container.World.State.Children(container))
            {
                if (it.Graphic == graphic)
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
                // RootContainer is the top container's serial (bench-000098: a reference comparison never matched, so the
                // cleanup's target mine found nothing)
                if (it.Graphic == graphic && !it.IsDestroyed && it.RootContainer == player.Serial)
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

            WorldState state = backpack.World.State;

            foreach (Item it in state.Children(backpack))
            {
                if (!state.HasChildren(it) && !it.ItemData.IsContainer)
                {
                    return it.Serial;
                }
            }

            return 0;
        }
    }
}

#endif
