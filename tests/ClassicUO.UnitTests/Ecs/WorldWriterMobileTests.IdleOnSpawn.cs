using System;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // idle_on_spawn (mount-fix): on, a created mobile's first idle is due at once (IdleDue 0); off, it is the steady-state
    // residual of the 30-60 s idle delay.
    public partial class WorldWriterMobileTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Created_Mobile_First_Idle_Due(bool idleOnSpawn)
        {
            IdleOnSpawn.OverrideForTests = idleOnSpawn;

            try
            {
                World world = BuildPlayer(out _);
                var p = new MobPacket { Serial = NEWMOB, Graphic = 0xC8, X = 1000, Y = 1000, Z = 0, Direction = Direction.North };
                long now = Time.Ticks;
                Mobile m = NewUpsertMobile(world, in p);

                if (idleOnSpawn)
                {
                    Assert.Equal(0, m.Timer.IdleDue);
                }
                else
                {
                    Assert.InRange(m.Timer.IdleDue, now, now + 60000);
                }
            }
            finally
            {
                IdleOnSpawn.OverrideForTests = null;
            }
        }

        [Fact]
        public void First_Idle_Delay_Is_The_Steady_State_Residual()
        {
            var rng = new Random(1234);
            const int N = 100_000;
            double sum = 0;
            int[] bins = new int[6];

            for (int i = 0; i < N; i++)
            {
                long ms = Mobile.FirstIdleDelayMs(rng.NextDouble(), rng.NextDouble());
                Assert.InRange(ms, 0, 60000);
                sum += ms;
                bins[Math.Min(5, (int)(ms / 10000))]++;
            }

            // mean E[L^2] / (2 E[L]) = (45^2 + 75) / 90 = 23.3 s for this density (not 25 s)
            Assert.InRange(sum / N / 1000.0, 23.0, 23.7);

            // density 1/45 per s on 0-30 s: 10 s bins hold 2/9 each; then linear to 0 at 60 s: 30-40 s 5/27, 40-50 s 3/27,
            // 50-60 s 1/27
            double[] expect = { 2 / 9.0, 2 / 9.0, 2 / 9.0, 5 / 27.0, 3 / 27.0, 1 / 27.0 };

            for (int b = 0; b < 6; b++)
            {
                Assert.InRange(bins[b] / (double)N, expect[b] - 0.006, expect[b] + 0.006);
            }

            // about 2 % idle within the first second
            int first = 0;

            for (int i = 0; i < N; i++)
            {
                if (Mobile.FirstIdleDelayMs(rng.NextDouble(), rng.NextDouble()) < 1000)
                {
                    first++;
                }
            }

            Assert.InRange(first / (double)N, 0.019, 0.025);
        }
    }
}
