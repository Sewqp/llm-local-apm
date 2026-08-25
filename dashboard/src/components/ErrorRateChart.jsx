import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";

export default function ErrorRateChart({ data }) {
  return (
    <div className="panel">
      <h3>에러율 추이 (%)</h3>
      <ResponsiveContainer width="100%" height={260}>
        <BarChart data={data}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--grid-color)" />
          <XAxis dataKey="label" tick={{ fontSize: 11 }} minTickGap={30} />
          <YAxis tick={{ fontSize: 11 }} />
          <Tooltip
            contentStyle={{ background: "var(--tooltip-bg)", border: "1px solid var(--border-color)" }}
            formatter={(value) => [`${value} %`, "에러율"]}
          />
          <Bar dataKey="errorRate" fill="#f87171" radius={[3, 3, 0, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}
