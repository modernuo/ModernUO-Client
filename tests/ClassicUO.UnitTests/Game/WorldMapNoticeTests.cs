using System.Threading.Tasks;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// perf-15: the world map's LoadMap task no longer prints "map loaded" itself (the journal and RenderedText are
    /// game-thread only). It posts a notice; the gump's Update takes the posted count and prints once per completed load.
    /// </summary>
    public sealed class WorldMapNoticeTests
    {
        [Fact]
        public void Nothing_Posted_Takes_Zero()
        {
            new WorldMapGump.PendingNotices().Take().Should().Be(0);
        }

        [Fact]
        public void Take_Returns_Each_Post_Once()
        {
            var notices = new WorldMapGump.PendingNotices();
            notices.Post();
            notices.Post();

            notices.Take().Should().Be(2);
            notices.Take().Should().Be(0);

            notices.Post();
            notices.Take().Should().Be(1);
        }

        [Fact]
        public void Posts_From_Other_Threads_Are_Neither_Lost_Nor_Taken_Twice()
        {
            var notices = new WorldMapGump.PendingNotices();
            int taken = 0;

            Task posters = Task.WhenAll(
                Task.Run(() => { for (int i = 0; i < 10_000; i++) notices.Post(); }),
                Task.Run(() => { for (int i = 0; i < 10_000; i++) notices.Post(); }));

            while (!posters.IsCompleted)
            {
                taken += notices.Take();
            }

            posters.Wait();
            taken += notices.Take();

            taken.Should().Be(20_000);
        }
    }
}
