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
    // Packets that need a GameScene (0x65 weather) or the animation front end (0xAF death), through the
    // test seam with a GameScene that is current but not loaded (SetSceneForTests: its camera, no UI), the whole receive
    // path, every read mode, compressed or not, span_reader off and on.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class IncomingSceneTests : IDisposable
    {
        private const uint PLAYER = OutgoingPacketTests.PLAYER;
        private const uint MOB = 0x0000_0200;
        private const uint CORPSE = 0x4000_0300;

        private readonly GameSeam _seam = new();

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
    }
}
