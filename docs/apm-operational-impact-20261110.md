# APM이 서버 프로젝트에 기여한 점 — 2026-11-10

트릭컬 서버 직무를 염두에 둔 개인 프로젝트에서, 기능 구현에 더해 장애를 추적하고 고부하에서도 검증 근거를 남기는 운영 역량을 확인했다. 에피드게임즈의 실제 서버 구현이나 운영 수치를 뜻하지 않는다.

APM의 기여는 서버를 자동으로 빠르게 만드는 기능보다 **요청 흐름을 코드와 연결하고, 계측 누락을 발견하며, 장애 시에도 조사 자료를 보존하는 것**이다. 정합성·복구를 위한 서버 로직 변경과 APM 자체의 수집 보완을 구분한다.

## 실제로 확인한 개선

| 개선 | 발견한 문제와 조치 | 확인한 결과 | 운영상 의미 |
|---|---|---|---|
| 비동기 매칭 결과 추적 | 매칭은 성공했지만 요청 20건이 unknown으로 남았다. 접수/생성 결과를 명시하고 별도 MatchResult trace를 원 요청에 연결했다. | 20명 시범 재검증에서 unknown 0건, 결과 전송 trace 20건 및 원 요청 연결 20건 | 대기열 접수, 매칭 생성, 실제 응답 쓰기를 구분해 조사할 수 있다. |
| 고부하 수집 근거 보존 | 3만 부하에서 Heartbeat 12,475건 누락을 대조했고, 다른 실행에서는 기본 유실 카운터 98,330을 확인했다. 저장 연결/SQL 재사용, WAL 체크포인트 분리, 묶음 전송을 보완했다. | 최종 비교의 APM ON 6회에서 기본 요청 2,659,080건의 metric/진입 span 건수와 trace ID를 대조해 누락·중복 0건 | 요청 성공과 계측 유실을 구분하고, 부하가 클 때도 조사 근거가 남는지 검증할 수 있다. |
| 미전송 기록 복구 | 전송 실패나 큐 포화 때 디스크에 묶음을 보관하고 같은 BatchId로 재전송한다. 수집기의 커밋 확인 뒤 삭제한다. | ab-clean-08-after-on에서 3,072건 보관 후 3,072건 재전송, 최종 스풀 0건 | 일시적인 수집 정체 때문에 기록을 즉시 버리지 않는다. |
| 처리 단계별 원인 조사 | 기존 패킷→핸들러→Grain→DB/Redis→응답 쓰기 계측을 활용하고 비동기 결과 연결을 보완했다. | 서버 처리시간과 클라이언트 RTT를 각각 집계했다. 현재 Heartbeat 서버 처리 p99 중앙값 0.177ms와 RTT p99 144.085ms를 구분한다. | 어느 구간을 더 조사해야 할지 좁힐 수 있다. 차이를 전부 클라이언트 원인으로 단정하지 않는다. |

매칭 시범과 3만 로그인/Heartbeat 부하는 서로 다른 검사다. 매칭을 3만 명으로 검증했다는 뜻이 아니다. 기본 요청 2,659,080건에는 각 실행의 워밍업 로그인 1,000건과 Heartbeat가 포함된다. 정상 내부 상세는 1% 선택 및 상한 정책을 유지했으며, 모든 내부 span의 전량 수집을 주장하지 않는다.

## 구현과 검증 코드

- 매칭 결과 구분: [MatchHandler](https://github.com/Sewqp/Trickcal-Bottleneck-Server/blob/main/src/GameServer/Packet/Handler/MatchHandler.cs), [MatchGrain](https://github.com/Sewqp/Trickcal-Bottleneck-Server/blob/main/src/GameServer/Grains/MatchGrain.cs).
- 실제 전송 trace와 원 요청 연결: [PlayerSessionObserver](https://github.com/Sewqp/Trickcal-Bottleneck-Server/blob/main/src/GameServer/Network/PlayerSessionObserver.cs), [ApmReporter](https://github.com/Sewqp/Trickcal-Bottleneck-Server/blob/main/src/GameServer/Metrics/ApmReporter.cs), [매칭 수집 대조 도구](https://github.com/Sewqp/Trickcal-Bottleneck-Server/blob/main/tools/check_match_telemetry.py).
- 배치 저장과 체크포인트 분리: [TelemetryBatchWriter](../src/CollectionServer/Services/TelemetryBatchWriter.cs), [TelemetryCheckpointService](../src/CollectionServer/Services/TelemetryCheckpointService.cs), [원자성·중복·동시 저장 검사](../tools/verification/CollectorWriterChecks/Program.cs).
- 디스크 보관·재전송: [TelemetrySender](../src/Apm.Agent/TelemetrySender.cs), [TelemetrySpool](../src/Apm.Agent/TelemetrySpool.cs), [장애·재시작·용량 제한 검사](../tools/verification/SenderRecoveryChecks/Program.cs).
- 서버 처리시간 측정 경계: [PacketDispatcher](https://github.com/Sewqp/Trickcal-Bottleneck-Server/blob/main/src/GameServer/Packet/PacketDispatcher.cs). Activity 시작부터 처리 종료까지이며, 디스패치 전 소켓/스케줄링 대기와 클라이언트 수신은 제외한다.

## 검증 범위와 남은 과제

3만 동접 60초의 이전/현재 코드 × APM OFF/ON을 각각 3회 실행했다. APM을 켠 6회의 기본 요청 대조와 SQLite quick_check를 완료했다. 전송 실패 시도 총 4회와 디스크 재전송도 결과에 포함했다. 과거 큐 보완 단계의 별도 180초 검증은 아래 수집 복구 기록에 보존했다.

서버 처리시간은 평균/분위수와 실행별 범위를 함께 봐야 한다. 현재 로그인 서버 처리 p99의 실행별 범위는 11.4705–241.2945ms이며, 세 실행의 중앙값은 17.3829ms다. 이 숫자를 안정적인 최대 처리량이나 전체 콘텐츠 성능으로 확대하지 않는다.

원인 미확정 런타임 정체, APM RuntimeLoopAsync의 CPU/시간 기준 갱신 시점 불일치, 최대 TPS와 콘텐츠 혼합 부하 검증이 남아 있다. CPU/메모리 비교는 외부 Win32 관측을 사용했다. LLM 정확도나 자동 원인 진단의 정량 성과는 검증하지 않았다.

스풀은 디스크에 저장된 묶음의 복구 수단이며, 강제 종료 순간 메모리에만 있던 항목이나 디스크 용량 소진까지 보장하지 않는다. 재화 정합성·가챠 멱등성·세션 보호는 별도로 구현한 서버 로직의 성과이며 APM이 자동으로 해결했다고 표현하지 않는다.

## 근거

- [공개 집계 JSON](evidence/apm-operational-summary-20261110.json): 실행별 대조 건수, 전송/복구 카운터, 서버 처리시간 중앙값과 범위. 원시 DB를 대신하는 독립 검증 자료는 아니다.
- [반복 측정과 APM 서버 처리시간](apm-ab-comparison-20261110.md).
- [서버 정합성·복구 및 매칭 시범 기록](https://github.com/Sewqp/Trickcal-Bottleneck-Server/blob/main/docs/reliability-upgrade-20261109.md).
- [APM 저장 병목·디스크 복구·180초 검증](../docs/apm-queue-recovery-20261110.md).

원시 DB, 전체 trace, 개인 설정은 비공개 증거 폴더에 보존하며 Git에는 포함하지 않는다.
