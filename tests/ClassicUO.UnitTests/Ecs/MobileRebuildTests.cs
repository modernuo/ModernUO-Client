using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Friflo.Engine.ECS;
using Xunit;
using Entity = Friflo.Engine.ECS.Entity;
using Item = ClassicUO.Game.GameObjects.Item;
using Mobile = ClassicUO.Game.GameObjects.Mobile;

namespace ClassicUO.UnitTests.Ecs
{
    // MobileAnimationSystem.Rebuild through its data-source seam (tiledata, equipconv, the 7.0.0.0 gate) in place of the
    // loaded client files: per-layer skips, hands, light-only items, the mount and Mobile.IsMounted.
    public class MobileRebuildTests
    {
        private const uint MOBILE = 0x0000_1000;
        private const ushort HUMAN = 0x0190;
        private const ushort HORSE = 0x00C8;

        private const ushort SHIRT = 0x1100, PANTS = 0x1101, SHOES = 0x1102, LEGS = 0x1103, HAIR = 0x1104, SWORD = 0x1105,
            TORCH = 0x1106, LAMP = 0x1108, BARE = 0x1109, MOUNT = 0x3EA2, TILLER = 0x3E96, CLOAK = 0x1110, HELM = 0x1111;

        private readonly StaticTiles[] _tiles = new StaticTiles[0x4000 + 0x100];
        private readonly Dictionary<ushort, Dictionary<ushort, EquipConvData>> _conv = new Dictionary<ushort, Dictionary<ushort, EquipConvData>>();

        public MobileRebuildTests()
        {
            Tile(SHIRT, 0x0101);
            Tile(PANTS, 0x0102, TileFlag.PartialHue);
            Tile(SHOES, 0x0103);
            Tile(LEGS, 0x0104);
            Tile(HAIR, 0x0105);
            Tile(SWORD, 0x0263);
            Tile(TORCH, 0x0240, TileFlag.LightSource);
            Tile(LAMP, 0, TileFlag.LightSource);
            Tile(BARE, 0);
            Tile(CLOAK, 0x0106);
            Tile(HELM, 0x0107);
            Tile(MOUNT, 0x00CC);
            Tile(TILLER, 0);
        }

        private void Tile(ushort graphic, ushort animId, TileFlag flags = 0) =>
            _tiles[graphic] = new StaticTiles((ulong)flags, 0, 0, 0, animId, 0, 0, 0, "");

        private static WorldMirror NewMirror(out Entity mobile, ushort body = HUMAN)
        {
            var mirror = new WorldMirror();
            mobile = mirror.UpsertMobile(MOBILE, body, 0, 10, 10, 0, 0, 0, false);

            return mirror;
        }

        private static void Wear(WorldMirror m, uint item, Layer layer, ushort graphic, ushort hue = 0) =>
            m.UpsertItem(item, graphic, hue, 0, 0, 0, 0, 0, 1, MOBILE, (byte)layer, false, attach: true);

        private EquipmentSlots Run(WorldMirror mirror, Entity mobile, AnimStateFlags flags = AnimStateFlags.None, Mobile owner = null, bool cv7000 = true)
        {
            var sys = new MobileAnimationSystem(mirror);
            var s = new AnimationState { Flags = flags };
            sys.Rebuild(mobile, in s, owner, _tiles, _conv, cv7000);

            return mobile.GetComponent<EquipmentSlots>();
        }

        private static List<Layer> Layers(in EquipmentSlots eq)
        {
            var l = new List<Layer>();

            for (int i = 0; i < eq.Count; i++)
            {
                l.Add((Layer)eq.Slots[i].Layer);
            }

            return l;
        }

        [Fact]
        public void The_Slots_Come_From_The_Worn_Layers_Not_The_Index_Order()
        {
            // T3a (the equipment-order stamp): a layer's first item is the lowest child-order stamp (the child list's order),
            // whatever order the mirror's equipment index holds them in; an item never attached (an orphan) is not worn
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT);
            Wear(mirror, 0x4000_0002, Layer.Shirt, SHOES);
            mirror.UpsertItem(0x4000_0003, HELM, 0, 0, 0, 0, 0, 0, 1, MOBILE, (byte)Layer.Helmet, false);
            mirror.UpsertItem(0x4000_0004, MOUNT, 0, 0, 0, 0, 0, 0, 1, MOBILE, (byte)Layer.Mount, false);

