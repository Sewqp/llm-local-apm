using System.Text;
using Apm.Agent;
using ArchitectureDemo;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var role = builder.Configuration["role"] ?? "edge";
if (role is not ("edge" or "stock")) throw new ArgumentException("role: edge or stock");
var port = role == "edge" ? 5241 : 5242;
// This fixture is deliberately local-only, including its outgoing HTTP dependency.
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var run = builder.Configuration["run"] ?? throw new ArgumentException("--run is required");
var db = builder.Configuration["db"] ?? throw new ArgumentException("--db must point to a new fixture database");
var telemetry = new LocalTelemetry(new AgentOptions
{
    ServiceName = role == "edge" ? "ArchitectureEdge" : "ArchitectureStock", RunId = run,
    CodeVersion = "architecture-fixture-v1", Endpoint = new Uri(builder.Configuration["collector"] ?? "http://127.0.0.1:5239"),
    DetailSampleRate = 1, CaptureDependencies = false,
    // Public synthetic fixture key. Never reuse this key for real user identifiers.
    UserHashKey = Encoding.UTF8.GetBytes("architecture-demo-synthetic-key-0001")
});
builder.Services.AddSingleton(telemetry);
builder.Services.AddSingleton(new CatalogStore(db));
builder.Services.AddSingleton<DemoStats>();
builder.Services.AddSingleton<RemoteCheckout>();
builder.Services.AddSingleton<RewardQueue>();
if (role == "edge") builder.Services.AddHostedService<RewardWorker>();
await using var app = builder.Build();
app.UseRouting();
app.UseMiddleware<DemoTelemetryMiddleware>(role);
app.MapGet("/health", () => new { role, run, pid = Environment.ProcessId });
app.MapGet("/stats", (DemoStats stats, RewardQueue jobs) => new { role, run, pid = Environment.ProcessId,
    requests = stats.Snapshot(), jobs = jobs.Snapshot(), agent = telemetry.Sender.Health });
if (role == "stock") StockHandler.Map(app);
else
{
    CheckoutHandler.Map(app);
    app.MapGet("/demo/remote", (HttpContext ctx, RemoteCheckout remote) => remote.ExecuteAsync(ctx, false))
        .WithMetadata(new SourceHint("RemoteCheckout.cs"));
    app.MapGet("/demo/remote-error", (HttpContext ctx, RemoteCheckout remote) => remote.ExecuteAsync(ctx, true))
        .WithMetadata(new SourceHint("RemoteCheckout.cs"));
    app.MapPost("/demo/jobs", (HttpContext ctx, RewardQueue jobs) => jobs.Enqueue(ctx, telemetry))
        .WithMetadata(new SourceHint("RewardQueue.cs"));
    app.MapGet("/jobs/{id}", (string id, RewardQueue jobs) => jobs.Find(id));
}
try { await app.RunAsync(); }
finally { await telemetry.DisposeAsync(); }
