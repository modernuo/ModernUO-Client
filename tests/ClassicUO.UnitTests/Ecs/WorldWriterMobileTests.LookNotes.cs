using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // mount-fix: asset_prefetch_on_packet notes the wearer when a worn item leaves it, and the mobile at a war-mode change.
    public partial class WorldWriterMobileTests
    {
        private static Mobile Spawn(World world, uint serial, ushort body, ushort x = 1000, ushort y = 1000)
        {
            var p = new MobPacket { Serial = serial, Graphic = body, X = x, Y = y, Z = 0, Direction = Direction.North };

            return NewUpsertMobile(world, in p);
        }

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
