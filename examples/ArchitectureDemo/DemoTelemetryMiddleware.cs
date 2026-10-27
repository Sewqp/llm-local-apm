using System.Collections.Concurrent;
using System.Diagnostics;
using Apm.Agent;

namespace ArchitectureDemo;

public sealed record SourceHint(string File);

public sealed class DemoStats
{
    private readonly ConcurrentDictionary<string, long> _counts = new();
    public void Record(string route, int status) => _counts.AddOrUpdate($"{route}|{status}", 1, (_, n) => n + 1);
    public Dictionary<string, long> Snapshot() => new(_counts);
}

public sealed class DemoTelemetryMiddleware(RequestDelegate next, string role)
{
    public async Task InvokeAsync(HttpContext ctx, LocalTelemetry telemetry, DemoStats stats)
    {
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<SourceHint>() is not { } source)
        { await next(ctx); return; }
        var route = ((RouteEndpoint)ctx.GetEndpoint()!).RoutePattern.RawText!;
        var user = long.TryParse(ctx.Request.Headers["X-Demo-User"], out var number) && number is > 0 and <= 100000 ? number : 1;
        var session = Guid.TryParse(ctx.Request.Headers["X-Demo-Session"], out var parsed) ? parsed : Guid.NewGuid();
        ActivityContext parent = default;
        if (role == "stock") ActivityContext.TryParse(ctx.Request.Headers["traceparent"], null, true, out parent);
        using var entry = telemetry.StartHttpRequest(ctx.Request.Method, route, session, user, parent, source.File);
        ctx.Items["user"] = user;
        ctx.Items["session"] = session;
        ctx.Response.Headers["X-Demo-TraceId"] = entry!.TraceId.ToHexString();
        ctx.Response.Headers["X-Demo-SpanId"] = entry.SpanId.ToHexString();
        try
        {
            await next(ctx);
            LocalTelemetry.SetOutcome(ctx.Response.StatusCode >= 500 ? "error" : ctx.Response.StatusCode >= 400 ? "rejected" : "success");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            LocalTelemetry.RecordException(ex);
            ctx.Response.StatusCode = ex is TimeoutException ? 504 : 500;
            await ctx.Response.WriteAsJsonAsync(new { error = "Intentional fixture failure" });
        }
        finally
        {
            entry.SetTag("http.response.status_code", ctx.Response.StatusCode);
            stats.Record(route, ctx.Response.StatusCode);
        }
    }
}
