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
        private void AddChunkLights(bool cached, bool recordStats = true)
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

            bool stats = recordStats && FrameStats.On;
            int chunks = 0, tiles = 0, objects = 0;

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

                    var sink = new SceneChunkLightSink
                    {
                        Scene = this, Left = left, Top = top, Right = right, Bottom = bottom, Margin = margin, Data = staticData
                    };
                    ChunkLightVisits visits = ChunkLightOffers.Collect(chunk, staticData, cached, stats, ref sink);
                    if (stats)
                    {
                        chunks++;
                        tiles += visits.Cells;
                        objects += visits.Objects;
                        FrameStats.Add(FrameCounter.EmitterRebuilds, visits.Rebuilds);
                        FrameStats.Add(FrameCounter.EmitterOverflows, visits.Overflows);
                        FrameStats.Add(FrameCounter.EmitterVisitedBytes, visits.VisitedBytes);
                    }
                }
            }

            if (stats)
            {
                FrameStats.Add(FrameCounter.ChunkLightChunks, chunks);
                FrameStats.Add(FrameCounter.ChunkLightTiles, tiles);
                FrameStats.Add(FrameCounter.ChunkLightObjects, objects);
            }
        }

        private struct SceneChunkLightSink : IChunkLightSink
        {
            internal GameScene Scene;
            internal int Left, Top, Right, Bottom, Margin;
            internal StaticTiles[] Data;

            public void Offer(GameObject obj, ChunkLightCache cache, int row, bool ground)
            {
                if (!ground && ChunkLights.LightsDrawn(obj.DrawnFrame, Scene._drawFrame, obj.IsDestroyed))
                {
                    Scene.AddLight(obj, obj, obj.RealScreenPosition.X + 22, obj.RealScreenPosition.Y + 22);
                    return;
                }
                int x, y;
                bool admitted = cache != null
                    ? cache.EdgeCandidate(row, obj, Scene._drawFrame, Scene._offset.X, Scene._offset.Y, Left, Top, Right, Bottom,
                        Margin, Scene._noDrawRoofs, Scene._maxZ, GPU_WALK_MAX_Z, out x, out y) && cache.DrawsNow(row)
                    : ground
                        ? Scene.EdgeItemLightCandidate((Item)obj, Data, Left, Top, Right, Bottom, Margin, out x, out y)
                        : Scene.EdgeLightCandidate((TileListObject)obj, Data, Left, Top, Right, Bottom, Margin, out x, out y);
                if (admitted) Scene.AddLight(obj, obj, x, y);
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

            return ChunkLights.EdgeVisible(obj.Z, maxObjectZ, data.IsInternal, data.IsRoof, _noDrawRoofs, false, _maxZ, GPU_WALK_MAX_Z);
        }
    }
}
