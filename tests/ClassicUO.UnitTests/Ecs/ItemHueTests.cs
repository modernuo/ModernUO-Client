using ClassicUO.Game;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // ItemSpriteExtract.ChooseHue: ItemView.Draw's art hue choice, as a pure function of the item, profile and selection.
    public class ItemHueTests
    {
        private const ushort GRAPHIC = 0x1234;

        private static ItemSpriteExtract.HueInput Plain(ushort hue = 0x0481) =>
            new ItemSpriteExtract.HueInput { Hue = hue, Graphic = GRAPHIC, RawGraphic = GRAPHIC };

        private static (ushort Graphic, ushort Hue, bool Partial) Choose(ItemSpriteExtract.HueInput i)
        {
            ItemSpriteExtract.ChooseHue(in i, out ushort g, out ushort h, out bool p);

            return (g, h, p);
        }

        [Fact]
        public void A_Plain_Item_Keeps_Its_Graphic_Hue_And_Partial_Flag()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.Partial = true;

            Choose(i).Should().Be((GRAPHIC, (ushort)0x0481, true));
        }

        [Theory]
        [InlineData(0x398C, 0x0020)] // fire field
        [InlineData(0x399F, 0x0020)]
        [InlineData(0x3967, 0x0058)] // paralyze
        [InlineData(0x397A, 0x0058)]
        [InlineData(0x082, 0x038A)]  // wall of stone
        public void Ground_Fields_Are_Replaced_When_The_Option_Is_On(int field, int hue)
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.OnGround = true;
            i.ReplaceFields = true;
            i.RawGraphic = (ushort)field;

            var (graphic, h, _) = Choose(i);

            graphic.Should().Be(Constants.FIELD_REPLACE_GRAPHIC);
            h.Should().Be((ushort)hue);
        }

        [Fact]
        public void Fields_Are_Left_Alone_When_Not_On_The_Ground_Or_The_Option_Is_Off()
        {
            ItemSpriteExtract.HueInput worn = Plain();
            worn.ReplaceFields = true;
            worn.RawGraphic = 0x398C;
            Choose(worn).Graphic.Should().Be(GRAPHIC);

            ItemSpriteExtract.HueInput off = Plain();
            off.OnGround = true;
            off.RawGraphic = 0x398C;
            Choose(off).Graphic.Should().Be(GRAPHIC);
        }

        [Fact]
        public void Field_Replacement_Tests_The_Raw_Graphic_Not_The_Displayed_One()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.OnGround = true;
            i.ReplaceFields = true;
            i.Graphic = 0x398C;
            i.RawGraphic = GRAPHIC;

            Choose(i).Graphic.Should().Be(0x398C);
        }

        [Fact]
        public void The_Selected_Container_On_The_Ground_Is_Hued_Fully()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.OnGround = true;
            i.SelectedContainer = true;
            i.Partial = true;

            Choose(i).Should().Be((GRAPHIC, (ushort)0x0035, false));

            i.OnGround = false;
            Choose(i).Should().Be((GRAPHIC, (ushort)0x0481, true), "only ground containers");
        }

        [Fact]
        public void The_Highlight_Beats_Everything_Else_And_Clears_Partial()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.Partial = true;
            i.HighlightSelected = true;
            i.OutOfRange = true;
            i.PlayerDeadGray = true;
            i.Selected = true;
            i.Hidden = true;

            Choose(i).Should().Be((GRAPHIC, Constants.HIGHLIGHT_CURRENT_OBJECT_HUE, false));
        }

        [Fact]
        public void Out_Of_Range_Tints_With_The_Out_Of_Range_Hue_And_Keeps_Partial()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.Partial = true;
            i.OutOfRange = true;
            i.PlayerDeadGray = true;
            i.Selected = true;
            i.Hidden = true;

            Choose(i).Should().Be((GRAPHIC, (ushort)Constants.OUT_RANGE_COLOR, true));
        }

        [Fact]
        public void Out_Of_Range_Beats_The_Selected_Container_Hue_But_Keeps_Its_Cleared_Partial()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.OnGround = true;
            i.SelectedContainer = true;
            i.Partial = true;
            i.OutOfRange = true;

            Choose(i).Should().Be((GRAPHIC, (ushort)Constants.OUT_RANGE_COLOR, false));
        }

        [Fact]
        public void A_Dead_Player_With_Black_And_White_Grays_Items_That_Are_In_Range()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.PlayerDeadGray = true;
            i.Selected = true;
            i.Hidden = true;

            Choose(i).Hue.Should().Be((ushort)Constants.DEAD_RANGE_COLOR);
        }

        [Fact]
        public void The_Item_Under_The_Mouse_Is_Hued_Unless_It_Is_Locked_Or_A_Multi()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.Selected = true;
            i.Hidden = true;
            Choose(i).Hue.Should().Be(0x0035);

            i.Locked = true;
            Choose(i).Hue.Should().Be(0x038E, "locked down: falls to the hidden hue");

            i.Locked = false;
            i.Multi = true;
            Choose(i).Hue.Should().Be(0x038E, "a multi: likewise");

            i.Hidden = false;
            Choose(i).Hue.Should().Be(0x0481);
        }

        [Fact]
        public void A_Hidden_Item_Takes_The_Hidden_Hue()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.Hidden = true;
            i.Partial = true;

            Choose(i).Should().Be((GRAPHIC, (ushort)0x038E, true));
        }

        [Fact]
        public void Field_Replacement_Hue_Survives_When_Nothing_Overrides_It_And_Loses_To_The_Out_Of_Range_Tint()
        {
            ItemSpriteExtract.HueInput i = Plain();
            i.OnGround = true;
            i.ReplaceFields = true;
            i.RawGraphic = 0x3967;

            Choose(i).Hue.Should().Be(0x0058);

            i.OutOfRange = true;
            var (graphic, hue, _) = Choose(i);

            graphic.Should().Be(Constants.FIELD_REPLACE_GRAPHIC, "the graphic is still replaced");
            hue.Should().Be((ushort)Constants.OUT_RANGE_COLOR);
        }
    }
}
