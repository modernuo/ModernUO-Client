using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // Edge cases of the pure rules that AnimationRulesTests doesn't reach: body ranges, covered layers, hue precedence,
    // strip splitting, frame timing, and the paperdoll/in-world equipment order that EquipmentSlots is built from.
    public class AnimationRulesEdgeTests
    {
        [Theory]
        [InlineData(0x018F, false)]
        [InlineData(0x0190, true)]
        [InlineData(0x0193, true)]
        [InlineData(0x0194, false)]
        [InlineData(0x00B6, false)]
        [InlineData(0x00B7, true)]
        [InlineData(0x00BA, true)]
        [InlineData(0x00BB, false)]
        [InlineData(0x025C, false)]
        [InlineData(0x025D, true)]
        [InlineData(0x0260, true)]
        [InlineData(0x0261, false)]
        [InlineData(0x029A, true)]
        [InlineData(0x029B, true)]
        [InlineData(0x02B6, true)]
        [InlineData(0x02B7, true)]
        [InlineData(0x03DB, true)]
        [InlineData(0x03DF, true)]
        [InlineData(0x03E2, true)]
        [InlineData(0x02E8, true)]
        [InlineData(0x02E9, true)]
        [InlineData(0x04E5, true)]
        [InlineData(0x00C8, false)]
        [InlineData(0x0000, false)]
        public void Human_Bodies_Are_The_Player_Races_And_Their_Ghosts(int graphic, bool human)
        {
            AnimationRules.IsHumanBody((ushort)graphic).Should().Be(human);
        }

        [Theory]
        [InlineData(0x0190, false)]
        [InlineData(0x0191, false)]
        [InlineData(0x0192, true)]
        [InlineData(0x0193, true)]
        [InlineData(0x025E, false)]
        [InlineData(0x025F, true)]
        [InlineData(0x0260, true)]
        [InlineData(0x0261, false)]
        [InlineData(0x02B6, true)]
        [InlineData(0x02B7, true)]
        [InlineData(0x029A, false)]
        public void Dead_Bodies_Are_The_Ghost_Graphics(int graphic, bool dead)
        {
            AnimationRules.IsDeadBody((ushort)graphic).Should().Be(dead);
        }

        [Fact]
        public void Body_For_Animation_Leaves_Every_Other_Body_Alone()
        {
            foreach (ushort g in new ushort[] { 0, 0x0191, 0x0194, 0x025F, 0x02B5, 0x02B8, 0xFFFF })
            {
                AnimationRules.BodyForAnimation(g).Should().Be(g);
            }
        }

        [Fact]
        public void Gargoyle_Draw_Body_Needs_Client_7000_But_The_Prop_Test_Accepts_0x29B_On_Any_Version()
        {
            foreach (ushort g in new ushort[] { 666, 667, 0x02B6, 0x02B7 })
            {
                AnimationRules.IsGargoyleDrawBody(g, true).Should().BeTrue();
                AnimationRules.IsGargoyleDrawBody(g, false).Should().BeFalse();
            }

            AnimationRules.IsGargoyleDrawBody(0x0190, true).Should().BeFalse();
            AnimationRules.IsGargoyleProp(0x029A, true).Should().BeTrue();
            AnimationRules.IsGargoyleProp(0x029B, true).Should().BeTrue();
            AnimationRules.IsGargoyleProp(0x0190, true).Should().BeFalse();
        }

        [Theory]
        [InlineData(0x3E9B, 0x00C0)]
        [InlineData(0x3E9D, 0x00C0)]
        [InlineData(0x3E9C, 0x00BF)]
        public void Ethereal_Mounts_Use_Fixed_Bodies_Whatever_The_AnimId(int item, int body)
        {
            AnimationRules.MountBody((ushort)item, 0).Should().Be((ushort)body);
            AnimationRules.MountBody((ushort)item, 0x0777).Should().Be((ushort)body);
        }

        [Fact]
        public void A_Mount_Whose_Body_Is_0xFFFF_Is_Not_Riding_And_Needs_A_Mount_Item()
        {
            AnimationRules.IsMountedBy(0x4000_0001, 0x1234, 0xFFFF).Should().BeFalse("the AnimID is the body");
            AnimationRules.IsMountedBy(0x4000_0001, 0xFFFF, 0).Should().BeFalse("an unmapped item graphic is its own body");
            AnimationRules.IsMountedBy(0x4000_0001, 0x1234, 0x00CC).Should().BeTrue();
            AnimationRules.IsMountedBy(0, 0x1234, 0x00CC).Should().BeFalse("serial 0 = no mount item");
        }

        [Theory]
        [InlineData(0x03CA, 0x0223)]
        [InlineData(0x0426, 0x042B)]
        [InlineData(1530, 1532)]
        [InlineData(0x0372, 330)]
        public void Gargoyle_Equipment_Is_Redrawn_With_The_Gargoyle_Bodies(int human, int gargoyle)
        {
            LegacyLayerRulesReference.FixGargoyleEquipment((ushort)human).Should().Be((ushort)gargoyle);
            LegacyLayerRulesReference.FixGargoyleEquipment((ushort)gargoyle).Should().Be((ushort)gargoyle, "an already remapped body is left alone");
        }

        [Fact]
        public void Map_Direction_Always_Lands_In_0_To_4_And_Mirrors_Only_The_Four_Western_Directions()
        {
            for (byte d = 0; d < 8; d++)
            {
                byte dir = d;
                bool mirrorOn = true, mirrorOff = false;
                AnimationRules.MapDirection(ref dir, ref mirrorOn);
                byte dir2 = d;
                AnimationRules.MapDirection(ref dir2, ref mirrorOff);

                dir.Should().BeLessThan(5);
                dir2.Should().Be(dir);

                if (d == 3 || d == 7)
                {
                    mirrorOn.Should().BeTrue("straight directions keep the previous mirror");
                    mirrorOff.Should().BeFalse();
                }
                else
                {
                    mirrorOn.Should().Be(mirrorOff, "the mirror is decided by the direction alone");
                }
            }
        }

        [Fact]
        public void Layer_Hue_A_Hue_Of_Only_The_Partial_Bit_Falls_Through_To_The_Body_File()
        {
            AnimationRules.LayerHue(0, 0x8000, false, true, true, 0x0444, 0x0555, out bool partial).Should().Be(0x0444);
            partial.Should().BeFalse("the fallback hue is never partial");

            AnimationRules.LayerHue(0, 0x8000, false, true, true, 0, 0x0555, out partial).Should().Be(0x0555);
            partial.Should().BeFalse();
        }

        [Fact]
        public void Layer_Hue_Partial_Needs_A_Tiledata_Item_Unless_The_Hue_Asks_For_It()
        {
            AnimationRules.LayerHue(0, 0x0021, false, false, true, 0, 0, out bool partial);
            partial.Should().BeFalse("only items are partial hued by tiledata (a body layer isn't an item)");

            AnimationRules.LayerHue(0, 0x0021, false, true, false, 0, 0, out partial);
            partial.Should().BeFalse("the item isn't a partial-hue item");

            AnimationRules.LayerHue(0, 0x8021, true, true, false, 0, 0, out partial).Should().Be(0x0021);
            partial.Should().BeTrue("the 0x8000 bit asks for partial even on the mount");
        }

        [Fact]
        public void Layer_Hue_Override_Wins_Over_Everything_Including_An_Empty_Layer_Hue()
        {
            AnimationRules.LayerHue(0x0386, 0, false, true, true, 0x0444, 0x0555, out bool partial).Should().Be(0x0386);
            partial.Should().BeFalse();
        }

        [Fact]
        public void Override_Hue_Order_Is_Highlight_Then_Healthbar_Then_Range_Then_Death_Screen_Then_Hidden()
        {
            var i = new AnimationRules.OverrideHueInput
            {
                HighlightSelected = true, IsHealthbarTarget = true, OutOfRange = true, PlayerDeadGray = true, Hidden = true, NotorietyHue = 0x59
            };
            AnimationRules.OverrideHue(in i).Should().Be(Constants.HIGHLIGHT_CURRENT_OBJECT_HUE);

            i.HighlightSelected = false;
            AnimationRules.OverrideHue(in i).Should().Be(0x59, "a healthbar target shows its notoriety");

            i.IsHealthbarTarget = false;
            AnimationRules.OverrideHue(in i).Should().Be(Constants.OUT_RANGE_COLOR);

            i.OutOfRange = false;
            AnimationRules.OverrideHue(in i).Should().Be(Constants.DEAD_RANGE_COLOR);

            i.PlayerDeadGray = false;
            AnimationRules.OverrideHue(in i).Should().Be(0x038E);
        }

        [Fact]
        public void Override_Hue_Status_Highlights_Only_Apply_To_The_Living_And_Respect_Invulnerability()
        {
            var live = new AnimationRules.OverrideHueInput
            {
                PoisonHighlight = true, Poisoned = true, PoisonHue = 0x44,
                ParalyzeHighlight = true, Paralyzed = true, ParalyzedHue = 0x14C,
                InvulHighlight = true, YellowHits = true, InvulnerableHue = 0x30
            };
            AnimationRules.OverrideHue(in live).Should().Be(0x30, "yellow hits are checked last");

            live.Invulnerable = true;
            AnimationRules.OverrideHue(in live).Should().Be(0x44, "an invulnerable mobile isn't tinted paralyzed or yellow");

            AnimationRules.OverrideHueInput dead = live;
            dead.Dead = true;
            dead.Human = true;
            AnimationRules.OverrideHue(in dead).Should().Be(0, "a dead human keeps its own colours");

            var noHighlight = new AnimationRules.OverrideHueInput { Poisoned = true, Paralyzed = true, YellowHits = true, PoisonHue = 0x44 };
            AnimationRules.OverrideHue(in noHighlight).Should().Be(0, "the highlight options are off");
        }

        [Fact]
        public void Override_Hue_Targeting_Under_The_Mouse_Shows_Notoriety_Except_On_The_Player()
        {
            var i = new AnimationRules.OverrideHueInput { UnderMouseWhileTargeting = true, NotorietyHue = 0x22, Hidden = true };
            AnimationRules.OverrideHue(in i).Should().Be(0x22, "even over the hidden grey");

            i.IsPlayer = true;
            AnimationRules.OverrideHue(in i).Should().Be(0x038E);
        }

        [Theory]
        [InlineData(100, -10, 0)]
        [InlineData(60, -60, 0)]
        [InlineData(60, 0, 0)]
        [InlineData(60, 25, 0)]
        [InlineData(60, 500, 0)]
        [InlineData(60, -10, -9)]
        [InlineData(1, 0, 0)]
        [InlineData(0, -5, 0)]
        public void Strips_Tile_The_Frame_Exactly_From_Top_To_Bottom(int height, int centerY, int mount)
        {
            Span<(int Y, int H)> s = stackalloc (int, int)[AnimationRules.MaxStrips(height)];

            int n = AnimationRules.Strips(height, centerY, mount, s);

            n.Should().BeGreaterThanOrEqualTo(2);
            n.Should().BeLessThanOrEqualTo(AnimationRules.MaxStrips(height));

            int y = 0;

            for (int i = 0; i < n; i++)
            {
                s[i].Y.Should().Be(y, "strips are contiguous");
                s[i].H.Should().BeGreaterThanOrEqualTo(0);
                y += s[i].H;
            }

            y.Should().Be(height, "strips cover the whole frame");
        }

        [Fact]
        public void Strips_Never_Write_Past_A_Short_Destination()
        {
            Span<(int Y, int H)> s = stackalloc (int, int)[3];

            AnimationRules.Strips(60, -60, 0, s).Should().Be(3);
        }

        [Fact]
        public void Max_Strips_Is_At_Least_Two()
        {
            AnimationRules.MaxStrips(0).Should().Be(2);
            AnimationRules.MaxStrips(1).Should().Be(2);
        }

        [Fact]
        public void Covered_By_Nothing_Without_A_Robe_Or_Related_Layers()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            Span<ushort> anim = stackalloc ushort[PaperdollOrder.N];
            Span<bool> present = stackalloc bool[PaperdollOrder.N];
            gfx.Clear();
            anim.Clear();
            present.Clear();

            foreach (Layer l in new[]
            {
                Layer.Cloak, Layer.Shirt, Layer.Torso, Layer.Arms, Layer.Gloves, Layer.Waist, Layer.Necklace, Layer.Hair, Layer.Helmet, Layer.Pants,
                Layer.Skirt, Layer.Bracelet, Layer.OneHanded
            })
            {
                LegacyLayerRulesReference.IsCovered(l, gfx, anim, present, 0x190).Should().BeFalse($"{l} with nothing else worn");
            }
        }

        [Fact]
        public void Covered_Shoes_Follow_Legs_And_Special_Pants()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            Span<ushort> anim = stackalloc ushort[PaperdollOrder.N];
            Span<bool> present = stackalloc bool[PaperdollOrder.N];
            gfx.Clear();
            anim.Clear();
            present.Clear();

            present[(int)Layer.Shoes] = true;
            LegacyLayerRulesReference.IsCovered(Layer.Shoes, gfx, anim, present, 0x190).Should().BeFalse();

            present[(int)Layer.Pants] = true;
            gfx[(int)Layer.Pants] = 0x152E;
            anim[(int)Layer.Pants] = 0x0513;
            LegacyLayerRulesReference.IsCovered(Layer.Shoes, gfx, anim, present, 0x190).Should().BeTrue("AnimID 0x513 pants cover the shoes");

            anim[(int)Layer.Pants] = 0x0514;
            LegacyLayerRulesReference.IsCovered(Layer.Shoes, gfx, anim, present, 0x190).Should().BeTrue();

            anim[(int)Layer.Pants] = 0x0001;
            LegacyLayerRulesReference.IsCovered(Layer.Shoes, gfx, anim, present, 0x190).Should().BeFalse("ordinary pants leave the shoes");

            present[(int)Layer.Robe] = true;
            gfx[(int)Layer.Robe] = 0x1F03;
            anim[(int)Layer.Robe] = 0x0504;
            LegacyLayerRulesReference.IsCovered(Layer.Shoes, gfx, anim, present, 0x190).Should().BeTrue("a floor-length robe (AnimID 0x504)");
        }

        [Fact]
        public void Covered_Pants_By_Legs_Or_A_Floor_Robe()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            Span<ushort> anim = stackalloc ushort[PaperdollOrder.N];
            Span<bool> present = stackalloc bool[PaperdollOrder.N];
            gfx.Clear();
            anim.Clear();
            present.Clear();

            present[(int)Layer.Pants] = true;
            anim[(int)Layer.Pants] = 0x0001;
            LegacyLayerRulesReference.IsCovered(Layer.Pants, gfx, anim, present, 0x190).Should().BeFalse();

            present[(int)Layer.Legs] = true;
            LegacyLayerRulesReference.IsCovered(Layer.Pants, gfx, anim, present, 0x190).Should().BeTrue("leg armour over the pants");

            present[(int)Layer.Legs] = false;
            present[(int)Layer.Robe] = true;
            anim[(int)Layer.Robe] = 0x0504;
            LegacyLayerRulesReference.IsCovered(Layer.Pants, gfx, anim, present, 0x190).Should().BeTrue();
        }

        [Fact]
        public void Covered_Hair_By_Hats_That_Hide_It_And_Helmet_Layer_By_Hooded_Robes()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            Span<ushort> anim = stackalloc ushort[PaperdollOrder.N];
            Span<bool> present = stackalloc bool[PaperdollOrder.N];
            gfx.Clear();
            anim.Clear();
            present.Clear();

            present[(int)Layer.Helmet] = true;
            gfx[(int)Layer.Helmet] = 0xA42B;
            LegacyLayerRulesReference.IsCovered(Layer.Hair, gfx, anim, present, 0x190).Should().BeTrue();
            gfx[(int)Layer.Helmet] = 0xA42C;
            LegacyLayerRulesReference.IsCovered(Layer.Hair, gfx, anim, present, 0x190).Should().BeTrue();
            gfx[(int)Layer.Helmet] = 0xA42D;
            LegacyLayerRulesReference.IsCovered(Layer.Hair, gfx, anim, present, 0x190).Should().BeFalse();
            LegacyLayerRulesReference.IsCovered(Layer.Helmet, gfx, anim, present, 0x190).Should().BeFalse("no robe, the helmet itself is drawn");

            present[(int)Layer.Robe] = true;
            gfx[(int)Layer.Robe] = 0x2FB9;
            LegacyLayerRulesReference.IsCovered(Layer.Helmet, gfx, anim, present, 0x190).Should().BeTrue("a hooded robe hides the helmet layer");
            gfx[(int)Layer.Robe] = 0x1F03;
            LegacyLayerRulesReference.IsCovered(Layer.Helmet, gfx, anim, present, 0x190).Should().BeFalse("a robe without a hood");
        }

        [Fact]
        public void Covered_Bracelet_Only_When_The_Wide_Bracelet_Meets_Arm_Armour()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            Span<ushort> anim = stackalloc ushort[PaperdollOrder.N];
            Span<bool> present = stackalloc bool[PaperdollOrder.N];
            gfx.Clear();
            anim.Clear();
            present.Clear();

            present[(int)Layer.Bracelet] = true;
            gfx[(int)Layer.Bracelet] = 0xB1C0;
            LegacyLayerRulesReference.IsCovered(Layer.Bracelet, gfx, anim, present, 0x190).Should().BeFalse("no arms");

            present[(int)Layer.Arms] = true;
            LegacyLayerRulesReference.IsCovered(Layer.Bracelet, gfx, anim, present, 0x190).Should().BeTrue();

            gfx[(int)Layer.Bracelet] = 0x1086;
            LegacyLayerRulesReference.IsCovered(Layer.Bracelet, gfx, anim, present, 0x190).Should().BeFalse("an ordinary bracelet");
        }

        [Fact]
        public void Covered_Necklace_Needs_A_Robe_And_A_Collar_That_Shows_Through()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            Span<ushort> anim = stackalloc ushort[PaperdollOrder.N];
            Span<bool> present = stackalloc bool[PaperdollOrder.N];
            gfx.Clear();
            anim.Clear();
            present.Clear();

            present[(int)Layer.Necklace] = true;
            anim[(int)Layer.Necklace] = 0x05EC;
            LegacyLayerRulesReference.IsCovered(Layer.Necklace, gfx, anim, present, 0x190).Should().BeFalse("no robe");

            present[(int)Layer.Robe] = true;
            gfx[(int)Layer.Robe] = 0x1F03;
            anim[(int)Layer.Robe] = 0x01DF;
            LegacyLayerRulesReference.IsCovered(Layer.Necklace, gfx, anim, present, 0x190).Should().BeTrue();

            anim[(int)Layer.Robe] = 0x05F2;
            LegacyLayerRulesReference.IsCovered(Layer.Necklace, gfx, anim, present, 0x190).Should().BeFalse("robes with an open neck");
        }

        // ---- action timing

        [Fact]
        public void A_Normal_Action_Of_One_Frame_Wraps_Every_Step()
        {
            var t = new AnimationTimer { Frame = 0 };

            AnimationTiming.Advance(ref t, 1, out AnimationAdvance r);

            t.Frame.Should().Be(0);
            r.Should().Be(AnimationAdvance.Wrapped);
        }

        [Fact]
        public void A_Normal_Action_Ignores_The_Server_Interval_And_Repeat_State()
        {
            var t = new AnimationTimer { Frame = 1, Interval = 9, RepeatMode = 3, Repeat = true, Forward = false };

            AnimationTiming.Advance(ref t, 4, out AnimationAdvance r).Should().Be(Constants.CHARACTER_ANIMATION_DELAY);

            t.Frame.Should().Be(2, "always forward");
            t.RepeatMode.Should().Be(3);
            r.Should().Be(AnimationAdvance.Continue);
        }

        [Fact]
        public void Server_Delay_Grows_Linearly_With_The_Interval()
        {
            var t0 = new AnimationTimer { FromServer = true, Forward = true, Interval = 0 };
            var t3 = new AnimationTimer { FromServer = true, Forward = true, Interval = 3 };
            int delay0 = AnimationTiming.Advance(ref t0, 5, out _);
            int delay3 = AnimationTiming.Advance(ref t3, 5, out _);

            delay0.Should().Be(Constants.CHARACTER_ANIMATION_DELAY * 2);
            delay3.Should().Be(Constants.CHARACTER_ANIMATION_DELAY * 5);
            t0.FrameCount.Should().Be(5);
            t3.FrameCount.Should().Be(5);
        }

        [Fact]
        public void Server_Action_With_Two_Repeats_Plays_Twice_Then_Finishes_At_The_End_Of_The_Second_Pass()
        {
            var t = new AnimationTimer { FromServer = true, Forward = true, RepeatMode = 2, Frame = 0 };
            int finishedAt = -1;

            for (int step = 0; step < 20; step++)
            {
                AnimationTiming.Advance(ref t, 4, out AnimationAdvance r);

                if (r == AnimationAdvance.Finished)
                {
                    finishedAt = step;

                    break;
                }
            }

            finishedAt.Should().Be(7, "4 frames x 2 passes = the 8th step ends the action");
        }

        [Fact]
        public void Server_Action_With_A_Repeat_Flag_Restarts_Once_Instead_Of_Finishing()
        {
            var t = new AnimationTimer { FromServer = true, Forward = true, RepeatMode = 1, Repeat = true, Frame = 3 };

            AnimationTiming.Advance(ref t, 4, out AnimationAdvance r);

            r.Should().Be(AnimationAdvance.Continue);
            t.Repeat.Should().BeFalse("the flag is consumed");
            t.Frame.Should().Be(0);

            t.Frame = 3;
            AnimationTiming.Advance(ref t, 4, out r);
            r.Should().Be(AnimationAdvance.Continue, "RepeatMode is already 0, which repeats forever");
        }

        [Fact]
        public void Server_Action_Backwards_Walks_Down_And_Finishes_Past_Frame_Zero()
        {
            var t = new AnimationTimer { FromServer = true, Forward = false, RepeatMode = 1, Frame = 2 };

            AnimationTiming.Advance(ref t, 4, out AnimationAdvance r);
            t.Frame.Should().Be(1);
            r.Should().Be(AnimationAdvance.Continue);

            AnimationTiming.Advance(ref t, 4, out r);
            t.Frame.Should().Be(0);

            AnimationTiming.Advance(ref t, 4, out r);
            t.Frame.Should().Be(3, "wraps to the last frame");
            r.Should().Be(AnimationAdvance.Finished);
        }

        [Fact]
        public void A_Wrapped_Normal_Action_Is_Reported_Only_On_The_Wrap()
        {
            var t = new AnimationTimer { Frame = 0 };
            int wraps = 0;

            for (int i = 0; i < 12; i++)
            {
                AnimationTiming.Advance(ref t, 4, out AnimationAdvance r);

                if (r == AnimationAdvance.Wrapped)
                {
                    wraps++;
                }
            }

            wraps.Should().Be(3);
        }

        // ---- equipment order (the order EquipmentSlots is built in)

        private static Layer[] Order(Span<ushort> animIds, bool alt, bool backpack = false)
        {
            Span<Layer> raw = stackalloc Layer[PaperdollOrder.N];
            Span<Layer> order = stackalloc Layer[PaperdollOrder.N];
            LegacyLayerRulesReference.Build(animIds, alt, raw);
            int n = PaperdollOrder.Filter(raw, backpack, order);

            return order.Slice(0, n).ToArray();
        }

        [Fact]
        public void Default_Order_Has_No_Sentinel_Mount_Or_Backpack_And_Every_Other_Layer_Once()
        {
            Span<ushort> none = stackalloc ushort[PaperdollOrder.N];
            none.Clear();

            Layer[] order = Order(none, false);

            order.Should().NotContain(Layer.Invalid).And.NotContain(Layer.Mount).And.NotContain(Layer.Backpack);
            order.Should().OnlyHaveUniqueItems();
            order.Length.Should().Be(PaperdollOrder.N - 2, "25 table entries minus the sentinel and the backpack");
            order[0].Should().Be(Layer.Cloak, "painted first, so behind everything");
            order[^1].Should().Be(Layer.Talisman);
        }

        [Fact]
        public void Backpack_Is_Kept_Only_When_Asked_For()
        {
            Span<ushort> none = stackalloc ushort[PaperdollOrder.N];
            none.Clear();

            Order(none, false, backpack: true).Should().Contain(Layer.Backpack).And.HaveCount(PaperdollOrder.N - 1);
        }

        [Fact]
        public void Female_And_Gargoyle_Torsos_Draw_Under_The_Shirt_But_Only_On_The_Alternate_Table()
        {
            Span<ushort> a = stackalloc ushort[PaperdollOrder.N];
            a.Clear();
            a[(int)Layer.Torso] = 0x399;

            Layer[] male = Order(a, false);
            Layer[] female = Order(a, true);

            Array.IndexOf(male, Layer.Torso).Should().BeGreaterThan(Array.IndexOf(male, Layer.Shirt));
            Array.IndexOf(female, Layer.Torso).Should().BeLessThan(Array.IndexOf(female, Layer.Shirt));

            a[(int)Layer.Torso] = 0x39E;
            Layer[] outOfRange = Order(a, true);
            Array.IndexOf(outOfRange, Layer.Torso).Should().BeGreaterThan(Array.IndexOf(outOfRange, Layer.Shirt), "0x39E is past the 0x399..0x39D range");
        }

        [Fact]
        public void Arms_Alternates_Win_Over_The_Torso_Rules()
        {
            Span<ushort> a = stackalloc ushort[PaperdollOrder.N];
            a.Clear();
            a[(int)Layer.Arms] = 0x3DD;
            a[(int)Layer.Torso] = 0x399;

            Layer[] order = Order(a, true);

            Array.IndexOf(order, Layer.Arms).Should().BeGreaterThan(Array.IndexOf(order, Layer.Torso), "the arms-late table, not the female one");
            Array.IndexOf(order, Layer.Torso).Should().BeGreaterThan(Array.IndexOf(order, Layer.Shirt));
        }

        [Fact]
        public void A_Tunic_Of_0x238_Draws_Right_After_The_Waist_And_Pants_Of_0x1EB_Go_Under_The_Shoes()
        {
            Span<ushort> a = stackalloc ushort[PaperdollOrder.N];
            a.Clear();
            a[(int)Layer.Tunic] = 0x238;
            a[(int)Layer.Pants] = 0x1EB;

            Layer[] order = Order(a, false);

            Array.IndexOf(order, Layer.Tunic).Should().Be(Array.IndexOf(order, Layer.Waist) + 1);
            Array.IndexOf(order, Layer.Shoes).Should().BeLessThan(Array.IndexOf(order, Layer.Pants));
        }

        [Fact]
        public void A_Quiver_Cloak_Draws_Right_After_The_Robe()
        {
            Span<ushort> a = stackalloc ushort[PaperdollOrder.N];
            a.Clear();
            Array.IndexOf(Order(a, false), Layer.Cloak).Should().Be(0);

            a[(int)Layer.Cloak] = 0x380;
            Layer[] order = Order(a, false);

            Array.IndexOf(order, Layer.Cloak).Should().Be(Array.IndexOf(order, Layer.Robe) + 1);
        }

        [Fact]
        public void Anim_Group_Input_Reads_The_Selection_Flags_And_Hands_From_The_Components()
        {
            var s = new AnimationState
            {
                ServerGroup = 7,
                Flags = AnimStateFlags.FromServer | AnimStateFlags.WarMode | AnimStateFlags.Mounted | AnimStateFlags.Dead | AnimStateFlags.StepRun
            };
            var eq = new EquipmentSlots
            {
                Hand1 = new HandItem { Present = true, AnimId = 5 },
                Hand2 = new HandItem { Present = true, IsLight = true, AnimId = 6 }
            };

            AnimGroupInput i = AnimGroupInput.From(in s, in eq);

            i.ServerGroup.Should().Be(7);
            i.FromServer.Should().BeTrue();
            i.WarMode.Should().BeTrue();
            i.Mounted.Should().BeTrue();
            i.Dead.Should().BeTrue();
            i.StepRun.Should().BeTrue();
            i.Walking.Should().BeFalse();
            i.Running.Should().BeFalse();
            i.HasSteps.Should().BeFalse();
            i.Flying.Should().BeFalse();
            i.GargoyleProp.Should().BeFalse();
            i.Hand1.AnimId.Should().Be(5);
            i.Hand2.IsLight.Should().BeTrue();
        }
    }
}
