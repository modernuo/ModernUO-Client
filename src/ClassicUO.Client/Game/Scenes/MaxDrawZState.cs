// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// GameScene.UpdateMaxDrawZ's decision (the max Z drawn, the ground cutoff and "under a roof", shared by both world
    /// renderers) and when it runs again. Upstream recomputed only when the player's tile or z changed, which was enough
    /// while every chunk loaded the moment it was first asked for. With incremental chunk loading (Map.UpdateLoading) a
    /// teleport's first frame can come before the player's chunk is loaded: the decision then saw no statics, said "not
    /// under a roof" (max Z 127), and kept that answer until the player moved (known-issues "Gpu world renderer: the roof
    /// over the player is sometimes drawn").
    ///
    /// The decision is kept while all of its inputs are unchanged: the player's x/y/z, DrawRoofs, the Map instance (a facet
    /// change replaces it) and the contents of every chunk it looked up, including the chunks the recursive roof search
    /// (Map.CalculateNearZ) read and the ones it found not loaded (Map.DecisionDirty, set by object and chunk changes in
    /// that reach and by World.ChangeSeason). Changes are coalesced: whatever came in since the last frame, the decision runs
    /// once, in the next GetViewPort, before the draw. Nothing here runs per frame beyond the comparison.
    /// </summary>
    internal sealed class MaxDrawZState
    {
        private int _x, _y, _z, _mapId;
        private bool _drawRoofs, _valid;

        public int MaxZ = 127;
        public sbyte MaxGroundZ = 127;
        public bool NoDrawRoofs;

        /// <summary>The last decision's chunk lookups that found the chunk not loaded (0: complete).</summary>
        public int Missing { get; private set; }

        /// <summary>Decisions computed (diagnostics, tests).</summary>
        public int Computations { get; private set; }

        public bool NeedsUpdate(Map.Map map, int x, int y, int z, bool drawRoofs) =>
            !_valid || map.DecisionDirty || map.Id != _mapId || x != _x || y != _y || z != _z || drawRoofs != _drawRoofs;

        /// <summary>
        /// Recomputes the decision when <paramref name="force"/> is set or an input changed (<see cref="NeedsUpdate"/>);
        /// returns whether it did.
        /// </summary>
        public bool Update(Map.Map map, StaticTiles[] tiles, int playerX, int playerY, int playerZ, bool drawRoofs, bool force)
        {
            if (!force && !NeedsUpdate(map, playerX, playerY, playerZ, drawRoofs))
            {
                return false;
            }

            var reach = new ChunkReach();
            Compute(map, tiles, playerX, playerY, playerZ, drawRoofs, DynamicTileIndex.WalkOn, ref reach);

            if (TileOrder.Check)
            {
                // T6b part 2: the same decision with the other walk (own reach: the first one is what the map keeps)
                int maxZ = MaxZ;
                sbyte maxGroundZ = MaxGroundZ;
                bool noDrawRoofs = NoDrawRoofs;
                var reach2 = new ChunkReach();
                Compute(map, tiles, playerX, playerY, playerZ, drawRoofs, !DynamicTileIndex.WalkOn, ref reach2);
                TileOrder.ReaderCheck(
                    maxZ == MaxZ && maxGroundZ == MaxGroundZ && noDrawRoofs == NoDrawRoofs && reach.Missing == reach2.Missing,
                    "MaxDrawZ",
                    () => $"at {playerX},{playerY},{playerZ}: maxZ {maxZ}/{MaxZ} ground {maxGroundZ}/{MaxGroundZ} noDrawRoofs {noDrawRoofs}/{NoDrawRoofs}"
                );
                MaxZ = maxZ;
                MaxGroundZ = maxGroundZ;
                NoDrawRoofs = noDrawRoofs;
            }

            map.SetDecisionReach(reach);

            _x = playerX;
            _y = playerY;
            _z = playerZ;
            _drawRoofs = drawRoofs;
            _mapId = map.Id;
            _valid = true;
            Missing = reach.Missing;
            Computations++;

            return true;
        }

        // the upstream decision, unchanged but for the tracked chunk lookups (Map.GetChunkForDecision)
        private void Compute(Map.Map map, StaticTiles[] tiles, int playerX, int playerY, int playerZ, bool drawRoofs, bool merged, ref ChunkReach reach)
        {
            sbyte maxGroundZ = 127;
            MaxGroundZ = 127;
            MaxZ = 127;
            NoDrawRoofs = !drawRoofs;
            Chunk chunk = map.GetChunkForDecision(playerX, playerY, ref reach);

            if (chunk == null)
            {
                return;
            }

            int x = playerX % 8;
            int y = playerY % 8;

            int pz14 = playerZ + 14;
            int pz16 = playerZ + 16;

            TileReader reader;

            for (GameObject obj = TileReader.Open(chunk, x << 3 | y, merged, null, out reader); obj != null; obj = reader.Next())
            {
                sbyte tileZ = obj.Z;

                if (obj is Land l)
                {
                    if (l.IsStretched)
                    {
                        tileZ = l.AverageZ;
                    }

                    if (pz16 <= tileZ)
                    {
                        maxGroundZ = (sbyte)pz16;
                        MaxGroundZ = (sbyte)pz16;
                        MaxZ = MaxGroundZ;

                        break;
                    }

                    continue;
                }

                if (obj is Mobile)
                {
                    continue;
                }

                if (tileZ > pz14 && MaxZ > tileZ)
                {
                    ref StaticTiles itemdata = ref tiles[obj.Graphic];

                    if (((ulong)itemdata.Flags & 0x20004) == 0 && (!itemdata.IsRoof || itemdata.IsSurface))
                    {
                        MaxZ = tileZ;
                        NoDrawRoofs = true;
                    }
                }
            }

            int tempZ = MaxZ;
            MaxGroundZ = (sbyte)MaxZ;
            playerX++;
            playerY++;
            chunk = map.GetChunkForDecision(playerX, playerY, ref reach);

            if (chunk != null)
            {
                x = playerX % 8;
                y = playerY % 8;

                for (GameObject obj2 = TileReader.Open(chunk, x << 3 | y, merged, null, out reader); obj2 != null; obj2 = reader.Next())
                {
                    if (obj2 is Mobile)
                    {
                        continue;
                    }

                    sbyte tileZ = obj2.Z;

                    if (tileZ > pz14 && MaxZ > tileZ)
                    {
                        if (!(obj2 is Land))
                        {
                            ref StaticTiles itemdata = ref tiles[obj2.Graphic];

                            if (((ulong)itemdata.Flags & 0x204) == 0 && itemdata.IsRoof)
                            {
                                MaxZ = tileZ;
                                map.ClearBockAccess();
                                MaxGroundZ = map.CalculateNearZ(tileZ, playerX, playerY, tileZ, tiles, ref reach);
                                NoDrawRoofs = true;
                            }
                        }
                    }
                }

                tempZ = MaxGroundZ;
            }

            MaxZ = MaxGroundZ;

            if (tempZ < pz16)
            {
                MaxZ = pz16;
                MaxGroundZ = (sbyte)pz16;
            }

            MaxGroundZ = maxGroundZ;
        }
    }
}
