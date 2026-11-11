using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using WornEntry = ClassicUO.Game.WorldWriter.WornEntry;

namespace ClassicUO.UnitTests.Ecs
{
    // t3a-fold: 0x78/0xD3's equipment list as one operation (WorldWriter.ReplaceWornItems: the wearer's WornLayers batched in
    // the mirror, written back once). Each case runs the same packets through the T3a path it replaces (copied below as the
    // reference: ClearWornItems, then one WearItem per entry, every departure and arrival on the wearer's component) on one
    // World and through ReplaceWornItems on another, from identical starting states, and compares: every item's values, which
    // Item objects survived and which were destroyed (and in which order: the destroyed-items callback), the OPL entries,
    // every child list in order, the mirror (records, stamps, entity ids, tags, epoch, counters, the equipment and contents
    // indexes in order), every mobile's WornLayers serials and counts, and the version semantics: the reference bumps at every
    // slot change, ReplaceWornItems bumps the wearer once when its serials or counts changed and never when they did not.
    // With ecs_read_equipment on (a warm WornCache), every layer's read equals the list's.
    public class ReplaceWornItemsTests
    {
        private const uint MOB = 0x0000_3000, OTHER = 0x0000_3001, DEAD = MOB | 0x8000_0000;
        private const uint BAG = 0x4000_8000, INBAG = 0x4000_8001, GROUND = 0x4000_8002;
        private const uint W1 = 0x4000_9001, W2 = 0x4000_9002, W3 = 0x4000_9003, W4 = 0x4000_9004, W5 = 0x4000_9005;
        private const uint MOUNT = 0x4000_9006, QUIVER = 0x4000_9007, ARROW = 0x4000_9008, PACK = 0x4000_9009, PACKITEM = 0x4000_900A;
        private const uint OPEN = 0x4000_900B, OPENITEM = 0x4000_900C, ORPHAN = 0x4000_900D, O1 = 0x4000_900E;
        private const uint N1 = 0x4000_9101, N2 = 0x4000_9102, N3 = 0x4000_9103;

        private static WornEntry E(uint serial, ushort graphic, Layer layer, ushort hue = 0) => new WornEntry(serial, graphic, layer, hue);

        private static readonly WornEntry[] Outfit =
        {
            E(W1, 0x1515, Layer.Shirt, 0x21), E(W2, 0x1516, Layer.Pants), E(W3, 0x170F, Layer.Shoes), E(W4, 0x1515, Layer.Cloak),
            E(W5, 0x0F5E, Layer.OneHanded), E(MOUNT, 0x3EA2, Layer.Mount), E(QUIVER, 0x2FB7, Layer.Waist), E(PACK, 0x0E75, Layer.Backpack)
        };

        private static WornEntry[] With(params WornEntry[] more) => Outfit.Concat(more).ToArray();

        private static WornEntry[] Without(uint serial) => Outfit.Where(e => e.Serial != serial).ToArray();

        private static WornEntry[] Replaced(uint serial, WornEntry by) => Outfit.Select(e => e.Serial == serial ? by : e).ToArray();

        // the packets: each from the built state, then all of them in a row (twice)
        internal static readonly (string Name, WornEntry[] List)[] Packets =
        {
            ("identical outfit resent", Outfit),
            ("the same items, another order", Outfit.Reverse().ToArray()),
            ("the same items, a hue and a graphic changed", Replaced(W1, E(W1, 0x1517, Layer.Shirt, 0x455))),
            ("one item swapped", Replaced(W2, E(N1, 0x1539, Layer.Pants))),
            ("an item's layer moved", Replaced(W3, E(W3, 0x170F, Layer.Gloves))),
            ("an item added", With(E(N2, 0x1408, Layer.Helmet))),
            ("an item removed", Without(W4)),
            ("two items on one layer", With(E(N3, 0x1517, Layer.Shirt))),
            ("an item listed twice", With(E(W1, 0x1517, Layer.Pants))),
            ("items from elsewhere (a bag, the ground, another wearer, the orphan)", With(E(INBAG, 0x1F03, Layer.Robe), E(GROUND, 0x1F7B, Layer.Arms), E(O1, 0x1515, Layer.Tunic), E(ORPHAN, 0x1408, Layer.Helmet))),
            ("the opened container listed", With(E(OPEN, 0x0E76, Layer.Bank))),
            ("the mount alone", new[] { E(MOUNT, 0x3EA2, Layer.Mount) }),
            ("a corpse graphic", With(E(N2, 0x2006, Layer.Helmet, 0x21))),
            ("empty", Array.Empty<WornEntry>()),
            ("the wearer's serial as an entry (a record over the wearer's)", With(E(MOB, 0x1515, Layer.Talisman))),
        };