            // a layer round trip without a push re-adds the first shirt's index entry (to the index's end), keeping its stamp
            mirror.UpsertItem(0x4000_0001, SHIRT, 0, 0, 0, 0, 0, 0, 1, MOBILE, (byte)Layer.Pants, false);
            mirror.UpsertItem(0x4000_0001, SHIRT, 0, 0, 0, 0, 0, 0, 1, MOBILE, (byte)Layer.Shirt, false);
            mirror.Worn(MOBILE, (byte)Layer.Shirt).Should().Be(0x4000_0001);

            EquipmentSlots eq = Run(mirror, mobile);
            var items = new List<uint>();

            for (int i = 0; i < eq.Count; i++)
            {
                items.Add(eq.Slots[i].Item);
            }

            items.Should().Equal(0x4000_0001u);
            eq.MountItem.Should().Be(0, "the unattached mount is not worn");
            eq.Mounted.Should().BeFalse();
        }

        // the per-frame dispatch (MobileAnimationSystem.Update): TakeDirty, then a rebuild only when the record was dirty
        private bool Dispatch(WorldMirror mirror, Entity mobile)
        {
            if (!MobileAnimationSystem.TakeDirty(mobile))
            {
                return false;
            }

            Run(mirror, mobile);

            return true;
        }

        private static List<uint> SlotItems(in EquipmentSlots eq)
        {
            var items = new List<uint>();

            for (int i = 0; i < eq.Count; i++)
            {
                items.Add(eq.Slots[i].Item);
            }

            return items;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Restamp_Or_Detach_Of_A_Worn_Item_Dirties_A_Clean_Record(bool detach)
        {
            // Codex audit P2 (#1): StampChild (the old re-key's re-link, the vendor sort) and DetachChild (the re-key's displaced
            // mobile) change WornLayers; an already-built, clean record must be rebuilt through the normal dirty dispatch
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT);
            Wear(mirror, 0x4000_0002, Layer.Shirt, SHOES);
            Dispatch(mirror, mobile).Should().BeTrue("a new record starts dirty");
            SlotItems(mobile.GetComponent<EquipmentSlots>()).Should().Equal(0x4000_0001u);
            Dispatch(mirror, mobile).Should().BeFalse("built and clean");

            if (detach)
            {
                mirror.DetachChild(0x4000_0001);
            }
            else
            {
                mirror.StampChild(0x4000_0001);
            }

            mirror.Worn(MOBILE, (byte)Layer.Shirt).Should().Be(0x4000_0002);
            Dispatch(mirror, mobile).Should().BeTrue("the worn selection changed");
            SlotItems(mobile.GetComponent<EquipmentSlots>()).Should().Equal(0x4000_0002u);

            // the last one detached: nothing worn, and the slots follow
            mirror.DetachChild(0x4000_0002);

            if (detach)
            {
                Dispatch(mirror, mobile).Should().BeTrue();
                SlotItems(mobile.GetComponent<EquipmentSlots>()).Should().BeEmpty();
            }
            else
            {
                Dispatch(mirror, mobile).Should().BeTrue();
                SlotItems(mobile.GetComponent<EquipmentSlots>()).Should().Equal(new[] { 0x4000_0001u }, "the re-stamped one is still attached");
            }
        }

        [Fact]
        public void Nothing_Worn_Gives_An_Empty_Versioned_Set()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            EquipmentSlots a = Run(mirror, mobile);

            a.Count.Should().Be(0);
            a.LightCount.Should().Be(0);
            a.Mounted.Should().BeFalse();
            a.Hand1.Present.Should().BeFalse();
            a.Version.Should().NotBe(0);

