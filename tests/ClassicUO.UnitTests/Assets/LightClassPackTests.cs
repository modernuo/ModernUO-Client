// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Linq;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // light_alpha_fade: lightclass.bin (pack-format.md "lightclass.bin"): the round trip, the seed, and the reader's checks
    public sealed class LightClassPackTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cuo_lightclass_{Guid.NewGuid():N}");

        public LightClassPackTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void Seed_round_trips()
        {
            string path = Path.Combine(_dir, LightClassFile.FileName);
            byte[] seed = LightClassFile.Seed();
            LightClassFile.Write(path, seed);

            Assert.Equal(seed, LightClassFile.Read(path));
            Assert.Equal(16 + 4 + LightClassFile.MaxCount, new FileInfo(path).Length);
        }

        [Fact]
        public void Seed_lists_each_standard_shape_once()
        {
            Assert.Empty(LightClassFile.SeedSurface.Intersect(LightClassFile.SeedRadial));
            Assert.Equal(LightClassFile.SeedSurface.Length, LightClassFile.SeedSurface.Distinct().Count());
            Assert.Equal(LightClassFile.SeedRadial.Length, LightClassFile.SeedRadial.Distinct().Count());

            // the bank windows' shapes are surface glows; the lamp post's (29), wall torches' (5, 8) and sconces' (16, 19) radial
            byte[] seed = LightClassFile.Seed();
            Assert.Equal((byte)LightClass.Surface, seed[26]);
            Assert.Equal((byte)LightClass.Surface, seed[27]);

            foreach (int id in new[] { 1, 2, 5, 8, 16, 19, 29 })
            {
                Assert.Equal((byte)LightClass.Radial, seed[id]);
            }

            Assert.Equal((byte)LightClass.Unlisted, seed[43]);
            Assert.Equal((byte)LightClass.Unlisted, seed[200]);
        }

        // every shape the pack's lights.bin holds is classed by the seed; no class for an id without a shape
        [Fact]
        public void Seed_covers_the_packs_light_shapes()
        {
            (LightEntry[] lights, _) = LightFile.Read(Path.Combine(TileDrawOracleTests.PackDir(), TableConst.LightsFile));
            byte[] seed = LightClassFile.Seed();

            for (int id = 0; id < seed.Length; id++)
            {
                bool shape = id < lights.Length && lights[id].Width != 0 && lights[id].Height != 0;
                Assert.True(shape == (seed[id] != (byte)LightClass.Unlisted), $"light {id}: shape {shape}, class {seed[id]}");
            }
        }

        [Fact]
        public void Lists_build_the_table_and_refuse_bad_ids()
        {
            byte[] t = LightClassFile.FromLists(new[] { 26, 27 }, new[] { 1 });
            Assert.Equal((byte)LightClass.Surface, t[26]);
            Assert.Equal((byte)LightClass.Radial, t[1]);
            Assert.Equal(2, LightClassFile.ToLists(t).Surface.Count);

            Assert.Throws<ArgumentException>(() => LightClassFile.FromLists(new[] { 26 }, new[] { 26 }));
            Assert.Throws<ArgumentException>(() => LightClassFile.FromLists(new[] { 256 }, Array.Empty<int>()));
            Assert.Throws<ArgumentException>(() => LightClassFile.FromLists(new[] { -1 }, Array.Empty<int>()));
        }

        [Fact]
        public void Reader_refuses_unknown_classes_and_bad_sizes()
        {
            string path = Path.Combine(_dir, LightClassFile.FileName);
            byte[] seed = LightClassFile.Seed();
            LightClassFile.Write(path, seed);
            byte[] good = File.ReadAllBytes(path);

            byte[] badClass = (byte[])good.Clone();
            badClass[20 + 5] = 3;
            File.WriteAllBytes(path, badClass);
            Assert.Throws<InvalidDataException>(() => LightClassFile.Read(path));

            File.WriteAllBytes(path, good.AsSpan(0, good.Length - 1).ToArray());
            Assert.Throws<InvalidDataException>(() => LightClassFile.Read(path));

            byte[] bigCount = (byte[])good.Clone();
            BitConverter.GetBytes(300).CopyTo(bigCount, 16);
            File.WriteAllBytes(path, bigCount);
            Assert.Throws<InvalidDataException>(() => LightClassFile.Read(path));

            Assert.Throws<InvalidDataException>(() => LightClassFile.Write(path, new byte[] { 0, 1, 2, 3 }));
        }
    }
}
