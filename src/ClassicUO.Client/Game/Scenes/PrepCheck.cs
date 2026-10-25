using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    // CUO_PREP_CHECK=1: verifies the world prepare's shortcuts against the work they skip, logging every 5 s:
    //   positions: a walked object whose screen position was kept (camera generation) is where a recompute would put it;
    //   item picks: an item the picking rectangle rejected is not hit by the pixel test either.
    internal static class PrepCheck
    {
        public static readonly bool On = System.Environment.GetEnvironmentVariable("CUO_PREP_CHECK") == "1";

        public static long PositionsKept, PositionsWrong, PicksRejected, PicksWrong;
        public static string Sample;
        private static long _at;

        public static void Report()
        {
            long now = Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;
            }
            else if (now - _at >= Stopwatch.Frequency * 5)
            {
                Log.Info($"[prep-check] positions kept {PositionsKept}, wrong {PositionsWrong}; item picks rejected {PicksRejected}, wrong {PicksWrong}" +
                         (Sample != null ? $"; first: {Sample}" : ""));
                _at = now;
                PositionsKept = PositionsWrong = PicksRejected = PicksWrong = 0;
                Sample = null;
            }
        }
    }
}
