// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// E6 test tooling (DevBench plan entry <c>effects:</c>, driver command <c>fx</c>): client-side graphic effects in bulk, to
    /// measure and check the Gpu world prepare with effects in view. Nothing is sent to the server, so there is nothing to
    /// clean up there.
    /// <para><c>effects:N:graphic[:blend[:radius[:z[:kind]]]]</c> spawns N effects on random tiles within <c>radius</c> (default
    /// 12) of the player at the player's Z (or <c>z</c>); graphic is hex, blend a <see cref="GraphicEffectBlendMode"/> number,
    /// kind <c>f</c> fixed and endless (default), <c>m</c> moving to another random tile at speed 1, <c>l</c> lightning.
    /// <c>effects:0</c> destroys every effect the world holds.</para>
    /// </summary>
    internal static class BenchEffects
    {
        private static int _spawns;
        // moving effects die on arrival: a plan entry keeps this many alive (Tick), re-spawned with the same arguments
        private static int _sustain;
        private static string _sustainEntry;
        private static long _nextTick;

        /// <summary>Keeps the moving effects of the last <c>m</c> entry alive; called every frame by DevBench's plan loop.</summary>
        public static void Tick(World world)
        {
            if (_sustain <= 0 || Environment.TickCount64 < _nextTick)
            {
                return;
            }

            _nextTick = Environment.TickCount64 + 200;
            int alive = 0;

            for (var f = world.Effects.Items as GameEffect; f != null; f = f.Next as GameEffect)
            {
                alive++;
            }

            if (alive < _sustain)
            {
                string[] p = _sustainEntry.Split(':');
                p[1] = (_sustain - alive).ToString();
                Spawn(world, string.Join(":", p), true);
            }
        }

        public static void Spawn(World world, string entry, bool sustaining = false)
        {
            string[] p = entry.Split(':');
            int n = int.Parse(p[1]);

            if (!sustaining)
            {
                _sustain = p.Length > 6 && p[6] == "m" ? n : 0;
                _sustainEntry = entry;
            }

            if (n <= 0)
            {
                int destroyed = 0;

                for (var f = world.Effects.Items as GameEffect; f != null;)
                {
                    var next = f.Next as GameEffect;
                    f.Destroy();
                    destroyed++;
                    f = next;
                }

                Log.Info($"[bench] {entry}: destroyed {destroyed}");

                return;
            }

            ushort graphic = Convert.ToUInt16(p[2], 16);
            var blend = p.Length > 3 && p[3].Length > 0 ? (GraphicEffectBlendMode)int.Parse(p[3]) : GraphicEffectBlendMode.Normal;
            int radius = p.Length > 4 && p[4].Length > 0 ? int.Parse(p[4]) : 12;
            int z = p.Length > 5 && int.TryParse(p[5], out int zz) ? zz : world.Player.Z;
            string kind = p.Length > 6 ? p[6] : "f";
            var rnd = new Random(12345 + _spawns++);

            for (int i = 0; i < n; i++)
            {
                int x = world.Player.X + rnd.Next(-radius, radius + 1);
                int y = world.Player.Y + rnd.Next(-radius, radius + 1);
                int tx = world.Player.X + rnd.Next(-radius, radius + 1);
                int ty = world.Player.Y + rnd.Next(-radius, radius + 1);

                world.SpawnEffect(
                    kind == "m" ? GraphicEffectType.Moving : kind == "l" ? GraphicEffectType.Lightning : GraphicEffectType.FixedXYZ,
                    0, 0, graphic, 0, (ushort)x, (ushort)y, (sbyte)z, (ushort)tx, (ushort)ty, (sbyte)z,
                    (byte)(kind == "m" ? 1 : 10), 0, false, false, false, blend
                );
            }

            if (!sustaining)
            {
                Log.Info($"[bench] {entry}: spawned {n}");
            }
        }
    }
}
