using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // Packets that need a GameScene (0x65 weather, 0x1B enter world) or the animation front end (0xAF death), through the
    // test seam with a GameScene that is current but not loaded (SetSceneForTests: its camera, no UI), the whole receive
    // path, every read mode, compressed or not, span_reader off and on. The seam has a map store (facet 0, 16 x 16 empty
    // blocks), so 0x1B builds its Map the way the client does.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingSceneTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint CORPSE = 0x4000_0300;

        private const int MapBlocks = 16;

        private readonly GameSeam _seam = new(mapBlocks: MapBlocks);

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
            _seam.Dispose();
        }

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        private World InScene()
        {
            var world = new World();
            world.CreatePlayer(PLAYER, new List<ClassicUO.Assets.SkillEntry>());
            typeof(World).GetProperty(nameof(World.Map))!.SetValue(world, new ClassicUO.Game.Map.Map(world, 0, 1));
            _seam.Game.SetSceneForTests(new GameScene(world));

            return world;
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Weather_0x65_In_A_Game_Scene_Starts_Weather_Once(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InScene();
            byte[] rain = new PacketBuilder(0x65, false).U8((int)WeatherType.WT_RAIN).U8(50).U8(20).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, rain, rain, new PacketBuilder(0xC8, false).U8(6).Build());

            r.Disconnects.Should().Be(0, r.Reason);
            r.Packets.Should().Be(3);
            world.Weather.CurrentWeather.Should().Be(WeatherType.WT_RAIN);
            world.ClientViewRange.Should().Be(6, "the repeated weather is skipped and the stream stays framed");
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Death_0xAF_Rekeys_The_Mobile_And_Records_The_Corpse(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            World world = InScene();
            Mobile mob = world.GetOrCreateMobile(MOB);
            world.Writer.SetMobileGraphic(mob, 0x0190, checkBody: true);

            var r = PacketPipe.Deliver(world, mode, read, compressed,
                new PacketBuilder(0xAF, false).U32(MOB).U32(CORPSE).U32(1).Build(),
                new PacketBuilder(0xAF, false).U32(PLAYER).U32(CORPSE).U32(0).Build(),
                new PacketBuilder(0xC8, false).U8(7).Build());

            r.Disconnects.Should().Be(0, r.Reason);
            r.Packets.Should().Be(3);
            mob.Serial.Should().Be(MOB | 0x8000_0000);
            world.Mobiles.Get(MOB | 0x8000_0000).Should().BeSameAs(mob);
            world.Player.Serial.Should().Be(PLAYER, "the player's own death packet is ignored");
            world.ClientViewRange.Should().Be(7);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void EnterWorld_0x1B_Creates_The_Player_On_Map_0_And_Answers(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            var world = new World { WarmupDone = true }; // the login pool prefill (32 MB) is not what this test is about
            var scene = new GameScene(world);
            scene.Camera.Bounds.Width = 640;
            scene.Camera.Bounds.Height = 480;
            _seam.Game.SetSceneForTests(scene);

            byte[] enter = new PacketBuilder(0x1B, false).U32(PLAYER).U32(0).U16(0x0190).U16(100).U16(120).U16(unchecked((ushort)-5))
                .U8(0x83).Zero(19).Build();

            var r = PacketPipe.Deliver(world, mode, read, compressed, enter, new PacketBuilder(0xC8, false).U8(9).Build());

            r.Disconnects.Should().Be(0, r.Reason);
            r.Packets.Should().Be(2);
            world.Player.Should().NotBeNull();
            world.Player.Serial.Should().Be(PLAYER);
            world.Player.Graphic.Should().Be(0x0190);
            (world.Player.X, world.Player.Y, world.Player.Z).Should().Be(((ushort)100, (ushort)120, (sbyte)-5));
            world.Player.Direction.Should().Be(Direction.Down, "only the low three bits are the direction");
            world.MapIndex.Should().Be(0);
            (world.Map.BlocksX, world.Map.BlocksY).Should().Be((MapBlocks, MapBlocks));
            (world.RangeSize.X, world.RangeSize.Y).Should().Be((100, 120));
            world.ClientViewRange.Should().Be(9);

            // the answers, in order: game window size (BF 05) and language (BF 0B) from 2.0.0, client version, a click on the
            // player, a skills request; no public house content (7.0.79.6+; the pipe is 7.0.13)
            Split(r.Sent).Should().Equal("BF05", "BF0B", "BD", "09", "34");
            r.Sent.AsSpan(0, 13).ToArray().Should().Equal(new PacketBuilder(0xBF, true).U16(0x05).U32(640).U32(480).Build());
        }

        // packet ids (with the 0xBF subcommand) of a stream the client sent
        private static List<string> Split(byte[] sent)
        {
            var table = new PacketsTable(PacketPipe.Version);
            var ids = new List<string>();

            for (int i = 0; i < sent.Length;)
            {
                int len = table.GetPacketLength(sent[i]);

                if (len < 0)
                {
                    len = (sent[i + 1] << 8) | sent[i + 2];
                }

                ids.Add(sent[i] == 0xBF ? $"BF{sent[i + 4]:X2}" : $"{sent[i]:X2}");
                i += len;
            }

            return ids;
        }
    }
}
