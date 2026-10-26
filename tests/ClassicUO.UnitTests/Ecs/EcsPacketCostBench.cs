using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // E2 (world-prepare-ecs-plan.md): cost of the mirror's hooks under packet storms. A Stopwatch loop, not a test: it does
    // nothing unless CUO_ECS_BENCH=<output file> is set. Run with the JIT settled, as the plan's timing rules say:
    //   DOTNET_TieredCompilation=0 CUO_ECS_BENCH=<file> dotnet test tests/ClassicUO.UnitTests -c Release --filter EcsPacketCostBench
    // Each scenario applies one packet kind to a legacy World holding N mobiles (5 worn items each) and calls the same
    // WorldEcs hook the packet handler reaches, then reports microseconds and allocated bytes per packet.
    public class EcsPacketCostBench
    {
        private const uint MOB0 = 0x0000_1000, ITEM0 = 0x4000_0000, SPARE0 = 0x5000_0000;
        private const int WORN = 5;

        private sealed class Scene
        {
            public World World;
            public Mobile[] Mobiles;
            public Item[] Worn; // WORN per mobile
            public Item Backpack;
        }

        private static Scene Build(int n)
        {
            var s = new Scene { World = new World(), Mobiles = new Mobile[n], Worn = new Item[n * WORN] };
            Layer[] layers = { Layer.Shirt, Layer.Pants, Layer.Shoes, Layer.Cloak, Layer.OneHanded };

            for (int i = 0; i < n; i++)
            {
                Mobile m = s.World.GetOrCreateMobile(MOB0 + (uint)i);
                m.Graphic = 0x190;
                m.X = (ushort)(1000 + i % 100);
                m.Y = (ushort)(1000 + i / 100);
                s.World.Ecs.Sync(m);
                s.Mobiles[i] = m;

                for (int k = 0; k < WORN; k++)
                {
                    Item it = s.World.GetOrCreateItem(ITEM0 + (uint)(i * WORN + k));
                    it.Graphic = (ushort)(0x1515 + k);
                    it.Layer = layers[k];
                    it.Container = m.Serial;
                    m.PushToBack(it);
                    s.World.Ecs.Sync(it);
                    s.Worn[i * WORN + k] = it;
                }
            }

            s.Backpack = s.World.GetOrCreateItem(ITEM0 + (uint)(n * WORN));
            s.Backpack.Graphic = 0xE75;
            s.World.Ecs.Sync(s.Backpack);

            return s;
        }

        private static (double us, double bytes) Measure(int packets, Action<int> one)
        {
            for (int i = 0; i < Math.Min(packets, 20_000); i++)
            {
                one(i); // warmup
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();

            for (int i = 0; i < packets; i++)
            {
                one(i);
            }

            long t1 = Stopwatch.GetTimestamp();
            long a1 = GC.GetAllocatedBytesForCurrentThread();

            return ((t1 - t0) * 1e6 / Stopwatch.Frequency / packets, (double)(a1 - a0) / packets);
        }

        private static void Run(StringBuilder sb, int n, int packets)
        {
            Scene s = Build(n);
            WorldEcs ecs = s.World.Ecs;
            int itemCount = n * WORN;
            uint spare = SPARE0;
            var rows = new List<(string name, (double us, double bytes) r)>();

            // 0x77/0x78 move: new position and direction, then Sync
            rows.Add(("mobile move (0x77: x,y,dir + Sync)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                m.X = (ushort)(m.X + 1);
                m.Direction = (Direction)(i & 7);
                ecs.Sync(m);
            })));

            // 0x78/0x20 resend with nothing changed
            rows.Add(("mobile Sync, nothing changed", Measure(packets, i => ecs.Sync(s.Mobiles[i % n]))));

            // body change: marks the equipment dirty
            rows.Add(("mobile graphic change + Sync (marks equipment dirty)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                m.Graphic = (ushort)((i & 1) == 0 ? 0x191 : 0x190);
                ecs.Sync(m);
            })));

            rows.Add(("PushInputs (flag/war mode/step change)", Measure(packets, i => ecs.PushInputs(s.Mobiles[i % n]))));

            // 0x2E refresh of a worn item (hue changes)
            rows.Add(("worn item Sync, hue change (equipment dirty)", Measure(packets, i =>
            {
                Item it = s.Worn[i % itemCount];
                it.Hue = (ushort)(i & 0x3FF);
                ecs.Sync(it);
            })));

            rows.Add(("worn item Sync, nothing changed", Measure(packets, i => ecs.Sync(s.Worn[i % itemCount]))));

            // unequip to the backpack then re-equip: parent index moves (indexed component re-add)
            rows.Add(("worn item to backpack / back (ContainedIn <-> EquippedOn)", Measure(packets, i =>
            {
                int idx = (i >> 1) % itemCount;
                Item it = s.Worn[idx];
                it.Container = (i & 1) == 0 ? s.Backpack.Serial : s.Mobiles[idx / WORN].Serial;
                ecs.Sync(it);
            })));

            // 0x1A/0xF3 ground item appears, then is deleted (0x1D): add and remove counted as two packets
            rows.Add(("ground item add (GetOrCreateItem + props + Sync) + delete, per packet", Measure(packets, i =>
            {
                if ((i & 1) == 0)
                {
                    Item it = s.World.GetOrCreateItem(spare);
                    it.Graphic = 0xEED;
                    it.X = 1100;
                    it.Y = 1100;
                    ecs.Sync(it);
                }
                else
                {
                    Item it = s.World.Items.Get(spare);
                    s.World.Items.Remove(spare);
                    ecs.OnDestroyed(it);
                    spare++;
                }
            })));

            // 0x25 item into a container, then out
            rows.Add(("item into backpack (0x25) + out, per packet", Measure(packets, i =>
            {
                uint ser = SPARE0 + 0x10_0000 + (uint)(i >> 1);

                if ((i & 1) == 0)
                {
                    Item it = s.World.GetOrCreateItem(ser);
                    it.Graphic = 0xF7A;
                    it.Container = s.Backpack.Serial;
                    ecs.Sync(it);
                }
                else
                {
                    Item it = s.World.Items.Get(ser);
                    s.World.Items.Remove(ser);
                    ecs.OnDestroyed(it);
                }
            })));

            // mobile leaves range (0x1D) and a new one reuses the serial while 5 items still name it: per packet
            int reuse = Math.Min(packets / 2, 20_000);
            rows.Add(("serial reuse: mobile delete + create same serial, per packet", Measure(reuse * 2, i =>
            {
                Mobile old = s.Mobiles[(i >> 1) % n];

                if ((i & 1) == 0)
                {
                    s.World.Mobiles.Remove(old.Serial);
                    ecs.OnDestroyed(old);
                }
                else
                {
                    Mobile m = s.World.GetOrCreateMobile(old.Serial);
                    m.Graphic = 0x190;
                    ecs.Sync(m);
                    s.Mobiles[(i >> 1) % n] = m;
                }
            })));

            // the same adds without the legacy objects: what the mirror itself allocates on create/delete
            WorldMirror mirror = ecs.Mirror;
            rows.Add(("mirror only: UpsertItem (ground) + Remove, per packet", Measure(packets, i =>
            {
                uint ser = SPARE0 + 0x20_0000 + (uint)(i >> 1);

                if ((i & 1) == 0)
                {
                    mirror.UpsertItem(ser, 0xEED, 0, 1100, 1100, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
                }
                else
                {
                    mirror.Remove(ser);
                }
            })));

            rows.Add(("mirror only: UpsertMobile + Remove, per packet", Measure(packets, i =>
            {
                uint ser = 0x0010_0000 + (uint)(i >> 1);

                if ((i & 1) == 0)
                {
                    mirror.UpsertMobile(ser, 0x190, 0, 1100, 1100, 0, 0, 0, false);
                }
                else
                {
                    mirror.Remove(ser);
                }
            })));

            sb.AppendLine($"mobiles={n} (each wearing {WORN}), items={itemCount}, packets per scenario={packets}");
            sb.AppendLine($"{"packet",-72} {"us/packet",10} {"bytes/packet",13}");

            foreach (var (name, r) in rows)
            {
                sb.AppendLine($"{name,-72} {r.us,10:F3} {r.bytes,13:F1}");
            }

            sb.AppendLine();
        }

        [Fact]
        public void Bench()
        {
            string output = Environment.GetEnvironmentVariable("CUO_ECS_BENCH");

            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"} server-gc={System.Runtime.GCSettings.IsServerGC}");

            foreach (int n in new[] { 500, 2000, 8000 })
            {
                Run(sb, n, 100_000);
            }

            File.WriteAllText(output, sb.ToString());
        }
    }
}
