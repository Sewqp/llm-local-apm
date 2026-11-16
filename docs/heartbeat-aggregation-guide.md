# Heartbeat 집계와 지정 유저 상세 추적

2026-11-17 구현. 기존 검증 DB·실패 기록은 변경하지 않는다. 새 측정 결과는 `heartbeat-aggregation-validation-20261117.md`를 참조한다.

## 수집 규칙

게임 서버의 정확한 `packet / Heartbeat` 이름에만 집계를 적용한다. HTTP 경로나 다른 패킷 이름에는 적용하지 않는다.

| 조건 | 저장 방식 |
|---|---|
| 성공, 지연 기준 미만, 내부 오류 없음, 지정 유저 아님 | 5초 구간별 건수·소요시간 합·최소·최대·히스토그램 |
| 오류·시간 초과, 또는 내부 호출 오류 | 요청 metric + entry span + 선택 상세·오류 로그 |
| 지연 기준 이상 | 요청 metric + entry span + 선택 상세 |
| 사전에 지정한 유저 | Heartbeat를 포함한 요청의 상세 우선 수집 |
| 거절·취소·결과 미확인 Heartbeat | 요청 기록 및 상세 보존 |
| 다른 정상 요청 | 기존 기본 기록 및 정상 상세 1% 정책 유지 |

오류·지연·지정 유저 요청은 집계에 다시 더하지 않는다. 따라서 전체 요청 수는 **개별 요청 metric 건수 + 집계 Count 합계**다. 집계는 표본의 배수가 아닌 실제 관측 건수이며, 유저·세션·trace ID를 포함하지 않는다. 늦게 완료된 요청은 같은 5초 구간에 여러 집계 조각으로 도착할 수 있으며, 각 조각은 고유 ID를 가진다. 조회 시에는 Count와 히스토그램을 합산한다.

Heartbeat 종료 후 기본 1초 동안 내부 호출의 늦은 도착을 기다린다. 이 기간에 발견한 오류는 상세 추적으로 남긴다. 이후 도착하는 구간까지 무기한 보장하지 않으며 기존 `UncorrelatedSpansSkipped`로 미연결 구간을 집계한다.

## 설정

수집 서버와 Agent를 함께 갱신한 다음 게임 서버를 시작한다. 게임 서버에서는 집계가 기본 활성화이며, 범용 Agent 라이브러리 자체는 opt-in이다.

```powershell
$env:APM_AGGREGATE_HEARTBEATS = '1'
$env:APM_TRACE_USER_IDS = '1001,1002'
$env:APM_SLOW_TRACE_MS = '100'
$env:APM_DETAIL_SAMPLE_RATE = '0.01'
```

위 ID는 예시다. `APM_TRACE_USER_IDS`는 실제 내부 player ID를 쉼표로 구분하며 최대 128개의 양수 ID를 허용한다. 서버 시작 시 읽고 HMAC 가명 키로 변환한다. 원래 ID를 수집 서버에 보내지 않는다. 재시작 간 같은 가명 키가 필요하면 기존 `APM_USER_HASH_KEY`를 안전하게 유지한다.

대시보드의 유저 필터는 이미 저장된 기록을 조회하는 기능이다. 필터에 입력한다고 수집 대상이 동적으로 변경되지는 않는다. 수집 대상을 바꾸려면 설정을 변경하고 서버를 재시작한다. 집계가 끝난 정상 Heartbeat를 나중에 특정 유저의 개별 이력으로 복원할 수는 없다.

`APM_AGGREGATE_HEARTBEATS=0`으로 다음 실행부터 기존 개별 기본 기록 방식으로 돌아갈 수 있다. 기존 집계 행과 상세 trace는 삭제하지 않는다. 이 설정은 구형 바이너리 자체로 되돌리는 것과는 다르다.

## 전송·저장·조회

