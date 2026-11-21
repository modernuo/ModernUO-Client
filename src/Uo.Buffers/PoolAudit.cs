// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Uo.Buffers;

/// <summary>
/// CLEANUP(release) Rent/return audit for the ST and large pools (CUO_POOL_AUDIT=1, devtools builds only), like
/// ModernUO's DEBUG_ARRAYPOOL plus its thread audit: double return, return of an array the pool never rented, use of
/// an ST pool from a thread that does not own it, and arrays still rented at shutdown (with the rent stack). Off: one
/// static bool read per rent/return. The shared (ArrayPool.Shared) family is not tracked.
/// </summary>
public static class PoolAudit
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CUO_POOL_AUDIT") == "1";

    /// <summary>Where findings go (the client points it at its logger). Default: stderr.</summary>
    public static Action<string> Log = static s => Console.Error.WriteLine(s);

    private const int MaxLogged = 200;

    private sealed class RentInfo
    {
        public bool Rented;
        public string Stack;
    }

    private static readonly ConditionalWeakTable<object, RentInfo> _rents = new ConditionalWeakTable<object, RentInfo>();
    private static int _logged;
    public static int DoubleReturns, ForeignReturns, WrongThread;

    static PoolAudit()
    {
        if (Enabled)
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => ReportOutstanding();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OnRent(Array a)
    {
        if (Enabled)
        {
            Rent(a);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OnReturn(Array a)
    {
        if (Enabled)
        {
            Return(a);
        }
    }

    private static void Rent(Array a)
    {
        RentInfo info = _rents.GetValue(a, static _ => new RentInfo());

        lock (info)
        {
            info.Rented = true;
            info.Stack = new StackTrace(3, false).ToString();
        }
    }

    private static void Return(Array a)
    {
        if (!_rents.TryGetValue(a, out RentInfo info))
        {
            Interlocked.Increment(ref ForeignReturns);
            Write($"[pool-audit] return of an array the pool never rented (len {a.Length}, {a.GetType().Name})\n{new StackTrace(3, false)}");

            return;
        }

        lock (info)
        {
            if (!info.Rented)
            {
                Interlocked.Increment(ref DoubleReturns);
                Write($"[pool-audit] double return (len {a.Length}, {a.GetType().Name})\n{new StackTrace(3, false)}");

                return;
            }

            info.Rented = false;
        }
    }

    internal static void CheckThread(int ownerThreadId, string what)
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
        {
            Interlocked.Increment(ref WrongThread);
            Write($"[pool-audit] {what} on thread {Environment.CurrentManagedThreadId}, pool owned by {ownerThreadId}\n{new StackTrace(2, false)}");
        }
    }

    /// <summary>Logs every array still rented (with its rent stack) and the totals.</summary>
    public static void ReportOutstanding()
    {
        int outstanding = 0;

        foreach (KeyValuePair<object, RentInfo> kv in _rents)
        {
            if (kv.Value.Rented)
            {
                outstanding++;
                Write($"[pool-audit] still rented at shutdown: len {((Array)kv.Key).Length} {kv.Key.GetType().Name}\n{kv.Value.Stack}");
            }
        }

        Log($"[pool-audit] summary: outstanding={outstanding} doubleReturns={DoubleReturns} foreignReturns={ForeignReturns} wrongThread={WrongThread}");
    }

    private static void Write(string s)
    {
        if (Interlocked.Increment(ref _logged) <= MaxLogged)
        {
            Log(s);
        }
    }
}
#endif
