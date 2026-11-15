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
using System.Threading.Tasks;
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

        // regions being decoded on the thread pool (PrefetchRegion); adopted into the cache by the next lookup that needs
        // them, under _sync, so the cache itself is only changed by callers of the lookups (the main thread)
        private readonly Dictionary<long, Task<RegionData>> _pending = new Dictionary<long, Task<RegionData>>();

        /// <summary>
        /// False (CUO_CHUNK_SYNC=1) turns prefetching off: every region decodes on the calling thread when first needed,
        /// as before the incremental chunk loading.
        /// </summary>
        public static bool PrefetchEnabled = Environment.GetEnvironmentVariable("CUO_CHUNK_SYNC") != "1";

        /// <summary>Statistics for FrameStats and the tests: regions decoded by a lookup (not prefetched), prefetches
        /// started, lookups that had to wait for a prefetch still decoding, and the time spent waiting (Stopwatch ticks).</summary>
        public int SyncLoads, PrefetchesStarted, PrefetchWaits;
        public long PrefetchWaitTicks;

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

        /// <summary>Map patch packets (0xBF/0x18) are not applied at run time. The converter bakes mapdif/stadif into the pack
        /// for pre-UOP installs the way ModernUO serves them (land below 6.0.0.0, statics below 7.0.9.0; Uo.Converter
        /// ClientLoad.ApplyLegacyMapRules), so the counts the server sends describe patches the pack already holds.</summary>
        public bool ApplyPatches(ref StackDataReader reader)
        {
            uint maps = reader.ReadUInt32BE();

            for (uint i = 0; i < maps && reader.Remaining >= 8; i++)
            {
                uint statics = reader.ReadUInt32BE();
                uint land = reader.ReadUInt32BE();

                if (statics != 0 || land != 0)
                {
                    Log.Warn($"Server sent map patches for map {i} ({land} land, {statics} statics); ignored: the converter bakes map diffs into the pack");
                }
            }

            return false;
        }

        public bool HasMap(int map) => (uint)map < (uint)_facets.Length && _facets[map] != null;

        public void LoadMap(int map)
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
                // one cache object, reused (a chunk load moves between up to four blocks)
                c ??= new LandZCache();
                c.Map = map;
                c.BlockX = bx;
                c.BlockY = by;
                c.Region = null;
                c.Offset = 0;

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
        /// <summary>
        /// Starts decoding region (rx, ry) of <paramref name="map"/> on the thread pool unless it is cached or already being
        /// decoded. Never blocks. The decoded region joins the cache on the next lookup that needs it (or IsBlockReady).
        /// </summary>
        public void PrefetchRegion(int map, int rx, int ry)
        {
            if (!PrefetchEnabled || !HasMap(map))
            {
                return;
            }

            var facet = _facets[map];
            int side = facet.RegionBlocks;

            if (rx < 0 || ry < 0 || rx * side >= facet.BlocksX || ry * side >= facet.BlocksY)
            {
                return;
            }

            lock (_sync)
            {
                StartPrefetch(facet, rx, ry);
            }
        }

        /// <summary>
        /// Prefetches every region that the square of +/- <paramref name="radius"/> tiles around tile (x, y) touches, the
        /// region under (x, y) first.
        /// </summary>
        public void PrefetchAround(int map, int x, int y, int radius)
        {
            if (!PrefetchEnabled || !HasMap(map) || x < 0 || y < 0)
            {
                return;
            }

            int tiles = _facets[map].RegionBlocks * 8;
            PrefetchRegion(map, x / tiles, y / tiles);

            int rx0 = Math.Max(0, x - radius) / tiles, rx1 = (x + radius) / tiles;
            int ry0 = Math.Max(0, y - radius) / tiles, ry1 = (y + radius) / tiles;

            for (int rx = rx0; rx <= rx1; rx++)
            {
                for (int ry = ry0; ry <= ry1; ry++)
                {
                    PrefetchRegion(map, rx, ry);
                }
            }
        }

        /// <summary>
        /// True when the regions holding block (blockX, blockY) and its eight neighbours (the land stretch reads the z of
        /// neighbouring cells) are decoded, so loading that chunk will not wait for a decode. Regions that are neither
        /// cached nor being decoded are prefetched, finished prefetches are adopted. Never blocks. Always true when
        /// prefetching is off or the block is outside the map.
        /// </summary>
        public bool IsBlockReady(int map, int blockX, int blockY)
        {
            if (!PrefetchEnabled || !HasMap(map))
            {
                return true;
            }

            var facet = _facets[map];

            if ((uint)blockX >= (uint)facet.BlocksX || (uint)blockY >= (uint)facet.BlocksY)
            {
                return true;
            }

            int side = facet.RegionBlocks;
            int rx0 = Math.Max(0, blockX - 1) / side, rx1 = Math.Min(facet.BlocksX - 1, blockX + 1) / side;
            int ry0 = Math.Max(0, blockY - 1) / side, ry1 = Math.Min(facet.BlocksY - 1, blockY + 1) / side;
            bool ready = true;

            lock (_sync)
            {
                for (int rx = rx0; rx <= rx1; rx++)
                {
                    for (int ry = ry0; ry <= ry1; ry++)
                    {
                        long key = RegionKey(facet, rx, ry);

                        if (_regions.ContainsKey(key))
                        {
                            continue;
                        }

                        if (_pending.TryGetValue(key, out var task))
                        {
                            if (task.IsCompleted)
                            {
                                GetRegion(facet, rx, ry); // adopts it without waiting

                                continue;
                            }
                        }
                        else
                        {
                            StartPrefetch(facet, rx, ry);
                        }

                        ready = false;
                    }
                }
            }

            return ready;
        }

        /// <summary>Land z of cell (x, y) as GetLandZ when its region is decoded; otherwise false and a prefetch.</summary>
        public bool TryGetLandZ(int map, int x, int y, out sbyte z)
        {
            z = -125;

            if (x >= 0 && y >= 0 && !IsBlockReady(map, x >> 3, y >> 3))
            {
                return false;
            }

            z = GetLandZ(map, x, y);

            return true;
        }

        /// <summary>Number of regions being decoded on the thread pool and not yet adopted.</summary>
        public int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    return _pending.Count;
                }
            }
        }

        private static long RegionKey(FacetInfo facet, int rx, int ry) => ((long)facet.Index << 40) | ((long)rx << 20) | (uint)ry;

        // under _sync
        private void StartPrefetch(FacetInfo facet, int rx, int ry)
        {
            long key = RegionKey(facet, rx, ry);

            if (_regions.ContainsKey(key) || _pending.ContainsKey(key))
            {
                return;
            }

            PrefetchesStarted++;
            _pending[key] = StartLoad(facet, rx, ry);
        }

        // out of line: a lambda in StartPrefetch made it allocate its closure on entry, on every call (several per frame,
        // nearly all of them returning early above)
        private Task<RegionData> StartLoad(FacetInfo facet, int rx, int ry) => Task.Run(() => LoadRegion(facet, rx, ry));

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

                // decodes still running finish into buffers nobody adopts; the GC takes them
                _pending.Clear();
                _regions.Clear();
                _lru.Clear();
                _zCache = null;
            }
        }

        // under _sync
        private RegionData GetRegion(FacetInfo facet, int rx, int ry)
        {
            long key = RegionKey(facet, rx, ry);

            if (_regions.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);

                return node.Value;
            }

            RegionData region;

            if (_pending.Remove(key, out var task))
            {
                // a prefetch: adopt it, waiting for the rest of its decode if it is still running. The wait is never longer
                // than decoding here would take, and LoadRegion takes no lock this thread holds.
                if (!task.IsCompleted)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
                    PrefetchWaits++;
                    PrefetchWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                }

                region = task.IsCompletedSuccessfully ? task.Result : LoadRegion(facet, rx, ry);
            }
            else
            {
                SyncLoads++;
                region = LoadRegion(facet, rx, ry);
            }

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

        // thread-safe: called by lookups and by prefetch tasks
        private RegionData LoadRegion(FacetInfo facet, int rx, int ry)
        {
            long key = RegionKey(facet, rx, ry);
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
