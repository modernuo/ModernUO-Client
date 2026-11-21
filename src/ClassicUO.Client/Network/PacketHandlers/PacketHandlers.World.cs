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
        private static void PersonalLightLevel(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            if (world.Player == p.ReadUInt32())
            {
                byte level = p.ReadByte();

                if (level > 0x1E)
                {
                    level = 0x1E;
                }

                world.Light.RealPersonal = level;

                if (!ProfileManager.CurrentProfile.UseCustomLightLevel)
                {
                    world.Light.Personal = level;
                }
            }
        }

        private static void LightLevel(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            byte level = p.ReadByte();

            if (level > 0x1E)
            {
                level = 0x1E;
            }

            world.Light.RealOverall = level;

            if (
                !ProfileManager.CurrentProfile.UseCustomLightLevel
                || ProfileManager.CurrentProfile.LightLevelType == 1
            )
            {
                world.Light.Overall =
                    ProfileManager.CurrentProfile.LightLevelType == 1
                        ? Math.Min(level, ProfileManager.CurrentProfile.LightLevel)
                        : level;
            }
        }

        private static void PlaySoundEffect(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            p.Skip(1);

            ushort index = p.ReadUInt16();
            ushort audio = p.ReadUInt16();
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            short z = (short)p.ReadUInt16();

            Client.Game.Audio.PlaySoundWithDistance(world, index, x, y);
        }

        private static void PlayMusic(World world, ref NetReader p)
        {
            if (p.Length == 3) // Play Midi Music packet (0x6D, 0x10, index)
            {
                byte cmd = p.ReadByte();
                byte index = p.ReadByte();

                // Check for stop music packet (6D 1F FF)
                if (cmd == 0x1F && index == 0xFF)
                {
                    Client.Game.Audio.StopMusic();
                }
                else
                {
                    Client.Game.Audio.PlayMusic(index);
                }
            }
            else
            {
                ushort index = p.ReadUInt16();
                Client.Game.Audio.PlayMusic(index);
            }
        }

        private static void MapData(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();

            MapGump gump = UIManager.GetGump<MapGump>(serial);

            if (gump != null)
            {
                switch ((MapMessageType)p.ReadByte())
                {
                    case MapMessageType.Add:
                        p.Skip(1);

                        ushort x = p.ReadUInt16();
                        ushort y = p.ReadUInt16();

                        gump.AddPin(x, y);

                        break;

                    case MapMessageType.Insert:
                        break;
                    case MapMessageType.Move:
                        break;
                    case MapMessageType.Remove:
                        break;

                    case MapMessageType.Clear:
                        gump.ClearContainer();

                        break;

                    case MapMessageType.Edit:
                        break;

                    case MapMessageType.EditResponse:
                        gump.SetPlotState(p.ReadByte());

                        break;
                }
            }
        }

        private static void SetTime(World world, ref NetReader p) { }

        private static void SetWeather(World world, ref NetReader p)
        {
            GameScene scene = Client.Game.GetScene<GameScene>();

            if (scene == null)
            {
                return;
            }

            WeatherType type = (WeatherType)p.ReadByte();

            if (world.Weather.CurrentWeather != type)
            {
                byte count = p.ReadByte();
                byte temp = p.ReadByte();

                world.Weather.Generate(type, count, temp);
            }
        }

        private static void ClientViewRange(World world, ref NetReader p)
        {
            world.ClientViewRange = p.ReadByte();
        }

        private static void Season(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            byte season = p.ReadByte();
            byte music = p.ReadByte();

            if (season > 4)
            {
                season = 0;
            }

            if (world.Player.IsDead && season == 4)
            {
                return;
            }

            world.OldSeason = (Season)season;
            world.OldMusicIndex = music;

            if (world.Season == Game.Managers.Season.Desolation)
            {
                world.OldMusicIndex = 42;
            }

            world.ChangeSeason((Season)season, music);
        }

        private static void InvalidMapEnable(World world, ref NetReader p) { }
    }
}
