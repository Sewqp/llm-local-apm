# LLM 기반 로컬 APM 시스템

## APM으로 보완한 운영 관측·검증

- **비동기 매칭 추적:** 매칭 접수·생성·결과 전송을 구분하고 원 요청과 연결했다. 20명 시범 재검증에서 결과 unknown 20건→0건, 결과 trace·원 요청 연결 각 20건을 확인했다.
- **3만 동접 수집 검증:** APM ON 6회에서 기본 요청 2,659,080건의 건수와 trace ID를 대조해 누락·중복 0건을 확인했다. 로그인/Heartbeat 부하이며 정상 내부 상세는 1% 선택 정책이다.
- **디스크 재전송 복구:** 실제 실행에서 3,072건을 보관한 뒤 모두 재전송했다. 저장된 묶음의 복구이며 무제한 장애 내성을 뜻하지 않는다.
- **처리 단계별 조사:** 패킷·핸들러·Grain·DB/Redis·응답 쓰기를 연결해 조사하고, 서버 처리시간과 클라이언트 왕복시간을 분리했다.

[APM의 실제 기여·구현 코드·검증 범위](docs/apm-operational-impact-20261110.md) · [실행별 공개 집계](docs/evidence/apm-operational-summary-20261110.json)

서버 처리 능력의 일률적인 향상률은 입증하지 않았다. 원인 미확정 런타임 정체와 최대 TPS·전체 콘텐츠 혼합 부하는 후속 검증 대상이다.

> **2026-11-10 APM 비용 실측:** [APM 비용·게임 코드 전후 반복 측정](docs/apm-ab-comparison-20261110.md). 게임 종료 후 네 조건을 각각 3회 측정했습니다. 실제 CPU·메모리·지연 비용과 수집 대조를 분리하고, 앞선 공유 부하 결과는 별도 보존했습니다.

> **2026-11-10 큐·수집 보완:** [WAL 저장 병목, 디스크 재전송, 3만 동접 재검증](docs/apm-queue-recovery-20261110.md). 메모리 큐 32,768개와 정상 상세 1% 정책을 유지하고 전송/저장 경로를 개선했습니다.

> **다른 구조 적용 검증:** [HTTP·분산 호출·큐 워커 실행 예제](examples/ArchitectureDemo/README.md)와 [실측 결과](docs/apm-architectures-20261026.md). HTTP 동시 호출 32개, 기본 기록 1,406건 대조 및 비동기 실패 연결을 확인했습니다. 아래 3만 게임 동접과 구분하는 합성 예제입니다.

> **과거 한계(2026-10-26):** 당시 최종 코드에서도 긴 런타임 정체가 발생한 별도 실행은 기본 기록 3,154건이 유실됐습니다. 성공 실행과 함께 보존했으며, 당시에는 영속 재전송이 없었습니다. 현재는 용량이 제한된 디스크 재전송을 추가했으며, 무제한 장애 내성을 의미하지 않습니다.

> **2026-10-26 수집 신뢰성 검증:** [3만 동접·60초 유지 및 기본 요청 100% 수집](docs/apm-reliability-20261026.md). 로그인 30,000건과 Heartbeat 414,969건을 전량 대조했습니다. 상세는 정상 1% 및 오류·느린 요청을 선택하며 상한 생략을 별도 기록합니다. [앞선 수집 실패](docs/concurrency-validation-20261026.md)도 보존합니다.

> **2026-10-26 v2 구현:** 요청별 waterfall, DB·Redis·Grain 추적, 가명 유저별 조회, 오류·자원·수집 상태, 근거를 보존하는 로컬 LLM 조사를 추가했습니다. [상용 APM 비교·실측 결과](docs/apm-v2-20261026.md), [실행 안내](docs/apm-v2-runbook.md), [기존 실검증·실패 기록](docs/validation-baseline.md), [후속 강화·파인튜닝 계획](docs/apm-upgrade-plan.md), [작업 상태](docs/continuity.md)를 확인하세요. 아래는 초기 HTTP 데모 설명이며 최신 게임 서버 계측·검증 범위는 v2 문서를 기준으로 합니다.

전통적인 APM(Application Performance Monitoring) 파이프라인에 **로컬에서 구동되는 LLM**을 결합해,
수집된 메트릭에서 임계값 기반으로 이상을 탐지하고 그 원인/개선 방향을 자연어로 설명하는 시스템입니다.

외부 API 기반 LLM에 트래픽/로그 데이터를 보내지 않고, **LM Studio로 로컬 서빙되는 LLM**을 호출하여
민감 데이터를 외부로 노출하지 않는 것이 핵심 설계 목표입니다.

## 현재 버전 실행