            EquipmentSlots b = Run(mirror, mobile);
            b.Version.Should().BeGreaterThan(a.Version, "a version is never reused");
        }

        [Fact]
        public void A_Worn_Shirt_Becomes_A_Slot_With_Its_Item_Draw_Body_And_Hue()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT, 0x22);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(1);
            EquipSlot slot = eq.Slots[0];
            slot.Item.Should().Be(0x4000_0001);
            slot.Graphic.Should().Be(SHIRT);
            slot.AnimId.Should().Be(0x0101);
            slot.DrawBody.Should().Be(0x0101);
            slot.Hue.Should().Be(0x22);
            slot.Layer.Should().Be((byte)Layer.Shirt);
            slot.Group.Should().Be(0xFF);
            slot.Bits.Should().Be(EquipSlotBits.None);
        }

        [Fact]
        public void A_Partial_Hue_Item_Carries_The_Partial_Bit()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Pants, PANTS);

            Run(mirror, mobile).Slots[0].Bits.Should().Be(EquipSlotBits.PartialHue);
        }

        [Fact]
        public void An_Item_Without_An_Equip_AnimId_Is_Not_Drawn_And_Is_Not_A_Light()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shirt, BARE);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(0);
            eq.LightCount.Should().Be(0);
        }

        [Fact]
        public void A_Light_Without_An_AnimId_On_A_Human_Is_Lit_But_Not_Drawn()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Waist, LAMP);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(0);
            eq.LightCount.Should().Be(1);
            eq.LightItems[0].Should().Be(0x4000_0001);
        }

        [Fact]
        public void A_Light_With_An_AnimId_On_A_Human_Is_Drawn_With_The_Light_Bit()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.OneHanded, TORCH);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(1);
            eq.Slots[0].Bits.Should().Be(EquipSlotBits.Light);
            eq.LightCount.Should().Be(0, "a drawn light is lit from its slot");
        }

        // the view submits one light per eligible layer, so none is dropped past a fixed count (human, AnimID 0 lights)
        [Fact]
        public void Every_Light_Only_Item_Of_A_Human_Is_Kept()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Layer[] layers = { Layer.Waist, Layer.Ring, Layer.Necklace, Layer.Bracelet, Layer.Earrings, Layer.Talisman };
            uint serial = 0x4000_0001;

            foreach (Layer l in layers)
            {
                Wear(mirror, serial++, l, LAMP);
            }

            EquipmentSlots eq = Run(mirror, mobile);

            eq.LightCount.Should().Be((byte)layers.Length);
            eq.Count.Should().Be(0);

            var kept = new HashSet<uint>();

            for (int i = 0; i < eq.LightCount; i++)
            {
                kept.Add(eq.LightItems[i]);
            }

            kept.Should().BeEquivalentTo(new uint[] { 0x4000_0001, 0x4000_0002, 0x4000_0003, 0x4000_0004, 0x4000_0005, 0x4000_0006 });
        }

        // a light-only item records how many drawn slots precede it, so the extract can light in the view's layer order
        [Fact]
        public void A_Light_Only_Item_Records_The_Drawn_Slots_Before_It()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT);
            Wear(mirror, 0x4000_0002, Layer.Waist, LAMP);
            Wear(mirror, 0x4000_0003, Layer.Legs, PANTS);
            Wear(mirror, 0x4000_0004, Layer.Earrings, LAMP);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(2);
            eq.LightCount.Should().Be(2);

            eq.LightBefore[1].Should().BeLessThanOrEqualTo((byte)eq.Count);
            eq.LightBefore[0].Should().BeLessThanOrEqualTo(eq.LightBefore[1], "lights are recorded in paint order");
        }

        // a non-human body lights every light item (any layer), however many
        [Fact]
        public void Every_Light_On_A_Non_Human_Body_Is_Kept()
        {
            WorldMirror mirror = NewMirror(out Entity mobile, HORSE);
            Layer[] layers = { Layer.OneHanded, Layer.TwoHanded, Layer.Waist, Layer.Ring, Layer.Necklace, Layer.Bracelet, Layer.Earrings };
            uint serial = 0x4000_0001;

            foreach (Layer l in layers)
            {
                Wear(mirror, serial++, l, TORCH);
            }

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(0);
            eq.LightCount.Should().BeGreaterThan(4);
            eq.LightCount.Should().BeLessThanOrEqualTo((byte)EquipmentSlots.MAX_LIGHT_ONLY);
        }

        [Fact]
        public void A_Non_Human_Body_Draws_No_Equipment_But_Lights_Its_Light_Items()
        {
            WorldMirror mirror = NewMirror(out Entity mobile, HORSE);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT);
            Wear(mirror, 0x4000_0002, Layer.OneHanded, TORCH);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(0);
            eq.LightCount.Should().Be(1);
            eq.LightItems[0].Should().Be(0x4000_0002);
            eq.Hand1.Present.Should().BeTrue("hands are read before the human test");
        }

        [Fact]
        public void The_Human_State_Flag_Draws_Equipment_On_A_Body_That_Is_Not_In_The_Human_Ranges()
        {
            WorldMirror mirror = NewMirror(out Entity mobile, HORSE);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT);

            Run(mirror, mobile, AnimStateFlags.Human).Count.Should().Be(1);
        }

        [Fact]
        public void A_Dead_Mobile_Does_Not_Draw_Hair_Or_Beard()
        {
            WorldMirror alive = NewMirror(out Entity mobile);
            Wear(alive, 0x4000_0001, Layer.Hair, HAIR);
            Wear(alive, 0x4000_0002, Layer.Beard, HAIR);
            Wear(alive, 0x4000_0003, Layer.Shirt, SHIRT);

            Layers(Run(alive, mobile)).Should().Contain(new[] { Layer.Hair, Layer.Beard, Layer.Shirt });

            WorldMirror dead = NewMirror(out Entity ghost);
            Wear(dead, 0x4000_0001, Layer.Hair, HAIR);
            Wear(dead, 0x4000_0002, Layer.Beard, HAIR);
            Wear(dead, 0x4000_0003, Layer.Shirt, SHIRT);

            Layers(Run(dead, ghost, AnimStateFlags.Dead)).Should().Equal(Layer.Shirt);
        }

        [Fact]
        public void A_Ghost_Body_Counts_As_Dead_Without_The_State_Flag()
        {
            WorldMirror mirror = NewMirror(out Entity mobile, 0x0192);
            Wear(mirror, 0x4000_0001, Layer.Hair, HAIR);

            Run(mirror, mobile).Count.Should().Be(0);
        }

        [Fact]
        public void Shoes_Under_Leg_Armour_Are_Covered()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shoes, SHOES);
            Wear(mirror, 0x4000_0002, Layer.Legs, LEGS);

            Layers(Run(mirror, mobile)).Should().Equal(Layer.Legs);

            WorldMirror bare = NewMirror(out Entity other);
            Wear(bare, 0x4000_0001, Layer.Shoes, SHOES);

            Layers(Run(bare, other)).Should().Equal(Layer.Shoes);
        }

        [Fact]
        public void A_Covered_Light_Is_Skipped_Before_The_Light_Test()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shoes, LAMP);
            Wear(mirror, 0x4000_0002, Layer.Legs, LEGS);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.LightCount.Should().Be(0);
            Layers(eq).Should().Equal(Layer.Legs);
        }

        [Fact]
        public void Hands_Are_Read_From_The_Hand_Layers_Whether_Or_Not_They_Are_Drawn()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.OneHanded, SWORD);
            Wear(mirror, 0x4000_0002, Layer.TwoHanded, TORCH);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Hand1.Should().Be(new HandItem { Present = true, AnimId = 0x0263, IsLight = false });
            eq.Hand2.Should().Be(new HandItem { Present = true, AnimId = 0x0240, IsLight = true });
        }

        [Fact]
        public void A_Hand_Item_Without_An_AnimId_Is_Still_Present_With_AnimId_Zero()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.TwoHanded, LAMP);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Hand2.Should().Be(new HandItem { Present = true, AnimId = 0, IsLight = true });
            eq.Hand1.Present.Should().BeFalse();
            eq.LightCount.Should().Be(1);
        }

        [Fact]
        public void The_Equipment_Conversion_Table_Of_The_Body_Remaps_The_Draw_Body_And_Colour()
        {
            _conv[HUMAN] = new Dictionary<ushort, EquipConvData> { [0x0101] = new EquipConvData(0x0777, 0, 0x0042) };
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT);
            Wear(mirror, 0x4000_0002, Layer.Pants, PANTS);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Count.Should().Be(2);

            for (int i = 0; i < eq.Count; i++)
            {
                EquipSlot slot = eq.Slots[i];

                if (slot.Layer == (byte)Layer.Shirt)
                {
                    slot.DrawBody.Should().Be(0x0777);
                    slot.ConvColor.Should().Be(0x0042);
                    slot.Bits.Should().HaveFlag(EquipSlotBits.Converted);
                    slot.AnimId.Should().Be(0x0101, "the item's own AnimID stays");
                }
                else
                {
                    slot.DrawBody.Should().Be(0x0102);
                    slot.Bits.Should().NotHaveFlag(EquipSlotBits.Converted);
                }
            }
        }

        [Fact]
        public void Gargoyle_Bodies_Remap_The_Draw_Body_Only_From_Client_7000()
        {
            _tiles[SHIRT].AnimID = 0x01D5; // robe, remapped for gargoyles

            WorldMirror mirror = NewMirror(out Entity mobile, 666);
            Wear(mirror, 0x4000_0001, Layer.Shirt, SHIRT);
            Run(mirror, mobile, AnimStateFlags.Human, cv7000: true).Slots[0].DrawBody.Should().Be(0x0156);

            WorldMirror old = NewMirror(out Entity oldMobile, 666);
            Wear(old, 0x4000_0001, Layer.Shirt, SHIRT);
            Run(old, oldMobile, AnimStateFlags.Human, cv7000: false).Slots[0].DrawBody.Should().Be(0x01D5);
        }

        [Fact]
        public void The_Cloak_Anchor_Is_The_Helmets_Place_In_The_Order_Whether_Or_Not_A_Helmet_Is_Worn()
        {
            WorldMirror noHelm = NewMirror(out Entity a);
            Wear(noHelm, 0x4000_0001, Layer.Cloak, CLOAK);
            Wear(noHelm, 0x4000_0002, Layer.OneHanded, SWORD);
            EquipmentSlots without = Run(noHelm, a);

            WorldMirror withHelm = NewMirror(out Entity b);
            Wear(withHelm, 0x4000_0001, Layer.Cloak, CLOAK);
            Wear(withHelm, 0x4000_0002, Layer.OneHanded, SWORD);
            Wear(withHelm, 0x4000_0003, Layer.Helmet, HELM);
            EquipmentSlots with = Run(withHelm, b);

            with.Count.Should().Be((byte)(without.Count + 1));
            without.CloakAnchor.Should().Be(AnimationRules.CloakAnchor(
                BuildOrder(without), LayersOf(without)));
            with.CloakAnchor.Should().BeGreaterThanOrEqualTo(without.CloakAnchor);
        }

        private static byte[] LayersOf(in EquipmentSlots eq)
        {
            var l = new byte[eq.Count];

            for (int i = 0; i < eq.Count; i++)
            {
                l[i] = eq.Slots[i].Layer;
            }

            return l;
        }

        private static Layer[] BuildOrder(in EquipmentSlots eq)
        {
            var key = new ushort[PaperdollOrder.N];
            var raw = new Layer[PaperdollOrder.N];
            var order = new Layer[PaperdollOrder.N];

            // the same key the system uses: the AnimIDs of the hand layers through Legs
            for (int i = 0; i < eq.Count; i++)
            {
                int layer = eq.Slots[i].Layer;

                if (layer >= (int)Layer.OneHanded && layer <= (int)Layer.Legs)
                {
                    key[layer] = eq.Slots[i].AnimId;
                }
            }

            LegacyLayerRulesReference.Build(key, false, raw);
            int n = PaperdollOrder.Filter(raw, false, order);

            return order[..n];
        }

        // ----- the mount -----

        [Fact]
        public void Without_The_Mobile_The_Mirror_Mount_Item_Decides_Mounted()
        {
            WorldMirror mirror = NewMirror(out Entity mobile);
            Wear(mirror, 0x4000_0001, Layer.Mount, MOUNT);

            EquipmentSlots eq = Run(mirror, mobile);

            eq.Mounted.Should().BeTrue();
            eq.MountItem.Should().Be(0x4000_0001);
            eq.MountGraphic.Should().Be(MOUNT);
            eq.MountAnimId.Should().Be(0x00CC);
            eq.Count.Should().Be(0, "the mount is not an equipment slot");
        }

        [Fact]
        public void A_Boat_Tiller_Or_A_Mount_Without_A_Body_Is_Not_Mounted()
        {
            WorldMirror tiller = NewMirror(out Entity a);
            Wear(tiller, 0x4000_0001, Layer.Mount, TILLER);
            EquipmentSlots t = Run(tiller, a);
            t.Mounted.Should().BeFalse();
            t.MountItem.Should().Be(0x4000_0001, "the item is still recorded");

            _tiles[MOUNT].AnimID = 0xFFFF;
            WorldMirror nobody = NewMirror(out Entity b);
            Wear(nobody, 0x4000_0001, Layer.Mount, MOUNT);
            Run(nobody, b).Mounted.Should().BeFalse();
        }

        [Fact]
        public void With_The_Mobile_Mounted_Comes_From_Its_Own_Equipment_List_Not_The_Mirror_Index()
        {
            var world = new World();
            var owner = new Mobile(world, MOBILE);
            var mount = new Item(world) { Serial = 0x4000_0001, Graphic = MOUNT, Layer = Layer.Mount };
            owner.Preview = new PreviewEquipment();
            owner.Preview.Wear(mount);

            // the mirror holds no mount (not yet synced): the Mobile decides
            WorldMirror mirror = NewMirror(out Entity mobile);
            Run(mirror, mobile, owner: owner).Mounted.Should().BeTrue();

            // the other way round: the mirror holds a mount the Mobile doesn't wear (an orphan that outlived it)
            WorldMirror orphan = NewMirror(out Entity ghost);
            Wear(orphan, 0x4000_0009, Layer.Mount, MOUNT);
            var bare = new Mobile(world, MOBILE + 1);
            EquipmentSlots eq = Run(orphan, ghost, owner: bare);

            eq.Mounted.Should().BeFalse();
            eq.MountItem.Should().Be(0x4000_0009, "the mirror's mount item is still recorded");

            world.Clear();
        }

        [Fact]
        public void IsMounted_Reads_The_First_Live_Mount_Layer_Item_And_Its_Tiledata_AnimId()
        {
            var world = new World();
            var m = new Mobile(world, MOBILE);

            MobileAnimationSystem.IsMounted(m, _tiles).Should().BeFalse("no mount item");

            var tiller = new Item(world) { Serial = 0x4000_0001, Graphic = TILLER, Layer = Layer.Mount };
            m.Preview = new PreviewEquipment();
            m.Preview.Wear(tiller);
            MobileAnimationSystem.IsMounted(m, _tiles).Should().BeFalse("a boat's tiller");

            var m2 = new Mobile(world, MOBILE + 1);
            var horse = new Item(world) { Serial = 0x4000_0002, Graphic = MOUNT, Layer = Layer.Mount };
            m2.Preview = new PreviewEquipment();
            m2.Preview.Wear(horse);
            MobileAnimationSystem.IsMounted(m2, _tiles).Should().BeTrue();

            _tiles[MOUNT].AnimID = 0xFFFF;
            MobileAnimationSystem.IsMounted(m2, _tiles).Should().BeFalse("tiledata gives the mount no body");

            _tiles[MOUNT].AnimID = 0;
            MobileAnimationSystem.IsMounted(m2, _tiles).Should().BeTrue("the Mounts table gives the body");

            horse.Graphic = 0x7FFF;
            MobileAnimationSystem.IsMounted(m2, _tiles).Should().BeTrue("a graphic past the tiledata reads AnimID 0; the item graphic is the body");

            world.Clear();
        }

        [Fact]
        public void A_Mount_Item_Serial_Of_Zero_Is_Not_Mounted()
        {
            var world = new World();
            var m = new Mobile(world, MOBILE);
            m.Preview = new PreviewEquipment();
            m.Preview.Wear(new Item(world) { Serial = 0, Graphic = MOUNT, Layer = Layer.Mount });

            MobileAnimationSystem.IsMounted(m, _tiles).Should().BeFalse();

            world.Clear();
        }
    }
}
