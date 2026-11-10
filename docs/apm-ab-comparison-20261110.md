# APM 비용과 게임 코드 전후 반복 측정 — 2026-11-10

## 결론과 해석

APM을 켜서 게임 서버가 일정 비율 빨라졌다고 주장할 근거는 얻지 못했다. 아래는 동일한 부하 설정으로 얻은 **사용자가 게임을 종료한 Windows PC에서의 관측값**이다. 현재 APM의 활성화 비용과 게임 코드 변경의 차이를 분리했다. 재측정 안에서 긴 지연과 재시도가 있었던 실행도 모두 포함했다. 게임 실행 중 얻은 앞선 8회와 재측정 전환 중 중단한 9회는 별도 증거로 보존하며 이 표에 섞지 않았다.

현재 코드에서 APM OFF→ON의 게임 CPU 중앙값은 1.71%→3.98% (**+2.27pp**)였다. 게임 working set 샘플평균의 중앙값은 1405.9→2430.9 MiB다. 수집기에는 별도로 CPU 2.66%, working set 462.2 MiB가 필요했다.

현재 코드의 유지 구간 실제 Heartbeat 응답률은 5133.2→5640.6/초 (+9.88%), p99는 115.893→144.085 ms였다. 이것은 **최대 TPS 측정이 아니다**. 클라이언트가 응답 후 5초를 기다리는 closed-loop 부하이므로 응답 지연뿐 아니라 같은 PC에서 실행되는 부하 생성기의 타이머·스케줄링 지연도 요청률에 영향을 준다. 따라서 응답률 차이를 서버 자체의 처리 능력 향상률로 해석하지 않는다.

APM의 확인된 성과는 관측·재전송·수집 대조다. 성능 향상률과 수집 신뢰성은 같은 지표가 아니다. 이번 게임 변경은 세션 소유권 검사·트랜잭션·복구 기능을 포함하며 순수 속도 최적화만의 전후 비교가 아니다.

게임 종료 후에도 12회 중 11회에 런타임 watchdog 정체(약 3.48–12.65초)가 기록됐다. 대부분의 해당 구간 GC 중단 시간은 0이었고 원인은 미확정이다. 따라서 아래 수치는 이 PC에서의 반복 관측값이며, 교란이 제거된 서버 순수 처리 능력이나 확정된 성능 향상률이 아니다. 클라이언트 RTT와 프로세스/타이머 정체는 서로 다른 지표다.

## APM으로 측정한 서버 패킷 처리시간 — 추가 확인

앞 표의 게임 CPU/메모리는 `Trickcal-Bottleneck-Server`의 GameServer 서버 프로세스 자원이다. 실행 중인 클라이언트 게임의 자원 수치가 아니다. 앞 표의 Heartbeat RTT는 부하 클라이언트의 왕복시간이고, 아래는 저장된 APM 진입 span의 `DurationMs`를 직접 집계한 서버 처리시간이다. 새 부하 실행은 하지 않았다.

이전/현재 모두 동일한 APM을 켠 각 3회의 중앙값이다. 정상 상세 1% 샘플이 아닌 전량 저장한 요청 진입 span을 사용했다.

| 서버 코드 | 요청 | 평균 ms | p50 ms | p95 ms | p99 ms | 실행별 p99 범위 ms |
|---|---|---:|---:|---:|---:|---:|
| 이전 | Heartbeat | 0.0352 | 0.0205 | 0.0502 | 0.0993 | 0.0962–0.1044 |
| 이전 | 로그인 | 1.9397 | 1.6825 | 2.5080 | 5.1450 | 4.1214–7.6606 |
| 현재 | Heartbeat | 0.0755 | 0.0481 | 0.0992 | 0.1770 | 0.1727–0.2090 |
| 현재 | 로그인 | 3.0685 | 2.3047 | 4.0144 | 17.3829 | 11.4705–241.2945 |

