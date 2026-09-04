export default function AnalysisPanel({ analyses, onRun, running, runningMode }) {
  return (
    <div className="panel">
      <div className="panel-header">
        <h3>이상탐지 원인 분석 (로컬 LLM)</h3>
        <div className="analysis-run-buttons">
          <button onClick={() => onRun("quick")} disabled={running} title="이상탐지된 엔드포인트 위주로 빠르게 분석">
            {runningMode === "quick" ? "빠른 분석 중..." : "빠른 분석"}
          </button>
          <button onClick={() => onRun("full")} disabled={running} title="co-occurrence 신호가 없어도 배경 서비스까지 강제로 넓게 탐색">
            {runningMode === "full" ? "전체 분석 중..." : "전체 분석"}
          </button>
        </div>
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
              <span className={`badge badge-mode-${a.mode}`}>{a.mode === "full" ? "전체 분석" : "빠른 분석"}</span>
              <span className="analysis-path">{a.serverName} · {a.path}</span>
              <span className="analysis-time">{new Date(a.timestamp).toLocaleString()}</span>
            </div>
            <div className="analysis-reason">
              {a.reason} — 관측 {a.observedDurationMs.toFixed(0)}ms / 기준선 {a.baselineDurationMs.toFixed(0)}ms
            </div>
            <p className="analysis-text">{a.llmAnalysis}</p>
            {a.suspectedCodeSnippets && a.suspectedCodeSnippets.length > 0 && (
              <div className="suspect-candidates">
                {a.suspectedCodeSnippets.map((s) => (
                  <div key={s.index} className="suspect-candidate">
                    <div className="suspect-candidate-header">
                      <span className="suspect-index">{s.index}</span>
                      <span className="suspect-reason">{s.reason}</span>
                    </div>
                    {s.lines ? (
                      <pre className="code-snippet">
                        {s.lines.map((l) => (
                          <div
                            key={l.lineNumber}
                            className={`code-line${l.isSuspect ? " code-line-suspect" : ""}`}
                          >
                            <span className="code-line-no">{l.lineNumber}</span>
                            <span className="code-line-text">{l.text}</span>
                          </div>
                        ))}
                      </pre>
                    ) : (
                      <div className="code-snippet-missing">
                        {s.filePath
                          ? `${s.filePath}:${s.lineNumber} — 소스 파일을 찾지 못했습니다`
                          : "코드 위치를 특정하지 못했습니다"}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
