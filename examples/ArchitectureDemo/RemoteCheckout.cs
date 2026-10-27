using System.Diagnostics;
using Apm.Agent;

namespace ArchitectureDemo;

public sealed class RemoteCheckout : IDisposable
{
    // Manual propagation links the collected client span, not an uncollected HttpClient span.
    private readonly HttpClient _http = new(new SocketsHttpHandler { ActivityHeadersPropagator = null, AllowAutoRedirect = false })
    { BaseAddress = new Uri("http://127.0.0.1:5242"), Timeout = TimeSpan.FromSeconds(5) };
    public async Task<IResult> ExecuteAsync(HttpContext ctx, bool fail)
    {
        using var span = LocalTelemetry.StartSpan("http.stock", "http-client");
        using var request = new HttpRequestMessage(HttpMethod.Get, fail ? "/stock/error" : "/stock");
        request.Headers.Add("traceparent", Activity.Current!.Id);
        request.Headers.Add("X-Demo-User", ctx.Items["user"]!.ToString());
        request.Headers.Add("X-Demo-Session", ctx.Items["session"]!.ToString());
        try
        {
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                span?.SetStatus(ActivityStatusCode.Error);
                return Results.StatusCode((int)response.StatusCode);
            }
            return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");
        }
        catch (HttpRequestException ex)
        { LocalTelemetry.RecordException(ex); return Results.StatusCode(502); }
        catch (TaskCanceledException ex)
        { LocalTelemetry.RecordException(new TimeoutException("Fixture dependency timeout", ex)); return Results.StatusCode(504); }
    }
    public void Dispose() => _http.Dispose();
}
