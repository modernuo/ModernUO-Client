using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Network.Packets
{
    // gump_resend_patch (Q1 (b), needs gump_layout_v2): a resend of the open gump's layout keeps the controls and patches the
    // text lines that changed; off (and on a layout change, or an html text from/to empty) the gump is rebuilt as before.
    // Both switch states; every patch is compared with an old-path rebuild (the CUO_GUMP_RESEND_CHECK checker, forced on).
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class GumpResendPatchTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint GUMP_ID = 0x0765_4322;

        private readonly GameSeam _seam = new();
        private readonly ITestOutputHelper _out;

        public GumpResendPatchTests(ITestOutputHelper output)
        {
            _out = output;
#if CUO_DEVTOOLS
            Client.Game.UO.GumpResendCheck.ForceForTests = true;
#endif
        }

        public void Dispose()
        {
            GumpLayoutSwitch.OverrideForTests = null;
            GumpResendPatchSwitch.OverrideForTests = null;
#if CUO_DEVTOOLS
            Client.Game.UO.GumpResendCheck.ForceForTests = false;
#endif
            UIManager.RemovePosition(GUMP_ID);
            _seam.Dispose();
        }

        private static World InGame()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            world.GetOrCreateMobile(MOB);

            return world;
        }

        private World Setup(bool patch)
        {
            World world = InGame();
            ServerGumpFixture.MakeResident(_seam);
            GumpLayoutSwitch.OverrideForTests = true;
            GumpResendPatchSwitch.OverrideForTests = patch;

            return world;
        }

        private static Gump Open(World world, string layout, string[] lines) =>
            GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines);

        private static List<Control> Live(Gump g) => g.Children.Where(c => !c.IsDisposed).ToList();

        private static void NoCheckMismatch()
        {
#if CUO_DEVTOOLS
            GumpResendCheck check = Client.Game.UO.GumpResendCheck;
            check.Mismatches.Should().Be(0, check.LastMismatch);
#endif
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Changed_Line_Reaches_Its_Label(bool patch)
        {
            World world = Setup(patch);
            var (layout, lines) = ServerGumpFixture.Build();
            Gump g = Open(world, layout, lines);
            List<Control> before = Live(g);

            string[] next = (string[])lines.Clone();
            next[3] = "Line three, refreshed";
            Gump again = Open(world, layout, next);

            again.Should().BeSameAs(g);
            List<Control> after = Live(again);
            after.OfType<Label>().Select(l => l.Text).Should().Contain("Line three, refreshed");
            after.OfType<Label>().Select(l => l.Text).Should().NotContain(lines[3]);

            if (patch)
            {
                after.Should().Equal(before, "the controls are kept");
            }
            else
            {
                after.Intersect(before).Should().BeEmpty("the gump is rebuilt");
            }

            NoCheckMismatch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Typed_Text_And_Checkbox_State_Survive_Only_The_Patch(bool patch)
        {
            World world = Setup(patch);
            var (layout, lines) = ServerGumpFixture.Build();
            Gump g = Open(world, layout, lines);
            StbTextBox entry = Live(g).OfType<StbTextBox>().Single();
            Checkbox box = Live(g).OfType<Checkbox>().First(c => c is not RadioButton);
            string server = entry.Text;
            entry.SetText("typed by the user");
            box.IsChecked = !box.IsChecked;
            bool userChecked = box.IsChecked;

            string[] next = (string[])lines.Clone();
            next[1] = "first line, refreshed";
            Gump again = Open(world, layout, next);

            StbTextBox entry2 = Live(again).OfType<StbTextBox>().Single();
            Checkbox box2 = Live(again).OfType<Checkbox>().First(c => c is not RadioButton);

            if (patch)
            {
                entry2.Text.Should().Be("typed by the user");
                box2.IsChecked.Should().Be(userChecked);
            }
            else
            {
                entry2.Text.Should().Be(server);
                box2.IsChecked.Should().Be(!userChecked);
            }

            NoCheckMismatch();
        }

        [Fact]
        public void A_Text_Entry_Whose_Line_Changed_Takes_The_Server_Value()
        {
            World world = Setup(true);
            var (layout, lines) = ServerGumpFixture.Build();
            Gump g = Open(world, layout, lines);
            StbTextBox entry = Live(g).OfType<StbTextBox>().Single();
            entry.SetText("typed by the user");

            string[] next = (string[])lines.Clone();
            next[18] = "server value";
            Open(world, layout, next).Should().BeSameAs(g);

            Live(g).OfType<StbTextBox>().Single().Should().BeSameAs(entry);
            entry.Text.Should().Be("server value");
            NoCheckMismatch();
        }

        [Fact]
        public void Html_And_Cropped_Text_Lines_Are_Patched()
        {
            World world = Setup(true);
            var (layout, lines) = ServerGumpFixture.Build();
            Gump g = Open(world, layout, lines);
            List<Control> before = Live(g);

            string[] next = (string[])lines.Clone();
            next[16] = "<b>new html</b> text";
            next[19] = "cropped text that changed and is long";
            Open(world, layout, next).Should().BeSameAs(g);

            Live(g).Should().Equal(before);
            Live(g).OfType<HtmlControl>().Select(h => h.Text).Should().Contain("<b>new html</b> text");
            NoCheckMismatch();
        }

        [Theory]
        [InlineData(16, "")]
        [InlineData(-1, null)]
        public void An_Unpatchable_Change_Rebuilds(int line, string text)
        {
            World world = Setup(true);
            var (layout, lines) = ServerGumpFixture.Build();
            Gump g = Open(world, layout, lines);
            List<Control> before = Live(g);

            string[] next = (string[])lines.Clone();
            string nextLayout = layout;

            if (line >= 0)
            {
                next[line] = text; // an html text going to empty
            }
            else
            {
                nextLayout = layout.Replace("{ text 20 40 1152 1 }", "{ text 22 40 1152 1 }"); // another layout
            }

            Gump again = Open(world, nextLayout, next);

            again.Should().BeSameAs(g);
            Live(again).Intersect(before).Should().BeEmpty();
            NoCheckMismatch();
        }

        [Fact]
        public void Random_Line_Changes_Match_A_Rebuild()
        {
            World world = Setup(true);
            var (layout, lines) = ServerGumpFixture.Build();
            Open(world, layout, lines);
            var rnd = new Random(7);

            for (int round = 0; round < 40; round++)
            {
                string[] next = (string[])lines.Clone();

                for (int k = rnd.Next(1, 6); k > 0; k--)
                {
                    next[rnd.Next(1, next.Length)] = "changed " + rnd.Next(1000);
                }

                Open(world, layout, next);
                lines = next;
            }

#if CUO_DEVTOOLS
            Client.Game.UO.GumpResendCheck.Checks.Should().BeGreaterThan(30);
#endif
            NoCheckMismatch();
        }

#if CUO_DEVTOOLS
        [Fact]
        public void The_Checker_Reports_A_Patch_That_Missed_A_Line()
        {
            World world = Setup(true);
            var (layout, lines) = ServerGumpFixture.Build();
            Gump g = Open(world, layout, lines);
            string[] next = (string[])lines.Clone();
            next[2] = "second line, refreshed";
            Open(world, layout, next);

            // a patch that left the old text behind
            Live(g).OfType<Label>().Single(l => l.Text == "second line, refreshed").Text = lines[2];
            GumpResendCheck check = Client.Game.UO.GumpResendCheck;
            check.Compare(world, g, g.ResendPatch, next);

            check.Mismatches.Should().Be(1);
            check.LastMismatch.Should().Contain(lines[2]);
        }
#endif

        [Theory]
        [InlineData(false, 32_000)]
        [InlineData(true, 3_000)]
        public void A_Resend_With_Unchanged_Lines_Allocates_Little_With_The_Patch(bool patch, long bound)
        {
            World world = Setup(patch);
#if CUO_DEVTOOLS
            Client.Game.UO.GumpResendCheck.ForceForTests = false;
#endif
            var (layout, lines) = ServerGumpFixture.Build();
            Gump g = Open(world, layout, lines);

            for (int i = 0; i < 2; i++)
            {
                Open(world, layout, lines);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            Gump again = Open(world, layout, lines);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _out.WriteLine($"server gump 50/20 resend, same lines, gump_resend_patch={patch} (v2): {bytes} B");

            again.Should().BeSameAs(g);
            bytes.Should().BeLessThan(bound);
        }
    }
}
