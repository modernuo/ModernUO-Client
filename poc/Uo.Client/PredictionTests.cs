using Uo.Net;
using Uo.Render;

namespace Uo.Client;

/// <summary>Headless prediction tests (<c>--test</c>): walker + fake server + walk map on the real pack, with a manual
/// clock stepped at 60 Hz. Test sites are found by searching the map around Britain, then printed.</summary>
public static class PredictionTests
{
    const int Cx = 1495, Cy = 1629;
    static int _fail;

    sealed class Rig
    {
        public readonly Clock Clock = new() { Manual = true, Now = 1_000_000 };
        public readonly WalkMap Map;
        public readonly EcsWorld World;
        public readonly FakeServer Server;
        public readonly Walker Walker;
        public int MaxPending;

        public Rig(PackSet pack, double latencyMs)
        {
            Map = new WalkMap(pack);
            World = new EcsWorld(Clock);
            Map.Dynamic = World;
            Server = new FakeServer(World, Map, Clock, new FakeServer.Scenario { Mobiles = 0, Items = 0, WanderPerSecond = 0 }, latencyMs);
            Walker = new Walker(Server, Map, World, Clock);
            Server.Connect(null);
            Server.Poll();
        }

        public void Hold(int dir, double ms, bool run = false)
        {
            for (double t = 0; t < ms; t += 1000.0 / 60)
            {
                Clock.Now += Clock.Ms(1000.0 / 60);
                Server.Poll();
                Walker.Update(dir, run);
                MaxPending = Math.Max(MaxPending, Math.Max(Walker.Pending, Server.PendingMoves));
            }
        }

        /// <summary>Walks until <paramref name="steps"/> steps were sent in <paramref name="dir"/> (or 5 s pass), then lets acks arrive.</summary>
        public void Steps(int dir, int steps, bool run = false)
        {
            int target = Walker.StepsSent + steps;
            for (int i = 0; i < 300 && Walker.StepsSent < target; i++) Hold(dir, 1000.0 / 60, run);
            Hold(-1, 600);
        }

        public void TeleportTo(int x, int y)
        {
            Server.Teleport(x, y);
            Server.Poll();
            Hold(-1, 50);
        }
    }

