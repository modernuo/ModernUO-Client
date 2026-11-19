using ClassicUO.Loop;
using Xunit;

namespace ClassicUO.UnitTests.Loop;

public class FpsDisplayTests
{
    // The player-facing counter: the display rate while the display-rate cap (present on change) is in force, an fps cap below
    // it shows the cap; otherwise (vsync, an fps cap path, an inactive window, unknown refresh) the drawn rate.
    [Theory]
    [InlineData(2u, true, 143.998, 0, 144u)]
    [InlineData(2u, true, 60.0, 0, 60u)]
    [InlineData(2u, true, 144.0, 30, 30u)]
    [InlineData(2u, true, 60.0, 240, 60u)]
    [InlineData(143u, false, 144.0, 0, 143u)]
    [InlineData(10u, false, 144.0, 0, 10u)]
    [InlineData(2u, true, 0.0, 0, 2u)]
    [InlineData(2u, true, double.NaN, 0, 2u)]
    public void ShownRateIsTheDisplayRateWhileCapped(uint drawn, bool capActive, double hz, int fpsCap, uint expected)
        => Assert.Equal(expected, FpsDisplay.Shown(drawn, capActive, hz, fpsCap));

    [Theory]
    [InlineData(4.0, 250u)]
    [InlineData(16.6667, 60u)]
    [InlineData(0.0, 0u)]
    [InlineData(double.NaN, 0u)]
    [InlineData(-1.0, 0u)]
    public void PossibleRateFromFrameBuildTime(double ms, uint expected)
        => Assert.Equal(expected, FpsDisplay.Possible(ms));
}
