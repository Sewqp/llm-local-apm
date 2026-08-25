using TargetServer.Metrics;
using TargetServer.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.Configure<MetricsOptions>(builder.Configuration.GetSection(MetricsOptions.SectionName));
builder.Services.AddSingleton<MetricsQueue>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<MetricsSenderHostedService>();
builder.Services.AddHostedService<TrafficGeneratorHostedService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 기존 서버에 계측을 추가할 때 필요한 것은 사실상 이 한 줄뿐
app.UseMiddleware<InstrumentationMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
