using CollectionServer.Data;
using CollectionServer.Options;
using CollectionServer.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 개인 환경의 실제 경로(ManagedServers 등)는 git에 커밋되는 appsettings.json 대신
// 이 파일에 넣는다 — .gitignore 처리되어 저장소에는 예시 값만 남는다.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

const string DashboardCorsPolicy = "DashboardCors";

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// 단일 exe로 배포됐을 때 실행 위치(작업 디렉터리)에 상관없이 항상 exe 옆에 DB가 생기도록
// 실행 파일 기준 절대경로를 기본값으로 사용
var defaultDbPath = Path.Combine(AppContext.BaseDirectory, "apm.db");
builder.Services.AddDbContext<ApmDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Apm") ?? $"Data Source={defaultDbPath}"));

builder.Services.Configure<LmStudioOptions>(builder.Configuration.GetSection(LmStudioOptions.SectionName));
builder.Services.Configure<AnomalyDetectionOptions>(builder.Configuration.GetSection(AnomalyDetectionOptions.SectionName));

builder.Services.AddHttpClient<ILmStudioClient, LmStudioClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LmStudioOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
});

builder.Services.AddScoped<IAnomalyDetectionService, AnomalyDetectionService>();

builder.Services.Configure<List<ManagedServerConfig>>(builder.Configuration.GetSection("ManagedServers"));
builder.Services.AddSingleton<IManagedServerService, ManagedServerService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(DashboardCorsPolicy, policy =>
    {
        policy.WithOrigins(
                builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:5173"])
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApmDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(DashboardCorsPolicy);

app.UseAuthorization();

// dashboard(React) 빌드 결과물(wwwroot) 서빙 — CollectionServer 하나만 실행하면
// API + 대시보드가 같은 포트에서 동시에 뜨도록 (별도 Vite dev server 불필요)
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