측정 경계는 `PacketDispatcher.DispatchAsync`의 요청 Activity 시작부터 종료까지다. 핸들러, 해당 요청의 Grain/DB/Redis 작업 및 응답 쓰기 구간을 포함하지만, 디스패치 전 소켓/스케줄링 대기와 클라이언트 수신까지의 전체 지연은 포함하지 않는다. 따라서 서버 p99 0.177ms와 클라이언트 RTT p99 144.085ms는 서로 모순되는 값이 아니며, 차이를 전부 클라이언트 탓으로 해석하지 않는다.

Heartbeat는 클라이언트 hold 시작/끝 UTC 사이에 서버에서 시작한 요청을 집계했다. 서버 진입 시각과 클라이언트 전송 시각이 달라 경계의 요청 집합은 정확히 같지 않다. 로그인은 각 본 부하의 30,000건을 사용하고 워밍업 1,000건을 제외했다. 각 실행의 nearest-rank 분위수를 먼저 계산한 뒤 3회 중앙값과 범위를 냈다.

APM 수집·trace·서버 처리시간 측정은 완료했다. CPU/메모리 ON/OFF 비교에는 외부 Win32 관측을 병행했다. 측정 범위는 로그인/Heartbeat이며 최대 서버 TPS, 전체 콘텐츠 혼합 부하, 원인 미확정 런타임 정체의 분석 완료를 뜻하지 않는다. 서버 처리시간 수치만으로 세션 보호 등 기능이 추가된 전후 코드의 성능 향상을 주장하지 않는다.

원시 집계와 SQL은 증거 폴더의 `apm-server-timings-20261110.json`, `analyze_server_timings.py`에 보존했다.

## 네 조건의 측정값

각 조건 3회. 아래 값은 실행별 측정값의 중앙값이다. CPU 100%는 16개 논리 CPU 전체이며 1개 코어가 계속 사용되면 6.25%다. 메모리는 유지 구간 working set 샘플평균이다. CPU초/1,000 HB는 유지 구간 게임 프로세스 전체 CPU시간(정기 작업 포함)을 완료 응답 수로 나눈 값이다.

| 조건 | 응답/초 | HB p50 ms | HB p95 ms | HB p99 ms | 게임 CPU % | CPU초/1,000 HB | 게임 working set MiB |
|---|---:|---:|---:|---:|---:|---:|---:|
| 이전 코드 · APM OFF | 5290.1 | 0.403 | 25.129 | 81.025 | 1.37 | 0.0374 | 1129.1 |
| 이전 코드 · APM ON | 5557.6 | 2.334 | 166.724 | 204.241 | 2.60 | 0.0796 | 2314.4 |
| 현재 코드 · APM OFF | 5133.2 | 0.471 | 83.758 | 115.893 | 1.71 | 0.0522 | 1405.9 |
| 현재 코드 · APM ON | 5640.6 | 3.770 | 112.803 | 144.085 | 3.98 | 0.1149 | 2430.9 |

| 조건 | p99 최소–최대 ms | CPU 최소–최대 % | private bytes 중앙값 MiB | 로그인 p99 중앙값 ms |
|---|---:|---:|---:|---:|
| 이전 코드 · APM OFF | 2.238–96.917 | 1.23–1.53 | 1055.8 | 104.221 |
| 이전 코드 · APM ON | 141.699–217.667 | 2.55–3.07 | 2266.9 | 63.682 |
| 현재 코드 · APM OFF | 105.614–1572.422 | 1.56–1.75 | 1340.1 | 98.749 |
| 현재 코드 · APM ON | 144.059–264.755 | 3.76–4.28 | 2391.6 | 107.646 |

| APM ON 조건 | 수집기 CPU % | 수집기 working set MiB | 게임+수집기 CPU % |
|---|---:|---:|---:|
| 이전 코드 · APM ON | 2.58 | 455.9 | 5.18 |
| 현재 코드 · APM ON | 2.66 | 462.2 | 6.64 |

