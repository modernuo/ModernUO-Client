namespace Uo.Render;

/// <summary>Tracks when the GPU has finished the copies recorded into the renderer's frame command buffers (memory
/// round 3). Each frame command buffer gets a serial (<see cref="Serial"/> while it is being recorded). A buffer that
/// carries staging copies (<see cref="Staged"/>) is submitted with a fence, which is handed over with
/// <see cref="Track"/> and polled without blocking (<see cref="Poll"/>); <see cref="Retired"/> is the highest serial
/// whose copies have all finished. Fences are polled in submit order and SDL_GPU runs one queue, so a signalled fence
/// also retires every earlier serial. No GPU calls here: the renderer passes the fence query and release in, so the
/// logic is tested without a device.</summary>
public sealed class CopyRetirement : IDisposable
{
    readonly Func<IntPtr, bool> _signalled;
    readonly Action<IntPtr> _release;
    readonly Queue<(long Serial, IntPtr Fence)> _pending = new(8);

    public CopyRetirement(Func<IntPtr, bool> signalled, Action<IntPtr> release)
    {
        _signalled = signalled;
        _release = release;
    }

    /// <summary>Serial of the frame command buffer being recorded (starts at 1).</summary>
    public long Serial { get; private set; } = 1;
    /// <summary>Every copy recorded at or before this serial has finished on the GPU.</summary>
    public long Retired { get; private set; }
    /// <summary>The command buffer being recorded carries staging copies: submit it with a fence.</summary>
    public bool Staged;
    /// <summary>Fences held (submitted, not yet seen signalled).</summary>
    public int PendingFences => _pending.Count;
    /// <summary>Fences taken in total (one per frame that carried staging copies).</summary>
    public long FencesTaken;

    public bool IsRetired(long serial) => serial <= Retired;

    /// <summary>The frame command buffer was submitted: returns its serial and starts the next one.</summary>
    public long Submit()
    {
        Staged = false;
        return Serial++;
    }

    /// <summary>Takes ownership of <paramref name="fence"/> for the submitted command buffer <paramref name="serial"/>;
    /// it is released once seen signalled (or on <see cref="Dispose"/>). A zero fence means the submit failed: see
    /// <see cref="Failed"/>.</summary>
    public void Track(long serial, IntPtr fence)
    {
        if (fence == IntPtr.Zero)
        {
            Failed(serial);
            return;
        }
        _pending.Enqueue((serial, fence));
        FencesTaken++;
    }

    /// <summary>The submit of command buffer <paramref name="serial"/> failed or returned no fence. That does not prove
    /// the GPU never runs it (D3D12 executes the list before it acquires the fence; Vulkan can queue it and then fail at
    /// present), and it never proves that earlier frames finished. So nothing retires here: its copies stay pending
    /// until a later fence signals or a wait for a later serial or for the whole GPU succeeds (the queue runs in submit
    /// order, so that also covers this serial if it ran). Until then <see cref="NeedsFence"/> asks for a fence on the next
    /// frame, so the slots free up without a blocking wait once the GPU works again.</summary>
    public void Failed(long serial)
    {
        if (serial > _unresolved) _unresolved = serial;
        FailedSubmits++;
    }

    long _unresolved;
    /// <summary>Frame submits that failed (or returned no fence) while carrying copies.</summary>
    public long FailedSubmits;
    /// <summary>A failed submit's copies are not yet known to be finished.</summary>
    public bool Unresolved => _unresolved > Retired;
    /// <summary>Submit the frame command buffer with a fence: it carries copies, or a failed submit waits for a later
    /// fence to resolve it.</summary>
    public bool NeedsFence => Staged || Unresolved;

    /// <summary>The caller waited successfully for <paramref name="serial"/> (or for the whole GPU) itself. Never call it
    /// for a failed submit or a failed wait.</summary>
    public void MarkRetired(long serial)
    {
        if (serial > Retired) Retired = serial;
    }

    /// <summary>Retires every leading fence that has signalled, and releases fences a wait elsewhere already covered;
    /// never blocks.</summary>
    public void Poll()
    {
        while (_pending.Count > 0)
        {
            var (serial, fence) = _pending.Peek();
            if (serial > Retired && !_signalled(fence)) break;
            _pending.Dequeue();
            _release(fence);
            MarkRetired(serial);
        }
    }

