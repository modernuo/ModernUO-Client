using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Friflo.Engine.ECS;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T9: the per-frame item update list with the ECS as its source (ActiveItemEntry entities and their slots). T9 deleted
    // ecs_active_items_source' off path (World's list) and CUO_ACTIVE_ITEMS_SOURCE_CHECK, which compared the two; the tests
    // that compared the switch states now write out the expected orders (equal to the list's, which they were compared with)
    // or compare a world with CUO_ACTIVE_ITEMS_CHECK (the tags against the order) on and off, and that checker must stay
    // silent.
    public class ActiveItemsSourceTests
    {
        private const uint PLAYER = 0x0000_0100;
        private const ushort CORPSE = 0x2006, PLAIN = 0x0EED;

        private sealed class Scene
        {
            public World World;
            public readonly List<string> Mismatches = new List<string>();
            public readonly List<uint> Visits = new List<uint>();
            public Action<Scene, Item> OnVisit;
            public Random VisitRandom;
        }

        private static Scene Build(bool check = true)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer _, PLAYER);

            // the mobile stage is not under test (its updates need the animation data): the world holds no mobile, and no
            // mobile record (the due mobiles resolve through the mirror's index)
            foreach (uint serial in world.Mobiles.Values.Select(m => m.Serial).ToList())
            {
                world.Mobiles.Remove(serial);
                world.Ecs.Mirror.Remove(serial);
            }

            var scene = new Scene { World = world };
            world.RangeSize.X = 1000;
            world.RangeSize.Y = 1000;
            world.ActiveItemsCheckOverride = check;
            world.ActiveItemsObserver = scene.Mismatches.Add;
            world.ActiveVisitObserver = item =>
            {
                scene.Visits.Add(item.Serial);
                scene.OnVisit?.Invoke(scene, item);
            };

            return scene;
        }

        private static Item AddItem(Scene s, uint serial, ushort graphic)
        {
            Item i = s.World.GetOrCreateItem(serial);
            i.Graphic = graphic;
            i.LastAnimationChangeTime = long.MaxValue / 2; // a corpse is not due for a frame (that needs the animation data)
            i.SetInWorldTile(1000, 1000, 0);
            s.World.Ecs.Sync(i);

            return i;
        }

        private static List<uint> Active(World w) => w.ActiveItems.Select(i => i.Serial).ToList();

        private static int Tagged(World w) => w.Ecs.Mirror.Store.Query().AllTags(Tags.Get<ItemActive>()).Count;

        private static int Entries(World w) => w.Ecs.Mirror.Store.Query<ActiveItemEntry>().Count;

        private static void Same(Scene[] scenes, string because)
        {
            List<uint> order = Active(scenes[0].World);

            foreach (Scene s in scenes)
            {
                Active(s.World).Should().Equal(order, because);
                s.World.ActiveItemCount.Should().Be(order.Count, because);
                s.Visits.Should().Equal(scenes[0].Visits, because);
                s.Mismatches.Should().BeEmpty(because);

                foreach (Item item in s.World.ActiveItems)
                {
                    s.World.IsItemListed(item).Should().BeTrue(because);
                }
            }
        }

        [Fact]
        public void The_Ecs_Holds_One_Entry_Per_Listed_Item()
        {
            Scene s = Build();
            Item a = AddItem(s, 0x4000_0001, CORPSE);
            AddItem(s, 0x4000_0002, CORPSE);
            AddItem(s, 0x4000_0003, CORPSE);
            s.World.QueueItemUpdate(a); // listed: no second entry

            Entries(s.World).Should().Be(3);
            Tagged(s.World).Should().Be(3);
            s.World.ActiveItemCount.Should().Be(3);
            a.ActiveEntryId.Should().NotBe(0);
        }

        // the scripted scenario of ActiveItemsSystemTests (queue twice, a queued item that needs nothing, a corpse turned into a
        // plain item, destroyed while listed, re-queued, a refreshed record), with the list's orders written out
        [Fact]
        public void Membership_And_Order_Match_The_List_In_The_Scripted_Scenario()
        {
            Scene[] scenes = { Build(), Build(check: false) };
            uint[][] expected =
            {
                new uint[] { 1, 3, 4, 5 },
                new uint[] { 1, 5, 4 },
                new uint[] { 1, 5, 4 },
                new uint[] { 4, 5 },
                new uint[] { 4, 5, 3 },
                new uint[] { 4, 5, 3 }
            };

            foreach (Scene s in scenes)
            {
                Item c1 = AddItem(s, 0x4000_0001, CORPSE);
                Item plain = AddItem(s, 0x4000_0002, PLAIN);
                AddItem(s, 0x4000_0003, CORPSE);
                AddItem(s, 0x4000_0004, CORPSE);
                AddItem(s, 0x4000_0005, CORPSE);
                s.World.QueueItemUpdate(c1);
                s.World.QueueItemUpdate(plain);
            }

            Same(scenes, "after the adds");
            Active(scenes[0].World).Should().Equal(0x4000_0001u, 0x4000_0003u, 0x4000_0004u, 0x4000_0005u, 0x4000_0002u);

            for (int frame = 0; frame < 6; frame++)
            {
                foreach (Scene s in scenes)
                {
                    switch (frame)
                    {
                        case 1:
                            s.World.Items[0x4000_0003].Graphic = PLAIN;

                            break;

                        case 2:
                            s.World.Ecs.Sync(s.World.Items[0x4000_0005]);

                            break;

                        case 3:
                            s.World.Items[0x4000_0001].Destroy();

                            break;

                        case 4:
                            s.World.Items[0x4000_0003].Graphic = CORPSE;
                            s.World.QueueItemUpdate(s.World.Items[0x4000_0003]);

                            break;
                    }

                    s.World.Update();
                }

                Same(scenes, $"frame {frame}");
                Active(scenes[0].World).Should().Equal(expected[frame].Select(e => 0x4000_0000 + e), $"frame {frame}");
            }

            scenes[0].World.ActiveItemsChecks.Should().BeGreaterThan(0);
            scenes[1].World.ActiveItemsChecks.Should().Be(0);
            Entries(scenes[0].World).Should().Be(scenes[0].World.ActiveItemCount);
        }

        // the swap-remove order: an item destroyed between frames keeps its slot until the visit removes it, which moves the
        // last item into it; so destroying c then b gives the same order as b then c (removing an entry when its record goes
        // would not: [a b c d e] -> [a b e d] -> [a d e])
        [Theory]
        [InlineData(new[] { 1 }, new uint[] { 0, 4, 2, 3 })]
        [InlineData(new[] { 2, 1 }, new uint[] { 0, 4, 3 })]
        [InlineData(new[] { 1, 2 }, new uint[] { 0, 4, 3 })]
        [InlineData(new[] { 0, 4 }, new uint[] { 3, 1, 2 })]
        [InlineData(new[] { 0, 1, 2, 3, 4 }, new uint[0])]
        public void Removals_Keep_The_Swap_Remove_Order(int[] destroyed, uint[] expected)
        {
            Scene s = Build();
            var items = new List<Item>();

            for (uint i = 0; i < 5; i++)
            {
                items.Add(AddItem(s, 0x4000_0000 + i, CORPSE));
            }

            foreach (int d in destroyed)
            {
                items[d].Destroy();
            }

            s.World.Update();

            Active(s.World).Should().Equal(expected.Select(e => 0x4000_0000 + e));
            s.Mismatches.Should().BeEmpty();
        }

        // removals and additions while the stage visits: the visited item leaves, a later item is destroyed (it leaves when
        // visited), a destroyed earlier item, new corpses queued (visited in the same frame), an already listed item queued
        [Fact]
        public void Changes_During_The_Visit_Keep_The_Order()
        {
            Scene[] scenes = { Build(), Build(check: false) };

            foreach (Scene s in scenes)
            {
                for (uint i = 0; i < 8; i++)
                {
                    AddItem(s, 0x4000_0000 + i, CORPSE);
                }

                s.OnVisit = (sc, item) =>
                {
                    World w = sc.World;

                    switch (item.Serial)
                    {
                        case 0x4000_0001:
                            item.Graphic = PLAIN; // leaves at its own visit

                            break;

                        case 0x4000_0002:
                            foreach (uint d in new uint[] { 0x4000_0006, 0x4000_0000 }) // a later item leaves when visited, an earlier one next frame
                            {
                                if (w.Items.Get(d) is Item victim && !victim.IsDestroyed)
                                {
                                    victim.Destroy();
                                }
                            }

                            break;

                        case 0x4000_0003:
                            AddItem(sc, 0x4000_0010, CORPSE); // added during the visit: visited this frame
                            w.QueueItemUpdate(w.Items[0x4000_0004]); // listed already

                            break;

                        case 0x4000_0010:
                            AddItem(sc, 0x4000_0011, CORPSE);
                            item.Graphic = PLAIN;

                            break;
                    }
                };
            }

            for (int frame = 0; frame < 3; frame++)
            {
                foreach (Scene s in scenes)
                {
                    s.World.Update();
                }

                Same(scenes, $"frame {frame}");
            }

            scenes[0].Visits.Should().Contain(new uint[] { 0x4000_0010, 0x4000_0011 });
        }

        // many random worlds: adds, re-adds, removals by graphic, destroy and replacement, record refreshes, and random changes made
        // during the visit (drawn from a per-world RNG with the same seed: equal visit orders draw the same changes)
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void Random_Operations_Keep_Membership_And_Order(int seed)
        {
            Scene[] scenes = { Build(), Build(check: false) };
            var rnd = new Random(seed);
            const int SERIALS = 40;

            foreach (Scene s in scenes)
            {
                s.VisitRandom = new Random(seed * 7919);
                s.OnVisit = (sc, item) =>
                {
                    Random r = sc.VisitRandom;
                    World w = sc.World;
                    uint other = 0x4000_0000 + (uint)r.Next(SERIALS);

                    switch (r.Next(10))
                    {
                        case 0 when !item.IsDestroyed:
                            item.Graphic = PLAIN;

                            break;

                        case 1:
                            if (w.Items.Get(other) is Item o && !o.IsDestroyed)
                            {
                                o.Destroy();
                            }

                            break;

                        case 2:
                            if (w.Items.Get(other) == null)
                            {
                                AddItem(sc, other, CORPSE);
                            }

                            break;

                        case 3:
                            if (w.Items.Get(other) is Item q && !q.IsDestroyed)
                            {
                                w.QueueItemUpdate(q);
                            }

                            break;
                    }
                };
            }

            for (int step = 0; step < 600; step++)
            {
                int op = rnd.Next(12);
                uint serial = 0x4000_0000 + (uint)rnd.Next(SERIALS);
                ushort graphic = rnd.Next(3) == 0 ? PLAIN : CORPSE;

                foreach (Scene s in scenes)
                {
                    World w = s.World;
                    Item item = w.Items.Get(serial);

                    switch (op)
                    {
                        case 0:
                        case 1:
                        case 2:
                            if (item == null)
                            {
                                AddItem(s, serial, graphic);
                            }
                            else if (!item.IsDestroyed)
                            {
                                item.Graphic = graphic;
                                w.Ecs.Sync(item);
                            }

                            break;

                        case 3:
                            if (item != null && !item.IsDestroyed)
                            {
                                w.QueueItemUpdate(item); // a plain item queued: leaves at the next visit
                            }

                            break;

                        case 4:
                            if (item != null && !item.IsDestroyed)
                            {
                                item.Destroy();
                            }

                            break;

                        case 5:
                            if (item != null)
                            {
                                // a replaced instance: destroyed and dropped, a new one under the serial; the old instance
                                // stays listed until visited (removal without Destroy would leave the T5d record tag to the
                                // old instance's cached handle: see ActiveItemsSystemTests)
                                if (!item.IsDestroyed)
                                {
                                    item.Destroy();
                                }

                                w.Items.Remove(serial);

                                if (graphic == CORPSE)
                                {
                                    AddItem(s, serial, CORPSE);
                                }
                            }

                            break;

                        case 6:
                            if (item != null && !item.IsDestroyed && ReferenceEquals(w.Items.Get(serial), item))
                            {
                                w.Ecs.Sync(item); // the record refreshed
                            }

                            break;

                        default:
                            Time.Ticks += 30; // every other frame is a 50 ms sweep frame
                            w.Update();

                            break;
                    }
                }

                Same(scenes, $"seed {seed} step {step} op {op}");
            }

            foreach (Scene s in scenes)
            {
                Entries(s.World).Should().Be(s.World.ActiveItemCount);
            }

            scenes[0].World.ActiveItemsChecks.Should().BeGreaterThan(0);
        }

        [Fact]
        public void World_Clear_Deletes_The_Entries()
        {
            Scene s = Build();
            Item a = AddItem(s, 0x4000_0001, CORPSE);
            AddItem(s, 0x4000_0002, CORPSE);
            s.World.Clear();

            Entries(s.World).Should().Be(0);
            a.ActiveEntryId.Should().Be(0);
            s.World.ActiveItemCount.Should().Be(0);
            Tagged(s.World).Should().Be(0);
        }

        // Codex audit P2: an entry must not keep its item reachable once it is gone. Friflo's DeleteEntity moves the last
        // entity's components into the freed slot and leaves the vacated tail uncleared, so an object reference in
        // ActiveItemEntry stayed in the component array: here c is moved into a's slot when a leaves, then leaves itself, and
        // World.Clear deletes the rest. Every destroyed item must be collectable, with the checker on and off.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Removed_Items_Are_Not_Kept_Reachable_By_Their_Entries(bool check)
        {
            Scene s = Build(check);
            WeakReference[] removed = AddAndRemoveViaSwap(s);
            Collect();
            removed.Select(w => w.IsAlive).Should().Equal(new[] { false, false }, $"check {check}: a and c removed by the visit");

            WeakReference[] cleared = AddAndClear(s);
            Collect();
            cleared.Select(w => w.IsAlive).Should().Equal(new[] { false, false, false, false }, $"check {check}: removed by World.Clear");
            s.Mismatches.Should().BeEmpty();
            GC.KeepAlive(s.World);
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference[] AddAndRemoveViaSwap(Scene s)
        {
            Item a = AddItem(s, 0x4000_0001, CORPSE);
            AddItem(s, 0x4000_0002, CORPSE);
            Item c = AddItem(s, 0x4000_0003, CORPSE);
            s.World.RemoveItem(a.Serial);
            s.World.Update(); // a leaves; c (the last entry) moves into its slot
            s.World.RemoveItem(c.Serial);
            s.World.Update();
            Active(s.World).Should().Equal(0x4000_0002u);

            return new[] { new WeakReference(a), new WeakReference(c) };
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference[] AddAndClear(Scene s)
        {
            var refs = new List<WeakReference>();

            for (uint i = 0; i < 4; i++)
            {
                refs.Add(new WeakReference(AddItem(s, 0x4000_0010 + i, CORPSE)));
            }

            s.World.Clear();
            Entries(s.World).Should().Be(0);

            return refs.ToArray();
        }

        // the last slot's removal (no entry moves), and a table grown past its first size, with every item removed by the visit
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Last_Slot_Removals_And_A_Grown_Table_Keep_No_Item_Reachable(bool check)
        {
            Scene s = Build(check);
            WeakReference last = RemoveLastSlot(s);
            Collect();
            last.IsAlive.Should().BeFalse($"check {check}: the last slot's item");

            WeakReference[] grown = GrowAndRemoveAll(s);
            Collect();
            grown.Count(w => w.IsAlive).Should().Be(0, $"check {check}: the grown table's items");
            s.Mismatches.Should().BeEmpty();
            GC.KeepAlive(s.World);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference RemoveLastSlot(Scene s)
        {
            AddItem(s, 0x4000_0001, CORPSE);
            AddItem(s, 0x4000_0002, CORPSE);
            Item c = AddItem(s, 0x4000_0003, CORPSE);
            s.World.RemoveItem(c.Serial);
            s.World.Update();
            Active(s.World).Should().Equal(0x4000_0001u, 0x4000_0002u);

            return new WeakReference(c);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference[] GrowAndRemoveAll(Scene s)
        {
            var refs = new List<WeakReference>();
            var serials = new List<uint>();

            for (uint i = 0; i < 150; i++)
            {
                Item item = AddItem(s, 0x4000_1000 + i, CORPSE);
                refs.Add(new WeakReference(item));
                serials.Add(item.Serial);
            }

            s.World.Update();
            s.World.ActiveItemCount.Should().Be(152);

            foreach (uint serial in serials)
            {
                s.World.RemoveItem(serial);
            }

            s.World.Update();
            Active(s.World).Should().Equal(0x4000_0001u, 0x4000_0002u);

            return refs.ToArray();
        }

        // Codex re-review P2: an entry deleted outside the store (only the corruption tests do that). The rebuild
        // frees its handle and unlists its item; the other entries leave, so the count is 0; World.Clear still clears the
        // store, and the item is collectable.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_Entry_Deleted_Outside_The_Store_Leaves_No_Handle_Or_Membership(bool check)
        {
            Scene s = Build(check);
            WeakReference orphan = DeleteOutsideThenEmpty(s);
            s.World.ActiveItemCount.Should().Be(0);

            s.World.Clear();
            s.World.ActiveItemHandles.Should().Be((0, 0));
            Collect();
            orphan.IsAlive.Should().BeFalse($"check {check}");
            GC.KeepAlive(s.World);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference DeleteOutsideThenEmpty(Scene s)
        {
            // a plain item, not a corpse: the stage's scan does not list it again once it is unlisted
            Item a = AddItem(s, 0x4000_0001, PLAIN);
            s.World.QueueItemUpdate(a);
            Item b = AddItem(s, 0x4000_0002, CORPSE);
            s.World.IsItemListed(a).Should().BeTrue();
            s.World.ActiveItemEntryOf(a).DeleteEntity();
            s.World.RemoveItem(b.Serial);

            s.World.Update();

            a.ActiveEntryId.Should().Be(0, "the rebuild unlists the item of a deleted entry");
            s.World.ActiveItemHandles.InUse.Should().Be(Entries(s.World));

            return new WeakReference(a);
        }

        // World.Clear resets the store whatever its count: here every item has left by the visit (count 0, handles freed)
        [Fact]
        public void World_Clear_Resets_The_Store_With_A_Zero_Count()
        {
            Scene s = Build();
            Item a = AddItem(s, 0x4000_0001, CORPSE);
            s.World.RemoveItem(a.Serial);
            s.World.Update();
            s.World.ActiveItemCount.Should().Be(0);
            s.World.ActiveItemHandles.End.Should().BeGreaterThan(0);

            s.World.Clear();

            s.World.ActiveItemHandles.Should().Be((0, 0));
        }

        // an entry outlives its item's mirror record: a destroyed item stays listed (and its slot holds) until the visit
        [Fact]
        public void A_Destroyed_Item_Keeps_Its_Slot_Until_The_Visit()
        {
            Scene s = Build();
            Item a = AddItem(s, 0x4000_0001, CORPSE);
            AddItem(s, 0x4000_0002, CORPSE);
            a.Destroy();

            a.TryScalarEntity(out _).Should().BeFalse("its record is gone");
            s.World.IsItemListed(a).Should().BeTrue();
            Active(s.World).Should().Equal(0x4000_0001u, 0x4000_0002u);

            s.World.Update();
            Active(s.World).Should().Equal(0x4000_0002u);
            s.World.IsItemListed(a).Should().BeFalse();
            s.Mismatches.Should().BeEmpty();
        }

        // the order is the ECS's: the stage rebuilds it from the slots, so a slot change made in the ECS changes the visit
        [Fact]
        public void The_Visit_Order_Is_Rebuilt_From_The_Entry_Slots()
        {
            Scene s = Build(check: false);
            Item a = AddItem(s, 0x4000_0001, CORPSE);
            Item b = AddItem(s, 0x4000_0002, CORPSE);
            s.World.ActiveItemEntryOf(a).GetComponent<ActiveItemEntry>().Slot = 1;
            s.World.ActiveItemEntryOf(b).GetComponent<ActiveItemEntry>().Slot = 0;

            s.World.Update();

            s.Visits.Should().Equal(0x4000_0002u, 0x4000_0001u);
            Active(s.World).Should().Equal(0x4000_0002u, 0x4000_0001u);
        }

        // a broken slot is reported (with or without CUO_ACTIVE_ITEMS_CHECK) and every entry is still visited
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Broken_Slot_Is_Reported(bool check)
        {
            Scene s = Build(check);
            AddItem(s, 0x4000_0001, CORPSE);
            Item b = AddItem(s, 0x4000_0002, CORPSE);
            s.World.ActiveItemEntryOf(b).GetComponent<ActiveItemEntry>().Slot = 0;
            s.World.Update();
            s.Mismatches.Should().Contain(m => m.Contains("slots broken"));
            s.Visits.Should().BeEquivalentTo(new uint[] { 0x4000_0001, 0x4000_0002 });
        }

        [Fact]
        public void The_Rebuild_Allocates_Nothing()
        {
            Scene s = Build(check: false);
            var store = new ActiveItemStore(s.World.Ecs.Mirror.Store);

            for (uint i = 0; i < 50; i++)
            {
                Item item = s.World.GetOrCreateItem(0x4000_0000 + i);
                store.Add(item);
            }

            store.Rebuild(out _).Should().BeTrue();
            store.Rebuild(out _);
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 100; i++)
            {
                store.Rebuild(out _);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0, "the rebuild scatters into a reused array");
            store.Count.Should().Be(50);
        }
    }
}
