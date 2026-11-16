using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apm.Agent;

// Exercise the real 5-second runtime loop: a delayed acknowledgement must not
// remove transport time from the denominator while keeping its CPU consumption.
var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var failFirst = args.Contains("--fail-first");
var points = new List<(double Cpu, long Tick, double Reported)>();
using var process = Process.GetCurrentProcess();
using var burnStop = new CancellationTokenSource();
Thread? burner = null;
var handler = new CaptureHandler(async (request, ct) =>
{
    var batch = await request.Content!.ReadFromJsonAsync<TelemetryBatch>(cancellationToken: ct);
    if (batch!.Runtime.Count == 0) return new(HttpStatusCode.Accepted);
    process.Refresh();
    points.Add((process.TotalProcessorTime.TotalMilliseconds, Stopwatch.GetTimestamp(), batch.Runtime.Single().CpuPercent));
    if (points.Count == 1)
    {
        burner = new Thread(() => { while (!burnStop.IsCancellationRequested) Thread.SpinWait(10000); })
        { IsBackground = true, Name = "CPU regression workload" };
        burner.Start();
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        if (failFirst) return new(HttpStatusCode.ServiceUnavailable);
    }
    else if (points.Count >= 2) completed.TrySetResult();
    return new(HttpStatusCode.Accepted);
});
try
{
    await using var sender = new TelemetrySender(new AgentOptions
    { ServiceName = "RuntimeCpuChecks", RequestTimeout = TimeSpan.FromSeconds(20), MaxAttempts = 1 }, "cpu-test", handler);
    await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
}
finally { burnStop.Cancel(); burner?.Join(TimeSpan.FromSeconds(2)); }
var elapsed = Stopwatch.GetElapsedTime(points[0].Tick, points[1].Tick).TotalMilliseconds;
var expected = (points[1].Cpu - points[0].Cpu) / elapsed / Environment.ProcessorCount * 100;
var reported = points[1].Reported;
var passed = elapsed >= 7900 && expected > 0 && double.IsFinite(reported)
    && Math.Abs(reported - expected) <= Math.Max(0.3, expected * 0.15);
Console.WriteLine(JsonSerializer.Serialize(new { test = "CPU interval includes delayed runtime upload", passed,
    delayedUploadFailed = failFirst,
    logicalProcessors = Environment.ProcessorCount, elapsedMs = elapsed, expectedPercent = expected,
    reportedPercent = reported, errorPercentagePoints = Math.Abs(reported - expected) }));
Environment.ExitCode = passed ? 0 : 1;

sealed class CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => callback(request, cancellationToken);
}