        public static IEnumerable<object[]> Cases()
        {
            for (int p = 0; p < Packets.Length; p++)
            {
                foreach (bool dead in new[] { false, true })
                {
                    foreach (bool read in new[] { false, true })
                    {
                        yield return new object[] { p, dead, read };
                    }
                }
            }
        }

        // the T3a path before t3a-fold (the handler: the clear on the owner's serial, then each entry onto the captured owner)
        internal static void Reference(World world, Entity owner, WornEntry[] list)
        {
            world.Writer.ClearWornItems(owner.Serial);

            foreach (WornEntry e in list)
            {
                world.Writer.WearItem(owner, e.Serial, e.Graphic, e.Layer, e.Hue);
            }
        }

        private static Item Wear(World world, Entity owner, uint serial, ushort graphic, Layer layer) =>
            world.Writer.WearItem(owner, serial, graphic, layer, 0);

        // MOB wearing the outfit (the quiver with an arrow, the backpack with an item, an opened container with an item), an
        // orphan with MOB as its Container (never linked), OTHER wearing O1, a bag with an item, an item on the ground, OPL
        // entries; dead: MOB rekeyed as a dying mobile (DEAD) as 0xAF does
        private static World Build(bool dead)
        {
            var world = new World();

            foreach (uint s in new[] { MOB, OTHER })
            {
                Mobile m = world.GetOrCreateMobile(s);
                m.Graphic = 0x190;
                m.SetInWorldTile(1000, 1000, 0);
                world.Ecs.Sync(m);
            }

            Item bag = world.GetOrCreateItem(BAG);
            bag.Graphic = 0xE75;
            bag.SetInWorldTile(1001, 1000, 0);
            world.Ecs.Sync(bag);
            world.Writer.PlaceInContainer(INBAG, 0x1F03, 1, 40, 50, 0, BAG, out _);

            Item ground = world.GetOrCreateItem(GROUND);
            ground.Graphic = 0x1F7B;
            ground.SetInWorldTile(1002, 1003, 5);
            world.Ecs.Sync(ground);

            Mobile mob = world.Mobiles.Get(MOB);
            Reference(world, mob, Outfit);
            world.Writer.PlaceInContainer(ARROW, 0x0F3F, 20, 10, 10, 0, QUIVER, out _);
            world.Writer.PlaceInContainer(PACKITEM, 0x0EED, 100, 10, 10, 0, PACK, out _);
            Wear(world, mob, OPEN, 0x0E76, Layer.Bank).Opened = true;
            world.Writer.PlaceInContainer(OPENITEM, 0x0EED, 5, 10, 10, 0, OPEN, out _);

            Item orphan = world.GetOrCreateItem(ORPHAN);
            orphan.Graphic = 0x1408;
            orphan.TestSetLayer(Layer.Helmet);
            orphan.TestSetContainer(MOB);
            world.Ecs.Sync(orphan);

            Wear(world, world.Mobiles.Get(OTHER), O1, 0x1515, Layer.Tunic);

            foreach (uint s in new[] { W1, W2, PACK, QUIVER, O1 })
            {
                world.OPL.Add(s, 1, "name", "data", 0);
            }

            if (dead)
            {
                Rekey(world);
            }

            return world;
        }

        // MOB rekeyed as DisplayDeath (0xAF) does for a dying mobile: its serial gets the high bit, its children follow
        private static void Rekey(World world)
        {
            Mobile owner = world.Mobiles.Get(MOB);
            world.Mobiles.Remove(owner.Serial).Should().BeTrue();
            LegacyRekeyReference.Rekey(world, owner, DEAD);
        }

        private sealed class Before
        {
            public Dictionary<uint, Item> Items;
            public Dictionary<uint, ulong> Versions;
            public Dictionary<uint, (uint[] Serials, byte[] Counts)> Slots;
            public int Destroyed;
            public long Writes, BatchWrites, BatchUnchanged;
        }

