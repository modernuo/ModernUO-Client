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
                            m.Serial = ns;
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


        private static List<uint> Serials(MobileTable t)
        {
            var l = new List<uint>();

            foreach (Mobile m in t.Values)
            {
                l.Add(m.Serial);
            }

            return l;
        }

        [Fact]
        public void A_Mobile_Cannot_Sit_Under_Two_Keys_Or_In_Two_Tables_And_Nothing_Changes_On_The_Refusal()
        {
            var world = new World();
            var t = new MobileTable();
            var other = new MobileTable();
            Mobile a = Mobile.Create(world, 1);
            Mobile b = Mobile.Create(world, 3);
            t[1] = a;

            System.Action second = () => t[2] = a;
            second.Should().Throw<System.InvalidOperationException>();
            System.Action twoTables = () => other.Add(a);
            twoTables.Should().Throw<System.InvalidOperationException>();
            System.Action overwrite = () => t[1] = a; // the same mobile under its own key is a no-op, not an error
            overwrite.Should().NotThrow();

            t.Count.Should().Be(1);
            t.Contains(2).Should().BeFalse();
            other.Count.Should().Be(0);

            // the sequence from the audit: it must stay consistent
            t.Remove(1).Should().BeTrue();
            t.Add(b).Should().BeTrue();
            t.Remove(2).Should().BeFalse();
            Serials(t).Should().Equal(3u);
            t.Get(3).Should().BeSameAs(b);

            // released by remove, replace and Clear: reusable afterwards
            other.Add(a).Should().BeTrue();
            other.Remove(1).Should().BeTrue();
            t[3] = a; // replaces b
            b.OwnerTable.Should().BeNull();
            t.Clear();
            a.OwnerTable.Should().BeNull();
            other.Add(a).Should().BeTrue();
        }

        [Fact]
        public void Clear_And_Remove_All_Then_Repopulate_Match_A_Dictionary()
        {
            var world = new World();

            foreach (bool useClear in new[] { true, false })
            {
                var t = new MobileTable();
                var d = new Dictionary<uint, Mobile>();

                for (uint s = 1; s <= 70; s++) // past the first array size
                {
                    Mobile m = Mobile.Create(world, s);
                    t.Add(m);
                    d[s] = m;
                }

                t.Remove(5);
                d.Remove(5);
                t.Remove(9);
                d.Remove(9);

                if (useClear)
                {
                    t.Clear();
                    d.Clear();
                }
                else
                {
                    foreach (uint k in new List<uint>(d.Keys))
                    {
                        t.Remove(k).Should().BeTrue();
                        d.Remove(k);
                    }
                }

                for (uint s = 200; s < 215; s++)
                {
                    Mobile m = Mobile.Create(world, s);
                    t.Add(m);
                    d[s] = m;
                }

                ShouldMatch(t, d, useClear ? "after Clear" : "after removing all");
            }
        }

        [Fact]
        public void The_Death_Sequence_Rekeys_Into_The_Freed_Slot_With_The_Serial_Updated()
        {
            var world = new World();
            var t = new MobileTable();
            var d = new Dictionary<uint, Mobile>();

            for (uint s = 1; s <= 6; s++)
            {
                Mobile m = Mobile.Create(world, s);
                t.Add(m);
                d[s] = m;
            }

            // 0xAF: Remove(old), set under the new serial, then the serial itself changes
            Mobile owner = t.Get(3);
            t.Remove(3).Should().BeTrue();
            d.Remove(3);
            uint dead = 3u | 0x80000000;
            t[dead] = owner;
            d[dead] = owner;
            owner.Serial = dead;

            ShouldMatch(t, d, "after re-key");
            Serials(t).Should().Equal(1u, 2u, dead, 4u, 5u, 6u);

            // re-keying into a key that exists replaces that entry in its slot
            Mobile victim = t.Get(5);
            Mobile mover = t.Get(2);
            t.Remove(2);
            d.Remove(2);
            t[5] = mover;
            d[5] = mover;
            mover.Serial = 5;
            victim.OwnerTable.Should().BeNull();
            ShouldMatch(t, d, "after re-key onto an existing key");
        }

        [Fact]
        public void Enumeration_Survives_Removal_Of_Unvisited_Entries_Overwrites_And_Clear_But_Not_New_Inserts()
        {
            var world = new World();
            var t = new MobileTable();

            for (uint s = 1; s <= 6; s++)
            {
                t.Add(Mobile.Create(world, s));
            }

            // remove one the walk has not reached yet, and one already visited
            var seen = new List<uint>();

            foreach (Mobile m in t.Values)
            {
                seen.Add(m.Serial);

                if (m.Serial == 2)
                {
                    t.Remove(4);
                    t.Remove(1);
                }
            }

            seen.Should().Equal(1u, 2u, 3u, 5u, 6u);

            // overwrite during the walk keeps the walk going and visits the new object in the same slot
            Mobile repl = Mobile.Create(world, 5);
            var seen2 = new List<Mobile>();

            foreach (Mobile m in t.Values)
            {
                if (m.Serial == 2)
                {
                    t[5] = repl;
                }

                seen2.Add(m);
            }

            seen2[seen2.Count - 2].Should().BeSameAs(repl);

            // Clear during the walk ends it quietly
            int steps = 0;

            foreach (Mobile m in t.Values)
            {
                steps++;
                t.Clear();
            }

            steps.Should().Be(1);

            // a new key invalidates active enumerators, like the dictionary
            t.Add(Mobile.Create(world, 1));
            t.Add(Mobile.Create(world, 2));
            System.Action insert = () =>
            {
                foreach (Mobile m in t.Values)
                {
                    t.Add(Mobile.Create(world, 99));
                }
            };
            insert.Should().Throw<System.InvalidOperationException>();

            // a refused duplicate is not an insert
            System.Action dup = () =>
            {
                foreach (Mobile m in t.Values)
                {
                    t.Add(Mobile.Create(world, 1));
                }
            };
            dup.Should().NotThrow();
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
