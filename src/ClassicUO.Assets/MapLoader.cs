// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility.Logging;
using System;
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
                _regions.Clear();
                _lru.Clear();
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
                var last = _lru.Last;
                _lru.RemoveLast();
                _regions.Remove(last.Value.Key);
            }

            return region;
        }

        private RegionData LoadRegion(FacetInfo facet, int rx, int ry)
        {
            long key = ((long)facet.Index << 40) | ((long)rx << 20) | (uint)ry;
            var path = PackConst.RegionPath(FileManager.Pack.PathOf(Path.Combine("map", facet.Index.ToString())), rx, ry);

            if (!File.Exists(path))
            {
                return new RegionData(key, null);
            }

            return new RegionData(key, RegionFile.Decompress(File.ReadAllBytes(path)));
        }

        public sealed class RegionData
        {
            private readonly byte[] _logical;
            private readonly RegionHeader _header;

            internal RegionData(long key, byte[] logical)
            {
                Key = key;
                _logical = logical;

                if (logical != null)
                {
                    _header = MemoryMarshal.Read<RegionHeader>(logical.AsSpan(RegionHeaderOffset));
                }
            }

            internal long Key { get; }

            public int BlocksW => _header.BlocksW;
            public int BlocksH => _header.BlocksH;

            public bool TryGetBlock(int x, int y, out MapBlockData block)
            {
                block = default;

                if (_logical == null || (uint)x >= _header.BlocksW || (uint)y >= _header.BlocksH)
                {
                    return false;
                }

                uint offset = MemoryMarshal.Read<uint>(_logical.AsSpan(BlockTableOffset + (y * _header.BlocksW + x) * 4));

                if (offset == 0)
                {
                    return false;
                }

                var span = _logical.AsSpan((int)offset);
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
