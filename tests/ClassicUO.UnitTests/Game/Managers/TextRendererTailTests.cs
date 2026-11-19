using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    // The text list's tail (where the draw pass starts) is kept while links change and validated by TextObject.LinkVersion; the
    // link operations (add, UnlinkD, destroy, move-to-top, clear) must leave the draw start on the walked tail.
    [Collection("TextRendererStatics")]
    public sealed class TextRendererTailTests
    {
        // Update prunes expired texts and Time.Ticks is a shared static other tests set: the texts must not expire
        private const long Live = 5_000_000_000;

        internal sealed class Probe : TextRenderer
        {
            public Probe(World world) : base(world) { }

            public TextObject Draw_Start => DrawPointer;
        }

        private static TextObject WalkTail(TextRenderer r)
        {
            TextObject t = r;

            while (t.DRight != null)
            {
                t = t.DRight;
            }

            return t;
        }

        internal static (Probe, TextObject[]) Build(int count)
        {
            var r = new Probe(new World());
            var objs = new TextObject[count];

            for (int i = 0; i < count; i++)
            {
                objs[i] = TextObject.Create(null);
                objs[i].Time = Live;
                r.AddMessage(objs[i]);
            }

            return (r, objs);
        }

        internal static void AssertStart(Probe r)
        {
            r.Update();
            r.Draw_Start.Should().BeSameAs(WalkTail(r));
            r.ProcessWorldText(false);
            r.Draw_Start.Should().BeSameAs(WalkTail(r));
        }

        [Fact]
        public void Empty_List_Starts_At_The_Head()
        {
            var (r, _) = Build(0);

            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(r);
        }

        [Fact]
        public void Add_Puts_The_Oldest_Message_At_The_Tail()
        {
            var (r, objs) = Build(4);

            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            var added = TextObject.Create(null);
            added.Time = Live;
            r.AddMessage(added);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);
            r.DRight.Should().BeSameAs(added);
        }

        [Fact]
        public void Adding_To_An_Empty_List_Makes_It_The_Tail_And_Re_Adding_Moves_It()
        {
            var (r, objs) = Build(1);

            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            var second = TextObject.Create(null);
            second.Time = Live;
            r.AddMessage(second);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            r.AddMessage(objs[0]); // an object already in the list is unlinked first, then goes to the front
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(second);
        }

        [Fact]
        public void UnlinkD_Of_The_Tail_Moves_The_Tail_Left()
        {
            var (r, objs) = Build(3);

            AssertStart(r);
            objs[0].UnlinkD();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
        }

        [Fact]
        public void UnlinkD_Of_A_Middle_Node_Keeps_The_Tail()
        {
            var (r, objs) = Build(3);

            AssertStart(r);
            objs[1].UnlinkD();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);
        }

        [Fact]
        public void Destroy_Of_The_Only_Message_Leaves_The_Head()
        {
            var (r, objs) = Build(1);

            AssertStart(r);
            objs[0].Destroy();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(r);
        }

        [Fact]
        public void MoveToTop_Of_The_Tail_Changes_The_Tail()
        {
            var (r, objs) = Build(3);

            AssertStart(r);
            r.MoveToTop(objs[0]);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            r.DRight.Should().BeSameAs(objs[0]);

            r.MoveToTop(objs[2]); // a middle node
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
        }

        [Fact]
        public void ToTopD_Moves_To_The_Front_Of_Its_Renderer()
        {
            var (r, objs) = Build(3);

            AssertStart(r);
            objs[0].ToTopD();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
        }

        [Fact]
        public void Clear_Resets_To_The_Head_And_The_List_Grows_Again()
        {
            var (r, objs) = Build(3);

            AssertStart(r);
            r.Clear();
            objs[0].IsDestroyed.Should().BeTrue();
            r.DRight.Should().BeNull();
            r.Draw_Start.Should().BeNull(); // Clear leaves the pointer null; the next pass sets it

            r.Update();
            r.Draw_Start.Should().BeSameAs(r);

            var again = TextObject.Create(null);
            again.Time = Live;
            r.AddMessage(again);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(again);
        }

        [Fact]
        public void The_Collision_Pass_Reaches_The_Same_Tail()
        {
            var (r, objs) = Build(5);

            r.ProcessWorldText(true);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            objs[0].UnlinkD();
            r.ProcessWorldText(true);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            r.ProcessWorldText(false);
            r.Draw_Start.Should().BeSameAs(objs[1]);
        }

        [Fact]
        public void Two_Renderers_Do_Not_Disturb_Each_Others_Tail()
        {
            var (a, aObjs) = Build(2);
            var b = new Probe(new World());
            var bObj = TextObject.Create(null);
            bObj.Time = Live;
            b.AddMessage(bObj);

            AssertStart(a);
            AssertStart(b);
            bObj.Destroy(); // changes the shared link version, not a's links
            AssertStart(a);
            AssertStart(b);
            a.Draw_Start.Should().BeSameAs(aObjs[0]);
            b.Draw_Start.Should().BeSameAs(b);
        }

        [Fact]
        public void A_Randomised_Sequence_Of_Link_Operations_Keeps_The_Start_On_The_Walked_Tail()
        {
            {
                var (r, objs) = Build(6);
                var rng = new Random(12345);

                for (int step = 0; step < 400; step++)
                {
                    TextObject o = objs[rng.Next(objs.Length)];

                    switch (rng.Next(5))
                    {
                        case 0: r.AddMessage(o); break;
                        case 1: o.UnlinkD(); break;
                        case 2: r.MoveToTop(o); break;
                        case 3: if (o.DLeft != null) { o.ToTopD(); } break;
                        default: r.ProcessWorldText(rng.Next(2) == 0); break;
                    }

                    // a node unlinked from the middle has no list; ToTopD on one would use itself as the head
                    if (o.DLeft == null && o.DRight == null)
                    {
                        r.AddMessage(o);
                    }

                    AssertStart(r);
                }
            }
        }

        [Fact]
        public void A_Valid_Kept_Tail_Is_Not_Walked_Again()
        {
            var (r, objs) = Build(3);

            AssertStart(r);

            // a writer that skips LinkVersion: only a kept tail that is really served stays stale
            var extra = TextObject.Create(null);
            extra.Time = Live;
            objs[0].DRight = extra;
            extra.DLeft = objs[0];

            r.Update();
            r.Draw_Start.Should().BeSameAs(objs[0]);

            TextObject.LinkVersion++; // any real link change invalidates it
            r.Update();
            r.Draw_Start.Should().BeSameAs(extra);
        }
    }
}
