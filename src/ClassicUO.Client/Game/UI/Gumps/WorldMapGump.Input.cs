// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static ClassicUO.Game.UI.Gumps.WorldMapGump;
using SpriteFont = ClassicUO.Renderer.SpriteFont;

namespace ClassicUO.Game.UI.Gumps
{
    internal partial class WorldMapGump
    {
        internal void HandlePositionTarget()
        {
            var position = Mouse.Position;
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            CanvasToWorld(x, y, out int xMap, out int yMap);
            World.TargetManager.Target
            (
                0,
                (ushort)xMap,
                (ushort)yMap,
                _map.GetTileZ(xMap, yMap)
            );
        }

        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            var allowTarget = _allowPositionalTarget && World.TargetManager.IsTargeting && World.TargetManager.TargetingState == CursorTarget.Position;
            if (allowTarget && button == MouseButtonType.Left)
            {
                HandlePositionTarget();
            }

            if (button == MouseButtonType.Left && !Keyboard.Alt)
            {
                _isScrolling = false;
                CanMove = true;
            }

            if (button == MouseButtonType.Left || button == MouseButtonType.Middle)
            {
                _lastScroll.X = _center.X;
                _lastScroll.Y = _center.Y;
            }

            Client.Game.UO.GameCursor.IsDraggingCursorForced = false;

            base.OnMouseUp(x, y, button);
        }

        protected override void OnMouseDown(int x, int y, MouseButtonType button)
        {
            if (!Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                if (button == MouseButtonType.Left && (Keyboard.Alt || _freeView) || button == MouseButtonType.Middle)
                {
                    if (x > 4 && x < Width - 8 && y > 4 && y < Height - 8)
                    {
                        if (button == MouseButtonType.Middle)
                        {
                            FreeView = true;
                        }

                        _lastScroll.X = _center.X;
                        _lastScroll.Y = _center.Y;
                        _isScrolling = true;
                        CanMove = false;

                        Client.Game.UO.GameCursor.IsDraggingCursorForced = true;
                    }
                }

                if (button == MouseButtonType.Left && Keyboard.Ctrl)
                {
                    CanvasToWorld(x, y, out _mouseCenter.X, out _mouseCenter.Y);

                    // Check if file is loaded and contain markers
                    var userFile = _markerFiles.Where(f => f.Name == USER_MARKERS_FILE).FirstOrDefault();

                    if (userFile == null)
                    {
                        return;
                    }

                    UserMarkersGump existingGump = UIManager.GetGump<UserMarkersGump>();

                    existingGump?.Dispose();
                    UIManager.Add(new UserMarkersGump(World, _mouseCenter.X, _mouseCenter.Y, userFile.Markers));
                }
            }

            base.OnMouseDown(x, y, button);
        }

        protected override void OnMouseOver(int x, int y)
        {
            _lastMousePosition = new Point(x, y);

            Point offset = Mouse.LButtonPressed ? Mouse.LDragOffset : Mouse.MButtonPressed ? Mouse.MDragOffset : Point.Zero;

            if (_isScrolling && offset != Point.Zero)
            {
                _scroll.X = _scroll.Y = 0;

                if (Mouse.LButtonPressed)
                {
                    _scroll.X = x - (Mouse.LClickPosition.X - X);
                    _scroll.Y = y - (Mouse.LClickPosition.Y - Y);
                }
                else if (Mouse.MButtonPressed)
                {
                    _scroll.X = x - (Mouse.MClickPosition.X - X);
                    _scroll.Y = y - (Mouse.MClickPosition.Y - Y);
                }

                if (_scroll == Point.Zero)
                {
                    return;
                }

                _scroll = RotatePoint
                (
                    _scroll.X,
                    _scroll.Y,
                    1f / Zoom,
                    -1,
                    _flipMap ? 45f : 0f
                );

                _center.X = _lastScroll.X - _scroll.X;
                _center.Y = _lastScroll.Y - _scroll.Y;

                if (_center.X < 0)
                {
                    _center.X = 0;
                }

                if (_center.Y < 0)
                {
                    _center.Y = 0;
                }

                if (_center.X > Client.Game.UO.FileManager.Maps.MapsDefaultSize[_map.Index, 0])
                {
                    _center.X = Client.Game.UO.FileManager.Maps.MapsDefaultSize[_map.Index, 0];
                }

                if (_center.Y > Client.Game.UO.FileManager.Maps.MapsDefaultSize[_map.Index, 1])
                {
                    _center.Y = Client.Game.UO.FileManager.Maps.MapsDefaultSize[_map.Index, 1];
                }
            }
            else
            {
                base.OnMouseOver(x, y);
            }
        }

        protected override void OnMouseWheel(MouseEventType delta)
        {
            if (delta == MouseEventType.WheelScrollUp)
            {
                _zoomIndex++;

                if (_zoomIndex >= _zooms.Length)
                {
                    _zoomIndex = _zooms.Length - 1;
                }
            }
            else
            {
                _zoomIndex--;

                if (_zoomIndex < 0)
                {
                    _zoomIndex = 0;
                }
            }


            base.OnMouseWheel(delta);
        }

        protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button != MouseButtonType.Left || _isScrolling || Keyboard.Alt)
            {
                return base.OnMouseDoubleClick(x, y, button);
            }

            TopMost = !TopMost;

            return true;
        }

        protected override void OnMouseExit(int x, int y)
        {
            _lastMousePosition = null;
            base.OnMouseExit(x, y);
        }

        protected override void OnMove(int x, int y)
        {
            base.OnMove(x, y);
            _last_position.X = ScreenCoordinateX;
            _last_position.Y = ScreenCoordinateY;
        }
    }
}
