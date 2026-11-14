// SPDX-License-Identifier: BSD-2-Clause

extern alias frozen;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.Game.Managers;
using ClassicUO.IO;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using FrozenFiles = frozen::ClassicUO.Assets.UOFileManager;

namespace ClassicUO.UnitTests.Assets
{
    // The last UO-folder files in the pack (pack-format.md "Skills, skill groups, multimap, string dictionary" and
    // "Loose text files"): the client's loaders on the pack give what the frozen MUL/UOP loaders give on the files, on
    // synthetic data; a pack without a table behaves like an install without the file.
    public sealed class MiscPackTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cuo_misc_pack_{Guid.NewGuid():N}");
        private readonly string _uo, _pack, _empty;

        public MiscPackTests()
        {
            _uo = Path.Combine(_dir, "uo");
            _pack = Path.Combine(_dir, "pack");
            _empty = Path.Combine(_dir, "empty");
            Directory.CreateDirectory(_uo);
            Directory.CreateDirectory(_pack);
            Directory.CreateDirectory(_empty);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private UOFileManager PackManager(UOFilesOverrideMap overrides = null) =>
            new UOFileManager(ClientVersion.CV_7090, _empty, overrides ?? new UOFilesOverrideMap(), PackAssets.OpenForTests(_pack));

        // ------------------------------------------------------------------ string lists

        [Fact]
        public void String_List_Round_Trips_Raw_And_Brotli()
        {
            var entries = new List<(uint, string)> { (1, "Alchemy"), (0, ""), (7, "Ñandú ✓"), (0, new string('x', 5000)) };

            foreach (bool brotli in new[] { false, true })
            {
                var path = Path.Combine(_pack, $"list_{brotli}.bin");
                StringListFile.Write(path, MiscConst.MagicSkills, entries, brotli);
                StringListFile.Read(path, MiscConst.MagicSkills).Should().Equal(entries);
                Action wrongMagic = () => StringListFile.Read(path, MiscConst.MagicStringDict);
                wrongMagic.Should().Throw<InvalidDataException>();
            }
        }

        [Fact]
        public void String_List_Rejects_Forged_Sizes_And_Ends()
        {
            var good = StringListFile.Encode(new List<(uint, string)> { (0, "ab"), (0, "cd") });
            StringListFile.Decode(good).Select(e => e.Text).Should().Equal("ab", "cd");

            var count = (byte[])good.Clone();
            count[0] = 3;
            ((Action)(() => StringListFile.Decode(count))).Should().Throw<InvalidDataException>();

            var end = (byte[])good.Clone();
            end[8 + 4] = 3; // the first entry ends after the second
            end[8 + 12] = 2;
            ((Action)(() => StringListFile.Decode(end))).Should().Throw<InvalidDataException>();

            ((Action)(() => StringListFile.Decode(good.AsSpan(0, good.Length - 1)))).Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void Skill_Groups_File_Round_Trips_And_Rejects_Truncation()
        {
            var path = Path.Combine(_pack, MiscConst.SkillGroupsFile);
            SkillGroupsFile.Write(path, new[] { "Combat", "Magie" }, new[] { 1, 2, 0, -1, 9 });
            var (names, assignments) = SkillGroupsFile.Read(path);
            names.Should().Equal("Combat", "Magie");
            assignments.Should().Equal(1, 2, 0, -1, 9);

            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 1).ToArray());
            ((Action)(() => SkillGroupsFile.Read(path))).Should().Throw<InvalidDataException>();
        }

        // ------------------------------------------------------------------ skills

        private void WriteSkillsMul(params (bool Action, string Name)[] skills)
        {
            using var mul = new BinaryWriter(File.Create(Path.Combine(_uo, "skills.mul")));
            using var idx = new BinaryWriter(File.Create(Path.Combine(_uo, "Skills.idx")));
            for (int i = 0; i < skills.Length; i++)
            {
                if (skills[i].Name == null)
                {
                    idx.Write(-1); idx.Write(-1); idx.Write(0); // an empty slot: skipped
                    continue;
                }
                var name = Encoding.ASCII.GetBytes(skills[i].Name + "\0");
                idx.Write((int)mul.BaseStream.Position); idx.Write(1 + name.Length); idx.Write(0);
                mul.Write((byte)(skills[i].Action ? 1 : 0));
                mul.Write(name);
            }
        }

