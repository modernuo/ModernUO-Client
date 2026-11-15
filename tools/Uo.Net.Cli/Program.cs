using System.Diagnostics;
using System.Globalization;
using Uo.Net;

namespace Uo.Net.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var o = Options.Parse(args);
        if (o == null)
        {
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        var failures = 0;
        for (var run = 1; run <= o.Runs; run++)
        {
            if (o.Runs > 1) Console.WriteLine($"===== run {run}/{o.Runs} =====");
            if (!RunOnce(o)) failures++;
            if (run < o.Runs) Thread.Sleep(1500); // let the server finish the logout
        }

        if (o.Runs > 1) Console.WriteLine($"===== {o.Runs - failures}/{o.Runs} runs passed =====");
        return failures == 0 ? 0 : 1;
    }

    private static bool RunOnce(Options o)
    {
        var sink = new StatsSink(o.Verbose);
        using var client = new ClassicClient(sink);
        client.ReceiveChunkSize = o.RecvChunk;
        var log = o.LogPackets;
        client.Tap = (outgoing, p, handled) =>
        {
            if (!outgoing && !handled) sink.CountUnhandled(p[0]);
            if (log) PacketLog.Write(outgoing, p, handled);
        };

        var total = Stopwatch.StartNew();
        client.Connect(new LoginOptions
        {
            Host = o.Host, Port = o.Port, Username = o.User, Password = o.Pass,
            CharacterName = o.Character, NewCharacterName = o.NewCharacter ?? "Pocwalker", ClientVersion = o.ClientVersion,
        });

        // ---- login
        if (!PumpUntil(client, () => client.State is ClientState.InWorld or ClientState.Failed or ClientState.Disconnected, 20_000)
            || client.State != ClientState.InWorld)
        {
            Console.WriteLine($"FAIL: did not reach world (state {client.State}: {client.LastError})");
            return false;
        }

        Console.WriteLine($"in world after {total.ElapsedMilliseconds} ms: player 0x{client.PlayerSerial:X8} at {sink.PlayerX},{sink.PlayerY},{sink.PlayerZ} map {sink.Map}");
        Pump(client, 2000);
        sink.PrintCounts("after login");

        // ---- teleport
        if (o.Go != null)
        {
            var before = (sink.PlayerX, sink.PlayerY);
            var updates = sink.PlayerUpdates;
            client.Say("[go " + o.Go);
            var moved = PumpUntil(client, () => sink.PlayerUpdates > updates && (sink.PlayerX, sink.PlayerY) != before, 5000);
            Console.WriteLine(moved
                ? $"[go {o.Go}: now at {sink.PlayerX},{sink.PlayerY},{sink.PlayerZ}"
                : $"[go {o.Go}: no position change (already there, or not staff)");
            Pump(client, 2500);
            sink.PrintCounts("after teleport");
        }

        // ---- walk
        var walkOk = Walk(client, sink, o);

        // ---- stay connected, measure steady-state allocation per packet
        Console.WriteLine($"staying connected {o.StaySeconds} s ...");
        var pings = sink.Pings.Count;
        var packets0 = client.PacketsReceived;
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var stay = Stopwatch.StartNew();
        while (stay.ElapsedMilliseconds < o.StaySeconds * 1000L && client.State == ClientState.InWorld)
            client.Poll(50);
        var alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        var stayPackets = client.PacketsReceived - packets0;

        if (client.State != ClientState.InWorld)
        {
            Console.WriteLine($"FAIL: dropped during stay ({client.LastError})");
            return false;
        }

        Console.WriteLine($"stay: {stayPackets} packets, {alloc} B allocated on the poll thread ({(stayPackets > 0 ? alloc / (double)stayPackets : 0):F1} B/packet incl. console output), pings {sink.Pings.Count - pings}: {string.Join(", ", sink.Pings.Skip(pings).Select(p => p.ToString("F2", CultureInfo.InvariantCulture) + " ms"))}");
        sink.PrintCounts("final");
        Console.WriteLine($"traffic: {client.BytesReceivedRaw} B raw / {client.BytesReceivedDecompressed} B decompressed, {client.PacketsReceived} packets in, {client.PacketsSent} out, unhandled ids: {sink.UnhandledSummary()}");

        client.Disconnect();
        Console.WriteLine($"disconnected cleanly after {total.Elapsed.TotalSeconds:F1} s");
        return walkOk;
    }

    private static bool Walk(ClassicClient client, StatsSink sink, Options o)
    {
        if (o.Steps <= 0) return true;

        var plan = ParsePattern(o.Pattern);
        if (plan == null) { Console.WriteLine("bad --pattern"); return false; }
        while (plan.Count > o.Steps) plan.RemoveAt(plan.Count - 1);

        var start = (sink.PlayerX, sink.PlayerY);
        var interval = o.Run ? 200 : 400; // ModernUO movement.delay.walkFoot/runFoot (turns are free)
        sink.ResetMoveStats();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var packetsBefore = client.PacketsReceived;
        var clock = Stopwatch.StartNew();
        long nextSend = 0;
        var requests = 0;

        foreach (var d0 in plan)
        {
            var d = o.Run ? d0 | Direction.Running : d0;

            // A request in a new direction only turns the mobile; send the turn first.
            if ((sink.Facing & Direction.Mask) != d0)
            {
                if (!SendWhenReady(client, d, clock, ref nextSend, 0)) return false;
                sink.PredictTurn(d0);
                requests++;
            }

            if (!SendWhenReady(client, d, clock, ref nextSend, interval)) return false;
            sink.PredictStep(d0);
            requests++;
        }

        PumpUntil(client, () => client.PendingMoves == 0, 3000);
        var alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        var walkPackets = client.PacketsReceived - packetsBefore;

        var rtts = sink.MoveRtts.OrderBy(x => x).ToArray();
        Console.WriteLine($"walk: {plan.Count} steps ({o.Pattern}), {requests} requests ({requests - plan.Count} turns) in {clock.ElapsedMilliseconds} ms; acks {sink.Acks}, rejects {sink.Rejects}, resets {sink.Resets}");
        if (rtts.Length > 0)
            Console.WriteLine($"move RTT ms: min {rtts[0]:F2}  median {rtts[rtts.Length / 2]:F2}  p95 {rtts[(int)(rtts.Length * 0.95)]:F2}  max {rtts[^1]:F2}");
        Console.WriteLine($"position: start {start.Item1},{start.Item2}  predicted {sink.PredX},{sink.PredY}  server {sink.PlayerX},{sink.PlayerY}; {walkPackets} packets during walk, {alloc} B allocated ({(walkPackets > 0 ? alloc / (double)walkPackets : 0):F1} B/packet incl. console output)");

        var ok = sink.Rejects == 0 && sink.Acks == requests;
        Console.WriteLine(ok ? "walk: all moves acked" : "walk: NOT all moves acked");
        return ok;
    }

    // "E5 S5 N5 W5": direction (N NE E SE S SW W NW) + count. The default is an out-and-back L
    // so the walk ends where it started.
    private static List<Direction>? ParsePattern(string pattern)
    {
        var plan = new List<Direction>();
        foreach (var tok in pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = tok.TakeWhile(char.IsLetter).Count();
            if (letters == 0 || !int.TryParse(tok.AsSpan(letters), out var n)) return null;
            Direction? d = tok[..letters].ToUpperInvariant() switch
            {
                "N" => Direction.North, "NE" => Direction.Right, "E" => Direction.East, "SE" => Direction.Down,
                "S" => Direction.South, "SW" => Direction.Left, "W" => Direction.West, "NW" => Direction.Up,
                _ => null,
            };
            if (d == null) return null;
            for (var i = 0; i < n; i++) plan.Add(d.Value);
        }
        return plan;
    }

    private static bool SendWhenReady(ClassicClient client, Direction d, Stopwatch clock, ref long nextSend, int interval)
    {
        while (clock.ElapsedMilliseconds < nextSend || client.PendingMoves >= 5)
        {
            client.Poll(5);
            if (client.State != ClientState.InWorld)
            {
                Console.WriteLine($"FAIL: dropped during walk ({client.LastError})");
                return false;
            }
        }

        if (client.RequestMove(d) < 0) return false;
        nextSend = clock.ElapsedMilliseconds + interval;
        return true;
    }

    private static void Pump(ClassicClient client, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms && client.State != ClientState.Failed) client.Poll(20);
    }

    private static bool PumpUntil(ClassicClient client, Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            client.Poll(20);
        }
        return true;
    }
}

