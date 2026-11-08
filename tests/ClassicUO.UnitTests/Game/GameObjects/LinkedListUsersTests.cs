using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using Xunit;

namespace ClassicUO.UnitTests.Game.GameObjects
{
    // linked-object (2026-11-07): TextContainer and EffectManager keep their own typed lists (links on TextObject and GameEffect)
    // instead of LinkedObject, and the remaining LinkedObject users (macros) keep the old API. Order, limits and unlinking as before.
    public class LinkedListUsersTests
    {
        private static List<TextObject> Forward(TextContainer c)
        {
            var list = new List<TextObject>();

            for (TextObject t = c.Items; t != null; t = t.Next)
            {
                list.Add(t);
            }

            return list;
        }

        private static List<TextObject> Backward(TextContainer c)
        {
            var list = new List<TextObject>();
            TextObject last = c.Items;

            while (last?.Next != null)
            {
                last = last.Next;
            }

            for (; last != null; last = last.Previous)
            {
                list.Insert(0, last);
            }

            return list;
        }

        [Fact]
        public void TextContainer_Keeps_Arrival_Order_Both_Ways()
        {
            var world = new World();
            var c = new TextContainer();
            var texts = new List<TextObject>();

            for (int i = 0; i < 4; i++)
            {
                TextObject t = TextObject.Create(world);
                texts.Add(t);
                c.Add(t);
            }

            Assert.Equal(texts, Forward(c));
            Assert.Equal(texts, Backward(c));
            Assert.Null(c.Items.Previous);
            Assert.Equal(4, c.Size);
            Assert.Equal(4u, c.Version);
            Assert.False(c.IsEmpty);
        }

        [Theory]
        [InlineData(5, 12)]
        [InlineData(1, 3)]
        public void TextContainer_Over_Its_Limit_Destroys_And_Unlinks_The_Oldest(int max, int count)
        {
            var world = new World();
            var c = new TextContainer { MaxSize = max };
            var texts = new List<TextObject>();

            for (int i = 0; i < count; i++)
            {
                TextObject t = TextObject.Create(world);
                texts.Add(t);
                c.Add(t);
            }

            Assert.Equal(max, c.Size);
            Assert.Equal(texts.GetRange(count - max, max), Forward(c));
            Assert.Equal(texts.GetRange(count - max, max), Backward(c));

            for (int i = 0; i < count - max; i++)
            {
                Assert.True(texts[i].IsDestroyed);
                Assert.Null(texts[i].Next);
                Assert.Null(texts[i].Previous);
            }

            for (int i = count - max; i < count; i++)
            {
                Assert.False(texts[i].IsDestroyed);
            }
        }

        [Fact]
        public void TextContainer_Clear_Destroys_And_Unlinks_Every_Text()
        {
            var world = new World();
            var c = new TextContainer();
            var texts = new List<TextObject>();

            for (int i = 0; i < 3; i++)
            {
                TextObject t = TextObject.Create(world);
                texts.Add(t);
                c.Add(t);
            }

            c.Clear();

            Assert.True(c.IsEmpty);
            Assert.Equal(0, c.Size);
            Assert.Equal(4u, c.Version);

            foreach (TextObject t in texts)
            {
                Assert.True(t.IsDestroyed);
                Assert.Null(t.Next);
                Assert.Null(t.Previous);
            }
        }

        [Fact]
        public void TextContainer_Readding_A_Text_Moves_It_To_The_Back()
        {
            var world = new World();
            var c = new TextContainer();
            TextObject a = TextObject.Create(world), b = TextObject.Create(world), d = TextObject.Create(world);
            c.Add(a);
            c.Add(b);
            c.Add(d);

            c.Add(a);

            Assert.Equal(new[] { b, d, a }, Forward(c));
            Assert.Equal(new[] { b, d, a }, Backward(c));
        }

        private static List<GameEffect> Effects(EffectManager em)
        {
            var list = new List<GameEffect>();

            for (GameEffect e = em.Items; e != null; e = e.Next)
            {
                Assert.Same(list.Count == 0 ? null : list[^1], e.Previous);
                list.Add(e);
            }

            return list;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(4)]
        public void EffectManager_Destroy_Unlinks_One_Effect_And_Keeps_The_Order(int victim)
        {
            var world = new World();
            var em = new EffectManager(world);
            var all = new List<GameEffect>();

            for (int i = 0; i < 5; i++)
            {
                var f = new FixedEffect(world, em, (ushort)(0x3709 + i), 0, 1000, 1);
                all.Add(f);
                em.Add(f);
            }

            Assert.Equal(all, Effects(em));

            all[victim].Destroy();

            Assert.Null(all[victim].Next);
            Assert.Null(all[victim].Previous);
            all.RemoveAt(victim);
            Assert.Equal(all, Effects(em));

            var late = new FixedEffect(world, em, 0x3779, 0, 1000, 1);
            em.Add(late);
            all.Add(late);
            Assert.Equal(all, Effects(em));
        }

        [Fact]
        public void EffectManager_Remove_Of_An_Unlinked_Effect_Leaves_The_List()
        {
            var world = new World();
            var em = new EffectManager(world);
            var a = new FixedEffect(world, em, 0x3709, 0, 1000, 1);
            var b = new FixedEffect(world, em, 0x370A, 0, 1000, 1);
            em.Add(a);

            b.Destroy();

            Assert.Equal(new GameEffect[] { a }, Effects(em));
        }

        [Fact]
        public void EffectManager_Clear_Destroys_Every_Effect()
        {
            var world = new World();
            var em = new EffectManager(world);
            var all = new List<GameEffect>();

            for (int i = 0; i < 3; i++)
            {
                var f = new FixedEffect(world, em, (ushort)(0x3709 + i), 0, 1000, 1);
                all.Add(f);
                em.Add(f);
            }

            em.Clear();

            Assert.True(em.IsEmpty);

            foreach (GameEffect f in all)
            {
                Assert.True(f.IsDestroyed);
                Assert.Null(f.Next);
                Assert.Null(f.Previous);
            }
        }

        private sealed class Node : LinkedObject
        {
            public int Id;
        }

        private static List<int> Ids(LinkedObject list)
        {
            var ids = new List<int>();

            for (LinkedObject o = list.Items; o != null; o = o.Next)
            {
                ids.Add(((Node)o).Id);
            }

            return ids;
        }

        // the macro lists (MacroManager, Macro, MacroObject) still use LinkedObject: PushToBack, Insert, MoveToFront/Back,
        // Remove and GetLast as MacroManager and MacroControl call them
        [Fact]
        public void LinkedObject_Keeps_Its_Macro_List_Api()
        {
            var list = new Node();
            Node a = new() { Id = 1 }, b = new() { Id = 2 }, c = new() { Id = 3 }, d = new() { Id = 4 };

            list.PushToBack(a);
            list.PushToBack(b);
            list.Insert(a, c);
            Assert.Equal(new[] { 1, 3, 2 }, Ids(list));

            list.Insert(null, d);
            Assert.Equal(new[] { 4, 1, 3, 2 }, Ids(list));

            list.MoveToBack(d);
            list.MoveToFront(b);
            Assert.Equal(new[] { 2, 1, 3, 4 }, Ids(list));
            Assert.Same(d, list.GetLast());

            list.Remove(a);
            Assert.Null(a.Next);
            Assert.Null(a.Previous);
            Assert.Equal(new[] { 2, 3, 4 }, Ids(list));

            list.Clear();
            Assert.True(list.IsEmpty);
        }
    }
}
