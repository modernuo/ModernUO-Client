using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Uo.Pack;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Fixes from the Codex audit of the tiledata single-source change (poc-log "Tile data single source", Codex audit).
    public class TileDataAuditTests
    {
        // ---- P1: untrusted name references cannot make the reader decode huge strings

        [Fact]
        public void Reader_Rejects_The_Audit_File_Of_81920_References_To_One_65535_Byte_Name_Before_Allocating_Names()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cuo-tiledata-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var land = new LandTileInfo[PackConst.LandTileCount];
                var statics = new StaticTileInfo[TileDataFile.MaxStatics];

                for (int i = 0; i < land.Length; i++)
                {
                    land[i].NameLength = ushort.MaxValue;
                }

                for (int i = 0; i < statics.Length; i++)
                {
                    statics[i].NameLength = ushort.MaxValue;
                }

                var path = Path.Combine(dir, "audit.bin");
                TileDataFile.Write(path, land, statics, new byte[ushort.MaxValue]);
                new FileInfo(path).Length.Should().Be(2_490_395); // the audit's valid-sized file

                long before = GC.GetAllocatedBytesForCurrentThread();
                Action read = () => TileDataFile.Read(path);
                read.Should().Throw<InvalidDataException>().WithMessage("*land 0 name is 65535 bytes*");
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                // the entry arrays (2.4 MB) and nothing like the ~10 GiB of strings the audit measured
                allocated.Should().BeLessThan(4 << 20);

                // a name table larger than every entry's longest name is refused from the header
                var table = Path.Combine(dir, "table.bin");

                using (var w = new BinaryWriter(File.Create(table)))
                {
                    w.Write(TableConst.MagicTileData);
                    w.Write((ushort)1);
                    w.Write((ushort)0);
                    w.Write(0UL);
                    w.Write(PackConst.LandTileCount);
                    w.Write(16);
                    w.Write(TileDataFile.MaxNameTableBytes + 1);
                }

                ((Action)(() => TileDataFile.Read(table))).Should().Throw<InvalidDataException>().WithMessage("*name table size*");

                // the longest accepted name, then one byte more
                var small = new StaticTileInfo[16];
                small[1].NameLength = TileDataFile.MaxNameBytes;
                var ok = Path.Combine(dir, "ok.bin");
                TileDataFile.Write(ok, land.Select(_ => default(LandTileInfo)).ToArray(), small, new byte[TileDataFile.MaxNameBytes + 1]);
                TileDataFile.Read(ok).Statics[1].NameLength.Should().Be(TileDataFile.MaxNameBytes);
                small[1].NameLength = TileDataFile.MaxNameBytes + 1;
                var tooLong = Path.Combine(dir, "long.bin");
                TileDataFile.Write(tooLong, land.Select(_ => default(LandTileInfo)).ToArray(), small, new byte[TileDataFile.MaxNameBytes + 1]);
                ((Action)(() => TileDataFile.Read(tooLong))).Should().Throw<InvalidDataException>().WithMessage("*static 1 name is 65 bytes*");
            }
            finally
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    File.Delete(f);
                }

                Directory.Delete(dir);
            }
        }

        [Fact]
        public void Identical_Name_References_Decode_To_One_Shared_String_And_The_Writer_Caps_Names()
        {
            var names = TileDataFile.BuildNames(new[] { "wall", "door", "wall" }, out var refs);
            var land = new LandTileInfo[2];
            var statics = new StaticTileInfo[3];
            land[0].NameOffset = refs[0].Offset;
            land[0].NameLength = refs[0].Length;
            statics[0].NameOffset = refs[2].Offset;
            statics[0].NameLength = refs[2].Length;
            statics[1].NameOffset = refs[1].Offset;
            statics[1].NameLength = refs[1].Length;

            var (l, s) = TileDataFile.DecodeNames(land, statics, names);

            (l[0], l[1], s[0], s[1], s[2]).Should().Be(("wall", "", "wall", "door", ""));
            ReferenceEquals(l[0], s[0]).Should().BeTrue();

            ((Action)(() => TileDataFile.BuildNames(new[] { new string('x', TileDataFile.MaxNameBytes + 1) }, out _)))
                .Should().Throw<InvalidDataException>().WithMessage("*at most 64*");
        }

        // ---- The easel (0x0F65): the pack's flag decides; the runtime client version has no say (owner rule, pack-format §NoDraw)

        [Fact]
        public void The_Easel_Follows_Its_Pack_Flag_And_The_Runtime_Client_Version_Has_No_Effect()
        {
            var statics = new StaticTiles[0x10000];
            const ushort easel = 0x0F65;

            // converted from a source older than 6.0.14.4: drawn; from a newer one with NoDiagonal: NoDraw
            GameObject.CanBeDrawn(statics, easel, false).Should().BeTrue();
            statics[easel].PackFlags = TilePackFlags.NoDraw;
            GameObject.CanBeDrawn(statics, easel, false).Should().BeFalse();
            GameObject.CanBeDrawn(statics, easel, true).Should().BeFalse();

            // the decision has no version input, and no client code keeps the version rule
            typeof(GameObject).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == nameof(GameObject.CanBeDrawn))
                .SelectMany(m => m.GetParameters())
                .Should().NotContain(p => p.ParameterType == typeof(ClassicUO.Utility.ClientVersion));

            string repo = ClassicUO.UnitTests.Ecs.WriteSiteScanner.FindRepoRoot();
            string obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
            var hits = new[] { Path.Combine(repo, "src"), Path.Combine(repo, "poc", "Uo.Render"), Path.Combine(repo, "poc", "Uo.Pack"), Path.Combine(repo, "poc", "Uo.Client") }
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains(obj))
                .Where(f => File.ReadAllText(f).Contains("0x0F65", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(repo, f))
                .ToList();
            hits.Should().BeEmpty("the easel rule is applied once by the converter (Uo.Converter/TileRules.cs)");
        }

        // ---- Gpu path: a drawn static that the bake left out is drawn by the list fill on every frame

        [Fact]
        public void A_Gargoyle_Only_Static_Is_Omitted_From_The_Bake_But_Drawn_For_A_Gargoyle()
        {
            var statics = new StaticTiles[0x10000];
            statics[0x5683].PackFlags = TilePackFlags.NoDrawUnlessGargoyle;
            statics[0x5684].PackFlags = TilePackFlags.NoDraw;

            // 0x5683: not baked, drawn for a gargoyle, so the Gpu host must draw it itself (animation offset 0 included)
            GameScene.BakeOmits(statics, 0x5683).Should().BeTrue();
            GameObject.CanBeDrawn(statics, 0x5683, true).Should().BeTrue();
            GameObject.CanBeDrawn(statics, 0x5683, false).Should().BeFalse();

            // baked statics are not replaced; NoDraw ones are omitted and never drawn
            GameScene.BakeOmits(statics, 0x1000).Should().BeFalse();
            GameScene.BakeOmits(statics, 0x5684).Should().BeTrue();
            GameObject.CanBeDrawn(statics, 0x5684, true).Should().BeFalse();

            // the predicate is ChunkBaker's (TileDraw.Static for a non-gargoyle player) for every flag combination
            for (int f = 0; f < 16; f++)
            {
                statics[0x2000].PackFlags = (TilePackFlags)f;
                GameScene.BakeOmits(statics, 0x2000).Should().Be(!TileDraw.Static((TilePackFlags)f, playerIsGargoyle: false));
            }
        }

        [TileDataPackFact]
        public void On_The_Pack_0x5683_Is_Gargoyle_Only()
        {
            TileDataLoader.Read(Path.Combine(TileDrawOracleTests.PackDir(), TableConst.TileDataFile), out _, out var statics);
            statics[0x5683].PackFlags.Should().HaveFlag(TilePackFlags.NoDrawUnlessGargoyle);
            GameScene.BakeOmits(statics, 0x5683).Should().BeTrue();
            GameObject.CanBeDrawn(statics, 0x5683, true).Should().BeTrue();
        }

        // ---- Multi components: the season swap recomputes the draw decision for the swapped graphic

        [Fact]
        public void A_Multi_Component_Takes_The_Draw_Flags_Of_Its_Season_Graphic()
        {
            const ushort original = 0x0CA7;
            ushort spring = SeasonManager.GetSeasonGraphic(Season.Spring, original);
            spring.Should().NotBe(original, "the default seasons file swaps 0x0CA7 in spring");

            var data = new StaticTiles[0x10000];
            data[spring].PackFlags = TilePackFlags.NoDraw;
            var saved = GameObject.TestStaticData;
            GameObject.TestStaticData = data;

            try
            {
                var world = new World();
                var m = new Multi(world) { Graphic = original };
                typeof(Multi).GetField("_originalGraphic", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(m, original);
                var season = typeof(World).GetProperty(nameof(World.Season))!;

                m.UpdateGraphicBySeason();
                (m.Graphic, m.AllowedToDraw).Should().Be((original, true));

                season.SetValue(world, Season.Spring);
                m.UpdateGraphicBySeason();
                (m.Graphic, m.AllowedToDraw).Should().Be((spring, false));

                season.SetValue(world, Season.Summer);
                m.UpdateGraphicBySeason();
                (m.Graphic, m.AllowedToDraw).Should().Be((original, true));
            }
            finally
            {
                GameObject.TestStaticData = saved;
            }
        }

        // ---- poc client ground items (Uo.Client RenderExtract): the pack's flags, the player's race, and the corpse

        [Fact]
        public void Poc_Ground_Items_Follow_The_Draw_Flags_With_Race_And_The_Corpse_Exception()
        {
            var statics = new StaticTileData[0x10000];
            statics[0x1000].PackFlags = TilePackFlags.NoDraw;
            statics[0x5683].PackFlags = TilePackFlags.NoDrawUnlessGargoyle;
            statics[GroundItemDraw.CorpseGraphic].PackFlags = TilePackFlags.NoDraw;

            GroundItemDraw.Drawn(statics, 0x0EED, false).Should().BeTrue();
            GroundItemDraw.Drawn(statics, 0x1000, true).Should().BeFalse();
            GroundItemDraw.Drawn(statics, 0x5683, false).Should().BeFalse();
            GroundItemDraw.Drawn(statics, 0x5683, true).Should().BeTrue();
            GroundItemDraw.Drawn(statics, GroundItemDraw.CorpseGraphic, false).Should().BeTrue(); // Item: IsCorpse || CanBeDrawn
            GroundItemDraw.Drawn(new StaticTileData[16], 16, false).Should().BeFalse();

            GroundItemDraw.IsGargoyleBody(0x029A).Should().BeTrue();
            GroundItemDraw.IsGargoyleBody(0x029B).Should().BeTrue();
            GroundItemDraw.IsGargoyleBody(0x0190).Should().BeFalse();

            // RenderExtract uses it for every ground item
            string repo = ClassicUO.UnitTests.Ecs.WriteSiteScanner.FindRepoRoot();
            File.ReadAllText(Path.Combine(repo, "poc", "Uo.Client", "RenderExtract.cs")).Should().Contain("GroundItemDraw.Drawn(staticData, g, gargoyle)");
        }
    }
}
