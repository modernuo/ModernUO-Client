// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
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
    /// kind <c>f</c> fixed and endless (default), <c>m</c> moving to another random tile at speed 1, <c>l</c> lightning, <c>e</c> on mobiles and ground items (sources and targets: ecs_effect_endpoints).
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

            if (kind == "e")
            {
                SpawnOnEntities(world, n, graphic, blend, radius, rnd);

                if (!sustaining)
                {
                    Log.Info($"[bench] {entry}: spawned {n} on entities");
                }

                return;
            }

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

        // kind e (ecs_effect_endpoints gates): effects whose source or target is a mobile (the player included) or a ground item
        // within radius, in turn: a fixed effect following an entity, a moving effect between two entities, a moving effect from
        // the player to an entity, lightning on an entity
        private static void SpawnOnEntities(World world, int n, ushort graphic, GraphicEffectBlendMode blend, int radius, Random rnd)
        {
            var near = new System.Collections.Generic.List<Entity> { world.Player };

            foreach (Mobile m in world.Mobiles.Values)
            {
                if (!ReferenceEquals(m, world.Player) && !m.IsDestroyed && Math.Max(Math.Abs(m.X - world.Player.X), Math.Abs(m.Y - world.Player.Y)) <= radius)
                {
                    near.Add(m);
                }
            }

            foreach (Item it in world.Items.Values)
            {
                if (it.OnGround && !it.IsDestroyed && !it.IsMulti && Math.Max(Math.Abs(it.X - world.Player.X), Math.Abs(it.Y - world.Player.Y)) <= radius)
                {
                    near.Add(it);
                }
            }

            for (int i = 0; i < n; i++)
            {
                Entity a = near[rnd.Next(near.Count)];
                Entity b = near[rnd.Next(near.Count)];

                switch (i & 3)
                {
                    case 0:
                        world.SpawnEffect(GraphicEffectType.FixedFrom, a.Serial, 0, graphic, 0, a.X, a.Y, a.Z, 0, 0, 0, 10, 0, false, false, false, blend);

                        break;

                    case 1:
                        world.SpawnEffect(GraphicEffectType.Moving, a.Serial, b.Serial, graphic, 0, a.X, a.Y, a.Z, b.X, b.Y, b.Z, 1, 0, false, true, false, blend);

                        break;

                    case 2:
                        world.SpawnEffect(GraphicEffectType.Moving, world.Player.Serial, a.Serial, graphic, 0, 0, 0, 0, a.X, a.Y, a.Z, 1, 0, false, false, false, blend);

                        break;

                    default:
                        world.SpawnEffect(GraphicEffectType.Lightning, a.Serial, 0, 0, 0, a.X, a.Y, a.Z, 0, 0, 0, 0, 0, false, false, false, blend);

                        break;
                }
            }
        }
    }
}
#endif
