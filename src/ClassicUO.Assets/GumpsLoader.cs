// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Uo.Pack;
using Uo.Buffers;

namespace ClassicUO.Assets
{
    public sealed class GumpsLoader : UOFileLoader
    {
        public const int MAX_GUMP_DATA_INDEX_COUNT = 0x10000;

        public GumpsLoader(UOFileManager fileManager) : base(fileManager) { }

        /// <summary>The gumps were converted from gumpartLegacyMUL.uop (the status gump's layout depends on it).</summary>
        public bool UseUOPGumps = false;

        /// <summary>
        /// The pack's gump section (gumps/gumps.bin, pack-format.md "Gumps"). gump.def and loose Gumps/*.gump files were
        /// applied by the converter, and decodes are thread-safe (<see cref="GetGump(uint, Span{uint})"/>).
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

        /// <summary>The pack's gump store; tests and tools only.</summary>
        public RecordStore PackStore => _pack;

        /// <summary>True once loaded: gumps come only from the pack.</summary>
        public bool FromPack => _pack != null;

        /// <summary>Gump ids the index covers: a larger id has no gump.</summary>
        public int Count => _pack?.SlotCount ?? 0;

        /// <summary>Worker threads may decode (<see cref="GetGump(uint, Span{uint})"/>): pack decodes are thread-safe.</summary>
        public bool CanDecodeOnWorker => _pack != null;

        public override void Load()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            LoadStore(FileManager.RequirePackFile(StoreConst.GumpsFile));
            Log.Trace($"Gumps: pack ({_pack.Records.Length} images for {_pack.SlotCount} ids{(UseUOPGumps ? ", from UOP" : "")}) loaded in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms, {(GC.GetAllocatedBytesForCurrentThread() - a0) / 1024} KB allocated");
        }

        /// <summary>Reads gumps from this store file (a gumps.bin): <see cref="Load"/> passes the pack's, tests and tools their own.</summary>
        public void LoadStore(string path)
        {
            _pack?.Dispose();
            _pack = RecordStore.Open(path, StoreConst.MagicGumps);
            UseUOPGumps = (_pack.Header.Flags & StoreConst.GumpFlagUop) != 0;
            ValidateRecords();
        }

        /// <summary>
        /// Width and height of gump <paramref name="index"/> without decoding it (the pack's size table). False = no gump.
        /// </summary>
        public bool TryGetSize(uint index, out int width, out int height)
        {
            if (_pack != null && TryGetRecord(index, out _, out var record, out _))
            {
                width = record.Meta0;
                height = record.Meta1;

                return true;
            }

            width = height = 0;

            return false;
        }

        /// <summary>Texels <see cref="GetGump(uint, Span{uint})"/> needs in its buffer (0 = no gump).</summary>
        public int PixelCount(uint index)
            => _pack != null && TryGetRecord(index, out _, out var record, out _) ? record.Meta0 * record.Meta1 : 0;

        /// <summary>Largest gump side the client accepts (the gump atlas is 4096²).</summary>
        public const int MaxGumpSide = 4096;

        // per record: 0 = usable, 1 = invalid (failed the load-time checks or a decode); set once, never cleared
        private int[] _badRecords;

        /// <summary>Records found invalid at load or at decode; their ids behave as absent (Exists, sizes, GetGump).</summary>
        public int BadRecords => _badRecords == null ? 0 : Array.FindAll(_badRecords, b => b != 0).Length;

        private bool TryGetRecord(uint index, out StoreSlot slot, out StoreRecord record, out int recordIndex)
        {
            if (_pack.TryGet((int)index, out slot, out record))
            {
                recordIndex = (int)slot.Record - 1;

                return Volatile.Read(ref _badRecords[recordIndex]) == 0;
            }

            recordIndex = -1;

            return false;
        }

