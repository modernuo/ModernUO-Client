using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // gump_layout_v2 (G3): the one-pass parser against the old two tokenizers, the kind builder
    // against the old loop (control-tree signatures), both switch states.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class GumpLayoutV2Tests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint GUMP_ID = 0x0123_4567;

        private readonly GameSeam _seam = new(art: true);

        public void Dispose()
        {
            GumpLayoutSwitch.OverrideForTests = null;
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

        // layouts that exercise the tokenizer rules: bare words, unterminated braces, '@' quoting (the closing '@' opens the
        // next quoted token), commas, tabs and CR, several lines, empty entries, the terminator, mixed case keywords
        public static readonly string[] Layouts =
        {
            "",
            "{ }",
            "{}{}{ page 0 }",
            "{ page 0 }{ text 10 20 0 0 }",
            "  { page 0 }   {text 1 2 3 0}",
            "{ PAGE 0 }{ Text 1 2 3 0 }{ BUTTON 1 2 4005 4007 1 0 1 }",
            "bare words { page 0 } outside",
            "{ page 0 }{ text 1 2 3 0",
            "{ page 0 }\n{ text 1 2 3 0 }\r\n{ button 1 2 4005 4007 1 0 2 }\n",
            "{ text 1 2 3\n0 }",
            "{ tooltip 1011036 @arg one\targ two@ }",
            "{ tooltip 1011036 @a@b@c@ }",
            "{ xmfhtmltok 1 2 3 4 0 0 32767 1060658 @Weight\t5@ }",
            "{ xmfhtmltok 1 2 3 4 0 0 32767 #1060658 @@ }",
            "{ xmfhtmltok 1 2 3 4 1 2 32767 1060658 }",
            "{ button,1,2,4005,4007,1,0,3 }",
            "{ text\t1 2 3 0 }",
            "{ ,, }{ page 0 }",
            "{ page 0 }\0",
            "{ page 0 }{ \0 }{ text 1 2 3 0 }",
            "{ gumppic 1 2 5100 hue=33 }{ gumppic 1 2 5100 33 }{ gumppic 1 2 5100 hue=0 class=VirtueGumpItem }",
            "{ resizepic 0 0 9200 300 200 }{ gumppictiled 1 2 3 4 2624 }{ checkertrans 0 0 50 50 }",
            "{ htmlgump 1 2 100 40 0 1 2 }{ xmfhtmlgump 1 2 100 40 #1015000 0 0 }{ xmfhtmlgumpcolor 1 2 100 40 1015000 1 1 32767 }",
            "{ textentry 1 2 100 20 0 1 0 }{ textentrylimited 1 2 100 20 0 2 1 10 }{ TextEntryLimited 1 2 100 20 0 3 1 10 }",
            "{ checkbox 1 2 210 211 1 4 }{ group 1 }{ radio 1 2 208 209 0 5 }{ endgroup }{ radio 1 2 208 209 1 6 }",
            "{ tilepic 1 2 3821 }{ tilepichue 1 2 3821 33 }{ tilepicasgumppic 1 2 5100 }",
            "{ noclose }{ nodispose }{ nomove }{ noresize }{ mastergump 0x1234 }{ togglelimitgumpscale }",
            "{ picinpic 1 2 5100 0 0 10 10 }{ picinpichued 1 2 5100 0 0 10 10 33 }{ picinpicphued 1 2 5100 0 0 10 10 33 }",
            "{ gumppichued 1 2 5100 33 }{ gumppicphued 1 2 5100 33 }{ croppedtext 1 2 50 20 37 0 }",
            "{ button 1 2 4005 4007 1 0 9 }{ itemproperty 1073741825 }{ tooltip 1011036 }{ tooltip 1011037 }",
            "{ buttontileart 1 2 4005 4007 1 0 9 3821 0 5 5 }",
            "{ unknownthing 1 2 3 }{ page 0 }",
        };

        public static IEnumerable<object[]> LayoutCases() => Layouts.Select((l, i) => new object[] { i });

        [Theory]
        [MemberData(nameof(LayoutCases))]
        public void Parse_Equals_The_Old_Tokenizers(int index)
        {
            string layout = Layouts[index];
            List<string>[] old = GumpFactory.TokenizeForCheck(layout);
            GumpLayout parsed = GumpLayout.Parse(layout);

            parsed.Entries.Should().HaveCount(old.Length);

            for (int i = 0; i < old.Length; i++)
            {
                GumpArgs a = parsed.Args(parsed.Entries[i]);
                var tokens = new List<string>();

                for (int t = 0; t < a.Count; t++)
                {
                    tokens.Add(a[t].ToString());
                }

                tokens.Should().Equal(old[i], $"entry {i} of layout {index}");
            }
        }

        [Fact]
        public void Parse_Equals_The_Old_Tokenizers_On_Random_Layouts()
        {
            var rnd = new Random(1234);
            string alphabet = "{} @,\t\r\n\0abcpageTEXT0123456789#=";

            for (int n = 0; n < 2000; n++)
            {
                var sb = new StringBuilder();
                int len = rnd.Next(0, 80);

                for (int i = 0; i < len; i++)
                {
                    sb.Append(alphabet[rnd.Next(alphabet.Length)]);
                }

                string layout = sb.ToString();
                List<string>[] old = GumpFactory.TokenizeForCheck(layout);
                GumpLayout parsed = GumpLayout.Parse(layout);

                parsed.Entries.Length.Should().Be(old.Length, layout);

                for (int i = 0; i < old.Length; i++)
                {
                    GumpArgs a = parsed.Args(parsed.Entries[i]);
                    a.Count.Should().Be(old[i].Count, layout);

                    for (int t = 0; t < a.Count; t++)
                    {
                        a[t].ToString().Should().Be(old[i][t], layout);
                    }
                }
            }
        }

        [Fact]
        public void Every_Keyword_Has_Its_Kind_Ignoring_Case()
        {
            var keywords = new Dictionary<string, GumpEntryKind>
            {
                ["button"] = GumpEntryKind.Button, ["buttontileart"] = GumpEntryKind.ButtonTileArt,
                ["checkertrans"] = GumpEntryKind.CheckerTrans, ["croppedtext"] = GumpEntryKind.CroppedText,
                ["tilepicasgumppic"] = GumpEntryKind.TilePicAsGumpPic, ["gumppic"] = GumpEntryKind.GumpPic,
                ["gumppictiled"] = GumpEntryKind.GumpPicTiled, ["htmlgump"] = GumpEntryKind.HtmlGump,
                ["xmfhtmlgump"] = GumpEntryKind.XmfHtmlGump, ["xmfhtmlgumpcolor"] = GumpEntryKind.XmfHtmlGumpColor,
                ["xmfhtmltok"] = GumpEntryKind.XmfHtmlTok, ["page"] = GumpEntryKind.Page, ["resizepic"] = GumpEntryKind.ResizePic,
                ["text"] = GumpEntryKind.Text, ["textentrylimited"] = GumpEntryKind.TextEntryLimited,
                ["textentry"] = GumpEntryKind.TextEntry, ["tilepichue"] = GumpEntryKind.TilePicHue, ["tilepic"] = GumpEntryKind.TilePic,
                ["noclose"] = GumpEntryKind.NoClose, ["nodispose"] = GumpEntryKind.NoDispose, ["nomove"] = GumpEntryKind.NoMove,
                ["group"] = GumpEntryKind.Group, ["endgroup"] = GumpEntryKind.EndGroup, ["radio"] = GumpEntryKind.Radio,
                ["checkbox"] = GumpEntryKind.Checkbox, ["tooltip"] = GumpEntryKind.Tooltip, ["itemproperty"] = GumpEntryKind.ItemProperty,
                ["noresize"] = GumpEntryKind.NoResize, ["mastergump"] = GumpEntryKind.MasterGump,
                ["picinpichued"] = GumpEntryKind.PicInPicHued, ["picinpicphued"] = GumpEntryKind.PicInPicPHued,
                ["picinpic"] = GumpEntryKind.PicInPic, ["\0"] = GumpEntryKind.Terminator, ["gumppichued"] = GumpEntryKind.GumpPicHued,
                ["gumppicphued"] = GumpEntryKind.GumpPicPHued, ["togglelimitgumpscale"] = GumpEntryKind.ToggleLimitGumpScale
            };

            foreach (var (word, kind) in keywords)
            {
                GumpLayout.KindOf(word).Should().Be(kind, word);
                GumpLayout.KindOf(word.ToUpperInvariant()).Should().Be(kind, word);
            }

            GumpLayout.KindOf("buttons").Should().Be(GumpEntryKind.Unknown);
            GumpLayout.KindOf("x").Should().Be(GumpEntryKind.Unknown);
        }

        [Fact]
        public void Cliloc_And_Argument_Helpers_Match_The_Old_String_Code()
        {
            GumpFactory.ParseCliloc("1060658").Should().Be(1060658);
            GumpFactory.ParseCliloc("#1060658").Should().Be(int.Parse("#1060658".Replace("#", "")));
            GumpFactory.ParseCliloc("10#60#658").Should().Be(1060658);

            var parts = new List<string> { "xmfhtmltok", "1", "2", "3", "4", "0", "0", "32767", "1060658", "Weight\t5", " more" };
            var sb = new StringBuilder();

            for (int i = 9; i < parts.Count; i++)
            {
                sb.Append('\t').Append(parts[i]);
            }

            GumpFactory.JoinArgs(parts, 9, true).Should().Be(sb.ToString().Trim('@').Replace('@', '\t'));
            GumpFactory.JoinArgs(parts.Take(9).ToList(), 9, true).Should().Be(string.Empty);

            var tip = new List<string> { "tooltip", "1011036", "a", "b c", "d" };
            GumpFactory.JoinArgs(tip, 2, false).Should().Be("a" + '\t' + "b c" + '\t' + "d");
        }

        // ---- the built tree: v2 against the old loop

        private void MakeResident()
        {
            foreach (ushort g in new ushort[] { 2624, 4005, 4007, 5100, 208, 209, 210, 211 })
            {
                _seam.Resident(g, 30, 22);
            }

            for (ushort i = 0; i < 9; i++)
            {
                _seam.Resident((ushort)(9200 + i), 16, 16);
            }

            _seam.ResidentArt(3821, 20, 18);
        }

        private static List<string> Signature(Gump gump)
        {
            var list = new List<string>
            {
                $"gump move={gump.CanMove} rclose={gump.CanCloseWithRightClick} esc={gump.CanCloseWithEsc} master={gump.MasterGumpSerial}"
            };

            foreach (Control c in gump.Children.Where(c => !c.IsDisposed))
            {
                string s = $"{c.GetType().Name} {c.Bounds} page{c.Page} a{c.Alpha} in{c.AcceptMouseInput} pr{c.Priority} s{c.LocalSerial} tip'{c.Tooltip}'";

                s += c switch
                {
                    Button b => $" id{b.ButtonID} act{b.ButtonAction} to{b.ToPage} g{b.ButtonGraphicNormal}/{b.ButtonGraphicPressed}",
                    Label l => $" '{l.Text}' hue{l.Hue}",
                    HtmlControl h => $" '{h.Text}' bg{h.HasBackground} sb{h.HasScrollbar}{h.UseFlagScrollbar}",
                    RadioButton r => $" group{r.GroupIndex} on{r.IsChecked}",
                    Checkbox k => $" on{k.IsChecked}",
                    StbTextBox t => $" '{t.Text}' hue{t.Hue}",
                    GumpPic g => $" g{g.Graphic} hue{g.Hue} partial{g.IsPartialHue}",
                    GumpPicInPic g => $" g{g.Graphic} hue{g.Hue} partial{g.IsPartialHue}",
                    GumpPicTiled g => $" g{g.Graphic}",
                    ResizePic r => $" g{r.Graphic}",
                    StaticPic p => $" g{p.Graphic} hue{p.Hue}",
                    _ => ""
                };

                list.Add(s);
            }

            return list;
        }

        private List<string> BuiltWith(bool v2, World world, string layout, string[] lines)
        {
            GumpLayoutSwitch.OverrideForTests = v2;
            Gump gump = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines);

            if (gump == null)
            {
                return null;
            }

            List<string> sig = Signature(gump);
            gump.Dispose();
            UIManager.Gumps.Clear();
            UIManager.RemovePosition(GUMP_ID);

            return sig;
        }

        [Theory]
        [MemberData(nameof(LayoutCases))]
        public void Build_Equals_The_Old_Loop(int index)
        {
            World world = InGame();
            MakeResident();
            string[] lines = { "zero", "one", "two", "three", "four", "five", "six" };

            List<string> old = BuiltWith(false, world, Layouts[index], lines);
            List<string> v2 = BuiltWith(true, world, Layouts[index], lines);

            v2.Should().Equal(old);
        }

        [Fact]
        public void Build_Equals_The_Old_Loop_On_The_50_Entry_Fixture()
        {
            World world = InGame();
            MakeResident();
            var (layout, lines) = ServerGumpFixture.Build();

            List<string> old = BuiltWith(false, world, layout, lines);
            List<string> v2 = BuiltWith(true, world, layout, lines);

            old.Should().HaveCountGreaterThan(40);
            v2.Should().Equal(old);
        }

        [Fact]
        public void A_Malformed_Entry_Throws_In_Both_States()
        {
            World world = InGame();

            foreach (bool v2 in new[] { false, true })
            {
                GumpLayoutSwitch.OverrideForTests = v2;
                Action open = () => GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 0, 0, "{ button 1 }", Array.Empty<string>());
                open.Should().Throw<Exception>($"gump_layout_v2={v2}: a short entry throws as before (the handler's caller disconnects)");
                UIManager.Gumps.Clear();
            }
        }

        // ---- the round-5 server-gump tests, unchanged, with gump_layout_v2 on

        [Fact]
        public void Round5_Server_Gump_Tests_Pass_With_V2()
        {
            foreach (object[] m in PacketPipe.Modes().Take(6))
            {
                var mode = (SpanBufferMode)m[0];
                var read = (PipeRead)m[1];
                bool compressed = (bool)m[2];

                Run(t => t.ServerGump_0xB0_Builds_The_Layout(mode, read, compressed));
                Run(t => t.ServerGump_0xDD_Compressed_Builds_The_Same_Layout(mode, read, compressed));
                Run(t => t.ServerGump_0xB0_Same_Sender_And_Id_Is_Rebuilt_In_Place(mode, read, compressed));
                Run(t => t.ServerGump_Missing_Pictures_Empty_Layout_And_Bad_Streams(mode, read, compressed));
            }
        }

        private void Run(Action<IncomingGumpTests> test)
        {
            // the round-5 class makes its own seam: this one's is set aside while it runs
            _seam.Dispose();

            using var t = new IncomingGumpTests();
            GumpLayoutSwitch.OverrideForTests = true;
            test(t);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Resend_Rebuilds_The_Open_Gump_In_Both_States(bool v2)
        {
            World world = InGame();
            MakeResident();
            GumpLayoutSwitch.OverrideForTests = v2;
            var (layout, lines) = ServerGumpFixture.Build();

            Gump first = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines);
            List<string> before = Signature(first);
            Gump again = GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 100, 100, layout, lines);

            again.Should().BeSameAs(first);
            Signature(again).Should().Equal(before);
            UIManager.Gumps.Count(g => !g.IsDisposed).Should().Be(1);
        }

#if CUO_DEVTOOLS
        // ---- CUO_GUMP_LAYOUT_CHECK

        [Fact]
        public void Checker_Finds_No_Mismatch_On_The_Fixture_And_Reports_A_Real_One()
        {
            World world = InGame();
            MakeResident();
            GumpLayoutSwitch.OverrideForTests = true;
            GumpLayoutCheck.ForceForTests = true;

            try
            {
                var (layout, lines) = ServerGumpFixture.Build();
                long mismatches = GumpLayoutCheck.Mismatches;
                long checks = GumpLayoutCheck.Checks;

                GumpFactory.OpenServerGump(world, MOB, GUMP_ID, 0, 0, layout, lines);

                GumpLayoutCheck.Checks.Should().Be(checks + 2, "parse and build");
                GumpLayoutCheck.Mismatches.Should().Be(mismatches);

                GumpLayoutCheck.CompareParse(GUMP_ID, GumpLayout.Parse("{ text 1 2 3 0 }"), "{ text 1 2 3 1 }");
                GumpLayoutCheck.Mismatches.Should().Be(mismatches + 1);
            }
            finally
            {
                GumpLayoutCheck.ForceForTests = false;
            }
        }
#endif
    }
}
