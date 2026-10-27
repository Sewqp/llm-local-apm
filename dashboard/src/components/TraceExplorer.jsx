import { useEffect, useMemo, useState } from "react";
import { api } from "../api";
import "./TraceExplorer.css";

const outcomeNames = { success: "성공", rejected: "거절", error: "오류", timeout: "시간 초과", cancelled: "취소", unknown: "결과 미확인" };
const ms = value => `${Number(value ?? 0).toFixed(value < 1 ? 3 : 2)} ms`;
const short = value => value ? `${value.slice(0, 8)}…` : "—";
const time = value => new Date(value).toLocaleTimeString("ko-KR", { hour12: false });

export default function TraceExplorer({ server }) {
  const [draft, setDraft] = useState({ user: "", session: "", operation: "", outcome: "", category: "packet", minutes: "60" });
  const [filters, setFilters] = useState(draft);
  const [skip, setSkip] = useState(0);
  const [page, setPage] = useState({ items: [], hasMore: false });
  const [runtime, setRuntime] = useState([]);
  const [selected, setSelected] = useState(null);
  const [detail, setDetail] = useState(null);
  const [investigations, setInvestigations] = useState([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [detailError, setDetailError] = useState("");
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    let active = true;
    let pending = false;
    const refresh = async () => {
      if (pending) return;
      pending = true;
      try {
        const [traces, resources] = await Promise.all([
          api.getTraces({ ...filters, server, skip, limit: 30 }), api.getRuntime(server),
        ]);
        if (active) { setPage(traces); setRuntime(resources.map(r => ({ ...r, stale: Date.now() - new Date(r.timestamp).getTime() > 20000 }))); setError(""); }
      } catch (e) { if (active) setError(e.message); }
      finally { pending = false; }
    };
    refresh();
    const timer = setInterval(refresh, 5000);
    return () => { active = false; clearInterval(timer); };
  }, [server, filters, skip, refreshKey]);

  useEffect(() => {
    if (!selected) return;
    let active = true;
    let pending = false;
    const refresh = async () => {
      if (pending) return;
      pending = true;
      try {
        const [trace, history] = await Promise.all([api.getTrace(selected), api.getInvestigations(selected)]);
        if (active) { setDetail(trace); setInvestigations(history); setDetailError(""); }
      } catch (e) { if (active) setDetailError(e.message); }
      finally { pending = false; }
    };
    refresh();
    const timer = setInterval(refresh, 5000);
    return () => { active = false; clearInterval(timer); };
  }, [selected, refreshKey]);

  const investigate = async () => {
    setBusy(true);
    try {
      const result = await api.investigateTrace(selected);
      setInvestigations(history => [result, ...history].slice(0, 10));
    } catch (e) { setDetailError(e.message); }
    finally { setBusy(false); }
  };

  const applyFilters = event => {
    event.preventDefault();
    setSkip(0);
    setFilters({ ...draft });
  };
  const followUser = user => {
    const next = { ...draft, user: user ?? "", session: "" };
    setDraft(next); setFilters(next); setSkip(0);
  };

  return <section className="trace-explorer" aria-label="요청 추적">
    <div className="trace-heading">
      <div><span className="trace-kicker">LOCAL OBSERVABILITY</span><h2>요청에서 원인까지</h2>
        <p>요청·내부 호출·오류를 연결하고, 선택한 근거를 로컬 LLM으로 분석합니다.</p></div>
      <span className="local-badge">내부 수집 · 로컬 추론</span>
    </div>

    <div className="runtime-grid">
      {runtime.length === 0 ? <div className="telemetry-note">최근 5분 내 서버 자원 보고가 없습니다. 계측 서버가 실행되면 표시됩니다.</div> :
        runtime.map(sample => <RuntimeCard key={sample.instanceId} sample={sample} />)}
    </div>

    <form className="trace-filters" onSubmit={applyFilters}>
      <label>기간<select value={draft.minutes} onChange={e => setDraft({ ...draft, minutes: e.target.value })}>
        <option value="15">최근 15분</option><option value="60">최근 1시간</option><option value="1440">최근 24시간</option>
      </select></label>
      <label>작업 종류<select value={draft.category} onChange={e => setDraft({ ...draft, category: e.target.value })}>
        <option value="packet">패킷 요청</option><option value="http">HTTP 요청</option><option value="background">백그라운드 작업</option><option value="">전체 작업</option>
      </select></label>
      <label>처리 결과<select value={draft.outcome} onChange={e => setDraft({ ...draft, outcome: e.target.value })}>
        <option value="">전체 결과</option>{Object.entries(outcomeNames).map(([value, name]) => <option key={value} value={value}>{name}</option>)}
      </select></label>
      <label>유저 키<input value={draft.user} onChange={e => setDraft({ ...draft, user: e.target.value })} placeholder="가명 식별자" /></label>
      <label>세션<input value={draft.session} onChange={e => setDraft({ ...draft, session: e.target.value })} placeholder="세션 식별자" /></label>
      <label>요청 이름<input value={draft.operation} onChange={e => setDraft({ ...draft, operation: e.target.value })} placeholder="예: LoginRequest" /></label>
      <button type="submit">조회</button>
      <button type="button" className="secondary" onClick={() => setRefreshKey(k => k + 1)}>새로고침</button>
    </form>
    {error && <div className="telemetry-error" role="alert">{error}</div>}
    <div className="trace-table-scroll"><table className="trace-table">
      <thead><tr><th>시각</th><th>요청 / 서버</th><th>결과</th><th>소요 시간</th><th>유저</th><th>Trace</th></tr></thead>
      <tbody>{page.items.map(span => <tr key={`${span.traceId}-${span.spanId}`} className={selected === span.traceId ? "selected" : ""}>
        <td>{time(span.startTime)}</td>
        <td><button className="trace-link" disabled={busy} onClick={() => setSelected(span.traceId)}>{span.name}</button><small>{span.serviceName} · {span.category}</small></td>
        <td><Outcome value={span.outcome} /></td><td className="numeric">{ms(span.durationMs)}</td>
        <td><button className="trace-link mono" title={span.userKey ?? "로그인 전 또는 미연결"} disabled={!span.userKey} onClick={() => followUser(span.userKey)}>{short(span.userKey)}</button></td>
        <td><button className="trace-link mono" disabled={busy} onClick={() => setSelected(span.traceId)}>{short(span.traceId)}</button></td>
      </tr>)}</tbody>
    </table></div>
    {page.items.length === 0 && <div className="trace-empty"><strong>조건에 맞는 요청 추적이 없습니다.</strong><p>계측된 서버를 실행하거나 조회 조건을 변경하세요. 기존 메트릭에는 요청 추적 정보가 없습니다.</p></div>}
    <div className="trace-pagination"><span>{page.items.length ? skip + 1 : 0}–{skip + page.items.length} · 최신 요청부터 표시</span>
      <button disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - 30))}>이전</button>
      <button disabled={!page.hasMore || skip >= 9960} onClick={() => setSkip(skip + 30)}>다음</button></div>

    {selected && <div className="trace-detail">
      <div className="trace-detail-heading"><h3>요청 상세</h3><button disabled={busy} onClick={() => setSelected(null)}>닫기</button></div>
      {detailError && <div className="telemetry-error" role="alert">{detailError}</div>}
      {detail?.traceId !== selected && !detailError && <p>요청 정보를 불러오는 중입니다…</p>}
      {detail?.traceId === selected && <>
        <div className="trace-facts">
          <div><small>요청</small><strong>{detail.root?.name ?? "루트 미수집"}</strong></div>
          <div><small>처리 결과</small><Outcome value={detail.root?.outcome ?? "unknown"} /></div>
          <div><small>총 소요 시간</small><strong>{ms(detail.root?.durationMs)}</strong></div>
          <div><small>수집한 전체 구간</small><strong>{detail.spans.length}</strong></div>
        </div>
        <dl className="trace-identifiers">
          <dt>Trace</dt><dd>{detail.traceId}</dd><dt>Run</dt><dd>{detail.root?.runId ?? "—"}</dd>
          <dt>유저 / 세션</dt><dd>{detail.root?.userKey ?? "로그인 전 또는 미연결"} / {detail.root?.sessionId ?? "—"}</dd>
          <dt>결과 코드</dt><dd>{detail.root?.resultCode ?? (detail.root?.httpStatusCode ? `HTTP ${detail.root.httpStatusCode}` : "—")}</dd><dt>코드 버전</dt><dd>{detail.root?.codeVersion ?? "미수집"}</dd>
        </dl>
        {detail.root?.linkedTraceId && <p className="telemetry-note">이 작업을 요청한 추적: <button className="trace-link" disabled={busy} onClick={() => setSelected(detail.root.linkedTraceId)}>{detail.root.linkedTraceId}</button> · 별도 비동기 작업입니다.</p>}
        {detail.spans.filter(s => s.isEntry).map(root => <DetailCoverage key={root.spanId} root={root} spans={detail.spans} />)}
        {(detail.truncated || detail.missingParents > 0) && <div className="telemetry-warning">추적이 일부만 표시됩니다. {detail.truncated ? "조회 상한에 도달했습니다. " : ""}미수집 부모 구간: {detail.missingParents}개. 수집 지연이나 유실 여부를 확인하세요.</div>}
        <Waterfall spans={detail.spans} />
        <div className="trace-logs"><h4>연결된 오류 로그 <span>{detail.logs.length}</span></h4>
          {detail.logs.length === 0 ? <p className="muted">이 요청에 수집된 오류 로그가 없습니다. 업무상 거절은 처리 결과에서 확인하세요.</p> : detail.logs.map(log =>
            <details key={log.id}><summary>{time(log.timestamp)} · {log.errorType ?? log.level}</summary>
              <p>{log.message}</p><small>Span {log.spanId}</small>{log.stackTrace && <pre>{log.stackTrace}</pre>}</details>)}
        </div>
        <div className="trace-ai">
          <div className="trace-detail-heading"><div><h4>근거 기반 LLM 조사</h4><p>현재 추적을 스냅샷으로 보존합니다. 분석 결과는 원인 후보이며 검증이 필요합니다.</p></div>
            <button className="primary" disabled={busy} onClick={investigate}>{busy ? "로컬 모델 분석 중…" : "이 요청 분석"}</button></div>
          {investigations.length === 0 && <p className="muted">아직 분석 기록이 없습니다. LM Studio에서 모델과 로컬 서버를 실행한 뒤 분석할 수 있습니다.</p>}
          {investigations.map(item => <article className="investigation" key={item.id}>
            <div><span className={`outcome ${item.status === "failed" ? "error" : "success"}`}>{item.status === "failed" ? "분석 실패" : "분석 완료"}</span>
              <small>{time(item.createdAt)} · {item.model} · {(item.durationMs / 1000).toFixed(1)}초</small></div>
            <pre>{item.answer}</pre><details><summary>사용한 관측 근거 · {item.promptVersion}</summary><pre>{item.evidenceJson}</pre></details>
          </article>)}
        </div>
      </>}
    </div>}
  </section>;
}

