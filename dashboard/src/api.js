// 개발 모드(vite dev server, 별도 포트)에서는 CollectionServer를 명시적으로 가리키고,
// 빌드 후 CollectionServer의 정적 파일로 서빙될 때는 같은 오리진이므로 상대경로(빈 문자열)를 쓴다.
const BASE_URL = import.meta.env.VITE_COLLECTION_SERVER_URL
  ?? (import.meta.env.DEV ? "http://localhost:5219" : "");

function withServer(params, server) {
  if (server) params.set("server", server);
  return params.toString();
}

async function getJson(path) {
  const res = await fetch(`${BASE_URL}${path}`);
  if (!res.ok) throw new Error(`GET ${path} failed: ${res.status}`);
  return res.json();
}

async function postJson(path) {
  const res = await fetch(`${BASE_URL}${path}`, { method: "POST" });
  if (!res.ok) throw new Error(`POST ${path} failed: ${res.status}`);
  return res.json();
}

// 시작/중지처럼 본문 없는 응답용 — 실패 시 서버가 내려준 에러 메시지를 함께 던진다
async function postAction(path) {
  const res = await fetch(`${BASE_URL}${path}`, { method: "POST" });
  if (!res.ok) {
    const body = await res.json().catch(() => null);
    throw new Error(body?.error || `POST ${path} failed: ${res.status}`);
  }
}

// 서버 등록/삭제처럼 본문이 있거나 메서드가 다른 액션용 — 실패 시 에러 메시지를 함께 던진다
async function sendAction(path, method, body) {
  const res = await fetch(`${BASE_URL}${path}`, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    const errBody = await res.json().catch(() => null);
    throw new Error(errBody?.error || `${method} ${path} failed: ${res.status}`);
  }
}

export const api = {
  getMetrics: (minutes = 30, server = null) =>
    getJson(`/api/metrics?${withServer(new URLSearchParams({ minutes }), server)}`),
  getSummary: (minutes = 30, server = null) =>
    getJson(`/api/metrics/summary?${withServer(new URLSearchParams({ minutes }), server)}`),
  getServerNames: (minutes = 30) => getJson(`/api/metrics/server-names?minutes=${minutes}`),
  getAnomalies: (minutes = 30, server = null) =>
    getJson(`/api/anomalies?${withServer(new URLSearchParams({ minutes }), server)}`),
  getAnalysisHistory: (limit = 20, server = null) =>
    getJson(`/api/analysis?${withServer(new URLSearchParams({ limit }), server)}`),
  runAnalysis: (minutes = 30, server = null, mode = "quick") =>
    postJson(`/api/analysis/run?${withServer(new URLSearchParams({ minutes, mode }), server)}`),
  resetData: () => postAction("/api/metrics/reset"),

  getManagedServers: () => getJson("/api/servers"),
  startManagedServer: (name) => postAction(`/api/servers/${encodeURIComponent(name)}/start`),
  stopManagedServer: (name) => postAction(`/api/servers/${encodeURIComponent(name)}/stop`),
  createManagedServer: (payload) => sendAction("/api/servers", "POST", payload),
  deleteManagedServer: (name) => sendAction(`/api/servers/${encodeURIComponent(name)}`, "DELETE"),
};
