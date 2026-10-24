using ClassicUO.Ecs;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    public class WorldMirrorTests
    {
        private const uint MOBILE = 0x0000_1000;
        private const uint ITEM = 0x4000_2000;
        private const uint BAG = 0x4000_3000;

        [Fact]
        public void Item_Moves_Between_Ground_Equipment_And_Container()
        {
            var mirror = new WorldMirror();

            mirror.UpsertItem(ITEM, 0x1517, 0x21, 100, 200, 5, 0, 0x20, 1, WorldMirror.NO_CONTAINER, 0, false);
            mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue();
            r.OnGround.Should().BeTrue();
            r.ParentKind.Should().Be(ParentKind.None);

            mirror.UpsertItem(ITEM, 0x1517, 0x21, 100, 200, 5, 0, 0x20, 1, MOBILE, 5, false);
            mirror.TryGet(ITEM, out r);
            r.OnGround.Should().BeFalse();
            r.ParentKind.Should().Be(ParentKind.Equipped);
            r.Parent.Should().Be(MOBILE);
            r.Layer.Should().Be(5);
            mirror.GetEquipment(MOBILE).Count.Should().Be(1);

            mirror.UpsertItem(ITEM, 0x1517, 0x21, 40, 60, 0, 0, 0x20, 3, BAG, 0, false);
            mirror.TryGet(ITEM, out r);
            r.ParentKind.Should().Be(ParentKind.Contained);
            r.Parent.Should().Be(BAG);
            r.Amount.Should().Be(3);
            mirror.GetEquipment(MOBILE).Count.Should().Be(0);
            mirror.GetContents(BAG).Count.Should().Be(1);

            mirror.UpsertItem(ITEM, 0x1517, 0x21, 101, 201, 0, 0, 0x20, 3, WorldMirror.NO_CONTAINER, 0, false);
            mirror.TryGet(ITEM, out r);
            r.OnGround.Should().BeTrue();
            mirror.GetContents(BAG).Count.Should().Be(0);
        }

        [Fact]
        public void Remove_And_Counts()
        {
            var mirror = new WorldMirror();
            mirror.UpsertMobile(MOBILE, 0x190, 0, 10, 10, 0, 2, 0, true);
            mirror.UpsertItem(ITEM, 0x1517, 0, 0, 0, 0, 0, 0, 1, MOBILE, 5, false);

            mirror.MobileCount.Should().Be(1);
            mirror.ItemCount.Should().Be(1);
            mirror.TryGet(MOBILE, out MirrorRecord r);
            r.IsPlayer.Should().BeTrue();

            mirror.Remove(MOBILE).Should().BeTrue();
            mirror.Remove(MOBILE).Should().BeFalse();
            mirror.MobileCount.Should().Be(0);
            mirror.Count.Should().Be(1);

            mirror.Clear();
            mirror.Count.Should().Be(0);
            mirror.ItemCount.Should().Be(0);
        }

        [Fact]
        public void Extract_Returns_Ground_Items_And_Mobiles_In_Range()
        {
            var mirror = new WorldMirror();
            var extract = new DynamicSpriteExtract(mirror.Store);

            mirror.UpsertMobile(MOBILE, 0x190, 0x33, 100, 100, 0, 3, 0, true);
            mirror.UpsertMobile(MOBILE + 1, 0x191, 0, 105, 95, 0, 1, 0, false);
            mirror.UpsertMobile(MOBILE + 2, 0x191, 0, 200, 200, 0, 1, 0, false);
            mirror.UpsertItem(ITEM, 0x0EED, 0, 101, 100, 0, 0, 0, 5, WorldMirror.NO_CONTAINER, 0, false);
            mirror.UpsertItem(ITEM + 1, 0x1517, 0, 0, 0, 0, 0, 0, 1, MOBILE, 5, false);
            mirror.UpsertItem(ITEM + 2, 0x1517, 0, 0, 0, 0, 0, 0, 1, BAG, 0, false);

            var sprites = extract.Run(100, 100, 18);

            sprites.Length.Should().Be(3);
            extract.ItemCount.Should().Be(1);
            extract.MobileCount.Should().Be(2);

            int players = 0;

            foreach (DynamicSprite s in sprites)
            {
                if (s.Kind == DynamicSpriteKind.Player)
                {
                    players++;
                    s.Serial.Should().Be(MOBILE);
                    s.Hue.Should().Be(0x33);
                    s.Facing.Should().Be(3);
                }
            }

            players.Should().Be(1);
        }
    }
}
