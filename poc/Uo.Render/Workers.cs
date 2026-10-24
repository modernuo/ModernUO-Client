namespace Uo.Render;

public abstract class Job
{
    public long EnqueuedAt, RunTicks; // Stopwatch ticks, for latency stats
    public abstract void Run();
}

/// <summary>Dedicated worker threads with a high-priority queue (texture pages) and a normal queue (regions, chunk bakes).
/// Completed jobs come back on a completion queue drained by the main thread. Queues are preallocated; no steady-state allocation.</summary>
public sealed class WorkerPool : IDisposable
{
    readonly Queue<Job> _high = new(1024), _normal = new(8192), _done = new(8192);
    readonly object _lock = new();
    readonly Thread[] _threads;
    volatile bool _stop;
    int _inFlight;

    public WorkerPool(int count)
    {
        _threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            _threads[i] = new Thread(Loop) { IsBackground = true, Name = $"uo-worker-{i}" }; // normal priority: streaming latency is visible (pop-in) when starved
            _threads[i].Start();
        }
    }

    public int InFlight => Volatile.Read(ref _inFlight);

    public void Enqueue(Job job, bool high)
    {
        lock (_lock)
        {
            job.EnqueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            (high ? _high : _normal).Enqueue(job);
            _inFlight++;
            Monitor.Pulse(_lock);
        }
    }

    public bool TryDequeueDone(out Job job)
    {
        lock (_done)
            return _done.TryDequeue(out job);
    }

    void Loop()
    {
        while (true)
        {
            Job job;
            lock (_lock)
            {
                while (_high.Count == 0 && _normal.Count == 0 && !_stop) Monitor.Wait(_lock);
                if (_stop) return;
                job = _high.Count > 0 ? _high.Dequeue() : _normal.Dequeue();
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { job.Run(); }
            catch (Exception e) { Console.Error.WriteLine($"worker: {job.GetType().Name} failed: {e.Message}"); }
            job.RunTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            lock (_done) _done.Enqueue(job);
            lock (_lock) _inFlight--;
        }
    }

    public void Dispose()
    {
        lock (_lock) { _stop = true; Monitor.PulseAll(_lock); }
        foreach (var t in _threads) t.Join(1000);
    }
}
