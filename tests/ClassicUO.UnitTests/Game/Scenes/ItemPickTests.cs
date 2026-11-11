using System;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // pa-itemprep (item_pick_early_reject): the early reject of an art item's pick test only rejects a mouse that the full
    // test misses whichever draw offset it finds (the sized entry of the pack size, or none yet), the stack's second copy
    // included; CUO_ITEM_PICK_CHECK counts an early reject the full test hit.
    [Collection(nameof(ItemPickTests))]
    public sealed class ItemPickTests : IDisposable
    {
        public ItemPickTests()
        {
            Reset();
        }

        public void Dispose()
        {
            Reset();
        }

        private static void Reset()
        {
#if CUO_DEVTOOLS
            ItemPick.ResetForTests();
#else
            ItemPick.OverrideForTests = null;
#endif
        }

        private static bool InMask(int x, int y, int w, int h) => x >= 0 && y >= 0 && x < w && y < h;

        [Theory]
        [InlineData(1, 1)]
        [InlineData(20, 30)]
        [InlineData(43, 44)]
        [InlineData(44, 44)]
        [InlineData(45, 45)]
        [InlineData(44, 120)]
        [InlineData(91, 17)]
        [InlineData(200, 260)]
        public void An_Early_Reject_Is_A_Miss_With_Either_Draw_Offset(int w, int h)
        {
            Item.PickEntryFromSize(w, h, out int entryW, out int entryH);
            int rejects = 0;

            for (int dy = -h - 60; dy < h + 60; dy++)
            {
                for (int dx = -w - 60; dx < w + 60; dx++)
                {
                    if (!ItemPick.OutsideArt(w, h, dx, dy))
                    {
                        continue;
                    }

                    rejects++;

                    // sized: the full test rejects by its box, and the mask (and the stack's copy) is inside that box
                    int sx = dx + entryW, sy = dy + entryH;
                    Item.OutsidePickBounds(entryW, entryH, sx, sy).Should().BeTrue();
                    InMask(sx, sy, w, h).Should().BeFalse();
                    InMask(sx + 5, sy + 5, w, h).Should().BeFalse();

                    // not sized yet: the pixel tests at the unshifted position
                    InMask(dx, dy, w, h).Should().BeFalse();
                    InMask(dx + 5, dy + 5, w, h).Should().BeFalse();
                }
            }

            rejects.Should().BeGreaterThan(0);
        }

        [Fact]
        public void A_Mouse_Far_From_The_Anchor_Is_Rejected()
        {
            ItemPick.OutsideArt(44, 44, 300, 0).Should().BeTrue();
            ItemPick.OutsideArt(44, 44, 0, 300).Should().BeTrue();
            ItemPick.OutsideArt(44, 44, -300, 0).Should().BeTrue();
            ItemPick.OutsideArt(44, 44, 0, -300).Should().BeTrue();
            ItemPick.OutsideArt(44, 44, 10, 10).Should().BeFalse();
        }

        [Theory]
        [InlineData(null)]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Switch_Follows_The_Override(bool? value)
        {
            ItemPick.OverrideForTests = value;
            ItemPick.Enabled.Should().Be(value ?? ClassicUO.Configuration.Settings.GlobalSettings.ItemPickEarlyReject);
        }

#if CUO_DEVTOOLS
        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(false, true, 0)]
        [InlineData(true, false, 0)]
        [InlineData(true, true, 1)]
        public void The_Checker_Counts_Only_A_Reject_The_Full_Test_Hit(bool reject, bool hit, int mismatches)
        {
            var world = new World();
            Item item = world.GetOrCreateItem(0x4000_0001);

            ItemPick.Judge(item, 0x0EED, reject, hit);

            ItemPick.Mismatches.Should().Be(mismatches);
            ItemPick.Rejects.Should().Be(reject ? 1 : 0);
        }
#endif
    }
}