        /// <summary>
        /// A pack is shipped by shards, so its records are untrusted: sizes within the atlas, a known layout and a raw
        /// size that layout allows (texels: exactly w·h·2; run-length: whole runs, at least the row table and at most a
        /// run per texel), and a compressed size its codec could produce. Anything else is marked invalid and its ids are absent.
        /// </summary>
        private void ValidateRecords()
        {
            var records = _pack.Records;
            _badRecords = new int[records.Length];
            int bad = 0;

            for (int i = 0; i < records.Length; i++)
            {
                string reason = CheckRecord(in records[i]);

                if (reason != null)
                {
                    _badRecords[i] = 1;

                    if (bad++ < 8)
                    {
                        Log.Warn($"gump pack: record {i} is invalid ({reason}); its gumps are treated as absent");
                    }
                }
            }

            if (bad > 8)
            {
                Log.Warn($"gump pack: {bad} invalid records in total");
            }
        }

        /// <summary>Null when a gump record passes the load-time checks, else why not.</summary>
        public static string CheckRecord(in StoreRecord r)
        {
            int w = r.Meta0, h = r.Meta1;

            if (w <= 0 || h <= 0 || w > MaxGumpSide || h > MaxGumpSide)
            {
                return $"size {w}x{h}";
            }

            long texels = (long)w * h;

            switch (r.Meta2)
            {
                case GumpCodec.LayoutTexels:
                    if (r.RawBytes != texels * 2)
                    {
                        return $"texel payload of {r.RawBytes} bytes for {w}x{h}";
                    }

                    break;

                case GumpCodec.LayoutRle:
                    if (r.RawBytes % 4 != 0 || r.RawBytes < 4L * h || r.RawBytes > 4L * h + 4L * texels)
                    {
                        return $"run-length payload of {r.RawBytes} bytes for {w}x{h}";
                    }

                    break;

                default:
                    return $"layout {r.Meta2}";
            }

            if (r.StoredBytes > PackCodec.MaxStoredBytes(r.Codec, r.RawBytes))
            {
                return $"{r.StoredBytes} compressed bytes for {r.RawBytes}";
            }

            return null;
        }

        // a decode failed (corrupt payload, I/O error): the record is invalid from now on, logged once; any thread
        private void MarkBad(int recordIndex, uint index, string reason)
        {
            if (Interlocked.Exchange(ref _badRecords[recordIndex], 1) == 0)
            {
                Log.Warn($"gump {index}: record {recordIndex} in the pack can't be decoded ({reason}); treated as absent");
            }
        }

        /// <summary>
        /// Decodes gump <paramref name="index"/> into <paramref name="buffer"/> (at least <see cref="PixelCount"/> texels;
        /// every texel of the image is written, so a dirty pooled buffer is fine) and returns it as a slice. Thread-safe.
        /// </summary>
        public GumpInfo GetGump(uint index, Span<uint> buffer)
        {
            if (_pack == null || !TryGetRecord(index, out var slot, out var record, out int recordIndex))
            {
                return default;
            }

            // validated at load: 1..MaxGumpSide each way, so w·h fits an int
            int w = record.Meta0, h = record.Meta1;

            if (buffer.Length < w * h)
            {
                return default;
            }

            try
            {
                return DecodeRecord(index, slot, record, recordIndex, w, h, buffer);
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
            {
                MarkBad(recordIndex, index, e.Message);

                return default;
            }
        }

        private GumpInfo DecodeRecord(uint index, StoreSlot slot, StoreRecord record, int recordIndex, int w, int h, Span<uint> buffer)
        {

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

            var raw = BufferPool.Rent<byte>((int)record.RawBytes);

            try
            {
                _pack.Read(record, raw);

                if (!GumpCodec.Decode(record.Meta2, raw.AsSpan(0, (int)record.RawBytes), w, h, pixels, _lut, _expand, hued ? shades : default))
                {
                    MarkBad(recordIndex, index, "malformed run-length rows");

                    return default;
                }

                return new GumpInfo { Pixels = pixels, Width = w, Height = h };
            }
            finally
            {
                BufferPool.Return(raw);
            }
        }

        public override void Dispose()
        {
            _pack?.Dispose();
            _pack = null;
            base.Dispose();
        }

        /// <summary>Gump <paramref name="index"/> in a new buffer (default when there is none).</summary>
        public GumpInfo GetGump(uint index)
        {
            int count = PixelCount(index);

            return count == 0 ? default : GetGump(index, new uint[count]);
        }
    }

    public ref struct GumpInfo
    {
        public Span<uint> Pixels;
        public int Width;
        public int Height;
    }
}
