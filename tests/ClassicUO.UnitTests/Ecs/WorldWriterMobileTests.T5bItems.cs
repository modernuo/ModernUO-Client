using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T5b-items (ecs-transition-plan.md): a corpse's frame advance computed in the ECS (ItemAnimationAdvanceSystem over
    // AnimationTimer and ItemData) against the legacy Item.ProcessAnimation on one World each; identical starts, the animation
    // data a stub (the real one needs the client), the advance driven every 40 ms.
    public partial class WorldWriterMobileTests
    {
        internal struct FakeCorpseSource : ICorpseAnimationSource
        {
            public int Max;

            public int MaxAnimationCount => Max;

            public void GetAnimDirection(ref byte dir, ref bool mirror) => new FakeAdvanceSource().GetAnimDirection(ref dir, ref mirror);

            // a different frame count per body, variant and direction (so a wrong input shows in the held frame); 999 has no frames
            public bool TryCorpseFrame(ushort graphic, bool second, byte storedDir, byte next, out byte frame)
            {
                frame = 0;

                if (graphic == 999)
                {
                    return false;
                }

                int count = 2 + (graphic + (second ? 3 : 0) + storedDir * 2) % 6;
                frame = next >= count ? (byte)(count - 1) : next;

                return true;
            }
        }

        // Item.ProcessAnimation from the item's fields over the stub, then its writes (OldCorpseFrame)
        private static bool OldProcess(Item corpse, FakeCorpseSource src)
        {
            if (!(corpse.LastAnimationChangeTime < Time.Ticks))
            {
                return false;
            }

            LegacyCorpse(corpse, src, out int frame, out _);
            OldCorpseFrame(corpse, frame);

            return true;
        }

        private static readonly Dictionary<uint, FakeCorpseSource> Sources = new Dictionary<uint, FakeCorpseSource>();

        private static bool LegacyCorpse(Item corpse, FakeCorpseSource src, out int frame, out long due)
        {
            frame = -1;
            due = Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY;
            byte dir = (byte)corpse.Layer;
            byte frameIndex = (byte)(corpse.AnimIndex + (corpse.ExecuteAnimation ? 1 : 0));
            ushort id = corpse.GetGraphicForAnimation();
            bool mirror = false;
            src.GetAnimDirection(ref dir, ref mirror);

            if (id < src.MaxAnimationCount && dir < 5 && src.TryCorpseFrame(id, corpse.UsedLayer, dir, frameIndex, out byte f))
            {
                frame = f;
            }

            return true;
        }

        private static bool LegacyDue(Item corpse, FakeCorpseSource src, out int frame, out long due)
        {
            frame = -1;
            due = 0;

            return corpse.LastAnimationChangeTime < Time.Ticks && LegacyCorpse(corpse, src, out frame, out due);
        }

        private static World BuildT5bItem(int layer, bool usedLayer, ushort body, byte start = 99)
        {
            World world = BuildCorpse(Start.Standing);
            world.Ecs.ItemAnimTimerEnabled = true;
            Item corpse = world.Items.Get(CORPSE);
            corpse.Amount = body;
            corpse.Layer = (Layer)layer;
            corpse.UsedLayer = usedLayer;
            corpse.AnimIndex = start; // 99 is what CheckGraphicChange sets (the first step lands on the last frame)
            corpse.LastAnimationChangeTime = 0;
            world.Ecs.Sync(corpse);

            return world;
        }

        public static IEnumerable<object[]> T5bItemCases()
        {
            foreach (ushort body in new ushort[] { 400, 401, 999, 1500 })
            {
                foreach (bool used in new[] { false, true })
                {
                    for (int layer = 0; layer < 8; layer++)
                    {
                        foreach (byte start in new byte[] { 99, 0 })
                        {
                            yield return new object[] { layer, used, body, start };
                        }
                    }
                }
            }
        }

        // the checker's counters are static (shared with other tests): compare before and after
        [Theory]
        [MemberData(nameof(T5bItemCases))]
        public void The_Ecs_Corpse_Advance_Equals_The_Legacy_ProcessAnimation(int layer, bool usedLayer, ushort body, byte start)
        {
            World oldWorld = BuildT5bItem(layer, usedLayer, body, start), newWorld = BuildT5bItem(layer, usedLayer, body, start);
            newWorld.Writer.ThrowOnViolation = true;
            Item a = oldWorld.Items.Get(CORPSE), b = newWorld.Items.Get(CORPSE);
            var src = new FakeCorpseSource { Max = 1000 };
            var system = new ItemAnimationAdvanceSystem();
            long diffs = ItemAnimationAdvanceSystem.Diffs, syncs = ItemAnimationAdvanceSystem.SyncDiffs, rnd = ItemAnimationAdvanceSystem.RandomDiffs;
            long checks = ItemAnimationAdvanceSystem.Checks, ran = ItemAnimationAdvanceSystem.Ran;
            int advanced = 0;

            for (int i = 1; i <= 60; i++)
            {
                Time.Ticks = NOW + (uint)(i * 40);
                bool oldAdvanced = false;
                Pair(() => oldAdvanced = OldProcess(a, src), () => system.TryRun(newWorld, b, src, true, (Item it, out int f, out long d) => LegacyDue(it, src, out f, out d)).Should().BeTrue());
                AssertSameWorld(oldWorld, newWorld);
                (b.AnimIndex, b.LastAnimationChangeTime).Should().Be((a.AnimIndex, a.LastAnimationChangeTime), $"tick {i}");
                newWorld.Ecs.Mirror.TryGetEntity(CORPSE, false, out EcsEntity e).Should().BeTrue();
                ItemAnimationAdvanceSystem.Same(e.GetComponent<AnimationTimer>(), b.CorpseTimer).Should().BeTrue($"the component follows the item at tick {i}");
                advanced += oldAdvanced ? 1 : 0;
            }

            Time.Ticks = NOW;
            advanced.Should().BeGreaterThan(5);
            (ItemAnimationAdvanceSystem.Ran - ran).Should().Be(advanced);
            (ItemAnimationAdvanceSystem.Checks - checks).Should().Be(60);
            (ItemAnimationAdvanceSystem.Diffs - diffs).Should().Be(0);
            (ItemAnimationAdvanceSystem.SyncDiffs - syncs).Should().Be(0);
            (ItemAnimationAdvanceSystem.RandomDiffs - rnd).Should().Be(0);
            newWorld.Writer.Violations.Should().Be(0);
            newWorld.Writer.InScope.Should().BeFalse();
        }

        [Fact]
        public void The_Ecs_Corpse_Advance_Ends_On_The_Last_Frame_Of_The_Death_Action()
        {
            World world = BuildT5bItem(3, false, 400);
            Item corpse = world.Items.Get(CORPSE);
            var src = new FakeCorpseSource { Max = 1000 };
            var system = new ItemAnimationAdvanceSystem();

            for (int i = 1; i <= 40; i++)
            {
                Time.Ticks = NOW + (uint)(i * 100);
                system.TryRun(world, corpse, src, false, null).Should().BeTrue();
            }

            Time.Ticks = NOW;
            // layer 3 is stored direction 0: count 2 + (400 + 0) % 6 = 6, so the held frame is 5
            corpse.AnimIndex.Should().Be(5);
        }

        [Fact]
        public void The_Ecs_Corpse_Advance_Declines_What_It_Cannot_Run()
        {
            var src = new FakeCorpseSource { Max = 1000 };
            var system = new ItemAnimationAdvanceSystem();
            World world = BuildT5bItem(3, false, 400);
            Item corpse = world.Items.Get(CORPSE);
            Item loot = world.Items.Get(CLOOT);
            Time.Ticks = NOW + 500;

            // not a corpse, or without the timer (the switch was off when its record was written)
            system.TryRun(world, loot, src, false, null).Should().BeFalse();

            // handles that just opened are Entity.Update's work
            corpse.ObjectHandlesStatus = ObjectHandlesStatus.OPEN;
            system.TryRun(world, corpse, src, false, null).Should().BeFalse();
            corpse.ObjectHandlesStatus = ObjectHandlesStatus.NONE;
            long due = corpse.LastAnimationChangeTime;
            system.TryRun(world, corpse, src, false, null).Should().BeTrue();
            corpse.LastAnimationChangeTime.Should().BeGreaterThan(due);

            // a world whose switch never ran has no component on its corpse
            World off = BuildCorpse(Start.Standing);
            Item plain = off.Items.Get(CORPSE);
            system.TryRun(off, plain, src, false, null).Should().BeFalse();

            // destroyed
            corpse.Destroy();
            system.TryRun(world, corpse, src, false, null).Should().BeFalse();
            Time.Ticks = NOW;
        }

        [Fact]
        public void A_Recreated_Corpse_Under_The_Same_Serial_Keeps_Its_Own_Timer()
        {
            var src = new FakeCorpseSource { Max = 1000 };
            var system = new ItemAnimationAdvanceSystem();
            World world = BuildT5bItem(3, false, 400);
            Item first = world.Items.Get(CORPSE);
            Time.Ticks = NOW + 500;
            system.TryRun(world, first, src, false, null).Should().BeTrue();
            byte firstFrame = first.AnimIndex;

            first.Destroy();
            world.DropDestroyedItems();

            Item second = world.GetOrCreateItem(CORPSE);
            second.Graphic = 0x2006;
            second.Amount = 401;
            second.Layer = (Layer)2;
            second.AnimIndex = 99;
            second.LastAnimationChangeTime = 0;
            second.SetInWorldTile(1002, 1000, 0);
            world.Ecs.Sync(second);

            // the stale instance never reaches the new record; the new one runs from its own fields
            system.TryRun(world, first, src, false, null).Should().BeFalse();
            first.AnimIndex.Should().Be(0);
            world.Ecs.Mirror.TryGetEntity(CORPSE, false, out EcsEntity e).Should().BeTrue();
            ItemAnimationAdvanceSystem.Same(e.GetComponent<AnimationTimer>(), second.CorpseTimer).Should().BeTrue();
            system.TryRun(world, second, src, false, null).Should().BeTrue();
            second.AnimIndex.Should().NotBe(99);
            second.AnimIndex.Should().BeLessThan(7);
            firstFrame.Should().BeLessThan(7);
            Time.Ticks = NOW;
        }

        [Fact]
        public void A_Replaced_But_Undestroyed_Instance_Neither_Runs_Nor_Overwrites_The_Record()
        {
            var src = new FakeCorpseSource { Max = 1000 };
            var system = new ItemAnimationAdvanceSystem();
            World world = BuildT5bItem(3, false, 400);
            Item stale = world.Items.Get(CORPSE);

            // the table now holds another object under the serial (the old one was never destroyed)
            world.Items.Remove(CORPSE);
            Item fresh = world.GetOrCreateItem(CORPSE);
            fresh.Graphic = 0x2006;
            fresh.Amount = 401;
            fresh.Layer = (Layer)2;
            fresh.AnimIndex = 7;
            fresh.LastAnimationChangeTime = 0;
            fresh.SetInWorldTile(1002, 1000, 0);
            world.Ecs.Sync(fresh);
            world.Ecs.Mirror.TryGetEntity(CORPSE, false, out EcsEntity e).Should().BeTrue();
            AnimationTimer before = e.GetComponent<AnimationTimer>();
            ItemAnimationAdvanceSystem.Same(before, fresh.CorpseTimer).Should().BeTrue();

            stale.AnimIndex = 1;
            world.Ecs.PushItemTimer(stale);
            ItemAnimationAdvanceSystem.Same(e.GetComponent<AnimationTimer>(), before).Should().BeTrue("a stale instance's push is ignored");

            Time.Ticks = NOW + 500;
            system.TryRun(world, stale, src, false, null).Should().BeFalse();
            Time.Ticks = NOW;
        }

        [Fact]
        public void A_Death_Rekey_Leaves_The_Corpse_Timer_And_The_Mobile_Alone()
        {
            var src = new FakeCorpseSource { Max = 1000 };
            var system = new ItemAnimationAdvanceSystem();
            World world = BuildT5bItem(3, false, 400);
            world.Ecs.AnimTimerEnabled = true;
            Mobile m = world.Mobiles.Get(MOB);
            OldRekey(world, m);
            world.CorpseManager.Add(0x4000_7900, DEAD, m.Direction, false);
            world.Ecs.ResyncClocks();
            Item corpse = world.Items.Get(CORPSE);
            Time.Ticks = NOW + 500;
            int mobileFrame = m.AnimIndex;

            system.TryRun(world, corpse, src, false, null).Should().BeTrue();
            m.AnimIndex.Should().Be((byte)mobileFrame);
            corpse.AnimIndex.Should().NotBe(99);
            world.Ecs.Mirror.TryGetEntity(CORPSE, false, out EcsEntity e).Should().BeTrue();
            ItemAnimationAdvanceSystem.Same(e.GetComponent<AnimationTimer>(), corpse.CorpseTimer).Should().BeTrue();
            Time.Ticks = NOW;
        }

        [Fact]
        public void The_Legacy_Item_Update_Advance_Keeps_The_Component_In_Step()
        {
            // the switch on, a corpse the system declines (handles opening): Item.ProcessAnimation's funnel write pushes
            var src = new FakeCorpseSource { Max = 1000 };
            World world = BuildT5bItem(3, false, 400);
            Item corpse = world.Items.Get(CORPSE);
            Time.Ticks = NOW + 500;
            world.Writer.AdvanceCorpseFrame(corpse, 4, Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY);
            world.Ecs.Mirror.TryGetEntity(CORPSE, false, out EcsEntity e).Should().BeTrue();
            e.GetComponent<AnimationTimer>().Frame.Should().Be(4);
            e.GetComponent<AnimationTimer>().AnimDue.Should().Be(corpse.LastAnimationChangeTime);
            Time.Ticks = NOW;
        }

        // Codex audit P2: the funnel's commit pushed the timer before the operation wrote AnimIndex = 99 and UsedLayer
        [Fact]
        public void An_Existing_Corpse_Updated_Through_The_Funnel_Has_Its_Timer_Pushed_After_The_Corpse_State()
        {
            World world = BuildCorpse(Start.Standing);
            world.Ecs.ItemAnimTimerEnabled = true;
            Item corpse = world.Items.Get(CORPSE);
            world.Ecs.Sync(corpse);
            Time.Ticks = NOW + 500;
            world.Writer.AdvanceCorpseFrame(corpse, 4, Time.Ticks + Constants.CHARACTER_ANIMATION_DELAY);
            world.Ecs.Mirror.TryGetEntity(CORPSE, false, out EcsEntity e).Should().BeTrue();
            e.GetComponent<AnimationTimer>().Frame.Should().Be(4);
            e.GetComponent<AnimationTimer>().UsedLayer.Should().BeFalse();

            // the server sends the corpse again with the running bit (the second death variant) and another direction
            Item same = world.Writer.UpsertWorldItem(CORPSE, 0x2006, 0, 0x190, 1002, 1000, 0, Direction.Running | Direction.West, 0, ClassicUO.Game.Data.Flags.None, 0, out bool created, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            created.Should().BeFalse();
            same.AnimIndex.Should().Be(99);
            same.UsedLayer.Should().BeTrue();
            world.Ecs.Mirror.TryGetEntity(CORPSE, false, out e).Should().BeTrue();
            e.GetComponent<AnimationTimer>().Should().Be(same.CorpseTimer);
            e.GetComponent<AnimationTimer>().Frame.Should().Be(99);
            e.GetComponent<AnimationTimer>().UsedLayer.Should().BeTrue();
            Time.Ticks = NOW;
        }

        [Fact]
        public void The_Item_Anim_Switch_Is_On_By_Default_And_Builds_The_Timers_On_The_Stage()
        {
            ClassicUO.Configuration.Settings.GlobalSettings.WorldItemAnimSystem.Should().BeTrue();

            World world = BuildCorpse(Start.Standing);
            Item corpse = world.Items.Get(CORPSE);
            corpse.LastAnimationChangeTime = long.MaxValue / 2; // not due: the live animation data is not needed
            world.Ecs.Mirror.TryGetEntity(CORPSE, false, out EcsEntity e).Should().BeTrue();
            e.HasComponent<AnimationTimer>().Should().BeFalse();
            world.ItemAnimSystem.Should().BeTrue();
        }
    }
}