        [Fact]
        public void Skills_From_The_Pack_Match_The_Frozen_Loader()
        {
            WriteSkillsMul((true, "Alchemy"), (false, "Zoology"), (false, null), (true, "Begging"));
            var frozenFm = new FrozenFiles(ClientVersion.CV_7090, _uo);
            var want = frozenFm.Skills;
            want.Load();
            want.SkillsCount.Should().Be(3);
            StringListFile.Write(Path.Combine(_pack, MiscConst.SkillsFile), MiscConst.MagicSkills,
                want.Skills.Select(s => (s.HasAction ? MiscConst.SkillHasAction : 0u, s.Name)).ToList(), brotli: false);

            var got = PackManager().Skills;
            got.Load();
            got.Skills.Select(s => (s.Index, s.Name, s.HasAction)).Should().Equal(want.Skills.Select(s => (s.Index, s.Name, s.HasAction)));
            got.SortedSkills.Select(s => s.Index).Should().Equal(want.SortedSkills.Select(s => s.Index));
            frozenFm.Dispose();
        }

        [Fact]
        public void Skills_Without_A_Table_Are_Empty_Not_A_Startup_Error()
        {
            var got = PackManager().Skills;
            got.Load();
            got.SkillsCount.Should().Be(0);
        }

        // ------------------------------------------------------------------ skill groups

        [Fact]
        public void Skill_Groups_Are_Built_Like_The_Mul_Reader_Built_Them()
        {
            // group 0 is Miscellaneous; 5 is past the last group: skipped without using up a skill; at most 4 skills
            var groups = SkillsGroupManager.GroupsFromTable(new[] { "Combat", "Magic" }, new[] { 1, 2, 0, 5, 1, 1 }, 4);
            groups.Should().HaveCount(3);
            groups[1].Name.Should().Be("Combat");
            groups[2].Name.Should().Be("Magic");
            Skills(groups[0]).Should().Equal(2);
            Skills(groups[1]).Should().Equal(0, 3);
            Skills(groups[2]).Should().Equal(1);

            // a negative group number throws (the caller falls back to the built-in groups, as with skillgrp.mul)
            ((Action)(() => SkillsGroupManager.GroupsFromTable(new[] { "A" }, new[] { -1 }, 4))).Should().Throw<IndexOutOfRangeException>();

            static IEnumerable<int> Skills(SkillsGroup g) => Enumerable.Range(0, g.Count).Select(i => (int)g.GetSkill(i));
        }

        // ------------------------------------------------------------------ multimap