        private static Before Snapshot(World w) => new Before
        {
            Items = w.Items.Values.ToDictionary(i => i.Serial),
            Versions = MobileSerials(w).ToDictionary(s => s, s => Version(w, s)),
            Slots = MobileSerials(w).ToDictionary(s => s, s => Slots(w, s)),
            Destroyed = DestroyedItems(w).Count,
            Writes = w.Ecs.Mirror.WornWrites,
            BatchWrites = w.Ecs.Mirror.WornBatchWrites,
            BatchUnchanged = w.Ecs.Mirror.WornBatchUnchanged
        };

        private static IEnumerable<uint> MobileSerials(World w) => new[] { MOB, OTHER, DEAD }.Where(s => w.Ecs.Mirror.TryGetEntity(s, out EcsEntity e) && e.HasComponent<EquipmentSlots>());

        private static ulong Version(World w, uint mobile) =>
            w.Ecs.Mirror.TryGetEntity(mobile, out EcsEntity e) && e.HasComponent<EquipmentSlots>() ? e.GetComponent<EquipmentSlots>().Worn.Version : 0;

        private static (uint[] Serials, byte[] Counts) Slots(World w, uint mobile)
        {
            if (!w.Ecs.Mirror.TryGetEntity(mobile, out EcsEntity e) || !e.HasComponent<EquipmentSlots>())
            {
                return (Array.Empty<uint>(), Array.Empty<byte>());
            }

            WornLayers worn = e.GetComponent<EquipmentSlots>().Worn;

            return (((ReadOnlySpan<uint>)worn.Serials).ToArray(), ((ReadOnlySpan<byte>)worn.Counts).ToArray());
        }

        private static List<Item> DestroyedItems(World w) =>
            (List<Item>)typeof(World).GetField("_destroyedItems", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w);

        // the entity's children as the readers see them (both paths are the funnel's: the ECS)
        private static List<uint> Children(Entity e)
        {
            var list = new List<uint>();

            if (e?.World != null)
            {
                foreach (Item it in e.World.State.Children(e))
                {
                    list.Add(it.Serial);
                }
            }

            return list;
        }

        private static List<uint> Serials(Friflo.Engine.ECS.Entities entities)
        {
            var list = new List<uint>();

            foreach (EcsEntity e in entities)
            {
                list.Add(e.GetComponent<Serial>().Value);
            }

            return list;
        }

