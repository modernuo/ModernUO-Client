using System;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Game;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Assets
{
    // Rider offsets on mounts (body-choices round-2 audit, finding 1). Both renderers draw the rider at the Mounts
    // table's per-item offset plus the pack's MountShift for the mount body (the drawn art's mount height minus the
    // default art's). The table was set for the default (aos) art and holds item-specific values the Bodyconv heights
    // don't have (UOP tigers 18, unicorn 0x3EB4 -9 with a Bodyconv height of 0, 0x3E90 0 with -9), so a default pack
    // must draw every rider exactly as the table says.
    //
    // Packs: CUO_MOUNT_AOS_PACK (else CUO_TEST_PACK, else uo-packs\<CURRENT>) and, optionally, CUO_MOUNT_T2A_PACK.
    public class MountHeightTests
    {
        private readonly ITestOutputHelper _output;

        public MountHeightTests(ITestOutputHelper output) => _output = output;

        internal static string AosPack() =>
            Environment.GetEnvironmentVariable("CUO_MOUNT_AOS_PACK") is { Length: > 0 } p ? p : TileDrawOracleTests.PackDir();

        [Theory]
        [InlineData(0x3EBB, 0, -9)]  // skeletal mount, default art
        [InlineData(0x3EBB, 9, 0)]   // skeletal mount drawn as horse 200 (t2a)
        [InlineData(0x3EC7, 0, 18)]  // tiger: item-specific offset kept
        [InlineData(0x3EA2, 0, 0)]   // dark brown horse
        [InlineData(0x1234, 5, 5)]   // no table entry: the shift alone (an extra with its own height)
        public void Offset_Is_The_Item_Offset_Plus_The_Pack_Shift(ushort item, int shift, int expected)
        {
            AnimationRules.MountOffsetY(item, (sbyte)shift).Should().Be((sbyte)expected);
        }

        private static (int Compared, int Changed) CompareEveryItem(string pack, ITestOutputHelper output, Func<ushort, bool> report)
        {
            TileDataLoader.Read(Path.Combine(pack, TableConst.TileDataFile), out _, out var statics);
            var bodies = AnimTableFile.Read(Path.Combine(pack, "anim", AnimTableFile.FileName)).Bodies;
            int compared = 0, changed = 0;

            for (int g = 0; g < statics.Length; g++)
            {
                ushort body = AnimationRules.MountBody((ushort)g, statics[g].AnimID);
                sbyte shift = body < bodies.Length ? bodies[body].MountShift : (sbyte)0;
                sbyte old = AnimationRules.MountOffsetY((ushort)g);
                sbyte now = AnimationRules.MountOffsetY((ushort)g, shift);
                compared++;

                if (old != now)
                {
                    changed++;

                    if (report((ushort)g))
                    {
                        output.WriteLine($"item 0x{g:X4} (body {body}): {old} -> {now}");
                    }
                }
            }

            return (compared, changed);
        }

        [MountPackFact]
        public void Default_Pack_Draws_Every_Rider_As_Before()
        {
            var pack = AosPack();
            var (compared, changed) = CompareEveryItem(pack, _output, _ => true);
            _output.WriteLine($"{pack}: {compared} tiledata items compared (each as a mount item), {changed} offsets changed");
            compared.Should().Be(PackConst.StaticTileCount);
            changed.Should().Be(0);
        }

        [Fact]
        public void T2a_Pack_Moves_Riders_Of_Mounts_Drawn_With_Other_Art()
        {
            var pack = Environment.GetEnvironmentVariable("CUO_MOUNT_T2A_PACK");

            if (string.IsNullOrEmpty(pack) || !File.Exists(Path.Combine(pack, "anim", AnimTableFile.FileName)))
            {
                _output.WriteLine("CUO_MOUNT_T2A_PACK not set: skipped");

                return;
            }

            var bodies = AnimTableFile.Read(Path.Combine(pack, "anim", AnimTableFile.FileName)).Bodies;
            var (compared, changed) = CompareEveryItem(pack, _output, g => g >= 0x3E90 && g <= 0x3EE4);
            _output.WriteLine($"{pack}: {compared} items, {changed} offsets changed");
            sbyte skeletal = AnimationRules.MountOffsetY(0x3EBB, bodies[793].MountShift);
            sbyte horse = AnimationRules.MountOffsetY(0x3EA2, bodies[AnimationRules.MountBody(0x3EA2, 0)].MountShift);
            (skeletal, horse).Should().Be(((sbyte)0, (sbyte)0), "skeletal mount 793 is drawn as horse 200, so its rider sits like a horse's");
        }
    }

    public sealed class MountPackFactAttribute : FactAttribute
    {
        public MountPackFactAttribute()
        {
            var dir = MountHeightTests.AosPack();

            if (!File.Exists(Path.Combine(dir, "anim", AnimTableFile.FileName)) || !File.Exists(Path.Combine(dir, TableConst.TileDataFile)))
            {
                Skip = $"integration data not on this machine: {dir}";
            }
        }
    }
}
