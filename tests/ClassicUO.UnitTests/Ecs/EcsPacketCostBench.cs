using Uo.Pack;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // t3a-fold: the lists as the handler hands them to ReplaceWornItems
        private static WorldWriter.WornEntry[][] ToEntries((uint Serial, ushort Graphic, Layer Layer, ushort Hue)[][] lists) =>
            lists.Select(l => l.Select(e => new WorldWriter.WornEntry(e.Serial, e.Graphic, e.Layer, e.Hue)).ToArray()).ToArray();

        private static (double us, double bytes) Measure(int packets, Action<int> one)
        {
            // warmup: one full pass (T1 part 9, Codex audit: a 20,000-call warmup left half of the 8,000-mobile scene's 40,000
            // worn items to their first commit inside the measured loop)
            for (int i = 0; i < packets; i++)
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

            // t3a-fold: the handler's path is ReplaceWornItems (the clear and the 5 wears in one operation); the row keeps its
            // name so E2 pairs it with the base's
            var entries78 = ToEntries(lists);

            rows.Add(("T1 funnel: 0x78 equipment list (ClearWornItems + 5 WearItem)", Measure(lists78, i =>
                s.World.Writer.ReplaceWornItems(s.Mobiles[i % n], entries78[i % n]))));

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

            // T2, after every older row (so they run without a cached parent, as in the base). The funnel rows run on three
            // fresh scenes, each built and prepared the same way and running the same rows in the same order: "T2 control"
            // (nothing cached), "T2 cached" (the backpack's and every mobile's contents cached before the warm-up: an open
            // container gump, a counter's walk; so every change of attachment pays the cache maintenance, an invalidation
            // lookup, while the build waits for the next read) and "T2 control2" (nothing cached again). The two controls
            // bracket the cached run and must allocate the same: that shows the scenes are equal, and only then is cached <=
            // control a proof of no added allocation (Codex audit round 2; round 1's control ran back to back with the cached
            // row on one mutated scene, where a repeated 0x78 row allocates 1819 B/packet against 1400 on its first run, a
            // run-order effect older than T2, so +256 B in the cached path would still have passed). Then the worst case of a
            // reader on the main scene: the backpack's contents read after every packet (a build each time), paired with the
            // cached update row.
            void T2Scene(string kind, bool cached)
            {
                Scene f = Build(n);
                WorldEcs fecs = f.World.Ecs;
                uint fpack = f.Backpack.Serial;
                Item fin = f.World.GetOrCreateItem(SPARE0 + 0x80_0000);
                WorldWriterTests.OldAddToContainer(f.World, fin.Serial, 0xF7A, 1, 40, 50, 0, fpack);
                uint t2Spare = SPARE0 + 0x78_0000;

                void Delete(uint serial)
                {
                    Item it = f.World.Items.Get(serial);
                    f.Backpack.Remove(it);
                    f.World.Items.Remove(serial);
                    fecs.OnDestroyed(it);
                }

                if (cached)
                {
                    f.World.State.Contents(fpack);

                    for (int m = 0; m < n; m++)
                    {
                        f.World.State.Contents(f.Mobiles[m].Serial);
                    }
                }

                rows.Add(($"T2 {kind}: 0x25 new item into backpack (PlaceInContainer) + delete, per packet", Measure(packets, i =>
                {
                    if ((i & 1) == 0)
                    {
                        f.World.Writer.PlaceInContainer(t2Spare, 0xF7A, 1, 40, 50, 0, fpack, out _);
                    }
                    else
                    {
                        Delete(t2Spare++);
                    }
                })));

                rows.Add(($"T2 {kind}: 0x25 update of an item in the backpack", Measure(packets, i =>
                    f.World.Writer.PlaceInContainer(fin.Serial, 0xF7A, (ushort)(1 + (i & 63)), 40, 50, 0, fpack, out _))));

                rows.Add(($"T2 {kind}: 0x2E re-equip of a worn item (Equip)", Measure(packets, i =>
                {
                    Item it = f.Worn[i % itemCount];
                    f.World.Writer.Equip(it.Serial, it.Graphic, it.Layer, it.Container, (ushort)(i & 63), out _, out _);
                })));

                rows.Add(($"T2 {kind}: 0x78 equipment list (ClearWornItems + 5 WearItem)", Measure(lists78, i =>
                    f.World.Writer.ReplaceWornItems(f.Mobiles[i % n], entries78[i % n]))));
            }

            T2Scene("control", false);
            T2Scene("cached", true);
            T2Scene("control2", false);
            GC.Collect();

            rows.Add(("T2 read: 0x25 update in the backpack + its Contents read (a build)", Measure(packets, i =>
            {
                s.World.Writer.PlaceInContainer(inPack.Serial, 0xF7A, (ushort)(1 + (i & 63)), 40, 50, 0, pack, out _);
                s.World.State.Contents(pack);
            })));

            // T3a: the equipment read, MobileView's per-frame reads of one drawn mobile: the guard (HasWornItems: !IsEmpty off,
            // any WornLayers slot on), then FindItemByLayer for layers 0x01..0x19 (the draw order's layers and the mount), with
            // ecs_read_equipment off (the list walk) and on (WornLayers). Like T2's rows, on three freshly built, identically
            // prepared scenes: "T3 control" (off), "T3 read" (on), "T3 control2" (off). Each scene's mobiles wear their 5 items
            // through the funnel (0x78: attached, stamped, as in a session), and as many bare mobiles (a monster: the guard only).
            int t3Sink = 0;

            void T3Scene(string kind, bool read)
            {
                Scene f = Build(n);
                var bare = new Mobile[n];

                for (int m = 0; m < n; m++)
                {
                    Mobile owner = f.Mobiles[m];
                    f.World.Writer.ClearWornItems(owner.Serial);

                    for (int k = 0; k < WORN; k++)
                    {
                        Item it = f.Worn[m * WORN + k];
                        f.World.Writer.WearItem(owner, it.Serial, it.Graphic, it.Layer, it.Hue);
                    }

                    bare[m] = f.World.GetOrCreateMobile(MOB0 + 0x10_0000 + (uint)m);
                    f.World.Ecs.Sync(bare[m]);
                }

                f.World.State.ReadEquipmentOverride = read;

                rows.Add(($"T3 {kind}: MobileView reads (guard + FindItemByLayer 0x01-0x19), per mobile", Measure(packets, i =>
                {
                    Mobile mob = f.Mobiles[i % n];

                    if (f.World.State.HasWornItems(mob))
                    {
                        for (int l = 1; l <= (int)Layer.Mount; l++)
                        {
                            if (mob.FindItemByLayer((Layer)l) != null)
                            {
                                t3Sink++;
                            }
                        }
                    }
                })));

                rows.Add(($"T3 {kind}: MobileView guard of a bare mobile, per mobile", Measure(packets, i =>
                {
                    if (f.World.State.HasWornItems(bare[i % n]))
                    {
                        t3Sink++;
                    }
                })));
            }

            T3Scene("control", false);
            T3Scene("read", true);
            T3Scene("control2", false);
            GC.Collect();

            // T3b: the layering decision of one drawn mobile, hard-coded (PaperdollOrder.Build + Filter + ApplyDirectionCloak,
            // then AnimationRules.IsCovered for every drawn layer: the rules MobileView and MobileAnimationSystem run) against
            // the data rules (LayerRules: BuildOrder + Filter + ApplyDirection + IsCovered), on the same per-mobile tables (n
            // random outfits of typical items, the bodies of every group, the 8 directions). The tables are shared input
            // (MobileView gathers them with FindItemByLayer either way), so the rows measure the decision only. New arm only:
            // "T3b hard", "T3b data", "T3b hard2" (e2t3b.py: data <= hard within the +0.1 us / 2x spread rule, 0 B).
            T3bRows(rows, n, packets);

            if (t3Sink < 0)
            {
                sb.AppendLine("unreachable");
            }

            sb.AppendLine($"mobiles={n} (each wearing {WORN}), items={itemCount}, packets per scenario={packets}");
            sb.AppendLine($"{"packet",-72} {"us/packet",10} {"bytes/packet",13}");

            foreach (var (name, r) in rows)
            {
                sb.AppendLine($"{name,-72} {r.us,10:F3} {r.bytes,13:F1}");
            }

            sb.AppendLine();
        }

        private static void T3bRows(List<(string name, (double us, double bytes) r)> rows, int n, int packets)
        {
            const int N = PaperdollOrder.N;
            var rnd = new Random(7);
            ushort[] bodies = { 0x190, 0x191, 0x25D, 0x25E, 0x29A, 0x29B, 0x190, 0x191 };
            // typical worn items per layer (graphic, AnimID), including the values the rules key on
            var choices = new Dictionary<int, (ushort g, ushort a)[]>
            {
                [(int)Layer.Shirt] = new (ushort, ushort)[] { (0x1517, 0x1D2), (0x1EFD, 0x1E9) },
                [(int)Layer.Pants] = new (ushort, ushort)[] { (0x152E, 0x1EB), (0x1539, 0x200), (0x1411, 0x513), (0x13BE, 0x3E4) },
                [(int)Layer.Shoes] = new (ushort, ushort)[] { (0x170F, 0x1F1), (0x1711, 0x1F5) },
                [(int)Layer.Helmet] = new (ushort, ushort)[] { (0x1408, 0x201), (0x1715, 0x1A9), (0xA42B, 0x5E9) },
                [(int)Layer.Necklace] = new (ushort, ushort)[] { (0x13C7, 0x1C8), (0x1088, 0x5EC) },
                [(int)Layer.Hair] = new (ushort, ushort)[] { (0x203B, 0x1F7), (0x203C, 0x1F8) },
                [(int)Layer.Torso] = new (ushort, ushort)[] { (0x1415, 0x21A), (0x1C04, 0x39A), (0x13CC, 0x3A9) },
                [(int)Layer.Tunic] = new (ushort, ushort)[] { (0x1FFD, 0x238), (0x1541, 0x1E5) },
                [(int)Layer.Arms] = new (ushort, ushort)[] { (0x1410, 0x210), (0x13CD, 0x3B1) },
                [(int)Layer.Cloak] = new (ushort, ushort)[] { (0x1515, 0x1E0), (0x2FB9, 0x380) },
                [(int)Layer.Robe] = new (ushort, ushort)[] { (0x1F03, 0x504), (0x2683, 0x1E6), (0x9985, 0x4E8), (0x204E, 0x229) },
                [(int)Layer.Skirt] = new (ushort, ushort)[] { (0x1516, 0x1C7), (0x1537, 0x1E4) },
                [(int)Layer.Legs] = new (ushort, ushort)[] { (0x1411, 0x1F2) },
                [(int)Layer.OneHanded] = new (ushort, ushort)[] { (0x0F5E, 0x2E0) },
            };

            var present = new bool[n][];
            var gfx = new ushort[n][];
            var anim = new ushort[n][];

            for (int m = 0; m < n; m++)
            {
                present[m] = new bool[N];
                gfx[m] = new ushort[N];
                anim[m] = new ushort[N];

                foreach (var (layer, c) in choices)
                {
                    if (rnd.Next(3) == 0) continue;
                    var it = c[rnd.Next(c.Length)];
                    present[m][layer] = true;
                    gfx[m][layer] = it.g;
                    anim[m][layer] = it.a;
                }
            }

            LayerRules rules = LayerRulesDefault.Create();
            int sink = 0;

            (double, double) Hard() => Measure(packets, i =>
            {
                int m = i % n;
                ushort body = bodies[m & 7];
                bool garg = body == 0x29A || body == 0x29B;
                Span<Layer> raw = stackalloc Layer[N];
                Span<Layer> order = stackalloc Layer[N];
                PaperdollOrder.Build(anim[m], (m & 1) != 0 || garg, raw);
                int count = PaperdollOrder.ApplyDirectionCloak(order, PaperdollOrder.Filter(raw, false, order), (byte)(i & 7));

                for (int k = 0; k < count; k++)
                {
                    Layer l = order[k];
                    if (present[m][(int)l] && !AnimationRules.IsCovered(l, gfx[m], anim[m], present[m], body)) sink++;
                }
            });

            (double, double) Data() => Measure(packets, i =>
            {
                int m = i % n;
                ushort body = bodies[m & 7];
                bool garg = body == 0x29A || body == 0x29B;
                var subject = new LayerSubject(rules.GroupOf(body), (byte)(((m & 1) != 0 ? LayerRulesConst.FlagFemale : 0) | (garg ? LayerRulesConst.FlagGargoyleDraw : 0)));
                Span<byte> raw = stackalloc byte[N];
                Span<Layer> order = stackalloc Layer[N];
                rules.BuildOrder(LayerRuleSet.World, anim[m], subject, raw);
                int count = PaperdollOrder.Filter(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Layer>(raw), false, order);
                count = rules.ApplyDirection(LayerRuleSet.World, System.Runtime.InteropServices.MemoryMarshal.Cast<Layer, byte>(order), count, (byte)(i & 7), anim[m], subject);

                for (int k = 0; k < count; k++)
                {
                    byte l = (byte)order[k];
                    if (present[m][l] && !rules.IsCovered(LayerRuleSet.World, l, present[m], gfx[m], anim[m], subject)) sink++;
                }
            });

            // T3b cache (LayerDecision): the same decision served from each mobile's cached answer, keyed as the client's
            // WorldState.WorldLayers does: the subject (body, flags), the mirror record's WornLayers.Version (the WornCache's,
            // held per mobile here) and, after an equipment mark anywhere, the record's Look. The tables stay shared input:
            // a miss gathers them in the client, as the hard path does. Rows: "hit" (no equipment change), "miss" (every
            // decision refills: the outfit changes every time), "mixed" (every 50th decision a mobile's outfit changes and
            // the mirror sees a mark: 2 % misses, and the decisions after a mark take the Look read until they catch up).
            var mirror = new WorldMirror();
            var records = new Friflo.Engine.ECS.Entity[n];
            var versions = new ulong[n];
            var tables = new LayerTables[n];
            var decisions = new LayerDecision[n];

            for (int m = 0; m < n; m++)
            {
                records[m] = mirror.UpsertMobile(0x0000_5000 + (uint)m, bodies[m & 7], 0, 10, 10, 0, 0, 0, false);
                mirror.UpsertItem(0x4100_0000 + (uint)m, 0x1517, 0, 0, 0, 0, 0, 0, 1, 0x0000_5000 + (uint)m, (byte)Layer.Shirt, false, attach: true);
                versions[m] = records[m].GetComponent<EquipmentSlots>().Worn.Version;
                decisions[m] = new LayerDecision();

                for (int l = 0; l < N; l++)
                {
                    tables[m].Present[l] = present[m][l];
                    tables[m].Graphic[l] = gfx[m][l];
                    tables[m].Anim[l] = anim[m][l];
                }
            }

            (double, double) Cached(int churn) => Measure(packets, i =>
            {
                int m = i % n;
                ushort body = bodies[m & 7];
                bool garg = body == 0x29A || body == 0x29B;
                var subject = new LayerSubject(rules.GroupOf(body), (byte)(((m & 1) != 0 ? LayerRulesConst.FlagFemale : 0) | (garg ? LayerRulesConst.FlagGargoyleDraw : 0)), body);
                LayerDecision d = decisions[m];

                if (churn != 0 && i % churn == 0)
                {
                    d.Invalidate();
                    mirror.EquipChanges++;
                }

                if (!d.Lookup(rules, subject, versions[m], mirror, records[m]))
                {
                    d.Fill(ref tables[m]);
                }

                ReadOnlySpan<Layer> order = d.Order((byte)(i & 7));

                for (int k = 0; k < order.Length; k++)
                {
                    Layer l = order[k];
                    if (present[m][(int)l] && !d.IsCovered(l)) sink++;
                }
            });

            rows.Add(("T3b hard: layering decision (order + covered), per mobile", Hard()));
            rows.Add(("T3b data: layering decision (order + covered), per mobile", Data()));
            rows.Add(("T3b cache hit: layering decision (order + covered), per mobile", Cached(0)));
            rows.Add(("T3b cache miss: layering decision (order + covered), per mobile", Cached(1)));
            rows.Add(("T3b cache mixed: layering decision, 2 % outfit changes, per mobile", Cached(50)));
            rows.Add(("T3b hard2: layering decision (order + covered), per mobile", Hard()));

            if (sink < 0)
            {
                rows.Add(("unreachable", (0, 0)));
            }
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
