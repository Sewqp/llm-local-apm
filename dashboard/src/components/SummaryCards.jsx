const duration = value => `${Number(value).toFixed(value < 1 ? 3 : 2)} ms`;

export default function SummaryCards({ summary }) {
  if (!summary?.length) return <div className="panel empty-state">최근 구간에 클라이언트 요청 메트릭이 없습니다.</div>;
  const count = summary.reduce((sum, item) => sum + item.count, 0);
  const average = summary.reduce((sum, item) => sum + item.avgDurationMs * item.count, 0) / count;
  const errorRate = summary.reduce((sum, item) => sum + item.errorRate * item.count, 0) / count;
  const rejectedRate = summary.reduce((sum, item) => sum + (item.rejectedRate ?? 0) * item.count, 0) / count;
  const throughput = summary.reduce((sum, item) => sum + (item.requestsPerSecond ?? 0), 0);
  return <>
    <div className="summary-cards">
      <Card label="총 요청 수" value={count.toLocaleString()} />
      <Card label="처리량" value={`${throughput.toFixed(2)} req/s`} sub="상단 조회 기간 전체 기준" />
      <Card label="평균 처리 시간" value={duration(average)} />
      <Card label="서버 오류율" value={`${(errorRate * 100).toFixed(2)}%`} tone={errorRate > .02 ? "warn" : "ok"} />
      <Card label="업무상 거절률" value={`${(rejectedRate * 100).toFixed(2)}%`} />
      <Card label="가장 느린 요청 p95" value={duration(summary[0].p95DurationMs)} sub={summary[0].path} />
    </div>
    <details className="panel"><summary style={{ cursor: "pointer" }}>요청별 처리량 · 지연 분포</summary>
      <div className="trace-table-scroll"><table className="trace-table"><thead><tr><th>요청</th><th>건수</th><th>req/s</th><th>p50</th><th>p95</th><th>p99</th><th>서버 오류</th><th>거절</th></tr></thead>
        <tbody>{summary.map(item => <tr key={item.path}><td>{item.path}</td><td>{item.count.toLocaleString()}</td>
          <td>{(item.requestsPerSecond ?? 0).toFixed(2)}</td><td>{duration(item.p50DurationMs ?? 0)}</td>
          <td>{duration(item.p95DurationMs)}</td><td>{duration(item.p99DurationMs ?? 0)}</td>
          <td>{(item.errorRate * 100).toFixed(2)}%</td><td>{((item.rejectedRate ?? 0) * 100).toFixed(2)}%</td></tr>)}</tbody>
      </table></div></details>
  </>;
}

function Card({ label, value, sub, tone }) {
  return <div className={`card ${tone ? `card-${tone}` : ""}`}><div className="card-label">{label}</div>
    <div className="card-value">{value}</div>{sub && <div className="card-sub">{sub}</div>}</div>;
}