        // a and b: the reference World and the ReplaceWornItems World, after the same packet; ba, bb: their snapshots before it
        private static void AssertSame(World a, World b, Before ba, Before bb, uint owner, string because)
        {
            b.Items.Count.Should().Be(a.Items.Count, because);
            b.Mobiles.Count.Should().Be(a.Mobiles.Count, because);
            b.Items.Keys.OrderBy(s => s).Should().Equal(a.Items.Keys.OrderBy(s => s), because);

            foreach (Item x in a.Items.Values)
            {
                Item y = b.Items.Get(x.Serial);
                string why = $"{because}: 0x{x.Serial:X8}";
                y.Graphic.Should().Be(x.Graphic, why);
                y.Hue.Should().Be(x.Hue, why);
                (y.X, y.Y, y.Z).Should().Be((x.X, x.Y, x.Z), why);
                y.Direction.Should().Be(x.Direction, why);
                y.Flags.Should().Be(x.Flags, why);
                y.Amount.Should().Be(x.Amount, why);
                y.Container.Should().Be(x.Container, why);
                y.Layer.Should().Be(x.Layer, why);
                y.Opened.Should().Be(x.Opened, why);
                y.IsDestroyed.Should().Be(x.IsDestroyed, why);
                y.AnimIndex.Should().Be(x.AnimIndex, why);
                y.UsedLayer.Should().Be(x.UsedLayer, why);
                y.AllowedToDraw.Should().Be(x.AllowedToDraw, why);
                b.OPL.Contains(x.Serial).Should().Be(a.OPL.Contains(x.Serial), why + " OPL");
                Children(y).Should().Equal(Children(x), why + " child list");
                Serials(b.Ecs.Mirror.GetContents(x.Serial)).Should().Equal(Serials(a.Ecs.Mirror.GetContents(x.Serial)), why + " contents index");
            }

            // item identity: the same serials kept their Item object, and the same old objects were destroyed
            foreach (KeyValuePair<uint, Item> old in ba.Items)
            {
                Item oldB = bb.Items[old.Key];
                oldB.IsDestroyed.Should().Be(old.Value.IsDestroyed, $"{because}: the old object of 0x{old.Key:X8} destroyed");
                ReferenceEquals(b.Items.Get(old.Key), oldB).Should().Be(ReferenceEquals(a.Items.Get(old.Key), old.Value), $"{because}: 0x{old.Key:X8} kept its object");
            }

            // the destroyed-items callback, in order
            DestroyedItems(b).Skip(bb.Destroyed).Select(i => i.Serial).Should().Equal(DestroyedItems(a).Skip(ba.Destroyed).Select(i => i.Serial), because + ": destroyed in order");

            foreach (uint s in new[] { MOB, OTHER, DEAD })
            {
                Children(b.Mobiles.Get(s)).Should().Equal(Children(a.Mobiles.Get(s)), $"{because}: 0x{s:X8}'s child list");
            }

            WorldMirror ma = a.Ecs.Mirror, mb = b.Ecs.Mirror;
            mb.Count.Should().Be(ma.Count, because);
            mb.ItemCount.Should().Be(ma.ItemCount, because);
            mb.MobileCount.Should().Be(ma.MobileCount, because);
            mb.Epoch.Should().Be(ma.Epoch, because);
            mb.ChildStamp.Should().Be(ma.ChildStamp, because);

            foreach (uint s in ma.Serials.Union(mb.Serials).ToList())
            {
                bool ha = ma.TryGet(s, out MirrorRecord ra), hb = mb.TryGet(s, out MirrorRecord rb);
                hb.Should().Be(ha, $"{because}: 0x{s:X8} mirrored");
                rb.Should().BeEquivalentTo(ra, $"{because}: 0x{s:X8} record");
                ma.TryGetExtras(s, out MirrorExtras xa).Should().Be(mb.TryGetExtras(s, out MirrorExtras xb));
                xb.Should().BeEquivalentTo(xa, $"{because}: 0x{s:X8} extras and stamp");
                ma.TryGetEntity(s, out EcsEntity ea);
                mb.TryGetEntity(s, out EcsEntity eb);
                eb.Id.Should().Be(ea.Id, $"{because}: 0x{s:X8} the same entities, created in the same order");
                eb.Tags.Has<EquipmentDirty>().Should().Be(ea.Tags.Has<EquipmentDirty>(), $"{because}: 0x{s:X8} equipment dirty");
                eb.Tags.Has<OnGround>().Should().Be(ea.Tags.Has<OnGround>(), because);
            }

            foreach (uint s in new[] { MOB, OTHER, DEAD })
            {
                Serials(mb.GetEquipment(s)).Should().Equal(Serials(ma.GetEquipment(s)), $"{because}: 0x{s:X8}'s equipment index, in order");
                (uint[] sa, byte[] ca) = Slots(a, s);
                (uint[] sb, byte[] cb) = Slots(b, s);
                sb.Should().Equal(sa, $"{because}: 0x{s:X8}'s WornLayers serials");
                cb.Should().Equal(ca, $"{because}: 0x{s:X8}'s WornLayers counts");

                for (byte l = 0; l <= 0x1E; l++)
                {
                    mb.Worn(s, l).Should().Be(ma.Worn(s, l));
                    mb.WornCount(s, l).Should().Be(ma.WornCount(s, l));
                }

                // the version: the reference's changes at every slot change; ReplaceWornItems' changes for its wearer exactly
                // when the serials or counts changed (never for an identical result), and for any other mobile as the reference's
                if (!bb.Versions.TryGetValue(s, out ulong vb0) || !ba.Versions.TryGetValue(s, out ulong va0) || !Slots(b, s).Serials.Any())
                {
                    continue;
                }

                bool changedB = Version(b, s) != vb0, changedA = Version(a, s) != va0;
                bool slotsChanged = !bb.Slots[s].Serials.SequenceEqual(sb) || !bb.Slots[s].Counts.SequenceEqual(cb);

                if (s == owner)
                {
                    changedB.Should().Be(slotsChanged, $"{because}: 0x{s:X8}'s version changes iff its slots changed");
                    (changedB && !changedA).Should().BeFalse($"{because}: no version change the reference did not make");
                }
                else
                {
                    changedB.Should().Be(changedA, $"{because}: 0x{s:X8}'s version (not the batch's wearer)");
                }
            }

            // the writes: never more than the reference's
            (mb.WornWrites - bb.Writes).Should().BeLessOrEqualTo(ma.WornWrites - ba.Writes, because);

            b.Writer.InScope.Should().BeFalse();
            b.Ecs.IsFolding.Should().BeFalse();
        }