function Outcome({ value }) { return <span className={`outcome ${value}`}>{outcomeNames[value] ?? value}</span>; }

function DetailCoverage({ root, spans }) {
  if (!root?.detailStatus) return <p className="muted">기존 기록에는 상세 추적의 선택 수집 여부가 기록되지 않았습니다.</p>;
  const expected = root.expectedDetailSpans ?? 0;
  const byId = new Map(spans.map(s => [s.spanId, s]));
  const collected = spans.filter(span => {
    if (span.isEntry || span.instanceId !== root.instanceId) return false;
    const visited = new Set();
    for (let current = span; current?.parentSpanId && !visited.has(current.spanId); current = byId.get(current.parentSpanId)) {
      visited.add(current.spanId);
      if (current.parentSpanId === root.spanId) return true;
      if (current !== span && current.isEntry) return false;
    }
    return false;
  }).length;
  const labels = { pending: "기본 요청 기록 보존 · 늦게 도착하는 상세 구간 수집 중", sampled_out: "기본 요청 기록만 보존 · 정상 상세 추적 표본에서 제외", retained: "선택한 상세 추적 보존",
    truncated: "상세 구간 상한 적용 · 일부 구간 생략", buffer_limit: "동시 추적 버퍼 상한 · 기본 요청 기록만 보존",
    queue_dropped: "상세 전송 큐 유실 · 기본 요청 기록은 보존" };
  const reason = { error: "오류", slow: "느린 요청", sample: "정상 표본", none: "표본 제외" }[root.samplingReason];
  const incomplete = ["truncated", "buffer_limit", "queue_dropped"].includes(root.detailStatus) || collected < expected;
  return <div className={incomplete ? "telemetry-warning" : "telemetry-note"}>
    <strong>{root.serviceName} · {labels[root.detailStatus] ?? root.detailStatus}</strong>{reason ? ` · ${reason}` : ""}
    {root.detailStatus !== "sampled_out" && <p>내부 구간 {collected}/{expected}개 · 상한으로 생략한 구간 {root.limitedDetailSpans ?? 0}개</p>}
    {collected < expected && <p>상세 전송 대기 또는 유실 상태입니다. 5초마다 다시 확인합니다.</p>}
    {root.detailStatus === "sampled_out" && <p>요청 시간·처리 결과·유저·세션과 통계는 남아 있습니다. 내부 호출 전체를 수집했다는 의미는 아닙니다.</p>}
  </div>;
}

