import { useEffect, useState } from "react";
import { api } from "../api";

const EMPTY_FORM = { name: "", workingDirectory: "", command: "dotnet", arguments: "run" };

export default function ServerManagerPanel() {
  const [servers, setServers] = useState([]);
  const [busyName, setBusyName] = useState(null);
  const [error, setError] = useState(null);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState(EMPTY_FORM);
  const [registering, setRegistering] = useState(false);

  const refresh = async () => {
    try {
      setServers(await api.getManagedServers());
      setError(null);
    } catch {
      // CollectionServer 재시작 중일 수 있음, 다음 폴링에서 재시도
    }
  };

  useEffect(() => {
    refresh();
    const id = setInterval(refresh, 3000);
    return () => clearInterval(id);
  }, []);

  const handleToggle = async (server) => {
    setBusyName(server.name);
    setError(null);
    try {
      if (server.isRunning) {
        await api.stopManagedServer(server.name);
      } else {
        await api.startManagedServer(server.name);
      }
      await refresh();
    } catch (e) {
      setError(e.message);
    } finally {
      setBusyName(null);
    }
  };

  const handleDelete = async (server) => {
    setBusyName(server.name);
    setError(null);
    try {
      await api.deleteManagedServer(server.name);
      await refresh();
    } catch (e) {
      setError(e.message);
    } finally {
      setBusyName(null);
    }
  };

  const handleRegister = async (e) => {
    e.preventDefault();
    setRegistering(true);
    setError(null);
    try {
      await api.createManagedServer({
        name: form.name,
        workingDirectory: form.workingDirectory,
        command: form.command,
        arguments: form.arguments,
      });
      setForm(EMPTY_FORM);
      setShowForm(false);
      await refresh();
    } catch (e) {
      setError(e.message);
    } finally {
      setRegistering(false);
    }
  };

  return (
    <div className="panel">
      <div className="panel-header">
        <h3>서버 관리</h3>
        <button onClick={() => setShowForm((v) => !v)}>{showForm ? "취소" : "+ 서버 등록"}</button>
      </div>

      {error && <div className="error-banner">{error}</div>}

      {showForm && (
        <form className="server-form" onSubmit={handleRegister}>
          <input
            placeholder="이름 (예: My Server)"
            value={form.name}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
            required
          />
          <input
            placeholder="작업 디렉터리 (예: C:\path\to\server)"
            value={form.workingDirectory}
            onChange={(e) => setForm({ ...form, workingDirectory: e.target.value })}
            required
          />
          <input
            placeholder="실행 명령 (예: dotnet)"
            value={form.command}
            onChange={(e) => setForm({ ...form, command: e.target.value })}
            required
          />
          <input
            placeholder="인자 (예: run --urls http://localhost:5082)"
            value={form.arguments}
            onChange={(e) => setForm({ ...form, arguments: e.target.value })}
          />
          <button type="submit" disabled={registering}>
            {registering ? "등록 중..." : "등록"}
          </button>
        </form>
      )}

      {servers.length === 0 && !showForm && (
        <div className="empty-state">등록된 서버가 없습니다. "+ 서버 등록"으로 추가하세요.</div>
      )}

      <ul className="server-list">
        {servers.map((s) => (
          <li key={s.name} className="server-item">
            <span className={`status-dot ${s.isRunning ? "status-on" : "status-off"}`} />
            <span className="server-name">{s.name}</span>
            {!s.isBuiltIn && <span className="badge badge-custom">등록됨</span>}
            {s.isRunning && s.processId && <span className="server-pid">pid {s.processId}</span>}
            <button
              className={s.isRunning ? "btn-stop" : "btn-start"}
              disabled={busyName === s.name}
              onClick={() => handleToggle(s)}
            >
              {busyName === s.name ? "처리 중..." : s.isRunning ? "중지" : "시작"}
            </button>
            {!s.isBuiltIn && (
              <button
                className="btn-delete"
                disabled={busyName === s.name || s.isRunning}
                title={s.isRunning ? "실행 중에는 삭제할 수 없습니다" : "삭제"}
                onClick={() => handleDelete(s)}
              >
                삭제
              </button>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
