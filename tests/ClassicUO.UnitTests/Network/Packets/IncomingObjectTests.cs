using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Incoming object packets whose handlers reach Client.Game (UpdateGameObject, AddItemToContainer, the cursor's held item,
    // the client version's layout choice): run through the test seam (GameSeam), the whole receive path, every read mode,
    // compressed or not, span_reader off and on.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingObjectTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint ITEM = 0x4000_0100;
        private const uint ITEM2 = 0x4000_0101;
        private const uint BAG = 0x4000_0300;

        private readonly GameSeam _seam = new();

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            _seam.Dispose();
        }

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        internal static World InGame()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            world.GetOrCreateMobile(MOB);

            return world;
        }

        // ---- 0x1A UpdateItem

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateItem_0x1A_Plain(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] p = new PacketBuilder(0x1A, true).U32(ITEM).U16(0x0EED).U16(100).U16(200).I8(-5).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Item item = world.Items.Get(ITEM);
            item.Should().NotBeNull();
            item.Graphic.Should().Be(0x0EED);
            item.Amount.Should().Be(1);
            item.X.Should().Be(100);
            item.Y.Should().Be(200);
            item.Z.Should().Be(-5);
            item.Hue.Should().Be(0);
            item.OnGround.Should().BeTrue();
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateItem_0x1A_Every_Optional_Field(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            // amount bit on the serial, graphic increment, direction bit on x, hue and flags bits on y
            byte[] p = new PacketBuilder(0x1A, true).U32(ITEM | 0x8000_0000).U16(0x8000 | 0x0EED).U8(2).U16(500)
                .U16(0x8000 | 100).U16(0x8000 | 0x4000 | 200).U8(3).I8(7).U16(0x0481).U8(0x80).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Item item = world.Items.Get(ITEM);
            item.Graphic.Should().Be(0x0EED + 2, "an item's graphic takes the increment");
            item.Amount.Should().Be(500);
            item.X.Should().Be(100);
            item.Y.Should().Be(200);
            item.Z.Should().Be(7);
            item.Hue.Should().Be(0x0481);
            item.Flags.Should().Be((Flags)0x80);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateItem_0x1A_Before_The_Player_Is_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var world = new World();
            byte[] p = new PacketBuilder(0x1A, true).U32(ITEM).U16(0x0EED).U16(100).U16(200).I8(0).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p, p);

            r.Packets.Should().Be(2);
            r.Disconnects.Should().Be(0);
            world.Items.Get(ITEM).Should().BeNull();
        }

        // ---- 0x77 UpdateCharacter / 0xD2

        private static byte[] Character(byte id, uint serial, int graphic, int x, int y, int z, int dir, int hue, int flags, int noto) =>
            new PacketBuilder(id, false).U32(serial).U16(graphic).U16(x).U16(y).I8(z).U8(dir).U16(hue).U8(flags).U8(noto).Build();

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateCharacter_0x77_Updates_A_Known_Mobile(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Character(0x77, MOB, 0x0190, 1000, 2000, 10, (int)Direction.East, 0x83EA, 0, (int)NotorietyFlag.Murderer));

            r.Packets.Should().Be(1);
            Mobile m = world.Mobiles.Get(MOB);
            m.Graphic.Should().Be(0x0190);
            m.Hue.Should().Be(0x83EA);
            m.NotorietyFlag.Should().Be(NotorietyFlag.Murderer);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateCharacter_0x77_Unknown_Serial_Is_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                Character(0x77, 0x0000_0999, 0x0190, 1, 2, 0, 0, 0, 0, 1),
                Character(0xD2, MOB, 0x0191, 1, 2, 0, 0, 0x0021, 0, (int)NotorietyFlag.Ally).Concat(new byte[8]).ToArray()); // 0xD2 is 25 bytes

            r.Packets.Should().Be(2);
            world.Get(0x0000_0999).Should().BeNull();
            world.Mobiles.Get(MOB).Graphic.Should().Be(0x0191);
            world.Mobiles.Get(MOB).NotorietyFlag.Should().Be(NotorietyFlag.Ally);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateCharacter_0x77_Player_Takes_Body_Not_Position(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            ushort x = world.Player.X;

            PacketPipe.Deliver(world, mode, read, compressed, Character(0x77, PLAYER, 0x0191, 1234, 1234, 0, 0, 0x0400, 0, 1));

            world.Player.Graphic.Should().Be(0x0191);
            world.Player.Hue.Should().Be(0x0400);
            world.Player.X.Should().Be(x);
        }

        // ---- 0x78 UpdateObject

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateObject_0x78_Creates_A_Mobile_With_Equipment(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            const uint NEW = 0x0000_0300;
            // pre-7.0.33 layout: an item's hue follows only when the graphic's top bit is set
            byte[] p = new PacketBuilder(0x78, true).U32(NEW).U16(0x0190).U16(300).U16(400).I8(0).U8(2).U16(0x0455).U8(0).U8(3)
                .U32(ITEM).U16(0x8000 | 0x13B9).U8((int)Layer.OneHanded).U16(0x0481)
                .U32(ITEM2).U16(0x1F03).U8((int)Layer.Robe)
                .U32(0).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Mobile m = world.Mobiles.Get(NEW);
            m.Should().NotBeNull();
            m.Graphic.Should().Be(0x0190);
            m.X.Should().Be(300);
            m.Y.Should().Be(400);
            m.Hue.Should().Be(0x0455);
            m.NotorietyFlag.Should().Be((NotorietyFlag)3);
            m.FindItemByLayer(Layer.OneHanded).Graphic.Should().Be(0x13B9);
            m.FindItemByLayer(Layer.OneHanded).Hue.Should().Be(0x0481);
            m.FindItemByLayer(Layer.Robe).Graphic.Should().Be(0x1F03);
            m.FindItemByLayer(Layer.Robe).Hue.Should().Be(0);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateObject_0x78_List_Without_Terminator_Ends_At_The_Packet_End(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] p = new PacketBuilder(0x78, true).U32(MOB).U16(0x0190).U16(300).U16(400).I8(0).U8(2).U16(0).U8(0).U8(1)
                .U32(ITEM).U16(0x13B9).U8((int)Layer.OneHanded).Build();
            byte[] empty = new PacketBuilder(0x78, true).U32(MOB).U16(0x0190).U16(300).U16(400).I8(0).U8(2).U16(0).U8(0).U8(1)
                .U32(0).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Disconnects.Should().Be(0);
            world.Mobiles.Get(MOB).FindItemByLayer(Layer.OneHanded).Should().NotBeNull();

            // an empty list clears the worn items
            PacketPipe.Deliver(world, mode, read, compressed, empty).Disconnects.Should().Be(0);
            world.Mobiles.Get(MOB).FindItemByLayer(Layer.OneHanded).Should().BeNull();
        }

        // ---- 0x20 UpdatePlayer

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdatePlayer_0x20_Moves_And_Dresses_The_Player(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] p = new PacketBuilder(0x20, false).U32(PLAYER).U16(0x0190).U8(0).U16(0x83EA).U8(0)
                .U16(1500).U16(1600).U16(0).U8((int)Direction.South).I8(20).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            world.Player.Graphic.Should().Be(0x0190);
            world.Player.Hue.Should().Be(0x83EA);
            world.Player.X.Should().Be(1500);
            world.Player.Y.Should().Be(1600);
            world.Player.Z.Should().Be(20);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdatePlayer_0x20_Without_A_Player_Is_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var world = new World();
            byte[] p = new PacketBuilder(0x20, false).U32(PLAYER).U16(0x0190).U8(0).U16(0).U8(0).U16(1).U16(1).U16(0).U8(0).I8(0).Build();

            PacketPipe.Deliver(world, mode, read, compressed, p).Packets.Should().Be(1);
            world.Player.Should().BeNull();
        }

        // ---- 0x25 / 0x3C contained items (7.0.13: the grid byte layout)

        private static PacketBuilder Contained(PacketBuilder b, uint serial, int graphic, int amount, int x, int y, uint container, int hue) =>
            b.U32(serial).U16(graphic).U8(0).U16(amount).U16(x).U16(y).U8(0).U32(container).U16(hue);

        private static World WithBag()
        {
            World world = InGame();
            PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false,
                new PacketBuilder(0x2E, false).U32(BAG).U16(0x0E75).I8(0).U8((int)Layer.Backpack).U32(PLAYER).U16(0).Build());

            return world;
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateContainedItem_0x25_Puts_The_Item_In_The_Bag(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();
            byte[] p = Contained(new PacketBuilder(0x25, false), ITEM, 0x0EED, 250, 44, 66, BAG, 0x0035).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Item item = world.Items.Get(ITEM);
            item.Container.Should().Be(BAG);
            item.Amount.Should().Be(250);
            item.X.Should().Be(44);
            item.Y.Should().Be(66);
            item.Hue.Should().Be(0x0035);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateContainedItem_0x25_Amount_Zero_Becomes_One(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();
            byte[] p = Contained(new PacketBuilder(0x25, false), ITEM, 0x0EED, 0, 1, 1, BAG, 0).Build();

            PacketPipe.Deliver(world, mode, read, compressed, p);

            world.Items.Get(ITEM).Amount.Should().Be(1);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateContainedItems_0x3C_Replaces_The_Contents(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();
            const uint OLD = 0x4000_0500;
            PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false,
                Contained(new PacketBuilder(0x25, false), OLD, 0x0EED, 1, 1, 1, BAG, 0).Build());

            var b = new PacketBuilder(0x3C, true).U16(2);
            Contained(b, ITEM, 0x0EED, 10, 20, 30, BAG, 0);
            Contained(b, ITEM2, 0x0F0E, 1, 40, 50, BAG, 0x0021);

            var r = PacketPipe.Deliver(world, mode, read, compressed, b.Build());

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            world.Items.Get(OLD).Should().BeNull("the first entry clears the container");
            world.Items.Get(ITEM).Container.Should().Be(BAG);
            world.Items.Get(ITEM).Amount.Should().Be(10);
            world.Items.Get(ITEM2).Hue.Should().Be(0x0021);
            world.Items.Get(ITEM2).X.Should().Be(40);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateContainedItems_0x3C_Count_Zero_Changes_Nothing(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = WithBag();
            PacketPipe.Deliver(world, SpanBufferMode.Old, PipeRead.Max, false,
                Contained(new PacketBuilder(0x25, false), ITEM, 0x0EED, 1, 1, 1, BAG, 0).Build());

            var r = PacketPipe.Deliver(world, mode, read, compressed, new PacketBuilder(0x3C, true).U16(0).Build());

            r.Disconnects.Should().Be(0);
            world.Items.Get(ITEM).Container.Should().Be(BAG);
        }

        // ---- 0xF3 UpdateItemSA (7.0.13 is after 7.0.9: the 26-byte layout with the trailing field)

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateItemSA_0xF3_Creates_An_Item(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] p = new PacketBuilder(0xF3, false).U16(1).U8(0).U32(ITEM).U16(0x0EED).U8(0).U16(42).U16(0)
                .U16(700).U16(800).I8(3).U8(0).U16(0x0481).U8(0).U16(0).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            Item item = world.Items.Get(ITEM);
            item.Graphic.Should().Be(0x0EED);
            item.Amount.Should().Be(42);
            item.X.Should().Be(700);
            item.Y.Should().Be(800);
            item.Z.Should().Be(3);
            item.Hue.Should().Be(0x0481);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void UpdateItemSA_0xF3_Player_Serial_Is_Ignored(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InGame();
            byte[] p = new PacketBuilder(0xF3, false).U16(1).U8(1).U32(PLAYER).U16(0x0191).U8(0).U16(1).U16(0)
                .U16(1).U16(1).I8(0).U8(0).U16(0x0021).U8(0).U16(0).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, p);

            r.Packets.Should().Be(1);
            r.Disconnects.Should().Be(0);
            world.Player.Hue.Should().NotBe(0x0021, "0xF3 for the player changes it only inside 0xF7");
        }
    }
}
