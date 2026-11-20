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
        // dist is 1 (world to gump) or -1 (gump to world) at every call: the cos/sin of those angles are computed once, with
        // the same expressions as before, so the result is bit-identical
        private static readonly double _cosQuarter = Math.Cos(1 * Math.PI / 4.0);
        private static readonly double _sinQuarter = Math.Sin(1 * Math.PI / 4.0);
        private static readonly double _cosMinusQuarter = Math.Cos(-1 * Math.PI / 4.0);
        private static readonly double _sinMinusQuarter = Math.Sin(-1 * Math.PI / 4.0);

        internal static Point RotatePoint(int x, int y, float zoom, int dist, float angle = 45f)
        {
            x = (int) (x * zoom);
            y = (int) (y * zoom);

            if (angle == 0.0f)
            {
                return new Point(x, y);
            }

            double cos, sin;

            if (dist == 1)
            {
                cos = _cosQuarter;
                sin = _sinQuarter;
            }
            else if (dist == -1)
            {
                cos = _cosMinusQuarter;
                sin = _sinMinusQuarter;
            }
            else
            {
                cos = Math.Cos(dist * Math.PI / 4.0);
                sin = Math.Sin(dist * Math.PI / 4.0);
            }

            return new Point((int) Math.Round(cos * x - sin * y), (int) Math.Round(sin * x + cos * y));
        }

        private void AdjustPosition
        (
            int x,
            int y,
            int centerX,
            int centerY,
            out int newX,
            out int newY
        )
        {
            int offset = GetOffset(x, y, centerX, centerY);
            int currX = x;
            int currY = y;

            while (offset != 0)
            {
                if ((offset & 1) != 0)
                {
                    currY = centerY;
                    currX = x * currY / y;
                }
                else if ((offset & 2) != 0)
                {
                    currY = -centerY;
                    currX = x * currY / y;
                }
                else if ((offset & 4) != 0)
                {
                    currX = centerX;
                    currY = y * currX / x;
                }
                else if ((offset & 8) != 0)
                {
                    currX = -centerX;
                    currY = y * currX / x;
                }

                x = currX;
                y = currY;
                offset = GetOffset(x, y, centerX, centerY);
            }

            newX = x;
            newY = y;
        }

        private void CanvasToWorld
        (
            int a_x,
            int a_y,
            out int out_x,
            out int out_y
        )
        {
            // Scale width to Zoom
            var newWidth = Width / Zoom;
            var newHeight = Height / Zoom;

            // Scale mouse cords to Zoom
            var newX = a_x / Zoom;
            var newY = a_y / Zoom;

            // Rotate Cords if map fliped
            // x' = (x + y)/Sqrt(2)
            // y' = (y - x)/Sqrt(2)
            if (_flipMap)
            {
                var nw = (newWidth + newHeight) / 1.41f;
                var nh = (newHeight - newWidth) / 1.41f;
                newWidth = (int)nw;
                newHeight = (int)nh;

                var nx = (newX + newY) / 1.41f;
                var ny = (newY - newX) / 1.41f;
                newX = (int)nx;
                newY = (int)ny;
            }

            // Calulate Click cords to Map Cords
            // (x,y) = MapCenter - ScaeldMapWidth/2 + ScaledMouseCords
            out_x = _center.X - (int)(newWidth / 2) + (int)newX;
            out_y = _center.Y - (int)(newHeight / 2) + (int)newY;
        }

        private int GetOffset(int x, int y, int centerX, int centerY)
        {
            const int offset = 0;

            if (y > centerY)
            {
                return 1;
            }

            if (y < -centerY)
            {
                return 2;
            }

            if (x > centerX)
            {
                return offset + 4;
            }

            if (x >= -centerX)
            {
                return offset;
            }

            return offset + 8;
        }

        private bool ShouldDrawGrid()
        {
            return (_showGridIfZoomed && Zoom >= 4);
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (IsDisposed || !World.InGame)
            {
                return false;
            }

            if (!_isScrolling && !_freeView)
            {
                _center.X = World.Player.X;
                _center.Y = World.Player.Y;
            }

            // A deferred command, not a closure: the closure and its delegate were allocated every frame.
            renderLists.AddGumpNoAtlas(GumpCommand.Deferred(this, x, y, layerDepthRef));

            //foreach (House house in World.HouseManager.Houses)
            //{
            //    foreach (Multi multi in house.Components)
            //    {
            //        batcher.Draw2D(Textures.GetTexture())
            //    }
            //}


            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        bool IGumpDeferredDraw.DrawDeferred(UltimaBatcher2D batcher, int x, int y, float layerDepth, int arg)
        {
            int gX = x + 4;
            int gY = y + 4;
            int gWidth = Width - 8;
            int gHeight = Height - 8;

            int centerX = _center.X + 1;
            int centerY = _center.Y + 1;

            int size = (int)Math.Max(gWidth * 1.75f, gHeight * 1.75f);

            int size_zoom = (int)(size / Zoom);
            int size_zoom_half = size_zoom >> 1;

            int halfWidth = gWidth >> 1;
            int halfHeight = gHeight >> 1;

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            batcher.Draw
            (
                SolidColorTextureCache.GetTexture(Color.Black),
                new Rectangle
                (
                    gX,
                    gY,
                    gWidth,
                    gHeight
                ),
                hueVector,
                layerDepth
            );

            if (_mapLoading == 1)
            {
                if (batcher.ClipBegin(gX, gY, gWidth, gHeight))
                {
                    var str = "Please wait, I'm making the map file...".AsSpan();
                    var strSize = Fonts.Bold.MeasureString(str);
                    var pos = strSize * -0.5f;
                    pos.X += gX + halfWidth;
                    pos.Y += gY + halfHeight;
                    batcher.DrawString(Fonts.Bold, str, pos, new Vector3(38, 1, 1), layerDepth);

                    batcher.ClipEnd();
                }
            }
            else if (_mapTexture.Texture is { IsDisposed: false } mapTexture)
            {
                if (batcher.ClipBegin(gX, gY, gWidth, gHeight))
                {
                    var destRect = new Rectangle
                    (
                        gX + halfWidth,
                        gY + halfHeight,
                        size,
                        size
                    );

                    var srcRect = new Rectangle
                    (
                        centerX - size_zoom_half,
                        centerY - size_zoom_half,
                        size_zoom,
                        size_zoom
                    );

                    var origin = new Vector2
                    (
                        srcRect.Width / 2f,
                        srcRect.Height / 2f
                    );

                    batcher.Draw
                    (
                        mapTexture,
                        destRect,
                        srcRect,
                        hueVector,
                        _flipMap ? Microsoft.Xna.Framework.MathHelper.ToRadians(45) : 0,
                        origin,
                        SpriteEffects.None,
                        layerDepth
                    );

                    DrawAll
                    (
                        batcher,
                        srcRect,
                        gX,
                        gY,
                        halfWidth,
                        halfHeight,
                        layerDepth
                    );

                    batcher.ClipEnd();
                }
            }

            return true;
        }

        private void DrawAll(UltimaBatcher2D batcher, Rectangle srcRect, int gX, int gY, int halfWidth, int halfHeight, float layerDepth)
        {
            // Walks the zone sets directly: an iterator over them was allocated every frame.
            foreach (KeyValuePair<string, ZoneSet> entry in _zoneSets.ZoneSetDict)
            {
                ZoneSet zoneSet = entry.Value;

                if (zoneSet.MapIndex != _map.Index || zoneSet.Hidden)
                {
                    continue;
                }

                foreach (Zone zone in zoneSet.Zones)
                {
                    if (zone.BoundingRectangle.Intersects(srcRect))
                    {
                        DrawZone(batcher, zone, gX, gY, halfWidth, halfHeight, Zoom, layerDepth);
                    }
                }
            }

            if (_showMultis)
            {
                foreach (House house in World.HouseManager.Houses)
                {
                    Item item = World.FindItem(house.Serial);

                    if (item != null)
                    {
                        DrawMulti
                        (
                            batcher,
                            house,
                            item.X,
                            item.Y,
                            gX,
                            gY,
                            halfWidth,
                            halfHeight,
                            Zoom,
                            layerDepth
                        );
                    }
                }
            }

            if (_showMarkers && _mapMarkersLoaded)
            {
                WMapMarker lastMarker = null;

                foreach (WMapMarkerFile file in _markerFiles)
                {
                    if (file.Hidden)
                    {
                        continue;
                    }

                    foreach (WMapMarker marker in file.Markers)
                    {
                        if (DrawMarker
                        (
                            batcher,
                            marker,
                            gX,
                            gY,
                            halfWidth,
                            halfHeight,
                            Zoom,
                            layerDepth
                        ))
                        {
                            lastMarker = marker;
                        }
                    }
                }

                if (lastMarker != null)
                {
                    DrawMarkerString(batcher, lastMarker, gX, gY, halfWidth, halfHeight, layerDepth);
                }
            }

            if (_gotoMarker != null)
            {
                DrawMarker
                (
                    batcher,
                    _gotoMarker,
                    gX,
                    gY,
                    halfWidth,
                    halfHeight,
                    Zoom,
                    layerDepth
                );
            }

            if (_showMobiles)
            {
                foreach (Mobile mob in World.Mobiles.Values)
                {
                    if (mob == World.Player)
                    {
                        continue;
                    }

                    if (mob.NotorietyFlag != NotorietyFlag.Ally)
                    {
                        DrawMobile
                        (
                            batcher,
                            mob,
                            gX,
                            gY,
                            halfWidth,
                            halfHeight,
                            Zoom,
                            Color.Red,
                            layerDepth
                        );
                    }
                    else
                    {
                        if (mob != null && mob.Distance <= World.ClientViewRange)
                        {
                            WMapEntity wme = World.WMapManager.GetEntity(mob);

                            if (wme != null)
                            {
                                if (string.IsNullOrEmpty(wme.Name) && !string.IsNullOrEmpty(mob.Name))
                                {
                                    wme.Name = mob.Name;
                                }
                            }
                            else
                            {
                                DrawMobile
                                (
                                    batcher,
                                    mob,
                                    gX,
                                    gY,
                                    halfWidth,
                                    halfHeight,
                                    Zoom,
                                    Color.Lime,
                                    layerDepth,
                                    true,
                                    true,
                                    _showGroupBar
                                );
                            }
                        }
                        else
                        {
                            WMapEntity wme = World.WMapManager.GetEntity(mob.Serial);

                            if (wme != null && wme.IsGuild)
                            {
                                DrawWMEntity
                                (
                                    batcher,
                                    wme,
                                    gX,
                                    gY,
                                    halfWidth,
                                    halfHeight,
                                    Zoom,
                                    layerDepth
                                );
                            }
                        }
                    }
                }
            }

            foreach (WMapEntity wme in World.WMapManager.Entities.Values)
            {
                if (wme.IsGuild && !World.Party.Contains(wme.Serial))
                {
                    DrawWMEntity
                    (
                        batcher,
                        wme,
                        gX,
                        gY,
                        halfWidth,
                        halfHeight,
                        Zoom,
                        layerDepth
                    );
                }
            }

            if (_showPartyMembers)
            {
                for (int i = 0; i < 10; i++)
                {
                    PartyMember partyMember = World.Party.Members[i];

                    if (partyMember != null && SerialHelper.IsValid(partyMember.Serial))
                    {
                        Mobile mob = World.FindMobile(partyMember.Serial);

                        if (mob != null && mob.Distance <= World.ClientViewRange)
                        {
                            WMapEntity wme = World.WMapManager.GetEntity(mob);

                            if (wme != null)
                            {
                                if (string.IsNullOrEmpty(wme.Name) && !string.IsNullOrEmpty(partyMember.Name))
                                {
                                    wme.Name = partyMember.Name;
                                }
                            }

                            DrawMobile
                            (
                                batcher,
                                mob,
                                gX,
                                gY,
                                halfWidth,
                                halfHeight,
                                Zoom,
                                Color.Yellow,
                                layerDepth,
                                _showGroupName,
                                true,
                                _showGroupBar
                            );
                        }
                        else
                        {
                            WMapEntity wme = World.WMapManager.GetEntity(partyMember.Serial);

                            if (wme != null && !wme.IsGuild)
                            {
                                DrawWMEntity
                                (
                                    batcher,
                                    wme,
                                    gX,
                                    gY,
                                    halfWidth,
                                    halfHeight,
                                    Zoom,
                                    layerDepth
                                );
                            }
                        }
                    }
                }
            }

            DrawMobile
            (
                batcher,
                World.Player,
                gX,
                gY,
                halfWidth,
                halfHeight,
                Zoom,
                Color.White,
                layerDepth,
                _showPlayerName,
                false,
                _showPlayerBar
                
            );

            if (ShouldDrawGrid())
            {
                DrawGrid(batcher, srcRect, gX, gY, halfWidth, halfHeight, Zoom, layerDepth);
            }

            if (_showCoordinates)
            {
                string text = _playerCoordsText.Player(World.Player.X, World.Player.Y, World.Player.Z, _zoomIndex, _map.Index, _showSextantCoordinates);

                Vector3 hueVector = new(0f, 1f, 1f);

                batcher.DrawString(Fonts.Bold, text, gX + 6, gY + 6, hueVector, layerDepth);
                hueVector = ShaderHueTranslator.GetHueVector(0);
                batcher.DrawString(Fonts.Bold, text, gX + 5, gY + 5, hueVector, layerDepth);
            }

            if (_showMouseCoordinates && _lastMousePosition != null)
            {
                CanvasToWorld(_lastMousePosition.Value.X, _lastMousePosition.Value.Y, out int mouseWorldX, out int mouseWorldY);

                string mouseCoordinateString = _mouseCoordsText.Mouse(mouseWorldX, mouseWorldY, _map.Index, _showSextantCoordinates);

                Vector2 size = Fonts.Regular.MeasureString(mouseCoordinateString);
                int mx = gX + 5;
                int my = gY + Height - (int)Math.Ceiling(size.Y) - 15;

                Vector3 hueVector = new(0f, 1f, 1f);

                batcher.DrawString
                (
                    Fonts.Bold,
                    mouseCoordinateString,
                    mx + 1,
                    my + 1,
                    hueVector,
                    layerDepth
                );

                hueVector = ShaderHueTranslator.GetHueVector(0);

                batcher.DrawString
                (
                    Fonts.Bold,
                    mouseCoordinateString,
                    mx,
                    my,
                    hueVector,
                    layerDepth
                );
            }
        }

        private void DrawMobile
        (
            UltimaBatcher2D batcher,
            Mobile mobile,
            int x,
            int y,
            int width,
            int height,
            float zoom,
            Color color,
            float layerDepth,
            bool drawName = false,
            bool isparty = false,
            bool drawHpBar = false
        )
        {
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            int sx = mobile.X - _center.X;
            int sy = mobile.Y - _center.Y;

            Point rot = RotatePoint
            (
                sx,
                sy,
                zoom,
                1,
                _flipMap ? 45f : 0f
            );

            AdjustPosition
            (
                rot.X,
                rot.Y,
                width - 4,
                height - 4,
                out rot.X,
                out rot.Y
            );

            rot.X += x + width;
            rot.Y += y + height;

            const int DOT_SIZE = 4;
            const int DOT_SIZE_HALF = DOT_SIZE >> 1;

            if (rot.X < x)
            {
                rot.X = x;
            }

            if (rot.X > x + Width - 8 - DOT_SIZE)
            {
                rot.X = x + Width - 8 - DOT_SIZE;
            }

            if (rot.Y < y)
            {
                rot.Y = y;
            }

            if (rot.Y > y + Height - 8 - DOT_SIZE)
            {
                rot.Y = y + Height - 8 - DOT_SIZE;
            }

            batcher.Draw
            (
                SolidColorTextureCache.GetTexture(color),
                new Rectangle
                (
                    rot.X - DOT_SIZE_HALF,
                    rot.Y - DOT_SIZE_HALF,
                    DOT_SIZE,
                    DOT_SIZE
                ),
                hueVector,
                layerDepth
            );

            if (drawName && !string.IsNullOrEmpty(mobile.Name))
            {
                Vector2 size = Fonts.Regular.MeasureString(mobile.Name);

                if (rot.X + size.X / 2 > x + Width - 8)
                {
                    rot.X = x + Width - 8 - (int) (size.X / 2);
                }
                else if (rot.X - size.X / 2 < x)
                {
                    rot.X = x + (int) (size.X / 2);
                }

                if (rot.Y + size.Y > y + Height)
                {
                    rot.Y = y + Height - (int) size.Y;
                }
                else if (rot.Y - size.Y < y)
                {
                    rot.Y = y + (int) size.Y;
                }

                int xx = (int) (rot.X - size.X / 2);
                int yy = (int) (rot.Y - size.Y);

                hueVector.X = 0;
                hueVector.Y = 1;

                batcher.DrawString
                (
                    Fonts.Regular,
                    mobile.Name,
                    xx + 1,
                    yy + 1,
                    hueVector,
                    layerDepth
                );

                hueVector.X = isparty ? 0x0034 : Notoriety.GetHue(mobile.NotorietyFlag);
                hueVector.Y = 1;
                hueVector.Z = 1;

                batcher.DrawString
                (
                    Fonts.Regular,
                    mobile.Name,
                    xx,
                    yy,
                    hueVector,
                    layerDepth
                );
            }

            if (drawHpBar)
            {
                int ww = mobile.HitsMax;

                if (ww > 0)
                {
                    ww = mobile.Hits * 100 / ww;

                    if (ww > 100)
                    {
                        ww = 100;
                    }
                    else if (ww < 1)
                    {
                        ww = 0;
                    }
                }

                rot.Y += DOT_SIZE + 1;

                DrawHpBar(batcher, rot.X, rot.Y, ww, layerDepth);
            }
        }

        private bool DrawMarker
        (
            UltimaBatcher2D batcher,
            WMapMarker marker,
            int x,
            int y,
            int width,
            int height,
            float zoom,
            float layerDepth
        )
        {
            if (marker.MapId != _map.Index)
            {
                return false;
            }

            if (_zoomIndex < marker.ZoomIndex && marker.Color == Color.Transparent)
            {
                return false;
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            int sx = marker.X - _center.X;
            int sy = marker.Y - _center.Y;

            // P-4: most markers of a large set are far off screen; skip them before the projection (the test is conservative:
            // it only drops markers the bounds test below would drop too)
            if (MarkerOutsideView(sx, sy, zoom, width, height, Width - 8, Height - 8))
            {
                return false;
            }

            Point rot = RotatePoint
            (
                sx,
                sy,
                zoom,
                1,
                _flipMap ? 45f : 0f
            );

            rot.X += x + width;
            rot.Y += y + height;

            const int DOT_SIZE_HALF = MARKER_DOT_SIZE >> 1;

            if (!MarkerInView(rot.X, rot.Y, x, y, Width - 8, Height - 8))
            {
                return false;
            }

            bool showMarkerName = _showMarkerNames && !string.IsNullOrEmpty(marker.Name) && _zoomIndex > 5;
            bool drawSingleName = false;

            if (_zoomIndex < marker.ZoomIndex || !_showMarkerIcons || marker.MarkerIcon == null)
            {
                batcher.Draw
                (
                    SolidColorTextureCache.GetTexture(marker.Color),
                    new Rectangle
                    (
                        rot.X - DOT_SIZE_HALF,
                        rot.Y - DOT_SIZE_HALF,
                        MARKER_DOT_SIZE,
                        MARKER_DOT_SIZE
                    ),
                    hueVector,
                    layerDepth
                );

                if (Mouse.Position.X >= rot.X - MARKER_DOT_SIZE && Mouse.Position.X <= rot.X + DOT_SIZE_HALF &&
                    Mouse.Position.Y >= rot.Y - MARKER_DOT_SIZE && Mouse.Position.Y <= rot.Y + DOT_SIZE_HALF)
                {
                    drawSingleName = true;
                }
            }
            else
            {
                batcher.Draw(marker.MarkerIcon, new Vector2(rot.X - (marker.MarkerIcon.Width >> 1), rot.Y - (marker.MarkerIcon.Height >> 1)), hueVector, layerDepth);

                if (!showMarkerName)
                {
                    if (Mouse.Position.X >= rot.X - (marker.MarkerIcon.Width >> 1) &&
                        Mouse.Position.X <= rot.X + (marker.MarkerIcon.Width >> 1) &&
                        Mouse.Position.Y >= rot.Y - (marker.MarkerIcon.Height >> 1) &&
                        Mouse.Position.Y <= rot.Y + (marker.MarkerIcon.Height >> 1))
                    {
                        drawSingleName = true;
                    }
                }
            }

            if (showMarkerName)
            {
                DrawMarkerString(batcher, marker, x, y, width, height, layerDepth);

                drawSingleName = false;
            }

            return drawSingleName;
        }

        private const int MARKER_DOT_SIZE = 4;

        /// <summary>The marker bounds test (gump coordinates of the projected marker against the map area less the dot size).</summary>
        internal static bool MarkerInView(int rotX, int rotY, int x, int y, int innerWidth, int innerHeight) =>
            !(rotX < x || rotX > x + innerWidth - MARKER_DOT_SIZE || rotY < y || rotY > y + innerHeight - MARKER_DOT_SIZE);

        /// <summary>
        /// True only for a marker <see cref="MarkerInView"/> rejects whatever the rotation: the projected offset from the map
        /// centre (halfWidth, halfHeight) must lie within M = the largest of those bounds per axis, so its length is at most
        /// M*sqrt(2) before rounding (+0.71) and the unrotated, truncated offset (|sx|*zoom - 1) is at most 1.5*M + 2. One
        /// more pixel covers float rounding of sx*zoom.
        /// </summary>
        internal static bool MarkerOutsideView(int sx, int sy, float zoom, int halfWidth, int halfHeight, int innerWidth, int innerHeight)
        {
            int m = Math.Max
            (
                Math.Max(halfWidth, innerWidth - MARKER_DOT_SIZE - halfWidth),
                Math.Max(halfHeight, innerHeight - MARKER_DOT_SIZE - halfHeight)
            );

            double limit = 1.5 * m + 3;

            return Math.Abs((double) sx) * zoom > limit || Math.Abs((double) sy) * zoom > limit;
        }

        private void DrawMarkerString(UltimaBatcher2D batcher, WMapMarker marker, int x, int y, int width, int height, float layerDepth)
        {
            int sx = marker.X - _center.X;
            int sy = marker.Y - _center.Y;

            Point rot = RotatePoint
            (
                sx,
                sy,
                Zoom,
                1,
                _flipMap ? 45f : 0f
            );

            rot.X += x + width;
            rot.Y += y + height;

            Vector2 size = _markerFont.MeasureString(marker.Name);

            if (rot.X + size.X / 2 > x + Width - 8)
            {
                rot.X = x + Width - 8 - (int)(size.X / 2);
            }
            else if (rot.X - size.X / 2 < x)
            {
                rot.X = x + (int)(size.X / 2);
            }

            if (rot.Y + size.Y > y + Height)
            {
                rot.Y = y + Height - (int)size.Y;
            }
            else if (rot.Y - size.Y < y)
            {
                rot.Y = y + (int)size.Y;
            }

            int xx = (int)(rot.X - size.X / 2);
            int yy = (int)(rot.Y - size.Y - 5);

            Vector3 hueVector = new Vector3(0f, 1f, 0.5f);

            batcher.Draw
            (
                SolidColorTextureCache.GetTexture(Color.Black),
                new Rectangle
                (
                    xx - 2,
                    yy - 2,
                    (int) (size.X + 4),
                    (int) (size.Y + 4)
                ),
                hueVector,
                layerDepth
            );

            hueVector = new Vector3(0f, 1f, 1f);

            batcher.DrawString
            (
                _markerFont,
                marker.Name,
                xx + 1,
                yy + 1,
                hueVector,
                layerDepth
            );

            hueVector = ShaderHueTranslator.GetHueVector(0);

            batcher.DrawString
            (
                _markerFont,
                marker.Name,
                xx,
                yy,
                hueVector,
                layerDepth
            );
        }

        private void DrawMulti
        (
            UltimaBatcher2D batcher,
            House house,
            int multiX,
            int multiY,
            int x,
            int y,
            int width,
            int height,
            float zoom,
            float layerDepth
        )
        {
            int sx = multiX - _center.X;
            int sy = multiY - _center.Y;
            int sW = Math.Abs(house.Bounds.Width - house.Bounds.X);
            int sH = Math.Abs(house.Bounds.Height - house.Bounds.Y);

            Point rot = RotatePoint
            (
                sx,
                sy,
                zoom,
                1,
                _flipMap ? 45f : 0f
            );


            rot.X += x + width;
            rot.Y += y + height;

            const int DOT_SIZE = 4;

            if (rot.X < x || rot.X > x + Width - 8 - DOT_SIZE || rot.Y < y || rot.Y > y + Height - 8 - DOT_SIZE)
            {
                return;
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            Texture2D texture = SolidColorTextureCache.GetTexture(Color.DarkGray);

            batcher.Draw
            (
                texture,
                new Rectangle
                (
                    rot.X,
                    rot.Y,
                    (int)(sW * zoom),
                    (int)(sH * zoom)
                ),
                null,
                hueVector,
                _flipMap ? Microsoft.Xna.Framework.MathHelper.ToRadians(45) : 0,
                new Vector2(0.5f, 0.5f),
                SpriteEffects.None,
                layerDepth
            );
        }

        private Vector2 WorldPointToGumpPoint(int wpx, int wpy, int x, int y, int width, int height, float zoom)
        {
            int sx = wpx - _center.X;
            int sy = wpy - _center.Y;

            Point rot = RotatePoint
            (
                sx,
                sy,
                zoom,
                1,
                _flipMap ? 45f : 0f
            );

            /* N.B. You don't want AdjustPosition() here if you want to draw rects
             * that extend beyond the gump's viewport without distoring them. */

            rot.X += x + width;
            rot.Y += y + height;

            return new Vector2(rot.X, rot.Y);
        }

        private void DrawZone
        (
            UltimaBatcher2D batcher,
            Zone zone,
            int x,
            int y,
            int width,
            int height,
            float zoom,
            float layerDepth
        )
        {
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);
            Texture2D texture = SolidColorTextureCache.GetTexture(zone.Color);

            for (int i = 0, j = 1; i < zone.Vertices.Count; i++, j++)
            {
                if (j >= zone.Vertices.Count) j = 0;

                Vector2 start = WorldPointToGumpPoint(zone.Vertices[i].X, zone.Vertices[i].Y, x, y, width, height, zoom);
                Vector2 end = WorldPointToGumpPoint(zone.Vertices[j].X, zone.Vertices[j].Y, x, y, width, height, zoom);

                batcher.DrawLine(texture, start, end, hueVector, 1, layerDepth);
            }
        }

        private void DrawGrid
        (
            UltimaBatcher2D batcher,
            Rectangle srcRect,
            int x,
            int y,
            int width,
            int height,
            float zoom,
            float layerDepth
        )
        {
            const int GRID_SKIP = 8;
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);
            Texture2D colorTexture = SolidColorTextureCache.GetTexture(_semiTransparentWhiteForGrid);

            batcher.SetBlendState(BlendState.Additive);

            for (int worldY = (srcRect.Y / GRID_SKIP) * GRID_SKIP; worldY < srcRect.Y + srcRect.Height; worldY += GRID_SKIP)
            {
                Vector2 start = WorldPointToGumpPoint(srcRect.X, worldY, x, y, width, height, zoom);
                Vector2 end = WorldPointToGumpPoint(srcRect.X + srcRect.Width, worldY, x, y, width, height, zoom);

                batcher.DrawLine(colorTexture, start, end, hueVector, 1, layerDepth);
            }

            for (int worldX = (srcRect.X / GRID_SKIP) * GRID_SKIP; worldX < srcRect.X + srcRect.Width; worldX += GRID_SKIP)
            {
                Vector2 start = WorldPointToGumpPoint(worldX, srcRect.Y, x, y, width, height, zoom);
                Vector2 end = WorldPointToGumpPoint(worldX, srcRect.Y + srcRect.Height, x, y, width, height, zoom);

                batcher.DrawLine(colorTexture, start, end, hueVector, 1, layerDepth);
            }

            batcher.SetBlendState(null);
        }

        private void DrawWMEntity
        (
            UltimaBatcher2D batcher,
            WMapEntity entity,
            int x,
            int y,
            int width,
            int height,
            float zoom,
            float layerDepth
        )
        {
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            ushort uohue;
            Color color;

            if (entity.IsGuild)
            {
                uohue = 0x0044;
                color = Color.LimeGreen;
            }
            else
            {
                uohue = 0x0034;
                color = Color.Yellow;
            }

            if (entity.Map != _map.Index)
            {
                uohue = 992;
                color = Color.DarkGray;
            }

            int sx = entity.X - _center.X;
            int sy = entity.Y - _center.Y;

            Point rot = RotatePoint
            (
                sx,
                sy,
                zoom,
                1,
                _flipMap ? 45f : 0f
            );

            AdjustPosition
            (
                rot.X,
                rot.Y,
                width - 4,
                height - 4,
                out rot.X,
                out rot.Y
            );

            rot.X += x + width;
            rot.Y += y + height;

            const int DOT_SIZE = 4;
            const int DOT_SIZE_HALF = DOT_SIZE >> 1;

            if (rot.X < x)
            {
                rot.X = x;
            }

            if (rot.X > x + Width - 8 - DOT_SIZE)
            {
                rot.X = x + Width - 8 - DOT_SIZE;
            }

            if (rot.Y < y)
            {
                rot.Y = y;
            }

            if (rot.Y > y + Height - 8 - DOT_SIZE)
            {
                rot.Y = y + Height - 8 - DOT_SIZE;
            }

            batcher.Draw
            (
                SolidColorTextureCache.GetTexture(color),
                new Rectangle
                (
                    rot.X - DOT_SIZE_HALF,
                    rot.Y - DOT_SIZE_HALF,
                    DOT_SIZE,
                    DOT_SIZE
                ),
                hueVector,
                layerDepth
            );

            if (_showGroupName)
            {
                string name = entity.Name ?? ResGumps.OutOfRange;
                Vector2 size = Fonts.Regular.MeasureString(entity.Name ?? name);

                if (rot.X + size.X / 2 > x + Width - 8)
                {
                    rot.X = x + Width - 8 - (int) (size.X / 2);
                }
                else if (rot.X - size.X / 2 < x)
                {
                    rot.X = x + (int) (size.X / 2);
                }

                if (rot.Y + size.Y > y + Height)
                {
                    rot.Y = y + Height - (int) size.Y;
                }
                else if (rot.Y - size.Y < y)
                {
                    rot.Y = y + (int) size.Y;
                }

                int xx = (int) (rot.X - size.X / 2);
                int yy = (int) (rot.Y - size.Y);

                hueVector.X = 0;
                hueVector.Y = 1;

                batcher.DrawString
                (
                    Fonts.Regular,
                    name,
                    xx + 1,
                    yy + 1,
                    hueVector,
                    layerDepth
                );

                hueVector = new Vector3(uohue, 1f, 1f);

                batcher.DrawString
                (
                    Fonts.Regular,
                    name,
                    xx,
                    yy,
                    hueVector,
                    layerDepth
                );
            }

            if (_showGroupBar)
            {
                rot.Y += DOT_SIZE + 1;
                DrawHpBar(batcher, rot.X, rot.Y, entity.HP, layerDepth);
            }
        }

        private void DrawHpBar(UltimaBatcher2D batcher, int x, int y, int hp, float layerDepth)
        {
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            const int BAR_MAX_WIDTH = 25;
            const int BAR_MAX_WIDTH_HALF = BAR_MAX_WIDTH / 2;

            const int BAR_MAX_HEIGHT = 3;
            const int BAR_MAX_HEIGHT_HALF = BAR_MAX_HEIGHT / 2;


            batcher.Draw
            (
                SolidColorTextureCache.GetTexture(Color.Black),
                new Rectangle
                (
                    x - BAR_MAX_WIDTH_HALF - 1,
                    y - BAR_MAX_HEIGHT_HALF - 1,
                    BAR_MAX_WIDTH + 2,
                    BAR_MAX_HEIGHT + 2
                ),
                hueVector,
                layerDepth
            );

            batcher.Draw
            (
                SolidColorTextureCache.GetTexture(Color.Red),
                new Rectangle
                (
                    x - BAR_MAX_WIDTH_HALF,
                    y - BAR_MAX_HEIGHT_HALF,
                    BAR_MAX_WIDTH,
                    BAR_MAX_HEIGHT
                ),
                hueVector,
                layerDepth
            );

            int max = 100;
            int current = hp;

            if (max > 0)
            {
                max = current * 100 / max;

                if (max > 100)
                {
                    max = 100;
                }

                if (max > 1)
                {
                    max = BAR_MAX_WIDTH * max / 100;
                }
            }

            batcher.Draw
            (
                SolidColorTextureCache.GetTexture(Color.CornflowerBlue),
                new Rectangle
                (
                    x - BAR_MAX_WIDTH_HALF,
                    y - BAR_MAX_HEIGHT_HALF,
                    max,
                    BAR_MAX_HEIGHT
                ),
                hueVector,
                layerDepth
            );
        }
    }
}
