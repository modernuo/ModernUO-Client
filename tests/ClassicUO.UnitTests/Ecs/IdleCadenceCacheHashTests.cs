using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClassicUO.Game;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // IdleCadenceCache's open-addressing table: collisions, delete inside a probe chain, a model comparison, and the burst
    // (a teleport into / out of the large scene) without allocation.
    public class IdleCadenceCacheHashTests
    {
        private static List<uint> Colliding(int count)
        {
            int home = IdleCadenceCache.Home(1);
            var list = new List<uint>();

            for (uint s = 1; list.Count < count; s++)
            {
                if (IdleCadenceCache.Home(s) == home)
                {
                    list.Add(s);
                }
            }

            return list;
        }

        [Fact]
        public void Colliding_Serials_Are_All_Found()
        {
            var c = new IdleCadenceCache();
            List<uint> s = Colliding(12);

            for (int i = 0; i < s.Count; i++)
            {
                c.Save(s[i], (ushort)(100 + i), 50_000 + i, now: 1000);
            }

            for (int i = s.Count - 1; i >= 0; i--)
            {
                c.TryRestore(s[i], (ushort)(100 + i), 2000, out long due).Should().BeTrue();
                due.Should().Be(50_000 + i);
            }

            c.Count.Should().Be(0);
        }

        [Fact]
        public void Delete_From_The_Middle_Of_A_Probe_Chain_Keeps_The_Rest_Findable()
        {
            var c = new IdleCadenceCache();
            List<uint> s = Colliding(8);

            foreach (uint serial in s)
            {
                c.Save(serial, 1, 50_000, now: 1000);
            }

            c.TryRestore(s[2], 1, 2000, out _).Should().BeTrue();
            c.TryRestore(s[2], 1, 2000, out _).Should().BeFalse("deleted");
            c.TryRestore(s[0], 1, 2000, out _).Should().BeTrue();
            c.TryRestore(s[5], 1, 2000, out _).Should().BeTrue();
            c.Save(s[2], 1, 60_000, now: 2000);

            foreach (int i in new[] { 1, 2, 3, 4, 6, 7 })
            {
                c.TryRestore(s[i], 1, 3000, out _).Should().BeTrue($"serial index {i}");
            }
        }

        [Fact]
        public void Random_Operations_Match_A_Model()
        {
            var rng = new Random(42);
            var c = new IdleCadenceCache();
            var model = new Dictionary<uint, (ushort body, uint due)>();
            uint now = 1000;

            for (int i = 0; i < 200_000; i++)
            {
                now = 1000 + (uint)(i / 10); // 21 s in all: inside the 60 s window
                uint serial = (uint)rng.Next(1, 3000) * 7919;
                ushort body = (ushort)(rng.Next(3) + 1);

                if (rng.Next(2) == 0)
                {
                    uint due = now + 100_000;
                    c.Save(serial, body, due, now);
                    model[serial] = (body, due);
                }
                else
                {
                    bool hit = c.TryRestore(serial, body, now, out long got);
                    bool expect = model.TryGetValue(serial, out var m) && m.body == body;
                    hit.Should().Be(expect);

                    if (expect)
                    {
                        got.Should().Be(m.due);
                        model.Remove(serial);
                    }
                }
            }

            c.Count.Should().Be(model.Count);
        }

        [Fact]
        public void Burst_Of_2000_Inserts_And_Lookups_Does_Not_Allocate_And_Is_Fast()
        {
            var c = new IdleCadenceCache();

            // warm up the code paths
            for (uint i = 1; i <= 50; i++) { c.Save(i, 1, 50_000, 1000); }
            for (uint i = 1; i <= 50; i++) { c.TryRestore(i, 1, 1001, out _); }

            long before = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();

            for (uint i = 1; i <= 2000; i++)
            {
                c.Save(0x1000 + i, 0xC8, 50_000, 2000);
            }

            int hits = 0;

            for (uint i = 1; i <= 2000; i++)
            {
                if (c.TryRestore(0x1000 + i, 0xC8, 2001, out _))
                {
                    hits++;
                }
            }

            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            hits.Should().Be(2000);
            allocated.Should().Be(0);
            ms.Should().BeLessThan(20, "4,000 O(1) calls; the linear scan was about 8 M compares");
        }

        [Fact]
        public void Full_Table_Burst_Evicts_In_Write_Order_Without_Allocating()
        {
            var c = new IdleCadenceCache();

            for (uint i = 1; i <= IdleCadenceCache.Capacity; i++)
            {
                c.Save(i, 1, 50_000, 1000 + i);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (uint i = 1; i <= 2000; i++)
            {
                c.Save(100_000 + i, 1, 50_000, 6000);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
            c.Count.Should().Be(IdleCadenceCache.Capacity);
            c.UnexpiredOverwrites.Should().Be(2000);
            c.TryRestore(2000, 1, 6001, out _).Should().BeFalse("the 2,000 oldest are gone");
            c.TryRestore(2001, 1, 6001, out _).Should().BeTrue();
        }
    }
}
