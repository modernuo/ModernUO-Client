using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 (world_entity_index): World.Get answers from the mirror's serial index (each record's handle) with the switch on, from
    // the Items and Mobiles tables with it off. The same operations run on worlds with the switch off and on, with
    // CUO_ENTITY_INDEX_CHECK on: every lookup must give the same object in both, and the checker must stay silent.
    public class WorldEntityIndexTests
    {
        private const uint PLAYER = 0x0000_0100, MOB = 0x0000_0200, MOB2 = 0x0000_0201;
        private const uint BAG = 0x4000_1000, COIN = 0x4000_1001, ROCK = 0x4000_1002, NONE = 0x4000_7777;

        private sealed class Scene
        {
            public World World;
            public readonly List<string> Mismatches = new List<string>();
            public readonly List<string> HoldsDiffs = new List<string>();
            public readonly List<string> Unswept = new List<string>();
        }

        private static Scene Build(bool index, bool check = true)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer _, PLAYER);
            var s = new Scene { World = world };
            world.EntityIndexOverride = index;
            world.EntityIndexCheckOverride = check;
            world.EntityIndexObserver = s.Mismatches.Add;
            world.EntityIndexHoldsObserver = s.HoldsDiffs.Add;
            world.EntityIndexUnsweptObserver = s.Unswept.Add;

            return s;
        }

        private static Mobile AddMobile(World w, uint serial)
        {
            Mobile m = w.GetOrCreateMobile(serial);
            m.Graphic = 0x190;
            m.SetInWorldTile(1001, 1000, 0);
            w.Ecs.Sync(m);

            return m;
        }

        private static Item AddItem(World w, uint serial, uint container = 0xFFFF_FFFF)
        {
            Item i = w.GetOrCreateItem(serial);
            i.Graphic = 0x0EED;

            if (container == 0xFFFF_FFFF)
            {
                i.SetInWorldTile(1000, 1001, 0);
            }
            else
            {
                i.Container = container;
            }

            w.Ecs.Sync(i);

            return i;
        }

        private static readonly uint[] Probe = { PLAYER, MOB, MOB2, BAG, COIN, ROCK, NONE, 0, 0x8000_0200 };

        private static void Same(Scene[] scenes, string because)
        {
            foreach (uint serial in Probe)
            {
                Entity first = scenes[0].World.Get(serial);

                foreach (Scene s in scenes)
                {
                    Entity e = s.World.Get(serial);
                    (e == null).Should().Be(first == null, $"0x{serial:X8} {because}");

                    if (e != null)
                    {
                        e.Serial.Should().Be(first.Serial, because);
                        (e is Mobile).Should().Be(first is Mobile, because);
                        e.IsDestroyed.Should().BeFalse(because);
                        s.World.GetHeld(serial).Should().BeSameAs(e, because);
                    }

                    s.World.GetIndexed(serial).Should().BeSameAs(e, $"0x{serial:X8} {because}");

                    // slice 2: the kind-checked lookups and the live Contains agree with Get
                    s.World.GetItem(serial).Should().BeSameAs(e as Item, $"0x{serial:X8} {because}");
                    s.World.GetMobile(serial).Should().BeSameAs(e as Mobile, $"0x{serial:X8} {because}");
                    s.World.GetIndexedItem(serial).Should().BeSameAs(e as Item, $"0x{serial:X8} {because}");
                    s.World.GetIndexedMobile(serial).Should().BeSameAs(e as Mobile, $"0x{serial:X8} {because}");
                    s.World.Contains(serial).Should().Be(e != null, $"0x{serial:X8} {because}");

                    // slice 3: the reader forms answer the tables (destroyed included) with the switch off, Get's kind on
                    bool on = s.World.EntityIndexOn;
                    s.World.FindItem(serial).Should().BeSameAs(on ? e as Item : s.World.Items.Get(serial), $"0x{serial:X8} {because}");
                    s.World.FindMobile(serial).Should().BeSameAs(on ? e as Mobile : s.World.Mobiles.Get(serial), $"0x{serial:X8} {because}");

                    if (e != null)
                    {
                        s.World.Holds(e).Should().BeTrue(because);
                        s.World.TryGetLiveRecord(e, serial, out Friflo.Engine.ECS.Entity record).Should().BeTrue(because);
                        record.IsNull.Should().BeFalse(because);
                    }
                }
            }

            foreach (Scene s in scenes)
            {
                s.Mismatches.Should().BeEmpty(because);
                s.HoldsDiffs.Should().BeEmpty(because);
            }
        }

        private static void Each(Scene[] scenes, Action<World> op)
        {
            foreach (Scene s in scenes)
            {
                op(s.World);
            }
        }

        [Fact]
        public void Both_Switch_States_Find_The_Same_Objects_Through_Creation_Removal_And_Replacement()
        {
            Scene[] scenes = { Build(false), Build(true), Build(true, check: false) };

            Same(scenes, "the player only");

            Each(scenes, w =>
            {
                AddMobile(w, MOB);
                AddMobile(w, MOB2);
                AddItem(w, BAG);
                AddItem(w, COIN, BAG);
                AddItem(w, ROCK);
            });
            Same(scenes, "after creation");

            // removed without force: the tables keep the destroyed objects until the sweep, Get does not return them
            Each(scenes, w => w.RemoveItem(ROCK));
            Each(scenes, w => w.RemoveMobile(MOB2));
            Same(scenes, "after removal, before the sweep");
            scenes[1].World.Items.Get(ROCK).Should().NotBeNull("the tables still hold the destroyed item");
            scenes[1].World.Ecs.Mirror.GetHandle(ROCK).Should().BeNull("the record and its handle are gone");

            // a destroyed object replaced by a new instance under its serial
            Each(scenes, w => AddItem(w, ROCK));
            Same(scenes, "after re-creation");
            scenes[1].World.Get(ROCK).Should().BeSameAs(scenes[1].World.Items.Get(ROCK));

            Each(scenes, w => w.RemoveItem(BAG, true));
            Same(scenes, "after a forced removal");
            scenes[1].World.Get(BAG).Should().BeNull();
            scenes[1].World.Items.Get(BAG).Should().BeNull("a forced removal leaves the tables too");
        }

        [Fact]
        public void The_Death_Rekey_Moves_The_Lookup_To_The_New_Serial()
        {
            Scene[] scenes = { Build(false), Build(true) };
            var mobiles = new List<Mobile>();
            Each(scenes, w => mobiles.Add(AddMobile(w, MOB)));
            Each(scenes, w => AddItem(w, COIN, MOB));

            uint[] dead = new uint[2];

            for (int i = 0; i < 2; i++)
            {
                dead[i] = scenes[i].World.Writer.RekeyDeadMobile(mobiles[i]);
            }

            dead[1].Should().Be(dead[0]);

            for (int i = 0; i < 2; i++)
            {
                scenes[i].World.Get(dead[i]).Should().BeSameAs(mobiles[i]);
                scenes[i].World.Get(MOB).Should().BeNull();
            }

            Same(scenes, "after the re-key");
        }

        [Fact]
        public void Clearing_The_World_Leaves_No_Handle()
        {
            Scene s = Build(true);
            AddMobile(s.World, MOB);
            AddItem(s.World, BAG);

            s.World.Writer.ClearWorld();
            s.World.Ecs.OnWorldCleared();

            s.World.Ecs.Mirror.GetHandle(MOB).Should().BeNull();
            s.World.Ecs.Mirror.GetHandle(BAG).Should().BeNull();
            s.World.Get(MOB).Should().BeNull();
            s.World.Get(BAG).Should().BeNull();
        }

        [Fact]
        public void A_Recycled_Entity_Id_Does_Not_Inherit_A_Handle()
        {
            Scene s = Build(true);
            Item first = AddItem(s.World, BAG);
            s.World.RemoveItem(BAG, true);

            // a record written without a handle (a mirror-only writer) reuses the freed id
            s.World.Ecs.Mirror.UpsertItem(ROCK, 1, 0, 1000, 1000, 0, 0, 0, 1, 0xFFFF_FFFF, 0, false);

            s.World.Ecs.Mirror.GetHandle(ROCK).Should().BeNull();
            s.World.Get(ROCK).Should().BeNull();
            s.World.Get(BAG).Should().BeNull();
            first.IsDestroyed.Should().BeTrue();
        }

        [Fact]
        public void The_Checker_Reports_An_Object_The_Index_Does_Not_Hold_And_The_Switch_Picks_The_Answer()
        {
            foreach (bool index in new[] { false, true })
            {
                Scene s = Build(index);

                // held by the tables but never committed (a funnel operation's creation window)
                Item item = s.World.GetOrCreateItem(NONE, out bool isNew);
                isNew.Should().BeTrue();

                Entity got = s.World.Get(NONE);

                if (index)
                {
                    got.Should().BeNull("the switch answers from the index");
                }
                else
                {
                    got.Should().BeSameAs(item, "the switch off answers from the tables");
                }

                s.Mismatches.Should().ContainSingle().Which.Should().Contain("0x40007777").And.Contain("index none");
                s.World.EntityIndexMismatches.Should().Be(1);
                s.World.EntityIndexChecks.Should().Be(1);
            }
        }

        // the one known difference (WorldWriterTests' "a mobile's serial" equip/wear cases and EffectSystemTests' shared serial
        // fail with the switch on for this reason): an item created under a mobile's serial replaces the mobile's record, so
        // the index answers the item while the tables answer the mobile. No sane server sends that; the checker reports it.
        [Fact]
        public void An_Item_Under_A_Mobile_Serial_Is_The_Known_Difference_And_The_Checker_Reports_It()
        {
            foreach (bool index in new[] { false, true })
            {
                Scene s = Build(index);
                Mobile m = AddMobile(s.World, MOB);
                Item shared = AddItem(s.World, MOB);

                s.World.Items.Get(MOB).Should().BeSameAs(shared);
                s.World.Mobiles.Get(MOB).Should().BeSameAs(m);
                s.World.Get(MOB).Should().BeSameAs(index ? shared : m);
                s.Mismatches.Should().ContainSingle().Which.Should().Contain("tables mobile 0x00000200, index item 0x00000200");

                // owner decision 2026-11-08: the serial resolves to the object holding the record. The item lookup agrees;
                // the mobile lookup finds no mobile in the index, and the mobile's Holds guard (still the tables) differs
                s.World.GetItem(MOB).Should().BeSameAs(shared);
                s.World.GetMobile(MOB).Should().BeSameAs(index ? null : m);
                s.Mismatches.Should().HaveCount(2).And.Contain(t => t.Contains("mobile: tables mobile 0x00000200, index none"));

                s.HoldsDiffs.Clear();
                s.World.Holds(m).Should().BeTrue("Holds stays on the tables");
                s.World.Holds(shared).Should().BeTrue();
                s.HoldsDiffs.Should().ContainSingle().Which.Should().Contain("holds mobile 0x00000200: tables yes, index item 0x00000200");
            }
        }

        [Fact]
        public void The_Kind_Checked_Lookups_Find_Only_Their_Kind_In_Both_Switch_States()
        {
            foreach (bool index in new[] { false, true })
            {
                Scene s = Build(index);
                Mobile m = AddMobile(s.World, MOB);
                Item bag = AddItem(s.World, BAG);
                long before = s.World.EntityIndexChecks;

                s.World.GetMobile(MOB).Should().BeSameAs(m);
                s.World.GetItem(MOB).Should().BeNull();
                s.World.GetItem(BAG).Should().BeSameAs(bag);
                s.World.GetMobile(BAG).Should().BeNull();
                s.World.GetItem(NONE).Should().BeNull();
                s.World.GetMobile(0).Should().BeNull();
                s.Mismatches.Should().BeEmpty();
                s.World.EntityIndexChecks.Should().Be(before + 6);
            }
        }

        // owner decision 2026-11-08: World.Contains (Entity.Exists) is live, as World.Get: a destroyed object not yet swept does
        // not exist. Holds still answers the tables, which keep it until the sweep (T9 slice 3 moves that window)
        [Fact]
        public void A_Destroyed_Object_Before_The_Sweep_Is_Absent_But_Still_Held()
        {
            foreach (bool index in new[] { false, true })
            {
                Scene s = Build(index);
                Item rock = AddItem(s.World, ROCK);
                Mobile mob = AddMobile(s.World, MOB2);
                rock.Exists.Should().BeTrue();

                s.World.RemoveItem(ROCK);
                s.World.RemoveMobile(MOB2);

                s.World.Items.Get(ROCK).Should().BeSameAs(rock, "the tables keep it until the sweep");
                s.World.GetItem(ROCK).Should().BeNull();
                s.World.GetMobile(MOB2).Should().BeNull();
                s.World.Contains(ROCK).Should().BeFalse();
                rock.Exists.Should().BeFalse();
                s.World.Holds(rock).Should().BeTrue();
                s.World.Holds(mob).Should().BeTrue();
                s.Mismatches.Should().BeEmpty();
                s.HoldsDiffs.Should().BeEmpty("the checker compares Holds for live objects only");
            }
        }

        [Fact]
        public void Holds_Is_By_Instance()
        {
            Scene s = Build(true);
            Item old = AddItem(s.World, ROCK);
            s.World.RemoveItem(ROCK, true);
            Item fresh = AddItem(s.World, ROCK);

            s.World.Holds(fresh).Should().BeTrue();
            s.World.Holds(old).Should().BeFalse("replaced under its serial");
            s.World.Holds(null).Should().BeFalse();
            s.World.Holds(fresh, BAG).Should().BeFalse("not under another serial");
            s.World.Holds(s.World.Player).Should().BeTrue();
            s.HoldsDiffs.Should().BeEmpty();
            s.World.EntityIndexHoldsChecks.Should().BeGreaterThan(0);
        }

        [Fact]
        public void The_Checker_Reports_A_Kind_Checked_Lookup_In_The_Creation_Window_And_Counts_The_Uncommitted_Guard()
        {
            foreach (bool index in new[] { false, true })
            {
                Scene s = Build(index);
                Item item = s.World.GetOrCreateItem(NONE, out bool isNew);
                isNew.Should().BeTrue();

                s.World.GetItem(NONE).Should().BeSameAs(index ? null : item);
                s.Mismatches.Should().ContainSingle().Which.Should().Contain("item: tables item 0x40007777, index none");

                long uncommitted = s.World.EntityIndexHoldsUncommitted;
                s.World.Holds(item).Should().BeTrue();
                s.World.EntityIndexHoldsUncommitted.Should().Be(uncommitted + 1);
                s.HoldsDiffs.Should().BeEmpty("an uncommitted object is counted, not reported");
            }
        }

        // Codex audit of slice 2 (P3): past the 20 logged reports, a persistent difference is counted but no message is built
        [Fact]
        public void A_Persistent_Difference_Stops_Formatting_After_Twenty_Reports()
        {
            Scene s = Build(true);
            s.World.EntityIndexObserver = null;
            s.World.EntityIndexHoldsObserver = null;
            Mobile m = AddMobile(s.World, MOB);
            AddItem(s.World, MOB);
            long mismatchesBefore = s.World.EntityIndexMismatches;
            long holdsBefore = s.World.EntityIndexHoldsDiffs;
            long formattedBefore = s.World.EntityIndexReportsFormatted;

            for (int i = 0; i < 50; i++)
            {
                s.World.GetMobile(MOB);
                s.World.Holds(m);
            }

            (s.World.EntityIndexMismatches - mismatchesBefore).Should().Be(50);
            (s.World.EntityIndexHoldsDiffs - holdsBefore).Should().Be(50);
            s.World.EntityIndexReportsFormatted.Should().Be(formattedBefore + Math.Max(0, 20 - mismatchesBefore) + Math.Max(0, 20 - holdsBefore));
        }

        [Fact]
        public void Without_The_Checker_The_Kind_Checked_Lookups_And_Holds_Compare_Nothing()
        {
            Scene s = Build(true, check: false);
            Item item = s.World.GetOrCreateItem(NONE, out _);

            s.World.GetItem(NONE).Should().BeNull();
            s.World.GetMobile(PLAYER).Should().BeSameAs(s.World.Player);
            s.World.Holds(item).Should().BeTrue();
            s.World.EntityIndexChecks.Should().Be(0);
            s.World.EntityIndexHoldsChecks.Should().Be(0);
        }

        // slice 3 (owner decision 2026-11-08): the readers that took the tables' answer (FindItem/FindMobile) see a destroyed
        // object not yet swept with the switch off, as before, and no object with it on; the checker counts that as the
        // switch's expected difference (unswept, logged at Info with the caller), not as a mismatch
        [Fact]
        public void The_Reader_Lookups_See_The_Unswept_Object_Only_With_The_Switch_Off()
        {
            foreach (bool index in new[] { false, true })
            {
                Scene s = Build(index);
                Item rock = AddItem(s.World, ROCK);
                Mobile mob = AddMobile(s.World, MOB2);

                s.World.FindItem(ROCK).Should().BeSameAs(rock);
                s.World.FindMobile(MOB2).Should().BeSameAs(mob);
                s.World.FindItem(MOB2).Should().BeNull("kind-checked");
                s.World.FindMobile(ROCK).Should().BeNull("kind-checked");
                s.Unswept.Should().BeEmpty();

                s.World.RemoveItem(ROCK);
                s.World.RemoveMobile(MOB2);

                s.World.FindItem(ROCK).Should().BeSameAs(index ? null : rock);
                s.World.FindMobile(MOB2).Should().BeSameAs(index ? null : mob);
                s.Unswept.Should().HaveCount(2);
                s.Unswept[0].Should().Contain("0x40001002 find item: tables item 0x40001002 (destroyed), index none");
                s.Unswept[1].Should().Contain("find mobile: tables mobile 0x00000201 (destroyed), index none");
                s.World.EntityIndexUnswept.Should().Be(2);
                s.Mismatches.Should().BeEmpty("the unswept window is the switch's expected difference");

                // re-created under the serial before the sweep: both answer the new object
                Item fresh = AddItem(s.World, ROCK);
                s.World.FindItem(ROCK).Should().BeSameAs(fresh);

                // after the sweep neither answers a destroyed item
                AddItem(s.World, COIN);
                s.World.RemoveItem(COIN);
                s.World.DropDestroyedItems();
                s.World.FindItem(COIN).Should().BeNull();
                s.World.EntityIndexUnswept.Should().Be(2);
                s.Mismatches.Should().BeEmpty();
            }
        }

        [Fact]
        public void The_Unswept_Reports_Stop_Formatting_After_Twenty()
        {
            Scene s = Build(false);
            s.World.EntityIndexUnsweptObserver = null;
            AddItem(s.World, ROCK);
            s.World.RemoveItem(ROCK);
            long formatted = s.World.EntityIndexReportsFormatted;

            for (int i = 0; i < 50; i++)
            {
                s.World.FindItem(ROCK).Should().NotBeNull();
            }

            s.World.EntityIndexUnswept.Should().Be(50);
            s.World.EntityIndexReportsFormatted.Should().Be(formatted + 20);
        }

        // the commit guard and the scalar lookup: the handle test with the switch on answers what Holds + the kind test answer
        // with it off, for live, destroyed, replaced and uncommitted objects; they differ only for a record written without a
        // handle (a mirror-only writer: tests), where the switch on finds no live record
        [Fact]
        public void The_Live_Record_Guard_Agrees_In_Both_Switch_States()
        {
            foreach (bool check in new[] { true, false })
            {
                foreach (bool index in new[] { false, true })
                {
                    Scene s = Build(index, check);
                    Mobile mob = AddMobile(s.World, MOB);
                    Item old = AddItem(s.World, ROCK);
                    s.World.RemoveItem(ROCK, true);
                    Item fresh = AddItem(s.World, ROCK);
                    Item gone = AddItem(s.World, COIN);
                    s.World.RemoveItem(COIN);
                    Item uncommitted = s.World.GetOrCreateItem(NONE, out _);

                    s.World.TryGetLiveRecord(mob, MOB, out Friflo.Engine.ECS.Entity r).Should().BeTrue();
                    s.World.Ecs.Mirror.TryGetEntity(MOB, out Friflo.Engine.ECS.Entity expected).Should().BeTrue();
                    r.Should().Be(expected);
                    s.World.TryGetLiveRecord(fresh, ROCK, out _).Should().BeTrue();
                    s.World.TryGetLiveRecord(old, ROCK, out _).Should().BeFalse("replaced");
                    s.World.TryGetLiveRecord(gone, COIN, out _).Should().BeFalse("destroyed, not yet swept");
                    s.World.TryGetLiveRecord(uncommitted, NONE, out _).Should().BeFalse("no record yet");
                    s.World.TryGetLiveRecord(mob, BAG, out _).Should().BeFalse("another serial");
                    s.World.TryGetLiveRecord(s.World.Player, PLAYER, out _).Should().BeTrue();
                    mob.TryScalarEntity(out _).Should().BeTrue();
                    s.HoldsDiffs.Should().BeEmpty();

                    // a record written without a handle under a held item's serial
                    Item bare = AddItem(s.World, BAG);
                    s.World.Ecs.Mirror.Remove(BAG);
                    s.World.Ecs.Mirror.UpsertItem(BAG, 1, 0, 1000, 1000, 0, 0, 0, 1, 0xFFFF_FFFF, 0, false);
                    s.World.TryGetLiveRecord(bare, BAG, out _).Should().Be(!index, "the switch on wants the record's handle");
                }
            }
        }

        [Fact]
        public void Without_The_Checker_The_Reader_Lookups_Compare_Nothing()
        {
            foreach (bool index in new[] { false, true })
            {
                Scene s = Build(index, check: false);
                Item rock = AddItem(s.World, ROCK);
                s.World.RemoveItem(ROCK);

                s.World.FindItem(ROCK).Should().BeSameAs(index ? null : rock);
                s.World.EntityIndexChecks.Should().Be(0);
                s.World.EntityIndexUnswept.Should().Be(0);
                s.Unswept.Should().BeEmpty();
            }
        }

        [Fact]
        public void Without_The_Checker_Only_The_Switchs_Lookup_Runs()
        {
            Scene s = Build(true, check: false);
            s.World.GetOrCreateItem(NONE, out _);

            s.World.Get(NONE).Should().BeNull();
            s.World.EntityIndexChecks.Should().Be(0);
            s.Mismatches.Should().BeEmpty();
        }
    }
}
