using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;
using R = ClassicUO.UnitTests.Ecs.WorldStateReaderTests;

namespace ClassicUO.UnitTests.Ecs
{
    // T2, the Codex audit's findings: (1) a spellbook's detached spell entries (serial 1-64) are leaves, also when a real
    // mobile has the same serial; (2) a death re-key onto an occupied dead serial detaches the displaced mobile's children;
    // (3) a book's spell entries are dropped with its record or set, without another read; (6) every divergence is reported
    // through Contents, Children and HasChildren with the switch both ways; the off walk reads the next link after the body.
    // (not in parallel with the mobile tests: the builders set the static Time.Ticks and RandomHelper seed)
    [Collection(nameof(FunnelCheckCollection))]
    public class WorldStateAuditTests
    {
        private const uint MOB = 0x0000_3900, DEAD = 0x8000_3900, NEWSHIRT = 0x4000_7960, BANK = 0x4000_7A20, BANKBOOK = 0x4000_7A21, PLATE = 0x4000_7A22;

        private static void BothWays(World world, Action check) => check();

        private static Item FirstSpellEntry(World world)
        {
            ReadOnlySpan<Item> entries = world.State.SpellEntries(R.BOOK);
            entries.Length.Should().BeGreaterThan(0);
            entries[0].Serial.Should().Be(1u);

            return entries[0];
        }

        // ---- (1) the spell entries are leaves

        [Fact]
        public void A_Spell_Entry_Is_A_Leaf_When_The_Player_Has_Its_Serial()
        {
            // the player at serial 1 carries the book with spell 1: player -> pack -> book -> spell 1 must not lead back
            World world = R.Build(out PlayerMobile p, 1);
            Item entry = FirstSpellEntry(world);
            world.State.Children(p).IsEmpty.Should().BeFalse();

            BothWays(world, () =>
            {
                R.Walk(world, entry).Should().BeEmpty();
                world.State.HasChildren(entry).Should().BeFalse();
                world.State.Children(entry).IsEmpty.Should().BeTrue();
            });
        }

        [Fact]
        public void A_Spell_Entry_Is_A_Leaf_When_Another_Mobile_Has_Its_Serial()
        {
            World world = R.Build(out PlayerMobile p);
            Mobile one = world.GetOrCreateMobile(1);
            world.Writer.Equip(PLATE, 0x1415, Layer.Torso, one.Serial, 0, out _, out _);
            world.State.Contents(1).ToArray().Should().Equal(PLATE);
            Item entry = FirstSpellEntry(world);

            BothWays(world, () =>
            {
                R.Walk(world, entry).Should().BeEmpty();
                world.State.HasChildren(entry).Should().BeFalse();

                // the recursive readers do not find the other mobile's plate through the entry
                p.FindItem(0x1415).Should().BeNull();
                world.Items.Get(R.PACK).GetItemByGraphic(0x1415, true).Should().BeNull();
                int n = 0;
                world.Items.Get(R.PACK).GetTotalAmount(0x1415, null, ref n);
                n.Should().Be(0);
            });
        }

        [Fact]
        public void A_Replaced_Instance_Is_A_Leaf_With_The_Switch_On()
        {
            // an object World no longer holds for its serial does not read the current one's contents: a leaf
            World world = R.Build(out _);
            Item stale = Item.Create(world, R.BAG);
            world.State.HasChildren(world.Items.Get(R.BAG)).Should().BeTrue();

            R.Walk(world, stale).Should().BeEmpty();
            world.State.HasChildren(stale).Should().BeFalse();
        }

        [Fact]
        public void World_Clear_Drops_The_Cached_Slots_Also_With_No_Record_Left()
        {
            var world = new World();
            world.State.Contents(0x4000_7A40).IsEmpty.Should().BeTrue();
            world.Ecs.Mirror.CachedContentsParents.Should().Be(1);

            world.Ecs.OnWorldCleared();
            world.Ecs.Mirror.CachedContentsParents.Should().Be(0);
        }

        // ---- (2) the re-key onto an occupied dead serial

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void A_Rekey_Onto_An_Occupied_Dead_Serial_Drops_The_Displaced_Children(bool warm, bool incomingWorn)
        {
            World world = WorldStateContentsTests.Build();

            // the first death: MOB's worn items go with it to DEAD
            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);
            Mobile displaced = world.Mobiles.Get(DEAD);
            world.State.HasChildren(displaced).Should().BeTrue();

