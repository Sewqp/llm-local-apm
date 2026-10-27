"""Contract checks against an isolated collector. Never reset a database."""
import copy, datetime, json, sys, urllib.request, urllib.error, uuid

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:5229'
results = []
def call(path, data=None, headers=None):
    req = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(),
        headers={'Content-Type': 'application/json', **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()
def check(condition, name):
    assert condition, name
    results.append({'test': name, 'passed': True})

run = uuid.uuid4().hex
service = 'ContractChecks-' + run[:8]
now = datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00', 'Z')
spans, metrics = [], []
for index in range(1, 101):
    trace, span = uuid.uuid4().hex, uuid.uuid4().hex[:16]
    outcome = 'error' if index == 100 else 'rejected' if index == 99 else 'success'
    spans.append(dict(traceId=trace, spanId=span, parentSpanId=None, runId=run, serviceName=service,
        instanceId=run, name='Synthetic.Percentiles', category='packet', startTime=now, durationMs=index,
        outcome=outcome, userKey='test-pseudonym', sessionId=run, isEntry=True, codeVersion='fixture-v1'))
    metrics.append(dict(timestamp=now, serverName=service, method='PACKET', path='/packet/Synthetic.Percentiles',
        statusCode=500 if outcome=='error' else 400 if outcome=='rejected' else 200,
        durationMs=index, traceId=trace, runId=run, outcome=outcome, userKey='test-pseudonym', sessionId=run))
batch = dict(batchId=uuid.uuid4().hex, spans=spans, metrics=metrics, logs=[], runtime=[])
check(call('/api/telemetry/batch', batch)[0] == 202, 'batch accepted')
check(call('/api/telemetry/batch', batch)[1].get('duplicate') is True, 'retry deduplicated')
summary = call('/api/metrics/summary?server='+service)[1][0]
check(summary['count'] == 100, 'duplicate batch does not duplicate metrics')
check([summary['p50DurationMs'],summary['p95DurationMs'],summary['p99DurationMs']] == [50,95,99], 'exact nearest-rank percentiles')
check(summary['errorRate'] == .01 and summary['rejectedRate'] == .01, 'business rejection separated from server error')
first = call('/api/traces?server='+service+'&limit=30')[1]
second = call('/api/traces?server='+service+'&limit=30&skip=30')[1]
check(first['hasMore'] and len(first['items']) == 30 and not
    ({s['spanId'] for s in first['items']} & {s['spanId'] for s in second['items']}), 'stable pagination for equal timestamps')
check(len(call('/api/traces?server='+service+'&outcome=rejected')[1]['items']) == 1, 'outcome filter')
check(len(call('/api/traces?server='+service+'&user=missing')[1]['items']) == 0, 'user filter isolates requests')
detail = call('/api/traces/'+spans[0]['traceId'])[1]
check(detail['root']['startTime'].endswith('Z'), 'UTC timestamps preserved')
check(detail['root']['runId'] == run and detail['root']['sessionId'] == run, 'run and session evidence retained')
check(call('/api/traces?minutes=999999')[0] == 400, 'query window bounded')
check(call('/api/metrics/reset', {})[0] == 403, 'destructive reset disabled')
check(call('/api/telemetry/batch', batch, {'Origin':'https://untrusted.example'})[0] == 403, 'cross-origin ingestion denied')
bad = copy.deepcopy(batch); bad['batchId'] = uuid.uuid4().hex; bad['spans'][0]['durationMs'] = -1
check(call('/api/telemetry/batch', bad)[0] == 400, 'negative duration rejected atomically')
bad = copy.deepcopy(batch); bad['batchId'] = uuid.uuid4().hex; bad['spans'] *= 3
check(call('/api/telemetry/batch', bad)[0] == 400, 'oversized batch rejected')
check(sum(p['requestCount'] for p in call('/api/metrics/timeseries?server='+service)[1]) == 100, 'server-side chart aggregation')
# The independent core/detail senders can arrive in either order.
for final_first in (False, True):
    pending = copy.deepcopy(spans[0]); pending.update(traceId=uuid.uuid4().hex, spanId=uuid.uuid4().hex[:16], detailStatus='pending')
    final = copy.deepcopy(pending); final.update(detailStatus='retained', samplingReason='sample', expectedDetailSpans=1)
    child = copy.deepcopy(final); child.update(spanId=uuid.uuid4().hex[:16], parentSpanId=final['spanId'], isEntry=False, category='redis', detailStatus=None, expectedDetailSpans=0)
    metric = copy.deepcopy(metrics[0]); metric['traceId'] = pending['traceId']
    core_batch = dict(batchId=uuid.uuid4().hex, spans=[pending], metrics=[metric], logs=[], runtime=[])
    detail_batch = dict(batchId=uuid.uuid4().hex, spans=[final,child], metrics=[], logs=[], runtime=[])
    for part in ([detail_batch,core_batch] if final_first else [core_batch,detail_batch]):
        check(call('/api/telemetry/batch',part)[0] == 202, 'metadata batch accepted: '+str(final_first))
    check(call('/api/telemetry/batch',detail_batch)[1].get('duplicate') is True, 'detail retry deduplicated: '+str(final_first))
    resolved = call('/api/traces/'+pending['traceId'])[1]
    check(resolved['root']['detailStatus']=='retained' and resolved['root']['expectedDetailSpans']==1 and len(resolved['spans'])==2,
          'final detail metadata survives sender ordering: '+str(final_first))
check(call('/api/metrics/summary?server='+service)[1][0]['count']==102, 'detail finalization never duplicates request statistics')
for field,value in [('httpStatusCode',600),('linkedTraceId','0'*32),('requestMethod','X'*21),('route','/'+('x'*250))]:
    invalid = copy.deepcopy(batch); invalid['batchId'] = uuid.uuid4().hex
    invalid['spans'][0][field] = value
    check(call('/api/telemetry/batch',invalid)[0]==400,'invalid architecture metadata rejected: '+field)
invalid = copy.deepcopy(batch); invalid['batchId']=uuid.uuid4().hex
invalid['spans'][0]['linkedTraceId']=uuid.uuid4().hex
check(call('/api/telemetry/batch',invalid)[0]==400,'partial causal link rejected')
print(json.dumps({'service':service,'runId':run,'results':results}, indent=2))