게임 서버 계측과 재전송 설정은 [현재 운영 안내](docs/apm-v2-runbook.md)를 따른다. 기존 증거 DB와 분리해서 시작하려면 저장소에서 다음을 실행한다.

```powershell
npm --prefix dashboard ci
./tools/start-local.ps1
```

기본 주소는 `http://127.0.0.1:5219`, DB는 `.local-evidence/live/apm-v2.db`다. LLM 없이도 수집·조회가 동작한다. `/api/health`는 API의 고정 응답이며 DB/LLM 준비 상태를 확인하지 않는다.

TCP 게임 서버는 `Apm.Agent`를 통해 기본 요청과 선택된 상세를 `/api/telemetry/batch`로 전송한다. 정상 내부 상세 기본 1%, 오류·느린 요청 우선 선택, 메모리·디스크 상한이 적용된다. 요청 추적의 근거 스냅샷 조사와 아래의 초기 HTTP 메트릭 데모를 구분한다.

## 초기 HTTP 데모 아키텍처

```
[대상 서버 (TargetServer, ASP.NET Core)]
      │  계측 미들웨어 (요청 시작/종료 시각, 상태코드, 경로)
      ▼
[수집 서버 (CollectionServer, ASP.NET Core Web API)]  ──▶  [SQLite]
      │
      ▼
[이상탐지 룰 (임계값 기반)] ──▶ [Graphify (로컬 코드 지식그래프)] ──▶ [LM Studio (로컬 LLM)] ──▶ [대시보드 (React)]
```

- **TargetServer**: 계측 미들웨어가 적용된 데모 대상 서버. 실제 프로젝트에서는 기존 보유 서버에
  `InstrumentationMiddleware` 한 줄만 추가하면 동일하게 적용 가능하도록 설계했습니다.
- **CollectionServer**: 메트릭 수집(`POST /api/metrics`), 조회, 임계값 기반 이상탐지, Graphify 조회,
  LM Studio 호출을 담당하는 API 서버.
- **dashboard**: 응답시간/에러율 시계열 그래프와 LLM 분석·의심 코드 결과를 보여주는 React 대시보드.

## 기술 스택

