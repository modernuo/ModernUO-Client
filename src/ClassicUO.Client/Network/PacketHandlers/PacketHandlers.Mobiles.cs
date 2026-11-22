// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace ClassicUO.Network
{
    sealed partial class PacketHandlers
    {
        private static void Damage(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            Entity entity = world.Get(p.ReadUInt32());

            if (entity != null)
            {
                ushort damage = p.ReadUInt16();

                if (damage > 0)
                {
                    world.WorldTextManager.AddDamage(entity, damage);
                }
            }
        }

        private static void CharacterStatus(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            Entity entity = world.Get(serial);

            if (entity == null)
            {
                return;
            }

            string oldName = entity.Name;
            string name = p.ReadCp1252(30);
            ushort hits = p.ReadUInt16();
            ushort hitsMax = p.ReadUInt16();

            // T1 (funnel part 6): the name, the hits and the hits request
            world.Writer.SetStatus(entity, name, hits, hitsMax);

            if (SerialHelper.IsMobile(serial))
            {
                Mobile mobile = entity as Mobile;

                if (mobile == null)
                {
                    return;
                }

                bool renamable = p.ReadBoolean();
                byte type = p.ReadByte();
                bool extended = type > 0 && p.Position + 1 <= p.Length;

                // IsRenamable, then (the extended part) IsFemale, an input
                world.Writer.SetMobileStatus(mobile, renamable, extended, extended && p.ReadBoolean());

                if (extended)
                {
                    if (mobile == world.Player)
                    {
                        if (
                            !string.IsNullOrEmpty(world.Player.Name) && oldName != world.Player.Name
                        )
                        {
                            Client.Game.SetWindowTitle(world.Player.Name);
                        }

                        ushort str = p.ReadUInt16();
                        ushort dex = p.ReadUInt16();
                        ushort intell = p.ReadUInt16();
                        ushort stamina = p.ReadUInt16();
                        ushort staminaMax = p.ReadUInt16();
                        ushort mana = p.ReadUInt16();
                        ushort manaMax = p.ReadUInt16();
                        world.Writer.SetStamina(world.Player, staminaMax, stamina);
                        world.Writer.SetMana(world.Player, manaMax, mana);
                        world.PlayerState.Gold = p.ReadUInt32();
                        world.PlayerState.PhysicalResistance = (short)p.ReadUInt16();
                        world.PlayerState.Weight = p.ReadUInt16();

                        if (
                            world.PlayerState.Strength != 0
                            && ProfileManager.CurrentProfile != null
                            && ProfileManager.CurrentProfile.ShowStatsChangedMessage
                        )
                        {
                            ushort currentStr = world.PlayerState.Strength;
                            ushort currentDex = world.PlayerState.Dexterity;
                            ushort currentInt = world.PlayerState.Intelligence;

                            int deltaStr = str - currentStr;
                            int deltaDex = dex - currentDex;
                            int deltaInt = intell - currentInt;

                            if (deltaStr != 0)
                            {
                                GameActions.Print(
                                    world,
                                    string.Format(
                                        ResGeneral.Your0HasChangedBy1ItIsNow2,
                                        ResGeneral.Strength,
                                        deltaStr,
                                        str
                                    ),
                                    0x0170,
                                    MessageType.System,
                                    3,
                                    false
                                );
                            }

                            if (deltaDex != 0)
                            {
                                GameActions.Print(
                                    world,
                                    string.Format(
                                        ResGeneral.Your0HasChangedBy1ItIsNow2,
                                        ResGeneral.Dexterity,
                                        deltaDex,
                                        dex
                                    ),
                                    0x0170,
                                    MessageType.System,
                                    3,
                                    false
                                );
                            }

                            if (deltaInt != 0)
                            {
                                GameActions.Print(
                                    world,
                                    string.Format(
                                        ResGeneral.Your0HasChangedBy1ItIsNow2,
                                        ResGeneral.Intelligence,
                                        deltaInt,
                                        intell
                                    ),
                                    0x0170,
                                    MessageType.System,
                                    3,
                                    false
                                );
                            }
                        }

                        world.PlayerState.Strength = str;
                        world.PlayerState.Dexterity = dex;
                        world.PlayerState.Intelligence = intell;

                        if (type >= 5) //ML
                        {
                            world.PlayerState.WeightMax = p.ReadUInt16();
                            byte race = p.ReadByte();

                            if (race == 0)
                            {
                                race = 1;
                            }

                            world.Writer.SetRace(world.Player, (RaceType)race);
                        }
                        else
                        {
                            if (Client.Game.UO.Version >= Utility.ClientVersion.CV_500A)
                            {
                                world.PlayerState.WeightMax = (ushort)(
                                    7 * (world.PlayerState.Strength >> 1) + 40
                                );
                            }
                            else
                            {
                                world.PlayerState.WeightMax = (ushort)(world.PlayerState.Strength * 4 + 25);
                            }
                        }

                        if (type >= 3) //Renaissance
                        {
                            world.PlayerState.StatsCap = (short)p.ReadUInt16();
                            world.PlayerState.Followers = p.ReadByte();
                            world.PlayerState.FollowersMax = p.ReadByte();
                        }

                        if (type >= 4) //AOS
                        {
                            world.PlayerState.FireResistance = (short)p.ReadUInt16();
                            world.PlayerState.ColdResistance = (short)p.ReadUInt16();
                            world.PlayerState.PoisonResistance = (short)p.ReadUInt16();
                            world.PlayerState.EnergyResistance = (short)p.ReadUInt16();
                            world.PlayerState.Luck = p.ReadUInt16();
                            world.PlayerState.DamageMin = (short)p.ReadUInt16();
                            world.PlayerState.DamageMax = (short)p.ReadUInt16();
                            world.PlayerState.TithingPoints = p.ReadUInt32();
                        }

                        if (type >= 6)
                        {
                            world.PlayerState.MaxPhysicResistence =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.MaxFireResistence =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.MaxColdResistence =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.MaxPoisonResistence =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.MaxEnergyResistence =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.DefenseChanceIncrease =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.MaxDefenseChanceIncrease =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.HitChanceIncrease =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.SwingSpeedIncrease =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.DamageIncrease =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.LowerReagentCost =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.SpellDamageIncrease =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.FasterCastRecovery =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.FasterCasting =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                            world.PlayerState.LowerManaCost =
                                p.Position + 2 > p.Length ? (short)0 : (short)p.ReadUInt16();
                        }
                    }
                }

                if (mobile == world.Player)
                {
                    world.UoAssist.SignalHits();
                    world.UoAssist.SignalStamina();
                    world.UoAssist.SignalMana();
                }
            }
        }

        private static void NewHealthbarUpdate(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            if (p[0] == 0x16 && Client.Game.UO.Version < Utility.ClientVersion.CV_500A)
            {
                return;
            }

            Mobile mobile = world.FindMobile(p.ReadUInt32());

            if (mobile == null)
            {
                return;
            }

            ushort count = p.ReadUInt16();

            // T1 (funnel part 6): the list folds into the final flags and SA poison (before T1 each entry was a write, then
            // a Sync), written by one operation
            Flags flags = mobile.Flags;
            bool setSAPoison = false, saPoison = false;

            for (int i = 0; i < count; i++)
            {
                ushort type = p.ReadUInt16();
                bool enabled = p.ReadBoolean();

                if (type == 1)
                {
                    if (Client.Game.UO.Version >= Utility.ClientVersion.CV_7000)
                    {
                        setSAPoison = true;
                        saPoison = enabled;
                    }
                    else if (enabled)
                    {
                        flags |= Flags.Poisoned;
                    }
                    else
                    {
                        flags &= ~Flags.Poisoned;
                    }
                }
                else if (type == 2)
                {
                    if (enabled)
                    {
                        flags |= Flags.YellowBar;
                    }
                    else
                    {
                        flags &= ~Flags.YellowBar;
                    }
                }
                else if (type == 3)
                {
                    // ???
                }
            }

            world.Writer.SetHealthbar(mobile, flags, setSAPoison, saPoison);
        }

        private static void DeathScreen(World world, ref NetReader p)
        {
            // todo
            byte action = p.ReadByte();

            if (action != 1)
            {
                world.Weather.Reset();

                Client.Game.Audio.PlayMusic(Client.Game.Audio.DeathMusicIndex, true);

                if (ProfileManager.CurrentProfile.EnableDeathScreen)
                {
                    world.PlayerState.DeathScreenTimer = Time.Ticks + Constants.DEATH_SCREEN_TIMER;
                }

                GameActions.RequestWarMode(world.Player, false);
            }
        }

        private static void MobileAttributes(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();

            Entity entity = world.Get(serial);

            if (entity == null)
            {
                return;
            }

            ushort hitsMax = p.ReadUInt16();
            ushort hits = p.ReadUInt16();

            // T1 (funnel part 6)
            world.Writer.SetHits(entity, hitsMax, hits);

            if (SerialHelper.IsMobile(serial))
            {
                Mobile mobile = entity as Mobile;

                if (mobile == null)
                {
                    return;
                }

                ushort manaMax = p.ReadUInt16();
                ushort mana = p.ReadUInt16();
                ushort staminaMax = p.ReadUInt16();
                ushort stamina = p.ReadUInt16();
                world.Writer.SetMana(mobile, manaMax, mana);
                world.Writer.SetStamina(mobile, staminaMax, stamina);

                if (mobile == world.Player)
                {
                    world.UoAssist.SignalHits();
                    world.UoAssist.SignalStamina();
                    world.UoAssist.SignalMana();
                }
            }
        }

        private static void Swing(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            p.Skip(1);

            uint attackers = p.ReadUInt32();

            if (attackers != world.Player)
            {
                return;
            }

            uint defenders = p.ReadUInt32();

            const int TIME_TURN_TO_LASTTARGET = 2000;

            if (
                world.TargetManager.LastAttack == defenders
                && world.Player.InWarMode
                && world.Player.Walker.LastStepRequestTime + TIME_TURN_TO_LASTTARGET < Time.Ticks
                && world.Player.Steps.Count == 0
            )
            {
                Mobile enemy = world.FindMobile(defenders);

                if (enemy != null)
                {
                    Direction pdir = DirectionHelper.GetDirectionAB(
                        world.Player.X,
                        world.Player.Y,
                        enemy.X,
                        enemy.Y
                    );

                    int x = world.Player.X;
                    int y = world.Player.Y;
                    sbyte z = world.Player.Z;

                    if (
                        world.Player.Pathfinder.CanWalk(ref pdir, ref x, ref y, ref z)
                        && world.Player.Direction != pdir
                    )
                    {
                        world.Player.Walk(pdir, false);
                    }
                }
            }
        }

        private static void Unknown_0x32(World world, ref NetReader p) { }

        private static void UpdateSkills(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            byte type = p.ReadByte();
            bool haveCap = type != 0u && type <= 0x03 || type == 0xDF;
            bool isSingleUpdate = type == 0xFF || type == 0xDF;

            if (type == 0xFE)
            {
                int count = p.ReadUInt16();

                Client.Game.UO.FileManager.Skills.Skills.Clear();
                Client.Game.UO.FileManager.Skills.SortedSkills.Clear();

                for (int i = 0; i < count; i++)
                {
                    bool haveButton = p.ReadBoolean();
                    int nameLength = p.ReadByte();

                    Client.Game.UO.FileManager.Skills.Skills.Add(
                        new SkillEntry(i, p.ReadCp1252(nameLength), haveButton)
                    );
                }

                Client.Game.UO.FileManager.Skills.SortedSkills.AddRange(Client.Game.UO.FileManager.Skills.Skills);

                Client.Game.UO.FileManager.Skills.SortedSkills.Sort(
                    (a, b) => string.Compare(a.Name, b.Name, StringComparison.InvariantCulture)
                );
            }
            else
            {
                StandardSkillsGump standard = null;
                SkillGumpAdvanced advanced = null;

                if (ProfileManager.CurrentProfile.StandardSkillsGump)
                {
                    standard = UIManager.GetGump<StandardSkillsGump>();
                }
                else
                {
                    advanced = UIManager.GetGump<SkillGumpAdvanced>();
                }

                if (!isSingleUpdate && (type == 1 || type == 3 || world.SkillsRequested))
                {
                    world.SkillsRequested = false;

                    // TODO: make a base class for this gump
                    if (ProfileManager.CurrentProfile.StandardSkillsGump)
                    {
                        if (standard == null)
                        {
                            UIManager.Add(standard = new StandardSkillsGump(world) { X = 100, Y = 100 });
                        }
                    }
                    else
                    {
                        if (advanced == null)
                        {
                            UIManager.Add(advanced = new SkillGumpAdvanced(world) { X = 100, Y = 100 });
                        }
                    }
                }

                while (p.Position < p.Length)
                {
                    ushort id = p.ReadUInt16();

                    if (p.Position >= p.Length)
                    {
                        break;
                    }

                    if (id == 0 && type == 0)
                    {
                        break;
                    }

                    if (type == 0 || type == 0x02)
                    {
                        id--;
                    }

                    ushort realVal = p.ReadUInt16();
                    ushort baseVal = p.ReadUInt16();
                    Lock locked = (Lock)p.ReadByte();
                    ushort cap = 1000;

                    if (haveCap)
                    {
                        cap = p.ReadUInt16();
                    }

                    if (id < world.PlayerState.Skills.Length)
                    {
                        Skill skill = world.PlayerState.Skills[id];

                        if (skill != null)
                        {
                            if (isSingleUpdate)
                            {
                                float change = realVal / 10.0f - skill.Value;

                                if (
                                    change != 0.0f
                                    && !float.IsNaN(change)
                                    && ProfileManager.CurrentProfile != null
                                    && ProfileManager.CurrentProfile.ShowSkillsChangedMessage
                                    && Math.Abs(change * 10)
                                        >= ProfileManager.CurrentProfile.ShowSkillsChangedDeltaValue
                                )
                                {
                                    GameActions.Print(
                                        world,
                                        string.Format(
                                            ResGeneral.YourSkillIn0Has1By2ItIsNow3,
                                            skill.Name,
                                            change < 0
                                                ? ResGeneral.Decreased
                                                : ResGeneral.Increased,
                                            Math.Abs(change),
                                            skill.Value + change
                                        ),
                                        0x58,
                                        MessageType.System,
                                        3,
                                        false
                                    );
                                }
                            }

                            skill.BaseFixed = baseVal;
                            skill.ValueFixed = realVal;
                            skill.CapFixed = cap;
                            skill.Lock = locked;

                            standard?.Update(id);
                            advanced?.ForceUpdate();
                        }
                    }

                    if (isSingleUpdate)
                    {
                        break;
                    }
                }
            }
        }

        private static void CharacterAnimation(World world, ref NetReader p)
        {
            Mobile mobile = world.FindMobile(p.ReadUInt32());

            if (mobile == null)
            {
                return;
            }

            ushort action = p.ReadUInt16();
            ushort frame_count = p.ReadUInt16();
            ushort repeat_count = p.ReadUInt16();
            bool forward = !p.ReadBoolean();
            bool repeat = p.ReadBoolean();
            byte delay = p.ReadByte();

            // T1 (funnel part 6)
            world.Writer.AnimateMobile(
                mobile,
                Mobile.GetReplacedObjectAnimation(mobile.Graphic, action),
                delay,
                (byte)frame_count,
                (byte)repeat_count,
                repeat,
                forward,
                true
            );
        }

        private static void Warmode(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            world.Writer.SetWarMode(world.Player, p.ReadBoolean());
        }

        private static void UpdateCharacter(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            Mobile mobile = world.FindMobile(serial);

            if (mobile == null)
            {
                return;
            }

            ushort graphic = p.ReadUInt16();
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            sbyte z = p.ReadSByte();
            Direction direction = (Direction)p.ReadByte();
            ushort hue = p.ReadUInt16();
            Flags flags = (Flags)p.ReadByte();
            NotorietyFlag notoriety = (NotorietyFlag)p.ReadByte();

            // T1: the notoriety of the mobile found above (an input: written, then pushed)
            world.Writer.SetNotoriety(mobile, notoriety);

            if (serial == world.Player)
            {
                // T1: graphic, CheckGraphicChange's sex and race, hue, flags (ECS and GameObject)
                world.Writer.SetMobileBody(mobile, graphic, hue, flags, checkBody: true, setDirection: false, 0);
                // TODO: x,y,z, direction cause elastic effect, ignore 'em for the moment
            }
            else
            {
                UpdateGameObject(world, serial, graphic, 0, 0, x, y, z, direction, hue, flags, 0, 1, 1);
            }
        }

        // t3a-fold: 0x78/0xD3's equipment list, read before the operation (grown when a list is longer; the game loop's thread)
        private static WorldWriter.WornEntry[] _wornEntries = new WorldWriter.WornEntry[32];

        private static void UpdateObject(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            bool wornHue = PacketLayouts.Resolve(ref p, PacketLayouts.WornList(p.Buffer, Client.Game.UO.Version),
                Client.Game.UO.Version >= Utility.ClientVersion.CV_70331 ? PacketLayouts.WornAlwaysHue : PacketLayouts.WornFlagHue)
                == PacketLayouts.WornAlwaysHue;
            uint serial = p.ReadUInt32();
            ushort graphic = p.ReadUInt16();
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            sbyte z = p.ReadSByte();
            Direction direction = (Direction)p.ReadByte();
            ushort hue = p.ReadUInt16();
            Flags flags = (Flags)p.ReadByte();
            NotorietyFlag notoriety = (NotorietyFlag)p.ReadByte();
            bool oldDead = false;
            //bool alreadyExists =world.Get(serial) != null;

            if (serial == world.Player)
            {
                oldDead = world.Player.IsDead;
                // T1: graphic, CheckGraphicChange's sex and race, hue, flags (ECS and GameObject)
                world.Writer.SetMobileBody(world.Player, graphic, hue, flags, checkBody: true, setDirection: false, 0);
            }
            else
            {
                UpdateGameObject(world, serial, graphic, 0, 0, x, y, z, direction, hue, flags, 0, 0, 1);
            }

            Entity obj = world.Get(serial);

            if (obj == null)
            {
                return;
            }

            // t3a-fold: the clear of the children that are neither opened nor the backpack (with their contents) runs with the
            // list in ReplaceWornItems below; the notoriety and the paperdoll's refresh request, which neither reads nor
            // changes the equipment, now come before it
            if (SerialHelper.IsMobile(serial) && obj is Mobile mob)
            {
                world.Writer.SetNotoriety(mob, notoriety);

                UIManager.GetGump<PaperDollGump>(serial)?.RequestUpdateContents();
            }

            if (p[0] != 0x78)
            {
                p.Skip(6);
            }

            // the layout check accepts a packet that ends after the header: an empty list, as with the terminator
            uint itemSerial = p.Position + 4 <= p.Length ? p.ReadUInt32() : 0;
            int entryCount = 0;

            // t3a-fold: the list is read first (before any change), then cleared and worn in one operation
            while (itemSerial != 0 && p.Position < p.Length)
            {
                //if (!SerialHelper.IsItem(itemSerial))
                //    break;

                ushort itemGraphic = p.ReadUInt16();
                byte layer = p.ReadByte();
                ushort item_hue = 0;

                if (wornHue)
                {
                    item_hue = p.ReadUInt16();
                }
                else if ((itemGraphic & 0x8000) != 0)
                {
                    itemGraphic &= 0x7FFF;
                    item_hue = p.ReadUInt16();
                }

                if (entryCount == _wornEntries.Length)
                {
                    Array.Resize(ref _wornEntries, entryCount * 2);
                }

                _wornEntries[entryCount++] = new WorldWriter.WornEntry(itemSerial, itemGraphic, (Layer)layer, item_hue);

                // a list without the zero serial ends at the packet's end
                itemSerial = p.Remaining > 0 ? p.ReadUInt32() : 0;
            }

            // T1: the clear, then each entry's values, out of its old container, onto obj (captured once, before the list) at
            // the end of its child list; t3a-fold: as one operation, the wearer's WornLayers written once
            world.Writer.ReplaceWornItems(obj, new ReadOnlySpan<WorldWriter.WornEntry>(_wornEntries, 0, entryCount));

            if (serial == world.Player)
            {
                if (oldDead != world.Player.IsDead)
                {
                    if (world.Player.IsDead)
                    {
                        // NOTE: This packet causes some weird issue on sphere servers.
                        //       When the character dies, this packet trigger a "reset" and
                        //       somehow it messes up the packet reading server side
                        //NetClient.Socket.Send_DeathScreen();
                        world.ChangeSeason(Game.Managers.Season.Desolation, 42);
                    }
                    else
                    {
                        world.ChangeSeason(world.OldSeason, world.OldMusicIndex);
                    }
                }

                UIManager.GetGump<PaperDollGump>(serial)?.RequestUpdateContents();

                world.Player.UpdateAbilities();
            }
        }

        private static void UpdateName(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            string name = p.ReadCp1252();

            WMapEntity wme = world.WMapManager.GetEntity(serial);

            if (wme != null && !string.IsNullOrEmpty(name))
            {
                wme.Name = name;
            }

            Entity entity = world.Get(serial);

            if (entity != null)
            {
                world.Writer.SetName(entity, name);

                if (
                    serial == world.Player.Serial
                    && !string.IsNullOrEmpty(name)
                    && name != world.Player.Name
                )
                {
                    Client.Game.SetWindowTitle(name);
                }

                UIManager.GetGump<NameOverheadGump>(serial)?.SetName();
            }
        }

        private static void UpdateHitpoints(World world, ref NetReader p)
        {
            Entity entity = world.Get(p.ReadUInt32());

            if (entity == null)
            {
                return;
            }

            ushort hitsMax = p.ReadUInt16();
            ushort hits = p.ReadUInt16();

            // T1 (funnel part 6)
            world.Writer.SetHits(entity, hitsMax, hits);

            if (entity == world.Player)
            {
                world.UoAssist.SignalHits();
            }
        }

        private static void UpdateMana(World world, ref NetReader p)
        {
            Mobile mobile = world.FindMobile(p.ReadUInt32());

            if (mobile == null)
            {
                return;
            }

            ushort manaMax = p.ReadUInt16();
            world.Writer.SetMana(mobile, manaMax, p.ReadUInt16());

            if (mobile == world.Player)
            {
                world.UoAssist.SignalMana();
            }
        }

        private static void UpdateStamina(World world, ref NetReader p)
        {
            Mobile mobile = world.FindMobile(p.ReadUInt32());

            if (mobile == null)
            {
                return;
            }

            ushort staminaMax = p.ReadUInt16();
            world.Writer.SetStamina(mobile, staminaMax, p.ReadUInt16());

            if (mobile == world.Player)
            {
                world.UoAssist.SignalStamina();
            }
        }

        private static void AttackCharacter(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();

            //if (TargetManager.LastAttack != serial && World.InGame)
            //{



            //}

            GameActions.SendCloseStatus(world, world.TargetManager.LastAttack);
            world.TargetManager.LastAttack = serial;
            GameActions.RequestMobileStatus(world, serial);
        }

        private static void DisplayDeath(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            uint corpseSerial = p.ReadUInt32();
            uint running = p.ReadUInt32();

            Mobile owner = world.FindMobile(serial);

            if (owner == null || serial == world.Player)
            {
                return;
            }

            serial |= 0x80000000;

            // T1 (funnel part 7): the re-key to serial | 0x80000000, its children's Container and the mirror's re-key
            world.Writer.RekeyDeadMobile(owner);

            if (SerialHelper.IsValid(corpseSerial))
            {
                world.CorpseManager.Add(corpseSerial, serial, owner.Direction, running != 0);
            }

            var animations = Client.Game.UO.Animations;
            var gfx = owner.Graphic;
            animations.ConvertBodyIfNeeded(ref gfx);
            var animGroup = animations.GetAnimType(gfx);
            var animFlags = animations.GetAnimFlags(gfx);
            byte group = Client.Game.UO.FileManager.Animations.GetDeathAction(
                gfx,
                animFlags,
                animGroup,
                running != 0,
                true
            );
            // T1 (funnel part 6): the death animation from its first frame
            world.Writer.AnimateMobile(owner, group, 0, 5, 1, frame: 0);

            if (ProfileManager.CurrentProfile.AutoOpenCorpses)
            {
                world.Player.TryOpenCorpses();
            }
        }

        private static void Semivisible(World world, ref NetReader p) { }

        private static void UpdateMobileStatus(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();
            byte status = p.ReadByte();

            if (status == 1)
            {
                uint attackerSerial = p.ReadUInt32();
            }
        }

        private static void NewCharacterAnimation(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            Mobile mobile = world.FindMobile(p.ReadUInt32());

            if (mobile == null)
            {
                return;
            }

            ushort type = p.ReadUInt16();
            ushort action = p.ReadUInt16();
            byte mode = p.ReadByte();
            byte group = Mobile.GetObjectNewAnimation(mobile, type, action, mode);

            // T1 (funnel part 6)
            world.Writer.AnimateMobile(
                mobile,
                group,
                repeatCount: 1,
                repeat: (type == 1 || type == 2) && mobile.Graphic == 0x0015,
                forward: true,
                fromServer: true
            );
        }
    }
}
