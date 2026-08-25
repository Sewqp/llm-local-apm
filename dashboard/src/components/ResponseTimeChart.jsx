import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";

export default function ResponseTimeChart({ data }) {
  return (
    <div className="panel">
      <h3>응답시간 추이 (평균, ms)</h3>
      <ResponsiveContainer width="100%" height={260}>
        <LineChart data={data}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--grid-color)" />
          <XAxis dataKey="label" tick={{ fontSize: 11 }} minTickGap={30} />
          <YAxis tick={{ fontSize: 11 }} />
          <Tooltip
            contentStyle={{ background: "var(--tooltip-bg)", border: "1px solid var(--border-color)" }}
            formatter={(value) => [`${value} ms`, "평균 응답시간"]}
          />
          <Line type="monotone" dataKey="avgDurationMs" stroke="#6366f1" strokeWidth={2} dot={false} />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
