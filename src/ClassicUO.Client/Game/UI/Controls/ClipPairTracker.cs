// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.UI.Controls
{
    /// <summary>
    /// Pairs the begin and end <see cref="ScissorControl"/>s of a gump. A begin whose ClipBegin returned false (zero size, or
    /// nothing left after clipping) pushed no scissor, so its matching end must not pop one.
    /// </summary>
    internal static class ClipPairTracker
    {
        private static int _skipped;

        public static void Begin(bool pushed)
        {
            if (!pushed)
            {
                _skipped++;
            }
        }

        /// <summary>True when the end control should call ClipEnd.</summary>
        public static bool End()
        {
            if (_skipped > 0)
            {
                _skipped--;

                return false;
            }

            return true;
        }

        public static void Reset() => _skipped = 0;
    }
}
