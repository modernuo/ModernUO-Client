using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T9: World.Get, GetItem, GetMobile, FindItem and FindMobile answer from the mirror's serial index (each record's handle);
    // Holds from the tables' marks. world_entity_index's off path (the tables' lookups) and CUO_ENTITY_INDEX_CHECK are deleted;
    // the old lookups survive here as the reference (LegacyWorldLookups), and every difference from them is stated: an
    // object created inside a funnel operation before its commit, a destroyed object before the sweep, and a shared serial.
    public class WorldEntityIndexTests
    {
        private const uint PLAYER = 0x0000_0100, MOB = 0x0000_0200, MOB2 = 0x0000_0201;
        private const uint BAG = 0x4000_1000, COIN = 0x4000_1001, ROCK = 0x4000_1002, NONE = 0x4000_7777;

        private static World Build() => WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer _, PLAYER);

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

        // the old table lookups (destroyed objects included): the reference for Holds
        private static bool HoldsLookup(World w, Entity e, uint serial) =>
            e is Mobile ? ReferenceEquals(w.Mobiles.Get(serial), e) : ReferenceEquals(w.Items.Get(serial), e);

        private static readonly uint[] Probe = { PLAYER, MOB, MOB2, BAG, COIN, ROCK, NONE, 0, 0x8000_0200 };

        // with no shared serial and no object in a funnel window, every lookup gives the old tables' answer
        private static void SameAsTheTables(World w, string because)
        {
            foreach (uint serial in Probe)
            {
                Entity e = w.Get(serial);
                e.Should().BeSameAs(LegacyWorldLookups.GetHeld(w, serial), $"0x{serial:X8} {because}");

                if (e != null)
                {
                    e.Serial.Should().Be(serial, because);
                    e.IsDestroyed.Should().BeFalse(because);
                    w.Holds(e).Should().BeTrue(because);
                    w.TryGetLiveRecord(e, serial, out Friflo.Engine.ECS.Entity record).Should().BeTrue(because);
                    record.IsNull.Should().BeFalse(because);
                }

                w.GetItem(serial).Should().BeSameAs(e as Item, $"0x{serial:X8} {because}");
                w.GetMobile(serial).Should().BeSameAs(e as Mobile, $"0x{serial:X8} {because}");
                w.FindItem(serial).Should().BeSameAs(e as Item, $"0x{serial:X8} {because}");
                w.FindMobile(serial).Should().BeSameAs(e as Mobile, $"0x{serial:X8} {because}");
                w.Contains(serial).Should().Be(e != null, $"0x{serial:X8} {because}");
            }
        }

        [Fact]
        public void The_Lookups_Follow_Creation_Removal_And_Replacement()
        {
            World w = Build();
            SameAsTheTables(w, "the player only");

            AddMobile(w, MOB);
            AddMobile(w, MOB2);
            AddItem(w, BAG);
            AddItem(w, COIN, BAG);
            AddItem(w, ROCK);
            SameAsTheTables(w, "after creation");

            // removed without force: the tables keep the destroyed objects until the sweep, Get does not return them
            w.RemoveItem(ROCK);
            w.RemoveMobile(MOB2);
            SameAsTheTables(w, "after removal, before the sweep");
            w.Items.Get(ROCK).Should().NotBeNull("the tables still hold the destroyed item");
            w.Ecs.Mirror.GetHandle(ROCK).Should().BeNull("the record and its handle are gone");

            // a destroyed object replaced by a new instance under its serial
            AddItem(w, ROCK);
            SameAsTheTables(w, "after re-creation");
            w.Get(ROCK).Should().BeSameAs(w.Items.Get(ROCK));

            w.RemoveItem(BAG, true);
            SameAsTheTables(w, "after a forced removal");
            w.Get(BAG).Should().BeNull();
            w.Items.Get(BAG).Should().BeNull("a forced removal leaves the tables too");
        }

        [Fact]
        public void The_Death_Rekey_Moves_The_Lookup_To_The_New_Serial()
        {
            World w = Build();
            Mobile mobile = AddMobile(w, MOB);
            AddItem(w, COIN, MOB);

            uint dead = w.Writer.RekeyDeadMobile(mobile);

            dead.Should().Be(MOB | 0x8000_0000);
            w.Get(dead).Should().BeSameAs(mobile);
            w.Get(MOB).Should().BeNull();
            SameAsTheTables(w, "after the re-key");
        }

        [Fact]
        public void Clearing_The_World_Leaves_No_Handle()
        {
            World w = Build();
            AddMobile(w, MOB);
            AddItem(w, BAG);

            w.Writer.ClearWorld();
            w.Ecs.OnWorldCleared();

            w.Ecs.Mirror.GetHandle(MOB).Should().BeNull();
            w.Ecs.Mirror.GetHandle(BAG).Should().BeNull();
            w.Get(MOB).Should().BeNull();
            w.Get(BAG).Should().BeNull();
        }

        [Fact]
        public void A_Recycled_Entity_Id_Does_Not_Inherit_A_Handle()
        {
            World w = Build();
            Item first = AddItem(w, BAG);
            w.RemoveItem(BAG, true);

            // a record written without a handle (a mirror-only writer) reuses the freed id
            w.Ecs.Mirror.UpsertItem(ROCK, 1, 0, 1000, 1000, 0, 0, 0, 1, 0xFFFF_FFFF, 0, false);

            w.Ecs.Mirror.GetHandle(ROCK).Should().BeNull();
            w.Get(ROCK).Should().BeNull();
            w.Get(BAG).Should().BeNull();
            first.IsDestroyed.Should().BeTrue();
        }

        // an object held by the tables but never committed (a funnel operation's creation window) is not found; the old
        // lookup found it. Holds (the marks) still answers it
        [Fact]
        public void An_Uncommitted_Object_Is_Not_Found_But_Is_Held()
        {
            World w = Build();
            Item item = w.GetOrCreateItem(NONE, out bool isNew);
            isNew.Should().BeTrue();

            LegacyWorldLookups.GetHeld(w, NONE).Should().BeSameAs(item, "the tables held it");
            w.Get(NONE).Should().BeNull();
            w.GetItem(NONE).Should().BeNull();
            w.FindItem(NONE).Should().BeNull();
            w.Holds(item).Should().BeTrue();
            w.TryGetLiveRecord(item, NONE, out _).Should().BeFalse("no record yet");
        }

        // the one known difference from the tables (WorldWriterTests' second wear under a shared serial and EffectSystemTests'
        // shared endpoints state it too): an item created under a mobile's serial replaces the mobile's record, so the index
        // answers the item while the tables answered the mobile (owner decision 2026-11-08). No sane server sends that
        [Fact]
        public void An_Item_Under_A_Mobile_Serial_Resolves_To_The_Record_Holder()
        {
            World w = Build();
            Mobile m = AddMobile(w, MOB);
            Item shared = AddItem(w, MOB);

            w.Items.Get(MOB).Should().BeSameAs(shared);
            w.Mobiles.Get(MOB).Should().BeSameAs(m);
            LegacyWorldLookups.GetHeld(w, MOB).Should().BeSameAs(m, "the tables answered the serial's kind");
            w.Get(MOB).Should().BeSameAs(shared);
            w.GetItem(MOB).Should().BeSameAs(shared);
            w.GetMobile(MOB).Should().BeNull("the index has no mobile under the serial");
            w.Holds(m).Should().BeTrue("Holds answers table membership");
            w.Holds(shared).Should().BeTrue();
        }

        // Codex audit (t9-world-index-5) P2: an item equipped under a mobile's serial takes the serial's record; destroying the
        // mobile that lost it must leave the live item's record (WorldEcs.OnDestroyed asks World.NewerOwner, which used to
        // accept only an owner of the destroyed object's kind, so the item's record went and GetItem missed a live item)
        [Fact]
        public void Removing_The_Mobile_That_Lost_A_Shared_Serial_Keeps_The_Items_Record()
        {
            World w = Build();
            Mobile loser = AddMobile(w, MOB);
            Mobile wearer = AddMobile(w, MOB2);

            Item shared = w.Writer.Equip(MOB, 0x1515, ClassicUO.Game.Data.Layer.Shirt, MOB2, 0, out Entity at, out _);
            shared.Should().NotBeNull();
            at.Should().BeSameAs(wearer);
            w.Ecs.Mirror.GetHandle(MOB).Should().BeSameAs(shared, "the item's commit took the serial's record");

            w.RemoveMobile(MOB).Should().BeTrue();
            loser.IsDestroyed.Should().BeTrue();

            shared.IsDestroyed.Should().BeFalse();
            w.Ecs.Mirror.TryGetEntity(MOB, out _).Should().BeTrue("the live item keeps its record");
            w.Ecs.Mirror.GetHandle(MOB).Should().BeSameAs(shared);
            w.GetItem(MOB).Should().BeSameAs(shared);
            w.FindItem(MOB).Should().BeSameAs(shared);
            w.Get(MOB).Should().BeSameAs(shared);
            w.Holds(shared).Should().BeTrue();
            shared.Container.Should().Be(MOB2);
            bool attached = false;

            foreach (Item it in w.State.Children(wearer))
            {
                attached |= ReferenceEquals(it, shared);
            }

            attached.Should().BeTrue("still attached to its wearer");

            // and the other way: the item removed while the mobile's record is the item's, nothing of the mobile's to keep
            w.RemoveItem(MOB, true);
            w.Ecs.Mirror.TryGetEntity(MOB, out _).Should().BeFalse();
            w.GetItem(MOB).Should().BeNull();
        }

        // the same for the death re-key (WorldEcs.CommitRekey): the dying mobile moves to its dead serial; a live item that took
        // the old serial's record keeps it
        [Fact]
        public void Rekeying_The_Mobile_That_Lost_A_Shared_Serial_Keeps_The_Items_Record()
        {
            const uint DEAD = MOB | 0x8000_0000;

            World w = Build();
            Mobile dying = AddMobile(w, MOB);
            AddMobile(w, MOB2);

            Item shared = w.Writer.Equip(MOB, 0x1515, ClassicUO.Game.Data.Layer.Shirt, MOB2, 0, out _, out _);
            w.Ecs.Mirror.GetHandle(MOB).Should().BeSameAs(shared);

            w.Writer.RekeyDeadMobile(dying).Should().Be(DEAD);

            dying.Serial.Should().Be(DEAD);
            w.Ecs.Mirror.GetHandle(DEAD).Should().BeSameAs(dying, "the re-keyed mobile's record under its dead serial");
            w.GetMobile(DEAD).Should().BeSameAs(dying);
            shared.IsDestroyed.Should().BeFalse();
            w.Ecs.Mirror.TryGetEntity(MOB, out _).Should().BeTrue("the live item keeps its record");
            w.Ecs.Mirror.GetHandle(MOB).Should().BeSameAs(shared);
            w.GetItem(MOB).Should().BeSameAs(shared);
            w.FindItem(MOB).Should().BeSameAs(shared);
            shared.Container.Should().Be(MOB2);
        }

        [Fact]
        public void The_Kind_Checked_Lookups_Find_Only_Their_Kind()
        {
            World w = Build();
            Mobile m = AddMobile(w, MOB);
            Item bag = AddItem(w, BAG);

            w.GetMobile(MOB).Should().BeSameAs(m);
            w.GetItem(MOB).Should().BeNull();
            w.GetItem(BAG).Should().BeSameAs(bag);
            w.GetMobile(BAG).Should().BeNull();
            w.GetItem(NONE).Should().BeNull();
            w.GetMobile(0).Should().BeNull();
            w.FindItem(MOB).Should().BeNull("kind-checked");
            w.FindMobile(BAG).Should().BeNull("kind-checked");
        }

        // owner decision 2026-11-08: a destroyed object not yet swept does not exist for the lookups (World.Contains,
        // Entity.Exists, FindItem/FindMobile included; the old FindItem/FindMobile still answered it). Holds answers the
        // tables, which keep it until the sweep
        [Fact]
        public void A_Destroyed_Object_Before_The_Sweep_Is_Absent_But_Still_Held()
        {
            World w = Build();
            Item rock = AddItem(w, ROCK);
            Mobile mob = AddMobile(w, MOB2);
            rock.Exists.Should().BeTrue();
            w.FindItem(ROCK).Should().BeSameAs(rock);
            w.FindMobile(MOB2).Should().BeSameAs(mob);

            w.RemoveItem(ROCK);
            w.RemoveMobile(MOB2);

            w.Items.Get(ROCK).Should().BeSameAs(rock, "the tables keep it until the sweep");
            w.GetItem(ROCK).Should().BeNull();
            w.GetMobile(MOB2).Should().BeNull();
            w.FindItem(ROCK).Should().BeNull();
            w.FindMobile(MOB2).Should().BeNull();
            w.Contains(ROCK).Should().BeFalse();
            rock.Exists.Should().BeFalse();
            w.Holds(rock).Should().BeTrue();
            w.Holds(mob).Should().BeTrue();

            // re-created under the serial before the sweep: the new object
            Item fresh = AddItem(w, ROCK);
            w.FindItem(ROCK).Should().BeSameAs(fresh);

            // after the sweep a destroyed item is neither found nor held
            Item coin = AddItem(w, COIN);
            w.RemoveItem(COIN);
            w.DropDestroyedItems();
            w.FindItem(COIN).Should().BeNull();
            w.Holds(coin).Should().BeFalse();
        }

        [Fact]
        public void Holds_Is_By_Instance()
        {
            World w = Build();
            Item old = AddItem(w, ROCK);
            w.RemoveItem(ROCK, true);
            Item fresh = AddItem(w, ROCK);

            w.Holds(fresh).Should().BeTrue();
            w.Holds(old).Should().BeFalse("replaced under its serial");
            w.Holds(null).Should().BeFalse();
            w.Holds(fresh, BAG).Should().BeFalse("not under another serial");
            w.Holds(w.Player).Should().BeTrue();
        }

        // the commit guard and the scalar lookup: the record's handle is the instance, for live, destroyed, replaced and
        // uncommitted objects; a record written without a handle (a mirror-only writer: tests) is no live record
        [Fact]
        public void The_Live_Record_Guard()
        {
            World w = Build();
            Mobile mob = AddMobile(w, MOB);
            Item old = AddItem(w, ROCK);
            w.RemoveItem(ROCK, true);
            Item fresh = AddItem(w, ROCK);
            Item gone = AddItem(w, COIN);
            w.RemoveItem(COIN);
            Item uncommitted = w.GetOrCreateItem(NONE, out _);

            w.TryGetLiveRecord(mob, MOB, out Friflo.Engine.ECS.Entity r).Should().BeTrue();
            w.Ecs.Mirror.TryGetEntity(MOB, out Friflo.Engine.ECS.Entity expected).Should().BeTrue();
            r.Should().Be(expected);
            w.TryGetLiveRecord(fresh, ROCK, out _).Should().BeTrue();
            w.TryGetLiveRecord(old, ROCK, out _).Should().BeFalse("replaced");
            w.TryGetLiveRecord(gone, COIN, out _).Should().BeFalse("destroyed, not yet swept");
            w.TryGetLiveRecord(uncommitted, NONE, out _).Should().BeFalse("no record yet");
            w.TryGetLiveRecord(mob, BAG, out _).Should().BeFalse("another serial");
            w.TryGetLiveRecord(w.Player, PLAYER, out _).Should().BeTrue();
            mob.TryScalarEntity(out _).Should().BeTrue();

            // a record written without a handle under a held item's serial
            Item bare = AddItem(w, BAG);
            w.Ecs.Mirror.Remove(BAG);
            w.Ecs.Mirror.UpsertItem(BAG, 1, 0, 1000, 1000, 0, 0, 0, 1, 0xFFFF_FFFF, 0, false);
            w.TryGetLiveRecord(bare, BAG, out _).Should().BeFalse("the guard wants the record's handle");
        }

        // slice 3 part 2: Holds answers from the tables' marks on the object (no lookup); the answer is the old lookup's for
        // live, destroyed-unswept, swept, replaced, force-removed, uncommitted, re-keyed and shared-serial objects
        [Fact]
        public void Holds_From_The_Table_Marks_Answers_What_The_Lookup_Answered()
        {
            World w = Build();
            Mobile mob = AddMobile(w, MOB);
            Mobile gone = AddMobile(w, MOB2);
            Item old = AddItem(w, ROCK);
            w.RemoveItem(ROCK, true);
            Item fresh = AddItem(w, ROCK);
            Item swept = AddItem(w, BAG);
            w.RemoveItem(BAG);
            w.DropDestroyedItems();
            Item unswept = AddItem(w, COIN);
            w.RemoveItem(COIN);
            w.RemoveMobile(MOB2);
            Item uncommitted = w.GetOrCreateItem(NONE, out _);
            Item shared = AddItem(w, MOB);

            var cases = new List<(Entity E, uint Serial, bool Want, string Why)>
            {
                (mob, MOB, true, "live"),
                (fresh, ROCK, true, "live"),
                (old, ROCK, false, "replaced"),
                (unswept, COIN, true, "destroyed, not yet swept"),
                (gone, MOB2, true, "destroyed, not yet swept"),
                (swept, BAG, false, "swept"),
                (uncommitted, NONE, true, "created in the funnel's window"),
                (shared, MOB, true, "the item under a mobile's serial"),
                (fresh, COIN, false, "not under another serial"),
                (w.Player, PLAYER, true, "the player")
            };

            foreach (var c in cases)
            {
                w.Holds(c.E, c.Serial).Should().Be(c.Want, c.Why);
                HoldsLookup(w, c.E, c.Serial).Should().Be(c.Want, "the old lookup: " + c.Why);
            }

            uint dead = w.Writer.RekeyDeadMobile(mob);
            w.Holds(mob, dead).Should().BeTrue("re-keyed");
            w.Holds(mob, MOB).Should().BeFalse("no longer under the old serial");
            HoldsLookup(w, mob, dead).Should().BeTrue();
            HoldsLookup(w, mob, MOB).Should().BeFalse();
        }

        // WorldEcs.OnDestroyed keeps a record a newer live object owns (the index's handle)
        [Fact]
        public void The_Newer_Owner_Is_The_Records_Live_Holder()
        {
            World w = Build();
            Item old = AddItem(w, ROCK);
            w.RemoveItem(ROCK, true);
            Item fresh = AddItem(w, ROCK);
            Item lone = AddItem(w, COIN);
            Mobile mob = AddMobile(w, MOB);

            w.NewerOwner(old).Should().BeSameAs(fresh);
            w.NewerOwner(fresh).Should().BeNull();
            w.NewerOwner(lone).Should().BeNull();
            w.NewerOwner(mob).Should().BeNull();

            // the old instance destroyed again: the fresh record stays
            w.Ecs.OnDestroyed(old);
            w.Get(ROCK).Should().BeSameAs(fresh);
            w.Ecs.Mirror.GetHandle(ROCK).Should().BeSameAs(fresh);
        }
    }
}
