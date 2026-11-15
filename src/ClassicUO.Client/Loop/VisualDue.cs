// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;

namespace ClassicUO.Loop
{
    /// <summary>What a scheduled or continuous visual change comes from (CUO_ANIM_DUE_CHECK names it in a mismatch).</summary>
    [Flags]
    internal enum VisualSource : uint
    {
        None = 0,
        MobileAnim = 1 << 0,
        MobileIdle = 1 << 1,
        MobileBusy = 1 << 2,
        ItemAnim = 1 << 3,
        StaticAnim = 1 << 4,
        Effects = 1 << 5,
        WorldText = 1 << 6,
        Weather = 1 << 7,
        Lights = 1 << 8,
        Cursor = 1 << 9,
        Drag = 1 << 10,
        Caret = 1 << 11,
        Gump = 1 << 12,
        Tooltip = 1 << 13,
        Scene = 1 << 14,
        Motion = 1 << 15,
        Other = 1 << 16,
        Driver = 1 << 17
    }

    /// <summary>Why present_on_change has a change pending (CUO_ANIM_DUE_CHECK logs it with a mismatch run).</summary>
    [Flags]
    internal enum ChangeReason : ushort
    {
        None = 0,
        Wake = 1 << 0,
        Net = 1 << 1,
        Connection = 1 << 2,
        SdlEvent = 1 << 3,
        Actions = 1 << 4,
        Motion = 1 << 5,
        Effects = 1 << 6,
        Continuous = 1 << 7,
        Due = 1 << 8,
        Carried = 1 << 9
    }

    /// <summary>
    /// present_on_change_anim_due (evloop-4): the next visual change that no packet or input causes. Sources report a due game
    /// tick (the change happens in the first Update with <c>Time.Ticks</c> past it) or that they change every frame while
    /// active. Two sets: the Update set is cleared at each Update start and filled by the Update walks; the Draw set is cleared
    /// at each Draw start and filled while drawing (state only the draw evaluates), so it describes the last drawn frame until
    /// the next draw. The loop reads the union. Statics, no allocation.
    /// </summary>
    internal static class VisualDue
    {
        private static long _updateTick = long.MaxValue, _drawTick = long.MaxValue;
        private static VisualSource _updateSource, _drawSource, _updateNow, _drawNow;
        private static bool _drawing;

        /// <summary>
        /// Whether the loop reads the signal this Update (present_on_change_anim_due or CUO_ANIM_DUE_CHECK). Sources whose due
        /// time costs a lookup (corpse frames, the due mobiles' records) report only then; the cheap ones always report.
        /// </summary>
        public static bool Tracking;

        /// <summary>The earliest due game tick of both sets (<see cref="long.MaxValue"/>: none).</summary>
        public static long NextTick => Math.Min(_updateTick, _drawTick);

        /// <summary>The source of <see cref="NextTick"/>.</summary>
        public static VisualSource NextSource => _updateTick <= _drawTick ? _updateSource : _drawSource;

        /// <summary>Sources that change every frame while active (both sets).</summary>
        public static VisualSource Continuous => _updateNow | _drawNow;

        public static void BeginUpdate()
        {
            _drawing = false;
            _updateTick = long.MaxValue;
            _updateSource = _updateNow = VisualSource.None;
        }

        public static void BeginDraw()
        {
            _drawing = true;
            _drawTick = long.MaxValue;
            _drawSource = _drawNow = VisualSource.None;
        }

        public static void EndDraw() => _drawing = false;

        /// <summary>A visual change once <c>Time.Ticks</c> is past <paramref name="tick"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void At(long tick, VisualSource source)
        {
            if (_drawing)
            {
                if (tick < _drawTick)
                {
                    _drawTick = tick;
                    _drawSource = source;
                }
            }
            else if (tick < _updateTick)
            {
                _updateTick = tick;
                _updateSource = source;
            }
        }

        /// <summary>A source that changes every frame while active (or whose next change is unknown): present at display rate.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Now(VisualSource source)
        {
            if (_drawing)
            {
                _drawNow |= source;
            }
            else
            {
                _updateNow |= source;
            }
        }

        /// <summary>Tests: clears both sets.</summary>
        internal static void Reset()
        {
            BeginDraw();
            BeginUpdate();
        }
    }
}
