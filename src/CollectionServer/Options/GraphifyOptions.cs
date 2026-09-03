namespace CollectionServer.Options;

/// <summary>
/// Graphify(로컬 CLI, https://github.com/Graphify-Labs/graphify) 연동 설정.
/// ServerName(메트릭에 찍히는 이름) -> 그 서버 소스 디렉터리 매핑이 핵심 — ManagedServers의 Name(대시보드
/// 표시용)과는 별개 네임스페이스라 재사용하지 않는다(TargetServer는 "TargetServer (데모)" vs "TargetServer"처럼 다름).
/// </summary>
public class GraphifyOptions
{
    public const string SectionName = "Graphify";

    public string CliPath { get; set; } = "graphify";
    public Dictionary<string, string> ServerSourceDirs { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxContextChars { get; set; } = 3000;
}
