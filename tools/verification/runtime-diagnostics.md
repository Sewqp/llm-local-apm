# CPU 회귀 검사와 Windows 런타임 정체 조사

실행 결과와 해석은 [2026-11-16 검증 기록](../../docs/runtime-cpu-stall-validation-20261116.md)을 따른다. 여기는 재현 방법이다. 3만 연결 시험은 같은 PC의 다른 작업에도 지연을 줄 수 있다. 별도 시험 환경에서 실행하고, 원래 서비스·DB를 종료하거나 초기화하지 않는다.

## CPU 산식 검사

APM 저장소에서 실행한다. 네트워크 서버나 DB 없이 실제 Agent 타이머와 가짜 HTTP 전송기를 사용한다. 각 실행은 약 14초 걸리며, 한 작업 스레드가 약 8초 동안 CPU를 사용한다.

```powershell
dotnet run --project tools/verification/RuntimeCpuChecks -c Release
dotnet run --project tools/verification/RuntimeCpuChecks -c Release -- --fail-first
```

첫 업로드를 8초 지연시키고 다음 CPU 보고값을 `Process.TotalProcessorTime`과 monotonic 경과 시간으로 계산한 값과 비교한다. 두 번째 명령은 지연된 업로드에 HTTP 503을 반환한다. 허용 오차는 0.3%p와 기준값의 15% 중 큰 값이다. 11월 16일 실제 오차는 각각 0.011%p, 0.004%p 이내였다. 구버전 Agent를 별도로 복사하고 `-p:ApmAgentProject=<구버전 csproj>`로 연결하면 회귀 전 실패를 재현할 수 있다. 현재 작업 트리를 되돌릴 필요는 없다.

## 관찰용 startup hook

```powershell
dotnet build tools/verification/RuntimeProbe -c Release
```

시험 프로세스에만 `DOTNET_STARTUP_HOOKS=<RuntimeProbe.dll 절대 경로>`, `APM_DIAG_OUTPUT=<새 JSONL 경로>`를 설정한다. 기존 파일은 덮어쓰지 않는다. 100ms 간격의 전용 스레드로 GC 누적 정지 시간, 프로세스 CPU, ThreadPool 대기·스레드 수와 작은 작업의 실행 지연을 관찰한다. 최대 15분 후 종료한다. 관찰 자체에도 비용이 있으며 운영 프로젝트에서는 참조하지 않는다. `APM_DIAG_MIN_THREADS` 옵션은 이번 검증에서 사용하지 않았다.

## 게임 없는 TCP 대조 시험

Windows, Python 3, .NET 10 SDK가 필요하다. TcpExpiryProbe는 외부 패키지 없이 loopback 연결을 열고 정상 종료한 뒤 145초 기다린다. 서버·DB·Orleans·APM은 실행하지 않는다.

```powershell
dotnet build tools/verification/TcpExpiryProbe -c Release
python tools/verification/run_tcp_expiry.py --output .local-evidence/tcp-expiry-new --probe tools/verification/TcpExpiryProbe/bin/Release/net10.0/TcpExpiryProbe.dll --runtime-probe tools/verification/RuntimeProbe/bin/Release/net10.0/RuntimeProbe.dll --count 30000
```

출력 폴더는 실행마다 새로 지정한다. 호스트 전체 TIME_WAIT가 2,000개 이상이면 시작하지 않는다. 이 값은 3만 연결 잔여 부하를 구분하기 위한 시험용 문턱이며 보편적인 안전 기준이 아니다. 종료 직후와 최종 시점에만 TCP 목록을 읽어, 만료 구간의 반복 조회가 없어도 현상이 나타나는지 확인한다. PDH 호스트 지표와 로컬 프로세스 이름이 원시 기록에 포함되므로 공개 전 확인한다.

## 격리 DB를 사용하는 게임 서버 시험

`run_runtime_investigation.py`는 이번 PC의 Windows PostgreSQL 18 및 Windows Redis 설치 경로를 사용하는 전용 하네스다. 일반 배포 스크립트가 아니다. 아래 `$work`는 새 비공개 폴더, `$server`는 Trickcal 서버 저장소 절대 경로로 지정한다. `$apm`은 현재 APM 저장소다.

```powershell
$apm = (Get-Location).Path
$server = 'C:/test/Trickcal-Bottleneck-Server'
$work = 'C:/test/private-runtime-new'
dotnet publish "$server/src/GameServer/GameServer.csproj" -c Release -o "$work/game" "-p:ApmAgentProject=$apm/src/Apm.Agent/Apm.Agent.csproj"
dotnet publish src/CollectionServer/CollectionServer.csproj -c Release -o "$work/collector"
dotnet publish tools/verification/ConcurrentLoad/ConcurrentLoad.csproj -c Release -o "$work/client"
dotnet build tools/verification/RuntimeProbe -c Release
python tools/verification/run_runtime_investigation.py --work "$work" --server-root "$server" --apm-root "$apm" --case guarded_off_01 --apm off --hold 120
```

PG 25446, Redis 26396, 수집기 5256, 게임 TCP 9321, Orleans 11321/30321/8931이 비어 있어야 한다. 새 전용 PG 클러스터와 실행별 DB·Redis·SQLite를 만들며, 자신이 시작한 프로세스만 정리한다. 기존 폴더·DB 이름을 재사용하지 않는다. PG는 loopback trust 인증이므로 이 설정을 외부 접속 가능한 운영 환경에 사용하지 않는다.

기본 절차는 1,000 연결 준비 → 30,000 로그인 → Heartbeat 5초 간격으로 유지 → 종료·수집 대조다. 시험 전 전체 TIME_WAIT가 2,000 미만인 상태를 2초 간격 두 번 확인하며, 최대 300초 내 정리되지 않으면 부하를 시작하지 않는다. `--apm on`으로 수집 대조도 수행할 수 있다. `--allow-timewait-overlap`은 앞선 연결 만료와 겹치는 진단에만 사용하고, 일반 비용 비교에는 사용하지 않는다.

`--split-cpus`, `--min-threads`, `--stacks`는 실험용 선택지이며 이번 결과에서는 사용하지 않았다. CPU affinity는 물리적으로 분리된 부하 발생기를 대신하지 않는다. TCP 대조용 Python 공개 실행기는 당시 스크립트에 경로 인자와 사전 검사를 추가한 버전이다. 대규모 실측은 기록에 적힌 당시 버전으로 수행했으며, 공개 실행기는 Python 구문과 명령행 인자 안내를 확인했다. 경로 인자를 추가한 버전으로 3만 연결 시험을 다시 실행하지는 않았다.
