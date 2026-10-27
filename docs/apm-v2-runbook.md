# Local APM v2 실행·검증 안내

## 기본 실행

필요 환경: .NET 10 SDK, Node/npm, 선택적으로 LM Studio의 로컬 OpenAI 호환 서버. 대시보드·수집·조회는 LLM 없이도 동작한다.

APM 저장소에서 한 번 `npm --prefix dashboard ci`로 패키지를 설치한다. 이후:

```powershell
./tools/start-local.ps1
```

스크립트는 대시보드 빌드와 CollectionServer Release publish 후 `http://127.0.0.1:5219`를 제공한다. DB 기본값은 `.local-evidence/live/apm-v2.db`이며 과거 두 `apm.db`를 사용하지 않는다. 종료는 실행 터미널의 Ctrl+C다. 실행 디렉터리의 환경변수/`appsettings.Local.json`을 점검하고 공개 저장소에 개인 설정을 올리지 않는다.

별도 포트·DB를 지정할 수 있다:

```powershell
./tools/start-local.ps1 -Port 5229 -DatabasePath 'C:/APM-private/run.db'
```

위 경로는 예시다. 자신이 관리하는 로컬 경로로 바꾼다. 기존 DB로 스키마를 확장하려면 먼저 SQLite backup API로 사본을 만들고 그 사본을 지정한다. 원본을 초기화하지 않는다.

## 게임 서버 연결

`apm`과 `Trickcal-Bottleneck-Server` 저장소를 같은 부모 디렉터리에 놓으면 프로젝트 참조가 연결된다. 다른 구조에서는 게임 빌드 시 `-p:ApmAgentProject=<Apm.Agent.csproj의 절대 경로>`를 지정한다.

게임 서버 실행 환경:

```powershell
$env:APM_COLLECTOR_ENDPOINT = 'http://127.0.0.1:5219'
$env:APM_RUN_ID = 'my-validation-run'
$env:APM_CODE_VERSION = '검증할-commit-또는-작업트리-식별자'
dotnet run --project src/GameServer
```

`APM_RUN_ID`는 실행마다 구분해 지정한다. 코드 버전은 자동 Git 검증값이 아니라 사용자가 지정하는 라벨이다. 연결할 PostgreSQL/Redis는 `TRICKCAL_POSTGRES_CONN`, `TRICKCAL_REDIS_CONN`을 명시해 다른 프로젝트 DB와 혼동하지 않도록 한다. 이번 변경은 `DebugHandler`에 있던 의도적 오류를 유지하므로 이 시험용 서버를 운영 배포 대상으로 취급하지 않는다.

유저별 흐름은 로그인 후 생성되는 가명 키로 조회한다. 실행을 넘어서 동일 유저를 추적해야 할 때만 `APM_USER_HASH_KEY`에 32바이트 이상 비밀 키를 설정한다. 기본값은 실행마다 새 키이므로 과거 실행과 연결되지 않는다. 화면의 유저 키를 클릭하거나 유저·세션 필터에 복사한다.

## 로컬 LLM

LM Studio에서 사용 가능한 모델을 로드하고 localhost:1234 서버를 실행한다. 수집기 실행 전에 다음 설정을 지정할 수 있다:

```powershell
$env:LmStudio__BaseUrl = 'http://127.0.0.1:1234'
$env:LmStudio__Model = 'LM-Studio에-로드한-모델-식별자'
$env:LmStudio__TimeoutSeconds = '150'
[Environment]::SetEnvironmentVariable(
  'Graphify__ServerSourceDirs__내-서비스-이름',
  'C:/내프로젝트/src/GameServer', 'Process')
```

서비스 이름은 게임 서버의 `ORLEANS_SERVICE_ID`와 같아야 한다. `appsettings.Local.json`을 사용할 경우 같은 설정이 환경변수와 중복되지 않게 관리한다. 현재 프로그램은 추가 로컬 설정 파일을 나중에 읽으므로 동일 키가 있으면 해당 파일 값이 우선할 수 있다.

요청을 선택하고 **이 요청 분석**을 누른다. 스냅샷의 span/log/code가 근거이며 답변은 검증할 후보이다. 코드가 연결되지 않은 요청은 관측 정보만으로 분석한다. 코드 이름 추론은 모든 패킷의 정확한 핸들러 매핑을 보장하지 않는다. 기존 Graphify 도구 호출은 별도 로컬 실행 환경이 필요하다.

## 이번 검증 환경

| 용도 | 포트/이름 |
|---|---|
| 검증 대시보드·수집기 | `http://127.0.0.1:5229` |
| 게임 TCP | 9129 |
| PostgreSQL | 15439 / `apm_verification` / `apmtest` |
| Redis | 16389 |
| Orleans Silo / Gateway / Dashboard | 11129 / 30029 / 8899 |
| 서비스 / Run | `Trickcal-APM-Verification` / `game-integration-20261026` |
| LM Studio | localhost:1234 |

당일에는 작업용 디렉터리에 별도 PostgreSQL 클러스터와 Redis 설정을 만들어 사용했다. 이 표는 테스트 fixture용이다. 원래 PostgreSQL 5432, Redis 6379, 기존 게임 DB를 지우거나 대체하지 않는다. 재현할 때 새 PostgreSQL DB에 게임 저장소의 `DB/schema.sql`, `DB/seed_characters.sql`을 적용하고 별도 Redis 인스턴스를 준비한다.

게임 검증 프로세스에 추가로 지정한 설정:

