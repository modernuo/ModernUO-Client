using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.UnitTests.Ecs;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Assets
{
    // Rider offsets on mounts (body-choices audits, rounds 2 and 3). Every renderer draws the rider at the Mounts table's
    // per-item offset plus the pack's MountShift for the mount body. The table was set for the default (aos) art and
    // holds item-specific values the Bodyconv heights don't have (UOP tigers 18, unicorn 0x3EB4 -9 with a Bodyconv height
    // of 0, 0x3E90 0 with -9), so a default pack must draw every rider exactly as the table says. A mount body drawn as a
    // Body.def stand-in takes the stand-in's own offset (owner, 2026-10-31).
    //
    // Fixture packs (converted with the body choices named; the tests check the manifest's record and skip without them):
    // CUO_MOUNT_AOS_PACK (preset aos, no overrides) and CUO_MOUNT_T2A_PACK (preset t2a).
    public class MountHeightTests
    {
        private readonly ITestOutputHelper _output;

        public MountHeightTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(0x3EBB, 0, false, -9)]    // skeletal mount, default art
        [InlineData(0x3EBB, 0, true, 0)]      // skeletal mount drawn as horse 200 (t2a): the stand-in's absolute offset
        [InlineData(0x3EB4, 0, true, 0)]      // unicorn drawn as horse 228 (t2a)
        [InlineData(0x007A, 0, true, 0)]      // unlisted item falling back to body 122 (t2a): also the stand-in's offset
        [InlineData(0x3EC7, 0, false, 18)]    // tiger: item-specific offset kept
        [InlineData(0x3EA2, 0, false, 0)]     // dark brown horse
        [InlineData(0x1234, 5, false, 5)]     // no table entry: the shift alone (an extra with its own height)
        [InlineData(0x3EC7, 127, false, 145)] // table + shift is not clamped to sbyte (round-3 finding 5)
        [InlineData(0x3EDE, -128, false, -138)]
        [InlineData(0x3EC7, -5, true, -5)]    // absolute: the item's offset is ignored
        public void Offset_Is_The_Pack_Seat_On_The_Item(ushort item, int value, bool absolute, int expected)
        {
            AnimationRules.MountOffsetY(item, new MountSeat((sbyte)value, absolute)).Should().Be(expected);
        }

        [Fact]
        public void Ecs_State_Holds_Every_Offset_Sum()
        {
            // AnimationState.MountOffsetY carries the sum: every table offset plus either extreme shift fits.
            foreach (var (item, _, off) in MountTable.Entries)
            {
                foreach (sbyte shift in new sbyte[] { sbyte.MinValue, sbyte.MaxValue })
                {
                    int sum = AnimationRules.MountOffsetY(item, new MountSeat(shift, false));
                    sum.Should().Be(off + shift);
                    ((int)(short)sum).Should().Be(sum);
                }
            }

            new ClassicUO.Ecs.AnimationState { MountOffsetY = 145 }.MountOffsetY.Should().Be(145);
        }

        /// <summary>The fixture pack named by <paramref name="variable"/>, after checking its manifest records <paramref name="preset"/> under the current policy.</summary>
        internal static string Fixture(string variable, string preset)
        {
            var pack = Environment.GetEnvironmentVariable(variable);
            pack.Should().NotBeNullOrEmpty($"{variable} is checked by the skip attribute");
            File.Exists(Path.Combine(pack, "anim", AnimTableFile.FileName)).Should().BeTrue($"{variable} ({pack}) must hold a converted pack");
            var bodies = ManifestFileIo.Read(Path.Combine(pack, "manifest.json")).Bodies;
            bodies.Should().NotBeNull($"{variable} ({pack}) must be built with bodies.json choices (a pack without a record uses the legacy rules and stores no shifts)");
            bodies.Policy.Should().Be(1, $"{variable}: resolver policy 1 (the current one)");
            bodies.Preset.Should().Be(preset, $"{variable} must be a {preset} pack");
            bodies.Remodels.Should().BeEmpty($"{variable}: no per-body overrides");
            bodies.Extras.Should().BeEmpty($"{variable}: no extras");
            bodies.ForceOldAnimations.Should().BeFalse($"{variable}: forceOldAnimations off");
            return pack;
        }

        private static (int Compared, List<(ushort Item, ushort Body, int Old, int Now)> Changed) CompareEveryItem(string pack)
        {
            TileDataLoader.Read(Path.Combine(pack, TableConst.TileDataFile), out _, out var statics);
            var bodies = AnimTableFile.Read(Path.Combine(pack, "anim", AnimTableFile.FileName)).Bodies;
            var changed = new List<(ushort, ushort, int, int)>();
            int compared = 0;

            for (int g = 0; g < statics.Length; g++)
            {
                ushort body = AnimationRules.MountBody((ushort)g, statics[g].AnimID);
                MountSeat seat = body < bodies.Length ? bodies[body].MountSeat : default;
                int old = AnimationRules.MountOffsetY((ushort)g);
                int now = AnimationRules.MountOffsetY((ushort)g, seat);
                compared++;

                if (old != now)
                {
                    changed.Add(((ushort)g, body, old, now));
                }
            }

            return (compared, changed);
        }

        [MountPackFact("CUO_MOUNT_AOS_PACK")]
        public void Default_Pack_Draws_Every_Rider_As_Before()
        {
            var pack = Fixture("CUO_MOUNT_AOS_PACK", "aos");
            var (compared, changed) = CompareEveryItem(pack);
            _output.WriteLine($"{pack}: {compared} tiledata items compared (each as a mount item), {changed.Count} offsets changed");
            compared.Should().Be(PackConst.StaticTileCount);
            changed.Should().BeEmpty();
        }

        [MountPackFact("CUO_MOUNT_T2A_PACK")]
        public void T2a_Pack_Seats_Riders_Of_Stand_In_Mounts_As_On_The_Stand_In()
        {
            var pack = Fixture("CUO_MOUNT_T2A_PACK", "t2a");
            var bodies = AnimTableFile.Read(Path.Combine(pack, "anim", AnimTableFile.FileName)).Bodies;
            var (compared, changed) = CompareEveryItem(pack);
            _output.WriteLine($"{pack}: {compared} items, {changed.Count} offsets changed");

            foreach (var c in changed)
            {
                _output.WriteLine($"  {(MountTable.TryGet(c.Item, out _, out _) ? "listed" : "unlisted")} mount item 0x{c.Item:X4} (body {c.Body} drawn as {bodies[c.Body].AliasBody}): {c.Old} -> {c.Now}");
            }

            // Round-4 finding 3: seats are item-aware. Only the four listed items with their own -9 move; unlisted items
            // falling back to a stand-in body (0x007A -> 122, 0x00C0 -> 192, 0x0319 -> 793) already sat at 0 and stay there.
            changed.Select(c => c.Item).Should().BeEquivalentTo(new ushort[] { 0x3E9B, 0x3E9D, 0x3EB4, 0x3EBB });
            changed.Should().AllSatisfy(c => (c.Old, c.Now).Should().Be((-9, 0)));

            int Rider(ushort item) => AnimationRules.MountOffsetY(item, bodies[AnimationRules.MountBody(item, 0)].MountSeat);
            Rider(0x3EBB).Should().Be(0, "skeletal mount 793 is drawn as horse 200, so its rider sits like a horse's");
            Rider(0x3EB4).Should().Be(0, "unicorn 122 is drawn as horse 228 (item 0x3EA1, offset 0), so its rider sits like a horse's");
            Rider(0x3E9D).Should().Be(0, "ethereal unicorn 192 is drawn as horse 228");
            Rider(0x007A).Should().Be(0, "unlisted 0x007A falls back to body 122, drawn as horse 228");
            Rider(0x3EA1).Should().Be(0);
        }

        // Round-3 finding 3: turkey 95 (and Corpse.def alias 156 -> 95) has death action 8 but no 12; a corpse with the
        // second-death bit (UsedLayer) selected 12 and drew nothing. GetDeathAction falls back to the first death action.
        // Draw (ItemSpriteExtract) and pick (ItemView.CheckMouseSelection) both take the group from
        // GetDeathAction and the frames from the resolved body (see Renderers_Use_The_Shared_Rules for the call sites).
        [MountPackFact("CUO_MOUNT_AOS_PACK")]
        public void Corpse_Death_Variants_Have_Frames_For_Turkey_95_And_Alias_156()
        {
            var packDir = Fixture("CUO_MOUNT_AOS_PACK", "aos");
            ClientVersionHelper.IsClientVersionValid("7.0.103.0", out var cv);
            using var pack = PackAssets.Open(packDir);
            var anims = new AnimationsLoader(new UOFileManager(cv, Path.GetTempPath(), pack));
            anims.Load();

            foreach (ushort corpse in new ushort[] { 95, 156 })
            {
                ushort body = anims.ResolveBody(corpse, true, out _);
                body.Should().Be(95, $"corpse {corpse} draws turkey 95");
                ref readonly var e = ref anims.GetBody(body);

                foreach (bool second in new[] { false, true })
                {
                    byte group = anims.GetDeathAction(body, (AnimationFlags)e.Flags, (AnimationGroupsType)e.Type, second);

                    for (int dir = 0; dir < 8; dir++)
                    {
                        anims.TryGetDirection(body, group, dir, out _).Should().BeTrue($"corpse {corpse}, {(second ? "second" : "first")} death (group {group}), direction {dir}");
                    }
                }
            }

            // A body that has both death actions keeps its second one (no fallback where none is needed).
            ref readonly var horse = ref anims.GetBody(204);
            byte g2 = anims.GetDeathAction(204, (AnimationFlags)horse.Flags, (AnimationGroupsType)horse.Type, true);
            byte g1 = anims.GetDeathAction(204, (AnimationFlags)horse.Flags, (AnimationGroupsType)horse.Type, false);
            g2.Should().NotBe(g1);
            anims.HasAction(204, g2).Should().BeTrue();
        }

        // Round-4 finding 1: the corpse animation tick (Item.ProcessAnimation -> AnimationsLoader.TryCorpseFrame) resolves the
        // Corpse.def target before it picks the death action and the frame count, as draw and pick do. For every corpse on
        // the t2a pack whose target is not its live body and whose death frame counts differ, both death variants advance
        // to the last frame of the target's death action and stay there.
        [MountPackFact("CUO_MOUNT_T2A_PACK")]
        public void Corpse_Tick_Advances_To_The_Last_Frame_Of_The_Corpse_Target()
        {
            var packDir = Fixture("CUO_MOUNT_T2A_PACK", "t2a");
            ClientVersionHelper.IsClientVersionValid("7.0.103.0", out var cv);
            using var pack = PackAssets.Open(packDir);
            var anims = new AnimationsLoader(new UOFileManager(cv, Path.GetTempPath(), pack));
            anims.Load();

            // The audit's case: Corpse.def 43 {191} 0 with t2a's frame-less 191 stores corpse target 228 (191's stand-in),
            // while 43 lives as 9 (Body.def 43 -> 9 hue 1152). 228's death actions are longer than 9's, so a tick that
            // resolved the live body stopped short (frame 3 of 228's six). Stock 7.0.103 has no such line, so it is set in
            // the loaded table (the converter's side is BodyChoicesTests.Corpse_Targets_End_At_A_Body_With_Frames).
            anims.ResolveBody(43, false, out _).Should().Be(9);
            anims.PackBodies[43].CorpseBody = 228;
            anims.PackBodies[43].CorpseHue = 0;

            int Count(ushort body, bool second, byte dir)
            {
                ref readonly var e = ref anims.GetBody(body);
                byte action = anims.GetDeathAction(body, (AnimationFlags)(e.Flags & ~AnimBodyEntry.MountAbsoluteFlag), (AnimationGroupsType)e.Type, second);
                return anims.TryGetDirection(anims.ResolveBody(body, true, out _), action, dir + 3, out var d) ? d.FrameCount : 0;
            }

            int corpses = 0, ticked = 0;
            var perVariant = new int[2];
            var differ = new List<string>();

            for (int g = 0; g < anims.BodyCount; g++)
            {
                ushort graphic = (ushort)g;
                ushort target = anims.ResolveBody(graphic, true, out _), live = anims.ResolveBody(graphic, false, out _);

                if (target == graphic || target == live)
                {
                    continue;
                }

                corpses++;

                foreach (bool second in new[] { false, true })
                {
                    for (byte dir = 0; dir < 5; dir++)
                    {
                        int want = Count(target, second, dir), liveCount = Count(live, second, dir);

                        if (want == 0 || want == liveCount)
                        {
                            continue;
                        }

                        byte index = 0;

                        for (int tick = 0; tick < 40; tick++)
                        {
                            anims.TryCorpseFrame(graphic, second, dir, (byte)(index + 1), out byte next).Should().BeTrue();
                            index = next;
                        }

                        index.Should().Be((byte)(want - 1), $"corpse {g} (drawn as {target}, live {live}), {(second ? "second" : "first")} death, stored direction {dir}: the last frame of the target ({want}), not of the live body ({liveCount})");
                        ticked++;
                        perVariant[second ? 1 : 0]++;

                        if (differ.Count < 12)
                        {
                            differ.Add($"{g}->{target}/{live} {(second ? 2 : 1)} d{dir} {want}vs{liveCount}");
                        }
                    }
                }
            }

            _output.WriteLine($"{corpses} corpses drawn as another body than their live one; {ticked} (variant, direction) cases with different frame counts, e.g. {string.Join(", ", differ)}");
            ticked.Should().BeGreaterThan(0, "corpse 43 is drawn as 228, whose death actions differ from live body 9's");
            differ.Should().Contain(d => d.StartsWith("43->228/9 "));
            perVariant.Should().AllSatisfy(n => n.Should().BeGreaterThan(0), "both death variants are covered");
        }

        // Round-3 finding 6: every renderer consumes the rider shift and the shared death action. A source check, so it
        // fails when a draw or pick path goes back to the table offset alone or picks its own death group.
        [Fact]
        public void Renderers_Use_The_Shared_Rules()
        {
            string root = WriteSiteScanner.FindRepoRoot();
            string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));

            // Gpu: MobileAnimationSystem stores table + shift; MobileSpriteExtract draws rider and equipment with it.
            Read("src/ClassicUO.Client/Game/Ecs/MobileAnimationSystem.cs").Should()
                .MatchRegex(@"s\.MountOffsetY\s*=\s*\(short\)AnimationRules\.MountOffsetY\(eq\.MountGraphic,\s*animations\.GetMountSeat\(mountBody\)\)");
            Read("src/ClassicUO.Client/Game/Ecs/MobileSpriteExtract.cs").Should().MatchRegex(@"int mountOffset\s*=\s*s\.MountOffsetY;");

            // Pick: MobileView's pick adds the shift of the mount body (before its alias). (T9 S8a deleted MobileView.Draw; both
            // worlds draw through MobileSpriteExtract above.)
            string view = Read("src/ClassicUO.Client/Game/GameObjects/Views/MobileView.cs");
            Regex.Matches(view, @"AnimationRules\.MountOffsetY\(mount\.Graphic,\s*Client\.Game\.UO\.Animations\.GetMountSeat\(mountGraphic\)\)").Count
                .Should().Be(1, "MobileView's pick (position.Y) uses table + shift");
            view.Should().MatchRegex(@"position\.Y\s*\+=\s*AnimationRules\.MountOffsetY\(mount\.Graphic,");

            // No renderer reads the table offset on its own.
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src/ClassicUO.Client"), "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.EndsWith("AnimationRules.cs") || f.EndsWith("EcsSessionDriver.cs") || f.EndsWith("Mounts.cs"))
                {
                    continue;
                }

                string src = File.ReadAllText(f);
                src.Should().NotMatchRegex(@"AnimationRules\.MountOffsetY\(\s*[^,()]+\)", $"{f}: the table offset alone ignores the pack's shift");
                if (!f.EndsWith("Item.cs"))
                {
                    src.Should().NotContain("Mounts.TryGet(", $"{f}: the Mounts table offset is read through AnimationRules.MountOffsetY(item, shift)");
                }
            }

            // Uo.Client: the rider offset is RiderOffset (table + the original mount body's shift), taken before the alias
            // is followed, and used for the body and every equipment layer.
            string poc = Read("poc/Uo.Client/Animation.cs");
            poc.Should().MatchRegex(@"\(\(uint\)mountBody < \(uint\)bodies\.Length \? bodies\[mountBody\]\.MountSeat : default\)\.RiderOffset\(item\)");
            int offsetAt = poc.IndexOf("mountOffset = RiderOffset(_bodies, item, mountBody);", StringComparison.Ordinal);
            int aliasAt = poc.IndexOf("mountBody = _bodies[mountBody].AliasBody;", StringComparison.Ordinal);
            offsetAt.Should().BeGreaterThan(0);
            aliasAt.Should().BeGreaterThan(offsetAt, "the shift belongs to the original mount body, before its alias");
            Regex.Matches(poc, @"Add\(outFrames, ref n, [^;]*mountOffset,").Count.Should().Be(2, "the body and the equipment layers are drawn at the rider offset");

            // Corpses: the extract (both worlds since T9 S8a), the pick and the corpse tick take the group from GetDeathAction (whose
            // second-death fallback the death test above covers).
            Read("src/ClassicUO.Client/Game/Ecs/ItemSpriteExtract.cs").Should().Contain("anims.GetDeathAction(converted,");
            Regex.Matches(Read("src/ClassicUO.Client/Game/GameObjects/Views/ItemView.cs"), @"Animations\.GetDeathAction\(").Count.Should().Be(1, "ItemView pick");
            Read("src/ClassicUO.Client/Game/GameObjects/Item.cs").Should().Contain("Animations.TryCorpseFrame(id, UsedLayer, dir, frameIndex, out byte frame)", "the corpse tick (round-4 finding 1)");
        }
    }

    public sealed class MountPackFactAttribute : FactAttribute
    {
        public MountPackFactAttribute(string variable)
        {
            var dir = Environment.GetEnvironmentVariable(variable);

            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "anim", AnimTableFile.FileName)) || !File.Exists(Path.Combine(dir, TableConst.TileDataFile)))
            {
                Skip = $"{variable} is not set to a converted pack ({(string.IsNullOrEmpty(dir) ? "unset" : dir)})";
            }
        }
    }
}