    public void Dispose()
    {
        while (_pending.Count > 0) _release(_pending.Dequeue().Fence);
    }
}

/// <summary>Reuse rule and wait statistics for a fixed set of staging transfer buffers (<see cref="PageResidency"/>'s
/// page slots; memory round 3). A slot's buffer is rewritten only after the copy recorded from it last has retired,
/// so it is mapped without cycling and SDL_GPU never adds a buffer to the slot's container (with cycling, a slot whose
/// copy was still in flight grew its container by a whole page chain, and containers never shrink: 6-8 art buffers of
/// 10.67 MiB where 4 would do). When a page is queued but every idle slot still waits for its copy, the load stays
/// queued for a later frame (a stall); the game loop never blocks. Without a <see cref="Retire"/> tracker (a host that
/// does not render through <see cref="Renderer"/>) every slot is reusable and maps with cycling, as before.</summary>
public sealed class StagingSlots
{
    readonly long[] _copySerial; // serial of the command buffer that carries the slot's last copy (0 = none)

    public StagingSlots(int count) => _copySerial = new long[count];

    public CopyRetirement Retire;
    public int Count => _copySerial.Length;
    /// <summary>Map with cycling: only without a tracker (no way to know when a copy finished).</summary>
    public bool CycleOnMap => Retire == null;

    /// <summary>Slot <paramref name="slot"/>'s buffer may be rewritten: no copy from it is still in flight.</summary>
    public bool Reusable(int slot)
    {
        long s = _copySerial[slot];
        return s == 0 || Retire == null || Retire.IsRetired(s);
    }

    /// <summary>A copy from <paramref name="slot"/> was recorded into the command buffer being recorded.</summary>
    public void Copied(int slot)
    {
        if (Retire == null) return;
        _copySerial[slot] = Retire.Serial;
        Retire.Staged = true;
    }

    /// <summary>Stall episodes (runs of frames in which a queued page could not start because every idle slot's copy
    /// was in flight), the frames stalled, and the time from each episode's first stalled frame to the frame it ended.</summary>
    public int Stalls;
    public long StallFrames, StallTicks, MaxStallTicks;
    long _stallStart;
    bool _stalled;

    /// <summary>Once per <see cref="PageResidency.StartLoads"/>: <paramref name="stalled"/> = a queued page is waiting
    /// only for a slot's copy to retire. <paramref name="now"/> = Stopwatch ticks.</summary>
    public void Track(bool stalled, long now)
    {
        if (stalled)
        {
            StallFrames++;
            if (!_stalled) { _stalled = true; _stallStart = now; Stalls++; }
            return;
        }
        if (!_stalled) return;
        _stalled = false;
        long t = now - _stallStart;
        StallTicks += t;
        if (t > MaxStallTicks) MaxStallTicks = t;
    }

    public string Summary()
    {
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"{Stalls} slot waits, {StallFrames} frames, {StallTicks * f:F1} ms (max {MaxStallTicks * f:F1} ms)";
    }
}

/// <summary>Runs teardown steps so one failure doesn't skip the rest: every step runs in order, and the first exception
/// is rethrown (with its stack) once they all ran; later ones go to stderr. <see cref="WorldRenderer.Dispose"/> uses it,
/// so a drain that throws after device loss still disposes the workers, the page residencies and the owned renderer,
/// whose Dispose releases the fences <see cref="CopyRetirement"/> still holds.</summary>
public static class Teardown
{
    /// <summary>One-shot <see cref="Run"/>: the first call marks <paramref name="disposed"/> before any step runs, so a
    /// caller that retries after a step threw (the client's facet-rebuild catch disposes the world again) gets a no-op
    /// instead of releasing handles the first call already freed. Returns false when it was already disposed.</summary>
    public static bool Once(ref int disposed, string what, params Action[] steps)
    {
        if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return false;
        Run(what, steps);
        return true;
    }

    public static void Run(string what, params Action[] steps)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo first = null;
        foreach (var step in steps)
        {
            if (step == null) continue;
            try { step(); }
            catch (Exception e)
            {
                if (first == null) first = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
                else Console.Error.WriteLine($"{what}: a later teardown step also failed: {e.Message}");
            }
        }
        first?.Throw();
    }
}
