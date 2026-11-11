using System.Reflection;
using ClassicUO.Game;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests
{
    // Weather.SimulateSkippedDraw (skip_draw_when_minimized): expired weather ends by time, whatever its particle count.
    // Generate needs the client's camera, so the state is set through the fields.
    [Collection(nameof(TimeTicksCollection))]
    public class WeatherSkippedDrawTests
    {
        private static void Set(Weather w, string field, object value) =>
            typeof(Weather).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, value);

        private static void SetProp(Weather w, string name, object value) =>
            typeof(Weather).GetProperty(name).GetSetMethod(true).Invoke(w, new[] { value });

        [Fact]
        public void Expired_Weather_Ends_Even_With_Particles_Left()
        {
            uint ticks = Time.Ticks;

            try
            {
                Time.Ticks = 1_000_000;
                var w = new Weather(null);
                SetProp(w, "Type", WeatherType.WT_SNOW);
                SetProp(w, "Count", (byte)40);
                SetProp(w, "CurrentCount", (byte)40);
                SetProp(w, "CurrentWeather", WeatherType.WT_SNOW);
                Set(w, "_timer", 999_000u);

                w.SimulateSkippedDraw();

                w.CurrentCount.Should().Be(0);
                w.Count.Should().Be(0);
                w.CurrentWeather.Should().BeNull();
            }
            finally
            {
                Time.Ticks = ticks;
            }
        }

        [Fact]
        public void Running_Weather_Is_Left_Alone()
        {
            uint ticks = Time.Ticks;

            try
            {
                Time.Ticks = 1_000_000;
                var w = new Weather(null);
                SetProp(w, "Type", WeatherType.WT_INVALID_0);
                SetProp(w, "CurrentCount", (byte)7);
                Set(w, "_timer", 2_000_000u);

                w.SimulateSkippedDraw();

                w.CurrentCount.Should().Be(7);
            }
            finally
            {
                Time.Ticks = ticks;
            }
        }
    }
}