| 구성 요소 | 기술 |
|---|---|
| 대상 서버 | ASP.NET Core (계측 미들웨어) |
| 수집 서버 | ASP.NET Core Web API + EF Core + SQLite |
| 이상탐지 | 임계값 기반 룰 (기준선 평균 대비 N배 초과 / HTTP 5xx) |
| 코드 분석 | [Graphify](https://github.com/Graphify-Labs/graphify) CLI — 로컬 코드 그래프를 LLM의 explain/query 도구 호출로 탐색 |
| LLM 추론 | LM Studio 로컬 서버 (OpenAI 호환 `/v1/chat/completions`), Gemma |
| 대시보드 | React (Vite) + Recharts |

## 사전 준비

1. [.NET 10 SDK](https://dotnet.microsoft.com/) 이상
2. Node.js 20.19 이상인 20.x 또는 22.12 이상과 npm(`dashboard/package-lock.json`의 Vite 요구사항)
3. LLM 분석을 사용할 때 [LM Studio](https://lmstudio.ai/) 설치 후:
   - 모델 다운로드 (예: Gemma 계열)
   - LM Studio 앱에서 **Local Server** 실행 (기본 포트 `1234`)
   - 또는 CLI: `lms server start` 후 `lms load <model>`
4. [Graphify](https://github.com/Graphify-Labs/graphify) CLI 설치 (원인 코드 위치 지목 기능에 필요, 없어도 나머지 기능은 그대로 동작):
   ```powershell
   winget install astral-sh.uv   # uv가 없다면
   uv tool install graphifyy
   ```
   설치 후 `graphify` 명령이 PATH에 안 잡히면(`uv tool update-shell` 후에도) `appsettings.Local.json`의
   `Graphify:CliPath`에 전체 경로(`C:\Users\<사용자>\.local\bin\graphify.exe`)를 넣으면 된다.

## 실행 방법

### Windows 단일 exe 빌드(기존 배포 방식)

dashboard(React)는 빌드되면 CollectionServer의 정적 파일(`wwwroot`)로 서빙되도록 구성되어 있어,
**API + 대시보드가 하나의 실행 파일·하나의 포트**로 뜬다. 별도 Vite dev server를 띄울 필요가 없다.

```powershell
.\build.ps1
.\publish\CollectionServer.exe --urls http://localhost:5219
```

`build.ps1`은 `dashboard`를 빌드해 `src/CollectionServer/wwwroot`로 복사한 뒤, `dotnet publish`로
`win-x64` 자체 포함(self-contained) 단일 파일 exe를 `publish/` 아래에 생성한다. exe는 실행 위치와
무관하게 자기 옆에 SQLite DB(`apm.db`)를 만든다. `appsettings.json`의 `LmStudio.Model` 값은 실제
로드한 모델 식별자로 맞춰야 한다(`lms ps` 또는 LM Studio Local Server 화면에서 확인).

브라우저에서 `http://localhost:5219` 접속. 5초마다 최근 15분 데이터를 자동 갱신하며,
**"이상탐지 + LLM 분석 실행"** 버튼으로 로컬 LLM 분석을 요청할 수 있다.

### 개발 모드 — 코드를 수정하며 띄우기

CollectionServer와 대시보드를 각각 핫리로드로 띄우고 싶을 때만 이 방식을 쓴다.

```bash
# 1) 수집 서버
cd src/CollectionServer
dotnet run --urls http://localhost:5219

# 2) 대시보드 (별도 터미널) — 개발 모드에선 자동으로 localhost:5219를 바라봄
cd dashboard
npm install
npm run dev   # http://localhost:5173
```

### 대상 서버 (TargetServer, 데모용)

```bash
cd src/TargetServer
dotnet run --urls http://localhost:5082
```

- 데모 편의를 위해 자체적으로 트래픽을 생성합니다(`TrafficGeneratorHostedService`).
- 5% 확률로 응답 지연 스파이크, 8% 확률로 500 에러를 발생시켜 이상탐지를 시연할 수 있습니다.

## 대시보드에서 서버 선택 / 시작·중지

여러 대상 서버가 동시에 메트릭을 보낼 수 있으므로, 대시보드 우측 상단 드롭다운에서 특정 서버만 골라
차트·이상탐지·LLM 분석을 그 서버로 한정할 수 있다(`GET /api/metrics/server-names`,
모든 조회 API의 `?server=` 쿼리 파라미터).

"서버 관리" 패널에서는 서버를 시작/중지할 수 있고, **"+ 서버 등록"**으로 새 서버를 UI에서 직접
등록할 수도 있다(이름/작업 디렉터리/명령/인자 입력). `appsettings.json`의 `ManagedServers`에
미리 등록된 항목(기본 제공, 삭제 불가)과 UI로 등록한 항목(`managed-servers.json`에 저장되어 재시작
후에도 유지, 삭제 가능)이 함께 표시된다.

`appsettings.json`에는 예시 값만 들어있다 — 실제 개인 환경의 서버 경로는
`src/CollectionServer/appsettings.Local.json`(git에는 올라가지 않음, `.gitignore` 처리됨)에
같은 형식으로 넣으면 `Program.cs`가 이 파일을 추가로 읽어 덮어쓴다:

```json
{
  "ManagedServers": [
    { "Name": "내 서버", "WorkingDirectory": "C:\\path\\to\\your\\server",
      "Command": "dotnet", "Arguments": "run --urls http://localhost:5082" }
  ]
}
```

> **주의**: UI 등록 폼은 임의 명령을 그대로 실행하므로, 이 화면에 접근 가능한 사람은 누구나 임의
> 명령 실행이 가능하다. 로컬 전용 개인 도구를 전제로 한 트레이드오프 — 외부에 노출하지 말 것.

실행 로그는 `server-logs/{서버이름}.log`에 남고, CollectionServer가 정상 종료되면 자신이 띄운
서버도 함께 정리된다(orphan 프로세스가 포트를 물고 남는 것 방지).

```json
"ManagedServers": [
  { "Name": "TargetServer (데모)", "WorkingDirectory": "C:\\...\\src\\TargetServer",
    "Command": "dotnet", "Arguments": "run --urls http://localhost:5082" }
]
```

## 초기 게임 서버 연동 사례와 해석 범위

데모용 `TargetServer`(HTTP) 외에, 실제 보유 중인 TCP 패킷 기반 게임 서버
([`Trickcal-Bottleneck-Server`](https://github.com/Sewqp/Trickcal-Bottleneck-Server), C#/.NET 10, Orleans + PostgreSQL + Redis)에
계측을 붙여 검증했습니다. HTTP 미들웨어를 쓸 수 없는 구조라, `PacketDispatcher.DispatchAsync`에서
핸들러 호출을 감싸는 방식으로 대체 계측(`ApmReporter`, `AsyncLogger`와 동일한 Channel 기반 백그라운드
전송 패턴)을 추가하고, `Method="PACKET"`, `Path="/packet/{PacketId}"`로 맞춰 기존 이상탐지 룰을
그대로 재사용했습니다.

초기 화면에는 `connectFailed=0, loginOk=30000`이 남아 있고, 보존한 initial DB의 Login 평균은 78.2693ms, p95는 244.6999ms다. 당시 화면의 실행과 DB 전체를 묶는 run ID는 없으므로 이를 한 실행의 정확한 전체 수집 결과로 합치지 않는다. 이후 run ID와 trace ID를 사용한 대조 결과는 문서 상단의 11월 기록을 따른다.

LLM은 `HeartbeatManager`를 관련 코드 후보로 제시했고, 사람이 전체 세션 순회·순차 Grain 호출 구조를 확인했다. 이 구조가 특정 지연을 일으켰다는 인과관계나 PostgreSQL 커넥션 풀 경합을 확정한 것은 아니다. `Task.WhenAll` 시도도 개선이 입증되지 않아 원복했다. [원본 증거와 한계](docs/validation-baseline.md)를 함께 보존한다.

기준선이 0에 가까운 Heartbeat에서는 배수만으로 이상 여부를 판단하면 왜곡될 수 있어 최소 절대 증가량 조건을 추가했다. 이것은 오탐률 개선을 정량 입증했다는 뜻은 아니다.

## 초기 메트릭·분석 API

게임 서버의 배치 수집(`/api/telemetry/batch`)과 요청별 추적·조사 API는 [운영 안내](docs/apm-v2-runbook.md) 및 [v2 기능 기록](docs/apm-v2-20261026.md)을 함께 본다.

| Method | Path | 설명 |
|---|---|---|
| POST | `/api/metrics` | 계측 미들웨어가 요청 1건마다 전송 |
| GET | `/api/metrics?minutes=` | 대시보드 시계열용 원본 메트릭 조회 |
| GET | `/api/metrics/summary?minutes=` | 경로별 평균/응답시간(p95)/에러율 요약 |
| GET | `/api/anomalies?minutes=` | 이상탐지 결과만 조회 (LLM 호출 없음) |
| POST | `/api/analysis/run?minutes=` | 이상탐지 + Graphify 코드 스코핑 + LM Studio 원인분석·의심 코드 지목 실행 및 저장 |
| GET | `/api/analysis?limit=` | 저장된 분석 이력 조회 |

## 이상탐지 룰

1. HTTP 5xx 응답은 항상 이상으로 표기
2. 응답시간이 해당 경로(Path)의 기준선 평균 대비 `ThresholdMultiplier`(기본 3배)를 초과하고 최소 절대 증가량 `MinAbsoluteDeltaMs`(기본 5ms)도 충족하면 이상으로 표기
   - 기준선은 조회 구간 이전 과거 데이터 평균 우선 사용, 부족하면 조회 구간 내 평균으로 대체
   - `/api/products/{id}`처럼 ID가 섞인 경로는 미들웨어에서 정규화하여 동일 경로로 집계

LLM이 생성하는 분석/추천 텍스트는 대시보드에 **"참고용 추천"**으로 표기되어, 과신하지 않도록 안내합니다.

## 코드 후보 탐색과 요청 조사

현재 `/api/analysis/run`은 LM Studio에 `graphify_explain`·`graphify_query` 도구를 제공하여 모델이 탐색할 이름을 선택하도록 한다. 예전의 패킷 이름 기반 고정 스코핑 설명은 현재 구현과 다르다. `quick`이 기본이며 `full`은 넓은 탐색을 유도한다. 도구 호출이나 올바른 원인 발견을 보장하는 모드는 아니다.

Graphify는 `Graphify:ServerSourceDirs`에 등록한 로컬 소스를 `extract . --code-only --no-viz`로 파싱한다. 기존 `graphify-out/graph.json`은 재사용하므로 소스를 수정한 뒤에는 그래프도 갱신해야 한다. 코드 버전과 캐시의 자동 일치 검사는 아직 없다. CLI가 없거나 경로가 잘못되면 코드 근거가 제한되며, 수집·조회 자체는 계속 사용할 수 있다.

v2의 **이 요청 분석**은 해당 trace의 span·log·code 스냅샷을 근거로 별도 조사 기록을 남긴다. Graphify 기반 이상탐지 분석과 구분한다. 답변은 검증할 후보이며 의도적 오류 시연, 자연 발생 관측, 원인 미확정 결과를 섞지 않는다. [기존 성공·실패 사례](docs/validation-baseline.md), [다른 구조의 조사와 오인용 기록](docs/apm-architectures-20261026.md)을 확인한다.

## 남은 범위

- RAG(벡터DB 기반 지식검색)
- LLM 파인튜닝/LoRA
- C++/Node 등 멀티 언어 에이전트
- 상용 운영용 다중 사용자 인증, DB/LLM readiness, 외부 경보
- 고정 사례 LLM 정확도 평가와 그래프/코드 버전 관리

부하·복구 재현 스크립트는 이미 있다. 실행 명령과 격리 조건은 [운영 안내](docs/apm-v2-runbook.md)를 따른다. 원본 DB를 초기화하거나 단기 통과 결과를 무제한 무손실·운영 SLA로 확대하지 않는다.
