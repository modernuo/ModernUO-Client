using System;
using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 S4: the character-creation and race-change gumps' detached preview objects are written by WorldWriter's preview
    // operations (SetPreviewBody, SetPreviewItem, SetPreviewHue): a record in WorldEcs.Previews (never the world mirror),
    // then the cache. Each test replays a gump's sequence twice, once with the direct writes the gumps made before
    // (OldBody/OldItem/OldHue, copied from 7e3448969) and once through the operations, and compares the results.
    public class PreviewWriterTests
    {
        private const uint PREVIEW = 1, RACE_PREVIEW = 0;

        // the gump that owns the previews (ReleasePreviews releases one owner's records)
        private static readonly object Owner = new object();

        private static World NewWorld()
        {
            var world = new World();
            world.Writer.ThrowOnViolation = true;

            return world;
        }

        // CreateCharAppearanceGump.CreateCharacter's body writes before S4 (the switch's graphic per race and sex)
        private static void OldBody(Mobile m, RaceType race, bool female)
        {
            m.Race = race;
            m.IsFemale = female;

            if (female)
            {
                m.Flags |= Flags.Female;
            }
            else
            {
                m.Flags &= ~Flags.Female;
            }

            m.Graphic = Body(race, female);
        }

        private static ushort Body(RaceType race, bool female) => race switch
        {
            RaceType.GARGOYLE => female ? (ushort)0x029B : (ushort)0x029A,
            RaceType.ELF => female ? (ushort)0x025E : (ushort)0x025D,
            _ => female ? (ushort)0x0191 : (ushort)0x0190
        };

        // CreateCharAppearanceGump.CreateItem's writes before S4 (RaceChangeGump's left the container at 0xFFFF_FFFF)
        private static Item OldItem(World world, ushort graphic, ushort hue, Layer layer, Mobile wearer)
        {
            Item item = Item.Create(world, 0x4000_0000 + (uint)layer);
            item.Graphic = graphic;
            item.Hue = hue;
            item.Layer = layer;

            if (wearer != null)
            {
                item.Container = wearer;
            }

            return item;
        }

        private static Item NewItem(World world, ushort graphic, ushort hue, Layer layer, Mobile wearer, object owner = null)
        {
            Item item = Item.Create(world, 0x4000_0000 + (uint)layer);
            world.Writer.SetPreviewItem(owner ?? Owner, item, graphic, hue, layer, wearer != null ? wearer.Serial : item.Container);

            return item;
        }

        private static (ushort, ushort, Flags, RaceType, bool) Fields(Mobile m) => (m.Graphic, m.Hue, m.Flags, m.Race, m.IsFemale);

        private static (uint, ushort, ushort, Layer, uint) Fields(Item i) => (i.Serial, i.Graphic, i.Hue, i.Layer, i.Container);

        public static IEnumerable<object[]> Bodies()
        {
            foreach (RaceType race in new[] { RaceType.HUMAN, RaceType.ELF, RaceType.GARGOYLE })
            {
                yield return new object[] { (int)race, false };
                yield return new object[] { (int)race, true };
            }
        }

        [Theory]
        [MemberData(nameof(Bodies))]
        public void Creation_Preview_Ends_With_The_Values_The_Direct_Writes_Gave(int raceValue, bool female)
        {
            var race = (RaceType)raceValue;
            World oldWorld = NewWorld(), newWorld = NewWorld();
            var oldM = new PlayerMobile(oldWorld, PREVIEW);
            var newM = new PlayerMobile(newWorld, PREVIEW);

            // a first pass with the other sex (the gump's sex buttons rebuild the same instance), then this one
            OldBody(oldM, race, !female);
            newWorld.Writer.SetPreviewBody(Owner, newM, Body(race, !female), race, !female);
            OldBody(oldM, race, female);
            newWorld.Writer.SetPreviewBody(Owner, newM, Body(race, female), race, female);

            oldM.Hue = 0x83EA; // UpdateEquipments' skin
            newWorld.Writer.SetPreviewHue(Owner, newM, 0x83EA);

            Item oldShirt = OldItem(oldWorld, 0x1518, 0x0384, Layer.Shirt, oldM);
            Item newShirt = NewItem(newWorld, 0x1518, 0x0384, Layer.Shirt, newM);
            Item oldHair = OldItem(oldWorld, 0x203B, 0x044E, Layer.Hair, oldM);
            Item newHair = NewItem(newWorld, 0x203B, 0x044E, Layer.Hair, newM);

            oldShirt.Hue = 0x0021; // a colour pick
            newWorld.Writer.SetPreviewHue(Owner, newShirt, 0x0021);

            Fields(newM).Should().Be(Fields(oldM));
            Fields(newShirt).Should().Be(Fields(oldShirt));
            Fields(newHair).Should().Be(Fields(oldHair));
            newShirt.Container.Should().Be(PREVIEW);

            // the record holds the same values
            newWorld.Ecs.TryGetPreviewRecord(newM, out EcsEntity m).Should().BeTrue();
            m.GetComponent<ClassicUO.Ecs.Graphic>().Value.Should().Be(Body(race, female));
            m.GetComponent<ClassicUO.Ecs.Hue>().Value.Should().Be((ushort)0x83EA);
            m.GetComponent<ClassicUO.Ecs.Flags>().Value.Should().Be((byte)newM.Flags);
            EcsRead.TryGet(m, out MobileTraits t);
            ((RaceType)t.Race, (t.Bits & MobileTraitBits.Female) != 0).Should().Be((race, female));

            newWorld.Ecs.TryGetPreviewRecord(newShirt, out EcsEntity s).Should().BeTrue();
            (s.GetComponent<ClassicUO.Ecs.Graphic>().Value, s.GetComponent<ClassicUO.Ecs.Hue>().Value, s.GetComponent<ItemData>().Layer)
                .Should().Be(((ushort)0x1518, (ushort)0x0021, (byte)Layer.Shirt));
            newWorld.Ecs.Previews.ParentOf(newShirt.Serial).Should().Be(PREVIEW);
        }

        [Fact]
        public void Race_Change_Preview_Ends_With_The_Values_The_Direct_Writes_Gave()
        {
            World oldWorld = NewWorld(), newWorld = NewWorld();
            var oldM = new PlayerMobile(oldWorld, RACE_PREVIEW);
            var newM = new PlayerMobile(newWorld, RACE_PREVIEW);

            OldBody(oldM, RaceType.ELF, false);
            newWorld.Writer.SetPreviewBody(Owner, newM, Body(RaceType.ELF, false), RaceType.ELF, false);

            // CreateCharacter's hair: the id argument is the serial (the gump's own quirk, kept), truncated to a graphic;
            // UpdateEquipments then writes the real graphic and hue, keeping layer and container
            Item oldHair = OldItem(oldWorld, unchecked((ushort)(0x4000_0000 + (int)Layer.Hair)), 0, Layer.Hair, null);
            Item newHair = NewItem(newWorld, unchecked((ushort)(0x4000_0000 + (int)Layer.Hair)), 0, Layer.Hair, null);
            oldM.Hue = 0x8402;
            newWorld.Writer.SetPreviewHue(Owner, newM, 0x8402);
            oldHair.Graphic = 0x2FBF;
            oldHair.Hue = 0x0035;
            newWorld.Writer.SetPreviewItem(Owner, newHair, 0x2FBF, 0x0035, newHair.Layer, newHair.Container);

            Fields(newM).Should().Be(Fields(oldM));
            Fields(newHair).Should().Be(Fields(oldHair));
            newHair.Container.Should().Be(0xFFFF_FFFFu);
            newWorld.Ecs.Previews.ParentOf(newHair.Serial).Should().Be(WorldMirror.NO_CONTAINER);
        }

        [Fact]
        public void Previews_Stay_Out_Of_The_World_Mirror_And_Tables()
        {
            World world = NewWorld();
            int records = world.Ecs.Mirror.Count;
            var m = new PlayerMobile(world, PREVIEW);

            world.Writer.SetPreviewBody(Owner, m, 0x0190, RaceType.HUMAN, false);
            NewItem(world, 0x1710, 0x0384, Layer.Shoes, m);

            world.Ecs.Mirror.Count.Should().Be(records);
            world.Holds(m).Should().BeFalse();
            world.Ecs.Previews.Count.Should().Be(2);
            world.Ecs.Previews.MobileCount.Should().Be(1);
        }

        [Fact]
        public void A_Newer_Instance_Takes_The_Serial_And_Release_Drops_Every_Record()
        {
            World world = NewWorld();
            var m = new PlayerMobile(world, PREVIEW);
            world.Writer.SetPreviewBody(Owner, m, 0x0190, RaceType.HUMAN, false);

            // CreateItem replaces the layer's item with a new instance under the same serial
            Item first = NewItem(world, 0x1710, 0x0384, Layer.Shoes, m);
            Item second = NewItem(world, 0x170F, 0x0384, Layer.Shoes, m);

            world.Ecs.TryGetPreviewRecord(first, out _).Should().BeFalse();
            world.Ecs.TryGetPreviewRecord(second, out EcsEntity e).Should().BeTrue();
            e.GetComponent<ClassicUO.Ecs.Graphic>().Value.Should().Be((ushort)0x170F);

            world.Ecs.ReleasePreviews(Owner);

            world.Ecs.TryGetPreviewRecord(m, out _).Should().BeFalse();
            world.Ecs.TryGetPreviewRecord(second, out _).Should().BeFalse();
            world.Ecs.Previews.Count.Should().Be(0);
            (m.Graphic, second.Graphic).Should().Be(((ushort)0x0190, (ushort)0x170F), "the cache stays");
        }

        [Fact]
        public void A_Held_Object_Is_Rejected()
        {
            World world = NewWorld();
            Mobile held = world.GetOrCreateMobile(0x0000_1234);
            held.Graphic = 0x0190;

            Action act = () => world.Writer.SetPreviewBody(Owner, held, 0x025D, RaceType.ELF, true);
            act.Should().Throw<ArgumentException>();

            world.Writer.ThrowOnViolation = false;
            int before = world.Writer.Violations;
            world.Writer.SetPreviewHue(Owner, held, 0x0021);
            world.Writer.SetPreviewBody(Owner, held, 0x025D, RaceType.ELF, true);

            world.Writer.Violations.Should().Be(before + 2);
            (held.Graphic, held.Hue, held.IsFemale).Should().Be(((ushort)0x0190, (ushort)0, false), "a rejected operation changes nothing");
            world.Ecs.Previews.Should().BeNull("nothing was committed");

            // another World's detached object is not this World's preview
            World other = NewWorld();
            other.Writer.ThrowOnViolation = false;
            other.Writer.SetPreviewBody(Owner, new PlayerMobile(world, PREVIEW), 0x0190, RaceType.HUMAN, false);
            other.Writer.Violations.Should().Be(1);
        }

        // Codex audit of S4 (P2): closing one preview gump must not drop another open gump's records
        [Fact]
        public void Releasing_One_Owner_Keeps_Another_Owners_Records()
        {
            World world = NewWorld();
            object a = new object(), b = new object();

            // A: a character-creation preview; B: a race-change preview in the same World (another mobile serial, the same
            // hair serial: B's hair is newer, so B holds that serial)
            var ma = new PlayerMobile(world, PREVIEW);
            world.Writer.SetPreviewBody(a, ma, 0x0190, RaceType.HUMAN, false);
            Item shirtA = NewItem(world, 0x1518, 0x0384, Layer.Shirt, ma, a);
            Item hairA = NewItem(world, 0x203B, 0x044E, Layer.Hair, ma, a);

            var mb = new PlayerMobile(world, RACE_PREVIEW);
            world.Writer.SetPreviewBody(b, mb, 0x025D, RaceType.ELF, false);
            Item hairB = NewItem(world, 0x2FBF, 0x0035, Layer.Hair, null, b);

            world.Ecs.ReleasePreviews(a);

            world.Ecs.TryGetPreviewRecord(mb, out _).Should().BeTrue("B is still open");
            world.Ecs.TryGetPreviewRecord(hairB, out EcsEntity h).Should().BeTrue("B holds the shared hair serial");
            h.GetComponent<ClassicUO.Ecs.Graphic>().Value.Should().Be((ushort)0x2FBF);
            world.Ecs.TryGetPreviewRecord(ma, out _).Should().BeFalse();
            world.Ecs.TryGetPreviewRecord(shirtA, out _).Should().BeFalse();
            world.Ecs.TryGetPreviewRecord(hairA, out _).Should().BeFalse();
            world.Ecs.Previews.Count.Should().Be(2);
        }

        // Codex audit of S4 (P2): a repeated release of a closed gump must not erase a gump opened after it
        [Fact]
        public void A_Repeated_Release_Keeps_A_Newer_Owners_Records()
        {
            World world = NewWorld();
            object a = new object(), b = new object();

            var ma = new PlayerMobile(world, RACE_PREVIEW);
            world.Writer.SetPreviewBody(a, ma, 0x0190, RaceType.HUMAN, false);
            NewItem(world, 0x203B, 0x044E, Layer.Hair, null, a);
            world.Ecs.ReleasePreviews(a);

            // B reuses the same serials
            var mb = new PlayerMobile(world, RACE_PREVIEW);
            world.Writer.SetPreviewBody(b, mb, 0x025D, RaceType.ELF, true);
            Item hairB = NewItem(world, 0x2FBF, 0x0035, Layer.Hair, null, b);

            world.Ecs.ReleasePreviews(a);

            world.Ecs.TryGetPreviewRecord(mb, out _).Should().BeTrue();
            world.Ecs.TryGetPreviewRecord(hairB, out _).Should().BeTrue();
            world.Ecs.Previews.Count.Should().Be(2);
        }
    }
}
