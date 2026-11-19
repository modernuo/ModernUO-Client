using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // Runs alone: the mobile frame and worn cache pools are process-wide statics.
    [CollectionDefinition("CachePool", DisableParallelization = true)]
    public class CachePoolCollection
    {
    }
}
