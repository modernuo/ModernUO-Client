#if CUO_DEVTOOLS
using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // anim_freeze (AnimFreeze, developer builds): a repeating mobile's frame holds while frozen and advances again once cleared
    public partial class WorldWriterMobileTests
    {
        [Fact]
        public void A_Frozen_Mobile_Holds_Its_Frame_And_Resumes_When_Cleared()
        {
            World world = BuildT5b(T5bStart.Repeating, out uint serial);
            Mobile m = world.Mobiles.Get(serial);
            FakeAdvanceSource src = T5bDataOf(T5bData.People);
            bool saved = AnimFreeze.On;

            try
            {
                AnimFreeze.On = true;
                string held = Describe(m);

                for (int i = 0; i < 100; i++)
                {
                    Time.Ticks += 37;
                    NewUpdateNoSteps(world, m, src).Should().BeTrue();
                    Describe(m).Should().Be(held, "frame {0} while frozen", i);
                }

                AnimFreeze.On = false;
                int changed = 0;

                for (int i = 0; i < 100; i++)
                {
                    string before = Describe(m);
                    Time.Ticks += 37;
                    NewUpdateNoSteps(world, m, src).Should().BeTrue();
                    changed += Describe(m) != before ? 1 : 0;
                }

                changed.Should().BeGreaterThan(0, "the animation resumes once the freeze is cleared");
            }
            finally
            {
                AnimFreeze.On = saved;
            }
        }
    }
}
#endif
