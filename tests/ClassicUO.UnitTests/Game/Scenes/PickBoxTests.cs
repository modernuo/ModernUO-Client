using System;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // perf-17, perf-19 (PickBox): the pick test of a static, a multi component and an art item rejects a mouse
    // outside the box the largest static art can reach from the anchor. The box must hold every pixel the full test can hit:
    // the picker's OrigW x OrigH mask at the anchor minus the art entry's stored offset ((OrigW >> 1) - 22, OrigH - 44), or
    // minus 0, 0 while the entry is unset; for an item also that mask 5 px up and left (a stack's copy).
    [Collection(nameof(PickSkipTests))] // shares SelectedObject.TranslatedMousePositionByViewport with PickSkipTests: never in parallel
    public sealed class PickBoxTests : IDisposable
    {
        public PickBoxTests()
        {
            Reset();
        }

        public void Dispose()
        {
            Reset();
            SelectedObject.TranslatedMousePositionByViewport = Point.Zero;
        }

        private static void Reset()
        {
            PickBox.Active = PickBox.ItemActive = false;
        }

        [Theory]
        [InlineData(1, 1, 1, 1)]
        [InlineData(44, 44, 44, 44)]
        [InlineData(20, 30, 200, 260)]
        [InlineData(45, 45, 45, 45)]
        [InlineData(200, 260, 200, 260)]
        [InlineData(91, 17, 300, 300)]
        [InlineData(255, 20, 255, 400)]
        public void An_Early_Reject_Is_A_Miss_With_Either_Draw_Offset(int w, int h, int maxW, int maxH)
        {
            PickBox.SetBounds(maxW, maxH);

            foreach (bool sized in new[] { false, true })
            {
                int entryW = sized ? (w >> 1) - 22 : 0;
                int entryH = sized ? h - 44 : 0;

                for (int dy = -maxH - 80; dy < maxH + 120; dy++)
                {
                    for (int dx = -maxW - 80; dx < maxW + 120; dx++)
                    {
                        int mx = dx + entryW, my = dy + entryH;
                        bool inMask = mx >= 0 && my >= 0 && mx < w && my < h;

                        if (inMask)
                        {
                            PickBox.Rejects(dx, dy).Should().BeFalse($"art {w}x{h} entry {entryW},{entryH}: mouse {dx},{dy} is in the mask");
                        }
                    }
                }
            }

            // the box is bounded: far away in every direction is rejected
            PickBox.Rejects(-maxW - 1, 0).Should().BeTrue();
            PickBox.Rejects(maxW + 22, 0).Should().BeTrue();
            PickBox.Rejects(0, -maxH - 1).Should().BeTrue();
            PickBox.Rejects(0, maxH + 44).Should().BeTrue();
            PickBox.Rejects(0, 0).Should().BeFalse();
        }

        [Fact]
        public void The_Box_Is_Sized_From_The_Largest_Static_Art()
        {
            var sprites = new SpriteEntry[0x4000 + 4];
            sprites[10].OrigW = 900; // land art: not a static
            sprites[0x4000].OrigW = 30;
            sprites[0x4000].OrigH = 140;
            sprites[0x4002].OrigW = 210;
            sprites[0x4002].OrigH = 50;

            PickBox.MaxStaticSize(sprites, out int w, out int h);
            (w, h).Should().Be((210, 140));

            PickBox.MaxStaticSize(null, out w, out h);
            (w, h).Should().Be((0, 0));
        }

        [Fact]
        public void A_Far_Mouse_Is_Rejected_Before_The_Art_Is_Read()
        {
            var world = new World();
            var st = new Static(world) { Graphic = 0x0ABC };
            st.RealScreenPosition = new Point(500, 400);
            PickBox.SetBounds(100, 120);
            PickBox.Active = true;

            // no Client.Game in tests: a pick that reached the full test would throw
            foreach (Point mouse in new[] { new Point(100, 400), new Point(500, 50), new Point(1000, 400), new Point(500, 900) })
            {
                SelectedObject.TranslatedMousePositionByViewport = mouse;
                st.CheckMouseSelection().Should().BeFalse($"mouse {mouse}");
            }
        }

        [Fact]
        public void Inactive_Box_Leaves_The_Full_Test()
        {
            var world = new World();
            var st = new Static(world) { Graphic = 0x0ABC };
            st.RealScreenPosition = new Point(500, 400);
            PickBox.SetBounds(100, 120);
            PickBox.Active = false;

            // the full test's first answer: the selected object is not picked again (it never reaches the art)
            SelectedObject.Object = st;

            try
            {
                SelectedObject.TranslatedMousePositionByViewport = new Point(100, 400);
                st.CheckMouseSelection().Should().BeFalse();
            }
            finally
            {
                SelectedObject.Object = null;
            }
        }

        [Theory]
        [InlineData(1, 1, 1, 1)]
        [InlineData(44, 44, 44, 44)]
        [InlineData(20, 30, 200, 260)]
        [InlineData(45, 45, 45, 45)]
        [InlineData(200, 260, 200, 260)]
        [InlineData(91, 17, 300, 300)]
        [InlineData(255, 20, 255, 400)]
        public void An_Item_Early_Reject_Is_A_Miss_With_Either_Draw_Offset_And_The_Stack(int w, int h, int maxW, int maxH)
        {
            PickBox.SetBounds(maxW, maxH);

            foreach (bool sized in new[] { false, true })
            {
                int entryW = 0, entryH = 0;

                if (sized)
                {
                    Item.PickEntryFromSize(w, h, out entryW, out entryH);
                }

                for (int dy = -maxH - 80; dy < maxH + 120; dy++)
                {
                    for (int dx = -maxW - 80; dx < maxW + 120; dx++)
                    {
                        int mx = dx + entryW, my = dy + entryH;
                        bool inMask = mx >= 0 && my >= 0 && mx < w && my < h;
                        bool inStack = mx + 5 >= 0 && my + 5 >= 0 && mx + 5 < w && my + 5 < h;

                        if (inMask || inStack)
                        {
                            PickBox.RejectsItem(dx, dy).Should().BeFalse($"art {w}x{h} entry {entryW},{entryH}: mouse {dx},{dy} is in a mask");
                        }
                    }
                }
            }

            PickBox.RejectsItem(-maxW - 6, 0).Should().BeTrue();
            PickBox.RejectsItem(maxW + 22, 0).Should().BeTrue();
            PickBox.RejectsItem(0, -maxH - 6).Should().BeTrue();
            PickBox.RejectsItem(0, maxH + 44).Should().BeTrue();
            PickBox.RejectsItem(-maxW - 5, -maxH - 5).Should().BeFalse();
        }

        [Fact]
        public void A_Far_Mouse_Is_Rejected_Before_A_Multi_Reads_Its_Art()
        {
            var world = new World();
            var m = new Multi(world) { Graphic = 0x0ABC };
            m.RealScreenPosition = new Point(500, 400);
            PickBox.SetBounds(100, 120);
            PickBox.Active = true;

            // no Client.Game in tests: a pick that reached the full test would throw
            foreach (Point mouse in new[] { new Point(100, 400), new Point(500, 50), new Point(1000, 400), new Point(500, 900) })
            {
                SelectedObject.TranslatedMousePositionByViewport = mouse;
                m.CheckMouseSelection().Should().BeFalse($"mouse {mouse}");
            }
        }

        [Fact]
        public void A_Multi_Box_Moves_With_The_Boat_Offset()
        {
            var world = new World();
            var m = new Multi(world) { Graphic = 0x0ABC };
            m.RealScreenPosition = new Point(500, 400);
            m.Offset = new Vector3(300, 0, 0);
            PickBox.SetBounds(100, 120);
            PickBox.Active = true;

            // inside the box only with the offset: the full test runs (and throws without Client.Game)
            SelectedObject.TranslatedMousePositionByViewport = new Point(850, 400);
            Action pick = () => m.CheckMouseSelection();
            pick.Should().Throw<Exception>();

            // inside the box only without the offset: rejected
            SelectedObject.TranslatedMousePositionByViewport = new Point(450, 400);
            m.CheckMouseSelection().Should().BeFalse();
        }

        [Fact]
        public void A_Far_Mouse_Is_Rejected_Before_An_Item_Reads_Its_Art()
        {
            var world = new World();
            Item item = world.GetOrCreateItem(0x4000_0010);
            item.RealScreenPosition = new Point(500, 400);
            PickBox.SetBounds(100, 120);
            PickBox.Active = PickBox.ItemActive = true;

            // no Client.Game in tests: a pick that reached the full test would throw
            foreach (Point mouse in new[] { new Point(100, 400), new Point(500, 50), new Point(1000, 400), new Point(500, 900) })
            {
                SelectedObject.TranslatedMousePositionByViewport = mouse;
                item.CheckMouseSelection().Should().BeFalse($"mouse {mouse}");
            }
        }

        [Fact]
        public void Without_ItemActive_An_Item_Keeps_The_Full_Test()
        {
            var world = new World();
            Item item = world.GetOrCreateItem(0x4000_0011);
            item.RealScreenPosition = new Point(500, 400);
            PickBox.SetBounds(100, 120);
            PickBox.Active = true;
            PickBox.ItemActive = false;
            SelectedObject.TranslatedMousePositionByViewport = new Point(100, 400);

            // the full test reads the tile data (no Client.Game in tests)
            Action pick = () => item.CheckMouseSelection();
            pick.Should().Throw<Exception>();
        }
    }
}
