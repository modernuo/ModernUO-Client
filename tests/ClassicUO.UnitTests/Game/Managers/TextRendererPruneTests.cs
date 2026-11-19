using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    // text_prune_expired: Update unlinks the texts the draw pass skips for being expired (Time < Time.Ticks). The live texts keep
    // their order, so the pass draws the same texts either way; with CUO_TEXT_PRUNE_CHECK's checker on, no mismatch is reported.
    [Collection("TextRendererStatics")]
    public class TextRendererPruneTests : IDisposable
    {
        // Time.Ticks is a shared static that unrelated tests set: the cases that do not move it use times no uint can straddle
        private const long Dead = -5, Live = 5_000_000_000;
        private const uint T0 = 1_000_000;
        private readonly uint _ticks;
        private readonly List<string> _mismatches = new List<string>();

        public TextRendererPruneTests()
        {
            _ticks = Time.Ticks;
            Time.Ticks = T0;
#if CUO_DEVTOOLS
            TextRenderer.PruneCheckOverride = true;
            TextRenderer.PruneMismatchObserver = _mismatches.Add;
#endif
        }

        public void Dispose()
        {
            Time.Ticks = _ticks;
            TextRenderer.PruneOverrideForTests = null;
#if CUO_DEVTOOLS
            TextRenderer.PruneCheckOverride = null;
            TextRenderer.PruneMismatchObserver = null;
#endif
        }

        private static (TextRenderer, TextObject[]) Build(bool on, params long[] times)
        {
            TextRenderer.PruneOverrideForTests = on;
            var r = new TextRenderer(new World());
            var objs = new TextObject[times.Length];

            for (int i = 0; i < times.Length; i++)
            {
                objs[i] = TextObject.Create(null);
                objs[i].Time = times[i];
                r.AddMessage(objs[i]);
            }

            return (r, objs);
        }

        // the texts the draw pass would draw (not expired), in the order it visits them: from the tail back to the head
        private static List<TextObject> Visited(TextRenderer r)
        {
            var all = new List<TextObject>();

            for (TextObject t = r.DRight; t != null; t = t.DRight)
            {
                all.Add(t);
            }

            all.Reverse();

            return all.Where(t => t.Time >= Time.Ticks).ToList();
        }

        private static int Linked(TextRenderer r)
        {
            int n = 0;

            for (TextObject t = r.DRight; t != null; t = t.DRight)
            {
                n++;
            }

            return n;
        }

        [Fact]
        public void Off_Leaves_Expired_Texts_Linked()
        {
            var (r, _) = Build(false, Dead, Live, Dead, Live);

            r.Update();

            Linked(r).Should().Be(4);
        }

        [Fact]
        public void On_Unlinks_Expired_Texts_And_Keeps_The_Order_Of_The_Rest()
        {
            var (r, objs) = Build(true, Dead, Live + 1, Dead, Live + 2, Live);
            var before = Visited(r);

            r.Update();

            Linked(r).Should().Be(3);
            Visited(r).Should().Equal(before);
            objs[0].DRight.Should().BeNull();
            objs[0].DLeft.Should().BeNull();
            objs[2].DLeft.Should().BeNull();
            _mismatches.Should().BeEmpty();
        }

        [Fact]
        public void Texts_Expiring_Later_Go_Without_Another_Add()
        {
            var (r, _) = Build(true, T0 + 100, T0 + 10, T0 + 50);

            Time.Ticks = T0;
            r.Update();
            Linked(r).Should().Be(3);

            Time.Ticks = T0 + 11;
            r.Update();
            Linked(r).Should().Be(2);

            Time.Ticks = T0 + 101;
            r.Update();
            Linked(r).Should().Be(0);
            _mismatches.Should().BeEmpty();
        }

        [Fact]
        public void A_Text_Added_After_A_Prune_Is_Seen_By_The_Next_One()
        {
            var (r, _) = Build(true, T0 + 1000);

            Time.Ticks = T0;
            r.Update();

            var late = TextObject.Create(null);
            late.Time = T0 + 5;
            r.AddMessage(late);

            Time.Ticks = T0 + 6;
            r.Update();

            Linked(r).Should().Be(1);
            late.DLeft.Should().BeNull();
            _mismatches.Should().BeEmpty();
        }

        [Fact]
        public void Draw_Start_Is_The_Tail_Of_The_Pruned_List()
        {
            var (r, objs) = Build(true, Dead, Live, Live + 1);

            r.Update();

            TextRenderer.TailCacheOn.Should().BeFalse();
            r.Tail.Should().BeSameAs(objs[1]);
        }

        [Fact]
        public void Pruned_Text_Can_Still_Be_Destroyed_And_Moved()
        {
            var (r, objs) = Build(true, Dead, Live);

            r.Update();
            objs[0].Destroy();
            r.MoveToTop(objs[1]);

            Linked(r).Should().Be(1);
            objs[0].IsDestroyed.Should().BeTrue();
        }
    }
}
