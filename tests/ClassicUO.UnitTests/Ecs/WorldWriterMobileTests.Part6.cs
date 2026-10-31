using System;
using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 6 (ecs-transition-plan.md, T1): the placement fold of another mobile (the 0x77 E2 row), then group 4.
    // Same method as parts 4 and 5: a copy of the old code on one World, the funnel on another, identical starts,
    // everything compared (AssertSameWorld).
    public partial class WorldWriterMobileTests
    {
        // ---- PlaceMobile of a mobile other than the player: folded (the tile's Sync and push rewrote UpsertMobile's values)

        [Theory]
        [InlineData(Start.None, 7)]
        [InlineData(Start.Standing, 1)]
        [InlineData(Start.Walking, 2)]
        [InlineData(Start.Unplaced, 3)]
        [InlineData(Start.FullQueue, 2)]
        public void PlaceMobile_Of_Another_Mobile_Writes_No_Second_Record(Start start, int packet)
        {
            World oldWorld = Build(start), newWorld = Build(start);
            MobPacket p = Packets[packet];
            long writes = 0;

            Pair
            (
                () => OldUpsertMobile(oldWorld, p),
                () => NewUpsertMobile(newWorld, p, _ => writes = newWorld.Ecs.MobileMirrorWrites)
            );

            newWorld.Ecs.MobileMirrorWrites.Should().Be(writes, "the placement of another mobile is folded into UpsertMobile's commit");
            newWorld.Ecs.IsFolding.Should().BeFalse();
            AssertSameWorld(oldWorld, newWorld);
        }
    }
}
