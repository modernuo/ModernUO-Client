using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Game;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Resources;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // G2 (gump system): the refreshing gumps (status, info bar, network stats, debug, buffs) format on the stack and make a
    // string only when the text changes. The text must be exactly what the old string formatting made, and an unchanged
    // refresh must allocate nothing.
    public sealed class RefreshTextTests
    {
        private static Label LabelShowing(string text)
        {
            var label = (Label)RuntimeHelpers.GetUninitializedObject(typeof(Label));
            var rendered = (RenderedText)RuntimeHelpers.GetUninitializedObject(typeof(RenderedText));
            typeof(RenderedText).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rendered, text);
            typeof(Label).GetField("_gText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(label, rendered);

            return label;
        }

        // Tier-0 code boxes the values the interpolation handlers format until the method tiers up (a JIT artifact: 0 with
        // DOTNET_TieredCompilation=0 and in NativeAOT), so allocations are measured warm, retried until tier-up (as Cp1252Tests).
        private static long Allocated(Action op)
        {
            long best = long.MaxValue;

            for (int attempt = 0; attempt < 40 && best > 0; attempt++)
            {
                for (int i = 0; i < 64; i++)
                {
                    op();
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                op();
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);

                if (best > 0)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            return best;
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(7L)]
        [InlineData(-3L)]
        [InlineData(65535L)]
        [InlineData(4294967295L)]
        [InlineData(-2147483648L)]
        public void A_Number_That_Did_Not_Change_Keeps_The_Shown_String(long value)
        {
            string shown = value.ToString();
            Label label = LabelShowing(shown);

            Allocated(() => label.SetNumber(value)).Should().Be(0);
            label.Text.Should().BeSameAs(shown, "the formatted text equals ToString's, so the label keeps its string");
        }

        [Theory]
        [InlineData(55, '/', 100)]
        [InlineData(0, '/', 0)]
        [InlineData(-5, '-', 12)]
        [InlineData(4294967295L, '/', 4294967295L)]
        public void A_Pair_That_Did_Not_Change_Keeps_The_Shown_String(long a, char separator, long b)
        {
            string shown = separator == '/' ? $"{a}/{b}" : $"{a}-{b}";
            Label label = LabelShowing(shown);

            Allocated(() => label.SetPair(a, separator, b)).Should().Be(0);
            label.Text.Should().BeSameAs(shown);
        }

        // NetStatistics.GetSizeAdaptive before G2
        private static string OldSizeAdaptive(long bytes)
        {
            decimal num = bytes;
            string arg = "B";

            if (!(num < 1024m))
            {
                arg = "KB";
                num /= 1024m;

                if (!(num < 1024m))
                {
                    arg = "MB";
                    num /= 1024m;

                    if (!(num < 1024m))
                    {
                        arg = "GB";
                        num /= 1024m;
                    }
                }
            }

            return $"{Math.Round(num, 2):0.##} {arg}";
        }

        private static readonly uint[] Sizes = { 0, 1, 999, 1023, 1024, 1536, 1537, 10_000, 1_048_575, 1_048_576, 5_000_000, 1_073_741_824, uint.MaxValue };

        [Fact]
        public void Sizes_Format_As_Before()
        {
            foreach (uint size in Sizes)
            {
                NetStatistics.GetSizeAdaptive(size).Should().Be(OldSizeAdaptive(size), $"size {size}");
            }
        }

        [Fact]
        public void The_Network_Stats_Text_Is_The_Old_Text_And_Allocates_Nothing()
        {
            var buffer = new char[128];

            foreach (uint size in Sizes)
            {
                foreach (bool minimized in new[] { false, true })
                {
                    uint ping = size % 400;
                    string old = minimized
                        ? $"Ping: {ping} ms"
                        : $"Ping: {ping} ms\n{"In:"} {OldSizeAdaptive(size),-6} {"Out:"} {OldSizeAdaptive(size / 3),-6}";

                    int n = NetworkStatsGump.FormatText(buffer, minimized, ping, size, size / 3);
                    new string(buffer, 0, n).Should().Be(old);
                }
            }

            Allocated(() => NetworkStatsGump.FormatText(buffer, false, 42, 123_456, 789)).Should().Be(0);
        }

        [Theory]
        [InlineData(1000u, true, 1.25f)]
        [InlineData(60u, false, 1f)]
        [InlineData(0u, true, 0.5f)]
        public void The_Collapsed_Debug_Text_Is_The_Old_Text(uint rate, bool showZoom, float zoom)
        {
            string old = showZoom ? string.Format("FPS: {0}\nZoom: {1:0.00}", rate, zoom) : string.Format("FPS: {0}", rate);
            var buffer = new char[64];
            int n = DebugGump.FormatSmall(buffer, rate, showZoom, zoom);
            new string(buffer, 0, n).Should().Be(old);

            Allocated(() => DebugGump.FormatSmall(buffer, rate, showZoom, zoom)).Should().Be(0);
        }

        [Theory]
        [InlineData(3, 2, 5)]
        [InlineData(0, 12, 5)]
        [InlineData(0, 0, 7)]
        [InlineData(0, 0, 0)]
        [InlineData(26, 0, 1)]
        public void The_Buff_Countdown_Is_The_Old_Text(int hours, int minutes, int seconds)
        {
            var span = new TimeSpan(hours, minutes, seconds);
            string old = span.Hours > 0
                ? string.Format(ResGumps.Span0Hours, span.Hours)
                : span.Minutes > 0 ? $"{span.Minutes}:{span.Seconds:00}" : $"{span.Seconds:00}s";

            var buffer = new char[32];
            int n = BuffGump.FormatTimeLeft(buffer, span);
            new string(buffer, 0, n).Should().Be(old);

            Allocated(() => BuffGump.FormatTimeLeft(buffer, span)).Should().Be(0);
        }
    }
}