## 게임 변경 전후

APM 버전은 양쪽 모두 동일한 최신 소스를 사용했다. 아래는 이전 코드→현재 코드의 중앙값 차이다. 각각의 변경 항목을 하나씩 제거하는 실험은 하지 않았으므로 특정 함수의 개선률로 해석하지 않는다.

| APM 조건 | 응답률 변화 % | p99 변화 % | 게임 CPU 변화 pp |
|---|---:|---:|---:|
| OFF | -2.96 | +43.03 | +0.34 |
| ON | +1.49 | -29.45 | +1.38 |

현재 `PacketDispatcher`는 로그인된 연결의 Heartbeat에도 `IPlayerGrain.IsCurrentSessionAsync`를 호출한다. 이전 코드에 없던 세션 보호 비용이다. 신규 계정 생성에도 초기 재화 지급 트랜잭션이 추가됐다. 이 차이는 코드로 확인했지만 지연 전체의 원인이라고 확정하지 않는다.

## 전체 실행 기록

모든 실행에서 30,000개 로그인 연결을 60초 이상 유지했고 접속 실패·로그인 실패·비정상 종료는 0이었다. 이는 기능 검사의 통과이며 응답시간 SLA 통과를 뜻하지 않는다. OS TCP 관측 최고값도 각 실행 30,000이었다.

| 실행 | 조건 | 유지 응답/초 | p99 ms | 게임 CPU % | 호스트 CPU % |
|---|---|---:|---:|---:|---:|
| ab-clean-01-before-off | 이전 코드 · APM OFF | 5991.4 | 2.238 | 1.37 | 16.24 |
| ab-clean-02-after-on | 현재 코드 · APM ON | 5846.3 | 264.755 | 3.98 | 29.40 |
| ab-clean-03-after-off | 현재 코드 · APM OFF | 5133.2 | 1572.422 | 1.75 | 31.08 |
| ab-clean-04-before-on | 이전 코드 · APM ON | 5886.4 | 141.699 | 2.55 | 28.77 |
| ab-clean-05-after-off | 현재 코드 · APM OFF | 4812.5 | 115.893 | 1.56 | 30.18 |
| ab-clean-06-before-on | 이전 코드 · APM ON | 5557.6 | 217.667 | 3.07 | 25.84 |
| ab-clean-07-before-off | 이전 코드 · APM OFF | 5290.1 | 81.025 | 1.23 | 27.11 |
| ab-clean-08-after-on | 현재 코드 · APM ON | 5640.6 | 144.059 | 4.28 | 30.59 |
| ab-clean-09-before-on | 이전 코드 · APM ON | 5221.7 | 204.241 | 2.60 | 38.01 |
| ab-clean-10-after-off | 현재 코드 · APM OFF | 5246.8 | 105.614 | 1.71 | 28.10 |
| ab-clean-11-after-on | 현재 코드 · APM ON | 5241.2 | 144.085 | 3.76 | 29.28 |
| ab-clean-12-before-off | 이전 코드 · APM OFF | 5219.9 | 96.917 | 1.53 | 20.47 |

## APM 수집 대조와 정체

APM ON 6회 각각 워밍업을 포함한 로그인 31,000건과 클라이언트 Heartbeat 응답 수를 SQLite에 대조했다. metric/진입 span의 건수와 trace ID 집합이 일치했고, 중복 trace ID와 양쪽 연결 누락은 0이었다. 각 DB의 `PRAGMA quick_check`는 ok였다. 정상 요청의 내부 상세는 1% 선택 정책이며 모든 내부 span을 전량 저장했다는 뜻이 아니다.