function RuntimeCard({ sample }) {
  const stale = sample.stale;
  return <div className={`runtime-card ${stale ? "stale" : ""}`}>
    <div className="runtime-title"><strong>{sample.serviceName}</strong><span>{stale ? "수신 지연" : "수집 중"} · {time(sample.timestamp)}</span></div>
    <div className="runtime-values"><div><small>CPU</small><b>{sample.cpuPercent.toFixed(1)}%</b></div>
      <div><small>메모리 / GC 힙</small><b>{sample.workingSetMb.toFixed(0)} / {sample.gcHeapMb.toFixed(0)} MB</b></div>
      <div><small>접속 세션</small><b>{sample.activeSessions.toLocaleString()}</b></div>
      <div><small>ThreadPool 대기</small><b>{sample.threadPoolQueue.toLocaleString()}</b></div></div>
    <div className={`collector-health ${sample.dropped > 0 || sample.failedBatches > 0 ? "degraded" : ""}`}>
      전송 대기 {sample.queueDepth.toLocaleString()} · 전송 완료 {sample.sent.toLocaleString()} · 유실 {sample.dropped.toLocaleString()} · 실패 시도 {sample.failedBatches.toLocaleString()}
    </div>
    <div className={`collector-health ${sample.requestsDropped > 0 ? "degraded" : ""}`}>
      기본 요청·작업: 관측 {(sample.requestsObserved ?? 0).toLocaleString()} · 전송 {(sample.requestsSent ?? 0).toLocaleString()} · 대기 {(sample.requestQueueDepth ?? 0).toLocaleString()} · 유실 {(sample.requestsDropped ?? 0).toLocaleString()}
    </div>
    <div className="collector-health">
      정상 상세 표본 {((sample.detailSampleRate ?? 0) * 100).toFixed(1)}% · 표본 제외 {(sample.sampledOutTraces ?? 0).toLocaleString()}건 · 상한 적용 {(sample.limitedTraces ?? 0).toLocaleString()}건 · 상세 유실 {(sample.detailsDropped ?? 0).toLocaleString()}개
    </div><small className="runtime-instance">인스턴스 {short(sample.instanceId)} · 누적 수치는 해당 프로세스 기준입니다. 구 버전에는 항목별 카운터가 없습니다.</small>
  </div>;
}

