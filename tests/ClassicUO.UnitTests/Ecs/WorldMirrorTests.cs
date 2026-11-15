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
        public void Items_Of_A_Dying_Mobile_Stay_Worn_After_The_Death_Rekey()
        {
            // PacketHandlers.DisplayDeath re-keys the mobile to serial | 0x80000000 and repoints its items; MobileView keeps
            // drawing them during the death animation, so the slots must still find them (was: ContainedIn, no slots)
            const uint DYING = MOBILE | 0x8000_0000;
            var mirror = new WorldMirror();
            mirror.UpsertMobile(MOBILE, 0x190, 0, 100, 200, 0, 2, 0, false);
            mirror.UpsertItem(ITEM, 0x203B, 0x44E, 100, 200, 0, 0, 0, 1, MOBILE, 11, false);
            mirror.Remove(MOBILE);
            mirror.UpsertMobile(DYING, 0x190, 0, 100, 200, 0, 2, 0, false);
            mirror.TryGetEntity(DYING, out var dying).Should().BeTrue();
            dying.Tags.Has<EquipmentDirty>().Should().BeTrue();
            mirror.UpsertItem(ITEM, 0x203B, 0x44E, 100, 200, 0, 0, 0, 1, DYING, 11, false);

            mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue();
            r.ParentKind.Should().Be(ParentKind.Equipped);
            r.Parent.Should().Be(DYING);
            mirror.GetEquipment(DYING).Count.Should().Be(1);
            WorldMirror.IsWearerSerial(DYING).Should().BeTrue();
            WorldMirror.IsWearerSerial(ITEM).Should().BeFalse();
            WorldMirror.IsWearerSerial(ITEM | 0x8000_0000).Should().BeFalse();
            WorldMirror.IsWearerSerial(0x8000_0000).Should().BeFalse();
        }

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
            var extract = new MirrorSpriteExtract(mirror.Store);

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

            foreach (MirrorSprite s in sprites)
            {
                if (s.Kind == MirrorSpriteKind.Player)
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
