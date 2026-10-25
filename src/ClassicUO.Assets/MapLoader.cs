// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public class MapLoader : UOFileLoader
    {
        private const int CACHED_REGIONS = 24;

        private static readonly int RegionHeaderOffset = StructIo.SizeOf<FileHeader>();
        private static readonly int BlockTableOffset = RegionHeaderOffset + StructIo.SizeOf<RegionHeader>();

        public static int MAPS_COUNT = 6;

        private readonly Dictionary<long, LinkedListNode<RegionData>> _regions = new Dictionary<long, LinkedListNode<RegionData>>();
        private readonly LinkedList<RegionData> _lru = new LinkedList<RegionData>();
        private readonly object _sync = new object();
        private FacetInfo[] _facets = Array.Empty<FacetInfo>();

        public MapLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public int[,] MapBlocksSize { get; private set; }

        public int[,] MapsDefaultSize { get; private set; }

        public override void Load()
        {
            int count = 0;

            foreach (var f in FileManager.Pack.Manifest.Facets)
            {
                count = Math.Max(count, f.Index + 1);
            }

            if (count == 0)
            {
                throw new FileNotFoundException("The asset pack has no maps.");
            }

            MAPS_COUNT = Math.Max(count, 2);
            _facets = new FacetInfo[MAPS_COUNT];
            MapsDefaultSize = new int[MAPS_COUNT, 2];
            MapBlocksSize = new int[MAPS_COUNT, 2];

            foreach (var f in FileManager.Pack.Manifest.Facets)
            {
                _facets[f.Index] = f;
            }

            // a client without map1 draws Trammel from map0, as ClassicUO did with the MUL files
            if (_facets[1] == null)
            {
                _facets[1] = _facets[0];
            }

            for (int i = 0; i < MAPS_COUNT; i++)
            {
                var f = _facets[i];

                if (f == null)
                {
                    continue;
                }

                MapsDefaultSize[i, 0] = f.Width;
                MapsDefaultSize[i, 1] = f.Height;
                MapBlocksSize[i, 0] = f.BlocksX;
                MapBlocksSize[i, 1] = f.BlocksY;
            }
        }

        /// <summary>Map patch packets (mapdif/stadif) are not supported: shard owners merge patches before converting.</summary>
        public bool ApplyPatches(ref StackDataReader reader)
        {
            uint maps = reader.ReadUInt32BE();

            for (uint i = 0; i < maps && reader.Remaining >= 8; i++)
            {
                uint statics = reader.ReadUInt32BE();
                uint land = reader.ReadUInt32BE();

                if (statics != 0 || land != 0)
                {
                    Log.Warn($"Server sent map patches for map {i} ({land} land, {statics} statics); patches are not supported with asset packs and are ignored");
                }
            }

            return false;
        }

        public bool HasMap(int map) => (uint)map < (uint)_facets.Length && _facets[map] != null;

        public void LoadMap(int map, bool useXFiles = false)
        {
            if (!HasMap(map))
            {
                Log.Warn($"Map {map} is not in the asset pack");
            }
        }

        public void SanitizeMapIndex(ref int map)
        {
            if (!HasMap(map))
            {
                map = 0;
            }
        }

        public bool TryGetBlock(int map, int blockX, int blockY, out MapBlockData block)
        {
            block = default;

            if (!HasMap(map))
            {
                return false;
            }

            var facet = _facets[map];

            if ((uint)blockX >= (uint)facet.BlocksX || (uint)blockY >= (uint)facet.BlocksY)
            {
                return false;
            }

            int side = facet.RegionBlocks;
            RegionData region;

            lock (_sync)
            {
                region = GetRegion(facet, blockX / side, blockY / side);
            }

            return region != null && region.TryGetBlock(blockX % side, blockY % side, out block);
        }

        /// <summary>
        /// The land z of cell (x, y), or -125 when the cell has no map block (as Map.GetTileZ). The last block looked up
        /// is remembered, so the stretch calculation's 13 neighbour lookups per land cell mostly skip the lock and the
        /// region lookup. Main thread only (the cache is not synchronised).
        /// </summary>
        public sbyte GetLandZ(int map, int x, int y)
        {
            if (x < 0 || y < 0)
            {
                return -125;
            }

            int bx = x >> 3, by = y >> 3;
            var c = _zCache;

            if (c == null || c.Map != map || c.BlockX != bx || c.BlockY != by || (c.Region != null && !c.Region.Live))
            {
                c = new LandZCache { Map = map, BlockX = bx, BlockY = by };

                if (HasMap(map) && (uint)bx < (uint)_facets[map].BlocksX && (uint)by < (uint)_facets[map].BlocksY)
                {
                    var facet = _facets[map];
                    int side = facet.RegionBlocks;

                    lock (_sync)
                    {
                        c.Region = GetRegion(facet, bx / side, by / side);
                    }

                    c.Offset = c.Region.BlockOffset(bx % side, by % side);
                }

                _zCache = c;
            }

            return c.Offset == 0 ? (sbyte)-125 : c.Region.LandZ(c.Offset, ((y & 7) << 3) + (x & 7));
        }

        private sealed class LandZCache
        {
            public int Map, BlockX, BlockY;
            public RegionData Region;
            public int Offset; // 0 = no block
        }

        private LandZCache _zCache;

        /// <summary>Reads one region without caching it, for bulk readers such as the world map.</summary>
        public RegionData ReadRegion(int map, int rx, int ry)
        {
            return HasMap(map) ? LoadRegion(_facets[map], rx, ry) : null;
        }

        public int RegionBlocks(int map) => HasMap(map) ? _facets[map].RegionBlocks : PackConst.RegionBlocks;

        /// <summary>A hash of the facet's region files, from the pack manifest.</summary>
        public string GetFacetHash(int map)
        {
            if (!HasMap(map))
            {
                return "";
            }

            var prefix = $"map/{_facets[map].Index}/";
            var sb = new StringBuilder();

            foreach (var f in FileManager.Pack.Manifest.Files)
            {
                if (f.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    sb.Append(f.Path).Append(f.Sha256);
                }
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32].ToLowerInvariant();
        }

        public override void ClearResources()
        {
            lock (_sync)
            {
                foreach (var region in _lru)
                {
                    region.Live = false;
                }

                _regions.Clear();
                _lru.Clear();
                _zCache = null;
            }
        }

        private RegionData GetRegion(FacetInfo facet, int rx, int ry)
        {
            long key = ((long)facet.Index << 40) | ((long)rx << 20) | (uint)ry;

            if (_regions.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);

                return node.Value;
            }

            var region = LoadRegion(facet, rx, ry);
            node = _lru.AddFirst(region);
            _regions[key] = node;

            if (_lru.Count > CACHED_REGIONS)
            {
                // the evicted region's buffer goes back to the pool for the next load (the cache and TryGetBlock's
                // callers are main-thread; a block span never outlives 24 further region loads)
                var last = _lru.Last;
                _lru.RemoveLast();
                _regions.Remove(last.Value.Key);
                last.Value.Live = false;
                ReturnBuffer(last.Value.Release());
            }

            return region;
        }

        // logical region buffers (about 1.2 MB each) reused across loads
        private readonly Stack<byte[]> _freeBuffers = new Stack<byte[]>();
        private const int MaxFreeBuffers = 4;

        private byte[] RentBuffer()
        {
            lock (_freeBuffers)
            {
                return _freeBuffers.Count > 0 ? _freeBuffers.Pop() : null;
            }
        }

        private void ReturnBuffer(byte[] buffer)
        {
            if (buffer == null)
            {
                return;
            }

            lock (_freeBuffers)
            {
                if (_freeBuffers.Count < MaxFreeBuffers)
                {
                    _freeBuffers.Push(buffer);
                }
            }
        }

        private RegionData LoadRegion(FacetInfo facet, int rx, int ry)
        {
            long key = ((long)facet.Index << 40) | ((long)rx << 20) | (uint)ry;
            var path = PackConst.RegionPath(FileManager.Pack.PathOf(Path.Combine("map", facet.Index.ToString())), rx, ry);

            if (!File.Exists(path))
            {
                return new RegionData(key, null, 0);
            }

            // the compressed file goes into a pooled buffer and decodes straight into a pooled logical buffer: no
            // File.ReadAllBytes, MemoryStream or ToArray copies (those were about 3.9 MB of garbage per region)
            using var fh = File.OpenHandle(path);
            int size = (int)RandomAccess.GetLength(fh);
            var file = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                RandomAccess.Read(fh, file.AsSpan(0, size), 0);
                var logical = RentBuffer();
                int length = RegionFile.DecompressInto(file.AsSpan(0, size), ref logical);

                return new RegionData(key, logical, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(file);
            }
        }

        public sealed class RegionData
        {
            private byte[] _logical;
            private readonly int _length;
            private readonly RegionHeader _header;

            internal RegionData(long key, byte[] logical, int length)
            {
                Key = key;
                _logical = logical;
                _length = length;
                Live = true;

                if (logical != null)
                {
                    _header = MemoryMarshal.Read<RegionHeader>(logical.AsSpan(RegionHeaderOffset));
                }
            }

            internal long Key { get; }

            /// <summary>False once evicted from the cache (its buffer may then hold another region).</summary>
            internal bool Live { get; set; }

            public int BlocksW => _header.BlocksW;
            public int BlocksH => _header.BlocksH;

            internal byte[] Release()
            {
                var buffer = _logical;
                _logical = null;

                return buffer;
            }

            /// <summary>Byte offset of block (x, y) in the logical file, 0 when the block is empty or outside the region.</summary>
            internal int BlockOffset(int x, int y)
            {
                if (_logical == null || (uint)x >= _header.BlocksW || (uint)y >= _header.BlocksH)
                {
                    return 0;
                }

                return (int)MemoryMarshal.Read<uint>(_logical.AsSpan(BlockTableOffset + (y * _header.BlocksW + x) * 4));
            }

            /// <summary>Land z of cell <paramref name="cell"/> (y * 8 + x) of the block at <paramref name="offset"/>.</summary>
            internal sbyte LandZ(int offset, int cell) => (sbyte)_logical[offset + cell * 4 + 2];

            public bool TryGetBlock(int x, int y, out MapBlockData block)
            {
                block = default;
                int offset = BlockOffset(x, y);

                if (offset == 0 || offset >= _length)
                {
                    return false;
                }

                var span = _logical.AsSpan(offset, _length - offset);
                var land = MemoryMarshal.Cast<byte, LandCell>(span.Slice(0, PackConst.CellsPerBlock * 4));
                int statics = MemoryMarshal.Read<ushort>(span.Slice(PackConst.CellsPerBlock * 4));
                var cells = MemoryMarshal.Cast<byte, StaticCell>(span.Slice(PackConst.CellsPerBlock * 4 + 4, statics * 8));
                block = new MapBlockData(land, cells);

                return true;
            }
        }
    }

    public readonly ref struct MapBlockData
    {
        public MapBlockData(ReadOnlySpan<LandCell> land, ReadOnlySpan<StaticCell> statics)
        {
            Land = land;
            Statics = statics;
        }

        /// <summary>64 cells, row-major (y * 8 + x).</summary>
        public readonly ReadOnlySpan<LandCell> Land;
        public readonly ReadOnlySpan<StaticCell> Statics;
    }
}