- 집계는 `HeartbeatAggregates` 별도 테이블에 추가한다. 과거 `Metrics`, `Analyses`, `Spans`의 내용을 재작성하거나 합치지 않는다.
- 집계가 포함된 배치는 `/api/telemetry/batch-with-heartbeats`를 사용한다. 구형 수집기의 미지원 필드 무시로 성공 처리되는 것을 방지한다. 구형 수집기는 404를 반환하므로 먼저 수집기를 갱신해야 한다.
- 기존 전송 큐·용량 제한 디스크 스풀을 사용한다. `BatchId` receipt와 집계 `Id`로 재전송을 중복 저장하지 않는다. receipt와 집계 행은 같은 SQLite 트랜잭션에서 저장한다.
- `RequestsObserved`, `RequestsSent`, `RequestsDropped`, 스풀 요청 건수는 집계의 **Count**를 반영한다. `RequestsAggregated`는 집계 대상으로 관측한 누적 요청 수다. 집계가 드롭되면 이 값에서 빠지는 대신 `RequestsDropped`에도 나타난다.
- `/api/metrics/summary`, `/api/metrics/timeseries`, 서버 목록, 이상 탐지의 평균 기준선은 집계를 포함한다. 원시 metric 조회와 trace 목록에는 개별 기록만 반환한다.
- 건수·평균·오류율 분모는 두 저장 경로를 합친 값이다. 집계가 섞인 경로의 p50/p95/p99는 히스토그램 구간 상한 추정값이며 `PercentileMethod=histogram_upper_bound`와 UI 안내로 구분한다. 개별 기록만 있는 경로의 백분위는 기존 최근접 순위 방식이다.
- 5초 구간 시작 시각으로 조회하므로 시간 필터 경계에서는 최대 한 구간의 요청이 함께 포함·제외될 수 있다. 집계는 정상 요청의 개별 시작 시각을 제공하지 않는다.

## 비용과 한계

집계가 메모리 사용을 무제한으로 늘리지 않도록 시간 구간은 최대 4개로 제한한다. 게임 서버의 trace 버퍼 상한은 4,096→16,384개로 조정해 3만 명의 5초 Heartbeat에서 약 1초간 상세를 대기할 공간을 둔다. 개별 trace의 내부 구간 상한 64개와 전송·스풀 상한은 유지한다. 버퍼 한도를 넘으면 `buffer_limit` 기본 기록으로 남기고, 상세 보존 제한을 명시한다. 오류·지연·선택 유저에도 유한한 버퍼와 장애 한계가 적용된다.

집계 전의 메모리 상태는 요청마다 디스크에 동기 저장하지 않는다. 정상 종료는 남은 집계를 flush하지만, 강제 종료·디스크 보관 이전 장애에서는 아직 내보내지 않은 구간이 유실될 수 있다. 이번 부하 검증은 전송을 비운 뒤 종료한 조건이며 강제 종료 시 집계 무손실을 입증하지 않는다. 기존 스풀의 용량 초과·장기 장애 한계도 유지된다.

클라이언트 RTT는 별도 부하 도구의 실제 송수신 시각으로 측정한다. 이 RTT p99와 서버 집계 히스토그램 p99를 혼동하지 않는다. Heartbeat 집계에 따른 비용 감소를 전체 게임의 최대 TPS 향상으로 해석하지 않는다.

## 로컬 재현

1. `dotnet run --project tools/verification/HeartbeatChecks/HeartbeatChecks.csproj -c Release -- <새 증거 디렉터리>`로 상세 보존·집계·재전송을 검사한다. 기존 증거 DB가 있으면 실행을 거절한다.
2. 구형·신형 서버/수집기 publish 디렉터리를 분리한다. 같은 `ConcurrentLoad`와 `RuntimeProbe` 바이너리를 사용한다.
3. Windows용 `tools/verification/run_heartbeat_case.py`에 `--work`, `--server-root`, `--apm-root`, `--case`, `--variant before|after`, `--game-dir`, `--collector-dir`, `--hold 120`을 전달한다. `--apm-root`에는 빌드된 RuntimeProbe가 있어야 하고, `--work/client`에는 ConcurrentLoad publish 결과가 있어야 한다.
4. 전·후·후·전·전·후 순서로 각각 새 DB·새 Redis·새 SQLite를 만든다. 이전 TCP TIME_WAIT가 기준 아래로 내려오기 전에는 다음 부하를 시작하지 않는다. 테스트 전용 포트 점유 시 실행을 거절한다.
5. 부하 실행이 끝난 다음에만 원시 로그·SQLite를 분석한다. 측정 중에는 큰 SQL 집계나 빌드를 실행하지 않는다.

이번 작업은 로컬 검증만 수행했다. 원격 CI 실행·업로드·배포 완료를 의미하지 않는다. 서버 CI의 APM 참조는 이 옵션을 지원하는 Agent 커밋으로 함께 갱신해야 한다. 이 로컬 검증 기록만으로 원격 CI의 성공을 주장하지 않는다.
