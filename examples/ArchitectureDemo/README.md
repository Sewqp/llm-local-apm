# 다른 서버 구조에서 APM 실행하기

HTTP 서버, 두 프로세스의 HTTP 호출, 비동기 큐 워커에 같은 APM을 연결하는 합성 예제입니다. 실제 SQLite 조회/기록을 수행하며 의도적 지연은 `simulation.*` 구간에 별도 표시합니다.

| 파일 | 역할 |
|---|---|
| Program.cs | Edge/Stock 역할, 로컬 주소, Run ID, 수집기 설정 |
| DemoTelemetryMiddleware.cs | HTTP 경로 템플릿·상태·가명 유저·세션 계측 |
| CheckoutHandler.cs / CatalogStore.cs | 순차·병렬 처리, 실제 SQLite, 모의 지연·오류 |
| RemoteCheckout.cs / StockHandler.cs | 별도 프로세스로 traceparent 전달 |
| RewardQueue.cs / RewardWorker.cs | 용량 8 메모리 큐, 접수·처리 분리, 인과 연결 |

```text
HTTP client → Edge :5241 → SQLite
                   ├─ HTTP client span → Stock :5242 → SQLite
                   └─ HTTP 202 → Channel(8) → Worker → SQLite
                                      별도 trace ──link──▶ 접수 요청
```

## 실행·검증

저장소 루트에서 .NET 10 SDK와 Python 3.10 이상을 사용합니다. 출력 폴더는 새 경로여야 합니다.

```powershell
dotnet build src/CollectionServer/CollectionServer.csproj -c Release -o .local-evidence/architecture-build/collector
dotnet build examples/ArchitectureDemo/ArchitectureDemo.csproj -c Release -o .local-evidence/architecture-build/demo
python tools/verification/run_architectures.py --repo . --collector-dll .local-evidence/architecture-build/collector/CollectionServer.dll --demo-dll .local-evidence/architecture-build/demo/ArchitectureDemo.dll --out .local-evidence/architecture-run-01 --keep-running
```

포트 5239/5241/5242가 사용 중이면 기존 프로세스를 건드리지 않고 거부합니다. 기존 DB도 덮어쓰지 않습니다. `--keep-running`을 빼면 검증 후 이번 실행의 프로세스를 종료합니다. 옵션 사용 시 `processes.json`의 PID와 명령줄을 확인하고 해당 프로세스만 종료하십시오.

결과: `results.json`, `checks.json`, 개별 응답·추적, 카운터, 일관된 SQLite 스냅샷, 프로세스 정보와 로그. 대시보드는 저장소 wwwroot 빌드를 사용합니다. [프런트엔드 빌드 안내](../../docs/apm-v2-runbook.md)를 참조하십시오.

## 호출 예

기본 주소: `http://127.0.0.1:5241`

- GET `/demo/sequential`, `/demo/parallel`: 동일한 15/25/35ms 모의 지연과 실제 SQLite 조회.
- GET `/demo/remote`, `/demo/remote-error`: Stock 서비스 정상/오류.
- POST `/demo/jobs`, `/demo/jobs?fail=true`: 접수 202, 포화 429. 반환 ID로 GET `/jobs/{id}` 조회.
- GET `/demo/slow`, `/demo/error`, `/demo/rejected`, `/demo/timeout`: 200/500/409/504 검증.

대시보드 `http://127.0.0.1:5239/`에서 **HTTP 요청** 또는 **백그라운드 작업**을 선택합니다.

## 제한

합성 사용자·주입 오류이며, HTTP 동시 호출 32개를 검증합니다. 3만 게임 로그인 동접이나 유지 연결 수와 구분하십시오. 상세 100%는 소규모 예제 설정이고 게임의 정상 상세 1% 정책은 유지됩니다.

프로세스당 에이전트 하나, 한 trace에서 프로세스당 진입 구간 하나를 전제로 합니다. 동일 프로세스로 돌아오는 순환 호출이나 중첩 진입, 다중 호스트 시계 보정은 검증하지 않았습니다. 자동 계측/OTLP 호환 제품이 아니며 통합 코드를 직접 작성했습니다.

큐는 영속 저장·재시도·DLQ 없는 메모리 Channel입니다. 재시작 시 접수 작업이 유실될 수 있습니다. 작업 루트는 접수부터 완료까지이며 queue.wait를 별도로 표시합니다. 상태 목록이 4,096개면 추가 접수를 거부합니다. HTTP 202는 작업 성공을 뜻하지 않습니다.

모든 주소는 loopback이며 외부 수집 endpoint/redirect를 차단합니다. 공개된 HMAC 키는 합성 데이터용으로 실제 사용자에 재사용하면 안 됩니다. 인증·권한 부여 또는 유출 위험 0을 증명하는 예제가 아닙니다.

[실측 결과·검증·과거 기록과의 구분](../../docs/apm-architectures-20261026.md).
