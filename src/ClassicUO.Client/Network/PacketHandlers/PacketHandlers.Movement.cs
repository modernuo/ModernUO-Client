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
        private static void FollowR(World world, ref NetReader p)
        {
            uint tofollow = p.ReadUInt32();
            uint isfollowing = p.ReadUInt32();
        }

        private static void UpdatePlayer(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            ushort graphic = p.ReadUInt16();
            byte graphic_inc = p.ReadByte();
            ushort hue = p.ReadUInt16();
            Flags flags = (Flags)p.ReadByte();
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            ushort serverID = p.ReadUInt16();
            Direction direction = (Direction)p.ReadByte();
            sbyte z = p.ReadSByte();

            UpdatePlayer(world, serial, graphic, graphic_inc, hue, flags, x, y, z, serverID, direction);
        }

        private static void DenyWalk(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            byte seq = p.ReadByte();
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            Direction direction = (Direction)p.ReadByte();
            direction &= Direction.Up;
            sbyte z = p.ReadSByte();

            world.Player.Walker.DenyWalk(seq, x, y, z);
            // T1 (funnel part 5): the direction (ECS and GameObject; its door check)
            world.Writer.SetMobileDirection(world.Player, direction);

            world.Weather.Reset();
        }

        private static void ConfirmWalk(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            byte seq = p.ReadByte();
            byte noto = (byte)(p.ReadByte() & ~0x40);

            if (noto == 0 || noto >= 8)
            {
                noto = 0x01;
            }

            // T1 (funnel part 5): the notoriety (ECS inputs and GameObject)
            world.Writer.SetNotoriety(world.Player, (NotorietyFlag)noto);
            world.Player.Walker.ConfirmWalk(seq);

            world.Player.AddToTile();
        }

        private static void Pathfinding(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            ushort z = p.ReadUInt16();

            world.Player.Pathfinder.WalkTo(x, y, z, 0);
        }

        private static void MovePlayer(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            Direction direction = (Direction)p.ReadByte();
            world.Player.Walk(direction & Direction.Mask, (direction & Direction.Running) != 0);
        }

        private static void BoatMoving(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            byte boatSpeed = p.ReadByte();
            Direction movingDirection = (Direction)p.ReadByte() & Direction.Mask;
            Direction facingDirection = (Direction)p.ReadByte() & Direction.Mask;
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            ushort z = p.ReadUInt16();

            Item multi = world.FindItem(serial);

            if (multi == null)
            {
                return;
            }

            //multi.LastX = x;
            //multi.LastY = y;

            //if (World.HouseManager.TryGetHouse(serial, out var house))
            //{
            //    foreach (Multi component in house.Components)
            //    {
            //        component.LastX = (ushort) (x + component.MultiOffsetX);
            //        component.LastY = (ushort) (y + component.MultiOffsetY);
            //    }
            //}

            bool smooth =
                ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.UseSmoothBoatMovement;

            if (smooth)
            {
                world.BoatMovingManager.AddStep(
                    serial,
                    boatSpeed,
                    movingDirection,
                    facingDirection,
                    x,
                    y,
                    (sbyte)z
                );
            }
            else
            {
                //UpdateGameObject(serial,
                //                 multi.Graphic,
                //                 0,
                //                 multi.Amount,
                //                 x,
                //                 y,
                //                 (sbyte) z,
                //                 facingDirection,
                //                 multi.Hue,
                //                 multi.Flags,
                //                 0,
                //                 2,
                //                 1);

                // T1 (funnel part 7)
                world.Writer.JumpBoat(multi, x, y, (sbyte)z);

                if (world.HouseManager.TryGetHouse(serial, out House house))
                {
                    house.Generate(true, true, true);
                }
            }

            int count = p.ReadUInt16();

            for (int i = 0; i < count; i++)
            {
                uint cSerial = p.ReadUInt32();
                ushort cx = p.ReadUInt16();
                ushort cy = p.ReadUInt16();
                ushort cz = p.ReadUInt16();

                if (cSerial == world.Player)
                {
                    world.RangeSize.X = cx;
                    world.RangeSize.Y = cy;
                }

                Entity ent = world.Get(cSerial);

                if (ent == null)
                {
                    continue;
                }

                //if (SerialHelper.IsMobile(cSerial))
                //{
                //    Mobile m = (Mobile) ent;

                //    if (m.Steps.Count != 0)
                //    {
                //        ref var step = ref m.Steps.Back();

                //        step.X = cx;
                //        step.Y = cy;
                //    }
                //}

                //ent.LastX = cx;
                //ent.LastY = cy;

                if (smooth)
                {
                    world.BoatMovingManager.PushItemToList(
                        serial,
                        cSerial,
                        x - cx,
                        y - cy,
                        (sbyte)(z - cz)
                    );
                }
                else
                {
                    if (cSerial == world.Player)
                    {
                        UpdatePlayer(
                            world,
                            cSerial,
                            ent.Graphic,
                            0,
                            ent.Hue,
                            ent.Flags,
                            cx,
                            cy,
                            (sbyte)cz,
                            0,
                            world.Player.Direction
                        );
                    }
                    else
                    {
                        UpdateGameObject(
                            world,
                            cSerial,
                            ent.Graphic,
                            0,
                            (ushort)(ent.Graphic == 0x2006 ? ((Item)ent).Amount : 0),
                            cx,
                            cy,
                            (sbyte)cz,
                            SerialHelper.IsMobile(ent) ? ent.Direction : 0,
                            ent.Hue,
                            ent.Flags,
                            0,
                            0,
                            1
                        );
                    }
                }
            }
        }

        private static void UpdatePlayer(
            World world,
            uint serial,
            ushort graphic,
            byte graph_inc,
            ushort hue,
            Flags flags,
            ushort x,
            ushort y,
            sbyte z,
            ushort serverID,
            Direction direction
        )
        {
            if (serial == world.Player)
            {
                world.RangeSize.X = x;
                world.RangeSize.Y = y;

                bool olddead = world.Player.IsDead;
                ushort old_graphic = world.Player.Graphic;

                world.Player.CloseBank();
                world.Player.Walker.WalkingFailed = false;
                // T1: graphic, direction (its door check), hue, flags (ECS and GameObject); the position comes last
                world.Writer.SetMobileBody(world.Player, graphic, hue, flags, checkBody: false, setDirection: true, direction & Direction.Mask);
                world.Player.Walker.DenyWalk(0xFF, -1, -1, -1);

                GameScene gs = Client.Game.GetScene<GameScene>();

                if (gs != null)
                {
                    world.Weather.Reset();
                    gs.UpdateDrawPosition = true;
                }

                // std client keeps the target open!
                /*if (old_graphic != 0 && old_graphic != world.Player.Graphic)
                {
                    if (world.Player.IsDead)
                    {
                        TargetManager.Reset();
                    }
                }*/

                if (olddead != world.Player.IsDead)
                {
                    if (world.Player.IsDead)
                    {
                        world.ChangeSeason(Game.Managers.Season.Desolation, 42);
                    }
                    else
                    {
                        world.ChangeSeason(world.OldSeason, world.OldMusicIndex);
                    }
                }

                world.Player.Walker.ResendPacketResync = false;
                world.Player.CloseRangedGumps();
                // a teleport: start decoding the destination's regions now, while the rest of the packet burst is handled
                world.Map?.Prefetch(x, y);
                // T1: the position (ECS and GameObject) and the tile, with the player's position callbacks
                world.Writer.MoveMobile(world.Player, x, y, z);
                world.Player.UpdateAbilities();
            }
        }
    }
}
