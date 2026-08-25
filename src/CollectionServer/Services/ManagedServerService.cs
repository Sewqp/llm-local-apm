using System.Diagnostics;
using System.Text.Json;
using CollectionServer.Dtos;
using CollectionServer.Options;
using Microsoft.Extensions.Options;

namespace CollectionServer.Services;

public interface IManagedServerService
{
    List<ManagedServerStatusDto> GetAll();
    Task<bool> StartAsync(string name);
    bool Stop(string name);
    (bool Success, string? Error) AddServer(CreateManagedServerRequest request);
    (bool Success, string? Error) RemoveServer(string name);
}

/// <summary>
/// 대시보드의 "서버 관리" 패널에서 시작/중지/등록/삭제할 수 있는 대상 서버들을 관리한다.
/// appsettings.json의 ManagedServers(IsBuiltIn=true)는 삭제 불가, UI에서 등록한 항목은
/// managed-servers.json에 저장되어 재시작 후에도 유지된다.
/// 로컬 전용 도구 전제 — 이 API에 접근 가능하면 임의 명령 실행이 가능하다(의도된 트레이드오프).
/// </summary>
public class ManagedServerService(IOptions<List<ManagedServerConfig>> options, ILogger<ManagedServerService> logger)
    : IManagedServerService, IDisposable
{
    private readonly string _customConfigPath = Path.Combine(AppContext.BaseDirectory, "managed-servers.json");
    private readonly object _configLock = new();
    private readonly List<ManagedServerConfig> _configs = LoadAll(options.Value ?? []);
    private readonly Dictionary<string, Process> _processes = new();

    private static List<ManagedServerConfig> LoadAll(List<ManagedServerConfig> builtins)
    {
        foreach (var c in builtins) c.IsBuiltIn = true;

        var customPath = Path.Combine(AppContext.BaseDirectory, "managed-servers.json");
        var custom = new List<ManagedServerConfig>();
        if (File.Exists(customPath))
        {
            try
            {
                custom = JsonSerializer.Deserialize<List<ManagedServerConfig>>(File.ReadAllText(customPath)) ?? [];
                foreach (var c in custom) c.IsBuiltIn = false;
            }
            catch
            {
                // 손상된 파일은 무시하고 빈 목록으로 시작
            }
        }

        return [.. builtins, .. custom];
    }

    private void PersistCustom()
    {
        var custom = _configs.Where(c => !c.IsBuiltIn).ToList();
        File.WriteAllText(_customConfigPath, JsonSerializer.Serialize(custom, new JsonSerializerOptions { WriteIndented = true }));
    }

    public List<ManagedServerStatusDto> GetAll()
    {
        lock (_configLock)
        {
            return _configs.Select(c =>
            {
                var running = _processes.TryGetValue(c.Name, out var p) && !HasExited(p);
                return new ManagedServerStatusDto(c.Name, running, running ? p!.Id : null, c.IsBuiltIn);
            }).ToList();
        }
    }

    public (bool Success, string? Error) AddServer(CreateManagedServerRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        var workingDirectory = request.WorkingDirectory?.Trim() ?? "";
        var command = request.Command?.Trim() ?? "";

        if (name.Length == 0) return (false, "이름을 입력하세요.");
        if (workingDirectory.Length == 0) return (false, "작업 디렉터리를 입력하세요.");
        if (command.Length == 0) return (false, "실행 명령을 입력하세요.");
        if (!Directory.Exists(workingDirectory)) return (false, $"디렉터리를 찾을 수 없습니다: {workingDirectory}");

        lock (_configLock)
        {
            if (_configs.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return (false, $"'{name}' 이름이 이미 등록되어 있습니다.");

            _configs.Add(new ManagedServerConfig
            {
                Name = name,
                WorkingDirectory = workingDirectory,
                Command = command,
                Arguments = request.Arguments?.Trim() ?? "",
                IsBuiltIn = false,
            });
            PersistCustom();
        }

        return (true, null);
    }

    public (bool Success, string? Error) RemoveServer(string name)
    {
        lock (_configLock)
        {
            var config = _configs.FirstOrDefault(c => c.Name == name);
            if (config is null) return (false, $"'{name}' 서버를 찾을 수 없습니다.");
            if (config.IsBuiltIn) return (false, "appsettings.json에 등록된 서버는 삭제할 수 없습니다.");

            if (_processes.TryGetValue(name, out var process) && !HasExited(process))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { logger.LogWarning(ex, "삭제 전 서버 '{Name}' 종료 실패", name); }
            }

            _configs.Remove(config);
            _processes.Remove(name);
            PersistCustom();
        }

        return (true, null);
    }

    public async Task<bool> StartAsync(string name)
    {
        ManagedServerConfig? config;
        lock (_configLock)
        {
            config = _configs.FirstOrDefault(c => c.Name == name);
            if (config is null) return false;
            if (_processes.TryGetValue(name, out var existing) && !HasExited(existing))
                return true; // 이미 실행 중
        }

        var workingDirectory = Path.IsPathRooted(config.WorkingDirectory)
            ? config.WorkingDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.WorkingDirectory));

        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "server-logs");
            Directory.CreateDirectory(logDir);
            var safeFileName = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var logPath = Path.Combine(logDir, $"{safeFileName}.log");

            var startInfo = new ProcessStartInfo
            {
                FileName = config.Command,
                Arguments = config.Arguments,
                WorkingDirectory = workingDirectory,
                // UseShellExecute:true로 콘솔 창을 새로 띄우는 방식은 CollectionServer가 콘솔이 없는
                // 환경(백그라운드/서비스로 실행 등)에서 자식 프로세스가 뜨지 않거나 멈추는 문제가 있어,
                // CreateProcess를 직접 쓰는 방식(UseShellExecute:false)으로 대체하고 로그는 파일로 남긴다.
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // 시작할 때마다 새 로그로 덮어써서 최근 실행 결과만 남긴다
            await File.WriteAllTextAsync(logPath, string.Empty);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => AppendLog(logPath, e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog(logPath, e.Data);

            if (!await Task.Run(() => process.Start())) return false;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_configLock) { _processes[name] = process; }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "서버 '{Name}' 시작 실패", name);
            return false;
        }
    }

    public bool Stop(string name)
    {
        lock (_configLock)
        {
            if (!_processes.TryGetValue(name, out var process) || HasExited(process))
                return false;

            try
            {
                // dotnet run은 실제 앱을 자식 프로세스로 띄우므로 트리 전체를 종료해야 함
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "서버 '{Name}' 중지 실패", name);
                return false;
            }
        }
    }

    private static readonly object LogFileLock = new();

    private static void AppendLog(string path, string? line)
    {
        if (line is null) return;
        lock (LogFileLock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    // CollectionServer가 정상 종료(Ctrl+C 등)될 때 자신이 띄운 대상 서버들도 같이 정리해
    // 포트를 물고 있는 orphan 프로세스가 남지 않도록 한다.
    public void Dispose()
    {
        lock (_configLock)
        {
            foreach (var (name, process) in _processes)
            {
                try
                {
                    if (!HasExited(process))
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "종료 중 서버 '{Name}' 정리 실패", name);
                }
            }
        }
    }
}
