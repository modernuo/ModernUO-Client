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
