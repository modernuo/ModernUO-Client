using System.Collections.Generic;
using System.Linq;
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

        [Fact]
        public void Holds_Answers_Like_The_Lookup_Through_Random_Operations()
        {
            var world = new World();
            var table = new ItemTable();
            var other = new ItemTable();
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
    }
}
