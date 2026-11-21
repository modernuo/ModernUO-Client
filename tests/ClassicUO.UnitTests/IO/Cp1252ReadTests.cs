using System;
using System.Collections.Generic;
using System.Text;
using ClassicUO.IO;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.IO
{
    // StackDataReader and SpanReader decode server text with StringHelper.Cp1252ToString(bytes, safe) or, with
    // text_string_cache on, the cached string filtered by StringHelper.RemoveUnsafeChars; UTF-8 / UTF-16 safe reads use
    // RemoveUnsafeChars too. The strings are the ones the old decode + IsSafeChar loop returned, in both cache states.
    [Collection("Cp1252Cache")]
    public class Cp1252ReadTests
    {
        private static string OldFilter(string s)
        {
            var sb = new StringBuilder();

            foreach (char c in s)
            {
                if (StringHelper.IsSafeChar(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string OldCp1252(ReadOnlySpan<byte> bytes, bool safe)
        {
            var sb = new StringBuilder();

            foreach (byte b in bytes)
            {
                sb.Append(Cp1252.ToChar(b));
            }

            return safe ? OldFilter(sb.ToString()) : sb.ToString();
        }

        // Strings without a NUL (the reads stop at one).
        private static IEnumerable<byte[]> Samples()
        {
            for (int i = 1; i < 256; i++)
            {
                yield return new[] { (byte)'a', (byte)i, (byte)'z' };
            }

            yield return Encoding.ASCII.GetBytes("Lord British");
            yield return new byte[] { 0x80, 0x99, 0x9F, 0x81, 0x8D };
            yield return new byte[] { 0x01, 0x41, 0x1F, 0x42, 0x0A, 0x0D };

            var big = new byte[600];

            for (int i = 0; i < big.Length; i++)
            {
                big[i] = (byte)(i % 7 == 0 ? 0x07 : 0x20 + i % 0xDF);
            }

            yield return big;
        }

        private static void WithCache(bool on, Action action)
        {
            Cp1252StringCache saved = StringHelper.Cp1252Cache;
            StringHelper.Cp1252Cache = on ? new Cp1252StringCache() : null;

            try
            {
                action();
            }
            finally
            {
                StringHelper.Cp1252Cache = saved;
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Cp1252_reads_match_the_old_decode(bool cache)
        {
            WithCache(cache, () =>
            {
                foreach (byte[] sample in Samples())
                {
                    var nul = new byte[sample.Length + 2];
                    sample.CopyTo(nul, 0);
                    nul[^1] = 0x5A;

                    foreach (bool safe in new[] { false, true })
                    {
                        string expected = OldCp1252(sample, safe);
                        string hex = Convert.ToHexString(sample);

                        var r = new StackDataReader(nul);
                        r.ReadASCII(safe).Should().Be(expected, $"{hex} safe {safe}");
                        r.Position.Should().Be(sample.Length + 1);

                        var fixedR = new StackDataReader(sample);
                        fixedR.ReadASCII(sample.Length, safe).Should().Be(expected, $"{hex} fixed, safe {safe}");

                        var s = new SpanReader(nul);
                        (safe ? s.ReadCp1252Safe() : s.ReadCp1252()).Should().Be(expected, $"{hex} span, safe {safe}");
                        s.Position.Should().Be(sample.Length + 1);

                        var sf = new SpanReader(sample);
                        (safe ? sf.ReadCp1252Safe(sample.Length) : sf.ReadCp1252(sample.Length))
                            .Should().Be(expected, $"{hex} span fixed, safe {safe}");
                    }
                }
            });
        }

        [Fact]
        public void A_safe_read_of_clean_text_keeps_the_cached_instance()
        {
            WithCache(true, () =>
            {
                byte[] data = Encoding.ASCII.GetBytes("Lord British\0");

                string a = new StackDataReader(data).ReadASCII(true);
                string b = new StackDataReader(data).ReadASCII(true);
                string c = new SpanReader(data).ReadCp1252Safe();

                a.Should().Be("Lord British");
                b.Should().BeSameAs(a);
                c.Should().BeSameAs(a);
            });
        }

        [Fact]
        public void Unicode_and_utf8_safe_reads_drop_the_unsafe_chars()
        {
            string text = "a\u0001b￾c￿d\u001Fe�";
            string expected = OldFilter(text);
            expected.Should().Be("abcde�");

            byte[] be = Encoding.BigEndianUnicode.GetBytes(text + "\0");
            byte[] le = Encoding.Unicode.GetBytes(text + "\0");
            byte[] utf8 = Encoding.UTF8.GetBytes(text + "\0");

            new StackDataReader(be).ReadUnicodeBE(true).Should().Be(expected);
            new StackDataReader(le).ReadUnicodeLE(true).Should().Be(expected);
            new StackDataReader(utf8).ReadUTF8(true).Should().Be(expected);
            new StackDataReader(be).ReadUnicodeBE(false).Should().Be(text);

            new SpanReader(be).ReadBigUniSafe().Should().Be(expected);
            new SpanReader(le).ReadLittleUniSafe().Should().Be(expected);
            new SpanReader(utf8).ReadUTF8Safe().Should().Be(expected);

            string clean = new string('k', 300);
            SpanText.Filter(clean, true).Should().BeSameAs(clean);
            SpanText.Filter(text, false).Should().BeSameAs(text);
        }
    }
}
