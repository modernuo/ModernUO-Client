using System;
using System.Linq;
using System.Threading;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Utility.StringHelper
{
    [Collection("Cp1252Cache")]
    public class Cp1252VariantAndCacheTests
    {
        private static byte[][] Inputs()
        {
            var rng = new Random(11);
            var list = Enumerable.Range(0, 300).Select(_ =>
            {
                byte[] b = new byte[rng.Next(0, 300)];
                rng.NextBytes(b);

                if (rng.Next(2) == 0)
                {
                    for (int i = 0; i < b.Length; i++)
                    {
                        b[i] &= 0x7F;
                    }
                }

                return b;
            }).ToList();

            byte[] all = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();

            for (int len = 0; len <= 70; len++)
            {
                list.Add(all.Take(len).ToArray());
                list.Add(all.Skip(0x80).Take(len).ToArray());
            }

            return list.ToArray();
        }

        [Fact]
        public void Decode_Variants_Agree()
        {
            foreach (byte[] b in Inputs())
            {
                string expected = Cp1252.GetStringBuffered(b);
                Cp1252.GetString(b).Should().Be(expected);
                Cp1252.GetStringTwoPass(b).Should().Be(expected);
                Cp1252.GetStringSimd(b).Should().Be(expected);
                Cp1252.GetStringTable(b).Should().Be(expected);
            }
        }

        [Fact]
        public void Encode_Variants_Agree()
        {
            var rng = new Random(12);
            byte[] x = new byte[700], y = new byte[700];

            for (int n = 0; n < 300; n++)
            {
                char[] c = new char[rng.Next(0, 300)];

                for (int i = 0; i < c.Length; i++)
                {
                    int k = rng.Next(4);
                    c[i] = k == 0 ? (char)rng.Next(0x80, 0x100) : k == 1 ? (char)rng.Next(0x100, 0x2200) : (char)rng.Next(0x20, 0x80);
                }

                int a = Cp1252.GetBytes(c, x);
                int b = Cp1252.GetBytesLatin1Scan(c, y);
                b.Should().Be(a);
                y.AsSpan(0, b).ToArray().Should().Equal(x.AsSpan(0, a).ToArray());
            }
        }

        [Fact]
        public void Cache_Returns_Equal_Strings_And_Reuses_Them()
        {
            var cache = new Cp1252StringCache(16, 64);

            foreach (byte[] b in Inputs())
            {
                cache.GetString(b).Should().Be(Cp1252.GetString(b));
                cache.GetString(b).Should().Be(Cp1252.GetString(b));
            }

            byte[] name = { (byte)'L', (byte)'o', (byte)'r', (byte)'d', 0x92, (byte)'s' };
            string first = cache.GetString(name);
            cache.GetString(name).Should().BeSameAs(first);
            cache.Count.Should().BeLessThanOrEqualTo(cache.Capacity);
            cache.Capacity.Should().Be(32);
        }

        [Fact]
        public void Cache_Hit_Allocates_Nothing()
        {
            var cache = new Cp1252StringCache(64, 64);
            byte[] name = "Britannia guard"u8.ToArray();
            string s = null;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                for (int i = 0; i < 64; i++)
                {
                    s = cache.GetString(name);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                s = cache.GetString(name);

                if (GC.GetAllocatedBytesForCurrentThread() - before == 0)
                {
                    break;
                }

                Thread.Sleep(50);

                if (attempt == 39)
                {
                    (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
                }
            }

            s.Should().Be("Britannia guard");
        }

        [Fact]
        public void Cache_Is_Bounded_Per_Set()
        {
            var cache = new Cp1252StringCache(1, 64);

            for (int i = 0; i < 100; i++)
            {
                cache.GetString(BitConverter.GetBytes(i + 0x41414141));
            }

            cache.Count.Should().Be(2);
            cache.Misses.Should().Be(100);
        }

        [Fact]
        public void Cache_Bypasses_Long_Strings_And_Other_Threads()
        {
            var cache = new Cp1252StringCache(16, 8);
            byte[] longer = new byte[9];
            longer.AsSpan().Fill((byte)'a');
            cache.GetString(longer).Should().Be("aaaaaaaaa");
            cache.Bypassed.Should().Be(1);

            string other = null;
            var t = new Thread(() => other = cache.GetString("abc"u8));
            t.Start();
            t.Join();

            other.Should().Be("abc");
            cache.Bypassed.Should().Be(2);
            cache.Count.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Cp1252ToString_Same_With_Switch_On_And_Off(bool on)
        {
            Cp1252StringCache saved = ClassicUO.Utility.StringHelper.Cp1252Cache;

            try
            {
                ClassicUO.Utility.StringHelper.Cp1252Cache = on ? new Cp1252StringCache() : null;

                foreach (byte[] b in Inputs())
                {
                    ClassicUO.Utility.StringHelper.Cp1252ToString(b).Should().Be(Cp1252.GetStringBuffered(b));
                    ClassicUO.Utility.StringHelper.Cp1252ToString(b).Should().Be(Cp1252.GetStringBuffered(b));
                }

                if (on)
                {
                    ClassicUO.Utility.StringHelper.Cp1252Cache.Hits.Should().BeGreaterThan(0);
                }
            }
            finally
            {
                ClassicUO.Utility.StringHelper.Cp1252Cache = saved;
            }
        }
    }
}
