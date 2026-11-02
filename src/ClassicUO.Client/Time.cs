// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO
{
    internal static class Time
    {
        public static uint Ticks;
        public static float Delta;

        /// <summary>Update calls so far: a key for values taken once per frame.</summary>
        public static uint Frame;
    }
}
