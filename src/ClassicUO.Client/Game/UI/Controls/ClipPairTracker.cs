// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.UI.Controls
{
    /// <summary>
    /// Pairs the begin and end <see cref="ScissorControl"/>s of a gump. A begin whose ClipBegin returned false (zero size, or
    /// nothing left after clipping) pushed no scissor, so its matching end must not pop one.
    /// </summary>
    internal static class ClipPairTracker
    {
        // one entry per open begin, innermost last: whether its ClipBegin pushed a scissor. Nested pairs keep their own
        // result, so a pushed inner pair inside a skipped outer one still pops its scissor. Deeper than the array: assume pushed.
        private static readonly bool[] _pushed = new bool[32];
        private static int _depth;

        public static void Begin(bool pushed)
        {
            if (_depth < _pushed.Length)
            {
                _pushed[_depth] = pushed;
            }

            _depth++;
        }

        /// <summary>True when the end control should call ClipEnd (false for a skipped begin, or an end with no open begin).</summary>
        public static bool End()
        {
            if (_depth == 0)
            {
                return false;
            }

            _depth--;

            return _depth >= _pushed.Length || _pushed[_depth];
        }

        public static void Reset() => _depth = 0;
    }
}
