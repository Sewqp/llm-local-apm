import { lazy, Suspense, useCallback, useEffect, useState } from "react";
import "./App.css";
import { api } from "./api";
import SummaryCards from "./components/SummaryCards";
import AnalysisPanel from "./components/AnalysisPanel";
import ServerSelector from "./components/ServerSelector";
import ServerManagerPanel from "./components/ServerManagerPanel";
import TraceExplorer from "./components/TraceExplorer";

const WINDOW_MINUTES = 15;
const POLL_INTERVAL_MS = 5000;
const ResponseTimeChart = lazy(() => import("./components/ResponseTimeChart"));
const ErrorRateChart = lazy(() => import("./components/ErrorRateChart"));

function App() {
  const [chartData, setChartData] = useState([]);
  const [summary, setSummary] = useState([]);
  const [analyses, setAnalyses] = useState([]);
  const [running, setRunning] = useState(null); // null | "quick" | "full" — 어느 모드가 도는 중인지
  const [error, setError] = useState(null);
  const [serverNames, setServerNames] = useState([]);
  const [selectedServer, setSelectedServer] = useState(null);

  const refresh = useCallback(async () => {
    try {
      const [metrics, summaryData, names] = await Promise.all([
        api.getTimeseries(WINDOW_MINUTES, selectedServer),
        api.getSummary(WINDOW_MINUTES, selectedServer),
        api.getServerNames(WINDOW_MINUTES),
      ]);
      setChartData(metrics.map(m => ({ ...m, label: new Date(m.time).toLocaleTimeString(), errorRate: m.errorRate * 100 })));
      setSummary(summaryData);
      setServerNames(names);
      setError(null);
    } catch (e) {
      setError(e.message);
    }
  }, [selectedServer]);

  const loadAnalysisHistory = useCallback(async () => {
    try {
      const history = await api.getAnalysisHistory(20, selectedServer);
      setAnalyses(history);
    } catch {
      // 수집 서버가 아직 안 떠 있을 수 있음, 다음 폴링에서 재시도
    }
  }, [selectedServer]);

  useEffect(() => {
    refresh();
    loadAnalysisHistory();
    const id = setInterval(refresh, POLL_INTERVAL_MS);
    return () => clearInterval(id);
  }, [refresh, loadAnalysisHistory]);

  const handleRunAnalysis = async (mode) => {
    setRunning(mode);
    try {
      await api.runAnalysis(WINDOW_MINUTES, selectedServer, mode);
      await loadAnalysisHistory();
    } catch (e) {
      setError(e.message);
    } finally {
      setRunning(null);
    }
  };


  return (
    <div className="app">
      <header className="app-header">
        <div className="header-row">
          <div>
            <h1>LLM 기반 로컬 APM 대시보드</h1>
            <p className="subtitle">최근 {WINDOW_MINUTES}분 · 5초마다 자동 갱신 · LLM 분석은 로컬(LM Studio)에서 수행</p>
          </div>
          <div className="header-actions">
            <ServerSelector serverNames={serverNames} selected={selectedServer} onChange={setSelectedServer} />

          </div>
        </div>
      </header>

      {error && <div className="error-banner">데이터를 불러오지 못했습니다: {error} (수집 서버가 실행 중인지 확인하세요)</div>}

      <ServerManagerPanel />

      <SummaryCards summary={summary} />
      <TraceExplorer key={selectedServer ?? "all"} server={selectedServer} />

      <Suspense fallback={<div className="panel">차트를 불러오는 중입니다…</div>}><div className="chart-grid">
        <ResponseTimeChart data={chartData} />
        <ErrorRateChart data={chartData} />
      </div></Suspense>

      <AnalysisPanel analyses={analyses} onRun={handleRunAnalysis} running={running !== null} runningMode={running} />
    </div>
  );
}

export default App;
