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
        readonly List<byte> m_Data = new List<byte>(InitialDataCount); // list<t> access is 10% slower than t[].

        public bool Get(ulong textureID, int x, int y, int extraRange = 0)
        {
            int index;
            if (!m_IDs.TryGetValue(textureID, out index))
            {
                return false;
            }
            int width = ReadIntegerFromData(ref index);
            if (x < 0 || x >= width)
            {
                return false;
            }
            int height = ReadIntegerFromData(ref index);
            if (y < 0 || y >= height)
            {
                return false;
            }
            int current = 0;
            int target = x + y * width;
            bool inTransparentSpan = true;
            while (current < target)
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
            m_Data.Clear();
        }

        public void Set(ulong textureID, int width, int height, ReadOnlySpan<uint> pixels)
        {
            if (Has(textureID))
            {
                return;
            }

            int begin = m_Data.Count;
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
            m_IDs[textureID] = begin;
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

            int begin = m_Data.Count;
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
            m_IDs[textureID] = begin;
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
            return m_IDs.ContainsKey(textureID);
        }

        void WriteIntegerToData(int value)
        {
            while (value > 0x7f)
            {
                m_Data.Add((byte)((value & 0x7f) | 0x80));
                value >>= 7;
            }
            m_Data.Add((byte)value);
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
