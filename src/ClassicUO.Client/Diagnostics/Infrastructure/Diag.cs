#if CUO_DEVTOOLS
// Shared mismatch logging for contained checkers; each caller owns its switch/env control.
// Lifetime: LONG-LIVED infrastructure; no per-frame state or release stub.
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.DevTools;

internal static class Diag
{
    [Conditional("CUO_DEVTOOLS")]
    public static void Report(string checker, string detail)
    {
        Log.Warn($"[{checker}] {detail}");
    }
}
#endif
