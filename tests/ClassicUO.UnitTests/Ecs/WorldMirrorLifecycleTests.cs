using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using FluentAssertions;
using Friflo.Engine.ECS;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // WorldMirror's entity lifecycle, serial reuse, item/mobile re-keying, equipment index and EquipmentDirty tagging.
    public class WorldMirrorLifecycleTests
    {
        private const uint MOBILE = 0x0000_1000;
        private const uint OTHER = 0x0000_1001;
        private const uint ITEM = 0x4000_2000;
        private const uint BAG = 0x4000_3000;

        private static WorldMirror NewMirror(out Entity mobile, bool isPlayer = false)
        {
            var mirror = new WorldMirror();
            mobile = mirror.UpsertMobile(MOBILE, 0x190, 0, 10, 10, 0, 0, 0, isPlayer);
            mobile.RemoveTag<EquipmentDirty>();

            return mirror;
        }

        private static void Worn(WorldMirror m, uint item, uint wearer, Layer layer, ushort graphic = 0x1517, ushort hue = 0) =>
            m.UpsertItem(item, graphic, hue, 0, 0, 0, 0, 0, 1, wearer, (byte)layer, false);

        [Theory]
        [InlineData(0u, false)]
        [InlineData(1u, true)]
        [InlineData(0x3FFF_FFFFu, true)]
        [InlineData(0x4000_0000u, false)]
        [InlineData(0xFFFF_FFFFu, false)]
        public void Mobile_Serials_Are_1_To_0x3FFFFFFF(uint serial, bool mobile)
        {
            WorldMirror.IsMobileSerial(serial).Should().Be(mobile);
        }

        [Fact]
        public void Upserting_A_Mobile_Creates_One_Entity_And_Updating_Keeps_It()
        {
            var mirror = new WorldMirror();
            Entity first = mirror.UpsertMobile(MOBILE, 0x190, 0x44, 100, 200, -5, 3, 0x40, false);

            mirror.MobileCount.Should().Be(1);
            mirror.Count.Should().Be(1);

            Entity again = mirror.UpsertMobile(MOBILE, 0x190, 0x55, 101, 201, 4, 5, 0x00, false);

            again.Id.Should().Be(first.Id, "an update writes in place");
            mirror.MobileCount.Should().Be(1);
            mirror.Count.Should().Be(1);

            mirror.TryGet(MOBILE, out MirrorRecord r).Should().BeTrue();
            r.IsMobile.Should().BeTrue();
            r.Hue.Should().Be(0x55);
            r.X.Should().Be(101);
            r.Y.Should().Be(201);
            r.Z.Should().Be(4);
            r.Facing.Should().Be(5);
            r.Flags.Should().Be(0);
        }

        [Fact]
        public void Unknown_Serials_Are_Not_Found_And_Removing_Them_Is_Harmless()
        {
            var mirror = new WorldMirror();

            mirror.TryGet(MOBILE, out _).Should().BeFalse();
            mirror.TryGetEntity(MOBILE, out Entity e).Should().BeFalse();
            e.IsNull.Should().BeTrue();
            mirror.Remove(MOBILE).Should().BeFalse();
            mirror.MarkEquipmentDirty(MOBILE);
            mirror.Count.Should().Be(0);
            mirror.GetEquipment(MOBILE).Count.Should().Be(0, "no children for a parent nobody wears anything on");
        }

        [Fact]
        public void The_Player_Tag_Follows_The_Latest_Upsert()
        {
            var mirror = NewMirror(out Entity m);
            m.Tags.Has<PlayerTag>().Should().BeFalse();

            mirror.UpsertMobile(MOBILE, 0x190, 0, 10, 10, 0, 0, 0, true);
            m.Tags.Has<PlayerTag>().Should().BeTrue();
            mirror.TryGet(MOBILE, out MirrorRecord r);
            r.IsPlayer.Should().BeTrue();

            mirror.UpsertMobile(MOBILE, 0x190, 0, 10, 10, 0, 0, 0, false);
            m.Tags.Has<PlayerTag>().Should().BeFalse();
        }

        [Fact]
        public void A_New_Mobile_Starts_With_Its_Equipment_Dirty_And_No_Server_Action()
        {
            var mirror = new WorldMirror();
            Entity m = mirror.UpsertMobile(MOBILE, 0x190, 0, 10, 10, 0, 0, 0, false);

            m.Tags.Has<EquipmentDirty>().Should().BeTrue();
            m.GetComponent<AnimationState>().ServerGroup.Should().Be(0xFF, "0xFF = no server action");
            m.GetComponent<MobileRenderState>().Alpha.Should().Be(0xFF);
            m.GetComponent<EquipmentSlots>().Version.Should().Be(0, "never built");
        }

        // perf-7: the mobile draw paths (GpuWorldRenderer.TryDrawMobileEcs, LegacySpriteSink.DrawMobile) read AnimationState and
        // MobileRenderState through one EcsRead.Row after TakeDirty; the refs must be the components GetComponent returns, and a
        // write through them must land in the entity, with the dirty tag set or already taken
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void One_Data_Read_Gives_The_Same_Mobile_Components_As_GetComponent(bool dirty)
        {
            var mirror = new WorldMirror();
            Entity m = mirror.UpsertMobile(MOBILE, 0x190, 0, 10, 10, 0, 0, 0, false);
            mirror.UpsertMobile(OTHER, 0x191, 0, 11, 10, 0, 0, 0, false);

            if (!dirty)
            {
                m.RemoveTag<EquipmentDirty>();
            }

            ClassicUO.Game.MobileAnimationSystem.TakeDirty(m).Should().Be(dirty);
            EcsRow data = EcsRead.Row(m);
            ref AnimationState state = ref data.Get<AnimationState>();
            ref MobileRenderState render = ref data.Get<MobileRenderState>();

            System.Runtime.CompilerServices.Unsafe.AreSame(ref state, ref m.GetComponent<AnimationState>()).Should().BeTrue();
            System.Runtime.CompilerServices.Unsafe.AreSame(ref render, ref m.GetComponent<MobileRenderState>()).Should().BeTrue();

            state.ServerGroup = 7;
            render.Alpha = 0x80;
            m.GetComponent<AnimationState>().ServerGroup.Should().Be(7);
            m.GetComponent<MobileRenderState>().Alpha.Should().Be(0x80);
        }

        [Fact]
        public void Serial_Reuse_Gives_A_Fresh_Entity_Without_The_Old_State()
        {
            var mirror = NewMirror(out Entity old, isPlayer: true);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            old.GetComponent<AnimationState>().Frame = 7;

            mirror.Remove(MOBILE).Should().BeTrue();
            old.IsNull.Should().BeTrue("the removed entity is deleted");
            mirror.TryGetEntity(MOBILE, out _).Should().BeFalse();

            Entity fresh = mirror.UpsertMobile(MOBILE, 0x191, 0, 50, 60, 0, 1, 0, false);

            fresh.Tags.Has<PlayerTag>().Should().BeFalse("the new mobile isn't the old player");
            fresh.Tags.Has<EquipmentDirty>().Should().BeTrue();
            fresh.GetComponent<AnimationState>().Frame.Should().Be(0);
            mirror.MobileCount.Should().Be(1);

            // the worn item survives its wearer and is found again through the reused serial
            mirror.GetEquipment(MOBILE).Count.Should().Be(1);
        }

        [Fact]
        public void Removing_A_Mobile_Leaves_Its_Items_In_The_Mirror()
        {
            var mirror = NewMirror(out _);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);

            mirror.Remove(MOBILE);

            mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue("items have their own lifetime");
            r.ParentKind.Should().Be(ParentKind.Equipped);
            r.Parent.Should().Be(MOBILE);
            mirror.ItemCount.Should().Be(1);
            mirror.MobileCount.Should().Be(0);
        }

        [Fact]
        public void An_Item_Serial_Reused_By_A_Mobile_Replaces_The_Item_Entity()
        {
            var mirror = NewMirror(out Entity wearer);
            const uint SHARED = 0x0000_2222;
            Worn(mirror, SHARED, MOBILE, Layer.Shirt);
            mirror.ItemCount.Should().Be(1);
            wearer.RemoveTag<EquipmentDirty>();

            Entity rekeyed = mirror.UpsertMobile(SHARED, 0x190, 0, 1, 1, 0, 0, 0, false);

            mirror.ItemCount.Should().Be(0);
            mirror.MobileCount.Should().Be(2);
            mirror.Count.Should().Be(2);
            rekeyed.Tags.Has<MobileTag>().Should().BeTrue();
            rekeyed.HasComponent<EquippedOn>().Should().BeFalse("no leftover item components");
            mirror.GetEquipment(MOBILE).Count.Should().Be(0, "the old item is no longer worn");
            wearer.Tags.Has<EquipmentDirty>().Should().BeTrue("a worn item vanished from its wearer");
        }

        [Fact]
        public void A_Mobile_Serial_Reused_By_An_Item_Replaces_The_Mobile_Entity()
        {
            var mirror = NewMirror(out _);

            mirror.UpsertItem(MOBILE, 0x1517, 0, 5, 5, 0, 0, 0, 2, WorldMirror.NO_CONTAINER, 0, false);

            mirror.MobileCount.Should().Be(0);
            mirror.ItemCount.Should().Be(1);
            mirror.TryGet(MOBILE, out MirrorRecord r).Should().BeTrue();
            r.IsMobile.Should().BeFalse();
            r.OnGround.Should().BeTrue();
            r.Amount.Should().Be(2);
        }

        [Fact]
        public void Equipment_Index_Is_Keyed_By_Container_And_Layer_Is_Kept()
        {
            var mirror = NewMirror(out _);
            mirror.UpsertMobile(OTHER, 0x190, 0, 1, 1, 0, 0, 0, false);

            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            Worn(mirror, ITEM + 1, MOBILE, Layer.Pants);
            Worn(mirror, ITEM + 2, OTHER, Layer.Shirt);

            mirror.GetEquipment(MOBILE).Count.Should().Be(2);
            mirror.GetEquipment(OTHER).Count.Should().Be(1);

            var layers = new List<byte>();

            foreach (Entity e in mirror.GetEquipment(MOBILE))
            {
                layers.Add(e.GetComponent<EquippedOn>().Layer);
            }

            layers.Should().BeEquivalentTo(new[] { (byte)Layer.Shirt, (byte)Layer.Pants });
        }

        [Fact]
        public void Equipment_Of_An_Absent_Mobile_Is_Indexed_And_Its_Mobile_Starts_Dirty_When_It_Arrives()
        {
            var mirror = new WorldMirror();

            Worn(mirror, ITEM, MOBILE, Layer.Shirt);

            mirror.GetEquipment(MOBILE).Count.Should().Be(1, "children may arrive before their parent");
            mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue();
            r.ParentKind.Should().Be(ParentKind.Equipped);

            Entity m = mirror.UpsertMobile(MOBILE, 0x190, 0, 1, 1, 0, 0, 0, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue();
        }

        [Fact]
        public void Equipping_Marks_The_Wearer_Dirty_And_Containers_And_Ground_Do_Not()
        {
            var mirror = NewMirror(out Entity m);

            mirror.UpsertItem(ITEM, 0x1517, 0, 0, 0, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
            mirror.UpsertItem(ITEM + 1, 0x1517, 0, 0, 0, 0, 0, 0, 1, BAG, 0, false);
            m.Tags.Has<EquipmentDirty>().Should().BeFalse("ground and bag items aren't equipment");

            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue();
        }

        [Fact]
        public void Unequipping_To_The_Ground_Or_A_Bag_Marks_The_Old_Wearer_Dirty()
        {
            var mirror = NewMirror(out Entity m);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            Worn(mirror, ITEM + 1, MOBILE, Layer.Pants);
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(ITEM, 0x1517, 0, 5, 5, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("dropped");
            m.RemoveTag<EquipmentDirty>();

            mirror.UpsertItem(ITEM + 1, 0x1517, 0, 0, 0, 0, 0, 0, 1, BAG, 0, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("put in a bag");
            mirror.GetEquipment(MOBILE).Count.Should().Be(0);
        }

        [Fact]
        public void Moving_A_Worn_Item_Between_Mobiles_Marks_Both_Dirty()
        {
            var mirror = NewMirror(out Entity a);
            Entity b = mirror.UpsertMobile(OTHER, 0x190, 0, 1, 1, 0, 0, 0, false);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            a.RemoveTag<EquipmentDirty>();
            b.RemoveTag<EquipmentDirty>();

            Worn(mirror, ITEM, OTHER, Layer.Shirt);

            a.Tags.Has<EquipmentDirty>().Should().BeTrue();
            b.Tags.Has<EquipmentDirty>().Should().BeTrue();
            mirror.GetEquipment(MOBILE).Count.Should().Be(0);
            mirror.GetEquipment(OTHER).Count.Should().Be(1);
        }

        [Fact]
        public void Changing_The_Layer_Of_A_Worn_Item_Updates_The_Index_And_Marks_Dirty()
        {
            var mirror = NewMirror(out Entity m);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            m.RemoveTag<EquipmentDirty>();

            Worn(mirror, ITEM, MOBILE, Layer.Tunic);

            m.Tags.Has<EquipmentDirty>().Should().BeTrue();
            mirror.TryGet(ITEM, out MirrorRecord r);
            r.Layer.Should().Be((byte)Layer.Tunic);
            mirror.GetEquipment(MOBILE).Count.Should().Be(1);
        }

        [Fact]
        public void Graphic_And_Hue_Changes_Of_A_Worn_Item_Mark_The_Wearer_Dirty()
        {
            var mirror = NewMirror(out Entity m);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt, 0x1517, 0x10);
            m.RemoveTag<EquipmentDirty>();

            Worn(mirror, ITEM, MOBILE, Layer.Shirt, 0x1518, 0x10);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("graphic");
            m.RemoveTag<EquipmentDirty>();

            Worn(mirror, ITEM, MOBILE, Layer.Shirt, 0x1518, 0x20);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue("hue");
        }

        [Fact]
        public void Deleting_A_Worn_Item_Marks_The_Wearer_Dirty_And_Deleting_A_Bag_Item_Does_Not()
        {
            var mirror = NewMirror(out Entity m);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            mirror.UpsertItem(ITEM + 1, 0x1517, 0, 0, 0, 0, 0, 0, 1, BAG, 0, false);
            mirror.UpsertItem(ITEM + 2, 0x1517, 0, 0, 0, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
            m.RemoveTag<EquipmentDirty>();

            mirror.Remove(ITEM + 1);
            mirror.Remove(ITEM + 2);
            m.Tags.Has<EquipmentDirty>().Should().BeFalse();

            mirror.Remove(ITEM);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue();
            mirror.GetEquipment(MOBILE).Count.Should().Be(0);
            mirror.ItemCount.Should().Be(0);
        }

        [Fact]
        public void Deleting_A_Worn_Item_Whose_Wearer_Is_Gone_Is_Harmless()
        {
            var mirror = new WorldMirror();
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);

            mirror.Remove(ITEM).Should().BeTrue();
            mirror.Count.Should().Be(0);
        }

        [Fact]
        public void Changing_The_Wearers_Body_Marks_It_Dirty_But_Moving_Or_Rehueing_It_Does_Not()
        {
            var mirror = NewMirror(out Entity m);

            mirror.UpsertMobile(MOBILE, 0x190, 0x33, 11, 12, 1, 4, 0x08, false);
            m.Tags.Has<EquipmentDirty>().Should().BeFalse();

            mirror.UpsertMobile(MOBILE, 0x191, 0x33, 11, 12, 1, 4, 0x08, false);
            m.Tags.Has<EquipmentDirty>().Should().BeTrue();
        }

        [Fact]
        public void Marking_Dirty_Twice_Keeps_One_Tag()
        {
            var mirror = NewMirror(out Entity m);

            mirror.MarkEquipmentDirty(MOBILE);
            mirror.MarkEquipmentDirty(MOBILE);

            m.Tags.Has<EquipmentDirty>().Should().BeTrue();
        }

        [Fact]
        public void Marking_An_Item_Dirty_Is_Ignored_Because_Only_Mobiles_Have_Slots()
        {
            var mirror = new WorldMirror();
            mirror.UpsertItem(ITEM, 0x1517, 0, 0, 0, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);

            mirror.MarkEquipmentDirty(ITEM);

            mirror.TryGetEntity(ITEM, out Entity e);
            e.Tags.Has<EquipmentDirty>().Should().BeFalse();
        }

        [Fact]
        public void Dying_Mobile_Key_Wears_Items_And_Deleting_One_Marks_It_Dirty()
        {
            const uint DYING = MOBILE | 0x8000_0000;
            var mirror = new WorldMirror();
            Entity dying = mirror.UpsertMobile(DYING, 0x190, 0, 1, 1, 0, 0, 0, false);
            Worn(mirror, ITEM, DYING, Layer.Shirt);
            dying.RemoveTag<EquipmentDirty>();

            mirror.Remove(ITEM);

            dying.Tags.Has<EquipmentDirty>().Should().BeTrue();
        }

        [Fact]
        public void Container_Zero_Means_On_The_Ground_Like_The_No_Container_Sentinel()
        {
            var mirror = new WorldMirror();

            mirror.UpsertItem(ITEM, 0x1517, 0, 0, 0, 0, 0, 0, 1, 0, 0, false);

            mirror.TryGet(ITEM, out MirrorRecord r);
            r.OnGround.Should().BeTrue();
            r.ParentKind.Should().Be(ParentKind.None);
            r.Parent.Should().Be(WorldMirror.NO_CONTAINER);
        }

        [Fact]
        public void Item_Contents_Index_Follows_The_Container_And_Amount_Updates()
        {
            var mirror = new WorldMirror();
            mirror.UpsertItem(ITEM, 0x0EED, 0, 0, 0, 0, 0, 0, 10, BAG, 0, false);
            mirror.UpsertItem(ITEM + 1, 0x0EED, 0, 0, 0, 0, 0, 0, 1, BAG, 0, false);
            mirror.GetContents(BAG).Count.Should().Be(2);

            mirror.UpsertItem(ITEM, 0x0EED, 0, 0, 0, 0, 0, 0, 25, BAG, 0, false);

            mirror.GetContents(BAG).Count.Should().Be(2, "same container, no duplicate");
            mirror.TryGet(ITEM, out MirrorRecord r);
            r.Amount.Should().Be(25);
            r.ParentKind.Should().Be(ParentKind.Contained);
        }

        [Fact]
        public void Multi_Tag_Follows_The_Latest_Upsert()
        {
            var mirror = new WorldMirror();
            mirror.UpsertItem(ITEM, 0x4000, 0, 0, 0, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, true);
            mirror.TryGet(ITEM, out MirrorRecord r);
            r.IsMulti.Should().BeTrue();

            mirror.UpsertItem(ITEM, 0x4000, 0, 0, 0, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
            mirror.TryGet(ITEM, out r);
            r.IsMulti.Should().BeFalse();
        }

        [Fact]
        public void Clear_Empties_The_Mirror_And_The_Indexes_And_It_Can_Be_Reused()
        {
            var mirror = NewMirror(out _);
            Worn(mirror, ITEM, MOBILE, Layer.Shirt);
            mirror.UpsertItem(ITEM + 1, 0x1517, 0, 0, 0, 0, 0, 0, 1, BAG, 0, false);

            mirror.Clear();

            mirror.Count.Should().Be(0);
            mirror.MobileCount.Should().Be(0);
            mirror.ItemCount.Should().Be(0);
            mirror.GetEquipment(MOBILE).Count.Should().Be(0);
            mirror.GetContents(BAG).Count.Should().Be(0);
            mirror.TryGet(MOBILE, out _).Should().BeFalse();

            mirror.UpsertMobile(MOBILE, 0x190, 0, 1, 1, 0, 0, 0, false);
            mirror.MobileCount.Should().Be(1);
        }
    }
}
