using System.Diagnostics;
using System.Text.Json;

// Opt-in diagnostic startup hook, not referenced by production projects.
// A dedicated thread distinguishes CLR/OS pauses from ThreadPool dispatch delay.
public static class StartupHook
{
    private static long _queuedAt, _lastDispatchTicks, _completed;
    public static void Initialize()
    {
        var output = Environment.GetEnvironmentVariable("APM_DIAG_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        ThreadPool.GetMinThreads(out var originalMin, out var minIo);
        var requested = Environment.GetEnvironmentVariable("APM_DIAG_MIN_THREADS");
        if (int.TryParse(requested, out var minimum) && minimum is > 0 and <= 256)
            ThreadPool.SetMinThreads(minimum, minIo);
        var thread = new Thread(() => Monitor(output, originalMin))
        { IsBackground = true, Name = "Runtime diagnostics sampler" };
        thread.Start();
    }
    private static void Monitor(string output, int originalMin)
    {
        try
        {
            using var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            using var process = Process.GetCurrentProcess();
            var started = Stopwatch.GetTimestamp(); var previous = started; var index = 0;
            ThreadPool.GetMinThreads(out var min, out var minIo);
            writer.WriteLine(JsonSerializer.Serialize(new { type = "config", pid = Environment.ProcessId,
                utc = DateTime.UtcNow, processorCount = Environment.ProcessorCount, originalMin, min, minIo,
                serverGC = System.Runtime.GCSettings.IsServerGC, intervalMs = 100 }));
            while (Stopwatch.GetElapsedTime(started).TotalMinutes < 15)
            {
                var now = Stopwatch.GetTimestamp(); process.Refresh();
                var queuedAt = Interlocked.Read(ref _queuedAt);
                if (queuedAt == 0 && Interlocked.CompareExchange(ref _queuedAt, now, 0) == 0)
                    ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        var since = Interlocked.Read(ref _queuedAt);
                        Interlocked.Exchange(ref _lastDispatchTicks, Stopwatch.GetTimestamp() - since);
                        Interlocked.Increment(ref _completed); Interlocked.Exchange(ref _queuedAt, 0);
                    }, (object?)null);
                ThreadPool.GetAvailableThreads(out var available, out var availableIo);
                var gc = GC.GetGCMemoryInfo();
                writer.WriteLine(JsonSerializer.Serialize(new { type = "sample", utc = DateTime.UtcNow,
                    elapsedMs = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds,
                    intervalMs = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds,
                    cpuMs = process.TotalProcessorTime.TotalMilliseconds, workingSet = process.WorkingSet64,
                    privateBytes = process.PrivateMemorySize64, totalAllocatedBytes = GC.GetTotalAllocatedBytes(),
                    gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                    gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                    heapBytes = gc.HeapSizeBytes, fragmentedBytes = gc.FragmentedBytes, gcIndex = gc.Index,
                    threadPoolThreads = ThreadPool.ThreadCount, threadPoolPending = ThreadPool.PendingWorkItemCount,
                    threadPoolCompleted = ThreadPool.CompletedWorkItemCount, available, availableIo,
                    probePendingMs = queuedAt == 0 ? 0 : Stopwatch.GetElapsedTime(queuedAt, now).TotalMilliseconds,
                    probeLastDelayMs = Interlocked.Read(ref _lastDispatchTicks) * 1000d / Stopwatch.Frequency,
                    probeCompleted = Interlocked.Read(ref _completed) }));
                if (++index % 10 == 0) writer.Flush();
                previous = now; Thread.Sleep(100);
            }
        }
        catch (Exception error) { Console.Error.WriteLine("RuntimeProbe: " + error); }
    }
}
