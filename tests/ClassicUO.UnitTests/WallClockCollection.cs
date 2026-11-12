using Xunit;

namespace ClassicUO.UnitTests
{
    /// <summary>
    /// Classes that assert wall-clock bounds (retry budgets, a helper thread that must run within one) run without other
    /// test classes beside them: in the parallel suite on a loaded machine the CPU and thread scheduling are contended and
    /// the bounds fail although the code is right (AtomicFileTests passed alone and failed in the suite).
    /// </summary>
    [CollectionDefinition(nameof(WallClockCollection), DisableParallelization = true)]
    public sealed class WallClockCollection { }
}
