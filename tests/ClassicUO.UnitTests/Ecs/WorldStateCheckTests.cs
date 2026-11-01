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
    // T2: CUO_CONTENTS_CHECK. At every Contents read (and every frame for each open container gump, through CheckParent) the
    // ECS enumeration (the spellbook's spell entries from its set, then Contents) must equal the World child list. A world
    // changed only through the funnel checks clean; each deliberate divergence is reported with its parent.
    public class WorldStateCheckTests
    {
        private const uint PACK = 0x4000_7900, A = 0x4000_7901, B = 0x4000_7902, C = 0x4000_7903, BOOK = 0x4000_7946, INBOOK = 0x4000_7947;

        private static (World World, List<string> Reports) Build()
        {
            World world = WorldStateContentsTests.Build();
            var reports = new List<string>();
            world.State.CheckEnabled = true;
            world.State.MismatchObserver = reports.Add;

            return (world, reports);
        }

        private static void ReadAll(World world)
        {
            foreach (uint parent in WorldStateContentsTests.Parents(world))
            {
                world.State.Contents(parent);
                world.State.CheckParent(parent, "gump");
            }
        }

        [Theory]
        [MemberData(nameof(Changes))]
        public void A_World_Changed_Through_The_Funnel_Checks_Clean(WorldStateContentsTests.Change change)
        {
            (World world, List<string> reports) = Build();
            ReadAll(world);

            WorldStateContentsTests.Apply(world, change);
            ReadAll(world);

            reports.Should().BeEmpty();
            world.State.Mismatches.Should().Be(0);
            world.State.ContentsReads.Should().BeGreaterThan(0);
        }

        public static IEnumerable<object[]> Changes() =>
            Enum.GetValues<WorldStateContentsTests.Change>().Where(c => c != WorldStateContentsTests.Change.MirrorClear).Select(c => new object[] { c });

        [Fact]
        public void The_Spellbook_Entries_Check_Against_The_Set()
        {
            (World world, List<string> reports) = Build();

            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b1011, 0x8000_0000);
            world.State.Spellbook(BOOK).Should().Be(0x8000_0000_0000_000BUL);
            ReadAll(world);

            // a real item the server puts in the book goes after the entries
            world.Writer.PlaceInContainer(INBOOK, 0x1F2E, 1, 0, 0, 0, BOOK, out _);
            world.State.Contents(BOOK).ToArray().Should().Equal(INBOOK);
            ReadAll(world);

            // a refill replaces the entries and detaches the real item
            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b10, 0);
            ReadAll(world);

            // 0x3C/0x24's clear drops the entries too
            world.Writer.ClearContainer(BOOK, false, true);
            world.State.Spellbook(BOOK).Should().Be(0UL);
            world.Items.Get(BOOK).Items.Should().BeNull();
            ReadAll(world);

            reports.Should().BeEmpty();
        }

        public enum Divergence { ChildOrder, StrayChild, MissingChild, MirrorDetach, MirrorRestamp, SpellSet, SpellEntriesDropped }

        [Theory]
        [InlineData(Divergence.ChildOrder, PACK)]
        [InlineData(Divergence.StrayChild, PACK)]
        [InlineData(Divergence.MissingChild, PACK)]
        [InlineData(Divergence.MirrorDetach, PACK)]
        [InlineData(Divergence.MirrorRestamp, PACK)]
        [InlineData(Divergence.SpellSet, BOOK)]
        [InlineData(Divergence.SpellEntriesDropped, BOOK)]
        public void A_Divergence_Is_Reported_At_A_Read_And_At_The_Gump_Check(Divergence divergence, uint parent)
        {
            (World world, List<string> reports) = Build();
            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b101, 0);
            ReadAll(world);
            reports.Should().BeEmpty();

            Item pack = world.Items.Get(PACK);

            switch (divergence)
            {
                case Divergence.ChildOrder:
                    Item b = world.Items.Get(B);
                    pack.Remove(b);
                    pack.PushToBack(b);

                    break;

                case Divergence.StrayChild:
                    pack.PushToBack(Item.Create(world, 0x4000_7999));

                    break;

                case Divergence.MissingChild:
                    pack.Remove(world.Items.Get(C));

                    break;

                case Divergence.MirrorDetach:
                    world.Ecs.Mirror.DetachChild(A);

                    break;

                case Divergence.MirrorRestamp:
                    world.Ecs.Mirror.StampChild(B);

                    break;

                case Divergence.SpellSet:
                    world.Ecs.Mirror.SetSpellbook(BOOK, 0b111);

                    break;

                case Divergence.SpellEntriesDropped:
                    world.Items.Get(BOOK).Clear();

                    break;
            }

            string prefix = $"children of 0x{parent:X8}";

            world.State.Contents(parent);
            reports.Should().ContainSingle().Which.Should().Contain("(read) " + prefix);

            world.State.CheckParent(parent, "ContainerGump");
            reports.Should().HaveCount(2);
            reports[1].Should().Contain("(ContainerGump) " + prefix);
            world.State.Mismatches.Should().Be(2);
        }

        [Fact]
        public void Off_Nothing_Is_Compared()
        {
            World world = WorldStateContentsTests.Build();
            var reports = new List<string>();
            world.State.MismatchObserver = reports.Add;
            world.Items.Get(PACK).Remove(world.Items.Get(C));

            world.State.Contents(PACK);
            world.State.CheckParent(PACK, "gump");

            reports.Should().BeEmpty();
            world.State.ContentsReads.Should().Be(0);
        }
    }
}
