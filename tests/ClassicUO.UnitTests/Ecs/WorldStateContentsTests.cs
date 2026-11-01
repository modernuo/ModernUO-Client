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
    // T2 (ecs-transition-plan.md): WorldState.Contents(parent), a parent's attached children in child order from the
    // mirror's per-parent cached span. Each parent is read first (so its contents are cached), then changed; every read must
    // equal the World child list (the children World holds: the spellbook's detached entries are not world objects).
    public class WorldStateContentsTests
    {
        private const uint MOB = 0x0000_3900, VENDOR = 0x0000_3901, PACK = 0x4000_7900, A = 0x4000_7901, B = 0x4000_7902, C = 0x4000_7903, SHIRT = 0x4000_7904, HELM = 0x4000_7905, CORPSE = 0x4000_7906, LOOT = 0x4000_7907, WEAR = 0x4000_790A;
        private const uint NEW = 0x4000_7940, BAG = 0x4000_7941, SHOP = 0x4000_7942, S1 = 0x4000_7943, S2 = 0x4000_7944, S3 = 0x4000_7945, BOOK = 0x4000_7946, INBOOK = 0x4000_7947, LATE = 0x0000_3940, ORPHAN = 0x4000_7948;

        internal static World Build()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();

            // a bag in the pack with an item, a vendor's shop container, a spellbook with an item the server put in it
            world.Writer.PlaceInContainer(BAG, 0xE76, 1, 40, 40, 0, PACK, out _);
            world.Writer.PlaceInContainer(NEW, 0xF3F, 10, 5, 5, 0, BAG, out _);
            world.Writer.Equip(SHOP, 0x2AF8, Layer.ShopBuy, VENDOR, 0, out _, out _);
            world.Writer.PlaceInContainer(S1, 0xEED, 1, 30, 10, 0, SHOP, out _);
            world.Writer.PlaceInContainer(S2, 0xEED, 1, 10, 10, 0, SHOP, out _);
            world.Writer.PlaceInContainer(S3, 0xEED, 1, 20, 10, 0, SHOP, out _);
            Item book = world.Writer.UpsertWorldItem(BOOK, 0xEFA, 0, 1, 1004, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);
            world.Writer.PlaceInContainer(INBOOK, 0x1F2E, 1, 0, 0, 0, BOOK, out _);

            return world;
        }

        internal static List<uint> HeldChildren(World world, uint parent)
        {
            var list = new List<uint>();
            Entity e = world.Get(parent);

            for (LinkedObject i = e?.Items; i != null; i = i.Next)
            {
                if (i is Item it && ReferenceEquals(world.Items.Get(it.Serial), it))
                {
                    list.Add(it.Serial);
                }
            }

            return list;
        }

        internal static IEnumerable<uint> Parents(World world) =>
            world.Items.Values.Select(i => i.Serial).Concat(world.Mobiles.Values.Select(m => m.Serial)).ToList();

        // every parent's contents equal its held child list (read twice: the second from the cache)
        internal static void AllMatch(World world)
        {
            foreach (uint parent in Parents(world))
            {
                world.State.Contents(parent).ToArray().Should().Equal(HeldChildren(world, parent), $"contents of 0x{parent:X8}");
                world.State.Contents(parent).ToArray().Should().Equal(HeldChildren(world, parent), $"cached contents of 0x{parent:X8}");
            }
        }

        [Fact]
        public void The_Contents_Are_The_Child_Lists_In_Order()
        {
            World world = Build();

            world.State.Contents(PACK).ToArray().Should().Equal(B, C, A, BAG);
            world.State.Contents(MOB).ToArray().Should().Equal(HELM, SHIRT, WEAR, 0x4000_7909u);
            world.State.Contents(CORPSE).ToArray().Should().Equal(LOOT, 0x4000_790Bu);
            world.State.Contents(BAG).ToArray().Should().Equal(NEW);
            world.State.Contents(NEW).ToArray().Should().BeEmpty();
            world.State.Contents(0x4000_7FFF).ToArray().Should().BeEmpty("a serial without a record has no contents");
            AllMatch(world);
        }

        [Fact]
        public void A_Cached_Read_Builds_Nothing_And_Allocates_Nothing()
        {
            World world = Build();
            WorldMirror mirror = world.Ecs.Mirror;
            world.State.Contents(PACK);
            long builds = mirror.ContentsBuilds;

            long before = GC.GetAllocatedBytesForCurrentThread();
            int sum = 0;

            for (int i = 0; i < 1000; i++)
            {
                foreach (uint s in world.State.Contents(PACK))
                {
                    sum += (int)(s & 0xFF);
                }
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            allocated.Should().Be(0);
            mirror.ContentsBuilds.Should().Be(builds);
            sum.Should().BeGreaterThan(0);
        }

        [Fact]
        public void A_Record_Removed_Directly_Leaves_Its_Cached_Parent()
        {
            // the funnel detaches before it removes, so this is the mirror's own guarantee (mutation round)
            World world = Build();
            world.State.Contents(PACK).ToArray().Should().Contain(A);

            world.Ecs.Mirror.Remove(A).Should().BeTrue();
            world.Ecs.Mirror.Contents(PACK).ToArray().Should().NotContain(A);
        }

        [Fact]
        public void A_Removed_Parent_Releases_Its_Cached_Slot()
        {
            World world = Build();
            world.State.Contents(BAG);
            world.Ecs.Mirror.CachedContentsParents.Should().Be(1);

            world.Writer.RemoveWorldItem(world.Items.Get(BAG), out _);
            world.Ecs.Mirror.CachedContentsParents.Should().Be(0);
        }

        [Fact]
        public void Nothing_Is_Tracked_Before_The_First_Read()
        {
            World world = Build();

            world.Ecs.Mirror.CachedContentsParents.Should().Be(0);
            world.Ecs.Mirror.ContentsInvalidations.Should().Be(0);
        }

        [Fact]
        public void A_Change_Elsewhere_Or_Of_Another_Field_Keeps_The_Cache()
        {
            World world = Build();
            WorldMirror mirror = world.Ecs.Mirror;
            world.State.Contents(PACK);
            world.State.Contents(CORPSE);
            long builds = mirror.ContentsBuilds;

            // another parent's contents, and fields that are not the attachment (name, price, a worn item's re-equip elsewhere)
            world.Writer.PlaceInContainer(0x4000_7950, 0xEED, 1, 1, 1, 0, CORPSE, out _);
            world.Writer.SetName(world.Items.Get(A), "renamed");
            world.Writer.SetPrice(world.Items.Get(B), 7);

            world.State.Contents(PACK).ToArray().Should().Equal(B, C, A, BAG);
            mirror.ContentsBuilds.Should().Be(builds, "the pack did not change");
            world.State.Contents(CORPSE).ToArray().Should().Equal(LOOT, 0x4000_790Bu, 0x4000_7950u);
            mirror.ContentsBuilds.Should().Be(builds + 1);
        }

        public enum Change
        {
            Push, RePush, NewChild, MoveToOtherContainer, MoveToGround, Remove, RemoveContainer, Equip, ReEquip, Unequip, OrphanThenAttach,
            SpellbookClear, VendorSort, ClearContainer, ClearCorpseKeepEquipped, Rekey, OldRekey, RemoveMobile, MirrorClear, RemoveMiddleThenAdd
        }

        [Theory]
        [InlineData(Change.Push)]
        [InlineData(Change.RePush)]
        [InlineData(Change.NewChild)]
        [InlineData(Change.MoveToOtherContainer)]
        [InlineData(Change.MoveToGround)]
        [InlineData(Change.Remove)]
        [InlineData(Change.RemoveContainer)]
        [InlineData(Change.Equip)]
        [InlineData(Change.ReEquip)]
        [InlineData(Change.Unequip)]
        [InlineData(Change.OrphanThenAttach)]
        [InlineData(Change.SpellbookClear)]
        [InlineData(Change.VendorSort)]
        [InlineData(Change.ClearContainer)]
        [InlineData(Change.ClearCorpseKeepEquipped)]
        [InlineData(Change.Rekey)]
        [InlineData(Change.OldRekey)]
        [InlineData(Change.RemoveMobile)]
        [InlineData(Change.MirrorClear)]
        [InlineData(Change.RemoveMiddleThenAdd)]
        public void Every_Change_Of_Attachment_Reaches_A_Cached_Parent(Change change)
        {
            World world = Build();
            AllMatch(world);

            Apply(world, change);

            if (change == Change.MirrorClear)
            {
                foreach (uint parent in Parents(world))
                {
                    world.State.Contents(parent).ToArray().Should().BeEmpty();
                }

                world.Ecs.Mirror.CachedContentsParents.Should().Be(Parents(world).Count());

                return;
            }

            AllMatch(world);
        }

        internal static void Apply(World world, Change change)
        {
            switch (change)
            {
                case Change.Push:
                    world.Writer.PlaceInContainer(0x4000_7951, 0xEED, 1, 1, 1, 0, PACK, out _);

                    break;

                case Change.RePush:
                    world.Writer.PlaceInContainer(B, 0xEEE, 1, 21, 21, 0, PACK, out _);
                    world.State.Contents(PACK).ToArray().Should().Equal(C, A, BAG, B);

                    break;

                case Change.NewChild:
                    world.Writer.PlaceInContainer(0x4000_7952, 0xEED, 1, 1, 1, 0, BAG, out _);

                    break;

                case Change.MoveToOtherContainer:
                    world.Writer.PlaceInContainer(C, 0xEEF, 1, 3, 3, 0, BAG, out _);

                    break;

                case Change.MoveToGround:
                {
                    Item a = world.Items.Get(A);
                    world.Writer.UpsertWorldItem(A, a.Graphic, 0, a.Amount, 1005, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement p);
                    world.Writer.PlaceWorldItem(p);

                    break;
                }

                case Change.Remove:
                    world.Writer.RemoveWorldItem(world.Items.Get(C), out _);

                    break;

                case Change.RemoveContainer:
                    world.Writer.RemoveWorldItem(world.Items.Get(BAG), out _);

                    break;

                case Change.Equip:
                    world.Writer.Equip(0x4000_7953, 0x1F7B, Layer.Cloak, MOB, 0, out _, out _);

                    break;

                case Change.ReEquip:
                    world.Writer.Equip(HELM, 0x1408, Layer.Helmet, MOB, 0, out _, out _);

                    break;

                case Change.Unequip:
                    world.Writer.PlaceInContainer(SHIRT, 0x1517, 1, 2, 2, 0, PACK, out _);

                    break;

                case Change.OrphanThenAttach:
                    world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out _, out _);
                    world.GetOrCreateMobile(LATE);
                    world.State.Contents(LATE).ToArray().Should().BeEmpty("an orphan is not attached");
                    world.Writer.Equip(ORPHAN, 0x1517, Layer.Shirt, LATE, 0, out _, out _);
                    world.State.Contents(LATE).ToArray().Should().Equal(ORPHAN);

                    break;

                case Change.SpellbookClear:
                    world.Writer.FillSpellbook(BOOK, 0xEFA, 0b101, 0);
                    world.State.Contents(BOOK).ToArray().Should().BeEmpty("the clear detaches the held child; the spell entries are not world objects");

                    break;

                case Change.VendorSort:
                    world.Writer.SortShopContents(world.Items.Get(SHOP));
                    world.State.Contents(SHOP).ToArray().Should().Equal(S2, S3, S1);

                    break;

                case Change.ClearContainer:
                    world.Writer.ClearContainer(PACK, false, true);
                    world.State.Contents(PACK).ToArray().Should().BeEmpty();

                    break;

                case Change.ClearCorpseKeepEquipped:
                    world.Writer.ClearContainer(CORPSE, true, false);
                    world.State.Contents(CORPSE).ToArray().Should().Equal(0x4000_790Bu);

                    break;

                case Change.Rekey:
                {
                    uint dead = world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB));
                    world.State.Contents(dead).ToArray().Should().Equal(HELM, SHIRT, WEAR, 0x4000_7909u);
                    world.State.Contents(MOB).ToArray().Should().BeEmpty();

                    break;
                }

                case Change.OldRekey:
                    WorldWriterMobileTests.OldRekey(world, world.Mobiles.Get(MOB));

                    break;

                case Change.RemoveMobile:
                    world.Writer.RemoveMobile(world.Mobiles.Get(MOB));

                    break;

                case Change.MirrorClear:
                    world.Ecs.Mirror.Clear();

                    break;

                case Change.RemoveMiddleThenAdd:
                    // Friflo's index can reorder on a remove: the order is the stamps'
                    world.Writer.RemoveWorldItem(world.Items.Get(C), out _);
                    world.Writer.PlaceInContainer(0x4000_7954, 0xEED, 1, 1, 1, 0, PACK, out _);
                    world.Writer.PlaceInContainer(0x4000_7955, 0xEED, 1, 1, 1, 0, PACK, out _);
                    world.State.Contents(PACK).ToArray().Should().Equal(B, A, BAG, 0x4000_7954u, 0x4000_7955u);

                    break;
            }
        }

        [Fact]
        public void The_Spellbook_Set_Is_Kept_By_The_Mirror()
        {
            World world = Build();
            WorldMirror mirror = world.Ecs.Mirror;

            mirror.Spellbook(BOOK).Should().Be(0UL);
            mirror.SetSpellbook(BOOK, 0x8000_0000_0000_0005UL).Should().BeTrue();
            mirror.Spellbook(BOOK).Should().Be(0x8000_0000_0000_0005UL);
            mirror.SetSpellbook(BOOK, 0).Should().BeTrue();
            mirror.Spellbook(BOOK).Should().Be(0UL);
            mirror.SetSpellbook(0x4000_7FFF, 1).Should().BeFalse("no record");
            mirror.Spellbook(0x4000_7FFF).Should().Be(0UL);
        }

        [Fact]
        public void The_Cache_Memory_Estimate_Counts_Each_Cached_Parent()
        {
            World world = Build();
            long empty = world.Ecs.Mirror.ContentsCacheBytes;
            AllMatch(world);

            world.Ecs.Mirror.ContentsCacheBytes.Should().BeGreaterThan(empty);
            world.Ecs.Mirror.CachedContentsParents.Should().Be(Parents(world).Count());
        }
    }
}
