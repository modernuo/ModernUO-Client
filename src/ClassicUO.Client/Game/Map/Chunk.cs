// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;
using ClassicUO.Utility;
using System;
using System.Runtime.InteropServices;
using System.Buffers;

namespace ClassicUO.Game.Map
{
    internal sealed class Chunk
    {
        private static readonly Queue<Chunk> _pool = new Queue<Chunk>();

        private readonly World _world;

        public Chunk(World world)
        {
            _world = world;
        }

        public GameObject[,] Tiles { get; } = new GameObject[8, 8];
        public bool IsDestroyed;
        public long LastAccessTime;
        public LinkedListNode<int> Node;
        public readonly ChunkMesh Mesh = new ChunkMesh();

        public int X;
        public int Y;

        // Gpu world prepare (GameSceneGpuPrepare): which tiles (bit x * 8 + y) still need the per-frame walk. Rebuilt from
        // the tile lists when dirty (objects added or removed, mesh rebuilt, text added to a land tile or static).
        public bool GpuMaskDirty = true;
        public int GpuMaskGeneration;
        public ulong GpuWalkMask, GpuFullMask;
        // extents of the chunk's land and statics, for the targeted picking and screen-edge bands
        public int GpuMinZ, GpuMaxZ, GpuMaxHalfWidth, GpuMaxHeight;
        public uint GpuSeenFrame = uint.MaxValue - 1;
        public int GpuFreshSteps;
        // W4 (GameSceneGpuItemCache): the chunk's settled ground items' sprite instances; GpuItemsDirty = re-extract
        public Scenes.GpuItemCache GpuItems;
        public bool GpuItemsDirty = true;
        // tiles holding a mobile, and a mobile the walk's max Z stops at (rebuilt with the masks); a mobile on a cached
        // item tile is walked on its own
        public ulong GpuMobileMask, GpuMobileStopMask;

        /// <summary>An object joined or left one of the chunk's tile lists. Mobiles don't change which items are cached.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnTileContentChanged(GameObject obj)
        {
            GpuMaskDirty = true;

            if (obj is not Mobile)
            {
                GpuItemsDirty = true;
            }
        }


        public static Chunk Create(World world, int x, int y)
        {
            Chunk c;
            if (_pool.Count > 0)
            {
                c = _pool.Dequeue();
                c.IsDestroyed = false;
            }
            else
            {
                c = new Chunk(world);
            }

            c.LastAccessTime = Time.Ticks + Constants.CLEAR_TEXTURES_DELAY;
            c.X = x;
            c.Y = y;
            c.GpuMaskDirty = true;
            c.GpuItemsDirty = true;
            c.GpuSeenFrame = uint.MaxValue - 1;

            return c;
        }

        public static void ClearPool()
        {
            while (_pool.Count > 0)
            {
                var chunk = _pool.Dequeue();
                chunk.Mesh.Clear();
            }
        }


        public void Load(int index)
        {
            IsDestroyed = false;

            Map map = _world.Map;

            if (!Client.Game.UO.FileManager.Maps.TryGetBlock(index, X, Y, out var block))
            {
                return;
            }

            int bx = X << 3;
            int by = Y << 3;

            for (int y = 0; y < 8; ++y)
            {
                int pos = y << 3;
                ushort tileY = (ushort)(by + y);

                for (int x = 0; x < 8; ++x, ++pos)
                {
                    ushort tileID = block.Land[pos].TileId;
                    sbyte z = block.Land[pos].Z;

                    Land land = Land.Create(_world, tileID);

                    ushort tileX = (ushort)(bx + x);

                    land.ApplyStretch(map, tileX, tileY, z);
                    land.X = tileX;
                    land.Y = tileY;
                    land.Z = z;
                    land.UpdateScreenPosition();

                    AddGameObject(land, x, y);
                }
            }

            foreach (ref readonly var sb in block.Statics)
            {
                int pos = (sb.Y << 3) + sb.X;

                Static staticObject = Static.Create(_world, sb.Graphic, sb.Hue, pos);
                staticObject.X = (ushort)(bx + sb.X);
                staticObject.Y = (ushort)(by + sb.Y);
                staticObject.Z = sb.Z;
                staticObject.UpdateScreenPosition();

                AddGameObject(staticObject, sb.X, sb.Y);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameObject GetHeadObject(int x, int y)
        {
            GameObject obj = Tiles[x, y];

            while (obj?.TPrevious != null)
            {
                obj = obj.TPrevious;
            }

            return obj;
        }

        public void AddGameObject(GameObject obj, int x, int y)
        {
            Mesh.MarkDirtyIfNeeded(obj);
            obj.RemoveFromTile();
            OnTileContentChanged(obj);
            obj.TileChunk = this;

            short priorityZ = obj.Z;
            sbyte state = -1;

            ushort graphic = obj.Graphic;

            switch (obj)
            {
                case Land tile:

                    if (tile.IsStretched)
                    {
                        priorityZ = (short) (tile.AverageZ - 1);
                    }
                    else
                    {
                        priorityZ--;
                    }

                    priorityZ -= 1;

                    state = 0;

                    break;

                case Mobile _:
                    priorityZ++;

                    break;

                case Item item:

                    if (item.IsCorpse)
                    {
                        priorityZ++;

                        break;
                    }
                    else if (item.IsMulti)
                    {
                        graphic = item.MultiGraphic;
                    }

                    goto default;

                case GameEffect _:
                    priorityZ += 2;

                    break;

                case Multi m:

                    state = 1;

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0)
                    {
                        priorityZ--;

                        break;
                    }

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) != 0)
                    {
                        state = 2;
                        priorityZ++;
                    }
                    //else if ((m.ItemData.Flags & TileFlag.StairRight) != 0)
                    //{
                    //    priorityZ++;
                    //}

                    //if (m.IsMovable)
                    //{
                    //    priorityZ += 1;
                    //}

                    goto default;

                default:
                    ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[graphic];

                    if (data.IsBackground)
                    {
                        priorityZ--;
                    }

                    //if (data.IsSurface)
                    //{
                    //    priorityZ--;
                    //}

                    if (data.Height != 0)
                    {
                        priorityZ++;
                    }

                    if (data.IsMultiMovable)
                    {
                        priorityZ++;
                    }

                    break;
            }

