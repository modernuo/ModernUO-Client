using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T3a: CUO_EQUIP_READ_CHECK. Every FindItemByLayer call on a mobile compares the child list's first item on the layer with
    // the ECS's worn item (WornLayers). A world changed only through the funnel checks clean; each deliberate divergence is
    // reported, with the switch off and on.
    public class WorldStateEquipmentCheckTests
    {
        private const uint MOB = 0x0000_3900, SHIRT = 0x4000_7904, HELM = 0x4000_7905, HELM2 = 0x4000_7960, WEAR = 0x4000_790A;

        internal static (World World, List<string> Reports) Build(bool read)
        {
            World world = WorldStateContentsTests.Build();
            var reports = new List<string>();
            world.State.ReadEquipmentOverride = read;
            world.State.EquipCheckEnabled = true;
            world.State.EquipMismatchObserver = reports.Add;

            return (world, reports);
        }

        private static void ReadAll(World world)
        {
            foreach (Mobile m in world.Mobiles.Values.ToList())
            {
                for (int l = 0; l <= 0x1E; l++)
                {
                    m.FindItemByLayer((Layer)l);
                }
            }
        }

        public static IEnumerable<object[]> Cases() =>
            new[] { false, true }.SelectMany
            (
                read => WorldStateCheckTests.Changes().Select(c => new[] { c[0], (object)read })
            );

        [Theory]
        [MemberData(nameof(Cases))]
        public void A_World_Changed_Through_The_Funnel_Checks_Clean(WorldStateContentsTests.Change change, bool read)
        {
            if (!WorldState.EquipCheckAvailable)
            {
                return;
            }

            (World world, List<string> reports) = Build(read);
            ReadAll(world);

            WorldStateContentsTests.Apply(world, change);
            ReadAll(world);

            reports.Should().BeEmpty();
            world.State.EquipMismatches.Should().Be(0);
            world.State.EquipReads.Should().BeGreaterThan(0);
        }

        public enum Divergence
        {
            ListReorder, LayerInCacheOnly, MirrorOnlyDetach, MirrorOnlyRemove, ListOnlyUnlink, MirrorOnlyRestamp
        }

        [Theory]
        [InlineData(Divergence.ListReorder, false)]
        [InlineData(Divergence.ListReorder, true)]
        [InlineData(Divergence.LayerInCacheOnly, false)]
        [InlineData(Divergence.LayerInCacheOnly, true)]
        [InlineData(Divergence.MirrorOnlyDetach, false)]
        [InlineData(Divergence.MirrorOnlyDetach, true)]
        [InlineData(Divergence.MirrorOnlyRemove, false)]
        [InlineData(Divergence.MirrorOnlyRemove, true)]
        [InlineData(Divergence.ListOnlyUnlink, false)]
        [InlineData(Divergence.ListOnlyUnlink, true)]
        [InlineData(Divergence.MirrorOnlyRestamp, false)]
        [InlineData(Divergence.MirrorOnlyRestamp, true)]
        public void A_Divergence_Is_Reported(Divergence divergence, bool read)
        {
            if (!WorldState.EquipCheckAvailable)
            {
                return;
            }

            (World world, List<string> reports) = Build(read);
            Layer layer = Diverge(world, divergence);

            Item found = world.Mobiles.Get(MOB).FindItemByLayer(layer);

            reports.Should().ContainSingle().Which.Should().Contain("0x00003900").And.Contain($"layer {layer}");
            world.State.EquipMismatches.Should().Be(1);

            // the switch picks the source the reader gets
            uint expected = read ? world.Ecs.Mirror.Worn(MOB, (byte)layer) : WornLayersTests.ListFind(world.Mobiles.Get(MOB), layer)?.Serial ?? 0;
            (found?.Serial ?? 0).Should().Be(expected);
        }

        [Fact]
        public void Off_Nothing_Is_Compared()
        {
            if (!WorldState.EquipCheckAvailable)
            {
                return;
            }

            (World world, List<string> reports) = Build(false);
            world.State.EquipCheckEnabled = false;
            Diverge(world, Divergence.MirrorOnlyDetach);

            world.Mobiles.Get(MOB).FindItemByLayer(Layer.Helmet).Serial.Should().Be(HELM, "the list");
            reports.Should().BeEmpty();
            world.State.EquipReads.Should().Be(0);
        }

        // a change of one side behind the funnel; returns the layer it shows on
        internal static Layer Diverge(World world, Divergence divergence)
        {
            Mobile mob = world.Mobiles.Get(MOB);

            switch (divergence)
            {
                case Divergence.ListReorder:
                    // a second helmet through the funnel, then the first re-pushed in the list only
                    world.Writer.Equip(HELM2, 0x1409, Layer.Helmet, MOB, 0, out _, out _);
                    mob.Remove(world.Items.Get(HELM));
                    mob.PushToBack(world.Items.Get(HELM));

                    return Layer.Helmet;

                case Divergence.LayerInCacheOnly:
                    world.Items.Get(SHIRT).Layer = Layer.Earrings;

                    return Layer.Earrings;

                case Divergence.MirrorOnlyDetach:
                    world.Ecs.Mirror.DetachChild(HELM);

                    return Layer.Helmet;

                case Divergence.MirrorOnlyRemove:
                    world.Ecs.Mirror.Remove(WEAR);

                    return Layer.Robe;

                case Divergence.ListOnlyUnlink:
                    mob.Remove(world.Items.Get(HELM));

                    return Layer.Helmet;

                default:
                    // a second helmet through the funnel, then the first re-stamped in the mirror only (to the back)
                    world.Writer.Equip(HELM2, 0x1409, Layer.Helmet, MOB, 0, out _, out _);
                    world.Ecs.Mirror.StampChild(HELM);

                    return Layer.Helmet;
            }
        }
    }
}
