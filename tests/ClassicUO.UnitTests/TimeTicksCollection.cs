using Xunit;

namespace ClassicUO.UnitTests
{
    /// <summary>
    /// Time.Ticks is process-wide: the classes that set it, or read it while another class may set it, never run in parallel
    /// (a WeatherSkippedDrawTests / MobileClockTests failure under load came from a parallel class moving the clock).
    /// </summary>
    [CollectionDefinition(nameof(TimeTicksCollection), DisableParallelization = true)]
    public sealed class TimeTicksCollection { }
}