function preciseTime(value) {
  // Date truncates .NET's sub-millisecond timestamp, distorting short-span offsets.
  const fractional = value.match(/\.(\d+)/)?.[1] ?? "";
  return Date.parse(value) + (fractional.length > 3 ? Number(`0.${fractional.slice(3)}`) : 0);
}

function Waterfall({ spans }) {
  const rows = useMemo(() => {
    const children = new Map();
    const ids = new Set(spans.map(s => s.spanId));
    const roots = [];
    for (const span of spans) {
      if (!span.parentSpanId || !ids.has(span.parentSpanId)) roots.push(span);
      else { if (!children.has(span.parentSpanId)) children.set(span.parentSpanId, []); children.get(span.parentSpanId).push(span); }
    }
    const result = [], visited = new Set();
    const visit = (span, depth) => { if (visited.has(span.spanId)) return; visited.add(span.spanId); result.push({ ...span, depth }); (children.get(span.spanId) ?? []).forEach(child => visit(child, Math.min(depth + 1, 12))); };
    roots.forEach(span => visit(span, 0));
    spans.filter(span => !visited.has(span.spanId)).forEach(span => visit(span, 0));
    return result;
  }, [spans]);
  const start = Math.min(...spans.map(s => preciseTime(s.startTime)));
  const end = Math.max(...spans.map(s => preciseTime(s.startTime) + s.durationMs));
  const duration = Math.max(end - start, 0.01);
  return <div className="waterfall"><div className="waterfall-title"><h4>실행 타임라인</h4><span>0 → {ms(duration)} · 겹친 구간은 동시에 실행될 수 있습니다.</span></div>
    {rows.map(span => <details key={span.spanId} className="waterfall-row">
      <summary><span className="span-name" style={{ paddingLeft: `${span.depth * 12}px` }} title={span.name}>{span.name}</span>
        <span className="span-track"><span className={`span-bar category-${span.category} ${span.outcome === "error" ? "span-error" : ""}`}
          style={{ left: `${Math.max(0, (preciseTime(span.startTime) - start) / duration * 100)}%`, width: `${Math.min(100, Math.max(0.3, span.durationMs / duration * 100))}%` }} /></span>
        <span className="span-duration">{ms(span.durationMs)}</span></summary>
      <div className="span-inspect"><b>{span.category} · {span.serviceName}</b><code>Span {span.spanId} / Parent {span.parentSpanId ?? "없음"}</code>
        <span>결과: {outcomeNames[span.outcome] ?? span.outcome} {span.errorType ?? ""}</span></div>
    </details>)}
    <div className="waterfall-legend"><span>● 요청</span><span>● 핸들러</span><span>● DB / Redis</span><span>● Grain</span><span>● 응답 전송</span></div>
  </div>;
}
