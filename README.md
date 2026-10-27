# LLM 기반 로컬 APM 시스템

> **다른 구조 적용 검증:** [HTTP·분산 호출·큐 워커 실행 예제](examples/ArchitectureDemo/README.md)와 [실측 결과](docs/apm-architectures-20261026.md). HTTP 동시 호출 32개, 기본 기록 1,406건 대조 및 비동기 실패 연결을 확인했습니다. 아래 3만 게임 동접과 구분하는 합성 예제입니다.

> **남은 한계:** 같은 최종 코드에서도 긴 런타임 정체가 발생한 별도 실행은 기본 기록 3,154건이 유실됐습니다. 성공 실행과 함께 보존했으며, 영속 재전송과 장애 내성은 아직 남은 과제입니다.

> **최신 수집 신뢰성 검증:** [3만 동접·60초 유지 및 기본 요청 100% 수집](docs/apm-reliability-20261026.md). 로그인 30,000건과 Heartbeat 414,969건을 전량 대조했습니다. 상세는 정상 1% 및 오류·느린 요청을 선택하며 상한 생략을 별도 기록합니다. [앞선 수집 실패](docs/concurrency-validation-20261026.md)도 보존합니다.

> **2026-10-26 v2 구현:** 요청별 waterfall, DB·Redis·Grain 추적, 가명 유저별 조회, 오류·자원·수집 상태, 근거를 보존하는 로컬 LLM 조사를 추가했습니다. [상용 APM 비교·실측 결과](docs/apm-v2-20261026.md), [실행 안내](docs/apm-v2-runbook.md), [기존 실검증·실패 기록](docs/validation-baseline.md), [후속 강화·파인튜닝 계획](docs/apm-upgrade-plan.md), [작업 상태](docs/continuity.md)를 확인하세요. 아래는 초기 HTTP 데모 설명이며 최신 게임 서버 계측·검증 범위는 v2 문서를 기준으로 합니다.

전통적인 APM(Application Performance Monitoring) 파이프라인에 **로컬에서 구동되는 LLM**을 결합해,
수집된 메트릭에서 임계값 기반으로 이상을 탐지하고 그 원인/개선 방향을 자연어로 설명하는 시스템입니다.

외부 API 기반 LLM에 트래픽/로그 데이터를 보내지 않고, **LM Studio로 로컬 서빙되는 LLM**을 호출하여
민감 데이터를 외부로 노출하지 않는 것이 핵심 설계 목표입니다.

