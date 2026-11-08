using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace ClassicUO.UnitTests
{
    // Process-wide statics that tests share, made deterministic once for the whole run.
    internal static class TestEnvironment
    {
        // SeasonManager's static constructor reads (and, if missing, writes) <current directory>/Data/Client/seasons.txt but
        // does not create the folder: whichever test touched a Land, Static or Multi first would fail with a missing file
        // if nothing had created Data/Client yet (it exists in the bin folder only as a side effect of other tests).
        [ModuleInitializer]
        internal static void CreateClientDataFolder()
        {
            Directory.CreateDirectory(Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client"));
        }

        // Friflo builds a component type's metadata through SchemaType.GetBlittableType, which fills a static Dictionary
        // without a lock. Two paths do that once per process: EcsSchema.Register (the NativeAOT registration, under its own
        // lock; every WorldMirror calls it) and the reflection schema behind the first EntityStore (a static constructor,
        // under the runtime's type lock). The client runs both on its main thread, in that order; the tests ran them on two
        // threads at once (a WorldMirror test beside EcsReadTests, which creates EntityStores directly), and the losing
        // Add threw "An item with the same key has already been added". Both run here, before any test, in the client's order.
        [ModuleInitializer]
        internal static void CreateEcsSchemas()
        {
            ClassicUO.Ecs.EcsSchema.Register();
            Friflo.Engine.ECS.EntityStore.GetEntitySchema();
        }

        // The client gets a 1 ms system timer from SDL's native init; the test host does not, so on Windows every short
        // Thread.Sleep or timed wait rounds up to the 15.6 ms default tick and the timing-sensitive tests (DecodeBudgetTests,
        // FirstSightDecoderTests, ChunkLoadingTests) go flaky under load. The period lasts until the process exits.
        [ModuleInitializer]
        internal static void RaiseTimerResolution()
        {
            if (OperatingSystem.IsWindows())
            {
                _ = TimeBeginPeriod(1);
            }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);
    }

    // MusicStream and Mp3Decoder tests: the static OpenMemoryOverride hook and the global LiveInputAllocations count are
    // process-wide, so every test that opens a decoder runs in this collection, alone.
    [CollectionDefinition("Mp3DecoderGlobals", DisableParallelization = true)]
    public class Mp3DecoderGlobalsCollection { }
}