        private static byte[] FacetImage(int w, int h, int seed)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)w); bw.Write((ushort)h);
            var rnd = new Random(seed);
            for (int y = 0; y < h; y++)
            {
                var runs = new List<(byte, ushort)>();
                for (int x = 0; x < w;)
                {
                    int n = Math.Min(w - x, rnd.Next(1, 6));
                    runs.Add(((byte)n, (ushort)rnd.Next(0, 0x8000)));
                    x += n;
                }
                bw.Write(runs.Count * 3);
                foreach (var (n, c) in runs) { bw.Write(n); bw.Write(c); }
            }
            bw.Flush();
            return ms.ToArray();
        }

        [Fact]
        public void Facet_Images_From_The_Pack_Match_The_Frozen_Loader()
        {
            // facet00 and facet02 only: the client indexes the folder's files in order, so facet02.mul is facet 1
            File.WriteAllBytes(Path.Combine(_uo, "Multimap.rle"), new byte[] { 2, 0, 0, 0, 2, 0, 0, 0, 0x84 });
            File.WriteAllBytes(Path.Combine(_uo, "facet00.mul"), FacetImage(40, 30, 1));
            File.WriteAllBytes(Path.Combine(_uo, "facet02.mul"), FacetImage(25, 17, 2));
            var frozenFm = new FrozenFiles(ClientVersion.CV_7090, _uo);
            var want = frozenFm.MultiMaps;
            want.Load();
            want.FacetPaths.Select(Path.GetFileName).Should().Equal("facet00.mul", "facet02.mul");

            var w = new RecordStoreWriter(1 + want.FacetPaths.Length);
            var sources = new[] { want.RlePath }.Concat(want.FacetPaths).ToArray();
            for (int i = 0; i < sources.Length; i++)
            {
                var raw = File.ReadAllBytes(sources[i]);
                ushort codec = i == 1 ? StoreConst.CodecBrotli : StoreConst.CodecRaw;
                int rec = w.Add(RecordStoreWriter.Encode(raw, codec), (uint)raw.Length, codec, 0, 0);
                w.Slots[i] = new StoreSlot { Record = (uint)rec + 1 };
            }
            w.Write(Path.Combine(_pack, MiscConst.MultiMapFile), MiscConst.MagicMultiMap, 0);

            var fm = PackManager();
            var got = fm.MultiMaps;
            got.Load();
            got.HasFacet(0).Should().BeTrue();
            got.HasFacet(1).Should().BeTrue();
            got.HasFacet(2).Should().BeFalse();
            foreach (var (facet, x0, y0, x1, y1) in new[] { (0, 0, 0, 0, 0), (0, 5, 3, 31, 22), (1, 0, 0, 25, 17), (1, 2, 1, 20, 9) })
            {
                var a = want.LoadFacet(facet, facet == 0 ? 40 : 25, facet == 0 ? 30 : 17, x0, y0, x1, y1);
                var b = got.LoadFacet(facet, facet == 0 ? 40 : 25, facet == 0 ? 30 : 17, x0, y0, x1, y1);
                (b.Width, b.Height).Should().Be((a.Width, a.Height));
                b.Pixels.ToArray().Should().Equal(a.Pixels.ToArray(), $"facet {facet} ({x0},{y0})-({x1},{y1})");
            }
            got.LoadFacet(2, 10, 10, 0, 0, 0, 0).Pixels.Length.Should().Be(0);
            fm.Dispose();
            frozenFm.Dispose();
        }

        [Fact]
        public void No_Multimap_Table_Means_No_Maps()
        {
            var m = PackManager().MultiMaps;
            m.Load();
            m.HasFacet(0).Should().BeFalse();
            m.LoadFacet(0, 10, 10, 0, 0, 0, 0).Pixels.Length.Should().Be(0);
        }

        // ------------------------------------------------------------------ string dictionary

        [Fact]
        public void String_Dictionary_From_The_Pack()
        {
            StringListFile.Write(Path.Combine(_pack, MiscConst.StringDictFile), MiscConst.MagicStringDict,
                new List<(uint, string)> { (0, "a"), (0, "b c"), (0, "") }, brotli: true);
            var d = PackManager().StringDictionary;
            d.Load();
            d.TryGetString(1, out var s).Should().BeTrue();
            s.Should().Be("b c");
            d.TryGetString(3, out _).Should().BeFalse();
        }

        // ------------------------------------------------------------------ loose files

        [Fact]
        public void Loose_Files_Come_From_The_Pack_And_An_Override_Wins()
        {
            Directory.CreateDirectory(Path.Combine(_pack, MiscConst.LooseDir));
            File.WriteAllText(Path.Combine(_pack, "loose", "prof.txt"), "pack");
            File.WriteAllText(Path.Combine(_empty, "Prof.txt"), "folder");
            var shard = Path.Combine(_dir, "shard-prof.txt");
            File.WriteAllText(shard, "override");

            var fm = PackManager();
            File.ReadAllText(fm.GetLooseFilePath("Prof.txt")).Should().Be("pack");
            // the install had no citytext.enu: the pack has none, and the folder is not consulted
            File.Exists(fm.GetLooseFilePath("citytext.enu")).Should().BeFalse();

            var overrides = new UOFilesOverrideMap { ["prof.txt"] = shard };
            File.ReadAllText(PackManager(overrides).GetLooseFilePath("Prof.txt")).Should().Be("override");
        }

        [Fact]
        public void A_Pack_Without_Loose_Files_Reads_The_Folder_As_Before()
        {
            File.WriteAllText(Path.Combine(_empty, "Prof.txt"), "folder");
            File.ReadAllText(PackManager().GetLooseFilePath("Prof.txt")).Should().Be("folder");
        }
    }
}
