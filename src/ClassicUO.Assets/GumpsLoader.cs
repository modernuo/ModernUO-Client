// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class GumpsLoader : UOFileLoader
    {
        public const int MAX_GUMP_DATA_INDEX_COUNT = 0x10000;

        /// <summary>
        /// Gump art of the shard's own, as loose files, by the number the server asks for.
        ///
        /// gump.def can only fill an empty slot - it skips anything the archive already has - so
        /// it aliases and never replaces. These do both: a file here is used whether or not the
        /// archive has that number, which is what makes correcting an existing gump possible.
        ///
        /// The archive uses 5,571 of the 65,536 numbers and its highest is 61,728, so there is
        /// plenty of room to put ours somewhere nothing will ever collide with.
        /// </summary>
        private readonly Dictionary<int, string> _ours = new Dictionary<int, string>();

        private UOFile _file;

        public GumpsLoader(UOFileManager fileManager) : base(fileManager) { }


        public bool UseUOPGumps = false;
        public UOFile File => _file;

        /// <summary>
        /// The pack's gump section (gumps/gumps.bin, pack-format.md "Gumps"), or null when the pack has none and gumps come
        /// from MUL/UOP. With it, every gump is read from the pack: gump.def and loose Gumps/*.gump files were applied by
        /// the converter, and decodes are thread-safe (<see cref="GetGump(uint, Span{uint})"/>).
        /// </summary>
        private RecordStore _pack;

        /// <summary>ClassicUO's 5-to-8-bit table (HuesHelper.Color16To32), so pack decodes give the MUL decode's RGBA exactly.</summary>
        private static readonly uint[] _expand = BuildExpand();
        private static readonly uint[] _lut = GumpCodec.BuildLut(_expand);

        private static uint[] BuildExpand()
        {
            var t = new uint[32];

            for (int c = 0; c < 32; c++)
            {
                t[c] = HuesHelper.Color16To32((ushort)(c << 10)) & 0xFF;
            }

            return t;
        }

        /// <summary>
        /// Read the pack's gump section when it has one (set before <see cref="Load"/>). CUO_PACK_GUMPS=0 starts it false:
        /// MUL/UOP even with a gump section, for side-by-side measurements and parity checks.
        /// </summary>
        public bool UsePack = Environment.GetEnvironmentVariable("CUO_PACK_GUMPS") != "0";

        /// <summary>The pack's gump store (null on the MUL/UOP path); tests and tools only.</summary>
        public RecordStore PackStore => _pack;

        /// <summary>True when gumps come from the pack.</summary>
        public bool FromPack => _pack != null;

        /// <summary>Gump ids the index covers: a larger id has no gump.</summary>
        public int Count => _pack?.SlotCount ?? _file.Entries.Length;

        /// <summary>Worker threads may decode (<see cref="GetGump(uint, Span{uint})"/>): only the pack path is thread-safe.</summary>
        public bool CanDecodeOnWorker => _pack != null;

        public override void Load()
        {
            string packPath = FileManager.Pack?.PathOf(StoreConst.GumpsFile);

            if (UsePack && packPath != null && System.IO.File.Exists(packPath))
            {
                _pack = RecordStore.Open(packPath, StoreConst.MagicGumps);
                UseUOPGumps = (_pack.Header.Flags & StoreConst.GumpFlagUop) != 0;
                Log.Trace($"Gumps: pack ({_pack.Records.Length} images for {_pack.SlotCount} ids{(UseUOPGumps ? ", from UOP" : "")})");

                return;
            }

            if (FileManager.Pack != null)
            {
                Log.Trace("Gumps: the pack has no gump section; reading gumpart MUL/UOP");
            }

            string path = FileManager.GetUOFilePath("gumpartLegacyMUL.uop");

            if (FileManager.IsUOPInstallation && System.IO.File.Exists(path))
            {
                _file = new UOFileUop(path, "build/gumpartlegacymul/{0:D8}.tga", true);
                UseUOPGumps = true;
            }
            else
            {
                path = FileManager.GetUOFilePath("gumpart.mul");
                string pathidx = FileManager.GetUOFilePath("gumpidx.mul");

                if (!System.IO.File.Exists(path))
                {
                    path = FileManager.GetUOFilePath("Gumpart.mul");
                }

                if (!System.IO.File.Exists(pathidx))
                {
                    pathidx = FileManager.GetUOFilePath("Gumpidx.mul");
                }

                _file = new UOFileMul(path, pathidx);

                UseUOPGumps = false;
            }

            _file.FillEntries();

            LoadOurs();

            string pathdef = FileManager.GetUOFilePath("gump.def");

            if (!System.IO.File.Exists(pathdef))
            {
                return;
            }

            using (DefReader defReader = new DefReader(pathdef, 3))
            {
                while (defReader.Next())
                {
                    int ingump = defReader.ReadInt();

                    if (
                        ingump < 0
                        || ingump >= MAX_GUMP_DATA_INDEX_COUNT
                        || ingump >= _file.Entries.Length
                        || _file.Entries[ingump].Length > 0
                    )
                    {
                        continue;
                    }

                    int[] group = defReader.ReadGroup();

                    if (group == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < group.Length; i++)
                    {
                        int checkIndex = group[i];

                        if (
                            checkIndex < 0
                            || checkIndex >= MAX_GUMP_DATA_INDEX_COUNT
                            || checkIndex >= _file.Entries.Length
                            || _file.Entries[checkIndex].Length <= 0
                        )
                        {
                            continue;
                        }

                        _file.Entries[ingump] = _file.Entries[checkIndex];
                        _file.Entries[ingump].Hue = (ushort)defReader.ReadInt();

                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Find the shard's own gump art once, at load, rather than asking the disk every time
        /// something is drawn.
        ///
        /// A File.Exists per gump would be a disk hit for every frame of every open window.
        /// </summary>
        public void LoadOurs()
        {
            _ours.Clear();

            if (_pack != null)
            {
                return; // the converter folded the loose files into the pack (re-run it with --only gumps after changing them)
            }

            string folder = Path.Combine(FileManager.BasePath, "Gumps");

            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.gump"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int id)
                    && id >= 0 && id < MAX_GUMP_DATA_INDEX_COUNT)
                {
                    _ours[id] = path;
                }
            }

            if (_ours.Count > 0)
            {
                Log.Trace($"{_ours.Count} gump(s) of our own in {folder}");
            }
        }

        /// <summary>
        /// One of ours off the disk.
        ///
        /// The file is the same run-length picture the archive stores, with its size in front:
        /// two 32-bit numbers for width and height, then the rows. That is not a new format -
        /// the archive's own compressed entries carry their size inline in exactly this way and
        /// are read by exactly this code a few lines down. Writing it out is a matter of leaving
        /// the size where the reader below already looks for it.
        /// </summary>
        private GumpInfo ReadOurs(string path)
        {
            byte[] raw;

            try
            {
                raw = System.IO.File.ReadAllBytes(path);
            }
            catch (IOException e)
            {
                Log.Warn($"could not read {path}: {e.Message}");
                return default;
            }

            if (raw.Length < 8)
            {
                Log.Warn($"{Path.GetFileName(path)} is too short to be a gump");
                return default;
            }

            var reader = new StackDataReader(raw);
            var w = reader.ReadUInt32LE();
            var h = reader.ReadUInt32LE();

            // A bad size is worth naming out loud. Believed, it asks for a picture of some
            // billions of pixels and takes the client down with it.
            if (w == 0 || h == 0 || w > 4096 || h > 4096)
            {
                Log.Warn($"{Path.GetFileName(path)} claims to be {w}x{h}");
                return default;
            }

            if (reader.Remaining < h * 4)
            {
                Log.Warn($"{Path.GetFileName(path)} has no room for {h} rows");
                return default;
            }

            return Decode(ref reader, w, h, 0);
        }

        /// <summary>
        /// Width and height of gump <paramref name="index"/> without decoding it (the pack's size table, or the MUL/UOP
        /// index; an entry whose size sits inside its compressed data reports 0 until first decoded). False = no gump.
        /// </summary>
        public bool TryGetSize(uint index, out int width, out int height)
        {
            if (_pack != null)
            {
                if (_pack.TryGet((int)index, out _, out var record))
                {
                    width = record.Meta0;
                    height = record.Meta1;

                    return true;
                }

                width = height = 0;

                return false;
            }

            ref var entry = ref _file.GetValidRefEntry((int)index);
            width = Math.Max(0, entry.Width);
            height = Math.Max(0, entry.Height);

            return entry.CompressionFlag == CompressionType.ZlibBwt || entry.Width > 0 || entry.Height > 0
                || (_ours.Count > 0 && _ours.ContainsKey((int)index));
        }

        /// <summary>Texels <see cref="GetGump(uint, Span{uint})"/> needs in its buffer (pack path; 0 = no gump).</summary>
        public int PixelCount(uint index)
            => _pack != null && _pack.TryGet((int)index, out _, out var record) ? record.Meta0 * record.Meta1 : 0;

        /// <summary>
        /// Pack path: decodes gump <paramref name="index"/> into <paramref name="buffer"/> (at least
        /// <see cref="PixelCount"/> texels; every texel of the image is written, so a dirty pooled buffer is fine) and
        /// returns it as a slice. Thread-safe. On the MUL/UOP path this is <see cref="GetGump(uint)"/> (main thread only;
        /// the buffer is unused).
        /// </summary>
        public GumpInfo GetGump(uint index, Span<uint> buffer)
        {
            if (_pack == null)
            {
                return GetGump(index);
            }

            if (!_pack.TryGet((int)index, out var slot, out var record))
            {
                return default;
            }

            int w = record.Meta0, h = record.Meta1;

            if (w == 0 || h == 0 || buffer.Length < w * h)
            {
                return default;
            }

            // the gump.def hue, as GetColor16 applies it: only a hue the table has
            Span<ushort> shades = stackalloc ushort[32];
            var hues = FileManager.Hues;
            bool hued = slot.Arg != 0 && slot.Arg < hues.HuesCount;

            if (hued)
            {
                int c = slot.Arg - 1;
                ref var block = ref hues.HuesRange[c >> 3].Entries[c & 7];

                for (int k = 0; k < 32; k++)
                {
                    shades[k] = block.ColorTable[k];
                }
            }

            var pixels = buffer.Slice(0, w * h);

            if (record.Meta2 == GumpCodec.LayoutTexels && record.RawBytes == (uint)(w * h * 2))
            {
                // no intermediate buffer: the 16-bit texels decode into the upper half of the pixel buffer and expand
                // in place, front to back (texel i is read before RGBA texel i overwrites anything unread)
                var texels = MemoryMarshal.Cast<uint, ushort>(pixels).Slice(w * h, w * h);
                _pack.Read(record, MemoryMarshal.AsBytes(texels));
                GumpCodec.ExpandTexels(texels, pixels, _lut, hued ? shades : default);

                return new GumpInfo { Pixels = pixels, Width = w, Height = h };
            }

            var raw = System.Buffers.ArrayPool<byte>.Shared.Rent((int)record.RawBytes);

            try
            {
                _pack.Read(record, raw);

                if (!GumpCodec.Decode(record.Meta2, raw.AsSpan(0, (int)record.RawBytes), w, h, pixels, _lut, _expand, hued ? shades : default))
                {
                    Log.Warn($"gump {index}: corrupt record in the pack");

                    return default;
                }

                return new GumpInfo { Pixels = pixels, Width = w, Height = h };
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(raw);
            }
        }

        public override void Dispose()
        {
            _pack?.Dispose();
            _pack = null;
            base.Dispose();
        }

        public GumpInfo GetGump(uint index)
        {
            if (_pack != null)
            {
                int count = PixelCount(index);

                return count == 0 ? default : GetGump(index, new uint[count]);
            }

            // Ours first, so a file can correct a gump the archive already has and not only fill
            // a number it lacks.
            if (_ours.Count > 0 && _ours.TryGetValue((int)index, out string ours))
            {
                GumpInfo mine = ReadOurs(ours);

                if (mine.Width > 0)
                {
                    return mine;
                }

                // A broken file falls through to the archive rather than leaving a hole, so a bad
                // import shows the old art and a warning instead of nothing at all.
            }

            ref var entry = ref _file.GetValidRefEntry((int)index);

            if (entry.CompressionFlag != CompressionType.ZlibBwt && entry.Width <= 0 && entry.Height <= 0)
            {
                return default;
            }

            ushort color = entry.Hue;

            var file = _file;
            if (entry.File != null)
                file = entry.File;

            file.Seek(entry.Offset, SeekOrigin.Begin);

            var buf = new byte[entry.Length];
            file.Read(buf);

            var reader = new StackDataReader(buf);
            var w = (uint)entry.Width;
            var h = (uint)entry.Height;

            if (entry.CompressionFlag >= CompressionType.Zlib)
            {
                var dbuf = new byte[entry.DecompressedLength];
                var result = ClassicUO.Utility.ZLib.Decompress(reader.Buffer.Slice(reader.Position), dbuf);
                if (result != Utility.ZLib.ZLibError.Ok)
                {
                    return default;
                }

                if (entry.CompressionFlag == CompressionType.ZlibBwt)
                {
                    dbuf = ClassicUO.Utility.BwtDecompress.Decompress(dbuf);
                }

                reader = new StackDataReader(dbuf);
                w = reader.ReadUInt32LE();
                h = reader.ReadUInt32LE();

                if (entry.Width <= 0)
                    entry.Width = (int)w;
                if (entry.Height <= 0)
                    entry.Height = (int)h;
            }

            return Decode(ref reader, w, h, color);
        }

        /// <summary>
        /// The rows of a gump, from wherever they came from.
        ///
        /// A row lookup of one 32-bit word per row - an offset from the table's own beginning, in
        /// units of four bytes - and then pairs of (colour, run), the colour being 16-bit with
        /// zero meaning transparent, which is why a mostly-empty gump costs so little.
        /// </summary>
        private GumpInfo Decode(scoped ref StackDataReader reader, uint w, uint h, ushort color)
        {
            Span<uint> pixels = new uint[w * h];
            var len = reader.Remaining;
            var halfLen = len >> 2;

            var start = reader.Position;
            var rowLookup = new int[h];
            reader.Read(MemoryMarshal.AsBytes<int>(rowLookup.AsSpan()));

            for (var y = 0; y < h; ++y)
            {
                reader.Seek(start + (rowLookup[y] << 2));
                var pixelIndex = (int)(y * w);
                var gsize = (y < h - 1) ? rowLookup[y + 1] - rowLookup[y] : halfLen - rowLookup[y];
                for (var i = 0; i < gsize; ++i)
                {
                    var value = reader.ReadUInt16LE();
                    var run = reader.ReadUInt16LE();
                    var rbga = 0u;

                    if (color != 0 && value != 0)
                    {
                        value = FileManager.Hues.GetColor16(value, color);
                    }

                    if (value != 0)
                    {
                        rbga = HuesHelper.Color16To32(value) | 0xFF_00_00_00;
                    }

                    // A run that would spill past the end of the row means the file disagrees
                    // with its own header. Trusting it walks off the end of the picture, and an
                    // IndexOutOfRange thrown from inside the drawing code is a long way from the
                    // file that caused it.
                    if (pixelIndex + run > pixels.Length)
                    {
                        run = (ushort)Math.Max(0, pixels.Length - pixelIndex);

                        if (run == 0)
                        {
                            break;
                        }
                    }

                    pixels.Slice(pixelIndex, run).Fill(rbga);
                    pixelIndex += run;
                }
            }

            return new GumpInfo()
            {
                Pixels = pixels,
                Width = (int)w,
                Height = (int)h
            };
        }
    }

    public ref struct GumpInfo
    {
        public Span<uint> Pixels;
        public int Width;
        public int Height;
    }
}
