namespace ArchitectureDemo;

public static class StockHandler
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/stock", async (CatalogStore store) => Results.Ok(new { available = await store.ReadAsync("inventory", 20) }))
            .WithMetadata(new SourceHint("StockHandler.cs"));
        app.MapGet("/stock/error", Fail).WithMetadata(new SourceHint("StockHandler.cs"));
    }
    private static IResult Fail() => throw new InvalidOperationException("SECRET_DOWNSTREAM_DO_NOT_EXPORT");
}
