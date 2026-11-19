using System;

namespace ClassicUO.UnitTests.Utility
{
    /// <summary>Deterministic payloads shared by the zlib tests; zlib-vectors.txt holds their compressed forms.</summary>
    public static class ZlibPayloads
    {
        public static byte[][] All()
        {
            var rnd = new Random(12345);

            byte[] Random(int n)
            {
                var b = new byte[n];
                rnd.NextBytes(b);
                return b;
            }

            // 0xD8 custom house: 5-byte entries (id u16 BE, x, y, z) as in the client's plane mode 0.
            var house = new byte[5 * 700];
            for (int i = 0; i < 700; i++)
            {
                int o = i * 5;
                ushort id = (ushort)(0x0500 + rnd.Next(0, 40));
                house[o] = (byte)(id >> 8);
                house[o + 1] = (byte)id;
                house[o + 2] = (byte)(i % 20 - 10);
                house[o + 3] = (byte)(i / 20 % 20 - 10);
                house[o + 4] = (byte)(i % 3 * 5);
            }

            string layout = string.Concat(
                System.Linq.Enumerable.Repeat("{ page 0 }{ resizepic 0 0 9200 300 200 }{ text 10 10 0 1 }{ button 20 150 4005 4007 1 0 1 }", 12));

            var repetitive = new byte[10_000];
            Array.Fill(repetitive, (byte)0x41);

            return new[]
            {
                Array.Empty<byte>(),
                new byte[] { 7 },
                Random(3),
                Random(4095),
                Random(33333),
                repetitive,
                house,
                System.Text.Encoding.UTF8.GetBytes(layout),
                System.Text.Encoding.BigEndianUnicode.GetBytes(layout),
            };
        }

        public static System.Collections.Generic.IEnumerable<object[]> Cases()
        {
            int n = All().Length;

            for (int i = 0; i < n; i++)
                yield return new object[] { i };
        }
    }
}
