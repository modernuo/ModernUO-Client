// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        private long _smFrames, _smMissing, _smMissingStatics, _smMissingItems, _smMissingMobiles, _smFramesMissing;
        private uint _smSummaryAt;
        private int _smLogged;

        // CUO_VIEW_MARGIN_CHECK (sprite margin): a wide reference walk after the fill. Every map static and multi component
        // (Legacy: on the Gpu world baked statics are drawn without the walk), ground item and mobile of the chunks the
        // largest art can reach the view from, whose sprite rect meets the visible world and which the walk's own tests would
        // draw, must carry this frame's DrawnFrame stamp; one without it is missing at the view's edge (logged with its
        // graphic and type). Runs in both switch states: off counts what the anchor culling leaves out.
        private void CheckSpriteMargin()
        {
            if (!ViewMargin.Check || !_world.InGame)
            {
                return;
            }

            Point vMin = Camera.ScreenToWorld(Point.Zero);
            Point vMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            ArtLoader arts = Client.Game.UO.FileManager.Arts;
            ViewMargin.ArtReach(arts, out int side, out int up);
            int offX = _offset.X, offY = _offset.Y;
            ViewMargin.Bands(vMin.X - side - 44, vMin.Y - 44, vMax.X + side + 44, vMax.Y + up + 44, offX, offY, out int dLo, out int dHi, out int sLo, out int sHi);
            ViewMargin.ChunkRange(dLo, dHi, sLo, sHi, out int cx0, out int cy0, out int cx1, out int cy1);
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;
            bool statics = _gpuWorld == null;
            int missing = 0;

            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    if (!ViewMargin.ChunkTouches(cx, cy, dLo, dHi, sLo, sHi))
                    {
                        continue;
                    }

                    Chunk chunk = _world.Map.PeekChunkAt(cx, cy);

                    if (chunk == null)
                    {
                        continue;
                    }

                    for (int cell = 0; statics && cell < 64; cell++)
                    {
                        for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
                        {
                            if ((o is Static || o is Multi) && (uint)o.Graphic < (uint)staticData.Length &&
                                SpriteMissing(o, arts, ref staticData[o.Graphic], vMin, vMax, offX, offY))
                            {
                                missing++;
                                _smMissingStatics++;
                                LogMissing(o);
                            }
                        }
                    }

                    if (chunk.Dynamic == null)
                    {
                        continue;
                    }

                    for (int cell = 0; cell < 64; cell++)
                    {
                        foreach (ref readonly DynamicTileIndex.Entry en in chunk.Dynamic.Cell(cell))
                        {
                            if (en.Obj is Item item && !item.IsMulti && !item.IsCorpse && (uint)item.Graphic < (uint)staticData.Length &&
                                SpriteMissing(item, arts, ref staticData[item.Graphic], vMin, vMax, offX, offY))
                            {
                                missing++;
                                _smMissingItems++;
                                LogMissing(item);
                            }
                            else if (en.Obj is Mobile mob && MobileMissing(mob, vMin, vMax, offX, offY))
                            {
                                missing++;
                                _smMissingMobiles++;
                                LogMissing(mob);
                            }
                        }
                    }
                }
            }

            _smFrames++;
            _smMissing += missing;

            if (missing != 0)
            {
                _smFramesMissing++;
            }

            if (Time.Ticks >= _smSummaryAt)
            {
                _smSummaryAt = Time.Ticks + 5000;
                Log.Info($"[view-margin-check] sprites: view_margin {(_vmSprites ? "on" : "off")}, {_smFrames} frames, {_smFramesMissing} with a sprite reaching the view undrawn; " +
                         $"missing {_smMissing} (statics/multis {_smMissingStatics}{(statics ? "" : " (Gpu: not checked)")}, items {_smMissingItems}, mobiles {_smMissingMobiles})");
            }
        }

        private bool SpriteMissing(GameObject obj, ArtLoader arts, ref StaticTiles data, Point vMin, Point vMax, int offX, int offY)
        {
            if (obj.IsDestroyed || obj.DrawnFrame == _drawFrame || !obj.AllowedToDraw || obj.AlphaHue == 0 || data.IsInternal ||
                obj.Z >= _maxZ || _noDrawRoofs && data.IsRoof || !arts.TrySize(obj.Graphic + 0x4000, out int w, out int h))
            {
                return false;
            }

            ViewMargin.Anchor(obj.X, obj.Y, obj.Z, offX, offY, out int ax, out int ay);
            int x0 = ax - (w >> 1) + 22, y0 = ay - h + 44;

            if (x0 + w < vMin.X || x0 > vMax.X || y0 + h < vMin.Y || y0 > vMax.Y)
            {
                return false;
            }

            // the walk's own height test (a tall object above the walk's max Z)
            int maxObjectZ = obj.PriorityZ;
            CalculateObjectHeight(ref maxObjectZ, ref data);

            return maxObjectZ <= GPU_WALK_MAX_Z;
        }

        private bool MobileMissing(Mobile mob, Point vMin, Point vMax, int offX, int offY)
        {
            if (mob.IsDestroyed || mob.DrawnFrame == _drawFrame || !mob.AllowedToDraw || mob.Z >= _maxZ || mob.FrameInfo.Width == 0)
            {
                return false;
            }

            ViewMargin.Anchor(mob.X, mob.Y, mob.Z, offX, offY, out int ax, out int ay);
            int x0 = ax - mob.FrameInfo.X + 22, y0 = ay - mob.FrameInfo.Y + 22;

            return x0 + mob.FrameInfo.Width >= vMin.X && x0 <= vMax.X && y0 + mob.FrameInfo.Height >= vMin.Y && y0 <= vMax.Y;
        }

        private void LogMissing(GameObject obj)
        {
            if (_smLogged < 30)
            {
                _smLogged++;
                Log.Warn($"[view-margin-check] sprite reaching the view not drawn ({(_vmSprites ? "on" : "off")}): {obj.GetType().Name} 0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z}");
            }
        }
    }
}
#endif