        // every layer's read (the WornCache) equals the first child on it computed afresh from the mirror's indexes
        private static void AssertReads(World w, string because)
        {
            foreach (Mobile m in w.Mobiles.Values)
            {
                for (int l = 1; l <= 0x1D; l++)
                {
                    w.State.WornItem(m, (Layer)l).Should().BeSameAs(WornLayersTests.ListFind(m, (Layer)l), $"{because}: 0x{m.Serial:X8} layer 0x{l:X2}");
                }
            }
        }

        private static void Warm(World w)
        {
            AssertReads(w, "before");
        }

        private static void Run(World a, World b, WornEntry[] list, bool read, string because)
        {
            uint owner = a.Mobiles.Get(MOB) != null ? MOB : DEAD;
            Entity oa = a.Get(owner), ob = b.Get(owner);
            Before ba = Snapshot(a), bb = Snapshot(b);

            Reference(a, oa, list);
            b.Writer.ReplaceWornItems(ob, list);

            AssertSame(a, b, ba, bb, owner, because);

            // one write-back when the wearer's slots changed, none when they did not; nothing batched for another wearer
            long writes = b.Ecs.Mirror.WornBatchWrites - bb.BatchWrites, unchanged = b.Ecs.Mirror.WornBatchUnchanged - bb.BatchUnchanged;
            (writes + unchanged).Should().BeLessOrEqualTo(1, because);

            if (read)
            {
                // the reads (WornCache) as the reference's, and the list's where the reference's are (a record over the wearer's
                // serial leaves the reference's ECS reads empty while its list is not: the T3a path's own behaviour, kept)
                Reads(b).Should().Equal(Reads(a), because + ": the ECS reads");

                if (Reads(a).SequenceEqual(ListReads(a)))
                {
                    AssertReads(b, because);
                }
            }
        }

        private static List<uint> Reads(World w) =>
            w.Mobiles.Values.OrderBy(m => m.Serial).SelectMany(m => Enumerable.Range(1, 0x1D).Select(l => w.State.WornItem(m, (Layer)l)?.Serial ?? 0)).ToList();

        private static List<uint> ListReads(World w) =>
            w.Mobiles.Values.OrderBy(m => m.Serial).SelectMany(m => Enumerable.Range(1, 0x1D).Select(l => WornLayersTests.ListFind(m, (Layer)l)?.Serial ?? 0)).ToList();

