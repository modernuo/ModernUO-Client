using System;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // qw-renderlist (pick_skip_off_world, permanent since quiet-1123): a world fill whose mouse is not over the world makes no
    // pick tests; with the mouse over the world, picking runs as before.
    [Collection(nameof(PickSkipTests))]
    public sealed class PickSkipTests : IDisposable
    {
        public PickSkipTests()
        {
            PickSkip.Off = false;
        }

        public void Dispose()
        {
            PickSkip.Off = false;
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Fill_Skips_Picking_Only_With_The_Mouse_Off_The_World(bool overWorld, bool off)
        {
            PickSkip.BeginFill(overWorld);

            PickSkip.Off.Should().Be(off);
        }
    }
}