| 실행 | 로그인 | Heartbeat | HTTP 실패 시도 | 스풀/재전송 요청 | 관측 request queue 최고 |
|---|---:|---:|---:|---:|---:|
| ab-clean-02-after-on | 31,000 | 436,806 | 0 | 0/0 | 5,052 |
| ab-clean-04-before-on | 31,000 | 434,955 | 0 | 0/0 | 2,311 |
| ab-clean-06-before-on | 31,000 | 417,814 | 1 | 0/0 | 1,586 |
| ab-clean-08-after-on | 31,000 | 421,136 | 1 | 3,072/3,072 | 10,619 |
| ab-clean-09-before-on | 31,000 | 369,781 | 1 | 0/0 | 1,676 |
| ab-clean-11-after-on | 31,000 | 392,588 | 1 | 0/0 | 24,880 |

마지막 관측 시점의 메모리 큐·요청 큐·스풀은 비었으며 requestsDropped/detailsDropped/spoolErrors는 모두 0이었다. 큐 최고값은 5초 주기 관측값으로 실제 순간 최고값을 보장하지 않는다.

런타임 watchdog의 원문 관측은 다음과 같다. 정체 로그의 GC 중단 값까지 원문으로 기록한다. 재측정의 정체 대부분은 GC 중단 값이 0이므로 GC 원인으로 단정하지 않는다.

- `ab-clean-02-after-on`: .NET Runtime Platform stalled for 00:00:03.4821175. Total GC Pause duration during that period: 00:00:00.0373690. We are now using a total of 402MB memory. Collection counts per generation: 0: 72, 1: 29, 2: 18
- `ab-clean-03-after-off`: .NET Runtime Platform stalled for 00:00:08.0438951. Total GC Pause duration during that period: 00:00:00. We are now using a total of 303MB memory. Collection counts per generation: 0: 38, 1: 17, 2: 13
- `ab-clean-04-before-on`: .NET Runtime Platform stalled for 00:00:05.8182354. Total GC Pause duration during that period: 00:00:00.0055630. We are now using a total of 351MB memory. Collection counts per generation: 0: 62, 1: 28, 2: 18
- `ab-clean-05-after-off`: .NET Runtime Platform stalled for 00:00:12.6533527. Total GC Pause duration during that period: 00:00:00. We are now using a total of 361MB memory. Collection counts per generation: 0: 35, 1: 16, 2: 12
- `ab-clean-06-before-on`: .NET Runtime Platform stalled for 00:00:05.7241839. Total GC Pause duration during that period: 00:00:00. We are now using a total of 365MB memory. Collection counts per generation: 0: 61, 1: 29, 2: 18
- `ab-clean-07-before-off`: .NET Runtime Platform stalled for 00:00:08.9599924. Total GC Pause duration during that period: 00:00:00. We are now using a total of 320MB memory. Collection counts per generation: 0: 37, 1: 17, 2: 13
- `ab-clean-08-after-on`: .NET Runtime Platform stalled for 00:00:05.0468652. Total GC Pause duration during that period: 00:00:00.0115840. We are now using a total of 489MB memory. Collection counts per generation: 0: 76, 1: 31, 2: 20
- `ab-clean-09-before-on`: .NET Runtime Platform stalled for 00:00:12.0271846. Total GC Pause duration during that period: 00:00:00. We are now using a total of 485MB memory. Collection counts per generation: 0: 43, 1: 23, 2: 17
- `ab-clean-10-after-off`: .NET Runtime Platform stalled for 00:00:07.9871534. Total GC Pause duration during that period: 00:00:00. We are now using a total of 309MB memory. Collection counts per generation: 0: 42, 1: 20, 2: 14
- `ab-clean-11-after-on`: .NET Runtime Platform stalled for 00:00:07.9325875. Total GC Pause duration during that period: 00:00:00. We are now using a total of 457MB memory. Collection counts per generation: 0: 73, 1: 32, 2: 19
- `ab-clean-12-before-off`: .NET Runtime Platform stalled for 00:00:07.8790626. Total GC Pause duration during that period: 00:00:00. We are now using a total of 322MB memory. Collection counts per generation: 0: 38, 1: 17, 2: 12

