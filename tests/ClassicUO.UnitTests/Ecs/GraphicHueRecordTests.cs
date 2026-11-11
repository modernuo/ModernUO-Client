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
    // T9 S9: an item's and a mobile's Graphic and Hue are the record's only (its Graphic and Hue components), an effect's its
    // EffectState's; land, statics and multi components keep their own. The record-less cases read 0 (a spellbook's spell
    // entry: the scroll graphic)
    public class GraphicHueRecordTests
    {
        private const uint MOB = 0x0000_3900, GROUND = 0x4000_7A60, BOOK = 0x4000_7A61;

        [Fact]
        public void The_Getters_Read_What_The_Operations_Committed()
        {
            World world = WorldStateContentsTests.Build();
            world.Writer.ThrowOnViolation = true;

            Item ground = world.Writer.UpsertWorldItem(GROUND, 0x0EED, 0, 1, 1002, 1000, 0, Direction.East, 0x0BB9, Flags.None, 0, out _, out _);
            (ground.Graphic, ground.Hue).Should().Be(((ushort)0x0EED, (ushort)1), "the packet hue's stored form (FixedHue)");
            world.Ecs.Mirror.TryGetEntity(GROUND, out EcsEntity e).Should().BeTrue();
            (e.GetComponent<Graphic>().Value, e.GetComponent<Hue>().Value).Should().Be(((ushort)0x0EED, (ushort)1));
            ((GameObject)ground).Graphic.Should().Be(0x0EED, "the generic getter dispatches to the record");

            world.Writer.UpsertWorldItem(GROUND, 0x0EEA, 0, 1, 1002, 1000, 0, Direction.East, 0x0021, Flags.None, 0, out _, out _);
            (ground.Graphic, ground.Hue).Should().Be(((ushort)0x0EEA, (ushort)0x0021));

            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.SetMobileBody(m, 0x0191, 0x8021, Flags.None, checkBody: true, setDirection: false, Direction.North);
            (m.Graphic, m.Hue, ((GameObject)m).Hue).Should().Be(((ushort)0x0191, (ushort)0x8021, (ushort)0x8021));
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity me).Should().BeTrue();
            me.GetComponent<MobileInputs>().Body.Should().Be(0x0191, "the inputs pushed from the record");
            world.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void A_Removed_Item_And_A_Detached_One_Read_Zero_And_A_Spell_Entry_The_Scroll()
        {
            World world = WorldStateContentsTests.Build();
            Item ground = world.Writer.UpsertWorldItem(GROUND, 0x0EED, 0, 1, 1002, 1000, 0, Direction.East, 0x21, Flags.None, 0, out _, out _);

            world.RemoveItem(GROUND, true);

            ground.IsDestroyed.Should().BeTrue();
            (ground.Graphic, ground.Hue).Should().Be(((ushort)0, (ushort)0));
            Item.Create(world, 0).Graphic.Should().Be(0, "never committed");

            Item book = world.Writer.FillSpellbook(BOOK, 0x0EFA, 0b101, 0);
            book.Graphic.Should().Be(0x0EFA);

            foreach (Item entry in world.State.SpellEntries(BOOK))
            {
                (entry.Graphic, entry.Hue, entry.Container).Should().Be((Entity.SPELL_ENTRY_GRAPHIC, (ushort)0, BOOK));
            }

            world.State.SpellEntries(BOOK).Length.Should().Be(2);
        }

        [Fact]
        public void A_Placement_Preview_Reads_Its_Preview_Record_Until_Released()
        {
            World world = WorldStateContentsTests.Build();
            object scene = new object();
            Item preview = Item.Create(world, 0);
            preview.IsMulti = true;

            world.Writer.SetPreviewItem(scene, preview, 0x13EC, 0x0021, Layer.Invalid, 0xFFFF_FFFF);
            (preview.Graphic, preview.Hue).Should().Be(((ushort)0x13EC, (ushort)0x0021));
            world.Ecs.Mirror.TryGetEntity(0, out _).Should().BeFalse("a preview is not a world record");

            world.Ecs.ReleasePreviews(scene);
            (preview.Graphic, preview.Hue).Should().Be(((ushort)0, (ushort)0));
        }

        // Codex audit of S9, P2 #1: the race-change gump's preview mobile is serial 0; opening the house placement preview
        // while it is open must not take its record (its body and skin would read 0, and a skin change would commit body 0)
        [Fact]
        public void The_Placement_Preview_Leaves_The_Race_Change_Mobiles_Record_Alone()
        {
            World world = WorldStateContentsTests.Build();
            object gump = new object(), scene = new object();
            var raceMobile = new PlayerMobile(world, 0);
            world.Writer.SetPreviewBody(gump, raceMobile, 0x0190, RaceType.HUMAN, false);
            world.Writer.SetPreviewHue(gump, raceMobile, 0x83EA);

            Item placement = ClassicUO.Game.Scenes.GameScene.CreatePlacementPreview(world, scene, 0x13EC, 0x0021);

            (placement.Graphic, placement.Hue).Should().Be(((ushort)0x13EC, (ushort)0x0021));
            (raceMobile.Graphic, raceMobile.Hue).Should().Be(((ushort)0x0190, (ushort)0x83EA));

            world.Writer.SetPreviewHue(gump, raceMobile, 0x83EB);
            (raceMobile.Graphic, raceMobile.Hue).Should().Be(((ushort)0x0190, (ushort)0x83EB), "the skin change keeps the body");
            (placement.Graphic, placement.Hue).Should().Be(((ushort)0x13EC, (ushort)0x0021));

            world.Ecs.ReleasePreviews(scene);
            (raceMobile.Graphic, raceMobile.Hue).Should().Be(((ushort)0x0190, (ushort)0x83EB), "releasing the scene's preview keeps the gump's");
        }

        // Codex audit of S9, P2 #2: a worn item whose serial is the dying mobile's new serial (the shared case) keeps its
        // graphic and hue through the re-key (the mobile's record replaces the item's before the children are committed)
        [Fact]
        public void A_Worn_Item_Under_The_Death_Serial_Keeps_Its_Graphic_And_Hue_Through_The_Rekey()
        {
            const uint Mob = 0x0000_3910, Dead = 0x8000_3910;
            World world = WorldStateContentsTests.Build();
            world.GetOrCreateMobile(Mob);
            Item worn = world.Writer.Equip(Dead, 0x1F04, Layer.Talisman, Mob, 0x0021, out _, out _);
            (worn.Graphic, worn.Hue).Should().Be(((ushort)0x1F04, (ushort)0x0021));

            world.Writer.RekeyDeadMobile(world.Mobiles.Get(Mob)).Should().Be(Dead);

            Item item = world.Items.Get(Dead);
            item.Should().BeSameAs(worn);

            // its values reach the corpse with the item (PlaceCorpseEquipment takes them from the item)
            const uint Corpse = 0x4000_7A70;
            world.Writer.UpsertWorldItem(Corpse, 0x2006, 0, 0x190, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _, out _);
            world.Writer.PlaceCorpseEquipment(world.Items.Get(Corpse), Dead, Layer.Talisman);
            (item.Graphic, item.Hue).Should().Be(((ushort)0x1F04, (ushort)0x0021));
        }

        [Fact]
        public void An_Effect_Reads_Its_State_And_A_Static_Its_Own_Fields()

        {
            World world = WorldStateContentsTests.Build();
            var fx = new FixedEffect(world, world.Effects, 1000, 1000, 0, 0x3779, 0x0021, 100, 0);

            ((GameObject)fx).Graphic.Should().Be(0, "no state before it joins the list");
            world.Effects.Add(fx);
            (fx.Graphic, fx.Hue).Should().Be(((ushort)0x3779, (ushort)0x0021));
            fx.EcsEntity.GetComponent<EffectState>().Graphic.Should().Be(0x3779);

            fx.Destroy();
            (fx.Graphic, fx.Hue).Should().Be(((ushort)0, (ushort)0), "as the destroyed object's were");

            var s = new Static(world) { Graphic = 0x0ABC, Hue = 0x0021 };
            (((GameObject)s).Graphic, ((GameObject)s).Hue).Should().Be(((ushort)0x0ABC, (ushort)0x0021));
            s.Destroy();
            (s.Graphic, s.Hue).Should().Be(((ushort)0, (ushort)0));
        }
    }
}
