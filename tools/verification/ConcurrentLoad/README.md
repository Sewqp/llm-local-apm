# ConcurrentLoad

Trickcal TCP 로그인/Heartbeat의 제한된 로컬 부하 검사다. IPv4 loopback만 사용한다.
3만 명 전체 게임 콘텐츠나 최대 처리량을 검사하는 도구는 아니다.

```powershell
dotnet run -c Release --project tools/verification/ConcurrentLoad -- --count 30000 --rate 3000 --hold 60 --heartbeat 5 --port 9160 --run unique-run-id --output new-result.json
```

각 옵션은 값과 함께 지정한다. count 최대 30,000, rate 최대 5,000,
hold 최대 180초, heartbeat 1–10초다. 기존 결과 파일은 덮어쓰지 않는다.
rate는 목표 접속 증가율이며 실제 완료율은 서버/클라이언트 자원과 지연에 따라 낮아진다.

모든 접속/로그인이 성공 또는 실패 상태에 도달하면 hold가 시작된다.
기능 검사 통과에는 전원 연결/로그인, hold 내 동접 유지, 비정상 종료 0,
보낸 Heartbeat와 응답 수 일치가 필요하다. 응답시간 SLA 통과 여부는 별도 판단한다.

결과는 다음을 구분한다.

- `connectRttMs`, `loginRttMs`: 접속 증가 구간의 접속/로그인 지연.
- `heartbeatRttMs`: 전체 실행 동안의 Heartbeat 지연.
- `holdStartedUtc`, `holdEndedUtc`: 외부 CPU/메모리 관측과 맞추는 UTC 경계.
- `observedHoldSeconds`: 단조 시계로 측정한 실제 유지 시간.
- `holdSent`, `holdHeartbeatRttMs`: hold 안에서 전송을 시작한 요청과 그 최종 응답 지연. 종료 후 drain에서 받은 응답도 포함한다.
- `holdAck`: hold 안에서 완료된 응답 수. 시작 경계 전에 보낸 요청의 응답이 포함될 수 있어 `holdSent`와 항상 같지는 않다.

유지 구간의 실제 응답률은 `holdAck / observedHoldSeconds`다.
클라이언트는 매 응답 후 heartbeat초를 기다리므로 요청률은 지연에 따라 낮아진다.
`30,000 / 5 = 6,000`은 대략적인 부하 상한이며 서버의 최대 TPS가 아니다.
분위수는 정렬 후 nearest-rank로 계산한다. 합산한 전체 분위수와 실행별 분위수의
중앙값은 다른 통계이므로 반복 실험에서 어떤 값을 썼는지 명시해야 한다.

APM ON/OFF 비용 측정에서는 서버의 `APM_ENABLED`를 명시하고,
별도 OTLP/Elastic 비용을 제외할 때 `OTEL_EXPORTER_OTLP_ENABLED=0`을 사용한다.
서버 코드 전후 비교는 동일한 APM 버전, DB/시드, 하드웨어, 부하 설정을 사용한다.
CPU/메모리는 APM OFF에서도 동일하게 얻을 수 있는 외부 관측 수단으로 측정한다.

실측과 한계는 [2026-11-10 비교 기록](../../../docs/apm-ab-comparison-20261110.md)에 있다.
