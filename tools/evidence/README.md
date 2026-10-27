# 비공개 APM 증거 보존 도구

Python 3.10 이상 표준 라이브러리만 사용한다. 서버·모델·네트워크 호출을 시작하지 않는다. 원본 DB는 SQLite 읽기 전용 연결과 `backup()`으로 복제하고 새 사본의 무결성을 확인한다. 이미 존재하는 출력 디렉터리에는 덮어쓰지 않는다.

```powershell
python tools/evidence/capture_baseline.py --apm-root . --server-root ../Trickcal-Bottleneck-Server --output .local-evidence/NEW-CAPTURE
python tools/evidence/verify_baseline.py --bundle .local-evidence/2026-10-26 --cases docs/evaluation/cases.json
```

첫 명령은 고정된 기존 프로젝트 경로·테이블 구성을 위한 보존 스크립트다. 선택 인자 `--portfolio-dir`, `--media-dir`, `--history-excerpts`, `--job-image`로 관련 자료를 함께 복사할 수 있다. 선택 입력 없이 실행한 새 번들에는 2026-10-26 사례의 모든 미디어가 포함되지 않으므로 같은 사례 목록으로 검증하지 않는다.

2026-10-26 번들은 별도로 현재 그래프·로컬 설정·기존 메모리·컨트롤러 diff와 사용자 확인 발췌를 추가한 54개 파일이다. 추가 파일도 같은 manifest에 등록했다. 원본 설정과 개인정보가 들어갈 수 있으므로 번들 전체를 Git에 추가하거나 외부로 전송하지 않는다.

검증 명령은 파일 크기·SHA-256, SQLite 무결성·행 수, 평가 사례의 파일/분석 ID 참조를 확인한다. 실제 탐지 성능·모델 정확도·보안 통제·완전한 과거 재현성을 검증하는 명령은 아니다. 현재 상태의 파일을 보존한 시각과 과거 테스트 시각을 구분한다.
