using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T1 funnel part 9 (ecs-transition-plan.md, T1 Checkers): -ecscheck v2. The funnel commits Name, Hits/HitsMax,
    // NotorietyFlag, every item's Layer, Price and the child order (a stamp per PushToBack) to the mirror; the check compares
    // them with World. A world built through the funnel checks clean; each deliberate divergence is reported.
    public class EcsCheckV2Tests
    {
        private const uint MOB = 0x0000_3900, VENDOR = 0x0000_3901, STAT = 0x0000_3902, PACK = 0x4000_7900, A = 0x4000_7901, B = 0x4000_7902, C = 0x4000_7903, SHIRT = 0x4000_7904, HELM = 0x4000_7905, CORPSE = 0x4000_7906, LOOT = 0x4000_7907, RET = 0x4000_7909, WEAR = 0x4000_790A, CLOOT = 0x4000_790B;

        // a world built only through funnel operations (and the creating Syncs of GetOrCreate*)
        private static World BuildFunnelWorld()
        {
            Time.Ticks = 100_000;
            var world = new World();
            world.Writer.ThrowOnViolation = true;

            Mobile m = world.GetOrCreateMobile(MOB);
            world.Writer.SetNotoriety(m, NotorietyFlag.Murderer);
            world.Writer.SetStatus(m, "Moe", 40, 50);
            world.Writer.SetHits(m, 60, 55);
            world.Writer.SetName(m, "Moe the Brave");

            Mobile vendor = world.GetOrCreateMobile(VENDOR);
            world.Writer.SetNotoriety(vendor, NotorietyFlag.Invulnerable);
            world.Writer.SetName(vendor, "Bob");
            world.Writer.SetName(vendor, null);
            world.Writer.SetHits(vendor, 0, 0);

            // a mobile only 0x11 (SetStatus) wrote
            world.Writer.SetStatus(world.GetOrCreateMobile(STAT), "Stan", 9, 12);

            // a pack on the ground, three items in it, the first re-pushed to the back
            Item pack = world.Writer.UpsertWorldItem(PACK, 0xE75, 0, 1, 1001, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(A, 0xEED, 5, 10, 10, 0, PACK, out _);
            world.Writer.PlaceInContainer(B, 0xEEE, 1, 20, 20, 0, PACK, out _);
            world.Writer.PlaceInContainer(C, 0xEEF, 1, 30, 30, 0, PACK, out _);
            world.Writer.PlaceInContainer(A, 0xEED, 6, 11, 11, 0, PACK, out _);
            world.Writer.SetName(world.Items.Get(B), "a gem");
            world.Writer.SetPrice(world.Items.Get(C), 120);
            world.Writer.SetHits(world.Items.Get(C), 10, 7);

            // worn items, a second wear moving one to the back, a 0x78 wear, a held item returned to the paperdoll
            world.Writer.Equip(RET, 0x13C6, Layer.Gloves, MOB, 0, out _, out _);
            world.Writer.Equip(SHIRT, 0x1517, Layer.Shirt, MOB, 0, out _, out _);
            world.Writer.Equip(HELM, 0x1408, Layer.Helmet, MOB, 0, out _, out _);
            world.Writer.Equip(SHIRT, 0x1517, Layer.Shirt, MOB, 0x21, out _, out _);
            world.Writer.WearItem(m, WEAR, 0x1F03, Layer.Robe, 0);
            world.Writer.ReturnHeldItem(RET, 0x13C6, 0, 1, Flags.None, Layer.Gloves, 0, 0, 0, MOB, out _, out WorldWriter.HeldItemReturn returned);
            returned.Should().Be(WorldWriter.HeldItemReturn.Paperdoll);

            // a corpse with an item laid in it
            Item corpse = world.Writer.UpsertWorldItem(CORPSE, 0x2006, 0, 0x190, 1002, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement cp);
            world.Writer.PlaceWorldItem(cp);
            world.Writer.PlaceInContainer(LOOT, 0x1F14, 1, 5, 5, 0, CORPSE, out _);
            world.Writer.PlaceCorpseEquipment(corpse, CLOOT, Layer.Pants);

            return world;
        }

        private static List<string> Check(World world)
        {
            var reported = new List<string>();
            world.Ecs.MismatchObserver = reported.Add;
            world.Ecs.RunCheck(false);
            world.Ecs.MismatchObserver = null;

            return reported;
        }

        [Fact]
        public void A_World_Built_Through_The_Funnel_Checks_Clean_And_Its_Mirror_Has_The_V2_Fields()
        {
            World world = BuildFunnelWorld();

            Check(world).Should().BeEmpty();

            world.Ecs.Mirror.TryGetExtras(MOB, out MirrorExtras m).Should().BeTrue();
            (m.Name, m.Hits, m.HitsMax, m.Notoriety).Should().Be(("Moe the Brave", (ushort)55, (ushort)60, (byte)NotorietyFlag.Murderer));
            world.Ecs.Mirror.TryGetExtras(STAT, out MirrorExtras st).Should().BeTrue();
            (st.Name, st.Hits, st.HitsMax).Should().Be(("Stan", (ushort)9, (ushort)12));
            world.Ecs.Mirror.TryGetExtras(C, out MirrorExtras c).Should().BeTrue();
            (c.Price, c.Hits, c.HitsMax).Should().Be((120u, (ushort)7, (ushort)10));
            world.Ecs.Mirror.TryGetExtras(HELM, out MirrorExtras helm).Should().BeTrue();
            helm.ItemLayer.Should().Be((byte)Layer.Helmet);

            // the child order: the re-pushed item is last, in the list and by stamp
            Children(world, PACK).Should().Equal(B, C, A);
            ByStamp(world, PACK).Should().Equal(B, C, A);
            Children(world, MOB).Should().Equal(HELM, SHIRT, WEAR, RET);
            ByStamp(world, MOB).Should().Equal(HELM, SHIRT, WEAR, RET);
            Children(world, CORPSE).Should().Equal(LOOT, CLOOT);
            ByStamp(world, CORPSE).Should().Equal(LOOT, CLOOT);
        }

        [Fact]
        public void The_Death_Rekey_Carries_The_V2_Fields_And_The_Child_Order()
        {
            World world = BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);
            uint dead = world.Writer.RekeyDeadMobile(m);

            Check(world).Should().BeEmpty();
            world.Ecs.Mirror.TryGetExtras(dead, out MirrorExtras x).Should().BeTrue();
            (x.Name, x.Notoriety).Should().Be(("Moe the Brave", (byte)NotorietyFlag.Murderer));
            ByStamp(world, dead).Should().Equal(HELM, SHIRT, WEAR, RET);
        }

        [Fact]
        public void The_Old_Rekey_Carries_Them_Too()
        {
            World world = BuildFunnelWorld();
            WorldWriterMobileTests.OldRekey(world, world.Mobiles.Get(MOB));

            Check(world).Should().BeEmpty();
        }

        public enum Divergence { ItemName, MobileName, ItemHits, MobileHitsMax, Notoriety, ItemLayer, WornLayer, Price, ChildOrder, MissingChild, RemovedChild }

        [Theory]
        [InlineData(Divergence.ItemName, "item 0x40007902 v2")]
        [InlineData(Divergence.MobileName, "mobile 0x00003900 v2")]
        [InlineData(Divergence.ItemHits, "item 0x40007903 v2")]
        [InlineData(Divergence.MobileHitsMax, "mobile 0x00003900 v2")]
        [InlineData(Divergence.Notoriety, "mobile 0x00003901 v2")]
        [InlineData(Divergence.ItemLayer, "item 0x40007901 v2")]
        [InlineData(Divergence.WornLayer, "item 0x40007905")]
        [InlineData(Divergence.Price, "item 0x40007903 v2")]
        [InlineData(Divergence.ChildOrder, "children of 0x40007900")]
        [InlineData(Divergence.MissingChild, "children of 0x40007900")]
        [InlineData(Divergence.RemovedChild, "children of 0x00003900")]
        public void A_Deliberate_Mismatch_Is_Reported(Divergence d, string expected)
        {
            World world = BuildFunnelWorld();
            Check(world).Should().BeEmpty();
            Item pack = world.Items.Get(PACK);

            // each a write that bypasses the funnel (or a mirror write World never made)
            switch (d)
            {
                case Divergence.ItemName: world.Items.Get(B).Name = "a stone"; break;
                case Divergence.MobileName: world.Mobiles.Get(MOB).Name = null; break;
                case Divergence.ItemHits: world.Items.Get(C).Hits = 3; break;
                case Divergence.MobileHitsMax: world.Mobiles.Get(MOB).HitsMax = 61; break;
                case Divergence.Notoriety: world.Mobiles.Get(VENDOR).NotorietyFlag = NotorietyFlag.Innocent; break;
                case Divergence.ItemLayer: world.Items.Get(A).Layer = Layer.Talisman; break;
                case Divergence.WornLayer: world.Items.Get(HELM).Layer = Layer.Earrings; break;
                case Divergence.Price: world.Ecs.Mirror.SetPrice(C, 99); break;

                case Divergence.ChildOrder:
                    // the list re-ordered without a stamp: B moved to the back
                    Item b = world.Items.Get(B);
                    pack.Remove(b);
                    pack.PushToBack(b);
                    break;

                case Divergence.MissingChild:
                    // an item linked into the pack that the mirror has elsewhere
                    Item stray = world.GetOrCreateItem(0x4000_7908);
                    pack.PushToBack(stray);
                    break;

                case Divergence.RemovedChild:
                    world.Mobiles.Get(MOB).Remove(world.Items.Get(HELM));
                    break;
            }

            List<string> reported = Check(world);
            reported.Should().NotBeEmpty();
            reported.Should().Contain(s => s.StartsWith(expected, StringComparison.Ordinal), $"the {d} divergence");
        }

        [Fact]
        public void The_Spellbooks_Detached_Spell_Items_Are_Not_Compared()
        {
            World world = BuildFunnelWorld();
            Item book = world.Items.Get(A);
            Item spell = Item.Create(world, 1);
            book.PushToBack(spell);

            Check(world).Should().BeEmpty();
        }

        private static List<uint> Children(World world, uint parent)
        {
            var list = new List<uint>();

            for (LinkedObject i = world.Get(parent).Items; i != null; i = i.Next)
            {
                list.Add(((Item)i).Serial);
            }

            return list;
        }

        private static List<uint> ByStamp(World world, uint parent)
        {
            WorldMirror mirror = world.Ecs.Mirror;

            return mirror.GetContents(parent).Concat(mirror.GetEquipment(parent))
                .Select(e => e.GetComponent<ClassicUO.Ecs.Serial>().Value)
                .OrderBy(s => { mirror.TryGetExtras(s, out MirrorExtras x); return x.ChildStamp; })
                .ToList();
        }
    }
}