    public static int Run(PackSet pack)
    {
        _fail = 0;
        var probe = new WalkMap(pack);

        // ---- 1. walk into a wall: blocked locally, nothing sent but a turn, no reject
        if (FindWall(probe, out int wx, out int wy, out int wdir))
        {
            var r = new Rig(pack, 80);
            r.TeleportTo(wx, wy);
            int x0 = r.Walker.X, y0 = r.Walker.Y;
            r.Hold(wdir, 2000);
            r.Hold(-1, 300);
            Check($"wall: from {wx},{wy} facing dir {wdir} toward a wall", r.Walker.X == x0 && r.Walker.Y == y0 && r.Walker.StepsSent == 0 && r.Walker.Blocked > 0 && r.Walker.Rejects == 0 && r.Server.ServerX == x0,
                $"pos {r.Walker.X},{r.Walker.Y} steps {r.Walker.StepsSent} turns {r.Walker.TurnsSent} blocked {r.Walker.Blocked} rejects {r.Walker.Rejects}");
        }
        else Check("wall: no wall found near Britain", false, "");

        // ---- 2. climb: stairs (static surface/bridge) and a land slope
        foreach (bool stairs in new[] { true, false })
        {
            string what = stairs ? "stairs" : "slope";
            if (!FindClimb(probe, stairs, out int sx, out int sy, out int sdir, out int steps, out int dz))
            {
                Check($"{what}: none found near Britain", false, "");
                continue;
            }
            var r = new Rig(pack, 80);
            r.TeleportTo(sx, sy);
            int z0 = r.Walker.Z;
            int minZ = z0, maxZ = z0;
            int done = 0;
            for (int k = 0; k < steps; k++)
            {
                r.Steps(sdir, 1);
                minZ = Math.Min(minZ, r.Walker.Z); maxZ = Math.Max(maxZ, r.Walker.Z);
                done++;
            }
            bool ok = r.Walker.Z - z0 >= dz - 1 && r.Walker.Rejects == 0 && r.Walker.Pending == 0 && r.Walker.X == r.Server.ServerX && r.Walker.Y == r.Server.ServerY && r.Walker.Z == r.Server.ServerZ;
            Check($"{what}: {done} steps dir {sdir} from {sx},{sy},{z0}", ok,
                $"z {z0} -> {r.Walker.Z} (expected about +{dz}), at {r.Walker.X},{r.Walker.Y}; server {r.Server.ServerX},{r.Server.ServerY},{r.Server.ServerZ}; acks {r.Walker.Acks} rejects {r.Walker.Rejects}");
        }

        // ---- 3. reject and snap: the server blocks a tile the client thinks is open
        if (FindOpenRun(probe, 2, 6, out int ox, out int oy))
        {
            var r = new Rig(pack, 80);
            r.TeleportTo(ox, oy);
            r.Server.ServerBlocked.Add(oy * 65536 + ox + 3);
            r.Steps(2, 6);                      // east: predicted through x+3, server rejects there
            r.Hold(-1, 500);
            bool snapped = r.Walker.Rejects >= 1 && r.Walker.Pending == 0 && r.Walker.X == ox + 2 && r.Walker.Y == oy && r.Walker.X == r.Server.ServerX;
            Check($"reject: server-only blocker at {ox + 3},{oy}", snapped,
                $"walker {r.Walker.X},{r.Walker.Y} server {r.Server.ServerX},{r.Server.ServerY}, rejects {r.Walker.Rejects} snaps {r.Walker.Snaps} pending {r.Walker.Pending}");
            int acks = r.Walker.Acks;
            r.Steps(4, 2);                      // after the reset (sequence 0) movement resumes
            Check("reject: movement resumes after the sequence reset", r.Walker.Acks > acks && r.Walker.Y == oy + 2 && r.Walker.Y == r.Server.ServerY,
                $"walker {r.Walker.X},{r.Walker.Y} server {r.Server.ServerX},{r.Server.ServerY}, acks {acks} -> {r.Walker.Acks}");
        }
        else Check("reject: no open run found", false, "");

        // ---- 4. at most 5 unacknowledged requests (2 s latency, running)
        if (FindOpenRun(probe, 2, 12, out int px, out int py))
        {
            var r = new Rig(pack, 2000);
            r.TeleportTo(px, py);
            r.Hold(2, 1500, run: true);
            Check("pending cap: 2 s latency, running east for 1.5 s", r.MaxPending <= 5 && r.MaxPending == 5 && r.Walker.CapHits > 0,
                $"max pending {r.MaxPending}, sent {r.Walker.StepsSent} steps + {r.Walker.TurnsSent} turns, cap waits {r.Walker.CapHits}");
            r.Hold(-1, 3000);
            Check("pending cap: all acked after latency", r.Walker.Pending == 0 && r.Walker.Rejects == 0 && r.Walker.X == r.Server.ServerX, $"pending {r.Walker.Pending} rejects {r.Walker.Rejects}");
        }

        // ---- 5. a new direction only turns; pacing 400 ms walk / 200 ms run
        if (FindOpenRun(probe, 2, 12, out int tx, out int ty))
        {
            var r = new Rig(pack, 30);
            r.TeleportTo(tx, ty);               // spawns facing south
            r.Hold(2, 50);
            Check("turn: first request in a new direction is a turn", r.Walker.TurnsSent == 1 && r.Walker.StepsSent == 0 && r.Walker.X == tx, $"turns {r.Walker.TurnsSent} steps {r.Walker.StepsSent}");
            int s0 = r.Walker.StepsSent;
            r.Hold(2, 2000);
            int walked = r.Walker.StepsSent - s0;
            int s1 = r.Walker.StepsSent;
            r.Hold(6, 150);                     // turn back west
            int s2 = r.Walker.StepsSent;
            r.Hold(6, 2000, run: true);
            int ran = r.Walker.StepsSent - s2;
            Check("pacing: ~5 walk steps / 2 s, ~10 run steps / 2 s", walked is >= 4 and <= 6 && ran is >= 9 and <= 11, $"walked {walked}, ran {ran}");
        }

        // ---- 6. teleport (0x20 + sequence reset) while walking snaps the prediction
        if (FindOpenRun(probe, 2, 12, out int qx, out int qy))
        {
            var r = new Rig(pack, 200);
            r.TeleportTo(qx, qy);
            r.Hold(2, 900);
            r.Server.Teleport(Cx, Cy);
            r.Hold(-1, 500);
            Check("teleport: player update resets pending and snaps", r.Walker.X == Cx && r.Walker.Y == Cy && r.Walker.Pending == 0, $"walker {r.Walker.X},{r.Walker.Y} pending {r.Walker.Pending}");
        }

        Console.WriteLine(_fail == 0 ? "prediction tests: ALL PASSED" : $"prediction tests: {_fail} FAILED");
        Console.WriteLine($"walk-map region loads: {probe.RegionLoads}, {probe.RegionLoadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms total");
        return _fail == 0 ? 0 : 1;
    }

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    static readonly int[] Cardinals = [0, 2, 4, 6];

