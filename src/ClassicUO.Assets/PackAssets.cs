// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Uo.Pack;

namespace ClassicUO.Assets
{
    /// <summary>
    /// The converted asset pack (docs/modern-client/pack-format.md). It is the only runtime source for art, texmaps,
    /// maps, tiledata, hues, radarcol, animations, animdata, multis, lights and tileart appearances.
    /// </summary>
    public sealed class PackAssets : IDisposable
    {
        public const string DefaultFolderName = "pack";

        private PackAssets(string root, Manifest manifest)
        {
            Root = root;
            Manifest = manifest;
            Art = new PackPages(Path.Combine(root, "art"), 48 << 20);
            Tex = new PackPages(Path.Combine(root, "tex"), 8 << 20);
            Anim = new PackPages(Path.Combine(root, "anim"), 32 << 20);
        }

        public string Root { get; }
        public Manifest Manifest { get; }
        public PackPages Art { get; }
        public PackPages Tex { get; }
        public PackPages Anim { get; }

        public string PathOf(string relative) => Path.Combine(Root, relative);

        /// <summary>
        /// Resolves the pack folder: the configured path if set, else a "pack" folder next to the client, else one in
        /// the UO directory.
        /// </summary>
        public static string Resolve(string configured, string clientDir, string uoPath)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            foreach (var dir in new[] { clientDir, uoPath })
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                var candidate = Path.Combine(dir, DefaultFolderName);

                if (File.Exists(Path.Combine(candidate, "manifest.json")))
                {
                    return candidate;
                }
            }

