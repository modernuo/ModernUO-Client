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
        }

        private static Scene Build(bool index, bool check = true)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer _, PLAYER);
            var s = new Scene { World = world };
            world.EntityIndexOverride = index;
            world.EntityIndexCheckOverride = check;
            world.EntityIndexObserver = s.Mismatches.Add;

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
                }
            }

            foreach (Scene s in scenes)
            {
                s.Mismatches.Should().BeEmpty(because);
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
