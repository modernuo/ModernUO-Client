// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Utility
{
    /// <summary>
    ///     The client's shared random numbers (main thread only, like the System.Random it replaces). The generator is
    ///     xoshiro256**, the algorithm an unseeded System.Random uses on 64-bit .NET, with the same reductions, kept here so its
    ///     state can be saved and put back: E12 (CUO_GPU_ITEM_CHECK) runs a check frame's probe pass between
    ///     <see cref="Save"/> and <see cref="Restore"/>, so the rolls the probe takes never change the frame that is drawn.
    /// </summary>
    public static class RandomHelper
    {
        /// <summary>The generator's whole state.</summary>
        public struct State : IEquatable<State>
        {
            internal ulong S0, S1, S2, S3;

            public readonly bool Equals(State other) => S0 == other.S0 && S1 == other.S1 && S2 == other.S2 && S3 == other.S3;
            public override readonly bool Equals(object obj) => obj is State s && Equals(s);
            public override readonly int GetHashCode() => HashCode.Combine(S0, S1, S2, S3);
        }

        private static State _state = Seed();

        private static State Seed()
        {
            State s;

            do
            {
                s.S0 = (ulong)Random.Shared.NextInt64();
                s.S1 = (ulong)Random.Shared.NextInt64();
                s.S2 = (ulong)Random.Shared.NextInt64();
                s.S3 = (ulong)Random.Shared.NextInt64();
            }
            while ((s.S0 | s.S1 | s.S2 | s.S3) == 0);

            return s;
        }

        /// <summary>The generator's state now.</summary>
        public static State Save() => _state;

        /// <summary>Puts the generator back to <paramref name="state"/> (from <see cref="Save"/>).</summary>
        public static void Restore(in State state) => _state = state;

        private static ulong NextUInt64()
        {
            ulong s0 = _state.S0, s1 = _state.S1, s2 = _state.S2, s3 = _state.S3;
            ulong result = RotateLeft(s1 * 5, 7) * 9;
            ulong t = s1 << 17;
            s2 ^= s0;
            s3 ^= s1;
            s1 ^= s2;
            s0 ^= s3;
            s2 ^= t;
            s3 = RotateLeft(s3, 45);
            _state.S0 = s0;
            _state.S1 = s1;
            _state.S2 = s2;
            _state.S3 = s3;

            return result;
        }

        private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

        private static uint NextUInt32() => (uint)(NextUInt64() >> 32);

        // [0, maxValue): Lemire's multiply-shift with rejection, as System.Random's xoshiro implementation
        private static uint NextUInt32(uint maxValue)
        {
            ulong product = (ulong)maxValue * NextUInt32();
            uint low = (uint)product;

            if (low < maxValue)
            {
                uint remainder = (0u - maxValue) % maxValue;

                while (low < remainder)
                {
                    product = (ulong)maxValue * NextUInt32();
                    low = (uint)product;
                }
            }

            return (uint)(product >> 32);
        }

        private static int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(minValue));
            }

            return (int)NextUInt32((uint)(maxValue - minValue)) + minValue;
        }

        /// <summary>
        ///     Returns a random number between low and high, inclusive of both low and high.
        /// </summary>
        public static int GetValue(int low, int high)
        {
            return Next(low, high + 1);
        }

        /// <summary>
        ///     Returns a non-negative random integer.
        /// </summary>
        public static int GetValue()
        {
            while (true)
            {
                ulong result = NextUInt64() >> 33;

                if (result != int.MaxValue)
                {
                    return (int)result;
                }
            }
        }

        public static int RandomList(params int[] list)
        {
            return list[(int)NextUInt32((uint)list.Length)];
        }

        public static bool RandomBool()
        {
            return (NextUInt64() >> 11) * (1.0 / (1ul << 53)) >= 0.5;
        }
    }
}
