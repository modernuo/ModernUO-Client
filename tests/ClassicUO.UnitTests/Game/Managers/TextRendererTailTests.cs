using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    // text_tail_cache: the text list's tail (where the draw pass starts) is kept while links change and validated by
    // TextObject.LinkVersion. Every test runs with the switch off and on and, in developer builds, with CUO_TEXT_ORDER_CHECK's
    // checker on, which must report no mismatch for the link operations (add, UnlinkD, destroy, move-to-top, clear).
    [Collection("TextRendererStatics")]
    public sealed class TextRendererTailTests : IDisposable
    {
        private sealed class Probe : TextRenderer
        {
            public Probe(World world) : base(world) { }

            public TextObject Draw_Start => DrawPointer;
        }

        private readonly List<string> _mismatches = new List<string>();

        public TextRendererTailTests()
        {
#if CUO_DEVTOOLS
            TextRenderer.CheckOverride = true;
            TextRenderer.MismatchObserver = _mismatches.Add;
#endif
        }

        public void Dispose()
        {
            TextRenderer.OverrideForTests = null;
#if CUO_DEVTOOLS
            TextRenderer.CheckOverride = null;
            TextRenderer.MismatchObserver = null;
#endif
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

        private static (Probe, TextObject[]) Build(int count, bool on)
        {
            TextRenderer.OverrideForTests = on;
            var r = new Probe(new World());
            var objs = new TextObject[count];

            for (int i = 0; i < count; i++)
            {
                objs[i] = TextObject.Create(null);
                r.AddMessage(objs[i]);
            }

            return (r, objs);
        }

        private static void AssertStart(Probe r)
        {
            r.Update();
            r.Draw_Start.Should().BeSameAs(WalkTail(r));
            r.ProcessWorldText(false);
            r.Draw_Start.Should().BeSameAs(WalkTail(r));
        }

        private void AssertClean()
        {
            _mismatches.Should().BeEmpty();
#if CUO_DEVTOOLS
            TextRenderer.Checks.Should().BeGreaterThan(0);
#endif
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Empty_List_Starts_At_The_Head(bool on)
        {
            var (r, _) = Build(0, on);

            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(r);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Add_Puts_The_Oldest_Message_At_The_Tail(bool on)
        {
            var (r, objs) = Build(4, on);

            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            var added = TextObject.Create(null);
            r.AddMessage(added);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);
            r.DRight.Should().BeSameAs(added);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Adding_To_An_Empty_List_Makes_It_The_Tail_And_Re_Adding_Moves_It(bool on)
        {
            var (r, objs) = Build(1, on);

            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            var second = TextObject.Create(null);
            r.AddMessage(second);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            r.AddMessage(objs[0]); // an object already in the list is unlinked first, then goes to the front
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(second);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnlinkD_Of_The_Tail_Moves_The_Tail_Left(bool on)
        {
            var (r, objs) = Build(3, on);

            AssertStart(r);
            objs[0].UnlinkD();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnlinkD_Of_A_Middle_Node_Keeps_The_Tail(bool on)
        {
            var (r, objs) = Build(3, on);

            AssertStart(r);
            objs[1].UnlinkD();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[0]);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Destroy_Of_The_Only_Message_Leaves_The_Head(bool on)
        {
            var (r, objs) = Build(1, on);

            AssertStart(r);
            objs[0].Destroy();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(r);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MoveToTop_Of_The_Tail_Changes_The_Tail(bool on)
        {
            var (r, objs) = Build(3, on);

            AssertStart(r);
            r.MoveToTop(objs[0]);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            r.DRight.Should().BeSameAs(objs[0]);

            r.MoveToTop(objs[2]); // a middle node
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ToTopD_Moves_To_The_Front_Of_Its_Renderer(bool on)
        {
            var (r, objs) = Build(3, on);

            AssertStart(r);
            objs[0].ToTopD();
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Clear_Resets_To_The_Head_And_The_List_Grows_Again(bool on)
        {
            var (r, objs) = Build(3, on);

            AssertStart(r);
            r.Clear();
            objs[0].IsDestroyed.Should().BeTrue();
            r.DRight.Should().BeNull();
            r.Draw_Start.Should().BeNull(); // Clear leaves the pointer null; the next pass sets it

            r.Update();
            r.Draw_Start.Should().BeSameAs(r);

            var again = TextObject.Create(null);
            r.AddMessage(again);
            AssertStart(r);
            r.Draw_Start.Should().BeSameAs(again);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Collision_Pass_Reaches_The_Same_Tail(bool on)
        {
            var (r, objs) = Build(5, on);

            r.ProcessWorldText(true);
            r.Draw_Start.Should().BeSameAs(objs[0]);

            objs[0].UnlinkD();
            r.ProcessWorldText(true);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            r.ProcessWorldText(false);
            r.Draw_Start.Should().BeSameAs(objs[1]);
            AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Two_Renderers_Do_Not_Disturb_Each_Others_Tail(bool on)
        {
            var (a, aObjs) = Build(2, on);
            var b = new Probe(new World());
            var bObj = TextObject.Create(null);
            b.AddMessage(bObj);

            AssertStart(a);
            AssertStart(b);
            bObj.Destroy(); // changes the shared link version, not a's links
            AssertStart(a);
            AssertStart(b);
            a.Draw_Start.Should().BeSameAs(aObjs[0]);
            b.Draw_Start.Should().BeSameAs(b);
            AssertClean();
        }

        [Fact]
        public void A_Randomised_Sequence_Of_Link_Operations_Keeps_The_Start_On_The_Walked_Tail()
        {
            foreach (bool on in new[] { false, true })
            {
                var (r, objs) = Build(6, on);
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

            AssertClean();
        }

#if CUO_DEVTOOLS
        [Fact]
        public void The_Checker_Reports_A_Link_Change_That_Missed_The_Version()
        {
            var (r, objs) = Build(3, true);

            AssertStart(r);
            long before = TextRenderer.Mismatches;

            // a writer that skips LinkVersion: the kept tail is stale at the next use
            var extra = TextObject.Create(null);
            objs[0].DRight = extra;
            extra.DLeft = objs[0];

            r.Update();
            TextRenderer.Mismatches.Should().BeGreaterThan(before);
            _mismatches.Should().Contain(m => m.Contains("kept tail differs"));
            r.Draw_Start.Should().BeSameAs(extra); // the checker's own use falls back to the walk
        }

        [Fact]
        public void The_Checker_Reports_A_Broken_Back_Link()
        {
            var (r, objs) = Build(3, false);
            long before = TextRenderer.Mismatches;

            objs[1].DLeft = null;
            r.Update();

            TextRenderer.Mismatches.Should().BeGreaterThan(before);
            _mismatches.Should().Contain(m => m.Contains("link order"));
        }
#endif
    }
}
