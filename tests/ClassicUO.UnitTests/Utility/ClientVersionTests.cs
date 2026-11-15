using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Utility
{
    // Letter versions as ModernUO's ClientVersion reads them (PR #2735, 61d0651af): 'a' = 1, letters only before 5.0.7.
    public class ClientVersionTests
    {
        [Theory]
        [InlineData("4.0.11c", 4, 0, 11, 3)]
        [InlineData("4.0.11", 4, 0, 11, 0)]
        [InlineData("5.0.0a", 5, 0, 0, 1)]
        [InlineData("5.0.6e", 5, 0, 6, 5)]
        [InlineData("5.0.7.0", 5, 0, 7, 0)]
        [InlineData("7.0.103.0", 7, 0, 103, 0)]
        public void Parses_And_Round_Trips(string text, int major, int minor, int build, int patch)
        {
            ClientVersionHelper.IsClientVersionValid(text, out var v).Should().BeTrue();
            ((int)v).Should().Be((major << 24) | (minor << 16) | (build << 8) | patch);
            ClientVersionHelper.ToVersionString(v).Should().Be(text);
            ClientVersionHelper.IsClientVersionValid(ClientVersionHelper.ToVersionString(v), out var back).Should().BeTrue();
            back.Should().Be(v);
        }

        [Fact]
        public void Lettered_Constants_Equal_What_Their_Version_Text_Parses_To()
        {
            void Same(string text, ClientVersion constant)
            {
                ClientVersionHelper.IsClientVersionValid(text, out var v).Should().BeTrue(text);
                v.Should().Be(constant, text);
                ClientVersionHelper.ToVersionString(constant).Should().Be(text);
            }

            Same("2.0.0x", ClientVersion.CV_200X);
            Same("2.0.4c", ClientVersion.CV_204C);
            Same("3.0.5d", ClientVersion.CV_305D);
            Same("3.0.6e", ClientVersion.CV_306E);
            Same("3.0.8d", ClientVersion.CV_308D);
            Same("3.0.8j", ClientVersion.CV_308J);
            Same("3.0.8z", ClientVersion.CV_308Z);
            Same("4.0.0b", ClientVersion.CV_400B);
            Same("4.0.5a", ClientVersion.CV_405A);
            Same("4.0.11c", ClientVersion.CV_4011C);
            Same("4.0.11d", ClientVersion.CV_4011D);
            Same("5.0.0a", ClientVersion.CV_500A);
        }

        [Fact]
        public void Comparisons_Select_The_Clients_The_Constants_Name()
        {
            static ClientVersion V(string t) => ClientVersionHelper.IsClientVersionValid(t, out var v) ? v : 0;

            (V("4.0.11c") < ClientVersion.CV_4011D).Should().BeTrue("4.0.11c has no elves");
            (V("4.0.11d") >= ClientVersion.CV_4011D).Should().BeTrue("4.0.11d adds elves");
            (V("4.0.11c") >= ClientVersion.CV_4011C).Should().BeTrue();
            (V("4.0.11b") < ClientVersion.CV_4011C).Should().BeTrue();
            (V("4.0.11") < ClientVersion.CV_4011C).Should().BeTrue();
            (V("5.0.0a") >= ClientVersion.CV_500A).Should().BeTrue();
            (V("5.0.0") < ClientVersion.CV_500A).Should().BeTrue();
            (V("4.0.11f") < ClientVersion.CV_500A).Should().BeTrue();
            (V("5.0.6e") < ClientVersion.CV_6000).Should().BeTrue();
            (V("5.0.6e") < V("5.0.7.0")).Should().BeTrue();
            (V("3.0.8z") >= ClientVersion.CV_308Z).Should().BeTrue();
            (V("3.0.8y") < ClientVersion.CV_308Z).Should().BeTrue();
        }

        [Fact]
        public void Letter_Era_Is_A_Version_Comparison()
        {
            ClientVersionHelper.IsLetterVersion(4, 0, 11).Should().BeTrue();
            ClientVersionHelper.IsLetterVersion(5, 0, 6).Should().BeTrue();
            ClientVersionHelper.IsLetterVersion(5, 0, 7).Should().BeFalse();
            ClientVersionHelper.IsLetterVersion(5, 1, 0).Should().BeFalse("5.1.0 is after 5.0.7 although its build is below 7");
            ClientVersionHelper.IsLetterVersion(4, 9, 99).Should().BeTrue();
            ClientVersionHelper.IsLetterVersion(6, 0, 1).Should().BeFalse();

            // after 5.0.7 a letter is ignored, as ModernUO does
            ClientVersionHelper.IsClientVersionValid("5.0.9b", out var v).Should().BeTrue();
            ((int)v).Should().Be((5 << 24) | (0 << 16) | (9 << 8));
        }
    }
}
