using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // mount_swap (mount-fix): a matched dismount (the Mount-layer item leaves, one mobile of the mount's body is created beside
    // the rider within the window) hides the horse until both looks are drawable or the bound passes; anything else is
    // untouched. asset_prefetch_on_packet notes the wearer when a worn item leaves it.
    public partial class WorldWriterMobileTests
    {
        private const ushort ETHEREAL_BODY = 0x00C0;
        private const uint HORSE2 = 0x0000_3003;

        private static Mobile Spawn(World world, uint serial, ushort body, ushort x = 1000, ushort y = 1000)
        {
            var p = new MobPacket { Serial = serial, Graphic = body, X = x, Y = y, Z = 0, Direction = Direction.North };

            return NewUpsertMobile(world, in p);
        }

        private static void RunSwap(bool on, System.Action<World, TestPlayer> body)
        {
            MountSwap.OverrideForTests = on;
            bool complete = false;
            MountSwap.LookCompleteForTests = _ => complete;

            try
            {
                World world = BuildRider(out TestPlayer p);
                EquipMount(world, ETHEREAL);
                body(world, p);
            }
            finally
            {
                MountSwap.OverrideForTests = null;
                MountSwap.LookCompleteForTests = null;
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Matched_Dismount_Hides_The_Horse_Until_Both_Looks_Are_Complete(bool on)
        {
            MountSwap.OverrideForTests = on;
            bool complete = false;
            MountSwap.LookCompleteForTests = _ => complete;

            try
            {
                World world = BuildRider(out TestPlayer p);
                EquipMount(world, ETHEREAL);

                Mobile horse = Spawn(world, NEWMOB, ETHEREAL_BODY);
                world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);
                world.MountSwap.Frame();

                world.MountSwap.Active.Should().Be(on);
                horse.PairHidden.Should().Be(on);
                horse.AlphaSnap.Should().Be(on || !MobileFadeIn.Enabled, "a matched horse is drawn at its target alpha at once");

                Time.Ticks += 5;
                world.MountSwap.Frame();
                horse.PairHidden.Should().Be(on, "neither look is complete yet");

                complete = true;
                Time.Ticks += 5;
                world.MountSwap.Frame();
                horse.PairHidden.Should().BeFalse();
                world.MountSwap.Active.Should().BeFalse();
                world.MountSwap.Completed.Should().Be(on ? 1 : 0);
            }
            finally
            {
                MountSwap.OverrideForTests = null;
                MountSwap.LookCompleteForTests = null;
            }
        }

        [Fact]
        public void Matched_Dismount_Hold_Ends_At_The_Bound() =>
            RunSwap(true, (world, p) =>
            {
                Mobile horse = Spawn(world, NEWMOB, ETHEREAL_BODY);
                world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);
                world.MountSwap.Frame();
                horse.PairHidden.Should().BeTrue();

                Time.Ticks += MountSwap.HoldMaxMs + 1;
                world.MountSwap.Frame();
                horse.PairHidden.Should().BeFalse();
                world.MountSwap.TimedOut.Should().Be(1);
            });

        [Fact]
        public void Two_Candidates_Are_Not_Matched() =>
            RunSwap(true, (world, p) =>
            {
                Mobile a = Spawn(world, NEWMOB, ETHEREAL_BODY);
                Mobile b = Spawn(world, HORSE2, ETHEREAL_BODY, 1001, 1000);
                world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);
                world.MountSwap.Frame();

                world.MountSwap.Active.Should().BeFalse();
                world.MountSwap.Ambiguous.Should().Be(1);
                a.PairHidden.Should().BeFalse();
                b.PairHidden.Should().BeFalse();
            });

        [Fact]
        public void Other_Body_Far_Away_Or_Late_Is_Not_Matched() =>
            RunSwap(true, (world, p) =>
            {
                Spawn(world, NEWMOB, 0x00C8);
                Spawn(world, HORSE2, ETHEREAL_BODY, 1005, 1000);
                world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);
                world.MountSwap.Frame();
                world.MountSwap.Active.Should().BeFalse();

                // a horse created after the window
                Time.Ticks += MountSwap.MatchWindowMs + 5;
                Mobile late = Spawn(world, 0x0000_3004, ETHEREAL_BODY);
                world.MountSwap.Frame();
                world.MountSwap.Active.Should().BeFalse();
                late.PairHidden.Should().BeFalse();
            });

        [Fact]
        public void Ethereal_Dismount_Without_A_Horse_Holds_Nothing() =>
            RunSwap(true, (world, p) =>
            {
                world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);
                world.MountSwap.Frame();
                world.MountSwap.Active.Should().BeFalse();
                world.MountSwap.Matched.Should().Be(0);
            });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Worn_Item_Leaving_Notes_The_Wearer(bool into)
        {
            LookPrefetch.OverrideForTests = true;
            LookPrefetch.ResetForTests();

            try
            {
                World world = BuildRider(out TestPlayer p);
                EquipMount(world, ETHEREAL);

                while (LookPrefetch.TryTake(out _, out _))
                {
                }

                if (into)
                {
                    world.Writer.PlaceInContainer(MOUNT_ITEM, ETHEREAL, 1, 10, 10, 0, BAG, out _);
                }
                else
                {
                    world.Writer.RemoveWorldItem(world.Items.Get(MOUNT_ITEM), out _);
                }

                bool noted = false;

                while (LookPrefetch.TryTake(out uint serial, out LookPrefetch.Kind kind))
                {
                    noted |= serial == p.Serial && kind == LookPrefetch.Kind.Mobile;
                }

                noted.Should().BeTrue("the rider's on-foot look is requested at the dismount");
            }
            finally
            {
                LookPrefetch.OverrideForTests = null;
                LookPrefetch.ResetForTests();
            }
        }

        [Fact]
        public void War_Mode_Notes_The_Mobile()
        {
            LookPrefetch.OverrideForTests = true;
            LookPrefetch.ResetForTests();

            try
            {
                World world = BuildRider(out TestPlayer p);
                world.Writer.SetWarMode(p, true);
                LookPrefetch.TryTake(out uint serial, out LookPrefetch.Kind kind).Should().BeTrue();
                serial.Should().Be(p.Serial);
                kind.Should().Be(LookPrefetch.Kind.Mobile);
            }
            finally
            {
                LookPrefetch.OverrideForTests = null;
                LookPrefetch.ResetForTests();
            }
        }
    }
}
