using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Utility.StringHelper
{
    public class Cp1252Tests
    {
        // The implementation before the span rework (StringHelper at 1bf5fef77), kept as the reference.
        private static class Old
        {
            public static IEnumerable<byte> StringToCp1252Bytes(string s, int length = -1)
            {
                length = length > 0 ? Math.Min(length, s.Length) : s.Length;

                for (int i = 0; i < length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
                {
                    yield return UnicodeToCp1252(char.ConvertToUtf32(s, i));
                }
            }

            public static string Cp1252ToString(ReadOnlySpan<byte> strCp1252)
            {
                var sb = new StringBuilder(strCp1252.Length);

                for (int i = 0; i < strCp1252.Length; ++i)
                {
                    sb.Append(char.ConvertFromUtf32(Cp1252ToUnicode(strCp1252[i])));
                }

                return sb.ToString();
            }

            // StackDataReader.ReadRawString's safe filter.
            public static string Filter(string result)
            {
                var sb = new StringBuilder();

                foreach (char c in result)
                {
                    if (ClassicUO.Utility.StringHelper.IsSafeChar(c))
                    {
                        sb.Append(c);
                    }
                }

                return sb.ToString();
            }

            private static byte UnicodeToCp1252(int codepoint)
            {
                if (codepoint >= 0x80 && codepoint <= 0x9f)
                    return (byte)'?';
                else if (codepoint <= 0xff)
                    return (byte)codepoint;
                else
                {
                    switch (codepoint)
                    {
                        case 0x20AC: return 128;
                        case 0x201A: return 130;
                        case 0x0192: return 131;
                        case 0x201E: return 132;
                        case 0x2026: return 133;
                        case 0x2020: return 134;
                        case 0x2021: return 135;
                        case 0x02C6: return 136;
                        case 0x2030: return 137;
                        case 0x0160: return 138;
                        case 0x2039: return 139;
                        case 0x0152: return 140;
                        case 0x017D: return 142;
                        case 0x2018: return 145;
                        case 0x2019: return 146;
                        case 0x201C: return 147;
                        case 0x201D: return 148;
                        case 0x2022: return 149;
                        case 0x2013: return 150;
                        case 0x2014: return 151;
                        case 0x02DC: return 152;
                        case 0x2122: return 153;
                        case 0x0161: return 154;
                        case 0x203A: return 155;
                        case 0x0153: return 156;
                        case 0x017E: return 158;
                        case 0x0178: return 159;
                        default: return (byte)'?';
                    }
                }
            }

            private static int Cp1252ToUnicode(byte codepoint)
            {
                switch (codepoint)
                {
                    case 128: return 0x20AC;
                    case 130: return 0x201A;
                    case 131: return 0x0192;
                    case 132: return 0x201E;
                    case 133: return 0x2026;
                    case 134: return 0x2020;
                    case 135: return 0x2021;
                    case 136: return 0x02C6;
                    case 137: return 0x2030;
                    case 138: return 0x0160;
                    case 139: return 0x2039;
                    case 140: return 0x0152;
                    case 142: return 0x017D;
                    case 145: return 0x2018;
                    case 146: return 0x2019;
                    case 147: return 0x201C;
                    case 148: return 0x201D;
                    case 149: return 0x2022;
                    case 150: return 0x2013;
                    case 151: return 0x2014;
                    case 152: return 0x02DC;
                    case 153: return 0x2122;
                    case 154: return 0x0161;
                    case 155: return 0x203A;
                    case 156: return 0x0153;
                    case 158: return 0x017E;
                    case 159: return 0x0178;
                    default: return codepoint;
                }
            }
        }

        private static readonly byte[] _unassigned = { 0x81, 0x8D, 0x8F, 0x90, 0x9D };

        private static byte[] AllBytes(int repeat)
        {
            byte[] b = new byte[256 * repeat];

            for (int i = 0; i < b.Length; i++)
            {
                b[i] = (byte)i;
            }

            return b;
        }

        private static IEnumerable<byte[]> RandomByteStrings(int seed, int count, bool latin1Only)
        {
            var rng = new Random(seed);

            for (int n = 0; n < count; n++)
            {
                byte[] b = new byte[rng.Next(0, 600)];

                for (int i = 0; i < b.Length; i++)
                {
                    int v = rng.Next(0, 256);
                    b[i] = latin1Only && v >= 0x80 && v <= 0x9F ? (byte)'a' : (byte)v;
                }

                yield return b;
            }
        }

        // Strings for the write tests: ASCII, Latin1, U+0080-U+009F, the typographic chars, other BMP chars and surrogate
        // pairs (one at the end, so a fixed length can cut it), no lone surrogates (the old iterator threw on those).
        private static readonly string[] _writeCorpus =
        {
            "",
            "a",
            "Hello Britannia",
            "café naïve ÿ ¿",
            "\u0080\u0085\u009F mid \u0090",
            "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ",
            "中文 АБ Ω",
            "emoji 😀 pair",
            "😀😁",
            "tail pair 😀",
            new string('x', 255) + "’" + new string('y', 300),
            "mixed € 5 é \u0081 𐀀 ™ end",
        };

        [Fact]
        public void Read_All_256_Bytes_Match_Old()
        {
            for (int i = 0; i < 256; i++)
            {
                byte[] one = { (byte)i };
                ClassicUO.Utility.StringHelper.Cp1252ToString(one).Should().Be(Old.Cp1252ToString(one), $"byte 0x{i:X2}");
            }

            foreach (int repeat in new[] { 1, 3 })
            {
                byte[] all = AllBytes(repeat);
                ClassicUO.Utility.StringHelper.Cp1252ToString(all).Should().Be(Old.Cp1252ToString(all));
            }
        }

        [Fact]
        public void Read_Random_Strings_Match_Old()
        {
            foreach (byte[] b in RandomByteStrings(1, 400, false).Concat(RandomByteStrings(2, 100, true)))
            {
                ClassicUO.Utility.StringHelper.Cp1252ToString(b).Should().Be(Old.Cp1252ToString(b));
                ClassicUO.Utility.StringHelper.Cp1252ToString(b, false).Should().Be(Old.Cp1252ToString(b));
            }
        }

        [Fact]
        public void Read_Safe_Matches_Old_Filter()
        {
            foreach (byte[] b in RandomByteStrings(3, 400, false).Concat(RandomByteStrings(4, 100, true)).Append(AllBytes(1)).Append(AllBytes(2)))
            {
                ClassicUO.Utility.StringHelper.Cp1252ToString(b, true).Should().Be(Old.Filter(Old.Cp1252ToString(b)));
            }
        }

        [Fact]
        public void RemoveUnsafeChars_Matches_Old_Filter()
        {
            var rng = new Random(5);
            char[] specials = { '\0', '\x01', '\x1F', '\x20', '\x7F', '\x80', '�', '￾', '￿', '\uD83D', '\uDE00' };

            for (int n = 0; n < 400; n++)
            {
                char[] c = new char[rng.Next(0, 600)];

                for (int i = 0; i < c.Length; i++)
                {
                    c[i] = rng.Next(4) == 0 ? specials[rng.Next(specials.Length)] : (char)rng.Next(0x20, 0x3000);
                }

                string s = new string(c);
                ClassicUO.Utility.StringHelper.RemoveUnsafeChars(s).Should().Be(Old.Filter(s));
            }

            string clean = "nothing to drop € �";
            ClassicUO.Utility.StringHelper.RemoveUnsafeChars(clean).Should().BeSameAs(clean);
        }

        [Fact]
        public void Round_Trip_Bytes()
        {
            byte[] all = AllBytes(2).Where(b => Array.IndexOf(_unassigned, b) < 0).ToArray();
            string s = ClassicUO.Utility.StringHelper.Cp1252ToString(all);
            byte[] back = new byte[s.Length];

            int n = ClassicUO.Utility.StringHelper.GetBytesCp1252(s, back);

            n.Should().Be(all.Length);
            back.Should().Equal(all);

            // The unassigned bytes decode to U+0081 etc., which a write sends as '?' (the old rule).
            string u = ClassicUO.Utility.StringHelper.Cp1252ToString(_unassigned);
            byte[] ub = new byte[u.Length];
            ClassicUO.Utility.StringHelper.GetBytesCp1252(u, ub);
            ub.Should().OnlyContain(b => b == (byte)'?');
        }

        [Fact]
        public void Round_Trip_Strings()
        {
            foreach (string s in _writeCorpus)
            {
                byte[] b = new byte[s.Length];
                int n = ClassicUO.Utility.StringHelper.GetBytesCp1252(s, b);
                string back = ClassicUO.Utility.StringHelper.Cp1252ToString(b.AsSpan(0, n));
                byte[] again = new byte[back.Length];
                int m = ClassicUO.Utility.StringHelper.GetBytesCp1252(back, again);

                again.AsSpan(0, m).ToArray().Should().Equal(b.AsSpan(0, n).ToArray(), s);
            }
        }

        [Fact]
        public void Writes_Match_Old_Iterator()
        {
            foreach (string s in _writeCorpus)
            {
                byte[] expected = Old.StringToCp1252Bytes(s).ToArray();

                byte[] span = new byte[s.Length];
                int n = ClassicUO.Utility.StringHelper.GetBytesCp1252(s, span);
                span.AsSpan(0, n).ToArray().Should().Equal(expected, s);

                var viaEnum = new List<byte>();

                foreach (byte b in ClassicUO.Utility.StringHelper.StringToCp1252Bytes(s))
                {
                    viaEnum.Add(b);
                }

                viaEnum.Should().Equal(expected, s);

                for (int length = -1; length <= s.Length + 2; length++)
                {
                    byte[] oldLen = Old.StringToCp1252Bytes(s, length).ToArray();
                    var newLen = new List<byte>();

                    foreach (byte b in ClassicUO.Utility.StringHelper.StringToCp1252Bytes(s, length))
                    {
                        newLen.Add(b);
                    }

                    newLen.Should().Equal(oldLen, $"{s} length {length}");

                    if (length > 0)
                    {
                        // StackDataWriter.WriteASCII(str, length): the truncated bytes, zero padded to length.
                        byte[] expectedField = new byte[length];
                        oldLen.CopyTo(expectedField, 0);

                        byte[] field = Enumerable.Repeat((byte)0xCC, length).ToArray();
                        ClassicUO.Utility.StringHelper.GetBytesCp1252Fixed(s, field);
                        field.Should().Equal(expectedField, $"{s} fixed {length}");
                    }
                }
            }
        }

        [Fact]
        public void Lone_Surrogates_Write_Question_Mark()
        {
            // The old iterator threw (char.ConvertToUtf32); the span encoder and the enumerator write '?'.
            string s = "a\uD800b\uDC00c\uD83D";
            byte[] b = new byte[s.Length];
            int n = ClassicUO.Utility.StringHelper.GetBytesCp1252(s, b);

            b.AsSpan(0, n).ToArray().Should().Equal((byte)'a', (byte)'?', (byte)'b', (byte)'?', (byte)'c', (byte)'?');

            var viaEnum = new List<byte>();

            foreach (byte x in ClassicUO.Utility.StringHelper.StringToCp1252Bytes(s))
            {
                viaEnum.Add(x);
            }

            viaEnum.Should().Equal(b.AsSpan(0, n).ToArray());
        }

        private static long StringBytes(int length)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new string('x', length));

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        [Theory]
        [InlineData(8)]
        [InlineData(30)]
        [InlineData(200)]
        [InlineData(400)]
        public void Read_Allocates_Only_The_Result(int length)
        {
            byte[] plain = Enumerable.Range(0, length).Select(i => (byte)('a' + i % 26)).ToArray();
            byte[] c1 = plain.ToArray();
            c1[length / 2] = 0x92;
            byte[] c0 = plain.ToArray();
            c0[length / 2] = 0x07;

            long expected = StringBytes(length);
            long expectedDropped = StringBytes(length - 1);

            Measure(() => ClassicUO.Utility.StringHelper.Cp1252ToString(plain), expected).Should().Be(expected, "plain");
            Measure(() => ClassicUO.Utility.StringHelper.Cp1252ToString(c1), expected).Should().Be(expected, "C1 remap");
            Measure(() => ClassicUO.Utility.StringHelper.Cp1252ToString(plain, true), expected).Should().Be(expected, "safe, clean");
            Measure(() => ClassicUO.Utility.StringHelper.Cp1252ToString(c1, true), expected).Should().Be(expected, "safe, C1");
            Measure(() => ClassicUO.Utility.StringHelper.Cp1252ToString(c0, true), expectedDropped).Should().Be(expectedDropped, "safe, C0 dropped");
        }

        private static long Measure(Func<string> f, long target) => SettledAllocation(() => GC.KeepAlive(f()), target);

        // Tier-0 code of the vectorized span helpers allocates a little per call until the method tiers up (a JIT artifact:
        // 0 with DOTNET_TieredCompilation=0 and in NativeAOT), so allocations are measured warm, retried until tier-up.
        private static long SettledAllocation(Action op, long target = 0)
        {
            long best = long.MaxValue;

            for (int attempt = 0; attempt < 40 && best > target; attempt++)
            {
                for (int i = 0; i < 64; i++)
                {
                    op();
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                op();
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);

                if (best > target)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            return best;
        }

        [Fact]
        public void Writes_Allocate_Nothing()
        {
            byte[] dest = new byte[1024];
            byte[] field = new byte[30];
            long sum = 0;

            Action span = () =>
            {
                foreach (string s in _writeCorpus)
                {
                    sum += ClassicUO.Utility.StringHelper.GetBytesCp1252(s, dest);
                }
            };

            Action fixedField = () =>
            {
                foreach (string s in _writeCorpus)
                {
                    ClassicUO.Utility.StringHelper.GetBytesCp1252Fixed(s, field);
                }
            };

            Action enumerate = () =>
            {
                foreach (string s in _writeCorpus)
                {
                    foreach (byte b in ClassicUO.Utility.StringHelper.StringToCp1252Bytes(s))
                    {
                        sum += b;
                    }
                }
            };

            SettledAllocation(span).Should().Be(0, "GetBytesCp1252");
            SettledAllocation(fixedField).Should().Be(0, "GetBytesCp1252Fixed");
            SettledAllocation(enumerate).Should().Be(0, "StringToCp1252Bytes");
            sum.Should().BeGreaterThan(0);
        }
    }
}
