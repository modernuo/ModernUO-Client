// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
                "tiledata_ext.bin", "hues.bin", "hues_raw.bin", "radarcol.bin", "multis.bin", "lights.bin",
                "tileart.bin", "equipconv.bin", "art/sprites.bin", "tex/texmaps.bin", "anim/anim.bin",
                "anim/palettes.bin", "anim/animdata.bin"
            })
            {
                if (!File.Exists(Path.Combine(root, required)))
                {
                    throw new FileNotFoundException($"Incomplete asset pack: '{required}' is missing (format 1.0 required).", Path.Combine(root, required));
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
    /// </summary>
    public sealed class PackPages
    {
        private static readonly int FileHeaderSize = StructIo.SizeOf<FileHeader>();
        private static readonly int PageHeaderSize = StructIo.SizeOf<PageHeader>();

        private readonly string _dir;
        private readonly long _cacheBytes;
        private readonly Dictionary<int, PageInfo> _pages = new Dictionary<int, PageInfo>();
        private readonly Dictionary<long, LinkedListNode<Chunk>> _chunks = new Dictionary<long, LinkedListNode<Chunk>>();
        private readonly LinkedList<Chunk> _lru = new LinkedList<Chunk>();
        private readonly object _sync = new object();
        private long _cachedBytes;

        public PackPages(string dir, long cacheBytes)
        {
            _dir = dir;
            _cacheBytes = cacheBytes;
        }

        public long CachedBytes => _cachedBytes;
        public int DecodedChunks { get; private set; }

        public PageHeader GetHeader(int page)
        {
            lock (_sync)
            {
                return GetPage(page).Header;
            }
        }

        /// <summary>Copies a <paramref name="w"/> x <paramref name="h"/> rect of mip 0 into <paramref name="dst"/>, rows packed.</summary>
        public void ReadRect(int page, int x, int y, int w, int h, Span<byte> dst)
        {
            lock (_sync)
            {
                var info = GetPage(page);
                int bpp = BytesPerTexel(info.Header.Format);
                int side = info.Header.Width;
                int rowBytes = w * bpp;

                for (int row = 0; row < h; row++)
                {
                    long logical = PageHeaderSize + ((long)(y + row) * side + x) * bpp;
                    ReadLogical(info, page, logical, dst.Slice(row * rowBytes, rowBytes));
                }
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _chunks.Clear();
                _lru.Clear();
                _pages.Clear();
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

        private void ReadLogical(PageInfo info, int page, long offset, Span<byte> dst)
        {
            while (!dst.IsEmpty)
            {
                int index = (int)(offset / info.ChunkBytes);
                int within = (int)(offset - (long)index * info.ChunkBytes);
                var chunk = GetChunk(info, page, index);
                int n = Math.Min(dst.Length, chunk.Length - within);

                if (n <= 0)
                {
                    throw new InvalidDataException($"page {page}: read past the end of the page");
                }

                chunk.Data.AsSpan(within, n).CopyTo(dst);
                dst = dst.Slice(n);
                offset += n;
            }
        }

        private Chunk GetChunk(PageInfo info, int page, int index)
        {
            long key = ((long)page << 32) | (uint)index;

            if (_chunks.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);

                return node.Value;
            }

            var chunk = new Chunk { Key = key };
            DecodeChunk(info, page, index, chunk);
            DecodedChunks++;
            node = _lru.AddFirst(chunk);
            _chunks[key] = node;
            _cachedBytes += chunk.Data.Length;

            while (_cachedBytes > _cacheBytes && _lru.Count > 1)
            {
                var last = _lru.Last;
                _lru.RemoveLast();
                _chunks.Remove(last.Value.Key);
                _cachedBytes -= last.Value.Data.Length;
            }

            return chunk;
        }

        private void DecodeChunk(PageInfo info, int page, int index, Chunk chunk)
        {
            using var fh = File.OpenHandle(PackConst.PagePath(_dir, page));
            long start = (long)index * info.ChunkBytes;
            int length = (int)Math.Min(info.ChunkBytes, info.LogicalBytes - start);
            chunk.Data = new byte[length];
            chunk.Length = length;

            switch (info.Kind)
            {
                case FileKind.Raw:
                    RandomAccess.Read(fh, chunk.Data, FileHeaderSize + start);

                    break;

                case FileKind.ChunkedBrotli:
                {
                    var compressed = new byte[info.ChunkSizes[index]];
                    RandomAccess.Read(fh, compressed, info.ChunkOffsets[index]);

                    if (!System.IO.Compression.BrotliDecoder.TryDecompress(compressed, chunk.Data, out int written) || written != length)
                    {
                        throw new InvalidDataException($"page {page}: corrupt chunk {index}");
                    }

                    break;
                }

                case FileKind.Brotli:
                {
                    using var fs = new FileStream(fh, FileAccess.Read);
                    fs.Position = FileHeaderSize;
                    using var br = new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionMode.Decompress);
                    br.ReadExactly(chunk.Data);

                    break;
                }
            }
        }

        private PageInfo GetPage(int page)
        {
            if (_pages.TryGetValue(page, out var info))
            {
                return info;
            }

            var path = PackConst.PagePath(_dir, page);
            using var fh = File.OpenHandle(path);
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

            _pages[page] = info;
            var first = GetChunk(info, page, 0);
            info.Header = MemoryMarshal.Read<PageHeader>(first.Data);

            if (info.Header.Width != info.Header.Height || info.Header.Width == 0)
            {
                throw new InvalidDataException($"'{path}': bad page size");
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
