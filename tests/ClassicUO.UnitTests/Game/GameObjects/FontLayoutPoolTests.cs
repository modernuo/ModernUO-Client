using System;
using System.Collections.Generic;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.UI.Controls;
using ClassicUO.UnitTests.Network;
using ClassicUO.UnitTests.Network.Packets;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.GameObjects
{
    // font_layout_pool (gump-system G5): a RenderedText gives its layout back to the font loader's node pool when its text
    // changes or it is destroyed. Both switch states: the layouts of every kind of text (wrapped, multi-line, cropped,
    // html with links, ASCII) are the same as fresh ones after the pool has been churned; only the on state refills the
    // pool; the pool stays bounded; CUO_FONT_LAYOUT_CHECK finds nothing in a churn and reports a layout reused while held.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class FontLayoutPoolTests : IDisposable
    {
        private readonly GameSeam _seam = new();

        public void Dispose()
        {
            FontLayoutPoolSwitch.OverrideForTests = null;
#if CUO_DEVTOOLS
            Client.Game.UO.FontLayoutCheck.ForceForTests = false;
#endif
            _seam.Dispose();
        }

        private static FontsLoader Fonts => Client.Game.UO.FileManager.Fonts;

        private sealed record Spec(string Text, bool Unicode, FontStyle Style, int MaxWidth, bool Html, TEXT_ALIGN_TYPE Align = TEXT_ALIGN_TYPE.TS_LEFT);

        private static readonly Spec[] Specs =
        {
            new("Line number 1 of the list", true, FontStyle.BlackBorder, 0, false),
            new("a much longer line that wraps across several lines because the width is small", true, FontStyle.None, 90, false),
            new("two\nlines\n\nand an empty one", true, FontStyle.None, 200, false),
            new("cropped text that is far too long for its box", true, FontStyle.Cropped, 60, false),
            new("centred words over two lines", true, FontStyle.None, 80, false, TEXT_ALIGN_TYPE.TS_CENTER),
            new("<basefont color=#FF0000>red</basefont> and <a href=\"https://example.org\">a link</a> then more words", true, FontStyle.None, 120, true),
            new("ASCII label text", false, FontStyle.None, 0, false),
            new("ASCII wrapped over a narrow width box", false, FontStyle.None, 70, false),
        };

        private static RenderedText Make(Spec s) =>
            RenderedText.Create(s.Text, 0x0481, 1, s.Unicode, s.Style, s.Align, s.MaxWidth, isHTML: s.Html);

        private static string Describe(MultilinesFontInfo info)
        {
            var sb = new StringBuilder();

            for (MultilinesFontInfo p = info; p != null; p = p.Next)
            {
                sb.Append($"[{p.Align} {p.CharCount} {p.CharStart} {p.IndentionOffset} {p.MaxHeight} {p.Width}");

                if (p.WebLinks != null)
                {
                    sb.Append(" links=").Append(string.Join(",", p.WebLinks));
                }

                sb.Append(' ');

                foreach (MultilinesFontData d in p.Data)
                {
                    sb.Append($"{d.Item}:{d.Color:X}:{d.Flags}:{d.Font}:{d.LinkID};");
                }

                sb.Append(']');
            }

            return sb.ToString();
        }

        private static void Churn(int seed)
        {
            var rnd = new Random(seed);
            var live = new List<RenderedText>();

            for (int i = 0; i < 300; i++)
            {
                switch (rnd.Next(3))
                {
                    case 0:
                        live.Add(Make(Specs[rnd.Next(Specs.Length)]));

                        break;

                    case 1 when live.Count != 0:
                        int k = rnd.Next(live.Count);
                        live[k].Destroy();
                        live.RemoveAt(k);

                        break;

                    case 2 when live.Count != 0:
                        live[rnd.Next(live.Count)].Text = Specs[rnd.Next(Specs.Length)].Text + " " + i;

                        break;
                }
            }

            foreach (RenderedText r in live)
            {
                r.Destroy();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Layouts_After_A_Churn_Are_The_Same_As_Fresh_Ones(bool pool)
        {
            FontLayoutPoolSwitch.OverrideForTests = false;
            var expected = new string[Specs.Length];

            for (int i = 0; i < Specs.Length; i++)
            {
                RenderedText fresh = Make(Specs[i]);
                expected[i] = Describe(fresh.GetInfo());
                expected[i].Should().NotBeEmpty();
            }

            FontLayoutPoolSwitch.OverrideForTests = pool;

            for (int round = 0; round < 5; round++)
            {
                Churn(round);

                for (int i = 0; i < Specs.Length; i++)
                {
                    RenderedText r = Make(Specs[i]);
                    Describe(r.GetInfo()).Should().Be(expected[i], $"spec {i}, round {round}, font_layout_pool={pool}");
                    r.Destroy();
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Only_The_Pool_Takes_Kept_Layouts_Back(bool pool)
        {
            FontLayoutPoolSwitch.OverrideForTests = pool;
            Make(Specs[0]).Destroy(); // fills the transient tier once
            int before = Fonts.PooledLayoutNodes;

            var labels = new List<RenderedText>();

            for (int i = 0; i < 10; i++)
            {
                labels.Add(Make(Specs[1]));
            }

            int taken = before - Fonts.PooledLayoutNodes;

            foreach (RenderedText r in labels)
            {
                r.Destroy();
            }

            int after = Fonts.PooledLayoutNodes;

            if (pool)
            {
                after.Should().BeGreaterThanOrEqualTo(before + 10, "each wrapped label returns its nodes");
            }
            else
            {
                after.Should().Be(before - taken, "kept layouts stay with their (destroyed) owners");
            }
        }

        [Fact]
        public void A_Text_Change_Returns_The_Old_Layout_With_The_Pool()
        {
            FontLayoutPoolSwitch.OverrideForTests = true;
            RenderedText r = Make(Specs[1]);
            MultilinesFontInfo old = r.GetInfo();
            int nodes = 0;

            for (MultilinesFontInfo p = old; p != null; p = p.Next)
            {
                nodes++;
            }

            nodes.Should().BeGreaterThan(1);
            int before = Fonts.PooledLayoutNodes;
            r.Text = "x";

            // the new one-line layout took one node back out
            Fonts.PooledLayoutNodes.Should().Be(before + nodes - 1);
            Describe(r.GetInfo()).Should().StartWith("[TS_LEFT 1 0 ");
            r.Destroy();
            r.GetInfo().Should().BeNull("a destroyed text keeps no pooled nodes");
        }

        [Fact]
        public void The_Pool_Is_Bounded()
        {
            FontLayoutPoolSwitch.OverrideForTests = true;
            var labels = new List<RenderedText>();

            for (int i = 0; i < 400; i++)
            {
                labels.Add(Make(Specs[1]));
            }

            foreach (RenderedText r in labels)
            {
                r.Destroy();
            }

            Fonts.PooledLayoutNodes.Should().BeLessThanOrEqualTo(256);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Label_Reopened_Allocates_No_Layout_With_The_Pool(bool pool)
        {
            FontLayoutPoolSwitch.OverrideForTests = pool;

            for (int i = 0; i < 3; i++)
            {
                new Label(Specs[0].Text, true, 0x0481, style: FontStyle.BlackBorder).Dispose();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            new Label(Specs[0].Text, true, 0x0481, style: FontStyle.BlackBorder).Dispose();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            if (pool)
            {
                bytes.Should().BeLessThan(500, "only the control itself");
            }
            else
            {
                bytes.Should().BeGreaterThan(800, "a new layout per label");
            }
        }

#if CUO_DEVTOOLS
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Checker_Finds_Nothing_In_A_Churn(bool pool)
        {
            FontLayoutCheck check = Client.Game.UO.FontLayoutCheck;
            check.ForceForTests = true;
            FontLayoutPoolSwitch.OverrideForTests = pool;

            for (int round = 0; round < 3; round++)
            {
                Churn(100 + round);
            }

            check.Checks.Should().BeGreaterThan(100);
            check.Mismatches.Should().Be(0, check.LastMismatch);
        }

        [Fact]
        public void The_Checker_Reports_A_Layout_Reused_While_Still_Held()
        {
            FontLayoutCheck check = Client.Game.UO.FontLayoutCheck;
            check.ForceForTests = true;
            FontLayoutPoolSwitch.OverrideForTests = true;

            RenderedText a = Make(Specs[0]);
            Fonts.ReleaseLayout(a.GetInfo()); // the bug the checker is for: nodes back in the pool while a still draws them
            RenderedText b = Make(Specs[2]);
            a.Text = "replaced";

            check.Mismatches.Should().Be(1);
            check.LastMismatch.Should().NotBeNullOrEmpty();
            b.Destroy();
        }
#endif
    }
}
