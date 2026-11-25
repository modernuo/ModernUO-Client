using System;
using System.Reflection;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Network.Packets;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // Uses the actual scene sink/AddLight and renderer equipment extractor without a graphics device.
    [Collection("SpanBuffersCollection")]
    public sealed class ChunkLightAdmissionTests : IDisposable
    {
        private readonly GameSeam _seam = new(mapBlocks: 4);
        private readonly StaticTiles[] _saved = Chunk.StaticTileData;
        private readonly bool[] _savedEmitters = LightEmitters.OverrideForTests;
        private readonly World _world = new();
        private readonly GameScene _scene;
        private readonly Chunk _chunk;
        private readonly StaticTiles[] _data;
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public ChunkLightAdmissionTests()
        {
            _data = Client.Game.UO.FileManager.TileData.StaticData;
            _data[10] = new StaticTiles { Flags = TileFlag.LightSource, Layer = 26, LightIndex = 26 };
            Chunk.StaticTileData = _data;
            LightEmitters.OverrideForTests = new bool[0x10000];
            LightEmitters.OverrideForTests[10] = true;
            var shapes = new LightEntry[100];
            shapes[26] = new LightEntry { Width = 100, Height = 100 };
            shapes[2] = new LightEntry { Width = 100, Height = 100 };
            shapes[1] = new LightEntry { Width = 60, Height = 60 };
            typeof(LightsLoader).GetField("_lights", Fields).SetValue(Client.Game.UO.FileManager.Lights, shapes);
            _world.MapIndex = 0;
            _chunk = _world.Map.LoadEmptyChunkForTest(0, 0);
            _scene = new GameScene(_world);
            ProfileManager.CurrentProfile.UseAlternativeLights = true;
            Set(_scene, "_drawFrame", 9u);
            Set(_scene, "_maxZ", 127);
            Set(_scene, "_lightsFrame", 1u);
            Set(_scene, "_lightViewFrame", 1u);
            Set(_scene, "_lvLeft", -200); Set(_scene, "_lvTop", -200);
            Set(_scene, "_lvRight", 300); Set(_scene, "_lvBottom", 300);
            Set(_scene, "_lvRange", 2);
        }

        public void Dispose()
        {
            _chunk.Destroy();
            Chunk.StaticTileData = _saved;
            LightEmitters.OverrideForTests = _savedEmitters;
            ChunkLightCache.OverrideForTests = null;
            _seam.Dispose();
        }

        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
        private Static Lamp(int x = 1, int y = 1)
        {
            var lamp = new Static(_world) { Graphic = 10, X = (ushort)x, Y = (ushort)y };
            _chunk.AddGameObject(lamp, x, y);
            return lamp;
        }

        private FrameLights Begin(bool range, int budget = 1)
        {
            var lights = new FrameLights(8);
            lights.BeginFrame(1, budget, true);
            Set(_scene, "_frameLights", lights);
            Set(_scene, "_lightRange", range);
            return lights;
        }

        private void Collect(bool cached)
        {
            Type type = typeof(GameScene).GetNestedType("SceneChunkLightSink", BindingFlags.NonPublic);
            object sink = Activator.CreateInstance(type);
            Set(sink, "Scene", _scene); Set(sink, "Data", _data);
            Set(sink, "Left", 10); Set(sink, "Top", 0); Set(sink, "Right", 100); Set(sink, "Bottom", 100); Set(sink, "Margin", 200);
            typeof(ChunkLightOffers).GetMethod("Collect", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(type).Invoke(null, new object[] { _chunk, _data, cached, false, sink });
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public void LightingCache_OfferOrderTiesBudgetAndRangeMatch_GraphicChangeKeepsOldWinner(bool on, bool range)
        {
            ChunkLightCache.OverrideForTests = on;
            Static a = Lamp(), b = Lamp(), c = Lamp();
            // Warm the cache before an in-place season/graphic change; no tile removal occurs.
            Begin(range); Collect(on);
            a.SetGraphic(11);
            LightEmitters.InvalidateRows();
            var prefixSource = new Static(_world) { Graphic = 10, X = 1, Y = 1 };
            FrameLights WithPrefix()
            {
                var result = Begin(range, 2);
                _scene.AddLight(prefixSource, prefixSource, 0, 0);
                return result;
            }
            var lights = WithPrefix();
            Collect(ChunkLightCache.Enabled);
            Assert.Equal(2, lights.Count);
            Assert.Same(b, lights[1].Source);
            Assert.Equal(26, lights[1].ID);
            Assert.Equal(1, lights.Refused);
            Assert.Equal(3, _chunk.LightSourceCount);
            Assert.Same(a, _chunk.LightSources[0]); Assert.Same(b, _chunk.LightSources[1]); Assert.Same(c, _chunk.LightSources[2]);
            var reference = WithPrefix(); Collect(false);
            Assert.Equal(range ? 0.5f : 1f, lights[1].Share);
            if (range) { lights.FadeLowest(2); reference.FadeLowest(2); }
            Assert.Equal(range ? 0.25f : 1f, lights[1].Share);
#if CUO_DEVTOOLS
            Assert.Equal(-1, lights.FirstDifference(reference));
#endif
            b.SetGraphic(11); c.SetGraphic(11); a.SetGraphic(10);
            lights = Begin(range); Collect(on);
            Assert.Same(a, lights[0].Source);
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public void LightingCache_ProductionAdmissionChecksShapeBandRoofMaxZAndDrawn(bool on, bool range)
        {
            Static lamp = Lamp();
            var lights = Begin(range); Collect(on);
            Assert.Equal(1, lights.Count);
            Assert.Equal((0, 44), (lights[0].DrawX, lights[0].DrawY));
            _data[10].Layer = 99; LightEmitters.InvalidateRows();
            lights = Begin(range); Collect(on); Assert.Equal(0, lights.Count);
            _data[10].Layer = 26; _data[10].Flags |= TileFlag.Roof; LightEmitters.InvalidateRows();
            Set(_scene, "_noDrawRoofs", true);
            lights = Begin(range); Collect(on); Assert.Equal(0, lights.Count);
            Set(_scene, "_noDrawRoofs", false); Set(_scene, "_maxZ", 0);
            lights = Begin(range); Collect(on); Assert.Equal(0, lights.Count);
            Set(_scene, "_maxZ", 127);
            lamp.DrawnFrame = 9; lamp.RealScreenPosition = new Microsoft.Xna.Framework.Point(30, 40);
            lights = Begin(range); Collect(on); Assert.Equal(range ? 0 : 1, lights.Count);
            Set(_scene, "_lvRange", 10);
            lights = Begin(range); Collect(on); Assert.Equal(1, lights.Count);
            Assert.Equal((52, 62), (lights[0].DrawX, lights[0].DrawY));
            lamp.DrawnFrame = 8;
            Set(_scene, "_offset", new Microsoft.Xna.Framework.Point(1000, 1000));
            lights = Begin(range); Collect(on); Assert.Equal(0, lights.Count);
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void LightingCache_ProductionOverheadRejectionAndColoredShape(bool on)
        {
            Static lamp = Lamp();
            _data[10].Layer = 225;
            LightEmitters.InvalidateRows();
            ProfileManager.CurrentProfile.UseColoredLights = true;
            var lights = Begin(false); Collect(on);
            Assert.Equal(1, lights.Count);
            Assert.Equal(1, lights[0].ID);
            Assert.Equal(26, lights[0].Color);
            ProfileManager.CurrentProfile.UseColoredLights = false;
            lights = Begin(false); Collect(on); Assert.Equal(0, lights.Count);
            _data[10].Layer = 26; LightEmitters.InvalidateRows();
            var art = new SpriteEntry[0x4010];
            art[0x400B] = new SpriteEntry { OrigW = 20, OrigH = 20 };
            Client.Game.UO.FileManager.Arts.SetTablesForTests(art);
            var blocker = new Static(_world) { Graphic = 11, X = 2, Y = 2, Z = 5 };
            _chunk.AddGameObject(blocker, 2, 2);
            lights = Begin(false); Collect(on); Assert.Equal(0, lights.Count);
            blocker.RemoveFromTile();
            lights = Begin(false); Collect(on); Assert.Equal(1, lights.Count);
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void LightingCache_GroundAdmissionUsesCurrentLightIdAndDisplayedGraphic(bool on)
        {
            Item item = _world.GetOrCreateItem(0x40003000);
            void Graphic(ushort graphic)
            {
                _world.Ecs.CommitItem(item, new ItemState
                {
                    Serial = item.Serial, Graphic = graphic, X = 1, Y = 1, Container = 0xFFFFFFFF
                });
            }
            Graphic(10);
            _world.Ecs.CommitLightId(item, 26);
            _chunk.AddGameObject(item, 1, 1);
            var lights = Begin(false); Collect(on);
            Assert.Equal(1, lights.Count); Assert.Same(item, lights[0].Source);
            Assert.Equal(26, lights[0].ID);
            _world.Ecs.CommitLightId(item, 99);
            lights = Begin(false); Collect(on); Assert.Equal(0, lights.Count);
            Graphic(0x3914); // forced fire-field shape without the tiledata light flag
            lights = Begin(false); Collect(on); Assert.Equal(1, lights.Count);
            Assert.Equal(2, lights[0].ID);
            Graphic(11);
            lights = Begin(false); Collect(on); Assert.Equal(0, lights.Count);
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void LightingCache_MovingEquippedLightMatchesReference(bool on)
        {
            var source = _world.GetOrCreateMobile(0x1000);
            Item held = _world.Writer.Equip(0x40002000, 10, Layer.OneHanded, source.Serial, 0, out _, out _);
            var anims = Client.Game.UO.FileManager.Animations;
            anims.SetTablesForTests(new AnimBodyEntry[1], Array.Empty<AnimDirEntry>(), Array.Empty<AnimFrameEntry>());
            var extract = new MobileSpriteExtract(anims);
            Lamp();
            for (uint frame = 1; frame <= 4; frame++)
            {
                _world.Writer.MoveMobile(source, (ushort)frame, 1, 0);
                _world.Writer.SetMobileDirection(source, frame == 2 ? Direction.East : Direction.North);
                if (frame == 3) _world.Writer.RemoveWorldItem(held, out _);
                if (frame == 4) held = _world.Writer.Equip(0x40002001, 10, Layer.OneHanded, source.Serial, 0, out _, out _);
                Assert.True(_world.Ecs.Mirror.TryGetEntity(source.Serial, out var entity));
                var state = new AnimationState { Flags = AnimStateFlags.Visible };
                entity.AddComponent(state);
                entity.AddComponent(new MobileRenderState { Alpha = 255, OffsetX = 3, OffsetY = 7 });
                _world.Ecs.Animation.Rebuild(entity, in state, source);
                extract.BeginSelect();
                extract.Select(entity, (int)frame * 22, 100, 0);
                Assert.Equal(frame == 3 ? 0 : 1, extract.Lights.Length);
                var lights = Begin(false, 8);
                foreach (var request in extract.Lights)
                {
                    Assert.Equal(held.Serial, request.Item);
                    Assert.Equal(((int)frame * 22 + 25, 126), (request.X, request.Y));
                    _scene.AddLight(source, held, request.X, request.Y);
                }
                var prefix = new FrameLights(8);
#if CUO_DEVTOOLS
                lights.CopyTo(prefix);
#endif
                Collect(on);
                Assert.Equal(frame == 3 ? 1 : 2, lights.Count);
                if (frame != 3) { Assert.Same(source, lights[0].Source); Assert.Equal(26, lights[0].ID);
                    Assert.Equal(((int)frame * 22 + 25 + (frame == 2 ? 22 : 0), 126 + (frame == 2 ? 55 : 0)), (lights[0].DrawX, lights[0].DrawY)); }
                Set(_scene, "_frameLights", prefix); Collect(false);
#if CUO_DEVTOOLS
                Assert.Equal(-1, lights.FirstDifference(prefix));
#endif
            }
        }
    }
}
