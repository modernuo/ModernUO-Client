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

            // E11: SetInWorldTile (a teleport, a spawn, a multi/house piece): the position write plus one mirror Sync
            rows.Add(("mobile SetInWorldTile (position + AddToTile, no map)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                // coordinates advance per visit of each mobile (i / n), so no call repeats its mobile's previous position
                int visit = i / n;
                m.SetInWorldTile((ushort)(1000 + (visit & 1023)), (ushort)(2000 + ((visit >> 3) & 511)), (sbyte)(visit & 7));
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

            // T1: the whole item branch of UpdateGameObject (0x1A/0xF3) as the handler ran it before the funnel (a copy, in
            // WorldWriterTests) and through WorldWriter.UpsertWorldItem + PlaceWorldItem, paired. Delete as in the row above.
            uint spareOld = SPARE0 + 0x30_0000, spareNew = SPARE0 + 0x40_0000;
            rows.Add(("T1 old path: 0x1A new ground item (handler branch) + delete, per packet", Measure(packets, i =>
            {
                if ((i & 1) == 0)
                {
                    var p = new WorldWriterTests.Packet { Serial = spareOld, Graphic = 0xEED, Count = 5, X = 1100, Y = 1100, Direction = Direction.North };
                    WorldWriterTests.OldUpsert(s.World, p);
                }
                else
                {
                    Item it = s.World.Items.Get(spareOld);
                    s.World.Items.Remove(spareOld);
                    ecs.OnDestroyed(it);
                    spareOld++;
                }
            })));

            rows.Add(("T1 funnel: 0x1A new ground item (UpsertWorldItem) + delete, per packet", Measure(packets, i =>
            {
                if ((i & 1) == 0)
                {
                    s.World.Writer.UpsertWorldItem(spareNew, 0xEED, 0, 5, 1100, 1100, 0, Direction.North, 0, ClassicUO.Game.Data.Flags.None, 0, out _, out WorldWriter.Placement pn);
                    s.World.Writer.PlaceWorldItem(pn);
                }
                else
                {
                    Item it = s.World.Items.Get(spareNew);
                    s.World.Items.Remove(spareNew);
                    ecs.OnDestroyed(it);
                    spareNew++;
                }
            })));

            // an existing ground item updated in place (a resend, a stack amount change): the common 0x1A
            Item upd = s.World.GetOrCreateItem(SPARE0 + 0x50_0000);
            upd.SetInWorldTile(1100, 1100, 0);
            rows.Add(("T1 old path: 0x1A update of a ground item", Measure(packets, i =>
            {
                var p = new WorldWriterTests.Packet { Serial = upd.Serial, Graphic = 0xEED, Count = (ushort)(1 + (i & 63)), X = 1100, Y = 1100, Direction = Direction.North };
                WorldWriterTests.OldUpsert(s.World, p);
            })));

            rows.Add(("T1 funnel: 0x1A update of a ground item", Measure(packets, i =>
            {
                s.World.Writer.UpsertWorldItem(upd.Serial, 0xEED, 0, (ushort)(1 + (i & 63)), 1100, 1100, 0, Direction.North, 0, ClassicUO.Game.Data.Flags.None, 0, out _, out WorldWriter.Placement pu);
                s.World.Writer.PlaceWorldItem(pu);
            })));

            // T1 containers: AddItemToContainer as before the funnel (a copy, in WorldWriterTests) and WorldWriter.PlaceInContainer,
            // paired. A new item into the backpack, deleted (unlinked, as the 0x1D removal does) on the next packet; and an item
            // already in the backpack updated in place (an amount change: the re-push)
            uint cSpareOld = SPARE0 + 0x60_0000, cSpareNew = SPARE0 + 0x70_0000;
            uint pack = s.Backpack.Serial;

            void DeleteFromPack(uint serial)
            {
                Item it = s.World.Items.Get(serial);
                s.Backpack.Remove(it);
                s.World.Items.Remove(serial);
                ecs.OnDestroyed(it);
            }

            rows.Add(("T1 old path: 0x25 new item into backpack (AddItemToContainer) + delete, per packet", Measure(packets, i =>
            {
                if ((i & 1) == 0)
                {
                    WorldWriterTests.OldAddToContainer(s.World, cSpareOld, 0xF7A, 1, 40, 50, 0, pack);
                }
                else
                {
                    DeleteFromPack(cSpareOld++);
                }
            })));

            rows.Add(("T1 funnel: 0x25 new item into backpack (PlaceInContainer) + delete, per packet", Measure(packets, i =>
            {
                if ((i & 1) == 0)
                {
                    s.World.Writer.PlaceInContainer(cSpareNew, 0xF7A, 1, 40, 50, 0, pack, out _);
                }
                else
                {
                    DeleteFromPack(cSpareNew++);
                }
            })));

            Item inPack = s.World.GetOrCreateItem(SPARE0 + 0x80_0000);
            WorldWriterTests.OldAddToContainer(s.World, inPack.Serial, 0xF7A, 1, 40, 50, 0, pack);
            rows.Add(("T1 old path: 0x25 update of an item in the backpack", Measure(packets, i =>
                WorldWriterTests.OldAddToContainer(s.World, inPack.Serial, 0xF7A, (ushort)(1 + (i & 63)), 40, 50, 0, pack))));

            rows.Add(("T1 funnel: 0x25 update of an item in the backpack", Measure(packets, i =>
                s.World.Writer.PlaceInContainer(inPack.Serial, 0xF7A, (ushort)(1 + (i & 63)), 40, 50, 0, pack, out _))));

            // T1 equipment: EquipItem (0x2E) and UpdateObject's equipment list (0x78) as before the funnel (copies in
            // WorldWriterTests) and through WorldWriter, paired. 0x2E: a worn item re-equipped on its wearer (a hue change).
            // 0x78: a mobile's whole list resent (the clear removes its WORN worn items, the entries recreate them), per packet
            rows.Add(("T1 old path: 0x2E re-equip of a worn item", Measure(packets, i =>
            {
                Item it = s.Worn[i % itemCount];
                var e = new WorldWriterTests.EquipPacket { Serial = it.Serial, Graphic = it.Graphic, Hue = (ushort)(i & 63), Layer = it.Layer, Container = it.Container };
                WorldWriterTests.OldEquip(s.World, e, out _, out _);
            })));

            rows.Add(("T1 funnel: 0x2E re-equip of a worn item (Equip)", Measure(packets, i =>
            {
                Item it = s.Worn[i % itemCount];
                s.World.Writer.Equip(it.Serial, it.Graphic, it.Layer, it.Container, (ushort)(i & 63), out _, out _);
            })));

            Layer[] wornLayers = { Layer.Shirt, Layer.Pants, Layer.Shoes, Layer.Cloak, Layer.OneHanded };
            var lists = new (uint Serial, ushort Graphic, Layer Layer, ushort Hue)[n][];

            for (int m = 0; m < n; m++)
            {
                lists[m] = new (uint, ushort, Layer, ushort)[WORN];

                for (int k = 0; k < WORN; k++)
                {
                    lists[m][k] = (ITEM0 + (uint)(m * WORN + k), (ushort)(0x1515 + k), wornLayers[k], 0);
                }
            }

            int lists78 = Math.Max(packets / WORN, 1);
            rows.Add(("T1 old path: 0x78 equipment list (clear + 5 entries), per packet", Measure(lists78, i =>
                WorldWriterTests.OldWear(s.World, s.Mobiles[i % n].Serial, lists[i % n]))));

            rows.Add(("T1 funnel: 0x78 equipment list (ClearWornItems + 5 WearItem)", Measure(lists78, i =>
            {
                Mobile owner = s.Mobiles[i % n];
                var list = lists[i % n];
                s.World.Writer.ClearWornItems(owner.Serial);

                for (int k = 0; k < list.Length; k++)
                {
                    s.World.Writer.WearItem(owner, list[k].Serial, list[k].Graphic, list[k].Layer, list[k].Hue);
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

            // T1 mobiles (funnel part 4), after every older row so they cannot shift them: 0x77 for another mobile (the
            // notoriety, then UpdateGameObject's mobile branch: a step east per visit; a full queue resets) and the player's
            // body of 0x78 (here on a plain mobile), before the funnel (copies in WorldWriterMobileTests) and through it, paired
            rows.Add(("T1 old path: 0x77 move of another mobile (notoriety + step + tile)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                var p = new WorldWriterMobileTests.MobPacket { Serial = m.Serial, Graphic = 0x190, X = (ushort)(m.X + 1), Y = m.Y, Direction = Direction.East, Hue = (ushort)(i & 63) };
                m.NotorietyFlag = (NotorietyFlag)(1 + (i & 3));
                WorldWriterMobileTests.OldUpsertMobile(s.World, p);
            })));

            rows.Add(("T1 funnel: 0x77 move of another mobile (SetNotoriety + UpsertMobile + PlaceMobile)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                s.World.Writer.SetNotoriety(m, (NotorietyFlag)(1 + (i & 3)));
                s.World.Writer.UpsertMobile(m, m.Serial, 0x190, 0, (ushort)(m.X + 1), m.Y, 0, Direction.East, (ushort)(i & 63), 0, out _, out WorldWriter.Placement placement);
                s.World.Writer.PlaceMobile(placement);
            })));

            rows.Add(("T1 old path: 0x78 player body (graphic, sex, hue, flags + Sync)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                m.Graphic = (ushort)((i & 1) == 0 ? 0x191 : 0x190);
                WorldWriterMobileTests.OldCheckGraphicChange(m);
                m.FixHue((ushort)(i & 63));
                m.Flags = (ClassicUO.Game.Data.Flags)(i & 0x40);
                ecs.Sync(m);
            })));

            rows.Add(("T1 funnel: 0x78 player body (SetMobileBody)", Measure(packets, i =>
                s.World.Writer.SetMobileBody(s.Mobiles[i % n], (ushort)((i & 1) == 0 ? 0x191 : 0x190), (ushort)(i & 63), (ClassicUO.Game.Data.Flags)(i & 0x40), checkBody: true, setDirection: false, 0))));

            // T1 funnel part 5, after every older row: Mobile.ProcessSteps' step completion (per step, not per frame), a step
            // east queued and the front step completed per visit (the queue cost is the same in both rows), old copy vs funnel
            rows.Add(("T1 old path: step completion (queue a step east + complete the front one)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                WorldWriterMobileTests.OldEnqueueStep(m, m.X + 1 + m.Steps.Count, m.Y, 0, Direction.East, false);
                Mobile.Step st = m.Steps.Front();
                WorldWriterMobileTests.OldCompleteStep(m, m.X == st.X && m.Y == st.Y);
            })));

            rows.Add(("T1 funnel: step completion (queue a step east + CompleteStep)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                WorldWriterMobileTests.OldEnqueueStep(m, m.X + 1 + m.Steps.Count, m.Y, 0, Direction.East, false);
                Mobile.Step st = m.Steps.Front();
                s.World.Writer.CompleteStep(m, in st, m.X == st.X && m.Y == st.Y);
            })));

            // T1 funnel part 6, after every older row: status and hits (group 4), old copy vs funnel, and the per-call cost of a
            // funnel operation that writes only the cache (admission, the held check, the scope): the animation group's probe
            // (ProcessAnimation runs per due mobile per frame; this is the overhead its conversion would add per call)
            WorldWriterMobileTests.PrefillHitsTexts();

            rows.Add(("T1 old path: 0xA1 hits (HitsMax, Hits, request)", Measure(packets, i =>
                WorldWriterMobileTests.OldUpdateHitpoints(s.World, s.Mobiles[i % n].Serial, 100, (ushort)(i & 63)))));

            rows.Add(("T1 funnel: 0xA1 hits (SetHits)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                s.World.Writer.SetHits(s.World.Get(m.Serial), 100, (ushort)(i & 63));
            })));

            rows.Add(("T1 old path: 0x17 healthbar (yellow bar + SA poison + Sync)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                m.SetSAPoison((i & 2) != 0);
                m.Flags = (i & 1) != 0 ? m.Flags | ClassicUO.Game.Data.Flags.YellowBar : m.Flags & ~ClassicUO.Game.Data.Flags.YellowBar;
                ecs.Sync(m);
            })));

            rows.Add(("T1 funnel: 0x17 healthbar (SetHealthbar)", Measure(packets, i =>
            {
                Mobile m = s.Mobiles[i % n];
                s.World.Writer.SetHealthbar(m, (i & 1) != 0 ? m.Flags | ClassicUO.Game.Data.Flags.YellowBar : m.Flags & ~ClassicUO.Game.Data.Flags.YellowBar, true, (i & 2) != 0);
            })));

            rows.Add(("T1 animation probe: bare cache write (Race)", Measure(packets, i =>
                s.Mobiles[i % n].Race = (RaceType)(1 + (i & 1)))));

            rows.Add(("T1 animation probe: the same through the funnel (SetRace)", Measure(packets, i =>
                s.World.Writer.SetRace(s.Mobiles[i % n], (RaceType)(1 + (i & 1))))));

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
