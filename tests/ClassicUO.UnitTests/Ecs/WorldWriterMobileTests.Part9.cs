using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 9 (ecs-transition-plan.md, T1): the value-taking re-key commit and the remaining removal sites. Same
    // method as parts 4-8: a copy of the old code on one World, the funnel on another, identical starts, everything compared.
    public partial class WorldWriterMobileTests
    {
        // ---- group 1: the death re-key commits the ECS from its values, before the cache writes

        [Theory]
        [MemberData(nameof(RekeyCases))]
        public void RekeyDeadMobile_Commits_The_Ecs_From_Its_Values_Before_The_Cache_Writes(Start start, RekeyStart rk)
        {
            World world = BuildRekey(start, rk);
            world.Writer.ThrowOnViolation = true;
            uint from = rk == RekeyStart.AlreadyDead ? DEAD : MOB;
            Mobile m = world.Mobiles.Get(from);
            var children = new List<Item>();

            for (LinkedObject i = m.Items; i != null; i = i.Next)
            {
                children.Add((Item)i);
            }

            var mobileCommits = new List<uint>();
            var childCommits = new List<(uint Serial, uint Container)>();

            world.Ecs.MobileCommitObserver = (mob, v) =>
            {
                mob.Should().BeSameAs(m);
                mob.Serial.Should().Be(from, "the cache still has the old serial when the ECS is written");
                world.Mobiles.Get(from).Should().BeSameAs(m, "the table is not written yet");
                if (from != DEAD)
                {
                    world.Mobiles.Get(DEAD).Should().NotBeSameAs(m);
                }
                v.Serial.Should().Be(DEAD, "the commit takes the computed serial");
                mobileCommits.Add(v.Serial);
            };

            world.Ecs.CommitObserver = (item, v) =>
            {
                item.Container.Should().Be(from, "the child's cache is not written yet");
                m.Serial.Should().Be(from);
                childCommits.Add((v.Serial, v.Container));
            };

            world.Writer.RekeyDeadMobile(m).Should().Be(DEAD);
            world.Ecs.MobileCommitObserver = null;
            world.Ecs.CommitObserver = null;

            mobileCommits.Should().Equal(start == Start.DestroyedHeld ? new uint[0] : new[] { DEAD }, "one record commit (none for a destroyed mobile)");
            childCommits.Should().Equal(children.Where(c => !c.IsDestroyed && ReferenceEquals(world.Items.Get(c.Serial), c)).Select(c => (c.Serial, DEAD)), "each child once, with the new parent");
            world.Ecs.Mirror.TryGetEntity(from, out _).Should().Be(from == DEAD && start != Start.DestroyedHeld, "the old record is gone (a second death re-keys onto itself)");
        }

        [Fact]
        public void RekeyDeadMobile_Writes_The_Mirror_As_Often_As_The_Old_Path()
        {
            World world = BuildRekey(Start.Standing, RekeyStart.Plain), reference = BuildRekey(Start.Standing, RekeyStart.Plain);
            long oldMobile = reference.Ecs.MobileMirrorWrites, oldItems = reference.Ecs.ItemMirrorWrites;
            long newMobile = world.Ecs.MobileMirrorWrites, newItems = world.Ecs.ItemMirrorWrites;
            OldRekey(reference, reference.Mobiles.Get(MOB));
            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB));
            (world.Ecs.MobileMirrorWrites - newMobile).Should().Be(reference.Ecs.MobileMirrorWrites - oldMobile).And.Be(1);
            (world.Ecs.ItemMirrorWrites - newItems).Should().Be(reference.Ecs.ItemMirrorWrites - oldItems).And.BeGreaterThan(0);
            Inputs(world, DEAD).Should().Be(Inputs(reference, DEAD));
        }
    }
}