internal sealed class Options
{
    public string Host = "127.0.0.1";
    public int Port = 2593;
    public string User = "";
    public string Pass = "";
    public string? Character;
    public string? NewCharacter;
    public string? Go = "1495 1629";
    public int Steps = 20;
    public string Pattern = "E5 S5 N5 W5";
    public int StaySeconds = 30;
    public int Runs = 1;
    public bool Run;
    public int RecvChunk = 16 * 1024;
    public bool LogPackets;
    public bool Verbose;
    public string ClientVersion = "7.0.103.0";

    public const string Usage =
        "Uo.Net.Cli [--host 127.0.0.1] [--port 2593] [--user U --pass P | env UO_USER/UO_PASS]\n" +
        "           [--char NAME] [--new-char NAME] [--go \"x y [z]\" | --no-go] [--steps 20] [--pattern \"E5 S5 N5 W5\"] [--run]\n" +
        "           [--stay 30] [--runs 1] [--client-version 7.0.103.0] [--recv-chunk N] [--log-packets] [--verbose]";

    public static Options? Parse(string[] args)
    {
        var o = new Options
        {
            User = Environment.GetEnvironmentVariable("UO_USER") ?? "",
            Pass = Environment.GetEnvironmentVariable("UO_PASS") ?? "",
        };

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + args[i]);
            switch (args[i])
            {
                case "--host": o.Host = Next(); break;
                case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--user": o.User = Next(); break;
                case "--pass": o.Pass = Next(); break;
                case "--char": o.Character = Next(); break;
                case "--new-char": o.NewCharacter = Next(); break;
                case "--go": o.Go = Next(); break;
                case "--no-go": o.Go = null; break;
                case "--pattern": o.Pattern = Next(); break;
                case "--steps": o.Steps = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--stay": o.StaySeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--runs": o.Runs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--run": o.Run = true; break;
                case "--recv-chunk": o.RecvChunk = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--client-version": o.ClientVersion = Next(); break;
                case "--log-packets": o.LogPackets = true; break;
                case "--verbose": o.Verbose = true; break;
                default: return null;
            }
        }

        return o.User.Length == 0 || o.Pass.Length == 0 ? null : o;
    }
}

internal static class PacketLog
{
    public static void Write(bool outgoing, ReadOnlySpan<byte> p, bool handled)
    {
        var shown = Math.Min(p.Length, 48);
        // Never print credentials: 0x80/0x91 carry the password.
        var redact = outgoing && p[0] is 0x80 or 0x91;
        Console.WriteLine($"{(outgoing ? "C->S" : "S->C")} 0x{p[0]:X2} len {p.Length,5}{(handled ? "  " : " ?")} {(redact ? "(credentials redacted)" : Convert.ToHexString(p[..shown]) + (p.Length > shown ? "..." : ""))}");
    }
}
