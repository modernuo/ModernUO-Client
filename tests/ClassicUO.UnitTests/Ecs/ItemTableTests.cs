using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // World.Items (ItemTable) keeps a held flag on each item so the active item loop answers World.Holds(item) without a
    // lookup. The flag must give the lookup's answer (ReferenceEquals(Get(item.Serial), item)) after any sequence of table
    // operations, and the table must enumerate exactly like the Dictionary<uint, Item> it wraps.
    public class ItemTableTests
    {
        private static bool Lookup(ItemTable t, Item i) => ReferenceEquals(t.Get(i.Serial), i);

        private static void ShouldMatch(ItemTable table, ItemTable other, Dictionary<uint, Item> dict, List<Item> seen, string why)
        {
            table.Values.ToList().Should().Equal(dict.Values.ToList(), (a, b) => ReferenceEquals(a, b), why);
            table.OrderMatches(out int at).Should().BeTrue($"{why}: slots differ from the dictionary at {at}");
            table.Keys.ToList().Should().Equal(dict.Keys.ToList(), why);
            table.Count.Should().Be(dict.Count, why);

            foreach (Item i in seen)
            {
                bool want = dict.TryGetValue(i.Serial, out Item held) && ReferenceEquals(held, i);
                table.Holds(i).Should().Be(want, $"{why}: 0x{i.Serial:X}");
                Lookup(table, i).Should().Be(want, why);
                other.Holds(i).Should().Be(Lookup(other, i), $"{why}: other table, 0x{i.Serial:X}");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Holds_And_Order_Answer_Like_The_Dictionary_Through_Random_Operations(bool ordered)
        {
            var world = new World();
            var table = new ItemTable { OrderedWithoutOwner = ordered };
            var other = new ItemTable { OrderedWithoutOwner = ordered };
            var dict = new Dictionary<uint, Item>();
            var seen = new List<Item>();
            var rng = new System.Random(1109);

            for (int step = 0; step < 8000; step++)
            {
                int op = rng.Next(20);
                uint s = (uint)rng.Next(1, 300);

                if (op < 7)
                {
                    // a new instance (a taken serial is refused by both)
                    Item i = Item.Create(world, 0x4000_0000 | s);
                    seen.Add(i);
                    bool want = !dict.ContainsKey(i.Serial);

                    if (want)
                    {
                        dict[i.Serial] = i;
                    }

                    table.Add(i).Should().Be(want);
                }
                else if (op < 12)
                {
                    table.Remove(0x4000_0000 | s).Should().Be(dict.Remove(0x4000_0000 | s));
                }
                else if (op < 15 && seen.Count != 0)
                {
                    // re-add an instance seen before (removed, or still held: then refused)
                    Item i = seen[rng.Next(seen.Count)];
                    bool want = !dict.ContainsKey(i.Serial);

                    if (want)
                    {
                        dict[i.Serial] = i;
                    }

                    table.Add(i).Should().Be(want);
                }
                else if (op < 17 && seen.Count != 0)
                {
                    // a serial change (held or not): the old key may keep the instance, or it is added under a second key
                    Item i = seen[rng.Next(seen.Count)];
                    i.Serial = 0x4000_0000 | s;
                }
                else if (op == 17 && seen.Count != 0)
                {
                    // the same instance in a second table
                    Item i = seen[rng.Next(seen.Count)];

                    if (rng.Next(2) == 0)
                    {
                        other.Add(i);
                    }
                    else
                    {
                        other.Remove(i.Serial);
                    }
                }
                else if (op == 18 && rng.Next(40) == 0)
                {
                    table.Clear();
                    dict.Clear();
                }
                else if (seen.Count != 0)
                {
                    // destroyed, not swept: still held
                    Item i = seen[rng.Next(seen.Count)];

                    if (!i.IsDestroyed)
                    {
                        i.Destroy();
                    }
                }

                if (step % 50 == 0 || step > 7900)
                {
                    ShouldMatch(table, other, dict, seen, $"step {step}");
                }
            }
        }

        [Fact]
        public void Removed_And_Re_Added_Serials()
        {
            var world = new World();
            var table = new ItemTable();
            Item a = Item.Create(world, 0x4000_0001);
            Item b = Item.Create(world, 0x4000_0001);

            table.Holds(a).Should().BeFalse("never added");
            table.Add(a).Should().BeTrue();
            table.Holds(a).Should().BeTrue();
            table.Add(b).Should().BeFalse("serial taken");
            table.Holds(b).Should().BeFalse();

            a.Destroy();
            table.Holds(a).Should().BeTrue("destroyed, not swept");

            table.Remove(a.Serial).Should().BeTrue();
            table.Holds(a).Should().BeFalse();
            table.Add(b).Should().BeTrue();
            table.Holds(b).Should().BeTrue();
            table.Holds(a).Should().BeFalse("a newer instance holds the serial");

            table.Clear();
            table.Holds(b).Should().BeFalse();
            table.Add(b).Should().BeTrue();
            table.Holds(b).Should().BeTrue("re-added after a clear");
        }

        [Fact]
        public void Serial_Changes_And_Second_Keys_Fall_Back_To_The_Lookup()
        {
            var world = new World();
            var table = new ItemTable();
            Item a = Item.Create(world, 0x4000_0001);
            table.Add(a);

            a.Serial = 0x4000_0002;
            table.Holds(a).Should().BeFalse("held under its old serial only");
            a.Serial = 0x4000_0001;
            table.Holds(a).Should().BeTrue();

            a.Serial = 0x4000_0002;
            table.Add(a).Should().BeTrue("a second key");
            table.Holds(a).Should().BeTrue();
            table.Remove(0x4000_0002);
            table.Holds(a).Should().BeFalse("its serial's key is gone");
            a.Serial = 0x4000_0001;
            table.Holds(a).Should().BeTrue("still under the first key");
            table.Remove(0x4000_0001);
            table.Holds(a).Should().BeFalse();
        }

        // the world's paths: create, destroy without sweep, World.Update's sweep, RemoveItem with forceRemove, World.Clear;
        // World.Holds (the tables) and the held flag agree throughout
        [Fact]
        public void World_Paths_Keep_The_Flag_Equal_To_World_Holds()
        {
            World world = WorldWriterMobileTests.BuildPlayer(out _, 0x0000_0100);
            var items = new List<Item>();

            void Agree(string why)
            {
                foreach (Item i in items)
                {
                    world.Items.Holds(i).Should().Be(world.Holds(i), $"{why}: 0x{i.Serial:X} destroyed {i.IsDestroyed}");
                }
            }

            for (uint s = 1; s <= 6; s++)
            {
                items.Add(world.GetOrCreateItem(0x4000_0000 | s));
            }

            Agree("created");
            world.RemoveItem(0x4000_0001);
            Agree("destroyed, not swept");
            world.Items.Holds(items[0]).Should().BeTrue();
            world.RemoveItem(0x4000_0002, forceRemove: true);
            Agree("force removed");
            world.Items.Holds(items[1]).Should().BeFalse();

            // GetOrCreateItem replaces a destroyed instance under the same serial
            Item again = world.GetOrCreateItem(0x4000_0001);
            items.Add(again);
            ReferenceEquals(again, items[0]).Should().BeFalse();
            Agree("re-created");
            world.Items.Holds(again).Should().BeTrue();
            world.Items.Holds(items[0]).Should().BeFalse();

            world.RemoveItem(0x4000_0003);
            world.DropDestroyedItems();
            Agree("swept");
            world.Items.Holds(items[2]).Should().BeFalse();

            world.Clear();
            Agree("cleared");
            items.Should().OnlyContain(i => !world.Items.Holds(i));
        }

        // T9 slice 4: with world_entity_index on, Values walks the slots. Removing while walking and clearing are legal for
        // the dictionary, so the walk must follow it there too.
        [Fact]
        public void An_Ordered_Walk_Follows_The_Dictionary_Through_Removals_During_The_Walk_And_Clears()
        {
            var world = new World();
            var table = new ItemTable { OrderedWithoutOwner = true };
            var dict = new Dictionary<uint, Item>();
            var rng = new System.Random(4);

            for (int step = 0; step < 3000; step++)
            {
                int op = rng.Next(10);
                uint s = 0x4000_0000 | (uint)rng.Next(1, 200);

                if (op < 5)
                {
                    Item i = Item.Create(world, s);
                    bool want = dict.TryAdd(s, i);
                    table.Add(i).Should().Be(want);
                }
                else if (op < 8)
                {
                    table.Remove(s).Should().Be(dict.Remove(s));
                }
                else if (op == 8)
                {
                    // remove every third item while walking each
                    int n = 0;
                    var gone = new List<uint>();

                    foreach (Item i in table.Values)
                    {
                        if (n++ % 3 == 0)
                        {
                            table.Remove(i.Serial).Should().BeTrue();
                            gone.Add(i.Serial);
                        }
                    }

                    n = 0;
                    var dictGone = new List<uint>();

                    foreach (KeyValuePair<uint, Item> kv in dict)
                    {
                        if (n++ % 3 == 0)
                        {
                            dict.Remove(kv.Key);
                            dictGone.Add(kv.Key);
                        }
                    }

                    gone.Should().Equal(dictGone, $"step {step}");
                }
                else if (rng.Next(60) == 0)
                {
                    table.Clear();
                    dict.Clear();
                }

                table.Values.ToList().Should().Equal(dict.Values.ToList(), (a, b) => ReferenceEquals(a, b), $"step {step}");
                table.OrderMatches(out _).Should().BeTrue($"step {step}");
            }
        }

        [Fact]
        public void A_Freed_Slot_Is_Reused_Last_In_First_Out_In_Both_Orders()
        {
            var world = new World();

            foreach (bool ordered in new[] { false, true })
            {
                var t = new ItemTable { OrderedWithoutOwner = ordered };

                for (uint s = 1; s <= 70; s++) // past the first slot array size
                {
                    t.Add(Item.Create(world, 0x4000_0000 | s));
                }

                t.Remove(0x4000_0002);
                t.Remove(0x4000_0004);
                t.Add(Item.Create(world, 0x4000_0100)); // takes 4's slot (the last freed)
                t.Add(Item.Create(world, 0x4000_0101)); // takes 2's slot
                t.Add(Item.Create(world, 0x4000_0102)); // appended

                t.Values.Take(5).Select(i => i.Serial & 0xFFFF).Should().Equal(new uint[] { 1, 0x101, 3, 0x100, 5 }, $"ordered {ordered}");
                t.Values.Last().Serial.Should().Be(0x4000_0102);
                t.Get(0x4000_0100).Ordinal.Should().Be(3);
            }
        }

        private static string Outcome(System.Func<object> f)
        {
            try
            {
                object o = f();

                return o is Item i ? $"item 0x{i.Serial:X}" : o?.ToString() ?? "null";
            }
            catch (System.Exception e)
            {
                return "throws " + e.GetType().Name;
            }
        }

        // Codex audit of slice 4 (P3): the enumerator's contract, step by step, against Dictionary<uint, Item>.ValueCollection's
        // enumerator in both modes: the non-generic Current before the start, on an item, past the end and after Reset; Reset
        // after an insert, a removal and a Clear; MoveNext after a Clear during the walk
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Enumerator_Contract_Matches_The_Dictionary(bool ordered)
        {
            var world = new World();

            foreach (string scenario in new[] { "current", "insert-reset", "remove-reset", "clear-walk", "clear-reset" })
            {
                var t = new ItemTable { OrderedWithoutOwner = ordered };
                var d = new Dictionary<uint, Item>();

                for (uint s = 1; s <= 4; s++)
                {
                    Item it = Item.Create(world, 0x4000_0000 | s);
                    t.Add(it);
                    d.Add(it.Serial, it);
                }

                System.Collections.IEnumerator te = ((System.Collections.IEnumerable)t.Values).GetEnumerator();
                System.Collections.IEnumerator de = ((System.Collections.IEnumerable)d.Values).GetEnumerator();
                var got = new List<string>();
                var want = new List<string>();

                void Step(System.Func<System.Collections.IEnumerator, object> f)
                {
                    got.Add(Outcome(() => f(te)));
                    want.Add(Outcome(() => f(de)));
                }

                void Both(System.Action<ItemTable> onTable, System.Action<Dictionary<uint, Item>> onDict)
                {
                    onTable(t);
                    onDict(d);
                }

                Step(e => e.Current);

                switch (scenario)
                {
                    case "current":
                        for (int i = 0; i < 6; i++)
                        {
                            Step(e => e.MoveNext());
                            Step(e => e.Current);
                        }

                        Step(e => { e.Reset(); return "reset"; });
                        Step(e => e.Current);
                        Step(e => e.MoveNext());
                        Step(e => e.Current);

                        break;
                    case "insert-reset":
                        Step(e => e.MoveNext());
                        Both(x => x.Add(Item.Create(world, 0x4000_0099)), x => x.Add(0x4000_0099, Item.Create(world, 0x4000_0099)));
                        Step(e => { e.Reset(); return "reset"; });
                        Step(e => e.MoveNext());

                        break;
                    case "remove-reset":
                        Step(e => e.MoveNext());
                        Both(x => x.Remove(0x4000_0002), x => x.Remove(0x4000_0002));
                        Step(e => { e.Reset(); return "reset"; });
                        Step(e => e.MoveNext());
                        Step(e => e.Current);
                        Step(e => e.MoveNext());
                        Step(e => e.Current);

                        break;
                    case "clear-walk":
                        Step(e => e.MoveNext());
                        Both(x => x.Clear(), x => x.Clear());
                        Step(e => e.MoveNext());
                        Step(e => e.Current);

                        break;
                    default:
                        Step(e => e.MoveNext());
                        Both(x => x.Clear(), x => x.Clear());
                        Step(e => { e.Reset(); return "reset"; });
                        Step(e => e.MoveNext());

                        break;
                }

                got.Should().Equal(want, $"{scenario}, ordered {ordered}");
            }
        }

        [Fact]
        public void An_Insert_During_An_Ordered_Walk_Throws_Like_The_Dictionary_And_A_Refused_Add_Does_Not()
        {
            var world = new World();
            var t = new ItemTable { OrderedWithoutOwner = true };
            t.Add(Item.Create(world, 0x4000_0001));
            t.Add(Item.Create(world, 0x4000_0002));

            System.Action insert = () =>
            {
                foreach (Item i in t.Values)
                {
                    t.Add(Item.Create(world, 0x4000_0099));
                }
            };
            insert.Should().Throw<System.InvalidOperationException>();

            System.Action refused = () =>
            {
                foreach (Item i in t.Values)
                {
                    t.Add(Item.Create(world, 0x4000_0001)).Should().BeFalse();
                }
            };
            refused.Should().NotThrow();
        }

        // the world's own table follows world_entity_index; with the checker on, World.Update's bookkeeping compares the slots
        // with the dictionary every update
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void World_Items_Follow_The_Switch_And_Keep_The_Dictionary_Order(bool on)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out _, 0x0000_0100);
            world.EntityIndexOverride = on;
            world.EntityIndexCheckOverride = true;
            world.Items.Ordered.Should().Be(on);

            for (uint s = 1; s <= 40; s++)
            {
                world.GetOrCreateItem(0x4000_0000 | s);
            }

            world.RemoveItem(0x4000_0003, forceRemove: true);
            world.RemoveItem(0x4000_0007);
            world.DropDestroyedItems();
            world.RemoveItem(0x4000_0011, forceRemove: true);
            world.GetOrCreateItem(0x4000_0200);
            world.GetOrCreateItem(0x4000_0201);
            world.GetOrCreateItem(0x4000_0202);

            var order = new List<uint>();

            foreach (Item i in world.Items.Values)
            {
                order.Add(i.Serial);
            }

            order.Should().Equal(world.Items.Keys.ToList(), "the dictionary's order");
            order.IndexOf(0x4000_0200).Should().Be(order.IndexOf(0x4000_0010) + 1, "the last freed slot (0x11) comes first");

            world.CheckTableOrder();
            world.EntityIndexOrderChecks.Should().Be(1);
            world.EntityIndexOrderDiffs.Should().Be(0);

            world.Clear();
            world.CheckTableOrder();
            world.EntityIndexOrderDiffs.Should().Be(0);
        }

        [Fact]
        public void The_Order_Check_Reports_Slots_That_Differ_From_The_Dictionary()
        {
            World world = WorldWriterMobileTests.BuildPlayer(out _, 0x0000_0100);
            world.EntityIndexCheckOverride = true;
            var reports = new List<string>();
            world.EntityIndexObserver = reports.Add;

            for (uint s = 1; s <= 4; s++)
            {
                world.GetOrCreateItem(0x4000_0000 | s);
            }

            // swap two slots behind the table's back
            var slots = (Item[])typeof(ItemTable).GetField("_slots", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(world.Items);
            (slots[1], slots[2]) = (slots[2], slots[1]);

            world.CheckTableOrder();
            world.EntityIndexOrderDiffs.Should().Be(1);
            reports.Should().ContainSingle().Which.Should().Contain("items differ at 1").And.Contain("mobiles same");
        }
    }
}