## 조건·재현·한계

- 이전 게임: `7fd5d04857e341e08ee6fd3ecf22c790286c7b21`의 `src/GameServer`. 현재 게임: 2026-11-10 작업 트리 스냅샷. 기존 사용자 변경을 보존했다.
- 양쪽 모두 최신 APM Agent와 수집기. 기준 APM HEAD는 `8eecd7d3b9bfb9c2afef179a4d41bb60bea1b1b8`이나 큐 복구 변경은 미커밋 상태이므로 실제 소스·바이너리 SHA-256을 별도 manifest에 보존했다. 과거 APM 버전과 최신 APM 버전의 직접 비교는 아니다.
- Ryzen 7 7800X3D, 8코어/16스레드, RAM 약 31.1 GiB, Windows 11 Pro build 26200, .NET SDK 10.0.301 Release, PostgreSQL 18.4, Redis Windows 3.0.504.
- 네 조건을 3회씩, 순서: 이전 OFF / 현재 ON / 현재 OFF / 이전 ON → 현재 OFF / 이전 ON / 이전 OFF / 현재 ON → 이전 ON / 현재 OFF / 현재 ON / 이전 OFF. 실행 간 30초 휴지.
- 매번 새 게임 프로세스, 동일 시드에서 만든 새 PostgreSQL DB, 새 Redis AOF, ON일 때 새 수집기·SQLite. 기존 사용자 DB는 초기화하지 않았다. PG shared_buffers 256MB, max_connections 300; Redis AOF everysec, maxmemory 2GB/noeviction.
- 워밍업 1,000명·초당 접속 목표 1,000명·10초 유지 후 연결/수집 큐 drain. 본 측정 30,000명·초당 접속 목표 3,000명·60초 유지·Heartbeat 응답 후 5초 간격. 실제 접속 증가 시간은 각 client-result에 보존했다.
- `APM_ENABLED=0`이면 LocalTelemetry 초기화, OpenTelemetry provider, Redis 계측, Orleans Activity 전파를 끈다. no-op 계측 호출 코드 자체는 남는다. `APM_ENABLED=1`은 이를 켠다. 양쪽 모두 `OTEL_EXPORTER_OTLP_ENABLED=0`: 별도 Elastic 수출 비용은 이번 범위 밖이다.
- APM 기본 요청 전량, 정상 상세 0.01, slow 기준 100ms, 기존 상세 상한/기본 큐/스풀 정책 유지. 수집 API의 런타임 조회는 ON에서 5초 간격이다.
- Win32 프로세스 CPU 누적값을 클라이언트 hold 시작/끝 UTC에 선형 보간하여 차분했다. 약 1초 주기 working set/private bytes, 호스트 CPU, 약 5초 주기 TCP를 기록했다. APM OFF의 CPU/메모리도 외부에서 측정했다.
- APM RuntimeLoopAsync의 CPU 기준값은 전송 전, 시간 기준값은 전송 후로 갱신되는 코드상 불일치를 발견했다. 전송 정체 후 대시보드 CPU가 왜곡될 수 있어 후속 수정 항목으로 기록했다. 이번 비교는 그 표시값이 아닌 독립 Win32 CPU 누적값을 사용하며, 측정 중 바이너리를 바꾸지 않았다.
- HB RTT는 유지 구간 안에서 시작한 요청의 최종 응답까지 포함하고, 응답률은 유지 구간 안에 완료된 응답 수/실제 유지 시간이다. 로그인 RTT는 접속 증가 구간의 별도 통계다.
- **호스트 조건:** 사용자가 게임을 종료한 뒤 해당 게임/녹화 프로세스가 사라진 것을 확인하고 전체 비교를 새로 시작했다. 시작 전 15초 호스트 CPU 평균 8.96%, 여유 메모리 18,147 MiB였다. 기본 데스크톱 프로세스는 남아 있으므로 완전히 격리된 전용 장비는 아니다. 사용자 프로세스나 OS 설정을 임의로 변경하지 않았다. n=3이므로 통계적 유의성이나 운영 장비의 성능을 주장하지 않는다.
- 로그인/Heartbeat만 측정했다. 가챠·보드·길드 혼합 부하, 네트워크 지연·장애, 다중 silo, 장시간 누적 DB, LLM 분석 병행, 서버 최대 수용량은 이 실험의 범위 밖이다.
- 앞선 공유 부하 실험의 준비 단계에서 pg_ctl 출력 핸들 상속 문제를 수정했다. 이후 사용자가 게임을 종료하고 순성능 재측정을 요청하여 새 폴더·새 DB에서 전체 12회를 다시 시작했다. 이전 결과는 삭제하지 않았고 두 환경을 합산하지 않는다. 재측정 안에서 임의로 좋은 실행만 선택하지 않았다.

