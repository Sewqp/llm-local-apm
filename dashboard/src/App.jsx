import { useCallback, useEffect, useState } from "react";
import "./App.css";
import { api } from "./api";
import SummaryCards from "./components/SummaryCards";
import ResponseTimeChart from "./components/ResponseTimeChart";
import ErrorRateChart from "./components/ErrorRateChart";
import AnalysisPanel from "./components/AnalysisPanel";
import ServerSelector from "./components/ServerSelector";
import ServerManagerPanel from "./components/ServerManagerPanel";
import { bucketMetrics } from "./utils/bucket";

const WINDOW_MINUTES = 15;
const POLL_INTERVAL_MS = 5000;

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
        api.getMetrics(WINDOW_MINUTES, selectedServer),
        api.getSummary(WINDOW_MINUTES, selectedServer),
        api.getServerNames(WINDOW_MINUTES),
      ]);
      setChartData(bucketMetrics(metrics));
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

  const handleReset = async () => {
    if (!window.confirm("메트릭·분석 이력을 전부 지웁니다(서버 등록 목록은 그대로 유지). 계속할까요?")) return;
    try {
      await api.resetData();
      await Promise.all([refresh(), loadAnalysisHistory()]);
    } catch (e) {
      setError(e.message);
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
            <button className="reset-button" onClick={handleReset}>초기화</button>
          </div>
        </div>
      </header>

      {error && <div className="error-banner">데이터를 불러오지 못했습니다: {error} (수집 서버가 실행 중인지 확인하세요)</div>}

      <ServerManagerPanel />

      <SummaryCards summary={summary} />

      <div className="chart-grid">
        <ResponseTimeChart data={chartData} />
        <ErrorRateChart data={chartData} />
      </div>

      <AnalysisPanel analyses={analyses} onRun={handleRunAnalysis} running={running !== null} runningMode={running} />
    </div>
  );
}

export default App;