            obj.PriorityZ = priorityZ;

            if (Tiles[x, y] == null)
            {
                Tiles[x, y] = obj;
                obj.TPrevious = null;
                obj.TNext = null;

                return;
            }


            GameObject o = Tiles[x, y];

            if (o == obj)
            {
                if (o.Previous != null)
                {
                    o = (GameObject) o.Previous;
                }
                else if (o.Next != null)
                {
                    o = (GameObject) o.Next;
                }
                else
                {
                    return;
                }
            }

            while (o?.TPrevious != null)
            {
                o = o.TPrevious;
            }

            GameObject found = null;
            GameObject start = o;

            while (o != null)
            {
                int testPriorityZ = o.PriorityZ;

                if (testPriorityZ > priorityZ || testPriorityZ == priorityZ && (state == 0 || state == 1 && !(o is Land)))
                {
                    break;
                }

                found = o;
                o = o.TNext;
            }

            if (found != null)
            {
                obj.TPrevious = found;
                GameObject next = found.TNext;
                obj.TNext = next;
                found.TNext = obj;

                if (next != null)
                {
                    next.TPrevious = obj;
                }
            }
            else if (start != null)
            {
                obj.TNext = start;
                start.TPrevious = obj;
                obj.TPrevious = null;
            }
        }

        public void RemoveGameObject(GameObject obj, int x, int y)
        {
            Mesh.MarkDirtyIfNeeded(obj);
            OnTileContentChanged(obj);

            if (obj != null && obj.TileChunk == this)
            {
                obj.TileChunk = null;
            }
            ref GameObject firstNode = ref Tiles[x, y];

            if (firstNode == null || obj == null)
            {
                return;
            }

            if (firstNode == obj)
            {
                firstNode = obj.TNext;
            }

            if (obj.TNext != null)
            {
                obj.TNext.TPrevious = obj.TPrevious;
            }

            if (obj.TPrevious != null)
            {
                obj.TPrevious.TNext = obj.TNext;
            }

            obj.TPrevious = null;
            obj.TNext = null;
        }


        public void Destroy()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject obj = Tiles[i, j];

                    if (obj == null)
                    {
                        continue;
                    }

                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first.TileChunk = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                }
            }

            if (Node != null)
            {
                if (Node.Next != null || Node.Previous != null)
                {
                    Node.List?.Remove(Node);
                }

                Node = null;
            }

            IsDestroyed = true;
            GpuItems?.Reset();

            if (_pool.Count < Constants.PREDICTABLE_CHUNKS)
            {
                Mesh.SoftClear();
                _pool.Enqueue(this);
            }
            else
            {
                Mesh.Clear();
            }
        }

        public void Clear()
        {
            Destroy();
        }

        /// <summary>
        /// Clears the chunk's tile objects for an in-place reload (UltimaLive block
        /// update) and marks the mesh dirty so it rebuilds — WITHOUT destroying or
        /// pooling the chunk itself. The chunk stays owned by the map: its
        /// <see cref="_terrainChunks"/> slot and <see cref="Node"/> are left intact.
        /// Using <see cref="Destroy"/>/<see cref="Clear"/> here would enqueue this
        /// chunk to the shared pool while it is still referenced by the map, so it
        /// would later be handed out by <see cref="Create"/> for a different block
        /// (double ownership) — the original block then renders a stale/empty mesh
        /// (black, world-locked, permanent).
        /// </summary>
        public void ClearForReload()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first.TileChunk = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                }
            }

            Mesh.SoftClear();
            GpuItems?.Reset();
        }

        public bool HasNoExternalData()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    for (GameObject obj = GetHeadObject(i, j); obj != null; obj = obj.TNext)
                    {
                        if (!(obj is Land) && !(obj is Static) /*&& !(obj is Multi)*/)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}