## 아키텍처

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
| 코드 분석 | [Graphify](https://github.com/Graphify-Labs/graphify) CLI — tree-sitter AST로 로컬에서 만든 코드 지식그래프를 이상탐지 엔드포인트 기준으로 스코핑해서 LLM에 전달 |
| LLM 추론 | LM Studio 로컬 서버 (OpenAI 호환 `/v1/chat/completions`), Gemma |
| 대시보드 | React (Vite) + Recharts |

## 사전 준비

1. [.NET 10 SDK](https://dotnet.microsoft.com/) 이상
2. Node.js 18+
3. [LM Studio](https://lmstudio.ai/) 설치 후:
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

### 빠른 실행 — 단일 exe (권장)

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

## 실제 서버 연동 사례 — Trickcal-Bottleneck-Server

데모용 `TargetServer`(HTTP) 외에, 실제 보유 중인 TCP 패킷 기반 게임 서버
([`Trickcal-Bottleneck-Server`](../Trickcal-Bottleneck-Server), C#/.NET 10, Orleans + PostgreSQL + Redis)에
계측을 붙여 검증했습니다. HTTP 미들웨어를 쓸 수 없는 구조라, `PacketDispatcher.DispatchAsync`에서
핸들러 호출을 감싸는 방식으로 대체 계측(`ApmReporter`, `AsyncLogger`와 동일한 Channel 기반 백그라운드
전송 패턴)을 추가하고, `Method="PACKET"`, `Path="/packet/{PacketId}"`로 맞춰 기존 이상탐지 룰을
그대로 재사용했습니다.

**검증 결과**: 가벼운 트래픽(30~50명, 평균 응답 14~15ms)으로 기준선을 만든 뒤 3,000명 및
README에 문서화된 규모인 3만 명(rampUp 3000) 동시 로그인을 발생시켜 재현. 3만 명 테스트에서
`connectFailed=0, loginOk=30000` 전원 성공했지만, 로그인 응답시간은 평균 78ms·p95 243ms까지
치솟아 기준선 대비 3배 이상 벌어진 구간이 다수 잡혔고, 이상탐지 룰이 이를 정확히 표기했습니다.
LM Studio(Gemma)가 생성한 분석도 "로그인 요청 집중으로 인한 서버 자원 점유율 증가",
"DB 쿼리 실행 계획 점검·인덱스 최적화·인증 캐싱 검토" 등 실제 원인(PostgreSQL 커넥션 풀 경합)과
부합하는 방향을 제시했습니다. 응답시간이 원래 1ms 미만인 하트비트처럼 기준선이 0에 가까운
경로는 배수만으로 이상 여부를 판단하면 왜곡되기 쉬워, 절대 지연 증가량(`MinAbsoluteDeltaMs`)
조건을 추가하고 사유 문구에도 배수·절대값을 함께 표기하도록 개선했습니다.

## API

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
2. 응답시간이 해당 경로(Path)의 기준선 평균 대비 `ThresholdMultiplier`(기본 3배)를 초과하면 이상으로 표기
   - 기준선은 조회 구간 이전 과거 데이터 평균 우선 사용, 부족하면 조회 구간 내 평균으로 대체
   - `/api/products/{id}`처럼 ID가 섞인 경로는 미들웨어에서 정규화하여 동일 경로로 집계

LLM이 생성하는 분석/추천 텍스트는 대시보드에 **"참고용 추천"**으로 표기되어, 과신하지 않도록 안내합니다.

## 원인 코드 위치 지목 (Graphify)

이상탐지까지는 "언제·어디서·얼마나" 벗어났는지만 알려준다. 그 다음 질문("정확히 어느 코드가 원인일
가능성이 높은가")에 답하기 위해, `/api/analysis/run`은 LLM을 부르기 전에
[Graphify](https://github.com/Graphify-Labs/graphify) CLI로 원시 소스 전체 대신 압축된 코드
지식그래프 조각 하나만 잘라와 프롬프트에 얹는다.

1. **그래프 빌드(최초 1회, 서버당)**: `graphify extract <소스 디렉터리> --code-only --no-viz` —
   tree-sitter AST 로컬 파싱만 쓰고 LLM 호출이 없어 API 키 없이도 몇 초 안에 끝난다.
   결과(`graphify-out/graph.json`)는 디스크에 캐시되고, `appsettings.json`의
   `Graphify:ServerSourceDirs`(메트릭의 `ServerName` -> 소스 디렉터리 매핑)에 등록된 서버에만 동작한다.
2. **스코핑**: 이상탐지된 엔드포인트 경로(예: `/packet/MatchRequest`, `/api/products/{id}`)에서 마지막
   세그먼트를 뽑아 `MatchHandler`/`ProductsController` 같은 처리 클래스 이름 후보를 만들고,
   `graphify explain "<후보>"`로 그 클래스와 바로 연결된 노드(소스 위치, 상속·호출 관계)만 가져온다.
   못 찾으면 `graphify query "<원본 세그먼트>"`로 한 번 더 넓게 훑는다.
3. **LLM에 위임**: 이 스코핑된 그래프 텍스트를 프롬프트에 포함시켜, LLM에게 "이 그래프 안에서
   원인일 가능성이 가장 높은 파일/함수 하나를 정확히 골라 마지막 줄에 `의심 코드: <파일:라인>` 형식으로
   답하라"고 지시한다. 응답에서 그 줄을 정규식으로 분리해 `SuspectedCode`로 따로 저장 — 대시보드에서
   분석 텍스트와 별개의 배지로 표시된다.

**"완전 간소화"는 Graphify(결정론적 로컬 그래프 스코핑)가 담당하고, "특정 코드를 고르는 판단"은 그
축약된 컨텍스트를 받은 LLM이 한다** — LLM이 직접 여러 차례 그래프 명령을 골라 호출하는 완전 에이전틱
방식은 쓰지 않았다(로컬 소형 모델 신뢰성·안전성 트레이드오프).

Graphify가 설치되어 있지 않거나 해당 서버가 `ServerSourceDirs`에 없으면 이 단계는 조용히 건너뛰고
기존처럼 지표만으로 분석한다 — 필수 의존성이 아니다.

## Out of Scope (향후 확장)

- RAG(벡터DB 기반 지식검색)
- LLM 파인튜닝/LoRA
- C++/Node 등 멀티 언어 에이전트
- 부하 시뮬레이션 자동화
