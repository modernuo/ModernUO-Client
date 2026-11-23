using ClassicUO.UnitTests.Ecs;
using System;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// light_occlusion_cache: the switch in both states, the occluder entry table (find, key, chunk versions, LRU eviction,
    /// idle sweep, release), and Chunk.OccluderVersion (bumped by statics and items joining or leaving a tile, not by mobiles).
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")]
    public sealed class LightOcclusionCacheTests : IDisposable
    {
        private readonly World _world = new World();
        private readonly StaticTiles[] _savedStaticData;
        private uint _nextSerial = 0x4000_0200, _nextMobile = 0x200;

        public LightOcclusionCacheTests()
        {
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = new StaticTiles[0x10000];
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            LightOcclusionCache.OverrideForTests = null;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_FollowsOverride(bool on)
        {
            LightOcclusionCache.OverrideForTests = on;
            Assert.Equal(on, LightOcclusionCache.Enabled);
        }

        [Fact]
        public void Settings_DefaultOff()
        {
            Assert.False(new ClassicUO.Configuration.Settings().LightOcclusionCache);
        }

        private Static NewStatic() => new Static(_world) { Graphic = 1 };

        [Fact]
        public void Find_ReturnsEntryTakenForSourceAndId()
        {
            var table = new LightOccluderTable();
            Static src = NewStatic();
            Assert.Null(table.Find(src, 5, 1));
            LightOccluderEntry e = table.Take(src, 5, 1);
            e.SetKey(1, 2, 3, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 77);
            Assert.Same(e, table.Find(src, 5, 2));
            Assert.Null(table.Find(src, 6, 2));
            Assert.True(e.KeyMatches(1, 2, 3, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 77));
            Assert.False(e.KeyMatches(1, 2, 3, 0x10, 4, 6, 0, 0, 10, 10, 8, 0, 77));
            Assert.False(e.KeyMatches(1, 2, 3, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 78));
            Assert.False(e.KeyMatches(1, 2, 4, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 77));
        }

        [Fact]
        public void ChunksValid_FalseAfterVersionBumpOrDestroy()
        {
            var table = new LightOccluderTable();
            var a = new Chunk(_world) { X = 0, Y = 0 };
            var b = new Chunk(_world) { X = 1, Y = 0 };
            LightOccluderEntry e = table.Take(NewStatic(), 1, 1);
            e.AddChunk(a);
            e.AddChunk(b);
            Assert.True(e.ChunksValid());
            b.OccluderVersion++;
            Assert.False(e.ChunksValid());

            e = table.Take(NewStatic(), 1, 1);
            e.AddChunk(a);
            Assert.True(e.ChunksValid());
            a.IsDestroyed = true;
            Assert.False(e.ChunksValid());
        }

        [Fact]
        public void Take_EvictsLeastRecentlyUsedWhenFull()
        {
            var table = new LightOccluderTable(3, 1000);
            Static s1 = NewStatic(), s2 = NewStatic(), s3 = NewStatic(), s4 = NewStatic();
            table.Take(s1, 1, 1);
            table.Take(s2, 1, 2);
            table.Take(s3, 1, 3);
            table.Find(s1, 1, 4);
            table.Take(s4, 1, 5);
            Assert.Equal(3, table.Count);
            Assert.Null(table.Find(s2, 1, 6));
            Assert.NotNull(table.Find(s1, 1, 6));
            Assert.NotNull(table.Find(s3, 1, 6));
            Assert.NotNull(table.Find(s4, 1, 6));
            Assert.Equal(1, table.Evictions);
        }

        [Fact]
        public void Sweep_DropsIdleEntriesAndReleasesObjects()
        {
            var table = new LightOccluderTable(16, 10);
            Static s1 = NewStatic(), s2 = NewStatic(), cand = NewStatic();
            LightOccluderEntry e1 = table.Take(s1, 1, 1);
            e1.AddCandidate(cand);
            e1.AddChunk(new Chunk(_world));
            table.Take(s2, 1, 20);
            table.Sweep(25);
            Assert.Equal(1, table.Count);
            Assert.Null(table.Find(s1, 1, 25));
            Assert.Null(e1.Source);
            Assert.Equal(0, e1.CandidateCount);
            Assert.Null(e1.Candidates[0]);
            Assert.Null(e1.Chunks[0]);

            // the dropped entry is reused for the next source
            Assert.Same(e1, table.Take(NewStatic(), 1, 26));
            table.Reset();
            Assert.Equal(0, table.Count);
        }

        [Fact]
        public void Candidates_GrowPastInitialSize()
        {
            var table = new LightOccluderTable();
            LightOccluderEntry e = table.Take(NewStatic(), 1, 1);

            for (int i = 0; i < 40; i++)
            {
                e.AddCandidate(NewStatic());
                e.AddChunk(new Chunk(_world));
            }

            Assert.Equal(40, e.CandidateCount);
            Assert.Equal(40, e.ChunkCount);
            Assert.True(e.Bytes > 0);
        }

        [Fact]
        public void OccluderVersion_BumpsForStaticsAndItems_NotMobiles()
        {
            var chunk = new Chunk(_world) { X = 0, Y = 0 };
            uint v = chunk.OccluderVersion;

            Static s = NewStatic();
            chunk.AddGameObject(s, 1, 1);
            Assert.NotEqual(v, chunk.OccluderVersion);

            v = chunk.OccluderVersion;
            Mobile m = _world.GetOrCreateMobile(_nextMobile++);
            chunk.AddGameObject(m, 2, 2);
            Assert.Equal(v, chunk.OccluderVersion);
            m.RemoveFromTile();
            Assert.Equal(v, chunk.OccluderVersion);

            Item item = _world.GetOrCreateItem(_nextSerial++);
            item.TestSetGraphic(2);
            chunk.AddGameObject(item, 3, 3);
            Assert.NotEqual(v, chunk.OccluderVersion);

            v = chunk.OccluderVersion;
            s.RemoveFromTile();
            Assert.NotEqual(v, chunk.OccluderVersion);
        }
    }
}

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>light_occlusion_bake: the switch in both states, when it runs, slot sizes and the atlas shelf.</summary>
    public sealed class LightOcclusionBakeTests : IDisposable
    {
        public void Dispose() => LightOcclusionBake.OverrideForTests = null;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_FollowsOverride(bool on)
        {
            LightOcclusionBake.OverrideForTests = on;
            Assert.Equal(on, LightOcclusionBake.Enabled);
        }

        [Fact]
        public void Settings_DefaultOff()
        {
            Assert.False(new ClassicUO.Configuration.Settings().LightOcclusionBake);
        }

        [Theory]
        [InlineData(true, true, true, true, false, true)]
        [InlineData(false, true, true, true, false, false)]
        [InlineData(true, false, true, true, false, false)]
        [InlineData(true, true, false, true, false, false)]
        [InlineData(true, true, true, false, false, false)]
        [InlineData(true, true, true, true, true, false)]
        public void Active_NeedsOcclusionCacheBatchAndNoDither(bool on, bool occlusion, bool cache, bool batch, bool dither, bool expected)
        {
            Assert.Equal(expected, LightOcclusionBake.Active(on, occlusion, cache, batch, dither));
        }

        [Theory]
        [InlineData(100, 1f, 103)]
        [InlineData(100, 0.4f, 43)]
        [InlineData(101, 2f, 205)]
        public void SlotSize_CoversScaledQuadPhaseAndMargin(int size, float scale, int expected)
        {
            Assert.Equal(expected, LightOcclusionBake.SlotSize(size, scale));
        }

        [Fact]
        public void Shelf_PacksRowsAndRefusesWhenFull()
        {
            var shelf = new LightBakeShelf(100, 50);
            Assert.True(shelf.TryAlloc(60, 20, out int x, out int y));
            Assert.Equal((0, 0), (x, y));
            Assert.True(shelf.TryAlloc(40, 10, out x, out y));
            Assert.Equal((60, 0), (x, y));
            Assert.True(shelf.TryAlloc(30, 25, out x, out y));
            Assert.Equal((0, 20), (x, y));
            Assert.False(shelf.TryAlloc(80, 10, out _, out _));
            Assert.False(shelf.TryAlloc(101, 1, out _, out _));
            int gen = shelf.Generation;
            shelf.Reset();
            Assert.Equal(gen + 1, shelf.Generation);
            Assert.True(shelf.TryAlloc(100, 50, out x, out y));
            Assert.Equal((0, 0), (x, y));
        }

        // the Moonglow bank mismatch: the first bake took a slot, then the atlas was made and its shelf started over, so the
        // second bake got the same slot and the two lights' glows were added into one another
        [Fact]
        public void AllocSlot_NewAtlasFirst_BakesGetDisjointSlotsInOneGeneration()
        {
            var shelf = new LightBakeShelf(LightOcclusionBake.AtlasSize, LightOcclusionBake.AtlasSize);
            bool reset = false, clear = false;
            long resets = 0;

            Assert.True(LightOcclusionBake.AllocSlot(shelf, true, 103, 103, ref reset, ref clear, ref resets, out int x1, out int y1));
            int gen = shelf.Generation;
            Assert.True(clear);
            Assert.True(LightOcclusionBake.AllocSlot(shelf, false, 90, 120, ref reset, ref clear, ref resets, out int x2, out int y2));

            Assert.Equal(gen, shelf.Generation);
            Assert.False(new Rectangle(x1, y1, 103, 103).Intersects(new Rectangle(x2, y2, 90, 120)));
            Assert.False(reset);
            Assert.Equal(0, resets);
        }

        [Fact]
        public void AllocSlot_FullShelf_StartsOverOncePerPass()
        {
            var shelf = new LightBakeShelf(100, 100);
            bool reset = false, clear = false;
            long resets = 0;

            Assert.True(LightOcclusionBake.AllocSlot(shelf, false, 100, 60, ref reset, ref clear, ref resets, out _, out _));
            int gen = shelf.Generation;
            Assert.True(LightOcclusionBake.AllocSlot(shelf, false, 100, 60, ref reset, ref clear, ref resets, out int x, out int y));
            Assert.Equal((0, 0), (x, y));
            Assert.Equal(gen + 1, shelf.Generation);
            Assert.True(reset && clear);
            Assert.Equal(1, resets);

            Assert.False(LightOcclusionBake.AllocSlot(shelf, false, 100, 60, ref reset, ref clear, ref resets, out _, out _));
            Assert.Equal(gen + 1, shelf.Generation);
            Assert.Equal(1, resets);
        }

        [Fact]
        public void Mix_DependsOnEveryValue()
        {
            ulong a = LightOcclusionBake.Mix(LightOcclusionBake.Mix(1, 2), 3);
            Assert.NotEqual(a, LightOcclusionBake.Mix(LightOcclusionBake.Mix(1, 3), 2));
            Assert.NotEqual(a, LightOcclusionBake.Mix(LightOcclusionBake.Mix(1, 2), 4));
        }
    }

    /// <summary>
    /// light_occlusion_bake_world_phase: both switch states, and the bake key's phase term on a real Camera while the player
    /// walks (the world draw offset moves a few pixels each frame): the world phase key stays the same and the slot moves by
    /// whole pixels with the world; the frame phase key (switch off) changes. A light moved by a pixel at a fractional scale, or
    /// a zoom change, changes the world phase key.
    /// </summary>
    public sealed class LightBakeWorldPhaseTests : IDisposable
    {
        public void Dispose() => LightBakeWorldPhase.OverrideForTests = null;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_FollowsOverride(bool on)
        {
            LightBakeWorldPhase.OverrideForTests = on;
            Assert.Equal(on, LightBakeWorldPhase.Enabled);
        }

        [Fact]
        public void Settings_DefaultOff()
        {
            Assert.False(new ClassicUO.Configuration.Settings().LightOcclusionBakeWorldPhase);
        }

        // a window glow 77 x 90 pixels whose emitter is at world pixel (wx, wy); the player walks south-east from tile
        // (1495, 1629), the draw offset moving 2 or 3 pixels a frame as the step offset is cast to whole pixels
        private const int LightW = 77, LightH = 90;

        private static Camera NewCamera(float zoom)
        {
            var camera = new Camera(0.5f, 2.5f, 0.1f);
            camera.Bounds = new Rectangle(0, 0, 1283, 721);
            camera.Zoom = zoom;

            return camera;
        }

        // what the prepass computes for the light at world pixel (wx, wy) under draw offset (offX, offY)
        private static (ulong Key, int X, int Y) Place(Camera camera, bool world, int wx, int wy, int offX, int offY)
        {
            camera.SetWorldOffset(offX, offY);
            Matrix m = camera.ViewTransformMatrix;
            float s = m.M11;
            float posX = wx - offX - LightW * 0.5f, posY = wy - offY - LightH * 0.5f;
            float tx = posX * s + m.M41, ty = posY * s + m.M42;

            if (!world)
            {
                int fx = (int)MathF.Floor(tx), fy = (int)MathF.Floor(ty);

                return (LightOcclusionBake.PhaseKey(false, 0, 0, tx - fx, ty - fy), fx, fy);
            }

            int px = LightOcclusionBake.WorldPhase(offX + (double)posX, s), py = LightOcclusionBake.WorldPhase(offY + (double)posY, s);
            int ix = LightOcclusionBake.PhasePixel(tx, px), iy = LightOcclusionBake.PhasePixel(ty, py);
            Assert.True(LightOcclusionBake.PhaseResidual(tx, ix, px) < 1.0 / 64);
            Assert.True(LightOcclusionBake.PhaseResidual(ty, iy, py) < 1.0 / 64);

            return (LightOcclusionBake.PhaseKey(true, px, py, 0, 0), ix, iy);
        }


        private static (int X, int Y) WalkOffset(int frame) =>
            ((1495 - 1629) * 22 - 641 + frame * 3, (1495 + 1629) * 22 - 360 - 5 * 4 + frame * 2);

        // the whole target pixel the camera puts the world's origin on for this draw offset
        private static (int X, int Y) Origin(Camera camera, int offX, int offY)
        {
            camera.SetWorldOffset(offX, offY);
            Matrix m = camera.ViewTransformMatrix;

            return ((int)Math.Round(m.M41 - (double)offX * m.M11), (int)Math.Round(m.M42 - (double)offY * m.M11));
        }

        private const int Wx = (1500 - 1630) * 22 + 13, Wy = (1500 + 1630) * 22 - 40 + 7;

        [Theory]
        [InlineData(2.5f)]
        [InlineData(1.3f)]
        [InlineData(0.7f)]
        [InlineData(1f)]
        public void Walk_KeepsWorldPhaseKey_SlotMovesWithWorldOrigin(float zoom)
        {
            Camera camera = NewCamera(zoom);
            (int ox0, int oy0) = WalkOffset(0);
            var first = Place(camera, true, Wx, Wy, ox0, oy0);
            var origin0 = Origin(camera, ox0, oy0);

            for (int f = 1; f < 300; f++)
            {
                (int ox, int oy) = WalkOffset(f);
                var p = Place(camera, true, Wx, Wy, ox, oy);
                var origin = Origin(camera, ox, oy);
                Assert.Equal(first.Key, p.Key);
                Assert.Equal(origin.X - origin0.X, p.X - first.X);
                Assert.Equal(origin.Y - origin0.Y, p.Y - first.Y);
            }
        }

        [Theory]
        [InlineData(2.5f, true)]
        [InlineData(1.3f, true)]
        [InlineData(1f, false)]
        public void Walk_ChangesFramePhaseKey_AtFractionalZoom(float zoom, bool changes)
        {
            Camera camera = NewCamera(zoom);
            (int ox0, int oy0) = WalkOffset(0);
            ulong first = Place(camera, false, Wx, Wy, ox0, oy0).Key;
            int changed = 0;

            for (int f = 1; f < 300; f++)
            {
                (int ox, int oy) = WalkOffset(f);

                if (Place(camera, false, Wx, Wy, ox, oy).Key != first)
                {
                    changed++;
                }
            }

            Assert.Equal(changes, changed > 0);
        }

        [Fact]
        public void LightMovedOnePixel_ChangesWorldPhaseKey_AtFractionalScale()
        {
            (int ox, int oy) = WalkOffset(0);
            Camera camera = NewCamera(2.5f);
            var a = Place(camera, true, Wx, Wy, ox, oy);
            Assert.NotEqual(a.Key, Place(camera, true, Wx + 1, Wy, ox, oy).Key);
            Assert.NotEqual(a.Key, Place(camera, true, Wx, Wy + 1, ox, oy).Key);
            // at scale 1 every world pixel has phase 0: the slot moves by the pixel, the bake stays
            camera = NewCamera(1f);
            a = Place(camera, true, Wx, Wy, ox, oy);
            var b = Place(camera, true, Wx + 1, Wy, ox, oy);
            Assert.Equal(a.Key, b.Key);
            Assert.Equal(a.X + 1, b.X);
        }

        [Fact]
        public void SubPixelCameraShift_KeepsKey_DrawsAtNearestPixel()
        {
            int phase = LightOcclusionBake.WorldPhase(Wx - LightW * 0.5, 0.4f);
            ulong key = LightOcclusionBake.PhaseKey(true, phase, 0, 0f, 0f);
            float t = 300f + phase / (float)LightOcclusionBake.PhaseSteps;
            Assert.Equal(300, LightOcclusionBake.PhasePixel(t, phase));
            Assert.Equal(300, LightOcclusionBake.PhasePixel(t + 0.3f, phase));
            Assert.Equal(301, LightOcclusionBake.PhasePixel(t + 0.7f, phase));
            Assert.Equal(299, LightOcclusionBake.PhasePixel(t - 0.7f, phase));
            Assert.InRange(LightOcclusionBake.PhaseResidual(t + 0.3f, 300, phase), 0.29, 0.31);
            // the key holds the world phase only: no camera term
            Assert.Equal(key, LightOcclusionBake.PhaseKey(true, phase, 0, 0.3f, 0.7f));
            // the frame phase key (switch off) changes with the shift
            Assert.NotEqual(LightOcclusionBake.PhaseKey(false, 0, 0, 0.25f, 0f), LightOcclusionBake.PhaseKey(false, 0, 0, 0.55f, 0f));
        }

        [Theory]
        [InlineData(9.999, 1f, 0, 10)]
        [InlineData(10.5, 1f, 128, 10)]
        [InlineData(-1.5, 1f, 128, -2)]
        [InlineData(7.0, 0.4f, 205, 2)]
        public void WorldPhase_RoundsToSteps_AndWrapsToNextPixel(double worldX, float scale, int phase, int pixel)
        {
            Assert.Equal(phase, LightOcclusionBake.WorldPhase(worldX, scale));
            Assert.Equal(pixel, LightOcclusionBake.PhasePixel((float)(worldX * scale), phase));
        }

        [Fact]
        public void Key_ChangesWithContentScaleAndPhase_NotOtherwise()
        {
            var uv = new Rectangle(10, 20, 77, 90);
            var occ = new Rectangle(0, 0, 44, 88);
            ulong phase = LightOcclusionBake.PhaseKey(true, 51, 205, 0f, 0f);

            ulong Key(float scale, ulong ph, uint art, Rectangle o, int dx, int dy, Vector3 hue) =>
                LightOcclusionBake.KeyOccluder(LightOcclusionBake.KeyLight(3 | 1UL << 32, hue, scale, ph, uv, 7), art, 9, o, dx, dy);

            ulong a = Key(0.4f, phase, 0x4001, occ, -20, 15, Vector3.UnitZ);
            Assert.Equal(a, Key(0.4f, phase, 0x4001, occ, -20, 15, Vector3.UnitZ));
            Assert.NotEqual(a, Key(0.5f, phase, 0x4001, occ, -20, 15, Vector3.UnitZ));
            Assert.NotEqual(a, Key(0.4f, LightOcclusionBake.PhaseKey(true, 52, 205, 0f, 0f), 0x4001, occ, -20, 15, Vector3.UnitZ));
            Assert.NotEqual(a, Key(0.4f, phase, 0x4002, occ, -20, 15, Vector3.UnitZ));
            Assert.NotEqual(a, Key(0.4f, phase, 0x4001, new Rectangle(0, 0, 44, 87), -20, 15, Vector3.UnitZ));
            Assert.NotEqual(a, Key(0.4f, phase, 0x4001, occ, -19, 15, Vector3.UnitZ));
            Assert.NotEqual(a, Key(0.4f, phase, 0x4001, occ, -20, 16, Vector3.UnitZ));
            Assert.NotEqual(a, Key(0.4f, phase, 0x4001, occ, -20, 15, new Vector3(0, 0, 0.5f)));
        }

        [Fact]
        public void Rate_ReportsLastWindowPerSecond()
        {
            var rate = new LightBakeRate();
            rate.Add(0, 1000);

            for (uint t = 1016; t < 2000; t += 16)
            {
                rate.Add(2, t);
            }

            Assert.Equal(0, rate.PerSecond);
            rate.Add(0, 2000);
            Assert.Equal(124, rate.PerSecond);
            rate.Add(0, 2500);
            rate.Add(0, 3000);
            Assert.Equal(0, rate.PerSecond);
            rate.Add(5, 3100);
            rate.Add(0, 5100);
            Assert.Equal(2, rate.PerSecond);
        }
    }
}
