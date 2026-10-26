using ClassicUO.Assets;
using ClassicUO.Game;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // EcsParity adds the ECS lights after the view's; GameScene.AddLight stops at MAX_LIGHTS_DATA_INDEX_COUNT, so when the
    // view's own light filled the list the ECS side is refused by the checker's doubling alone (the E4 "lights-mismatched"
    // at dusk, view=1 ecs=0). Those objects are skipped.
    public class ParityLightsFitTests
    {
        private const int MAX = LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT;

        [Fact]
        public void EmptyList_Fits() => EcsParity.LightsFit(0, 0).Should().BeTrue();

        [Fact]
        public void ViewAddedNothing_FitsEvenWhenFull() => EcsParity.LightsFit(MAX, MAX).Should().BeTrue();

        [Fact]
        public void ViewLightLeavesRoomForTheEcsOne_Fits() => EcsParity.LightsFit(MAX - 2, MAX - 1).Should().BeTrue();

        [Fact]
        public void ViewLightTookTheLastSlot_DoesNotFit() => EcsParity.LightsFit(MAX - 1, MAX).Should().BeFalse();

        [Fact]
        public void TwoViewLightsOnlyOneSlotLeft_DoesNotFit() => EcsParity.LightsFit(MAX - 2, MAX).Should().BeFalse();
    }
}
