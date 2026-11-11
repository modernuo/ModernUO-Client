using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // legacy-sprites-fix: the Legacy sink's render-list prefetch reads nothing it could get wrong: over a list of items with
    // and without records, mobiles and stale handles it changes no record and no read.
    public class LegacySinkPrefetchTests
    {
        private const uint ITEM0 = 0x4000_0000;

        [Fact]
        public void Item_Is_Sealed_So_The_Sinks_Item_Test_Is_One_Compare()
        {
            Assert.True(typeof(Item).IsSealed);
        }

        [Fact]
        public void Prefetch_Over_A_Render_List_Changes_No_Record()
        {
            var world = new World();
            var list = new List<GameObject>();
            var items = new List<Item>();

            for (int i = 0; i < 40; i++)
            {
                Item it = world.GetOrCreateItem(ITEM0 + (uint)i);
                it.Graphic = (ushort)(0x0100 + i);
                it.Hue = (ushort)(i * 3);
                it.X = (ushort)(1400 + i);
                it.Y = (ushort)(1600 + i);
                it.Flags = i % 4 == 0 ? Flags.Movable : Flags.None;

                // every fifth item is not synced (its record, if any, keeps the creation values)
                if (i % 5 != 0)
                {
                    world.Ecs.Sync(it);
                }

                list.Add(it);
                items.Add(it);

                if (i % 7 == 0)
                {
                    Mobile m = world.GetOrCreateMobile(0x0000_1000 + (uint)i);
                    list.Add(m);
                }
            }

            // a held handle whose record is gone: the prefetch still takes it (EcsEntityHeld is not rechecked)
            Item stale = items[1];
            Assert.True(stale.TryScalarEntity(out Friflo.Engine.ECS.Entity staleEntity));
            staleEntity.DeleteEntity();

            ReadOnlySpan<GameObject> span = list.ToArray();

            for (int i = 0; i < span.Length; i++)
            {
                LegacySpriteSink.Prefetch(span, i);
            }

            foreach (GameObject obj in list)
            {
                if (obj is not Item it || ReferenceEquals(it, stale) || !it.TryScalarEntity(out Friflo.Engine.ECS.Entity e))
                {
                    continue;
                }

                int n = (int)(it.Serial - ITEM0);

                if (n % 5 == 0)
                {
                    continue;
                }

                Assert.Equal((ushort)(0x0100 + n), ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Graphic>(e).Value);
                Assert.Equal((ushort)(n * 3), ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Hue>(e).Value);
                Assert.Equal((ushort)(1400 + n), ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.Position>(e).X);
            }
        }

        // the object stage prefetches the line of Entity.EcsEntity: one offset for every Entity subclass (the field is
        // Entity's), past the object's first fields and the same however often it is measured
        [Fact]
        public void Handle_Offset_Is_Entitys_Field_For_Items_And_Mobiles()
        {
            var world = new World();
            Item item = world.GetOrCreateItem(ITEM0);
            Mobile mobile = world.GetOrCreateMobile(0x0000_1000);

            nint a = LegacySpriteSink.HandleOffset(item), b = LegacySpriteSink.HandleOffset(mobile);
            Assert.Equal(a, b);
            Assert.True(a > 0);
            Assert.Equal(a, LegacySpriteSink.HandleOffset(world.GetOrCreateItem(ITEM0 + 1)));
        }

        // legacy-sprites-fix-2: CUO_LEGACY_PREFETCH=obj,node,row (dev builds) sets the distances of the timing arms; 0 turns a
        // stage off; anything malformed keeps the defaults
        [Fact]
        public void Prefetch_Override_Parses_Three_Distances()
        {
            var defaults = LegacySpriteSink.ParsePrefetch(null);
            Assert.Equal((16, 8, 4), defaults);
            Assert.Equal((12, 0, 3), LegacySpriteSink.ParsePrefetch("12,0,3"));
            Assert.Equal((0, 0, 0), LegacySpriteSink.ParsePrefetch("0,0,0"));
            Assert.Equal(defaults, LegacySpriteSink.ParsePrefetch("12,4"));
            Assert.Equal(defaults, LegacySpriteSink.ParsePrefetch("a,4,2"));
            Assert.Equal(defaults, LegacySpriteSink.ParsePrefetch("-1,4,2"));
            Assert.Equal(defaults, LegacySpriteSink.ParsePrefetch("100,4,2"));
        }

        [Fact]
        public void Prefetch_At_The_Ends_Of_A_Short_List()
        {
            var world = new World();
            Item it = world.GetOrCreateItem(ITEM0);
            world.Ecs.Sync(it);
            GameObject[] one = { it };

            LegacySpriteSink.Prefetch(one, 0);
            LegacySpriteSink.Prefetch(ReadOnlySpan<GameObject>.Empty, 0);
            Assert.True(it.TryScalarEntity(out _));
        }

        public static TheoryData<int, int, int, bool> PrefetchCases
        {
            get
            {
                var cases = new TheoryData<int, int, int, bool>();

                foreach (bool row in new[] { false, true })
                {
                    cases.Add(16, 8, 4, row);
                    cases.Add(0, 0, 0, row);
                    cases.Add(1, 1, 1, row);
                    cases.Add(16, 0, 4, row);
                    cases.Add(64, 64, 64, row);
                }

                return cases;
            }
        }

        private static StaticTiles[] Tiles()
        {
            var tiles = new StaticTiles[0x10000];

            for (int g = 0; g < tiles.Length; g++)
            {
                tiles[g] = new StaticTiles(g % 3 == 0 ? (ulong)TileFlag.Generic : 0, (byte)(g & 0xFF), 0, 0, 0, 0, 0, 1, null);
            }

            return tiles;
        }

        // what the item draw reads of an item (ItemSpriteInput.ReadRecord, Gather's record part) in the given row state
        private static string ReadDraw(GameObject obj, StaticTiles[] tiles, bool drawRow)
        {
            if (obj is not Item it)
            {
                return obj is Mobile m ? $"mobile {m.Serial:X8}" : "null";
            }

            ItemSpriteInput a = default;
            bool record = ItemSpriteInput.ReadRecord(it, tiles, ref a, out ushort y, out _, drawRow);

            return $"{record} {a.Serial:X8} {a.Graphic:X4} {a.Hue} {a.Amount} {a.X},{y} {a.Layer} {a.OnGround} {a.Multi} {a.Hidden} " +
                   $"{a.Movable} {a.TileLight} {a.TilePartialHue} {a.TileAnimated} {a.TileContainer} {a.TileStackable} {a.MultiGraphic:X4}";
        }

        private static List<GameObject> MixedList(World world, int count)
        {
            var list = new List<GameObject>();

            for (int i = 0; i < count; i++)
            {
                Item it = world.GetOrCreateItem(ITEM0 + (uint)i);
                it.Graphic = (ushort)(0x0100 + i * 7);
                it.Hue = (ushort)(i * 3);
                it.Amount = (ushort)(i % 3 == 0 ? 5 : 1);
                it.X = (ushort)(1400 + i);
                it.Y = (ushort)(1600 + i);
                it.Z = (sbyte)(i % 20);
                it.Flags = i % 4 == 0 ? Flags.Movable : Flags.None;
                it.IsMulti = i % 11 == 10;

                // every fifth item is not synced (its record keeps the creation values)
                if (i % 5 != 0)
                {
                    world.Ecs.Sync(it);
                }

                list.Add(it);

                if (i % 7 == 0)
                {
                    list.Add(world.GetOrCreateMobile(0x0000_1000 + (uint)i));
                }
            }

            return list;
        }

        // legacy-flip (Codex: the prefetch stage was untested): DrawWorldRenderList calls Prefetch(list, i) before it draws
        // entry i. With any distances and either ecs_item_draw_row state, that loop reads the same draw input for every entry,
        // in the same order, as the loop without prefetch, and leaves the list as it was.
        [Theory]
        [MemberData(nameof(PrefetchCases))]
        public void Prefetch_Does_Not_Change_What_Is_Drawn_Or_The_Order(int obj, int node, int row, bool drawRow)
        {
            var world = new World();
            StaticTiles[] tiles = Tiles();
            GameObject[] list = MixedList(world, 60).ToArray();
            GameObject[] before = (GameObject[])list.Clone();

            var want = new List<string>();

            for (int i = 0; i < list.Length; i++)
            {
                want.Add(ReadDraw(list[i], tiles, drawRow));
            }

            var got = new List<string>();

            for (int i = 0; i < list.Length; i++)
            {
                LegacySpriteSink.Prefetch(list, i, (obj, node, row), drawRow);
                got.Add(ReadDraw(list[i], tiles, drawRow));
            }

            Assert.Equal(want, got);
            Assert.Equal(before, list);

            // and the row state's read is the component read (the prefetch picked no stale input)
            for (int i = 0; i < list.Length; i++)
            {
                Assert.Equal(ReadDraw(list[i], tiles, !drawRow), got[i]);
            }
        }

        // lists shorter than the distances, the first and last entries, an empty list, a list of mobiles only, and a null entry
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Prefetch_Edge_Cases_Read_Nothing_Out_Of_Range(bool drawRow)
        {
            var world = new World();
            StaticTiles[] tiles = Tiles();
            (int, int, int) ahead = LegacySpriteSink.ParsePrefetch(null);

            LegacySpriteSink.Prefetch(ReadOnlySpan<GameObject>.Empty, 0, ahead, drawRow);
            LegacySpriteSink.Prefetch(ReadOnlySpan<GameObject>.Empty, 0, (64, 64, 64), drawRow);

            foreach (int count in new[] { 1, 2, 3, 4, 5, 8, 9, 16, 17 })
            {
                var w = new World();
                GameObject[] list = MixedList(w, count).ToArray();
                var want = new List<string>();

                foreach (GameObject o in list)
                {
                    want.Add(ReadDraw(o, tiles, drawRow));
                }

                for (int i = 0; i < list.Length; i++)
                {
                    LegacySpriteSink.Prefetch(list, i, ahead, drawRow);
                    LegacySpriteSink.Prefetch(list, i, (count, count, count), drawRow);
                    Assert.Equal(want[i], ReadDraw(list[i], tiles, drawRow));
                }
            }

            GameObject[] mobiles = { world.GetOrCreateMobile(0x2000), world.GetOrCreateMobile(0x2001), world.GetOrCreateMobile(0x2002) };
            Item it = world.GetOrCreateItem(ITEM0 + 500);
            world.Ecs.Sync(it);
            GameObject[] withNull = { it, null, mobiles[0], it };

            for (int i = 0; i < mobiles.Length; i++)
            {
                LegacySpriteSink.Prefetch(mobiles, i, (1, 1, 1), drawRow);
                LegacySpriteSink.Prefetch(mobiles, i, ahead, drawRow);
            }

            string itemRead = ReadDraw(it, tiles, drawRow);

            for (int i = 0; i < withNull.Length; i++)
            {
                LegacySpriteSink.Prefetch(withNull, i, (1, 1, 1), drawRow);
                LegacySpriteSink.Prefetch(withNull, i, (2, 2, 2), drawRow);
            }

            Assert.Equal(itemRead, ReadDraw(it, tiles, drawRow));
            Assert.True(it.TryScalarEntity(out _));
        }

        // the frame's entry point uses the sink's distances (the defaults in a release build and without CUO_LEGACY_PREFETCH)
        [Fact]
        public void Default_Prefetch_Distances_Are_16_8_4()
        {
            Assert.Equal((16, 8, 4), LegacySpriteSink.ParsePrefetch(null));

            if (Environment.GetEnvironmentVariable("CUO_LEGACY_PREFETCH") == null)
            {
                Assert.Equal((16, 8, 4), LegacySpriteSink.PrefetchDistances);
            }
        }
    }
}