            return Path.Combine(clientDir ?? "", DefaultFolderName);
        }

        public static PackAssets Open(string root)
        {
            var manifestPath = Path.Combine(root, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"No asset pack at '{root}' (manifest.json missing). Convert the client with Uo.Converter and pass -pack <dir>.", manifestPath);
            }

            var manifest = ManifestFileIo.Read(manifestPath);

            if (manifest.Version > PackConst.Version)
            {
                throw new InvalidDataException($"Pack format {manifest.Version} is newer than this client supports ({PackConst.Version}).");
            }

            foreach (var required in new[]
            {
                TableConst.TileDataFile, "hues_raw.bin", "radarcol.bin", "multis.bin", "lights.bin",
                "tileart.bin", "equipconv.bin", "art/sprites.bin", "tex/texmaps.bin", "anim/anim.bin",
                "anim/palettes.bin", "anim/animdata.bin"
            })
            {
                if (!File.Exists(Path.Combine(root, required)))
                {
                    throw new FileNotFoundException($"Incomplete asset pack: '{required}' is missing (format 1.1 required: convert the client again with Uo.Converter).", Path.Combine(root, required));
                }
            }

            Log.Trace($"Asset pack: {root} (format {manifest.Version}, from {manifest.SourceClientVersion})");

            return new PackAssets(root, manifest);
        }

        public void Dispose()
        {
            Art.Clear();
            Tex.Clear();
            Anim.Clear();
        }
    }

    /// <summary>
    /// Mip-0 texel access to a folder of atlas pages. Kind 2 pages are decoded one 1 MiB chunk at a time, so a sprite
    /// costs only the chunks its rows touch; decoded chunks stay in an LRU bounded by <c>cacheBytes</c>.
    /// Thread-safe: a chunk decodes outside the lock (several threads can decode at once) into a buffer reused from an
    /// evicted chunk, and every copy out of a cached chunk happens under the lock, so an evicted buffer is never in use.
    /// </summary>
    public sealed class PackPages
    {
        private static readonly int FileHeaderSize = StructIo.SizeOf<FileHeader>();
        private static readonly int PageHeaderSize = StructIo.SizeOf<PageHeader>();

        // evicted chunk buffers kept for the next decodes (one size class: the pack's chunk size)
        private const int MaxFreeBuffers = 4;

        private readonly string _dir;
        private readonly long _cacheBytes;
        private readonly Dictionary<int, PageInfo> _pages = new Dictionary<int, PageInfo>();
        private readonly Dictionary<long, LinkedListNode<Chunk>> _chunks = new Dictionary<long, LinkedListNode<Chunk>>();
        private readonly LinkedList<Chunk> _lru = new LinkedList<Chunk>();
        private readonly Stack<byte[]> _free = new Stack<byte[]>();
        private readonly object _sync = new object();
        private long _cachedBytes;
        private int _decodedChunks;

        public PackPages(string dir, long cacheBytes)
        {
            _dir = dir;
            _cacheBytes = cacheBytes;
        }

        public long CachedBytes => _cachedBytes;
        public int DecodedChunks => _decodedChunks;

        public PageHeader GetHeader(int page) => GetPage(page).Header;

        /// <summary>Copies a <paramref name="w"/> x <paramref name="h"/> rect of mip 0 into <paramref name="dst"/>, rows packed.</summary>
        public void ReadRect(int page, int x, int y, int w, int h, Span<byte> dst)
        {
            var info = GetPage(page);
            int bpp = BytesPerTexel(info.Header.Format);
            int side = info.Header.Width;
            int rowBytes = w * bpp;

            for (int row = 0; row < h; row++)
            {
                long logical = PageHeaderSize + ((long)(y + row) * side + x) * bpp;
                ReadLogical(info, page, logical, dst.Slice(row * rowBytes, rowBytes), false);
            }
        }

        /// <summary>True when every chunk under the rect is decoded and cached, so a <see cref="ReadRect"/> would not decode.</summary>
        public bool IsResident(int page, int x, int y, int w, int h)
        {
            lock (_sync)
            {
                if (!_pages.TryGetValue(page, out var info))
                {
                    return false;
                }

                ChunkRange(info, x, y, w, h, out long first, out long last);

                for (long c = first; c <= last; c++)
                {
                    if (!_chunks.ContainsKey(((long)page << 32) | (uint)c))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Decodes the chunks under a rect into the cache without copying anything out (prefetch).</summary>
        public void Touch(int page, int x, int y, int w, int h)
        {
            var info = GetPage(page);
            ChunkRange(info, x, y, w, h, out long first, out long last);

            for (long c = first; c <= last; c++)
            {
                ReadLogical(info, page, c * info.ChunkBytes, Span<byte>.Empty, true);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _chunks.Clear();
                _lru.Clear();
                _pages.Clear();
                _free.Clear();
                _cachedBytes = 0;
            }
        }

        public static int BytesPerTexel(PixelFormat format) => format switch
        {
            PixelFormat.B5G5R5A1 => 2,
            PixelFormat.Indexed8 => 1,
            PixelFormat.Rgba8 => 4,
            _ => throw new NotSupportedException($"pack pixel format {format}")
        };

        private static void ChunkRange(PageInfo info, int x, int y, int w, int h, out long first, out long last)
        {
            int bpp = BytesPerTexel(info.Header.Format);
            long side = info.Header.Width;
            first = (PageHeaderSize + (y * side + x) * bpp) / info.ChunkBytes;
            last = (PageHeaderSize + ((y + Math.Max(h, 1) - 1) * side + x + Math.Max(w, 1)) * bpp - 1) / info.ChunkBytes;
        }

        private void ReadLogical(PageInfo info, int page, long offset, Span<byte> dst, bool touchOnly)
        {
            do
            {
                int index = (int)(offset / info.ChunkBytes);
                int within = (int)(offset - (long)index * info.ChunkBytes);
                long key = ((long)page << 32) | (uint)index;
                int n;

                lock (_sync)
                {
                    n = TryCopyLocked(key, within, dst, touchOnly);
                }

                if (n < 0)
                {
                    // miss: decode outside the lock, then publish (another thread may have published it meanwhile)
                    var buffer = RentBuffer(info.ChunkBytes);
                    int length = DecodeChunk(info, page, index, buffer);
                    Interlocked.Increment(ref _decodedChunks);

                    lock (_sync)
                    {
                        if (_chunks.ContainsKey(key))
                        {
                            ReturnBufferLocked(buffer);
                        }
                        else
                        {
                            InsertLocked(key, buffer, length);
                        }

                        n = TryCopyLocked(key, within, dst, touchOnly);
                    }
                }

                if (touchOnly)
                {
                    return;
                }

                if (n <= 0)
                {
                    throw new InvalidDataException($"page {page}: read past the end of the page");
                }

                dst = dst.Slice(n);
                offset += n;
            }
            while (!dst.IsEmpty);
        }

        // -1 = not cached, else the bytes copied (0 when touchOnly)
        private int TryCopyLocked(long key, int within, Span<byte> dst, bool touchOnly)
        {
            if (!_chunks.TryGetValue(key, out var node))
            {
                return -1;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);

            if (touchOnly)
            {
                return 0;
            }

            var chunk = node.Value;
            int n = Math.Min(dst.Length, chunk.Length - within);

            if (n > 0)
            {
                chunk.Data.AsSpan(within, n).CopyTo(dst);
            }

            return n;
        }

        private void InsertLocked(long key, byte[] buffer, int length)
        {
            var node = _lru.AddFirst(new Chunk { Key = key, Data = buffer, Length = length });
            _chunks[key] = node;
            _cachedBytes += buffer.Length;

            while (_cachedBytes > _cacheBytes && _lru.Count > 1)
            {
                var last = _lru.Last;
                _lru.RemoveLast();
                _chunks.Remove(last.Value.Key);
                _cachedBytes -= last.Value.Data.Length;
                ReturnBufferLocked(last.Value.Data);
            }
        }

        private byte[] RentBuffer(int size)
        {
            lock (_sync)
            {
                if (_free.Count > 0 && _free.Peek().Length == size)
                {
                    return _free.Pop();
                }
            }

            return new byte[size];
        }

        private void ReturnBufferLocked(byte[] buffer)
        {
            if (_free.Count < MaxFreeBuffers && (_free.Count == 0 || _free.Peek().Length == buffer.Length))
            {
                _free.Push(buffer);
            }
        }

        // decodes chunk `index` into `buffer` (capacity info.ChunkBytes) and returns its length
        private int DecodeChunk(PageInfo info, int page, int index, byte[] buffer)
        {
            using var fh = File.OpenHandle(PackConst.PagePath(_dir, page));
            long start = (long)index * info.ChunkBytes;
            int length = (int)Math.Min(info.ChunkBytes, info.LogicalBytes - start);
            var dst = buffer.AsSpan(0, length);

            switch (info.Kind)
            {
                case FileKind.Raw:
                    RandomAccess.Read(fh, dst, FileHeaderSize + start);

                    break;

                case FileKind.ChunkedBrotli:
                {
                    int size = (int)info.ChunkSizes[index];
                    var compressed = ArrayPool<byte>.Shared.Rent(size);

                    try
                    {
                        RandomAccess.Read(fh, compressed.AsSpan(0, size), info.ChunkOffsets[index]);

                        if (!System.IO.Compression.BrotliDecoder.TryDecompress(compressed.AsSpan(0, size), dst, out int written) || written != length)
                        {
                            throw new InvalidDataException($"page {page}: corrupt chunk {index}");
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(compressed);
                    }

                    break;
                }

                case FileKind.Brotli:
                {
                    using var fs = new FileStream(fh, FileAccess.Read);
                    fs.Position = FileHeaderSize;
                    using var br = new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionMode.Decompress);
                    br.ReadExactly(dst);

                    break;
                }
            }

            return length;
        }

        private PageInfo GetPage(int page)
        {
            lock (_sync)
            {
                if (_pages.TryGetValue(page, out var cached))
                {
                    return cached;
                }
            }

            var path = PackConst.PagePath(_dir, page);
            PageInfo info;

            using (var fh = File.OpenHandle(path))
            {
                FileHeader fileHeader = default;
                RandomAccess.Read(fh, MemoryMarshal.AsBytes(new Span<FileHeader>(ref fileHeader)), 0);

                if (fileHeader.Magic != PackConst.MagicPage || !PackConst.VersionSupported(fileHeader.Version))
                {
                    throw new InvalidDataException($"'{path}' is not a supported pack page");
                }

                info = new PageInfo { Kind = fileHeader.Kind };

                switch (fileHeader.Kind)
                {
                    case FileKind.Raw:
                        info.LogicalBytes = RandomAccess.GetLength(fh) - FileHeaderSize;
                        info.ChunkBytes = 1 << 20;

                        break;

                    case FileKind.ChunkedBrotli:
                    {
                        ChunkedHeader ch = default;
                        RandomAccess.Read(fh, MemoryMarshal.AsBytes(new Span<ChunkedHeader>(ref ch)), FileHeaderSize);
                        info.LogicalBytes = (long)ch.LogicalBytes;
                        info.ChunkBytes = (int)ch.ChunkBytes;
                        info.ChunkSizes = new uint[ch.ChunkCount];
                        long table = FileHeaderSize + ChunkedBrotli.HeaderBytes;
                        RandomAccess.Read(fh, MemoryMarshal.AsBytes(info.ChunkSizes.AsSpan()), table);
                        info.ChunkOffsets = new long[ch.ChunkCount];
                        long offset = table + 4L * ch.ChunkCount;

                        for (int i = 0; i < info.ChunkSizes.Length; i++)
                        {
                            info.ChunkOffsets[i] = offset;
                            offset += info.ChunkSizes[i];
                        }

                        break;
                    }

                    case FileKind.Brotli:
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        fs.Position = FileHeaderSize;
                        using var br = new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionMode.Decompress);
                        var header = StructIo.Read<PageHeader>(br);
                        info.LogicalBytes = PageHeaderSize + header.DataBytes;
                        info.ChunkBytes = (int)info.LogicalBytes;

                        break;
                    }

                    default:
                        throw new InvalidDataException($"'{path}': unknown kind {fileHeader.Kind}");
                }
            }

            // the PageHeader comes from chunk 0, which then stays cached for the sprites in it
            PageHeader pageHeader = default;
            ReadLogical(info, page, 0, MemoryMarshal.AsBytes(new Span<PageHeader>(ref pageHeader)), false);
            info.Header = pageHeader;

            if (info.Header.Width != info.Header.Height || info.Header.Width == 0)
            {
                throw new InvalidDataException($"'{path}': bad page size");
            }

            lock (_sync)
            {
                if (_pages.TryGetValue(page, out var raced))
                {
                    return raced;
                }

                _pages[page] = info;
            }

            return info;
        }

        private sealed class PageInfo
        {
            public ushort Kind;
            public long LogicalBytes;
            public int ChunkBytes;
            public uint[] ChunkSizes;
            public long[] ChunkOffsets;
            public PageHeader Header;
        }

        private sealed class Chunk
        {
            public long Key;
            public byte[] Data;
            public int Length;
        }
    }
}
