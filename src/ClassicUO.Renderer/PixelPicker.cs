using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ClassicUO.Renderer
{
    public sealed class PixelPicker
    {
        const int InitialDataCount = 0x40000; // 256kb

        Dictionary<ulong, int> m_IDs = new Dictionary<ulong, int>();
        // varint data of every entry; a plain array (List<byte> was ~10% slower on reads), doubled when full
        byte[] m_Data = new byte[InitialDataCount];
        int m_DataCount;

        // Keys below _denseCapacity (art and gump ids) live in _dense instead of m_IDs: the world walk pick-tests every drawn
        // static and item each frame, and nearly every test misses on the size check, so a dictionary lookup plus two varint
        // reads per test became an array load. Grown on Set, up to _denseCapacity.
        private readonly int _denseCapacity;
        private DenseEntry[] _dense = Array.Empty<DenseEntry>();

        private struct DenseEntry
        {
            public int Width;
            public int Height;
            public int RunsIndex; // m_Data index of the first run, +1 (0 = no entry)
        }

        public PixelPicker() : this(0)
        {
        }

        /// <param name="denseCapacity">Keys below this are kept in a flat table (ids of a fixed-size sprite set).</param>
        public PixelPicker(int denseCapacity)
        {
            _denseCapacity = Math.Max(0, denseCapacity);
        }

        public bool Get(ulong textureID, int x, int y, int extraRange = 0)
        {
            int index;
            int width;
            int height;

            if (textureID < (ulong)_denseCapacity)
            {
                if (textureID >= (ulong)_dense.Length)
                {
                    return false;
                }

                ref DenseEntry e = ref _dense[(int)textureID];

                if (e.RunsIndex == 0)
                {
                    return false;
                }

                width = e.Width;
                if (x < 0 || x >= width)
                {
                    return false;
                }
                height = e.Height;
                if (y < 0 || y >= height)
                {
                    return false;
                }
                index = e.RunsIndex - 1;
            }
            else
            {
                if (!m_IDs.TryGetValue(textureID, out index))
                {
                    return false;
                }
                width = ReadIntegerFromData(ref index);
                if (x < 0 || x >= width)
                {
                    return false;
                }
                height = ReadIntegerFromData(ref index);
                if (y < 0 || y >= height)
                {
                    return false;
                }
            }
            int current = 0;
            int target = x + y * width;
            bool inTransparentSpan = true;
            // <=, not <: a target on a run boundary is the first texel of the next run, so that run must be read too
            // (with < pixel 0,0 and the first texel of every opaque run never hit; the runs cover width * height, so
            // the walk always returns inside)
            while (current <= target)
            {
                int spanLength = ReadIntegerFromData(ref index);
                current += spanLength;
                if (extraRange == 0)
                {
                    if (target < current)
                    {
                        return !inTransparentSpan;
                    }
                }
                else
                {
                    if (!inTransparentSpan)
                    {
                        int y0 = current / width;
                        int x1 = current % width;
                        int x0 = x1 - spanLength;
                        for (int range = -extraRange; range <= extraRange; range++)
                        {
                            if (y + range == y0 && (x + extraRange >= x0) && (x - extraRange <= x1))
                            {
                                return true;
                            }
                        }
                    }
                }
                inTransparentSpan = !inTransparentSpan;
            }
            return false;
        }

        public void GetDimensions(ulong textureID, out int width, out int height)
        {
            if (textureID < (ulong)_denseCapacity)
            {
                if (textureID < (ulong)_dense.Length && _dense[(int)textureID].RunsIndex != 0)
                {
                    width = _dense[(int)textureID].Width;
                    height = _dense[(int)textureID].Height;
                }
                else
                {
                    width = height = 0;
                }

                return;
            }

            int index;
            if (!m_IDs.TryGetValue(textureID, out index))
            {
                width = height = 0;
                return;
            }
            width = ReadIntegerFromData(ref index);
            height = ReadIntegerFromData(ref index);
        }

        public void Clear()
        {
            m_IDs.Clear();
            Array.Clear(_dense);
            m_DataCount = 0;
        }

        public void Set(ulong textureID, int width, int height, ReadOnlySpan<uint> pixels)
        {
            if (Has(textureID))
            {
                return;
            }

            int begin = m_DataCount;
            WriteIntegerToData(width);
            WriteIntegerToData(height);
            bool countingTransparent = true;
            int count = 0;
            for (int i = 0, len = width * height; i < len; i++)
            {
                bool isTransparent = pixels[i] == 0;
                if (countingTransparent != isTransparent)
                {
                    WriteIntegerToData(count);
                    countingTransparent = !countingTransparent;
                    count = 0;
                }
                count += 1;
            }
            WriteIntegerToData(count);
            AddEntry(textureID, begin);
        }

        /// <summary>
        /// Same result as <see cref="Set"/> for an image whose texels outside the trim rect are all 0 (a pack sprite
        /// decoded untrimmed): only the trim rect is scanned, the rest is known transparent. <paramref name="pixels"/> is
        /// the full <paramref name="width"/> x <paramref name="height"/> image.
        /// </summary>
        public void SetTrimmed(ulong textureID, int width, int height, int trimX, int trimY, int trimW, int trimH, ReadOnlySpan<uint> pixels)
        {
            if (Has(textureID))
            {
                return;
            }

            int begin = m_DataCount;
            WriteIntegerToData(width);
            WriteIntegerToData(height);
            var runs = new RunWriter(this);

            if (trimW <= 0 || trimH <= 0)
            {
                runs.Transparent(width * height);
            }
            else
            {
                runs.Transparent(trimY * width + trimX);

                for (int row = 0; row < trimH; row++)
                {
                    if (row > 0)
                    {
                        runs.Transparent(width - trimW);
                    }

                    runs.Pixels(pixels.Slice((trimY + row) * width + trimX, trimW));
                }

                runs.Transparent(width - trimX - trimW + (height - trimY - trimH) * width);
            }

            runs.Finish();
            AddEntry(textureID, begin);
        }

        /// <summary>Makes <see cref="SetTrimmed"/> scalar (no SIMD), for parity tests and benchmarks.</summary>
        public static bool ForceScalar;

        /// <summary>
        /// The bounding box of the non-zero texels, as the scan over the whole image that Art used to do: (minX, minY,
        /// maxX, maxY), or (width, height, 0, 0) when every texel is 0. Texels outside the trim rect must be 0. A pack
        /// sprite's trim is its visible box, so this usually only confirms that each edge of the trim rect has a visible
        /// texel; otherwise it scans the trim rect.
        /// </summary>
        public static void VisibleBounds(ReadOnlySpan<uint> pixels, int width, int height, int trimX, int trimY, int trimW, int trimH,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = width;
            minY = height;
            maxX = 0;
            maxY = 0;

            if (trimW <= 0 || trimH <= 0)
            {
                return;
            }

            if (AnyVisible(pixels.Slice(trimY * width + trimX, trimW)) &&
                AnyVisible(pixels.Slice((trimY + trimH - 1) * width + trimX, trimW)) &&
                AnyVisibleColumn(pixels, width, trimX, trimY, trimH) &&
                AnyVisibleColumn(pixels, width, trimX + trimW - 1, trimY, trimH))
            {
                minX = trimX;
                minY = trimY;
                maxX = trimX + trimW - 1;
                maxY = trimY + trimH - 1;

                return;
            }

            for (int y = trimY; y < trimY + trimH; y++)
            {
                var row = pixels.Slice(y * width + trimX, trimW);

                for (int x = 0; x < row.Length; x++)
                {
                    if (row[x] != 0)
                    {
                        minX = Math.Min(minX, trimX + x);
                        maxX = Math.Max(maxX, trimX + x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
        }

        // the BCL search is already vectorised (Vector128/256 with a scalar tail)
        private static bool AnyVisible(ReadOnlySpan<uint> row) => row.ContainsAnyExcept(0u);

        private static bool AnyVisibleColumn(ReadOnlySpan<uint> pixels, int width, int x, int y, int h)
        {
            for (int i = 0; i < h; i++)
            {
                if (pixels[(y + i) * width + x] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Run-length state of Set: alternating transparent/opaque run lengths, starting with a transparent run.
        private ref struct RunWriter
        {
            private readonly PixelPicker _picker;
            private bool _countingTransparent;
            private int _count;

            public RunWriter(PixelPicker picker)
            {
                _picker = picker;
                _countingTransparent = true;
                _count = 0;
            }

            public void Transparent(int n)
            {
                if (n <= 0)
                {
                    return;
                }

                if (!_countingTransparent)
                {
                    _picker.WriteIntegerToData(_count);
                    _countingTransparent = true;
                    _count = 0;
                }

                _count += n;
            }

            private void Opaque(int n)
            {
                if (_countingTransparent)
                {
                    _picker.WriteIntegerToData(_count);
                    _countingTransparent = false;
                    _count = 0;
                }

                _count += n;
            }

            public void Pixels(ReadOnlySpan<uint> row)
            {
                int i = 0;

                if (!ForceScalar && Vector256.IsHardwareAccelerated)
                {
                    // 8 texels at a time: all transparent or all opaque blocks extend the current run without a per-texel branch
                    ref uint r = ref MemoryMarshal.GetReference(row);

                    for (; i + 8 <= row.Length; i += 8)
                    {
                        uint zero = Vector256.Equals(Vector256.LoadUnsafe(ref r, (nuint)i), Vector256<uint>.Zero).ExtractMostSignificantBits();

                        if (zero == 0xFF)
                        {
                            Transparent(8);
                        }
                        else if (zero == 0)
                        {
                            Opaque(8);
                        }
                        else
                        {
                            Bits(zero, 8);
                        }
                    }
                }
                else if (!ForceScalar && Vector128.IsHardwareAccelerated)
                {
                    ref uint r = ref MemoryMarshal.GetReference(row);

                    for (; i + 4 <= row.Length; i += 4)
                    {
                        uint zero = Vector128.Equals(Vector128.LoadUnsafe(ref r, (nuint)i), Vector128<uint>.Zero).ExtractMostSignificantBits();

                        if (zero == 0xF)
                        {
                            Transparent(4);
                        }
                        else if (zero == 0)
                        {
                            Opaque(4);
                        }
                        else
                        {
                            Bits(zero, 4);
                        }
                    }
                }

                for (; i < row.Length; i++)
                {
                    if (row[i] == 0)
                    {
                        Transparent(1);
                    }
                    else
                    {
                        Opaque(1);
                    }
                }
            }

            // `zero` bit k set = texel k transparent; walks the mixed block run by run
            private void Bits(uint zero, int n)
            {
                int k = 0;

                while (k < n)
                {
                    uint rest = zero >> k;
                    bool transparent = (rest & 1) != 0;
                    int len = transparent ? BitOperations.TrailingZeroCount(~rest) : BitOperations.TrailingZeroCount(rest);
                    len = Math.Min(len, n - k);

                    if (transparent)
                    {
                        Transparent(len);
                    }
                    else
                    {
                        Opaque(len);
                    }

                    k += len;
                }
            }

            public void Finish()
            {
                _picker.WriteIntegerToData(_count);
            }
        }

        bool Has(ulong textureID)
        {
            if (textureID < (ulong)_denseCapacity)
            {
                return textureID < (ulong)_dense.Length && _dense[(int)textureID].RunsIndex != 0;
            }

            return m_IDs.ContainsKey(textureID);
        }

        // begin: m_Data index of the entry (width, height, then runs), as m_IDs stores it
        private void AddEntry(ulong textureID, int begin)
        {
            if (textureID >= (ulong)_denseCapacity)
            {
                m_IDs[textureID] = begin;

                return;
            }

            int key = (int)textureID;

            if (key >= _dense.Length)
            {
                Array.Resize(ref _dense, Math.Min(_denseCapacity, Math.Max(key + 1, Math.Max(256, _dense.Length * 2))));
            }

            // decoded back, so the table holds exactly what the dictionary path would read
            int runs = begin;
            int w = ReadIntegerFromData(ref runs);
            int h = ReadIntegerFromData(ref runs);
            _dense[key] = new DenseEntry { Width = w, Height = h, RunsIndex = runs + 1 };
        }

        void WriteIntegerToData(int value)
        {
            // a varint of a non-negative int is at most 5 bytes
            if (m_Data.Length - m_DataCount < 5)
            {
                Array.Resize(ref m_Data, m_Data.Length * 2);
            }

            while (value > 0x7f)
            {
                m_Data[m_DataCount++] = (byte)((value & 0x7f) | 0x80);
                value >>= 7;
            }
            m_Data[m_DataCount++] = (byte)value;
        }

        int ReadIntegerFromData(ref int index)
        {
            int value = 0;
            int shift = 0;
            while (true)
            {
                byte data = m_Data[index++];
                value += (data & 0x7f) << shift;
                if ((data & 0x80) == 0x00)
                {
                    return value;
                }
                shift += 7;
            }
        }
    }
}
