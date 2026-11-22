// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        private int _lightMargin = -1;

        // view_margin (ViewMargin): adds the light sources the walk left out because their anchor is beside the walked
        // rectangle, though their halo reaches into it. Runs in the light pass, after the walk and the world draw added theirs,
        // so the lights in view keep the first places of the 100-light cap. No allocation; per frame it touches the loaded
        // chunks of the band's rectangle, their light lists and their dynamics.
        private void AddEdgeLights()
        {
            bool on = ViewMargin.Enabled;

            if (!on && !ViewMargin.Check)
            {
                return;
            }

            if (_lightMargin < 0)
            {
                _lightMargin = ViewMargin.LightMargin(Client.Game.UO.FileManager.Lights.MaxExtent(LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT));
            }

            int margin = _lightMargin;
            int left = (int)_minPixel.X, top = (int)_minPixel.Y, right = (int)_maxPixel.X, bottom = (int)_maxPixel.Y;
            int offX = _offset.X, offY = _offset.Y;
            ViewMargin.Bands(left - margin, top - margin, right + margin, bottom + margin, offX, offY, out int dLo, out int dHi, out int sLo, out int sHi);
            ViewMargin.ChunkRange(dLo, dHi, sLo, sHi, out int cx0, out int cy0, out int cx1, out int cy1);
            Map.Map map = _world.Map;
            StaticTiles[] staticData = Client.Game.UO.FileManager.TileData.StaticData;
            int count = 0;
            uint hash = 0;

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

                        if (EdgeLightCandidate(obj, staticData, left, top, right, bottom, margin, out int lx, out int ly))
                        {
                            count++;
                            hash += ViewMargin.Mix(obj.X, obj.Y, obj.Z, obj.Graphic, false);

                            if (on)
                            {
                                AddLight(obj, obj, lx, ly);
                            }
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
                                count++;
                                hash += ViewMargin.Mix(item.X, item.Y, item.Z, item.Graphic, true);

                                if (on)
                                {
                                    AddLight(item, item, lx, ly);
                                }
                            }
                        }
                    }
                }
            }

#if CUO_DEVTOOLS
            if (ViewMargin.Check)
            {
                CheckEdgeLights(on, count, hash, staticData, left, top, right, bottom, margin, cx0, cy0, cx1, cy1);
            }
#endif
        }

        // a light static or multi component beside the walked rectangle that the walk would light if it reached it: the
        // meshed walk's tests (GameSceneDrawingSorting: max Z, hidden roofs, the object's height against the walk's max Z),
        // without the alpha, which the walk updates only for objects it visits
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

            if (!data.IsLight || !EdgeVisible(obj, ref data))
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

            if (!(data.IsLight || displayed >= 0x3E02 && displayed <= 0x3E0B || displayed >= 0x3914 && displayed <= 0x3929))
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

#if CUO_DEVTOOLS
        private long _vmFrames, _vmFramesWithEdge, _vmCandidates, _vmMismatches, _vmCapFrames, _vmRefused;
        private int _vmMaxLights;
        private uint _vmSummaryAt;

        // CUO_VIEW_MARGIN_CHECK: the reference walks every tile list and dynamic of the chunk rectangle (the extended walk the
        // chunk light lists stand in for) with the same tests; a different count or hash means a light list missed or kept
        // an object (a static added or removed outside AddGameObject/RemoveFromTile, a graphic changed in place)
        private void CheckEdgeLights(bool on, int count, uint hash, StaticTiles[] staticData, int left, int top, int right, int bottom, int margin,
            int cx0, int cy0, int cx1, int cy1)
        {
            Map.Map map = _world.Map;
            int refCount = 0;
            uint refHash = 0;
            GameObject sample = null;

            for (int cx = cx0; cx <= cx1; cx++)
            {
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    Chunk chunk = map.PeekChunkAt(cx, cy);

                    if (chunk == null)
                    {
                        continue;
                    }

                    for (int cell = 0; cell < 64; cell++)
                    {
                        for (TileListObject o = chunk.HeadAt(cell); o != null; o = o.TNext)
                        {
                            if ((o is Static || o is Multi) && EdgeLightCandidate(o, staticData, left, top, right, bottom, margin, out _, out _))
                            {
                                refCount++;
                                refHash += ViewMargin.Mix(o.X, o.Y, o.Z, o.Graphic, false);

                                if (!ContainsLightSource(chunk, o))
                                {
                                    sample ??= o;
                                }
                            }
                        }

                        if (chunk.Dynamic == null)
                        {
                            continue;
                        }

                        foreach (ref readonly DynamicTileIndex.Entry en in chunk.Dynamic.Cell(cell))
                        {
                            if (en.Obj is Item item && EdgeItemLightCandidate(item, staticData, left, top, right, bottom, margin, out _, out _))
                            {
                                refCount++;
                                refHash += ViewMargin.Mix(item.X, item.Y, item.Z, item.Graphic, true);
                            }
                        }
                    }
                }
            }

            _vmFrames++;
            _vmCandidates += count;

            if (count != 0)
            {
                _vmFramesWithEdge++;
            }

            if (_frameLights.Refused != 0)
            {
                _vmCapFrames++;
                _vmRefused += _frameLights.Refused;
            }

            _vmMaxLights = System.Math.Max(_vmMaxLights, _frameLights.Count + _frameLights.Refused);

            if (refCount != count || refHash != hash)
            {
                _vmMismatches++;

                if (_vmMismatches <= 20)
                {
                    Log.Warn($"[view-margin-check] edge lights {count} (hash {hash:X8}), reference walk {refCount} (hash {refHash:X8}); " +
                             (sample != null ? $"not in its chunk's light list: {sample.GetType().Name} 0x{sample.Graphic:X4} at {sample.X},{sample.Y},{sample.Z}" : "no static missing from a light list"));
                }
            }

            if (Time.Ticks >= _vmSummaryAt)
            {
                _vmSummaryAt = Time.Ticks + 5000;
                Log.Info($"[view-margin-check] view_margin {(on ? "on" : "off")}, light margin {margin} px: {_vmFrames} frames, {_vmFramesWithEdge} with edge lights " +
                         $"({_vmCandidates} candidates, {(on ? "added up to the cap" : "not added")}), light cap {_frameLights.Limit}: {_vmCapFrames} frames dropped {_vmRefused} lights, most lights offered in a frame {_vmMaxLights}; mismatches {_vmMismatches}");
            }
        }

        private static bool ContainsLightSource(Chunk chunk, TileListObject obj)
        {
            for (int i = 0; i < chunk.LightSourceCount; i++)
            {
                if (ReferenceEquals(chunk.LightSources[i], obj))
                {
                    return true;
                }
            }

            return false;
        }
#endif
    }
}