### 코드 변경과 재실행

게임 `Config/ServerConfig.cs`와 `Program.cs`에 `APM_ENABLED`/`OTEL_EXPORTER_OTLP_ENABLED`를 추가했다. 둘 다 미지정이면 기존처럼 켜진다. APM 저장소의 `tools/verification/ConcurrentLoad/Program.cs`에는 `holdStartedUtc`, `holdEndedUtc`, `holdSent`, `holdAck`, `holdHeartbeatRttMs`를 추가했다.

격리된 게임 서버가 실행된 상태에서 동일 클라이언트 명령을 사용한다. 실행 ID와 출력 파일은 매번 새 값이어야 한다.

```powershell
dotnet <ConcurrentLoad.dll> --count 30000 --rate 3000 --hold 60 --heartbeat 5 --port <전용_TCP_포트> --run <새_ID> --output <새_결과_JSON>
```

원시 증거와 실행·분석 스크립트 위치는 두 저장소의 `.local-evidence/2026-11-10-ab/manifest.json`에 기록했다. `protocol.json`, 소스·바이너리 해시, 준비/실행 스크립트, 실행별 main/warmup 결과·외부 관측·서버 로그·SQLite·ledger-verified, summary.json을 보존한다. 소스와 빌드는 최초 비교 폴더에서 고정하고 재측정에 동일 바이너리를 복사했다. 소스 스냅샷은 외부 증거 경로라 Git에 포함되지 않으므로 HEAD만으로 당시 작업 트리를 대체하지 않는다.

### 최종 검증

게임 솔루션 및 직접 GameServer Release 빌드, 부하 도구 Release 빌드는 경고/오류 0이었다. 기존 단위 검증 40,200 assertions와 두 저장소 git diff --check가 통과했다. 이번 12회는 TCP/로그인/Heartbeat 실검사이며 과거 콘텐츠 전체 통합 검증 40,298개를 이번에 다시 실행했다는 뜻은 아니다. NuGetAudit=false를 사용했으므로 취약점 감사 성공을 의미하지 않는다.

직접 게임 프로젝트 빌드를 사용하면 APM Agent도 Release로 빌드된다. 현재 솔루션 빌드만 사용할 때 외부 APM 프로젝트가 Debug 경로로 잡히는 것을 확인하여 최종 전달 산출물은 직접 GameServer Release 빌드로 재확인했다. 측정용 실행본도 직접 프로젝트 publish -c Release와 명시적인 ApmAgentProject 경로로 만들었고 두 조건의 APM 바이너리 해시가 같다.

추가 성능 판단에는 독립 호스트에서 같은 반복 측정, GC/할당 프로파일, 세션 검사 경로 분리 실험이 필요하다. 이번에는 원인을 분리하지 않은 상태에서 세션 보호나 내구성 설정을 약화시키는 변경을 하지 않았다.
