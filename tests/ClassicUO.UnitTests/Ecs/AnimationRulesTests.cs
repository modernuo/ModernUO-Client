using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    public class AnimationRulesTests
    {
        [Theory]
        [InlineData(0, 3, true)]
        [InlineData(1, 2, true)]
        [InlineData(2, 1, true)]
        [InlineData(4, 1, false)]
        [InlineData(5, 2, false)]
        [InlineData(6, 3, false)]
        public void MapDirection_Mirrors_West_Facing_Directions(byte uoDir, byte stored, bool mirror)
        {
            foreach (bool before in new[] { false, true })
            {
                byte d = uoDir;
                bool m = before;
                AnimationRules.MapDirection(ref d, ref m);
                d.Should().Be(stored);
                m.Should().Be(mirror);
            }
        }

        [Theory]
        [InlineData(3, 0)]
        [InlineData(7, 4)]
        public void MapDirection_Keeps_The_Previous_Mirror_For_3_And_7(byte uoDir, byte stored)
        {
            foreach (bool before in new[] { false, true })
            {
                byte d = uoDir;
                bool m = before;
                AnimationRules.MapDirection(ref d, ref m);
                d.Should().Be(stored);
                m.Should().Be(before, "GetAnimDirection leaves the mirror untouched, so the ECS keeps it as state");
            }
        }

        [Fact]
        public void MapDirection_Leaves_Run_Flagged_Directions_Out_Of_Range()
        {
            byte d = 0x82;
            bool m = false;
            AnimationRules.MapDirection(ref d, ref m);
            d.Should().Be(0x82);
        }

        [Fact]
        public void Cloak_Order_Matches_PaperdollOrder_For_Every_Direction()
        {
            Layer[] baseOrder = { Layer.Cloak, Layer.Shirt, Layer.Pants, Layer.Torso, Layer.Hair, Layer.Helmet, Layer.OneHanded };

            for (byte dir = 0; dir < 8; dir++)
            {
                Span<Layer> expected = (Layer[])baseOrder.Clone();
                int n = PaperdollOrder.ApplyDirectionCloak(expected, baseOrder.Length, dir);

                Span<byte> order = stackalloc byte[baseOrder.Length];
                Span<byte> layerOf = stackalloc byte[baseOrder.Length];

                for (int i = 0; i < baseOrder.Length; i++)
                {
                    order[i] = (byte)i;
                    layerOf[i] = (byte)baseOrder[i];
                }

                int got = AnimationRules.ApplyDirectionCloak(order, baseOrder.Length, layerOf, dir);
                got.Should().Be(n);

                for (int i = 0; i < n; i++)
                {
                    ((Layer)layerOf[order[i]]).Should().Be(expected[i], $"dir {dir} index {i}");
                }
            }
        }

        [Theory]
        [InlineData(0, 6)] // facing north: cloak painted last
        [InlineData(3, 0)] // facing the viewer: cloak painted first
        [InlineData(2, 3)] // otherwise just below the helmet (index 3 once the cloak left index 0)
        public void Cloak_Position_By_Facing(byte dir, int cloakIndex)
        {
            Layer[] layers = { Layer.Cloak, Layer.Shirt, Layer.Pants, Layer.Hair, Layer.Helmet, Layer.OneHanded, Layer.Torso };
            Span<byte> order = stackalloc byte[layers.Length];
            Span<byte> layerOf = stackalloc byte[layers.Length];

            for (int i = 0; i < layers.Length; i++)
            {
                order[i] = (byte)i;
                layerOf[i] = (byte)layers[i];
            }

            AnimationRules.ApplyDirectionCloak(order, layers.Length, layerOf, dir);
            ((Layer)layerOf[order[cloakIndex]]).Should().Be(Layer.Cloak);
        }

        [Fact]
        public void Equipment_Order_Uses_The_Arms_Late_Table_For_Bone_Arms()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            gfx.Clear();
            gfx[(int)Layer.Arms] = 0x210;
            gfx[(int)Layer.Torso] = 0x1;
            Span<Layer> order = stackalloc Layer[PaperdollOrder.N];

            PaperdollOrder.Build(gfx, false, order);

            order.IndexOf(Layer.Arms).Should().BeGreaterThan(order.IndexOf(Layer.Torso));

            gfx[(int)Layer.Arms] = 0x1;
            PaperdollOrder.Build(gfx, false, order);
            order.IndexOf(Layer.Arms).Should().BeLessThan(order.IndexOf(Layer.Torso));
        }

        [Theory]
        [InlineData(0x3EB4, -9)]  // unicorn
        [InlineData(0x3EC7, 18)]  // tiger
        [InlineData(0x3EDE, -10)] // clydesdale
        [InlineData(0x3EA2, 0)]   // dark brown horse
        [InlineData(0x1234, 0)]   // not a mount
        public void Mount_Rider_Offset(ushort mountItem, int offset)
        {
            AnimationRules.MountOffsetY(mountItem).Should().Be((sbyte)offset);
        }

        [Fact]
        public void Mount_Body()
        {
            AnimationRules.MountBody(0x3E9B, 0).Should().Be(0x00C0, "ethereal unicorn");
            AnimationRules.MountBody(0x3E9C, 0x1234).Should().Be(0x00BF, "ethereal kirin ignores the AnimID");
            AnimationRules.MountBody(0x3EA2, 0).Should().Be(0x00CC, "Mounts table");
            AnimationRules.MountBody(0x3EA2, 0x00E4).Should().Be(0x00E4, "a tiledata AnimID wins");
            AnimationRules.MountBody(0x1234, 0).Should().Be(0x1234);
        }

        [Fact]
        public void Body_For_Animation_Maps_Ghosts_And_Gargoyle_Ghosts()
        {
            AnimationRules.BodyForAnimation(0x0192).Should().Be(0x0190);
            AnimationRules.BodyForAnimation(0x0193).Should().Be(0x0191);
            AnimationRules.BodyForAnimation(0x02B6).Should().Be(667);
            AnimationRules.BodyForAnimation(0x02B7).Should().Be(666);
            AnimationRules.BodyForAnimation(0x0190).Should().Be(0x0190);
        }

        [Fact]
        public void Gargoyle_Tests_Keep_The_Legacy_Precedence()
        {
            AnimationRules.IsGargoyleDrawBody(666, true).Should().BeTrue();
            AnimationRules.IsGargoyleDrawBody(666, false).Should().BeFalse();
            AnimationRules.IsGargoyleProp(0x029B, false).Should().BeTrue("Mobile.IsGargoyle: '&&' binds tighter than '||'");
            AnimationRules.IsGargoyleProp(0x029A, false).Should().BeFalse();
            AnimationRules.FixGargoyleEquipment(0x01D5).Should().Be(0x0156);
            AnimationRules.FixGargoyleEquipment(1529).Should().Be(1531);
            AnimationRules.FixGargoyleEquipment(0x0200).Should().Be(0x0200);
        }

        [Fact]
        public void Strips_Split_At_The_Feet()
        {
            Span<(int Y, int H)> s = stackalloc (int, int)[64];

            AnimationRules.Strips(60, -10, 0, s).Should().Be(2);
            s[0].Should().Be((0, 50));
            s[1].Should().Be((50, 10));

            // a mount offset of -9 moves the split 9 px lower
            AnimationRules.Strips(60, -10, -9, s).Should().Be(2);
            s[0].Should().Be((0, 59));
            s[1].Should().Be((59, 1));

            // feet at the very top: one-pixel first strip, then the rest, then empty strips
            int n = AnimationRules.Strips(60, -60, 0, s);
            n.Should().Be(61 > s.Length ? s.Length : 61);
            s[0].Should().Be((0, 1));
            s[1].Should().Be((1, 59));
            s[2].Should().Be((60, 0));
            AnimationRules.MaxStrips(60).Should().Be(61);
        }

        [Fact]
        public void Layer_Hue_Rules()
        {
            AnimationRules.LayerHue(0x0035, 0x8021, false, true, true, 0, 0, out bool partial).Should().Be(0x0035);
            partial.Should().BeFalse("an overriding hue is never partial");

            AnimationRules.LayerHue(0, 0x8021, false, false, false, 0, 0, out partial).Should().Be(0x0021);
            partial.Should().BeTrue("0x8000 asks for a partial hue");

            AnimationRules.LayerHue(0, 0x0021, false, true, true, 0, 0, out partial).Should().Be(0x0021);
            partial.Should().BeTrue("tiledata partial-hue item");

            AnimationRules.LayerHue(0, 0x0021, true, true, true, 0, 0, out partial).Should().Be(0x0021);
            partial.Should().BeFalse("the mount is never partial");

            AnimationRules.LayerHue(0, 0, false, true, true, 0x0444, 0x0555, out partial).Should().Be(0x0444);
            partial.Should().BeFalse("the body file's hue comes before the equipconv colour");

            AnimationRules.LayerHue(0, 0, false, true, true, 0, 0x0555, out _).Should().Be(0x0555);
            AnimationRules.LayerHue(0, 0x4001, false, false, false, 0, 0, out _).Should().Be(0x4001, "spectral passes through");
        }

        [Fact]
        public void Override_Hue_Precedence()
        {
            var i = new AnimationRules.OverrideHueInput { NotorietyHue = 0x0059, PoisonHue = 0x0044, ParalyzedHue = 0x014C, InvulnerableHue = 0x0030 };
            AnimationRules.OverrideHue(in i).Should().Be(0);

            i.Hidden = true;
            AnimationRules.OverrideHue(in i).Should().Be(0x038E);

            i.HighlightSelected = true;
            AnimationRules.OverrideHue(in i).Should().Be(Constants.HIGHLIGHT_CURRENT_OBJECT_HUE);

            i = new AnimationRules.OverrideHueInput { Dead = true, Human = false };
            AnimationRules.OverrideHue(in i).Should().Be(0x0386, "dead non-human");
            i.Human = true;
            AnimationRules.OverrideHue(in i).Should().Be(0);

            i = new AnimationRules.OverrideHueInput { PoisonHighlight = true, Poisoned = true, PoisonHue = 0x44, ParalyzeHighlight = true, Paralyzed = true, ParalyzedHue = 0x14C };
            AnimationRules.OverrideHue(in i).Should().Be(0x14C, "paralysis is checked after poison");

            i.IsLastAttack = true;
            i.NotorietyHue = 0x22;
            AnimationRules.OverrideHue(in i).Should().Be(0x22, "the attack target shows its notoriety");
            i.IsPlayer = true;
            AnimationRules.OverrideHue(in i).Should().Be(0x14C, "not on the player");
        }

        [Fact]
        public void Covered_Layers()
        {
            Span<ushort> gfx = stackalloc ushort[PaperdollOrder.N];
            Span<ushort> anim = stackalloc ushort[PaperdollOrder.N];
            Span<bool> present = stackalloc bool[PaperdollOrder.N];
            gfx.Clear();
            anim.Clear();
            present.Clear();

            void Put(Layer l, ushort g, ushort a, Span<ushort> gs, Span<ushort> an, Span<bool> pr)
            {
                gs[(int)l] = g;
                an[(int)l] = a;
                pr[(int)l] = true;
            }

            Put(Layer.Torso, 0x1415, 0x0001, gfx, anim, present);
            AnimationRules.IsCovered(Layer.Torso, gfx, anim, present, 0x190).Should().BeFalse();

            // a full robe hides torso and arms; an open robe doesn't
            Put(Layer.Robe, 0x1F03, 0x01DF, gfx, anim, present);
            AnimationRules.IsCovered(Layer.Torso, gfx, anim, present, 0x190).Should().BeTrue();
            AnimationRules.IsCovered(Layer.Arms, gfx, anim, present, 0x190).Should().BeTrue();
            Put(Layer.Robe, 0x9985, 0x01DF, gfx, anim, present);
            AnimationRules.IsCovered(Layer.Torso, gfx, anim, present, 0x190).Should().BeFalse();

            // hood robes hide hair; the 0x2683 range on every body, the 0xA0AB range except on gargoyles
            Put(Layer.Hair, 0x203B, 0x0001, gfx, anim, present);
            Put(Layer.Robe, 0x2683, 0x01DF, gfx, anim, present);
            AnimationRules.IsCovered(Layer.Hair, gfx, anim, present, 0x190).Should().BeTrue();
            AnimationRules.IsCovered(Layer.Hair, gfx, anim, present, 0x29A).Should().BeTrue();
            Put(Layer.Robe, 0xA0AB, 0x01DF, gfx, anim, present);
            AnimationRules.IsCovered(Layer.Hair, gfx, anim, present, 0x190).Should().BeTrue();
            AnimationRules.IsCovered(Layer.Hair, gfx, anim, present, 0x29A).Should().BeFalse();

            // plate legs over shoes
            present[(int)Layer.Robe] = false;
            Put(Layer.Shoes, 0x170F, 0x0001, gfx, anim, present);
            AnimationRules.IsCovered(Layer.Shoes, gfx, anim, present, 0x190).Should().BeFalse();
            Put(Layer.Legs, 0x1411, 0x0002, gfx, anim, present);
            AnimationRules.IsCovered(Layer.Shoes, gfx, anim, present, 0x190).Should().BeTrue();
        }

        [Fact]
        public void Timing_Loops_A_Normal_Action()
        {
            var t = new AnimationTimer { Frame = 1 };
            AnimationTiming.Advance(ref t, 5, out AnimationAdvance r).Should().Be(Constants.CHARACTER_ANIMATION_DELAY);
            t.Frame.Should().Be(2);
            r.Should().Be(AnimationAdvance.Continue);

            t.Frame = 4;
            AnimationTiming.Advance(ref t, 5, out r);
            t.Frame.Should().Be(0);
            r.Should().Be(AnimationAdvance.Wrapped);
        }

        [Fact]
        public void Timing_Server_Action_Plays_Its_Repeats_Then_Finishes()
        {
            var t = new AnimationTimer { FromServer = true, Forward = true, Interval = 1, RepeatMode = 2, Frame = 3 };

            AnimationTiming.Advance(ref t, 5, out AnimationAdvance r).Should().Be(80 + 80 * 2, "delay grows with the server interval");
            t.FrameCount.Should().Be(5);
            t.Frame.Should().Be(4);
            r.Should().Be(AnimationAdvance.Continue);

            AnimationTiming.Advance(ref t, 5, out r);
            t.Frame.Should().Be(0);
            t.RepeatMode.Should().Be(1);
            r.Should().Be(AnimationAdvance.Continue);

            t.Frame = 4;
            AnimationTiming.Advance(ref t, 5, out r);
            r.Should().Be(AnimationAdvance.Finished);
        }

        [Fact]
        public void Timing_Server_Action_Repeat_Flag_And_Infinite()
        {
            var t = new AnimationTimer { FromServer = true, Forward = true, RepeatMode = 1, Repeat = true, Frame = 4 };
            AnimationTiming.Advance(ref t, 5, out AnimationAdvance r);
            r.Should().Be(AnimationAdvance.Continue);
            t.Repeat.Should().BeFalse();

            t = new AnimationTimer { FromServer = true, Forward = true, RepeatMode = 0, Frame = 4 };
            AnimationTiming.Advance(ref t, 5, out r);
            r.Should().Be(AnimationAdvance.Continue, "repeat 0 plays forever");
            t.Frame.Should().Be(0);
        }

        [Fact]
        public void Timing_Server_Action_Backwards_And_FrameCount_Override()
        {
            var t = new AnimationTimer { FromServer = true, Forward = false, Frame = 0 };
            AnimationTiming.Advance(ref t, 5, out _);
            t.Frame.Should().Be(4, "backwards wraps to the last frame");

            t = new AnimationTimer { FromServer = true, Forward = true, FrameCount = 3, Frame = 2 };
            AnimationTiming.Advance(ref t, 5, out _);
            t.Frame.Should().Be(0, "the server's frame count ends the action early");
        }

        [Fact]
        public void Mirror_Marks_Equipment_Dirty_On_Equip_Change()
        {
            var mirror = new WorldMirror();
            const uint MOBILE = 0x0000_1000, SHIRT = 0x4000_0001;

            mirror.UpsertMobile(MOBILE, 0x190, 0, 10, 10, 0, 0, 0, false);
            mirror.TryGetEntity(MOBILE, out var m).Should().BeTrue();
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("new mobiles start dirty");
            m.HasComponent<AnimationState>().Should().BeTrue();
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(SHIRT, 0x1517, 0, 0, 0, 0, 0, 0, 1, MOBILE, (byte)Layer.Shirt, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("equip");
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(SHIRT, 0x1517, 0x21, 0, 0, 0, 0, 0, 1, MOBILE, (byte)Layer.Shirt, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("hue change of a worn item");
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertMobile(MOBILE, 0x190, 0, 11, 10, 0, 0, 0, false);
            m.Tags.Has<EquipmentDirty>().Should().BeFalse("moving doesn't touch the slots");

            mirror.UpsertMobile(MOBILE, 0x191, 0, 11, 10, 0, 0, 0, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("body change");
            m.RemoveTag<EquipmentDirty>();

            mirror.Remove(SHIRT);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("unequip by removal");
        }
    }
}
