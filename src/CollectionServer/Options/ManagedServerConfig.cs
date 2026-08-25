namespace CollectionServer.Options;

/// <summary>
/// 대시보드에서 시작/중지할 수 있는 대상 서버 하나의 실행 정보.
/// appsettings.json에 등록된 항목(IsBuiltIn=true)은 UI에서 삭제할 수 없고,
/// 대시보드의 "서버 등록" 폼으로 추가한 항목(IsBuiltIn=false)은 managed-servers.json에 저장되어
/// 재시작 후에도 유지되며 UI에서 삭제 가능하다.
/// 로컬 전용 도구를 전제로 한 기능 — 이 화면에 접근 가능한 사람은 누구나 임의 명령을 등록/실행할 수 있다.
/// </summary>
public class ManagedServerConfig
{
    public string Name { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
}
