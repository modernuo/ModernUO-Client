using System;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // PacketHandlers.ParseArgCliloc replaced argument.Split('#', RemoveEmptyEntries) in MegaCliloc; it must give the
    // same cliloc for every argument shape.
    public class MegaClilocArgTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("plain")]
        [InlineData("#1042971")]
        [InlineData("a#1042971")]
        [InlineData("a##1042971")]
        [InlineData("#a#1042971")]
        [InlineData("a#1042971#")]
        [InlineData("a#1042971#b")]
        [InlineData("a#b")]
        [InlineData("a# 12 ")]
        [InlineData("a#-5")]
        [InlineData("a#99999999999")]
        [InlineData("###")]
        [InlineData("1#2")]
        public void Matches_The_Split_Version(string argument)
        {
            int expected = 0;
            string[] parts = argument.Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2)
            {
                int.TryParse(parts[1], out expected);
            }

            PacketHandlers.ParseArgCliloc(argument).Should().Be(expected);
        }
    }
}
