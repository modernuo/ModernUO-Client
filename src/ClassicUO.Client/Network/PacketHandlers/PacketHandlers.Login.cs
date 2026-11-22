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
        private static void EnterWorld(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();

            world.CreatePlayer(serial);

            p.Skip(4);
            // T1 (funnel part 5): the body graphic and CheckGraphicChange's sex and race (ECS and GameObject)
            world.Writer.SetMobileGraphic(world.Player, p.ReadUInt16(), checkBody: true);
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            sbyte z = (sbyte)p.ReadUInt16();

            if (world.Map == null)
            {
                world.MapIndex = 0;
            }

            world.Map?.Prefetch(x, y);
            // T1: the position and the tile, with the player's position callbacks; then the direction (its door check)
            world.Writer.MoveMobile(world.Player, x, y, z);
            world.Writer.SetMobileDirection(world.Player, (Direction)(p.ReadByte() & 0x7));
            world.RangeSize.X = x;
            world.RangeSize.Y = y;

            if (
                ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.UseCustomLightLevel
            )
            {
                world.Light.Overall =
                    ProfileManager.CurrentProfile.LightLevelType == 1
                        ? Math.Min(world.Light.Overall, ProfileManager.CurrentProfile.LightLevel)
                        : ProfileManager.CurrentProfile.LightLevel;
            }

            Client.Game.Audio.UpdateCurrentMusicVolume();

            if (Client.Game.UO.Version >= Utility.ClientVersion.CV_200)
            {
                if (ProfileManager.CurrentProfile != null)
                {
                    NetClient.Socket.Send_GameWindowSize(
                        (uint)Client.Game.Scene.Camera.Bounds.Width,
                        (uint)Client.Game.Scene.Camera.Bounds.Height
                    );
                }

                NetClient.Socket.Send_Language(Settings.GlobalSettings.Language);
            }

            NetClient.Socket.Send_ClientVersion(Client.Game.UO.VersionText);

            GameActions.SingleClick(world, world.Player);
            NetClient.Socket.Send_SkillsRequest(world.Player.Serial);

            if (world.Player.IsDead)
            {
                world.ChangeSeason(Game.Managers.Season.Desolation, 42);
            }

            if (
                Client.Game.UO.Version >= Utility.ClientVersion.CV_70796
                && ProfileManager.CurrentProfile != null
            )
            {
                NetClient.Socket.Send_ShowPublicHouseContent(
                    ProfileManager.CurrentProfile.ShowHouseContent
                );
            }
        }

        private static void LoginComplete(World world, ref NetReader p)
        {
            if (world.Player != null && Client.Game.Scene is LoginScene)
            {
                var scene = new GameScene(world);
                Client.Game.SetScene(scene);

                //GameActions.OpenPaperdoll(world.Player);
                GameActions.RequestMobileStatus(world, world.Player);
                NetClient.Socket.Send_OpenChat("");

                NetClient.Socket.Send_SkillsRequest(world.Player);
                scene.DoubleClickDelayed(world.Player);

                if (Client.Game.UO.Version >= Utility.ClientVersion.CV_306E)
                {
                    NetClient.Socket.Send_ClientType();
                }

                if (Client.Game.UO.Version >= Utility.ClientVersion.CV_305D)
                {
                    NetClient.Socket.Send_ClientViewRange(world.ClientViewRange);
                }

                List<Gump> gumps = ProfileManager.CurrentProfile.ReadGumps(
                    world,
                    ProfileManager.ProfilePath
                );

                if (gumps != null)
                {
                    foreach (Gump gump in gumps)
                    {
                        UIManager.Add(gump);
                    }
                }
            }
        }

        private static void Ping(World world, ref NetReader p)
        {
            NetClient.Socket.Statistics.PingReceived(p.ReadByte());
        }

        private static void EnableLockedFeatures(World world, ref NetReader p)
        {
            LockedFeatureFlags flags = 0;
            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.LockedFeatures(p.Buffer),
                Client.Game.UO.Version >= Utility.ClientVersion.CV_60142 ? PacketLayouts.Features32 : PacketLayouts.Features16);

            if (layout == PacketLayouts.Features32)
            {
                flags = (LockedFeatureFlags)p.ReadUInt32();
            }
            else
            {
                flags = (LockedFeatureFlags)p.ReadUInt16();
            }

            world.ClientLockedFeatures.SetFlags(flags);

            world.ChatManager.ChatIsEnabled = world.ClientLockedFeatures.Flags.HasFlag(
                LockedFeatureFlags.T2A
            )
                ? ChatStatus.Enabled
                : 0;

            // Body art does not depend on these flags: the pack holds the body set chosen at import (bodies.json).
        }

        private static void ClientVersion(World world, ref NetReader p)
        {
            NetClient.Socket.Send_ClientVersion(Client.Game.UO.VersionText);
        }

        private static void AssistVersion(World world, ref NetReader p)
        {
            //uint version = p.ReadUInt32();

            //string[] parts = Service.GetByLocalSerial<Settings>().ClientVersion.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            //byte[] clientVersionBuffer =
            //    {byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]), byte.Parse(parts[3])};

            //NetClient.Socket.Send(new PAssistVersion(clientVersionBuffer, version));
        }

        private static void GetUserServerPingGodClientR(World world, ref NetReader p) { }

        private static void GlobalQueCount(World world, ref NetReader p) { }

        private static void ConfigurationFileR(World world, ref NetReader p) { }

        private static void Logout(World world, ref NetReader p)
        {
            // http://docs.polserver.com/packets/index.php?Packet=0xD1

            if (
                Client.Game.GetScene<GameScene>().DisconnectionRequested
                && (
                    world.ClientFeatures.Flags
                    & CharacterListFlags.CLF_OWERWRITE_CONFIGURATION_BUTTON
                ) != 0
            )
            {
                if (p.ReadBoolean())
                {
                    // client can disconnect
                    NetClient.Socket.Disconnect();
                    Client.Game.SetScene(new LoginScene(world));
                }
                else
                {
                    Log.Warn("0x1D - client asked to disconnect but server answered 'NO!'");
                }
            }
        }

        private static void CharacterTransferLog(World world, ref NetReader p) { }

        private static void KREncryptionResponse(World world, ref NetReader p) { }

        private static void ServerListReceived(World world, ref NetReader p)
        {
            if (world.InGame)
            {
                return;
            }

            LoginScene scene = Client.Game.GetScene<LoginScene>();

            if (scene != null)
            {
                scene.ServerListReceived(ref p);
            }
        }

        private static void ReceiveServerRelay(World world, ref NetReader p)
        {
            if (world.InGame)
            {
                return;
            }

            LoginScene scene = Client.Game.GetScene<LoginScene>();

            if (scene != null)
            {
                scene.HandleRelayServerPacket(ref p);
            }
        }

        private static void UpdateCharacterList(World world, ref NetReader p)
        {
            if (world.InGame)
            {
                return;
            }

            LoginScene scene = Client.Game.GetScene<LoginScene>();

            if (scene != null)
            {
                scene.UpdateCharacterList(ref p);
            }
        }

        private static void ReceiveCharacterList(World world, ref NetReader p)
        {
            if (world.InGame)
            {
                return;
            }

            LoginScene scene = Client.Game.GetScene<LoginScene>();

            if (scene != null)
            {
                scene.ReceiveCharacterList(ref p);
            }
        }

        private static void LoginDelay(World world, ref NetReader p)
        {
            if (world.InGame)
            {
                return;
            }

            LoginScene scene = Client.Game.GetScene<LoginScene>();

            if (scene != null)
            {
                scene.HandleLoginDelayPacket(ref p);
            }
        }

        private static void ReceiveLoginRejection(World world, ref NetReader p)
        {
            if (world.InGame)
            {
                return;
            }

            LoginScene scene = Client.Game.GetScene<LoginScene>();

            if (scene != null)
            {
                scene.HandleErrorCode(ref p);
            }
        }
    }
}
