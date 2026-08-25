// 원본 메트릭(요청 단위)을 일정 간격(bucketMs)으로 묶어 차트용 시계열로 변환
export function bucketMetrics(metrics, bucketMs = 5000) {
  const buckets = new Map();

  for (const m of metrics) {
    const t = new Date(m.timestamp).getTime();
    const bucketStart = Math.floor(t / bucketMs) * bucketMs;
    if (!buckets.has(bucketStart)) {
      buckets.set(bucketStart, { time: bucketStart, totalDuration: 0, count: 0, errorCount: 0 });
    }
    const b = buckets.get(bucketStart);
    b.totalDuration += m.durationMs;
    b.count += 1;
    if (m.statusCode >= 400) b.errorCount += 1;
  }

  return Array.from(buckets.values())
    .sort((a, b) => a.time - b.time)
    .map((b) => ({
      time: b.time,
      label: new Date(b.time).toLocaleTimeString(),
      avgDurationMs: Math.round((b.totalDuration / b.count) * 10) / 10,
      requestCount: b.count,
      errorRate: Math.round((b.errorCount / b.count) * 1000) / 10,
    }));
}
