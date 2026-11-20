using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game;
using ClassicUO.Renderer;
using ClassicUO.Renderer.Animations;
using FluentAssertions;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    // atlas_lru: the page LRU of the Art, Gump, Texmap, Light and glyph atlases over fake pages (no GPU), and the switch.
    [Collection(nameof(AnimAtlasLruCollection))]
    public class AtlasPageLruTests : IDisposable
    {
        private readonly long _savedNow = AtlasPageClock.NowMs;

        public void Dispose()
        {
            AtlasLruSetup.OnOverride = null;
            AtlasPageClock.NowMs = _savedNow;
#if CUO_DEVTOOLS
            AtlasPageLru.Check = false;
            AtlasPageLru.MismatchObserver = null;
#endif
        }

        private sealed class FakePages : IAtlasPages
        {
            public readonly List<Texture2D> List = new List<Texture2D>();
            public readonly List<Texture2D> Removed = new List<Texture2D>();
            private int _serial;

            public int TexturesCount => List.Count;

            public Texture2D GetPage(int index) => List[index];

            public void RemovePage(int index)
            {
                Removed.Add(List[index]);
                List.RemoveAt(index);
            }

            public Texture2D Add(long lastUseMs)
            {
                var texture = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
                GC.SuppressFinalize(texture);
                texture.Tag = new AtlasPage(_serial++, lastUseMs);
                List.Add(texture);

                return texture;
            }
        }

        private static AtlasLruPolicy Policy(int budget = 2, int floor = 1, int ceiling = 4) =>
            new AtlasLruPolicy(budget, floor, ceiling) { MinAgeMs = 45_000, ThrashWindowMs = 60_000, GrowCooldownMs = 5_000 };

        // drop callback of an owner: forgets the keys it is given, as Art/Gump/Texmap/Light do
        private static (AtlasPageLru Lru, FakePages Pages, List<(Texture2D, uint)> Dropped) Make(AtlasLruPolicy policy)
        {
            var pages = new FakePages();
            var dropped = new List<(Texture2D, uint)>();
            AtlasPageLru lru = null;
            lru = new AtlasPageLru("test", pages, 16L << 20, policy, (page, keys) =>
            {
                foreach (uint key in keys)
                {
                    dropped.Add((page, key));
                    lru.Forgot(key, 10, 20);
                }
            });

            return (lru, pages, dropped);
        }

        [Fact]
        public void Pages_above_the_budget_go_oldest_first_and_their_entries_are_dropped()
        {
            var (lru, pages, dropped) = Make(Policy(budget: 2));
            AtlasPageClock.NowMs = 0;
            var old = pages.Add(0);
            lru.Uploaded(old, 7, 10, 20);
            lru.Uploaded(old, 8, 10, 20);
            var recent = pages.Add(0);
            lru.Uploaded(recent, 9, 10, 20);
            pages.Add(0); // open page
            ((AtlasPage)recent.Tag).LastUseMs = 80_000;
            int epoch = AtlasPageClock.FreedEpoch;

            lru.Tick(100_000, unfocused: false);

            // low water of a 2-page budget is 1 page, but the recent page is younger than the min age and the open page stays
            pages.Removed.Should().Equal(old);
            dropped.Should().Equal((old, 7u), (old, 8u));
            lru.Policy.EvictedPages.Should().Be(1);
            lru.Policy.EvictedDirections.Should().Be(2);
            AtlasPageClock.FreedEpoch.Should().Be(epoch + 1);
        }

        [Fact]
        public void Within_the_budget_nothing_is_freed()
        {
            var (lru, pages, _) = Make(Policy(budget: 2));
            pages.Add(0);
            pages.Add(0);

            lru.Tick(1_000_000, unfocused: false);

            pages.Removed.Should().BeEmpty();
        }

        [Fact]
        public void The_open_page_is_never_freed()
        {
            var (lru, pages, _) = Make(Policy(budget: 1, floor: 1, ceiling: 1));
            pages.Add(0);
            pages.Add(0);
            var open = pages.Add(0);

            lru.Tick(1_000_000, unfocused: false);

            pages.List.Should().Equal(open);
        }

        [Fact]
        public void A_dropped_entry_uploaded_again_soon_is_a_thrash_reload_that_grows_the_budget()
        {
            var (lru, pages, _) = Make(Policy(budget: 2, ceiling: 4));
            AtlasPageClock.NowMs = 0;
            var p = pages.Add(0);
            lru.Uploaded(p, 7, 10, 20);
            pages.Add(0);
            pages.Add(0);
            lru.Policy.Tick(0, false);
            AtlasPageClock.NowMs = 100_000;
            lru.Tick(100_000, unfocused: false);
            lru.RememberedEvictions.Should().Be(1);

            AtlasPageClock.NowMs = 110_000;
            lru.Uploaded(pages.List[^1], 7, 10, 20);

            lru.Policy.Reloads.Should().Be(1);
            lru.Policy.ThrashReloads.Should().Be(1);
            lru.Policy.BudgetPages.Should().Be(3);
            lru.RememberedEvictions.Should().Be(0);
        }

        [Fact]
        public void A_new_key_is_not_a_reload()
        {
            var (lru, pages, _) = Make(Policy());
            lru.Uploaded(pages.Add(0), 7, 10, 20);

            lru.Policy.Reloads.Should().Be(0);
        }

        [Fact]
        public void Repeated_uploads_of_one_key_list_it_once()
        {
            var (lru, pages, _) = Make(Policy());
            var p = pages.Add(0);
            lru.Uploaded(p, 7, 10, 20);
            lru.Uploaded(p, 7, 10, 20);

            ((AtlasPage)p.Tag).Directions.Should().Equal(7u);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void The_checker_reports_a_reload_of_another_size()
        {
            AtlasPageLru.Check = true;
            var lines = new List<string>();
            AtlasPageLru.MismatchObserver = lines.Add;
            var (lru, pages, _) = Make(Policy(budget: 1, floor: 1, ceiling: 1));
            AtlasPageClock.NowMs = 0;
            lru.Uploaded(pages.Add(0), 7, 10, 20);
            lru.Uploaded(pages.Add(0), 8, 10, 20);
            pages.Add(0);
            lru.Tick(100_000, unfocused: false);

            lru.Uploaded(pages.List[^1], 7, 10, 20); // the size it had (Make's drop records 10x20)
            lru.Uploaded(pages.List[^1], 8, 11, 20);

            lines.Should().ContainSingle().Which.Should().Contain("test 0x8").And.Contain("11x20");
        }

        [Fact]
        public void The_checker_reports_a_draw_from_a_freed_page_only()
        {
            AtlasPageLru.Check = true;
            var lines = new List<string>();
            AtlasPageLru.MismatchObserver = lines.Add;

            AtlasPageLru.CheckUse(null, "test");

            lines.Should().BeEmpty();
        }
#endif

        [Fact]
        public void The_switch_off_makes_no_policy()
        {
            AtlasLruSetup.Create(false, "art", 64L << 20).Should().BeNull();
        }

        [Fact]
        public void The_switch_on_sizes_each_atlas_from_its_budget()
        {
            AtlasLruSetup.OnOverride = true;
            AtlasLruSetup.On.Should().BeTrue();
            var art = AtlasLruSetup.Create(true, "art", 64L << 20);
            (art.BudgetPages, art.FloorPages, art.CeilingPages).Should().Be((4, 2, 16));
            var font = AtlasLruSetup.Create(true, "font", 16L << 20);
            (font.BudgetPages, font.FloorPages, font.CeilingPages).Should().Be((2, 1, 8));
            AtlasLruSetup.OnOverride = false;
            AtlasLruSetup.On.Should().BeFalse();
        }

        [Fact]
        public void An_atlas_that_never_opened_a_page_disposes()
        {
            var atlas = new TextureAtlas(null, 16, 16, SurfaceFormat.Color);

            atlas.Invoking(a => a.Dispose()).Should().NotThrow();
        }
    }
}