    /// <summary>A standable tile next to a wall (a static with the Wall and Impassable flags on the target tile).</summary>
    static bool FindWall(WalkMap m, out int x, out int y, out int dir)
    {
        for (int r = 0; r < 30; r++)
            for (y = Cy - r; y <= Cy + r; y++)
                for (x = Cx - r; x <= Cx + r; x++)
                {
                    if (Math.Max(Math.Abs(x - Cx), Math.Abs(y - Cy)) != r || !m.SpawnZ(x, y, out var z)) continue;
                    foreach (int d in Cardinals)
                    {
                        int nx = x, ny = y, dd = d;
                        WalkMap.GetNewXY(d, ref nx, ref ny);
                        if (!m.TileHasStaticFlag(nx, ny, TileFlag.Wall | TileFlag.Impassable)) continue;
                        if (!m.TileHasStaticFlag(nx, ny, TileFlag.Wall)) continue;
                        int cx = x, cy = y;
                        sbyte cz = z;
                        if (m.CanWalk(ref dd, ref cx, ref cy, ref cz)) continue;
                        dir = d;
                        return true;
                    }
                }
        x = y = dir = 0;
        return false;
    }

    /// <summary>A run of ≥ 3 steps in one cardinal direction gaining ≥ 6 z, on stairs (static surfaces) or a land slope.</summary>
    static bool FindClimb(WalkMap m, bool stairs, out int x, out int y, out int dir, out int steps, out int dz)
    {
        int best = 0;
        x = y = dir = steps = dz = 0;
        for (int r = 0; r < 60 && best == 0; r++)
            for (int yy = Cy - r; yy <= Cy + r; yy++)
                for (int xx = Cx - r; xx <= Cx + r; xx++)
                {
                    if (Math.Max(Math.Abs(xx - Cx), Math.Abs(yy - Cy)) != r || !m.SpawnZ(xx, yy, out var z0)) continue;
                    foreach (int d in Cardinals)
                    {
                        int cx = xx, cy = yy;
                        sbyte cz = z0;
                        int n = 0;
                        bool kind = true;
                        for (; n < 6; n++)
                        {
                            int dd = d, nx = cx, ny = cy;
                            sbyte nz = cz;
                            if (!m.CanWalk(ref dd, ref nx, ref ny, ref nz) || dd != d || nz < cz) break;
                            bool onStatic = m.TileHasStaticFlag(nx, ny, TileFlag.Surface | TileFlag.Bridge) && (m.TileHasStaticFlag(nx, ny, TileFlag.StairBack | TileFlag.StairRight) || m.TileHasStaticFlag(nx, ny, TileFlag.Bridge));
                            if (nz > cz && onStatic != stairs) { kind = false; break; }
                            cx = nx; cy = ny; cz = nz;
                        }
                        int gain = cz - z0;
                        if (kind && n >= 3 && gain >= 6 && gain > best)
                        {
                            best = gain; x = xx; y = yy; dir = d; steps = n; dz = gain;
                        }
                    }
                }
        return best > 0;
    }

    /// <summary>A flat-ish tile from which <paramref name="len"/> steps in <paramref name="dir"/> and 3 steps south all pass.</summary>
    static bool FindOpenRun(WalkMap m, int dir, int len, out int x, out int y)
    {
        for (int r = 0; r < 40; r++)
            for (y = Cy - r; y <= Cy + r; y++)
                for (x = Cx - r; x <= Cx + r; x++)
                {
                    if (Math.Max(Math.Abs(x - Cx), Math.Abs(y - Cy)) != r || !m.SpawnZ(x, y, out var z)) continue;
                    if (Clear(m, x, y, z, dir, len) && Clear(m, x + 2, y, z, 4, 3)) return true;
                }
        x = y = 0;
        return false;
    }

    static bool Clear(WalkMap m, int x, int y, sbyte z, int dir, int len)
    {
        for (int i = 0; i < len; i++)
        {
            int d = dir;
            if (!m.CanWalk(ref d, ref x, ref y, ref z) || d != dir) return false;
        }
        return true;
    }
}