            if (warm)
            {
                world.State.Contents(DEAD).Length.Should().Be(4);
            }

            // MOB comes back (with or without a shirt) and dies again before the first corpse animation is over
            world.GetOrCreateMobile(MOB);

            if (incomingWorn)
            {
                world.Writer.Equip(NEWSHIRT, 0x1517, Layer.Shirt, MOB, 0, out _, out _);
            }

            world.Writer.RekeyDeadMobile(world.Mobiles.Get(MOB)).Should().Be(DEAD);
            Mobile dead = world.Mobiles.Get(DEAD);
            dead.Should().NotBeSameAs(displaced);

            uint[] expected = incomingWorn ? new[] { NEWSHIRT } : Array.Empty<uint>();
            world.State.Contents(DEAD).ToArray().Should().Equal(expected);
            BothWays(world, () =>
            {
                R.Walk(world, dead).Cast<Item>().Select(i => i.Serial).Should().Equal(expected);
                world.State.HasChildren(dead).Should().Be(incomingWorn);
            });

            WorldStateContentsTests.AllMatch(world);
        }

        // ---- (3) the spell entries are reclaimed without another read

        public enum BookGone { Removed, ContainerRemoved, Cleared, EmptyRefill, Login, MapChange, Bank }

        [Theory]
        [InlineData(BookGone.Removed)]
        [InlineData(BookGone.ContainerRemoved)]
        [InlineData(BookGone.Cleared)]
        [InlineData(BookGone.EmptyRefill)]
        [InlineData(BookGone.Login)]
        [InlineData(BookGone.MapChange)]
        [InlineData(BookGone.Bank)]
        public void The_Spell_Entries_Go_With_The_Book_Without_Another_Read(BookGone how)
        {
            World world = R.Build(out PlayerMobile p);
            uint book = R.BOOK;

            if (how == BookGone.Bank)
            {
                world.Writer.Equip(BANK, 0xE7C, Layer.Bank, p.Serial, 0, out _, out _);
                world.Writer.PlaceInContainer(BANKBOOK, 0xEFA, 1, 10, 10, 0, BANK, out _);
                world.Writer.FillSpellbook(BANKBOOK, 0xEFA, 0b11, 0);
                book = BANKBOOK;
            }
            else if (how == BookGone.MapChange)
            {
                // the map change keeps the player's items (noplayer: the tests have no Map): a book on the ground
                Item ground = world.Writer.UpsertWorldItem(BANKBOOK, 0xEFA, 0, 1, 1002, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out WorldWriter.Placement placement);
                world.Writer.PlaceWorldItem(placement);
                world.Writer.FillSpellbook(ground.Serial, 0xEFA, 0b11, 0);
                book = BANKBOOK;
            }

            bool two = how is BookGone.Bank or BookGone.MapChange;
            world.State.SpellEntryBooks.Should().Be(two ? 2 : 1);
            long held = world.State.SpellEntriesBytes;

            switch (how)
            {
                case BookGone.Removed:
                    world.Writer.RemoveWorldItem(world.Items.Get(R.BOOK), out _);

                    break;

                case BookGone.ContainerRemoved:
                    world.Writer.RemoveWorldItem(world.Items.Get(R.PACK), out _);

                    break;

                case BookGone.Cleared:
                    world.Writer.ClearContainer(R.BOOK, false, true);

                    break;

                case BookGone.EmptyRefill:
                    world.Writer.FillSpellbook(R.BOOK, 0xEFA, 0, 0);

                    break;

                case BookGone.Login:
                    world.Writer.ClearForLogin();

                    break;

                case BookGone.MapChange:
                    world.Writer.ClearForMapChange(true);

                    break;

                case BookGone.Bank:
                    world.Writer.RemoveBankContents(p, world.Items.Get(BANK)).Should().BeTrue();

                    break;
            }

            world.Items.Get(book)?.Items.Should().BeNull("the book is gone or empty");
            world.State.SpellEntryBooks.Should().Be(two ? 1 : 0);
            world.State.SpellEntriesBytes.Should().BeLessThan(held);
        }

        [Fact]
        public void A_Refill_Replaces_The_Entries()
        {
            World world = R.Build(out _);
            world.Writer.FillSpellbook(R.BOOK, 0xEFA, 0b110, 0);
            world.State.SpellEntryBooks.Should().Be(1);
            world.State.SpellEntries(R.BOOK).ToArray().Select(i => i.Serial).Should().Equal(2u, 3u);
        }
    }
}
