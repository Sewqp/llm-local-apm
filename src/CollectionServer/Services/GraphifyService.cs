using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CollectionServer.Options;
using Microsoft.Extensions.Options;

namespace CollectionServer.Services;

public interface IGraphifyService
{
    /// <summary>
    /// LLM이 지목한 "의심 코드" 파일 경로를 실제 소스 파일로 풀어내기 위한 서버별 소스 디렉터리.
    /// 등록 안 된 서버면 null.
    /// </summary>
    string? GetSourceDir(string serverName);

    /// <summary>
    /// 정밀탐색(tool-calling) 전용 — LLM이 직접 고른 정확한 이름으로 그래프 노드를 찾는다.
    /// 패킷 이름 기반 후보 추측과 달리, 이름 규칙에 안 맞는 코드(백그라운드 서비스 등)도 LLM이
    /// 알아서 이름을 대면 찾을 수 있다. 그래프/서버 없으면 null.
    /// </summary>
    Task<string?> ExplainAsync(string serverName, string name, CancellationToken ct);

    /// <summary>정밀탐색 전용 — 정확한 이름을 모를 때 검색어로 넓게 훑는다.</summary>
    Task<string?> QueryAsync(string serverName, string term, CancellationToken ct);
}

/// <summary>
/// Graphify(https://github.com/Graphify-Labs/graphify) CLI를 서브프로세스로 호출해, 원시 소스 대신
/// 미리 만들어둔 코드 지식그래프를 LLM이 직접 tool-calling으로 탐색하게 해준다("정밀탐색"). 이름 규칙
/// 기반 자동 추측(예전 GetCodeContextAsync)은 패킷 이름과 무관한 백그라운드 코드를 못 찾는 한계가
/// 있어서 폐기하고, LLM이 스스로 이름을 골라 explain/query를 반복 호출하는 방식으로 대체함.
/// </summary>
public class GraphifyService(IOptions<GraphifyOptions> options, ILogger<GraphifyService> logger) : IGraphifyService
{
    private readonly GraphifyOptions _options = options.Value;

    // 서버당 그래프 빌드는 한 번만 시도 — 실패해도 매 분석 요청마다 재시도하지 않음(로그만 반복해서 남는 것 방지).
    private readonly ConcurrentDictionary<string, bool> _graphReady = new();

    public string? GetSourceDir(string serverName) =>
        _options.ServerSourceDirs.TryGetValue(serverName, out var dir) && Directory.Exists(dir) ? dir : null;

    public async Task<string?> ExplainAsync(string serverName, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!_options.ServerSourceDirs.TryGetValue(serverName, out var sourceDir) || !Directory.Exists(sourceDir))
            return null;
        if (!await EnsureGraphAsync(sourceDir, ct)) return null;

        var graphPath = System.IO.Path.Combine(sourceDir, "graphify-out", "graph.json");
        var result = await RunAsync(sourceDir, ["explain", name, "--graph", graphPath], ct);
        if (result is null || result.Contains("No node matching", StringComparison.OrdinalIgnoreCase)) return null;
        return Truncate(result);
    }

    public async Task<string?> QueryAsync(string serverName, string term, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        if (!_options.ServerSourceDirs.TryGetValue(serverName, out var sourceDir) || !Directory.Exists(sourceDir))
            return null;
        if (!await EnsureGraphAsync(sourceDir, ct)) return null;

        var graphPath = System.IO.Path.Combine(sourceDir, "graphify-out", "graph.json");
        var result = await RunAsync(sourceDir, ["query", term, "--graph", graphPath], ct);
        if (result is null || result.Contains("No matching nodes found", StringComparison.OrdinalIgnoreCase)) return null;
        return Truncate(result);
    }

    private async Task<bool> EnsureGraphAsync(string sourceDir, CancellationToken ct)
    {
        if (_graphReady.TryGetValue(sourceDir, out var ready)) return ready;

        var graphPath = System.IO.Path.Combine(sourceDir, "graphify-out", "graph.json");
        if (File.Exists(graphPath))
        {
            _graphReady[sourceDir] = true;
            return true;
        }

        logger.LogInformation("Graphify 그래프가 없어 새로 빌드합니다: {Dir}", sourceDir);
        var output = await RunAsync(sourceDir, ["extract", ".", "--code-only", "--no-viz"], ct, extractTimeout: true);
        var success = output is not null && File.Exists(graphPath);
        _graphReady[sourceDir] = success;
        if (!success)
            logger.LogWarning("Graphify 그래프 빌드 실패: {Dir}\n{Output}", sourceDir, output);
        return success;
    }

    private async Task<string?> RunAsync(
        string workingDirectory, string[] args, CancellationToken ct, bool extractTimeout = false)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.CliPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) startInfo.ArgumentList.Add(a);

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // extract(빌드)는 explain/query보다 오래 걸릴 수 있어 별도로 더 긴 타임아웃을 준다.
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(extractTimeout ? _options.TimeoutSeconds * 4 : _options.TimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 이미 종료된 경우 무시 */ }
                logger.LogWarning("Graphify CLI 타임아웃: {Args}", string.Join(' ', args));
                return null;
            }

            return process.ExitCode == 0 || stdout.Length > 0 ? stdout.ToString() : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Graphify CLI 실행 실패 (설치됐는지, CliPath 설정이 맞는지 확인): {Args}", string.Join(' ', args));
            return null;
        }
    }

    private string Truncate(string text)
    {
        text = text.Trim();
        return text.Length <= _options.MaxContextChars ? text : text[.._options.MaxContextChars] + "\n... (생략)";
    }
}
