using ClassicUO.Assets;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // ClilocLoader.Translate keeps the results for empty arguments (bounded); the text must be what the uncached
    // path gives, and a new state (ClearResources) must drop them.
    public class ClilocTranslateCacheTests
    {
        // no data loaded: every number is "missing", which is deterministic and enough to see caching
        [Fact]
        public void Empty_Arguments_Reuse_One_String_Per_Number_And_Case()
        {
            var c = new ClilocLoader(null);

            string a = c.Translate(1234);

            c.Translate(1234).Should().BeSameAs(a);
            c.Translate(1234, null).Should().BeSameAs(a);
            c.Translate(1234, "").Should().BeSameAs(a);

            string cap = c.Translate(1234, "", true);

            cap.Should().Be(ClassicUO.Utility.StringHelper.CapitalizeAllWords(a));
            c.Translate(1234, "", true).Should().BeSameAs(cap);
            c.Translate(1235).Should().NotBeSameAs(a);
        }

        [Fact]
        public void Arguments_Are_Never_Cached_And_Still_Substituted()
        {
            var c = new ClilocLoader(null);

            c.Translate(77);
            string withArg = c.Translate(77, "x\ty");

            withArg.Should().Be("MegaCliloc: missing 77 [x] [y]");
            c.Translate(77, "x\ty").Should().NotBeSameAs(withArg);
            c.Translate(77, "").Should().StartWith("MegaCliloc: missing 77").And.NotContain("[x]");
        }

        [Fact]
        public void Clear_Drops_The_Cache()
        {
            var c = new ClilocLoader(null);
            string a = c.Translate(9);

            c.ClearResources();

            c.Translate(9).Should().Be(a).And.NotBeSameAs(a);
        }

        [Fact]
        public void The_Cache_Stops_At_Its_Limit()
        {
            var c = new ClilocLoader(null);
            int over = ClilocLoader.TranslateCacheLimit + 50;

            for (int i = 0; i < over; i++)
            {
                c.Translate(100000 + i);
            }

            c.Translate(100000).Should().BeSameAs(c.Translate(100000));
            string late = c.Translate(100000 + over - 1);

            c.Translate(100000 + over - 1).Should().NotBeSameAs(late);
        }
    }
}
