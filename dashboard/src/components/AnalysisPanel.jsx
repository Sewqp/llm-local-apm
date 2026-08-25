export default function AnalysisPanel({ analyses, onRun, running }) {
  return (
    <div className="panel">
      <div className="panel-header">
        <h3>이상탐지 원인 분석 (로컬 LLM)</h3>
        <button onClick={onRun} disabled={running}>
          {running ? "분석 중..." : "이상탐지 + LLM 분석 실행"}
        </button>
      </div>

      {analyses.length === 0 && (
        <div className="empty-state">
          아직 분석 결과가 없습니다. 버튼을 눌러 최근 구간의 이상탐지 및 LM Studio 분석을 실행하세요.
        </div>
      )}

      <ul className="analysis-list">
        {analyses.map((a, i) => (
          <li key={i} className="analysis-item">
            <div className="analysis-item-header">
              <span className="badge badge-advisory">참고용 추천</span>
              <span className="analysis-path">{a.serverName} · {a.path}</span>
              <span className="analysis-time">{new Date(a.timestamp).toLocaleString()}</span>
            </div>
            <div className="analysis-reason">
              {a.reason} — 관측 {a.observedDurationMs.toFixed(0)}ms / 기준선 {a.baselineDurationMs.toFixed(0)}ms
            </div>
            <p className="analysis-text">{a.llmAnalysis}</p>
          </li>
        ))}
      </ul>
    </div>
  );
}
