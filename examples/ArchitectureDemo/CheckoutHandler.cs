using Apm.Agent;

namespace ArchitectureDemo;

public static class CheckoutHandler
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/demo/sequential", SequentialAsync).WithMetadata(new SourceHint("CheckoutHandler.cs"));
        app.MapGet("/demo/parallel", ParallelAsync).WithMetadata(new SourceHint("CheckoutHandler.cs"));
        app.MapGet("/demo/slow", async () =>
        { using var span = LocalTelemetry.StartSpan("simulation.slow.wait", "wait"); await Task.Delay(200); return Results.Ok(); })
            .WithMetadata(new SourceHint("CheckoutHandler.cs"));
        app.MapGet("/demo/error", () => Fail(false)).WithMetadata(new SourceHint("CheckoutHandler.cs"));
        app.MapGet("/demo/timeout", () => Fail(true)).WithMetadata(new SourceHint("CheckoutHandler.cs"));
        app.MapGet("/demo/rejected", () => Results.Conflict(new { error = "Fixture business rejection" }))
            .WithMetadata(new SourceHint("CheckoutHandler.cs"));
    }
    private static async Task<IResult> SequentialAsync(CatalogStore store)
    {
        var inventory = await store.ReadAsync("inventory", 15);
        var price = await store.ReadAsync("price", 25);
        var eligibility = await store.ReadAsync("eligibility", 35);
        return Results.Ok(new[] { inventory, price, eligibility });
    }
    private static async Task<IResult> ParallelAsync(CatalogStore store)
    {
        var values = await Task.WhenAll(store.ReadAsync("inventory", 15), store.ReadAsync("price", 25), store.ReadAsync("eligibility", 35));
        return Results.Ok(values);
    }
    private static IResult Fail(bool timeout) => throw (timeout ? new TimeoutException("SECRET_TIMEOUT_DO_NOT_EXPORT")
        : new InvalidOperationException("SECRET_EXCEPTION_DO_NOT_EXPORT"));
}
