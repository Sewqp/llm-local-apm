export default function SummaryCards({ summary }) {
  if (!summary || summary.length === 0) {
    return <div className="panel empty-state">아직 수집된 메트릭이 없습니다. 대상 서버가 실행 중인지 확인하세요.</div>;
  }

  const totalCount = summary.reduce((sum, s) => sum + s.count, 0);
  const weightedAvg =
    summary.reduce((sum, s) => sum + s.avgDurationMs * s.count, 0) / (totalCount || 1);
  const overallErrorRate =
    summary.reduce((sum, s) => sum + s.errorRate * s.count, 0) / (totalCount || 1);
  const worstEndpoint = summary[0];

  return (
    <div className="summary-cards">
      <Card label="총 요청 수" value={totalCount.toLocaleString()} />
      <Card label="평균 응답시간" value={`${weightedAvg.toFixed(0)} ms`} />
      <Card label="에러율" value={`${(overallErrorRate * 100).toFixed(1)} %`} tone={overallErrorRate > 0.02 ? "warn" : "ok"} />
      <Card
        label="가장 느린 엔드포인트"
        value={worstEndpoint ? `${worstEndpoint.path}` : "-"}
        sub={worstEndpoint ? `${worstEndpoint.avgDurationMs} ms (p95 ${worstEndpoint.p95DurationMs} ms)` : ""}
      />
    </div>
  );
}

function Card({ label, value, sub, tone }) {
  return (
    <div className={`card ${tone ? `card-${tone}` : ""}`}>
      <div className="card-label">{label}</div>
      <div className="card-value">{value}</div>
      {sub && <div className="card-sub">{sub}</div>}
    </div>
  );
}
