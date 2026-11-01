using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T2 step 3: the child-list readers behind ecs_read_contents. Each reader runs with the switch off (the World child list)
    // and on (the ECS through WorldState.Children) on the same world and must give the same result; on a world whose child
    // list was changed behind the funnel's back, the switch-on reader follows the ECS and the switch-off reader the list
    // (so a reader that ignores the switch fails). The gumps (ContainerGump, GridLootGump, TradingGump, SpellbookGump, ItemGump),
    // the shop handlers (0x24's 0x30, 0x74), Profile's gump save and GetTotalAmountOfItem need the UI or the tile data: they
    // walk the same Children/HasChildren, and the gate runs them with the switch on.
    public class WorldStateReaderTests
    {
        private const uint PACK = 0x4000_7A00, BAG = 0x4000_7A01, GOLD1 = 0x4000_7A02, GOLD2 = 0x4000_7A03, ARROWS = 0x4000_7A04, BAND = 0x4000_7A05,
            BOOK = 0x4000_7A06, INBOOK = 0x4000_7A07, TRADE = 0x4000_7A08, TRADED = 0x4000_7A09, CAP = 0x4000_7A0A, GOLD3 = 0x4000_7A0B;

        private const int ARROW_CLILOC = 1023902;

        internal static World Build(out PlayerMobile player)
        {
            World world = WorldWriterMobileTests.BuildPlayer(out WorldWriterMobileTests.TestPlayer p);
            player = p;
            world.Writer.ThrowOnViolation = true;

            world.Writer.Equip(PACK, 0xE75, Layer.Backpack, p.Serial, 0, out _, out _);
            world.Writer.Equip(CAP, 0x1715, Layer.Helmet, p.Serial, 0, out _, out _);
            world.Writer.PlaceInContainer(GOLD1, 0xEED, 100, 10, 10, 0, PACK, out _);
            world.Writer.PlaceInContainer(BAG, 0xE76, 1, 20, 20, 0, PACK, out _);
            world.Writer.PlaceInContainer(GOLD2, 0xEED, 50, 5, 5, 0x22, BAG, out _);
            world.Writer.PlaceInContainer(ARROWS, 0xF3F, 30, 6, 6, 0, BAG, out _);
            world.Writer.PlaceInContainer(GOLD3, 0xEED, 7, 7, 7, 0x22, BAG, out _);
            world.Writer.PlaceInContainer(BAND, 0xE21, 20, 30, 30, 0, PACK, out _);
            world.Writer.PlaceInContainer(BOOK, 0xEFA, 1, 40, 40, 0, PACK, out _);
            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b10011, 0);
            world.Writer.PlaceInContainer(INBOOK, 0x1F2E, 3, 0, 0, 0, BOOK, out _);
            world.Writer.PlaceInContainer(TRADE, 0x1E5E, 1, 0, 0, 0, p.Serial, out _);
            world.Writer.PlaceInContainer(TRADED, 0x9D0, 1, 0, 0, 0, TRADE, out _);

            // a re-push: the first gold to the back of the pack
            world.Writer.PlaceInContainer(GOLD1, 0xEED, 100, 11, 11, 0, PACK, out _);
            world.OPL.Add(ARROWS, 1, "arrows", "", ARROW_CLILOC);

            world.State.CheckEnabled = true;
            world.State.MismatchObserver = m => throw new InvalidOperationException(m);

            return world;
        }

        private static T Both<T>(World world, Func<T> reader)
        {
            world.State.ReadContentsOverride = false;
            T off = reader();
            world.State.ReadContentsOverride = true;
            T on = reader();
            world.State.ReadContentsOverride = null;
            on.Should().Be(off);

            return on;
        }

        private static List<object> Walk(World world, Entity parent)
        {
            var list = new List<object>();

            foreach (Item it in world.State.Children(parent))
            {
                list.Add(it);
            }

            return list;
        }

        [Fact]
        public void The_Switch_Is_Off_By_Default()
        {
            World world = Build(out _);

            ClassicUO.Configuration.Settings.GlobalSettings.EcsReadContents.Should().BeFalse();
            new ClassicUO.Configuration.Settings().EcsReadContents.Should().BeFalse();
            world.State.ReadContents.Should().BeFalse();
        }

        [Fact]
        public void Children_Are_The_Same_Objects_In_The_Same_Order_Both_Ways()
        {
            World world = Build(out PlayerMobile p);

            foreach (uint serial in WorldStateContentsTests.Parents(world))
            {
                Entity parent = world.Get(serial);
                world.State.ReadContentsOverride = false;
                List<object> off = Walk(world, parent);
                bool offAny = world.State.HasChildren(parent);
                world.State.ReadContentsOverride = true;
                List<object> on = Walk(world, parent);
                bool onAny = world.State.HasChildren(parent);

                on.Should().Equal(off, (a, b) => ReferenceEquals(a, b), $"children of 0x{serial:X8}");
                onAny.Should().Be(offAny);
            }

            // the book: its three spell entries (the legacy objects), then the scroll the server put in it
            world.State.ReadContentsOverride = true;
            Walk(world, world.Items.Get(BOOK)).Cast<Item>().Select(i => (i.Serial, i.Amount)).Should()
                .Equal(((uint)1, (ushort)1), ((uint)2, (ushort)2), ((uint)5, (ushort)5), (INBOOK, (ushort)3));
            Walk(world, world.Items.Get(PACK)).Cast<Item>().Select(i => i.Serial).Should().Equal(BAG, BAND, BOOK, GOLD1);
            Walk(world, p).Cast<Item>().Select(i => i.Serial).Should().Equal(PACK, CAP, TRADE);
            world.State.Children(null).IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void The_Readers_Agree_Both_Ways()
        {
            World world = Build(out PlayerMobile p);
            Item pack = world.Items.Get(PACK);

            // Entity.FindItem: the least hue of a graphic at any depth, or the last match of a hue (the counters, bandages)
            Both(world, () => p.FindItem(0xEED)?.Serial).Should().Be(GOLD1);
            Both(world, () => p.FindItem(0xEED, 0x22)?.Serial).Should().Be(GOLD3);
            Both(world, () => pack.FindItem(0xF3F)?.Serial).Should().Be(ARROWS);
            Both(world, () => p.FindItem(0x9D0)?.Serial).Should().Be(TRADED);
            // the legacy quirk kept: a spell entry is found as an item of graphic 0x1F2E (hue 0 is the least)
            Both(world, () => pack.FindItem(0x1F2E)?.Serial).Should().Be(1u);
            Both(world, () => pack.FindItem(0x1F2E, 0)?.Serial).Should().Be(INBOOK);
            Both(world, () => (p as PlayerMobile).FindBandage()?.Serial).Should().Be(BAND);

            // Entity.GetItemByGraphic (with the legacy deep search over the entity's own children)
            Both(world, () => pack.GetItemByGraphic(0xE21)?.Serial).Should().Be(BAND);
            Both(world, () => pack.GetItemByGraphic(0xF3F, true)?.Serial).Should().Be(ARROWS);
            Both(world, () => pack.GetItemByGraphic(0xF3F)?.Serial).Should().BeNull();

            // Item.GetTotalAmount: every depth (a spell entry is walked but not counted: Exists is World.Contains, and World does not hold it)
            Both(world, () => { int n = 0; pack.GetTotalAmount(0xEED, null, ref n); return n; }).Should().Be(157);
            Both(world, () => { int n = 0; pack.GetTotalAmount(0xEED, 0x22, ref n); return n; }).Should().Be(57);
            Both(world, () => { int n = 0; pack.GetTotalAmount(0x1F2E, null, ref n); return n; }).Should().Be(3);

            // Mobile.GetSecureTradeBox
            Both(world, () => p.GetSecureTradeBox()?.Serial).Should().Be(TRADE);

            // PlayerMobile.FindItemByGraphic / FindItemByCliloc (the macros)
            Both(world, () => p.FindItemByGraphic(0xF3F)?.Serial).Should().Be(ARROWS);
            Both(world, () => p.FindItemByGraphic(0x9D0)?.Serial).Should().BeNull("the trade box is not in the backpack");
            Both(world, () => p.FindItemByCliloc(ARROW_CLILOC)?.Serial).Should().Be(ARROWS);
            Both(world, () => p.FindPreferredItemByCliloc(new[] { 1, ARROW_CLILOC })?.Serial).Should().Be(ARROWS);

            // HasChildren (IsEmpty)
            Both(world, () => world.State.HasChildren(world.Items.Get(BAG))).Should().BeTrue();
            Both(world, () => world.State.HasChildren(world.Items.Get(ARROWS))).Should().BeFalse();
        }

        public enum Reader { FindItem, FindItemHue, GetItemByGraphic, GetTotalAmount, SecureTradeBox, FindItemByGraphic, FindItemByCliloc, HasChildren, SpellEntries, Order }

        // the child list changed behind the funnel's back: the switch-off reader sees the list, the switch-on reader the ECS
        [Theory]
        [InlineData(Reader.FindItem)]
        [InlineData(Reader.FindItemHue)]
        [InlineData(Reader.GetItemByGraphic)]
        [InlineData(Reader.GetTotalAmount)]
        [InlineData(Reader.SecureTradeBox)]
        [InlineData(Reader.FindItemByGraphic)]
        [InlineData(Reader.FindItemByCliloc)]
        [InlineData(Reader.HasChildren)]
        [InlineData(Reader.SpellEntries)]
        [InlineData(Reader.Order)]
        public void With_The_Switch_On_The_Readers_Follow_The_ECS(Reader reader)
        {
            World world = Build(out PlayerMobile p);
            world.State.CheckEnabled = false;
            Item pack = world.Items.Get(PACK), bag = world.Items.Get(BAG);
            object offExpected, onExpected;
            Func<object> read;

            switch (reader)
            {
                case Reader.FindItem:
                    pack.Remove(world.Items.Get(BAND));
                    read = () => p.FindItem(0xE21)?.Serial;
                    (offExpected, onExpected) = (null, BAND);

                    break;

                case Reader.FindItemHue:
                    bag.Remove(world.Items.Get(GOLD3));
                    read = () => p.FindItem(0xEED, 0x22)?.Serial;
                    (offExpected, onExpected) = (GOLD2, GOLD3);

                    break;

                case Reader.GetItemByGraphic:
                    pack.Remove(world.Items.Get(BAND));
                    read = () => pack.GetItemByGraphic(0xE21)?.Serial;
                    (offExpected, onExpected) = (null, BAND);

                    break;

                case Reader.GetTotalAmount:
                    bag.Remove(world.Items.Get(ARROWS));
                    read = () => { int n = 0; pack.GetTotalAmount(0xF3F, null, ref n); return n; };
                    (offExpected, onExpected) = (0, 30);

                    break;

                case Reader.SecureTradeBox:
                    p.Remove(world.Items.Get(TRADE));
                    read = () => p.GetSecureTradeBox()?.Serial;
                    (offExpected, onExpected) = (null, TRADE);

                    break;

                case Reader.FindItemByGraphic:
                    bag.Remove(world.Items.Get(ARROWS));
                    read = () => p.FindItemByGraphic(0xF3F)?.Serial;
                    (offExpected, onExpected) = (null, ARROWS);

                    break;

                case Reader.FindItemByCliloc:
                    bag.Remove(world.Items.Get(ARROWS));
                    read = () => p.FindItemByCliloc(ARROW_CLILOC)?.Serial;
                    (offExpected, onExpected) = (null, ARROWS);

                    break;

                case Reader.HasChildren:
                    bag.Clear();
                    read = () => world.State.HasChildren(bag);
                    (offExpected, onExpected) = (false, true);

                    break;

                case Reader.SpellEntries:
                    world.Items.Get(BOOK).Clear();
                    read = () => Walk(world, world.Items.Get(BOOK)).Count;
                    (offExpected, onExpected) = (0, 4);

                    break;

                default:
                    Item band = world.Items.Get(BAND);
                    pack.Remove(band);
                    pack.PushToBack(band);
                    read = () => string.Join(",", Walk(world, pack).Cast<Item>().Select(i => i.Serial));
                    (offExpected, onExpected) = ($"{BAG},{BOOK},{GOLD1},{BAND}", $"{BAG},{BAND},{BOOK},{GOLD1}");

                    break;
            }

            world.State.ReadContentsOverride = false;
            read().Should().Be(offExpected);
            world.State.ReadContentsOverride = true;
            read().Should().Be(onExpected);
        }

        [Fact]
        public void The_Spell_Entries_Go_Stale_With_Their_Set()
        {
            World world = Build(out _);
            world.State.ReadContentsOverride = true;
            Item book = world.Items.Get(BOOK);
            world.State.SpellEntries(BOOK).Length.Should().Be(3);

            // a refill hands over new entries
            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b1, 0);
            Walk(world, book).Cast<Item>().Select(i => i.Serial).Should().Equal(1u);

            // the clear drops them (the set is 0)
            world.Writer.ClearContainer(BOOK, false, true);
            world.State.SpellEntries(BOOK).Length.Should().Be(0);
            Walk(world, book).Should().BeEmpty();

            // a set without its hand-over (not FillSpellbook) gives no entries, and the check reports it
            world.Ecs.Mirror.SetSpellbook(BOOK, 0b1);
            world.State.SpellEntries(BOOK).Length.Should().Be(0);
            var reports = new List<string>();
            world.State.MismatchObserver = reports.Add;
            world.State.CheckParent(BOOK, "SpellbookGump");
            reports.Should().HaveCount(WorldState.CheckAvailable ? 1 : 0);

            // the book removed: its set and entries are gone
            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b1, 0);
            world.Writer.RemoveWorldItem(book, out _);
            world.State.SpellEntries(BOOK).Length.Should().Be(0);
        }

        [Fact]
        public void A_Walk_Allocates_Nothing_Both_Ways()
        {
            World world = Build(out PlayerMobile p);
            world.State.CheckEnabled = false;
            Item pack = world.Items.Get(PACK);

            foreach (bool on in new[] { false, true })
            {
                world.State.ReadContentsOverride = on;
                pack.FindItem(0xEED);
                long before = GC.GetAllocatedBytesForCurrentThread();

                for (int i = 0; i < 200; i++)
                {
                    pack.FindItem(0xEED);
                    int n = 0;
                    pack.GetTotalAmount(0xEED, null, ref n);
                }

                (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0, $"switch {(on ? "on" : "off")}");
            }
        }
    }
}
