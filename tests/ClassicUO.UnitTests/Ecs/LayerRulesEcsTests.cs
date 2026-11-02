using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;
using Entity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    [CollectionDefinition(nameof(LayerRulesStateCollection), DisableParallelization = true)]
    public class LayerRulesStateCollection { }

    // T3b: the ECS side of the layering rules through MobileAnimationSystem.Rebuild and AnimationRules.PaintOrder (what
    // MobileSpriteExtract draws): the default data gives the former hard-coded slot order (LegacyLayerRulesReference) for
    // every facing; custom direction and remap rules reach the GPU path. LayerRuleEval's rules are static, so this runs
    // alone.
    [Collection(nameof(LayerRulesStateCollection))]
    public class LayerRulesEcsTests : IDisposable
    {
        private const uint MOBILE = 0x0000_2000;
        private const ushort HUMAN = 0x0190;
        private const ushort SHIRT = 0x1100, CLOAK = 0x1110, HELM = 0x1111, ROBE = 0x1112, SWORD = 0x1105;

        private readonly StaticTiles[] _tiles = new StaticTiles[0x4000 + 0x100];
        private readonly Dictionary<ushort, Dictionary<ushort, EquipConvData>> _conv = new();

        private readonly ITestOutputHelper _out;

        public LayerRulesEcsTests(ITestOutputHelper output)
        {
            _out = output;
            Tile(SHIRT, 0x0101);
            Tile(CLOAK, 0x0106);
            Tile(HELM, 0x0107);
            Tile(ROBE, 0x01D5); // a gargoyle-remapped robe AnimID
            Tile(SWORD, 0x0263);
            LayerRuleEval.Cv7000Override = true;
        }

        public void Dispose()
        {
            LayerRuleEval.SetRules(null);
            LayerRuleEval.Cv7000Override = null;
        }

        private void Tile(ushort graphic, ushort animId) => _tiles[graphic] = new StaticTiles(0, 0, 0, 0, animId, 0, 0, 0, "");

        private EquipmentSlots Build()
        {
            var mirror = new WorldMirror();
            Entity mobile = mirror.UpsertMobile(MOBILE, HUMAN, 0, 10, 10, 0, 0, 0, false);
            uint s = 0x4000_0100;

            foreach (var (layer, g) in new[] { (Layer.Shirt, SHIRT), (Layer.Cloak, CLOAK), (Layer.Helmet, HELM), (Layer.OneHanded, SWORD) })
            {
                mirror.UpsertItem(s++, g, 0, 0, 0, 0, 0, 0, 1, MOBILE, (byte)layer, false, attach: true);
            }

            var sys = new MobileAnimationSystem(mirror);
            var st = new AnimationState { Flags = AnimStateFlags.Human };
            sys.Rebuild(mobile, in st, null, _tiles, _conv, true);

            return mobile.GetComponent<EquipmentSlots>();
        }

        // the outfit Build wears, as the order key (AnimIDs by layer)
        private static ushort[] Key()
        {
            var key = new ushort[PaperdollOrder.N];
            key[(int)Layer.Shirt] = 0x101;
            key[(int)Layer.Cloak] = 0x106;
            key[(int)Layer.Helmet] = 0x107;
            key[(int)Layer.OneHanded] = 0x263;
            return key;
        }

        // the former hard-coded world order for the facing (PaperdollOrder.Build + Filter + ApplyDirectionCloak) on the
        // drawn layers: nothing in Build's outfit is covered, so all four are drawn
        private static List<Layer> Reference(byte facing)
        {
            ushort[] key = Key();
            Span<Layer> raw = stackalloc Layer[PaperdollOrder.N];
            Span<Layer> full = stackalloc Layer[PaperdollOrder.N];
            LegacyLayerRulesReference.Build(key, false, raw);
            int n = LegacyLayerRulesReference.ApplyDirectionCloak(full, PaperdollOrder.Filter(raw, false, full), facing);
            var l = new List<Layer>();
            for (int i = 0; i < n; i++) if (key[(int)full[i]] != 0) l.Add(full[i]);
            return l;
        }

        private static List<Layer> Paint(in EquipmentSlots eq, byte facing)
        {
            Span<byte> order = stackalloc byte[EquipmentSlots.MAX];
            int n = AnimationRules.PaintOrder(in eq, facing, order);
            var l = new List<Layer>();
            for (int i = 0; i < n; i++) l.Add((Layer)eq.Slots[order[i]].Layer);
            return l;
        }

        // the default rules with one change
        private static LayerRules Custom(Action<LayerRule[], LayerCond[]> change)
        {
            LayerRules a = LayerRulesDefault.Create();
            var rules = (LayerRule[])a.Rules.Clone();
            var conds = (LayerCond[])a.Conds.Clone();
            change(rules, conds);
            return new LayerRules(a.Bodies, a.Tables, rules, conds, a.Ranges);
        }

        private static LayerRules NorthCloakAtTheBottom() => Custom((r, c) =>
        {
            int k = Array.FindIndex(r, x => x.Kind == (byte)LayerRuleKind.DirMove && x.Value == 1);
            r[k].Op = (byte)LayerDirOp.Bottom;
        });

        [Fact]
        public void Default_Data_Gives_The_Hard_Coded_Slot_Order_For_Every_Facing()
        {
            EquipmentSlots data = Build();
            data.DirOrder.Should().NotBe(0);

            for (byte f = 0; f < 16; f++)
            {
                Paint(data, f).Should().Equal(Reference(f), $"facing {f}");
            }

            Paint(data, 0).Last().Should().Be(Layer.Cloak);
            Paint(data, 3).First().Should().Be(Layer.Cloak);
        }

        [Fact]
        public void A_Custom_Direction_Rule_Reaches_The_Gpu_Slot_Order()
        {
            LayerRuleEval.SetRules(NorthCloakAtTheBottom());

            EquipmentSlots data = Build();
            List<Layer> north = Paint(data, 0);
            north.First().Should().Be(Layer.Cloak);
            Paint(data, 1).Should().Equal(Reference(1), "the other facings are unchanged");

            // and MobileView's order (LayerRuleEval.BuildInWorld) agrees on the drawn layers
            ushort[] key = Key();
            Span<Layer> legacy = stackalloc Layer[PaperdollOrder.N];
            int lc = LayerRuleEval.BuildInWorld(key, LayerRuleEval.Subject(HUMAN, false, false), 0, legacy);
            legacy.Slice(0, lc).ToArray().Where(north.Contains).Should().Equal(north);
        }

        [Fact]
        public void A_Remap_Whose_Conditions_Match_A_Human_Is_Applied()
        {
            // the robe remap 0x1D5 -> 0x156 keyed on the client version instead of the gargoyle draw flag
            LayerRuleEval.SetRules(Custom((r, c) =>
            {
                int k = Array.FindIndex(r, x => x.Kind == (byte)LayerRuleKind.Remap && x.Value == 0x1D5);
                c[r[k].CondStart].Layer = LayerRulesConst.FlagCv7000;
            }));

            var mirror = new WorldMirror();
            Entity mobile = mirror.UpsertMobile(MOBILE, HUMAN, 0, 10, 10, 0, 0, 0, false);
            mirror.UpsertItem(0x4000_0200, ROBE, 0, 0, 0, 0, 0, 0, 1, MOBILE, (byte)Layer.Robe, false, attach: true);
            var sys = new MobileAnimationSystem(mirror);
            var st = new AnimationState { Flags = AnimStateFlags.Human };

            LegacyLayerRulesReference.FixGargoyleEquipment(0x1D5).Should().Be(0x156, "the former hard-coded remap, for a gargoyle draw only");
            sys.Rebuild(mobile, in st, null, _tiles, _conv, true);
            mobile.GetComponent<EquipmentSlots>().Slots[0].DrawBody.Should().Be(0x156, "the data's condition decides");
        }
    }
}
