// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        private int _lightMargin = -1;

        // chunk_lights (ChunkLights): the frame's static lights from the chunks' emitter lists (LightEmitters); the walk adds
        // none: an emitter the walk drew this frame, and one beside the walked rectangle whose halo reaches into it (with the
        // ground items there, which the walk did not visit). AddLight drops the ones whose halo cannot reach the view. No
        // allocation; per frame it touches the loaded chunks of the band's rectangle, their light lists and their dynamics.
        private void AddChunkLights()
        {
            if (_lightMargin < 0)
            {
                _lightMargin = ViewMargin.LightMargin(Client.Game.UO.FileManager.Lights.MaxExtent(LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT));
            }

            int margin = _lightMargin;
            // the view before the sprite margin: a light source in a tile the sprite margin walks but did not draw is a band candidate
            int left = (int)_viewMinPixel.X, top = (int)_viewMinPixel.Y, right = (int)_viewMaxPixel.X, bottom = (int)_viewMaxPixel.Y;
            int offX = _offset.X, offY = _offset.Y;
            ViewMargin.Bands(left - margin, top - margin, right + margin, bottom + margin, offX, offY, out int dLo, out int dHi, out int sLo, out int sHi);
            ViewMargin.ChunkRange(dLo, dHi, sLo, sHi, out int cx0, out int cy0, out int cx1, out int cy1);
            Map.Map map = _world.Map;
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;

            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    if (!ViewMargin.ChunkTouches(cx, cy, dLo, dHi, sLo, sHi))
                    {
                        continue;
                    }

                    Chunk chunk = map.PeekChunkAt(cx, cy);

                    if (chunk == null)
                    {
                        continue;
                    }

                    for (int i = 0, n = chunk.LightSourceCount; i < n; i++)
                    {
                        TileListObject obj = chunk.LightSources[i];

                        if (ChunkLights.LightsDrawn(obj.DrawnFrame, _drawFrame, obj.IsDestroyed))
                        {
                            AddLight(obj, obj, obj.RealScreenPosition.X + 22, obj.RealScreenPosition.Y + 22);

                            continue;
                        }

                        if (EdgeLightCandidate(obj, staticData, left, top, right, bottom, margin, out int lx, out int ly))
                        {
                            AddLight(obj, obj, lx, ly);
                        }
                    }

                    DynamicTileIndex dyn = chunk.Dynamic;

                    if (dyn == null || dyn.Count == 0)
                    {
                        continue;
                    }

                    for (ulong cells = dyn.Cells; cells != 0; cells &= cells - 1)
                    {
                        int cell = System.Numerics.BitOperations.TrailingZeroCount(cells);
                        DynamicTileIndex.Entry[] entries = dyn.RawCell(cell, out int entryCount);

                        for (int e = 0; e < entryCount; e++)
                        {
                            if (entries[e].Obj is Item item && EdgeItemLightCandidate(item, staticData, left, top, right, bottom, margin, out int lx, out int ly))
                            {
                                AddLight(item, item, lx, ly);
                            }
                        }
                    }
                }
            }
        }

        // a light static or multi component beside the walked rectangle that the walk would light if it reached it: its light
        // draws a shape with this frame's colored-lights setting (LightEmitters.StaticDrawsNow) and the meshed walk's tests (GameSceneDrawingSorting: max Z,
        // hidden roofs, the object's height against the walk's max Z), without the alpha, which the walk updates only for
        // objects it visits
        private bool EdgeLightCandidate(TileListObject obj, StaticTiles[] staticData, int left, int top, int right, int bottom, int margin, out int lx, out int ly)
        {
            lx = ly = 0;

            if (obj.IsDestroyed || obj.DrawnFrame == _drawFrame || (uint)obj.Graphic >= (uint)staticData.Length)
            {
                return false;
            }

            ViewMargin.Anchor(obj.X, obj.Y, obj.Z, _offset.X, _offset.Y, out int ax, out int ay);

            if (!ViewMargin.InBand(ax, ay, left, top, right, bottom, margin))
            {
                return false;
            }

            ref StaticTiles data = ref staticData[obj.Graphic];

            if (!data.IsLight || !LightEmitters.StaticDrawsNow(obj.Graphic, data.Layer) || !EdgeVisible(obj, ref data))
            {
                return false;
            }

            lx = ax + 22;
            ly = ay + 22;

            return true;
        }

        // a ground item beside the walked rectangle whose view would add a light (ItemSpriteExtract: tiledata Light, or the
        // graphics AddLight gives light 2)
        private bool EdgeItemLightCandidate(Item item, StaticTiles[] staticData, int left, int top, int right, int bottom, int margin, out int lx, out int ly)
        {
            lx = ly = 0;

            if (item.IsDestroyed || item.DrawnFrame == _drawFrame || item.IsMulti || item.IsCorpse)
            {
                return false;
            }

            ushort graphic = item.Graphic;
            ushort displayed = item.DisplayedGraphicOf(graphic);

            if ((uint)graphic >= (uint)staticData.Length)
            {
                return false;
            }

            ref StaticTiles data = ref staticData[graphic];

            if (!(data.IsLight && LightEmitters.ItemDrawsNow(item.LightID) || displayed >= 0x3E02 && displayed <= 0x3E0B || displayed >= 0x3914 && displayed <= 0x3929))
            {
                return false;
            }

            ViewMargin.Anchor(item.X, item.Y, item.Z, _offset.X, _offset.Y, out int ax, out int ay);

            if (!ViewMargin.InBand(ax, ay, left, top, right, bottom, margin) || !EdgeVisible(item, ref data))
            {
                return false;
            }

            lx = ax + 22;
            ly = ay + 22;

            return true;
        }

        private bool EdgeVisible(GameObject obj, ref StaticTiles data)
        {
            if (data.IsInternal || obj.Z >= _maxZ || _noDrawRoofs && data.IsRoof)
            {
                return false;
            }

            int maxObjectZ = obj.PriorityZ;

            if (obj.AllowedToDraw)
            {
                CalculateObjectHeight(ref maxObjectZ, ref data);
            }

            return maxObjectZ <= GPU_WALK_MAX_Z;
        }
    }
}
