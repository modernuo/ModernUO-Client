using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Friflo.Engine.ECS;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // E4: the due-time rule, the system that applies it over the clock components, and the
    // pushes that keep the clock equal to the Mobile. The in-client checker CUO_MOBILE_UPDATE_CHECK covers the paths that
    // need the running client (packets, ProcessAnimation's timers, corpses, the player's walk).
    public class MobileClockTests
    {
        private const uint SERIAL = 0x0000_3000;

        private static MobileClock Clock(long anim, long idle, MobileClockFlags flags = MobileClockFlags.None)
        {
            return new MobileClock { AnimDue = anim, IdleDue = idle, Flags = flags };
        }

        [Fact]
        public void IsDue_Needs_An_Expired_Timer_Or_A_Flag()
        {
            // ProcessAnimation: `LastAnimationChangeTime >= Ticks` returns; Update: `_lastAnimationIdleDelay < Ticks` runs the idle
            MobileClockRules.IsDue(Clock(1000, 5000), 999).Should().BeFalse();
            MobileClockRules.IsDue(Clock(1000, 5000), 1000).Should().BeFalse("equal is not expired");
            MobileClockRules.IsDue(Clock(1000, 5000), 1001).Should().BeTrue("the frame timer expired");
            MobileClockRules.IsDue(Clock(9000, 5000), 5000).Should().BeFalse();
            MobileClockRules.IsDue(Clock(9000, 5000), 5001).Should().BeTrue("the idle timer expired");
            MobileClockRules.IsDue(Clock(9000, 9000, MobileClockFlags.Stepping), 1).Should().BeTrue("steps run every frame");
            MobileClockRules.IsDue(Clock(9000, 9000, MobileClockFlags.HandlesOpening), 1).Should().BeTrue();
            MobileClockRules.IsDue(default, 1).Should().BeTrue("a fresh clock is due until the first push");
            MobileClockRules.IsDue(Clock(-5, long.MaxValue), 0).Should().BeTrue();
        }

        [Fact]
        public void System_Picks_Exactly_The_Due_Mobiles_And_Skips_Items()
        {
            var mirror = new WorldMirror();
            var system = new MobileClockSystem(mirror.Store);
            uint[] serials = { 0x10, 0x11, 0x12, 0x13, 0x14 };

            foreach (uint s in serials)
            {
                mirror.UpsertMobile(s, 0x190, 0, 10, 10, 0, 0, 0, false).AddComponent(new MobileClock());
            }

            mirror.UpsertItem(0x4000_0001, 0x0EED, 0, 10, 10, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);

            void Set(uint serial, MobileClock c)
            {
                mirror.TryGetEntity(serial, out EcsEntity e).Should().BeTrue();
                e.GetComponent<MobileClock>() = c;
            }

            Set(0x10, Clock(2000, 3000));
            Set(0x11, Clock(500, 3000));
            Set(0x12, Clock(2000, 100));
            Set(0x13, Clock(2000, 3000, MobileClockFlags.Stepping));
            Set(0x14, Clock(2000, 3000, MobileClockFlags.HandlesOpening));

            var due = new List<uint>();
            system.CollectDue(1000, due);
            due.Should().BeEquivalentTo(new uint[] { 0x11, 0x12, 0x13, 0x14 });
            system.Scanned.Should().Be(5);

            // a tag change (the mirror's dirty tag) moves entities between archetypes; all are still scanned once
            mirror.TryGetEntity(0x10, out EcsEntity moved);
            moved.RemoveTag<EquipmentDirty>();
            due.Clear();
            system.CollectDue(2500, due);
            due.Should().BeEquivalentTo(serials, "0x10's frame timer (2000) expired at 2500");
            system.Scanned.Should().Be(5);

            due.Clear();
            system.CollectDue(100, due);
            due.Should().BeEquivalentTo(new uint[] { 0x13, 0x14 }, "only the flags: 0x12's idle timer (100) is not before 100");
        }

        [Fact]
        public void Entity_Removal_Leaves_The_Scan_Consistent()
        {
            var mirror = new WorldMirror();
            var system = new MobileClockSystem(mirror.Store);
            mirror.UpsertMobile(0x20, 0x190, 0, 1, 1, 0, 0, 0, false).AddComponent(new MobileClock());
            mirror.UpsertMobile(0x21, 0x190, 0, 1, 1, 0, 0, 0, false).AddComponent(new MobileClock());
            mirror.Remove(0x20);

            var due = new List<uint>();
            system.CollectDue(10, due);
            due.Should().Equal(0x21u);
        }

        private static MobileClock Pushed(World world, uint serial)
        {
            world.Ecs.Mirror.TryGetEntity(serial, out EcsEntity e).Should().BeTrue();

            return e.GetComponent<MobileClock>();
        }

        private static void ShouldMatchMobile(World world, Mobile m)
        {
            MobileClock c = Pushed(world, m.Serial);
            c.AnimDue.Should().Be(m.LastAnimationChangeTime);
            c.IdleDue.Should().Be(m.IdleDue);
            c.Flags.Should().Be(WorldEcs.ClockFlagsOf(m.Steps.Count, m));
        }

        [Fact]
        public void New_Entity_And_Mobile_Writes_Push_The_Clock()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(SERIAL);
            m.Graphic = 0x190;
            world.Ecs.Sync(m);
            ShouldMatchMobile(world, m);
            // Mobile.Create uses the constructor that sets no timers: a new mobile is due at once, runs one Update, then its timers are set
            MobileClockRules.IsDue(Pushed(world, SERIAL), 1).Should().BeTrue();

            // SetAnimation moves the frame timer to the tick and re-rolls the idle timer
            m.SetAnimDue(12345);
            m.SetAnimation(5);
            ShouldMatchMobile(world, m);
            Pushed(world, SERIAL).AnimDue.Should().Be(Time.Ticks);

            // steps
            m.X = 100;
            m.Y = 100;
            world.Ecs.Sync(m);
            m.EnqueueStep(101, 100, 0, Direction.East, false).Should().BeTrue();
            m.Steps.Count.Should().BeGreaterThan(0);
            Pushed(world, SERIAL).Flags.Should().HaveFlag(MobileClockFlags.Stepping);
            ShouldMatchMobile(world, m);
            m.ClearSteps();
            Pushed(world, SERIAL).Flags.Should().NotHaveFlag(MobileClockFlags.Stepping);
            ShouldMatchMobile(world, m);

            // handles: GameScene pushes after NONE -> OPEN; Update turns OPEN into DISPLAYING and the system pushes again
            m.ObjectHandlesStatus = ObjectHandlesStatus.OPEN;
            world.Ecs.PushClock(m);
            Pushed(world, SERIAL).Flags.Should().HaveFlag(MobileClockFlags.HandlesOpening);
            m.ObjectHandlesStatus = ObjectHandlesStatus.DISPLAYING;
            world.Ecs.PushClock(m);
            Pushed(world, SERIAL).Flags.Should().Be(MobileClockFlags.None);
        }

        [Fact]
        public void Push_For_An_Object_World_No_Longer_Holds_Is_Ignored()
        {
            var world = new World();
            Mobile old = world.GetOrCreateMobile(SERIAL);
            old.Graphic = 0x190;
            world.Ecs.Sync(old);

            world.Mobiles.Remove(SERIAL);
            Mobile next = world.GetOrCreateMobile(SERIAL);
            next.Graphic = 0x190;
            world.Ecs.Sync(next);

            MobileClock before = Pushed(world, SERIAL);
            old.SetAnimDue(before.AnimDue + 777);
            world.Ecs.PushClock(old);
            Pushed(world, SERIAL).AnimDue.Should().Be(before.AnimDue, "the replaced Mobile doesn't write the new one's clock");
        }

        [Fact]
        public void A_Timer_Write_Keeps_The_Clock()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(SERIAL);
            m.Graphic = 0x190;
            world.Ecs.Sync(m);

            // T9 S8c: the timer is the record's; its write keeps the clock (no push, no resync)
            m.SetAnimDue(m.LastAnimationChangeTime + 4000);
            Pushed(world, SERIAL).AnimDue.Should().Be(m.LastAnimationChangeTime);

            ShouldMatchMobile(world, m);
        }

        [Fact]
        public void A_Quiet_Mobile_Is_Not_Due()
        {
            // no steps, timers in the future
            var world = new World();
            Mobile m = world.GetOrCreateMobile(SERIAL);
            m.Graphic = 0x190;
            world.Ecs.Sync(m);
            m.SetAnimDue((long)Time.Ticks + 100000);
            m.SetAnimation(0xFF);
            m.SetAnimDue((long)Time.Ticks + 100000);
            world.Ecs.PushClock(m);

            MobileClockRules.IsDue(Pushed(world, SERIAL), Time.Ticks).Should().BeFalse();
        }
        [Fact]
        public void The_Clock_Timer_And_Queue_Exist_From_The_Record_Creation()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(SERIAL);
            m.Graphic = 0x190;
            world.Ecs.Sync(m);
            world.Ecs.Mirror.TryGetEntity(SERIAL, out EcsEntity e).Should().BeTrue();

            // T9 S8c: no switch and no first update: a mobile record has them from its creation, with a new mobile's timer
            e.HasComponent<MobileClock>().Should().BeTrue();
            e.HasComponent<Steps>().Should().BeTrue();
            MobileAnimationAdvanceSystem.Same(e.GetComponent<AnimationTimer>(), AnimationTimer.MobileDefault).Should().BeTrue();

            m.SetAnimation(3);
            m.EnqueueStep(101, 100, 0, Direction.East, false);

            ShouldMatchMobile(world, m);
            Pushed(world, SERIAL).Flags.Should().HaveFlag(MobileClockFlags.Stepping);
        }

        private static List<uint> DueInMobilesOrder(World world, long now)
        {
            var serials = new List<uint>();
            world.Ecs.Clock.CollectDue(now, serials);
            var mobiles = new List<Mobile>();

            foreach (uint s in serials)
            {
                mobiles.Add(world.Mobiles[s]);
            }

            world.SortInMobilesOrder(mobiles);

            return mobiles.ConvertAll(x => x.Serial);
        }

        private static List<uint> Expected(World world, long now, Dictionary<uint, Mobile> oracle = null)
        {
            // the legacy loop: every mobile in Values order, filtered by the due rule. With an oracle, the order comes from a
            // plain Dictionary that saw the same operations (what Mobiles was before E10), not from the table under test
            var list = new List<uint>();

            foreach (Mobile m in oracle != null ? (IEnumerable<Mobile>)oracle.Values : world.Mobiles.Values)
            {
                world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e).Should().BeTrue();

                if (MobileClockRules.IsDue(e.GetComponent<MobileClock>(), now))
                {
                    list.Add(m.Serial);
                }
            }

            return list;
        }

        [Fact]
        public void Due_Mobiles_Run_In_The_Dictionarys_Order_Through_Tag_Moves_And_Slot_Reuse()
        {
            var world = new World();
            var rng = new System.Random(12345);
            var oracle = new Dictionary<uint, Mobile>();

            void Add(uint serial)
            {
                Mobile m = world.GetOrCreateMobile(serial);
                oracle[serial] = m;
                m.Graphic = 0x190;
                world.Ecs.Sync(m);
            }

            for (uint s = 1; s <= 40; s++)
            {
                Add(s);
            }

            for (int round = 0; round < 200; round++)
            {
                // archetype moves independent of the dictionary: the dirty tag comes and goes
                for (int k = 0; k < 6; k++)
                {
                    uint s = (uint)rng.Next(1, 80);

                    if (world.Ecs.Mirror.TryGetEntity(s, out EcsEntity e))
                    {
                        if (e.Tags.Has<EquipmentDirty>()) e.RemoveTag<EquipmentDirty>(); else e.AddTag<EquipmentDirty>();
                    }
                }

                // removes free slots, adds reuse them (LIFO) or append
                if (rng.Next(3) == 0)
                {
                    uint victim = (uint)rng.Next(1, 80);

                    if (world.Mobiles.TryGetValue(victim, out Mobile gone))
                    {
                        world.Mobiles.Remove(victim);
                        oracle.Remove(victim);
                        world.Ecs.Mirror.Remove(victim);
                    }
                }

                if (rng.Next(2) == 0)
                {
                    Add((uint)rng.Next(1, 80));
                }

                // random clocks: some due, some not
                foreach (Mobile m in world.Mobiles.Values)
                {
                    world.Ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e);
                    e.GetComponent<MobileClock>() = Clock(rng.Next(0, 2) == 0 ? 0 : 1000, 1000, rng.Next(8) == 0 ? MobileClockFlags.Stepping : MobileClockFlags.None);
                }

                DueInMobilesOrder(world, 500).Should().Equal(Expected(world, 500, oracle), "round " + round);
            }
        }

        [Fact]
        public void Two_Mobiles_Arriving_In_The_Same_Frame_Keep_The_Dictionarys_Order_After_A_Tag_Move()
        {
            var world = new World();

            foreach (uint s in new uint[] { 10, 11, 12 })
            {
                Mobile m = world.GetOrCreateMobile(s);
                m.Graphic = 0x190;
                world.Ecs.Sync(m);
                world.Ecs.Mirror.TryGetEntity(s, out EcsEntity e);
                e.GetComponent<MobileClock>() = Clock(0, 0, MobileClockFlags.Stepping);
            }

            DueInMobilesOrder(world, 5).Should().Equal(10u, 11u, 12u);

            // the first mobile's tag changes (equipment arrives, then the slots are rebuilt): its entity moves to another
            // archetype, after the others in the scan, but not in the dictionary
            world.Ecs.Mirror.TryGetEntity(10, out EcsEntity first);
            first.RemoveTag<EquipmentDirty>();

            var scan = new List<uint>();
            world.Ecs.Clock.CollectDue(5, scan);
            scan.Should().NotEqual(new uint[] { 10, 11, 12 }, "the scan order follows the archetypes");
            DueInMobilesOrder(world, 5).Should().Equal(10u, 11u, 12u);
        }
    }
}
