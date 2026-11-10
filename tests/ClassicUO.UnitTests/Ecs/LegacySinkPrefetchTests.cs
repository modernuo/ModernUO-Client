using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // legacy-sprites-fix: the Legacy sink's render-list prefetch (legacy_ecs_sprites on) reads nothing it could get wrong:
    // over a list of items with and without records, mobiles and stale handles it changes no record and no read, and the
    // switch-off loop never calls it (DrawWorldRenderList takes DrawRenderList then; prefetch is read from On).
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
    }
}
