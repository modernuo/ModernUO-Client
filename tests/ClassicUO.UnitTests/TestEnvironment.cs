using System.IO;
using System.Runtime.CompilerServices;
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
    }

    // MusicStream and Mp3Decoder tests: the static OpenMemoryOverride hook and the global LiveInputAllocations count are
    // process-wide, so every test that opens a decoder runs in this collection, alone.
    [CollectionDefinition("Mp3DecoderGlobals", DisableParallelization = true)]
    public class Mp3DecoderGlobalsCollection { }
}
