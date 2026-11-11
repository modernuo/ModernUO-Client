using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 S5: Item Amount, Container and Layer are the record's only (no cache); the record-less cases read defined values
    public class ItemRecordFieldsTests
    {
        private const uint MOB = 0x0000_3900, PACK = 0x4000_7900, BOOK = 0x4000_7946, INBOOK = 0x4000_7947;
        private const uint GROUND = 0x4000_7A50, WORN = 0x4000_7A51, INSIDE = 0x4000_7A52;

        [Fact]
        public void The_Getters_Read_What_The_Operations_Committed()
        {
            World world = WorldStateContentsTests.Build();
            world.Writer.ThrowOnViolation = true;

            Item ground = world.Writer.UpsertWorldItem(GROUND, 0x0EED, 0, 7, 1002, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            (ground.Amount, ground.Container, ground.Layer, ground.OnGround).Should().Be(((ushort)7, 0xFFFF_FFFFu, Layer.Invalid, true));

            Item worn = world.Writer.Equip(WORN, 0x1517, Layer.Shirt, MOB, 0, out _, out _);
            (worn.Amount, worn.Container, worn.Layer, worn.OnGround).Should().Be(((ushort)1, MOB, Layer.Shirt, false));

            Item inside = world.Writer.PlaceInContainer(INSIDE, 0x0EED, 12, 10, 10, 0, PACK, out _);
            (inside.Amount, inside.Container, inside.Layer).Should().Be(((ushort)12, PACK, Layer.Invalid));

            // moved from the container to the ground, then worn: each getter follows its record
            world.Writer.UpsertWorldItem(INSIDE, 0x0EED, 0, 3, 1003, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            (inside.Amount, inside.Container).Should().Be(((ushort)3, 0xFFFF_FFFFu));
            world.Writer.Equip(INSIDE, 0x0EED, Layer.Ring, MOB, 0, out _, out _);
            (inside.Container, inside.Layer).Should().Be((MOB, Layer.Ring));
            world.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void A_Removed_Item_Reads_The_Defaults()
        {
            World world = WorldStateContentsTests.Build();
            Item worn = world.Writer.Equip(WORN, 0x1517, Layer.Shirt, MOB, 0, out _, out _);

            world.RemoveItem(WORN, true);

            worn.IsDestroyed.Should().BeTrue();
            (worn.Amount, worn.Container, worn.Layer).Should().Be(((ushort)0, 0xFFFF_FFFFu, Layer.Invalid));
        }

        // FillSpellbook's entries have no record: their spell number (= their serial) and their book
        [Fact]
        public void A_Spell_Entry_Reads_Its_Spell_Number_And_Its_Book()
        {
            World world = WorldStateContentsTests.Build();

            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b1011, 0);

            var entries = world.State.SpellEntries(BOOK).ToArray();
            entries.Should().HaveCount(3);

            foreach (Item entry in entries)
            {
                entry.TryScalarEntity(out _).Should().BeFalse("an entry is no world object");
                (entry.Amount, entry.Container, entry.Layer).Should().Be(((ushort)entry.Serial, BOOK, Layer.Invalid));
            }

            entries[0].Amount.Should().Be(1);
            entries[2].Amount.Should().Be(4);

            // a refill drops the old entries: they are no longer the book's
            world.Writer.FillSpellbook(BOOK, 0xEFA, 0b1, 0);
            (entries[2].Amount, entries[2].Container).Should().Be(((ushort)0, 0xFFFF_FFFFu));
        }

        // a preview (character creation, race change) reads its preview record
        [Fact]
        public void A_Preview_Item_Reads_Its_Preview_Record()
        {
            World world = WorldStateContentsTests.Build();
            object gump = new object();
            Item hair = Item.Create(world, 0x4000_0000 + (uint)Layer.Hair);

            (hair.Layer, hair.Container).Should().Be((Layer.Invalid, 0xFFFF_FFFFu), "no record before the first preview write");
            world.Writer.SetPreviewItem(gump, hair, 0x203B, 0x44E, Layer.Hair, 1);
            (hair.Layer, hair.Container).Should().Be((Layer.Hair, 1u));

            world.Ecs.ReleasePreviews(gump);
            (hair.Layer, hair.Container).Should().Be((Layer.Invalid, 0xFFFF_FFFFu), "released with its gump");
        }
    }
}
