using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // E10: World.Mobiles (MobileTable) must enumerate exactly like the Dictionary<uint, Mobile> it replaced, because the
    // mobile loop order decides tile lists, picking ties and footstep sounds. The real dictionary is the specification.
    public class MobileTableTests
    {
        private static void ShouldMatch(MobileTable table, Dictionary<uint, Mobile> dict, string why)
        {
            var got = new List<Mobile>(table.Values);
            var want = new List<Mobile>(dict.Values);
            got.Should().HaveCount(want.Count, why);

            for (int i = 0; i < got.Count; i++)
            {
                ReferenceEquals(got[i], want[i]).Should().BeTrue($"{why}: position {i} (0x{got[i].Serial:X} vs 0x{want[i].Serial:X})");
            }

            table.Count.Should().Be(dict.Count, why);

            // every mobile's Ordinal is its slot: strictly increasing along the enumeration
            int last = -1;

            foreach (Mobile m in table.Values)
            {
                m.Ordinal.Should().BeGreaterThan(last, why);
                last = m.Ordinal;
            }

            foreach (KeyValuePair<uint, Mobile> kv in dict)
            {
                ReferenceEquals(table.Get(kv.Key), kv.Value).Should().BeTrue(why);
            }
        }

        [Fact]
        public void Enumerates_Like_A_Dictionary_Through_Random_Adds_Removes_Overwrites_And_Clears()
        {
            var world = new World();
            var table = new MobileTable();
            var dict = new Dictionary<uint, Mobile>();
            var rng = new System.Random(2026);
            uint nextSerial = 1000;

            for (int step = 0; step < 6000; step++)
            {
                switch (rng.Next(10))
                {
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                    {
                        // add (a taken serial is refused by both)
                        uint s = (uint)rng.Next(1, 400);
                        Mobile m = Mobile.Create(world, s);
                        bool added = table.Add(m);
                        bool want = !dict.ContainsKey(s);

                        if (want)
                        {
                            dict[s] = m;
                        }

                        added.Should().Be(want);

                        break;
                    }
                    case 4:
                    case 5:
                    case 6:
                    {
                        uint s = (uint)rng.Next(1, 400);
                        table.Remove(s).Should().Be(dict.Remove(s));

                        break;
                    }
                    case 7:
                    {
                        // replace the value under an existing key (the slot stays), or insert when absent
                        uint s = (uint)rng.Next(1, 400);
                        Mobile m = Mobile.Create(world, s);
                        table[s] = m;
                        dict[s] = m;

                        break;
                    }
                    case 8:
                    {
                        // a serial change (0xBB-style): Remove, then set under the new serial (takes the freed slot)
                        uint s = (uint)rng.Next(1, 400);

                        if (dict.TryGetValue(s, out Mobile m))
                        {
                            table.Remove(s).Should().BeTrue();
                            dict.Remove(s);
                            uint ns = nextSerial++;
                            table[ns] = m;
                            dict[ns] = m;
                        }

                        break;
                    }
                    default:
                    {
                        // removing during a walk is legal for the dictionary; the table must follow
                        if (rng.Next(12) == 0)
                        {
                            int i = 0;

                            foreach (Mobile m in table.Values)
                            {
                                if (i++ % 3 == 0)
                                {
                                    table.Remove(FindKey(dict, m));
                                }
                            }

                            i = 0;

                            foreach (KeyValuePair<uint, Mobile> kv in dict)
                            {
                                if (i++ % 3 == 0)
                                {
                                    dict.Remove(kv.Key); // legal while enumerating (.NET Core 3+)
                                }
                            }
                        }
                        else if (rng.Next(300) == 0)
                        {
                            table.Clear();
                            dict.Clear();
                        }

                        break;
                    }
                }

                ShouldMatch(table, dict, "step " + step);
            }
        }

        private static uint FindKey(Dictionary<uint, Mobile> dict, Mobile m)
        {
            foreach (KeyValuePair<uint, Mobile> kv in dict)
            {
                if (ReferenceEquals(kv.Value, m))
                {
                    return kv.Key;
                }
            }

            return 0;
        }

        [Fact]
        public void A_Freed_Slot_Is_Reused_Last_In_First_Out_And_Overwrites_Keep_Their_Slot()
        {
            var world = new World();
            var table = new MobileTable();

            for (uint s = 1; s <= 5; s++)
            {
                table.Add(Mobile.Create(world, s));
            }

            table.Remove(2);
            table.Remove(4);
            table.Add(Mobile.Create(world, 10)); // takes 4's slot (the last freed)
            table.Add(Mobile.Create(world, 11)); // takes 2's slot
            table.Add(Mobile.Create(world, 12)); // appended

            var order = new List<uint>();

            foreach (Mobile m in table.Values)
            {
                order.Add(m.Serial);
            }

            order.Should().Equal(1u, 11u, 3u, 10u, 5u, 12u);

            Mobile replacement = Mobile.Create(world, 3);
            table[3] = replacement;
            table.Get(3).Should().BeSameAs(replacement);
            replacement.Ordinal.Should().Be(2);
        }

        [Fact]
        public void The_Shadow_Dictionary_Agrees_And_Notices_A_Corrupted_Order()
        {
            var world = new World();
            var table = new MobileTable();
            table.EnableShadow();
            var rng = new System.Random(7);

            for (int i = 0; i < 2000; i++)
            {
                uint s = (uint)rng.Next(1, 200);

                if (rng.Next(3) == 0)
                {
                    table.Remove(s);
                }
                else
                {
                    table.Add(Mobile.Create(world, s));
                }

                table.VerifyAgainstShadow().Should().Be(0);
            }

            // a mobile whose ordinal is wrong (what a missed update of the slot would look like) is reported
            foreach (Mobile m in table.Values)
            {
                m.Ordinal += 1;

                break;
            }

            table.VerifyAgainstShadow().Should().BeGreaterThan(0);
        }
    }
}