        [Theory]
        [MemberData(nameof(Cases))]
        public void ReplaceWornItems_Equals_The_T3a_Path(int packet, bool dead, bool read)
        {
            World a = Build(dead), b = Build(dead);

            if (read)
            {
                Warm(a);
                Warm(b);
            }

            (string name, WornEntry[] list) = Packets[packet];
            Run(a, b, list, read, name);
            Run(a, b, list, read, name + " (again)");
            Run(a, b, Outfit, read, name + ", then the outfit");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReplaceWornItems_Equals_The_T3a_Path_For_Every_Packet_In_A_Row(bool dead)
        {
            World a = Build(dead), b = Build(dead);
            Warm(a);
            Warm(b);

            for (int pass = 0; pass < 2; pass++)
            {
                // the wearer's serial as an entry replaces its record: last
                for (int p = 0; p < Packets.Length; p++)
                {
                    Run(a, b, Packets[p].List, true, $"pass {pass}: {Packets[p].Name}");
                }
            }
        }

        [Fact]
        public void An_Identical_Outfit_Writes_Nothing_And_Keeps_The_Version()
        {
            World w = Build(false);
            Mobile m = w.Mobiles.Get(MOB);
            ulong v = Version(w, MOB);
            (uint[] s, byte[] c) = Slots(w, MOB);
            long writes = w.Ecs.Mirror.WornWrites, unchanged = w.Ecs.Mirror.WornBatchUnchanged;

            w.Writer.ReplaceWornItems(m, Outfit);

            Version(w, MOB).Should().Be(v);
            Slots(w, MOB).Serials.Should().Equal(s);
            Slots(w, MOB).Counts.Should().Equal(c);
            w.Ecs.Mirror.WornWrites.Should().Be(writes, "no slot write");
            w.Ecs.Mirror.WornBatchUnchanged.Should().Be(unchanged + 1);

            // the reference bumps it (the departures and arrivals): the change of semantics this branch makes
            ulong before = Version(w, MOB);
            Reference(w, m, Outfit);
            Version(w, MOB).Should().NotBe(before);
        }

        [Fact]
        public void A_Change_Writes_Once_With_One_Version()
        {
            World w = Build(false);
            Mobile m = w.Mobiles.Get(MOB);
            long writes = w.Ecs.Mirror.WornWrites, batch = w.Ecs.Mirror.WornBatchWrites;

            w.Writer.ReplaceWornItems(m, Replaced(W2, E(N1, 0x1539, Layer.Pants)));

            w.Ecs.Mirror.WornWrites.Should().Be(writes + 1, "one slot write for the whole packet");
            w.Ecs.Mirror.WornBatchWrites.Should().Be(batch + 1);
            Version(w, MOB).Should().Be((ulong)w.Ecs.Mirror.WornWrites);
            w.Ecs.Mirror.Worn(MOB, (byte)Layer.Pants).Should().Be(N1);
        }

        [Fact]
        public void A_Bare_Owner_With_An_Empty_List_Loads_Nothing()
        {
            World w = Build(false);
            Mobile other = w.Mobiles.Get(OTHER);
            w.Writer.ReplaceWornItems(other, Array.Empty<WornEntry>());
            Mobile bare = w.GetOrCreateMobile(0x0000_3100);
            w.Ecs.Sync(bare);
            long writes = w.Ecs.Mirror.WornWrites, batch = w.Ecs.Mirror.WornBatchWrites, unchanged = w.Ecs.Mirror.WornBatchUnchanged;

            w.Writer.ReplaceWornItems(bare, Array.Empty<WornEntry>());
            w.Writer.ReplaceWornItems(null, Outfit);

            w.Ecs.Mirror.WornWrites.Should().Be(writes);
            w.Ecs.Mirror.WornBatchWrites.Should().Be(batch);
            w.Ecs.Mirror.WornBatchUnchanged.Should().Be(unchanged);
        }

        // an item as the owner (0x78 for an item serial): its children are contents, not worn; nothing is batched
        [Fact]
        public void An_Item_Owner_Equals_The_T3a_Path()
        {
            World a = Build(false), b = Build(false);
            Before ba = Snapshot(a), bb = Snapshot(b);
            WornEntry[] list = { E(N1, 0x1539, Layer.Pants), E(INBAG, 0x1F03, Layer.Robe), E(W1, 0x1515, Layer.Shirt) };

            Reference(a, a.Get(BAG), list);
            b.Writer.ReplaceWornItems(b.Get(BAG), list);

            AssertSame(a, b, ba, bb, BAG, "an item owner");
            b.Ecs.Mirror.WornBatchWrites.Should().Be(bb.BatchWrites);
            b.Ecs.Mirror.WornBatchUnchanged.Should().Be(bb.BatchUnchanged);
        }

        // more than 255 attached items on one layer: the count saturates and each departure rescans (Codex audit P2 #3)
        [Theory]
        [InlineData(254)]
        [InlineData(300)]
        public void A_Saturated_Layer_Equals_The_T3a_Path(int extra)
        {
            World a = Build(false), b = Build(false);

            foreach (World w in new[] { a, b })
            {
                Mobile m = w.Mobiles.Get(MOB);

                for (int i = 0; i < extra; i++)
                {
                    Wear(w, m, 0x4001_0000 + (uint)i, 0x1515, Layer.Shirt);
                }
            }

            a.Ecs.Mirror.WornCount(MOB, (byte)Layer.Shirt).Should().Be(Math.Min(extra + 1, 255));
            long rescans = b.Ecs.Mirror.WornRescans;

            WornEntry[] two = With(E(N3, 0x1517, Layer.Shirt));
            Run(a, b, two, false, "the saturated layer cleared, two on it again");
            Run(a, b, Outfit, false, "the outfit");

            if (extra >= 255)
            {
                b.Ecs.Mirror.WornRescans.Should().BeGreaterThan(rescans, "the saturated layer's departure rescanned");
            }
        }

        // a violation inside the packet (an owner World does not hold: WearItem rejects it) ends the batch: what the clear
        // changed is written back, and the next operation on the wearer is not batched
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_Rejected_Entry_Ends_The_Batch(bool throwOnViolation)
        {
            World a = Build(false), b = Build(false);
            a.Writer.ThrowOnViolation = throwOnViolation;
            b.Writer.ThrowOnViolation = throwOnViolation;
            Before ba = Snapshot(a), bb = Snapshot(b);

            // a detached mobile under MOB's serial: ClearWornItems clears the held MOB's children, then each WearItem rejects
            Mobile stranger = Mobile.Create(a, MOB), strangerB = Mobile.Create(b, MOB);

            Action ra = () => Reference(a, stranger, Outfit);
            Action rb = () => b.Writer.ReplaceWornItems(strangerB, Outfit);

            if (throwOnViolation)
            {
                ra.Should().Throw<ArgumentException>();
                rb.Should().Throw<ArgumentException>();
            }
            else
            {
                ra();
                rb();
            }

            AssertSame(a, b, ba, bb, MOB, "a rejected entry");

            // not batched any more: a 0x2E on the wearer writes its component at once
            b.Writer.Equip(N2, 0x1408, Layer.Helmet, MOB, 0, out _, out _);
            Slots(b, MOB).Serials[(int)Layer.Helmet - 1].Should().Be(N2);
        }

        // ReplaceWornItems inside an open operation is a nesting violation (rejected before anything changes)
        [Fact]
        public void ReplaceWornItems_Is_An_Operation()
        {
            World w = Build(false);
            w.Writer.ThrowOnViolation = false;
            Mobile m = w.Mobiles.Get(MOB);
            int v = w.Writer.Violations;
            w.Ecs.BeginFold(m);
            w.Writer.ReplaceWornItems(m, Outfit);
            w.Ecs.EndFold(m);
            w.Writer.Violations.Should().Be(v + 1, "one rejection for the packet, not one per sub-operation");
            Children(m).Should().Contain(W1, "nothing was cleared");
        }
    
        // the mirror's batch itself: reads during a batch see the batch's copy; a second batch ends the first; Clear drops it
        [Fact]
        public void The_Batch_Is_Read_Ended_And_Dropped()
        {
            World w = Build(false);
            WorldMirror mirror = w.Ecs.Mirror;
            byte shirt = (byte)Layer.Shirt;

            mirror.BeginWornBatch(MOB);
            mirror.Remove(W1).Should().BeTrue();
            mirror.Worn(MOB, shirt).Should().Be(0, "the batch's copy");
            mirror.WornCount(MOB, shirt).Should().Be(0);
            Slots(w, MOB).Serials[shirt - 1].Should().Be(W1, "not written back yet");

            // a batch on another wearer ends this one (written back)
            mirror.BeginWornBatch(OTHER);
            Slots(w, MOB).Serials[shirt - 1].Should().Be(0);
            Slots(w, MOB).Counts[shirt - 1].Should().Be(0);
            mirror.Remove(O1).Should().BeTrue();
            Slots(w, OTHER).Serials[(int)Layer.Tunic - 1].Should().Be(O1);
            mirror.EndWornBatch();
            Slots(w, OTHER).Serials[(int)Layer.Tunic - 1].Should().Be(0);

            // Clear drops an open batch: ending it afterwards writes nothing (no record is left)
            mirror.BeginWornBatch(MOB);
            mirror.Remove(W2).Should().BeTrue();
            long writes = mirror.WornBatchWrites;
            mirror.Clear();
            mirror.EndWornBatch();
            mirror.WornBatchWrites.Should().Be(writes);
            mirror.Worn(MOB, (byte)Layer.Pants).Should().Be(0);
        }

        // the wearer's own record removed during its batch: the batch goes with it (nothing written to a deleted record, and
        // later leaves and enters on the serial are not batched)
        [Fact]
        public void The_Wearers_Removal_Ends_Its_Batch()
        {
            World w = Build(false);
            WorldMirror mirror = w.Ecs.Mirror;

            mirror.BeginWornBatch(MOB);
            mirror.Remove(W1).Should().BeTrue();
            mirror.Remove(MOB).Should().BeTrue();
            mirror.Worn(MOB, (byte)Layer.Pants).Should().Be(0, "no record, no batch");
            mirror.WornCount(MOB, (byte)Layer.Pants).Should().Be(0);
            long writes = mirror.WornBatchWrites;
            mirror.EndWornBatch();
            mirror.WornBatchWrites.Should().Be(writes);
        }
}
}