```powershell
$env:TCP_PORT = '9129'
$env:TRICKCAL_POSTGRES_CONN = 'Host=127.0.0.1;Port=15439;Database=apm_verification;Username=apmtest;Pooling=true;Maximum Pool Size=100'
$env:TRICKCAL_REDIS_CONN = '127.0.0.1:16389'
$env:ORLEANS_CLUSTER_ID = 'apm-verification-20261026'
$env:ORLEANS_SERVICE_ID = 'Trickcal-APM-Verification'
$env:ORLEANS_SILO_PORT = '11129'
$env:ORLEANS_GATEWAY_PORT = '30029'
$env:DASHBOARD_PORT = '8899'
$env:APM_COLLECTOR_ENDPOINT = 'http://127.0.0.1:5229'
$env:APM_RUN_ID = 'game-integration-20261026'
$env:APM_CODE_VERSION = 'apm-v2-working-tree-20261026'
$env:WATCH_PLAYER_NAME = ''
dotnet run --project src/GameServer
```

인증 방식은 자체 테스트 인스턴스의 설정에 맞춘다. 위 암호 없는 loopback 테스트 연결을 운영 설정에 사용하지 않는다.

## 검증 명령

아래 명령은 APM 저장소에서 실행한다. API/에이전트 검사는 합성 fixture를 수집 DB에 추가하므로 별도 검증 DB를 사용한다. 게임/부하 검사는 게임 DB에 시험 계정을 만들거나 상태를 변경한다. 반드시 위 격리 환경에서 실행한다.

```powershell
dotnet run --project tools/verification/AgentChecks -- http://127.0.0.1:5229
python tools/verification/check_api.py http://127.0.0.1:5229
python tools/verification/check_game.py 9129 http://127.0.0.1:5229
python tools/verification/check_load.py 9129 http://127.0.0.1:5229
dotnet run --project tools/verification/DbDelayProbe
dotnet run --project tools/verification/SourcePathChecks -- '.local-evidence/new-source-path-fixture'
```

`DbDelayProbe`는 15439의 `apm_verification`에만 연결하도록 고정되어 있으며 약 800ms 테이블 잠금을 주입한다. `check_load.py`는 16연결/800 Heartbeat 검사이고 3만 동접 검사가 아니다. 소스 경로 검사는 아직 존재하지 않는 fixture 디렉터리를 지정한다. DB 마이그레이션 검사는 `python tools/verification/check_migration.py .local-evidence/new-migration-check`처럼 새 출력 디렉터리를 지정하며 5239 포트가 비어 있어야 한다.

## 운영 중 확인

- `/api/health`가 `ok`인지 확인한다. LLM 연결 상태를 의미하지는 않는다.
- 자원 카드의 마지막 수신 시각, 전송 대기, 유실, 실패 시도를 확인한다. 누적 수치는 프로세스 인스턴스별이다.
- 수집기 재시작/연결 장애 중 유실이 발생할 수 있다. 디스크 영속 재전송 큐는 구현하지 않았다.
- `Telemetry__RetentionDays`는 새 추적 자료에만 적용되며 기본은 비활성이다. Metrics/Analyses 보존·정리는 별도 정책이 필요하다.
- 대시보드 API는 단일 로컬 사용자용이다. 공용 네트워크에 노출하지 않는다.

당일 검증용 프로세스의 PID/경로/포트는 비공개 증거 폴더의 `processes.json`에 기록한다. PID는 재사용될 수 있으므로 종료 전 해당 실행 경로와 포트를 대조한다. 정상 종료 순서는 게임 → 수집기 → 시험 PostgreSQL/Redis다. LM Studio는 앱에서 서버 중지/모델 언로드할 수 있다. 기존 사용자 서비스나 Docker를 전체 종료·초기화하지 않는다.

## 상세 결과

[비교·실측 결과·남은 범위](apm-v2-20261026.md), [기존 증거](validation-baseline.md), [다음 단계와 파인튜닝 계획](apm-upgrade-plan.md).

## v2.1 수집 정책·부하 검증 후 갱신

최신 결과는 [수집 신뢰성 개선·3만 동접 재검증](apm-reliability-20261026.md)에 있다. 모든 요청의 기본 기록은 보존 대상으로 보내고, 정상 내부 상세는 기본 1%만 선택한다. `APM_DETAIL_SAMPLE_RATE`는 0~1, `APM_SLOW_TRACE_MS`는 0 이상 숫자다. 기본 100ms 이상 또는 종료 시 오류인 요청은 상세 보존 대상이며 버퍼·큐·구간 상한은 적용된다.

`check_game.py`의 상세 연결 회귀 검사는 게임 서버를 `APM_DETAIL_SAMPLE_RATE=1`로 실행해야 한다. 부하 검사는 기본 0.01을 사용하며 비교 조건에 남긴다. `sampled_out`은 정책상 제외, `truncated`/`buffer_limit`는 상한, `queue_dropped`는 상세 전송 큐 유실, `pending`은 상세 도착 대기다. pending이 지속되면 유실/HTTP 실패 카운터도 확인한다. 상세 유실과 기본 요청 유실을 혼동하지 않는다.

에이전트 기본 상한: 요청 큐 32,768개, 상세 큐 32,768 span, 추적 버퍼 4,096개, trace당 내부 64개. 선택한 상세는 약 1초 기다린 후 묶는다. batch는 256개까지다. 저장기는 FULL 동기화, 32MiB cache, 16,384페이지 WAL checkpoint를 쓰며 디스크 공간과 긴 읽기 트랜잭션을 감시해야 한다. 해당 정책과 단기 실험 통과는 무제한·영속 무손실 보장을 뜻하지 않는다.
