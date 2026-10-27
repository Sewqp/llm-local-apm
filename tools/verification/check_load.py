"""A small real TCP load/collection check; not a replacement for a 30k concurrency test."""
import asyncio, json, math, sys, time, urllib.parse, urllib.request

port=int(sys.argv[1]) if len(sys.argv)>1 else 9129
base=sys.argv[2] if len(sys.argv)>2 else 'http://127.0.0.1:5229'
service='Trickcal-APM-Verification'
concurrency, per_client=16,50
def get(path):
    with urllib.request.urlopen(base+path,timeout=20) as response: return json.load(response)
def count():
    rows=get('/api/metrics/summary?server='+service)
    return next((r['count'] for r in rows if r['path']=='/packet/Heartbeat'),0)
async def client():
    reader,writer=await asyncio.open_connection('127.0.0.1',port)
    times=[]
    for _ in range(per_client):
        start=time.perf_counter();writer.write(b'\x04\x00\x28\x23');await writer.drain()
        response=await reader.readexactly(4)
        assert response==b'\x04\x00\x28\x23'
        times.append((time.perf_counter()-start)*1000)
        await asyncio.sleep(.002)
    writer.close();await writer.wait_closed()
    return times
async def run(): return await asyncio.gather(*(client() for _ in range(concurrency)))
before=count();started=time.perf_counter();groups=asyncio.run(run());duration=time.perf_counter()-started
latencies=sorted(value for group in groups for value in group)
for _ in range(60):
    observed=count()-before
    if observed>=len(latencies): break
    time.sleep(.5)
assert observed==len(latencies),(observed,len(latencies))
def percentile(p): return latencies[math.ceil(len(latencies)*p)-1]
print(json.dumps(dict(scenario='real TCP heartbeat load, local collector enabled',passed=True,
    connections=concurrency,acknowledgedRequests=len(latencies),collectedRequestMetrics=observed,
    wallSeconds=duration,clientRequestsPerSecond=len(latencies)/duration,
    clientRttMs=dict(p50=percentile(.5),p95=percentile(.95),p99=percentile(.99)),
    note='Single local run including 2ms pacing. Not an A/B overhead measurement or a 30,000-user validation.'),indent=2))
