using System;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // qw-renderlist (pick_skip_off_world): a world fill whose mouse is not over the world makes no pick tests; with the switch
    // off, or the mouse over the world, picking runs as before. CUO_PICK_SKIP_CHECK runs the old picking and counts a frame
    // whose skip would change the selection the game sees after DrawUI.
    [Collection(nameof(PickSkipTests))]
    public sealed class PickSkipTests : IDisposable
    {
        public PickSkipTests()
        {
            Reset();
        }

        public void Dispose()
        {
            Reset();
            SelectedObject.TranslatedMousePositionByViewport = Point.Zero;
        }

        // the checker and its test hooks exist only in developer builds (CUO_DEVTOOLS)
        private static void Reset()
        {
#if CUO_DEVTOOLS
            PickSkip.ResetForTests();
#else
            PickSkip.OverrideForTests = null;
            PickSkip.Off = false;
#endif
        }

        private static Land LandUnderMouse(World world)
        {
            var land = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new object[] { world }, null);
            land.RealScreenPosition = new Point(100, 200);
            // the middle of the land diamond (SelectedObject.IsPointInLand)
            SelectedObject.TranslatedMousePositionByViewport = new Point(100 + 22, 200 + 22);

            return land;
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        public void Fill_Skips_Picking_Only_With_The_Switch_On_And_The_Mouse_Off_The_World(bool on, bool overWorld, bool off)
        {
            PickSkip.OverrideForTests = on;

            PickSkip.BeginFill(overWorld);

            PickSkip.Off.Should().Be(off);
        }

#if CUO_DEVTOOLS
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Checker_Keeps_The_Old_Picking_And_Judges_The_Skip(bool on)
        {
            var world = new World();
            Land land = LandUnderMouse(world);
            PickSkip.OverrideForTests = on;
            PickSkip.CheckOverrideForTests = true;

            // mouse over a gump: the old fill picks the land (the draw highlight the switch drops), DrawUI clears it
            PickSkip.BeginFill(mouseOverWorld: false);
            PickSkip.Off.Should().BeFalse();
            land.CheckMouseSelection().Should().BeTrue();
            PickSkip.Judge(land, null);

            PickSkip.Mismatches.Should().Be(0);
            PickSkip.HighlightOnly.Should().Be(on ? 1 : 0);

            // the mouse reached the world between the fill and DrawUI: the old selection survives, a skipped fill's would not
            PickSkip.BeginFill(mouseOverWorld: false);
            PickSkip.Judge(land, land);

            PickSkip.Mismatches.Should().Be(on ? 1 : 0);

            // mouse over the world: nothing to skip
            PickSkip.BeginFill(mouseOverWorld: true);
            PickSkip.Judge(land, land);

            PickSkip.Mismatches.Should().Be(on ? 1 : 0);
        }

        [Fact]
        public void Judge_Without_A_Fill_Counts_Nothing()
        {
            PickSkip.OverrideForTests = true;
            PickSkip.CheckOverrideForTests = true;

            PickSkip.Judge(null, LandUnderMouse(new World()));

            PickSkip.Mismatches.Should().Be(0);
        }
#endif
    }
}
