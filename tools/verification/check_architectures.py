"""Evidence assertions against the synthetic HTTP / distributed / queued demo."""
import concurrent.futures, collections, datetime, json, pathlib, sqlite3, statistics, threading, time, urllib.request, urllib.error, uuid

EDGE, STOCK, COLLECTOR = 'http://127.0.0.1:5241', 'http://127.0.0.1:5242', 'http://127.0.0.1:5239'
SESSION = uuid.uuid4().hex
records, checks = [], []
lock = threading.Lock()

def call(base, path, method='GET', record=False):
    request = urllib.request.Request(base+path, data=b'{}' if method=='POST' else None, method=method,
        headers={'X-Demo-User':'701', 'X-Demo-Session':SESSION, 'Authorization':'Bearer SECRET_HEADER_DO_NOT_EXPORT',
                 'Content-Type':'application/json'})
    started = time.perf_counter()
    try: response = urllib.request.urlopen(request, timeout=20)
    except urllib.error.HTTPError as error: response = error
    with response:
        raw = response.read().decode()
        result = dict(path=path.split('?')[0], status=response.status,
            trace=response.headers.get('X-Demo-TraceId'), span=response.headers.get('X-Demo-SpanId'),
            elapsedMs=(time.perf_counter()-started)*1000, body=json.loads(raw) if raw else None)
    if record:
        with lock: records.append(result)
    return result

def check(condition, name):
    checks.append({'test':name, 'passed':bool(condition)})
    if not condition: raise AssertionError(name)

def detail(trace):
    return call(COLLECTOR, '/api/traces/'+trace)['body']

def verify(out, run):
    global records, checks
    records, checks = [], []
    try:
        result = exercise(out, run)
        (out/'results.json').write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding='utf-8')
        return result
    finally:
        (out/'client-results.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
        (out/'checks.json').write_text(json.dumps(checks, indent=2), encoding='utf-8')

def exercise(out, run):
    edge_health, stock_health = [call(base,'/health')['body'] for base in (EDGE, STOCK)]
    check(edge_health['pid'] != stock_health['pid'], 'HTTP dependency runs in a separate process')
    samples = {}
    # Warm-up is recorded and included in the count reconciliation.
    for path in ('sequential','parallel','remote'):
        call(EDGE, '/demo/'+path, record=True)
    for path in ('sequential','parallel'):
        samples[path] = [call(EDGE, '/demo/'+path, record=True) for _ in range(20)]
    check(all(r['body']==[100,2500,1] and r['status']==200 for rows in samples.values() for r in rows),
          'sequential and parallel paths return the same real SQLite results')
    special = {}
    for path,expected in [('slow',200),('error',500),('timeout',504),('rejected',409),('remote',200),('remote-error',500)]:
        special[path] = call(EDGE, '/demo/'+path+'?secret=SECRET_QUERY_DO_NOT_EXPORT', record=True)
        check(special[path]['status']==expected, 'HTTP fixture status '+path)
    # These are concurrent short HTTP requests, not long-lived logged-in game sessions.
    loads = {}
    for path in ('sequential','parallel','remote'):
        begin=time.perf_counter()
        with concurrent.futures.ThreadPoolExecutor(max_workers=32) as pool:
            rows = list(pool.map(lambda _:call(EDGE,'/demo/'+path,record=True), range(320)))
        loads[path] = {'requests':len(rows), 'concurrency':32, 'success':sum(r['status']==200 for r in rows),
            'elapsedSeconds':time.perf_counter()-begin,
            'clientP50Ms':statistics.median(r['elapsedMs'] for r in rows),
            'clientP95Ms':sorted(r['elapsedMs'] for r in rows)[int(.95*(len(rows)-1))]}
        check(loads[path]['success']==320, '320 requests complete at concurrency 32: '+path)
    # First queued job must fail, so the burst cannot accidentally exclude the failure case.
    job_requests = [call(EDGE,'/demo/jobs?fail=true',method='POST',record=True)]
    check(job_requests[0]['status']==202,'worker failure fixture admitted')
    with concurrent.futures.ThreadPoolExecutor(max_workers=32) as pool:
        job_requests += list(pool.map(lambda _:call(EDGE,'/demo/jobs',method='POST',record=True),range(64)))
    accepted = [r for r in job_requests if r['status']==202]
    rejected = [r for r in job_requests if r['status']==429]
    check(bool(accepted) and bool(rejected) and len(accepted)+len(rejected)==65,'bounded queue accepts work and returns HTTP 429 on saturation')
    jobs = []
    deadline = time.monotonic()+30
    while time.monotonic()<deadline:
        jobs = [call(EDGE,'/jobs/'+r['body']['id'])['body'] for r in accepted]
        if all(j['status'] in ('completed','failed') for j in jobs): break
        time.sleep(.2)
    check(all(j['status'] in ('completed','failed') for j in jobs), 'every admitted job reaches a terminal state')
    # Wait for request queue and delayed detail flush independently, then runtime publication.
    time.sleep(7)
    stats = {name:call(base,'/stats')['body'] for name,base in [('edge',EDGE),('stock',STOCK)]}
    (out/'stats.json').write_text(json.dumps(stats,indent=2),encoding='utf-8')
    expected = collections.Counter((r['path'],r['status']) for r in records)
    check(stats['edge']['requests']=={p+'|'+str(s):n for (p,s),n in expected.items()},'independent client results match edge route/status counters')
    for name,state in stats.items():
        h=state['agent']
        check(h['requestsDropped']==0 and h['detailsDropped']==0 and h['failedBatches']==0, name+' has no telemetry queue loss or failed HTTP batch')
        check(h['queueDepth']==0 and h['requestQueueDepth']==0, name+' telemetry queues drained')
        check(h['requestsObserved']==h['requestsSent'], name+' observed entries equal sent entries')
    with sqlite3.connect(out/'collector.db') as db:
        db.row_factory=sqlite3.Row
        spans=[dict(r) for r in db.execute('SELECT * FROM Spans WHERE RunId=?',(run,))]
        metrics=[dict(r) for r in db.execute('SELECT * FROM Metrics WHERE RunId=?',(run,))]
        logs=[dict(r) for r in db.execute('SELECT * FROM TelemetryLogs WHERE TraceId IN (SELECT TraceId FROM Spans WHERE RunId=?)',(run,))]
        with sqlite3.connect(out/'collector.snapshot.db') as backup: db.backup(backup)
    by_trace=collections.defaultdict(list)
    for span in spans: by_trace[span['TraceId']].append(span)
    entries=[s for s in spans if s['IsEntry']]
    check(len(entries)==sum(s['agent']['requestsObserved'] for s in stats.values())==len(metrics),'collector entries and metrics match both processes including jobs')
    actual=collections.Counter((m['Path'],m['StatusCode']) for m in metrics if m['ServerName']=='ArchitectureEdge' and m['Method']!='BACKGROUND')
    check(actual==expected,'every edge HTTP outcome matches stored metric route and status')
    check(all(m['Method'] in ('GET','POST','BACKGROUND') and not m['Path'].startswith('/packet/') for m in metrics),
          'HTTP metrics are not recorded as game packets')
    check(all(s['ParentSpanId'] is None or any(p['SpanId']==s['ParentSpanId'] for p in by_trace[s['TraceId']]) for s in spans),
          'all retained parent span references resolve')
    check(all(s['DetailStatus']=='retained' and s['ExpectedDetailSpans']==sum(not p['IsEntry'] and p['InstanceId']==s['InstanceId'] for p in by_trace[s['TraceId']]) for s in entries),
          'detail coverage is complete per service instance, not across the whole distributed trace')
    seq=detail(samples['sequential'][-1]['trace']); par=detail(samples['parallel'][-1]['trace'])
    def operations(d): return sorted([s for s in d['spans'] if s['name'].startswith('catalog.')],key=lambda s:s['startTime'])
    def stamp(s):
        # Python 3.10 accepts six fractional digits; .NET emits seven (100 ns precision).
        raw=s['startTime'].rstrip('Z')
        if '.' in raw: head,fraction=raw.split('.',1); raw=head+'.'+fraction[:6].ljust(6,'0')
        return datetime.datetime.fromisoformat(raw).replace(tzinfo=datetime.timezone.utc).timestamp()
    sq,pq=operations(seq),operations(par)
    check(len(sq)==3 and all(stamp(sq[i])+sq[i]['durationMs']/1000<=stamp(sq[i+1])+.00001 for i in range(2)),
          'sequential dependency spans do not overlap')
    check(len(pq)==3 and max(map(stamp,pq))<min(stamp(s)+s['durationMs']/1000 for s in pq),
          'parallel dependency spans overlap in the measured trace')
    check(all(any(s['name']=='SQLite.catalog.select' for s in d['spans']) for d in (seq,par)),
          'real SQLite work is separately recorded from simulated wait')
    distributed=detail(special['remote']['trace'])
    roots=[s for s in distributed['spans'] if s['isEntry']]
    check(len(roots)==2 and len({s['serviceName'] for s in roots})==2 and distributed['missingParents']==0,
          'distributed HTTP trace joins two services without missing parents')
    stock_root=next(s for s in roots if s['serviceName']=='ArchitectureStock')
    client_span=next(s for s in distributed['spans'] if s['name']=='http.stock')
    check(stock_root['parentSpanId']==client_span['spanId'],'downstream entry is a child of the recorded HTTP client span')
    check(len({s['userKey'] for s in roots})==1 and len({s['sessionId'] for s in roots})==1,
          'pseudonymous user and session correlate across processes')
    bad=detail(special['remote-error']['trace'])
    check(len([s for s in bad['spans'] if s['isEntry'] and s['outcome']=='error'])==2 and len(bad['logs'])==1,
          'downstream failure correlates with upstream HTTP failure')
    for name,status,outcome in [('error',500,'error'),('timeout',504,'timeout'),('rejected',409,'rejected')]:
        d=detail(special[name]['trace'])
        check(d['root']['httpStatusCode']==status and d['root']['outcome']==outcome,'trace keeps status and outcome for '+name)
    queue_details=[]
    for request,job in zip(accepted,jobs):
        d=detail(job['traceId']); root=d['root']; queue_details.append(d)
        check(root['linkedTraceId']==request['trace'] and root['linkedSpanId']==request['span']
              and root['traceId']!=request['trace'] and root['parentSpanId'] is None,'queued job retains a separate trace with the exact enqueue cause')
        check(root['userKey']==roots[0]['userKey'] and root['sessionId']==SESSION,'queued job retains user and session')
    failed=next(d for d in queue_details if d['root']['outcome']=='error')
    original=detail(failed['root']['linkedTraceId'])
    check(original['root']['httpStatusCode']==202 and original['root']['outcome']=='success' and failed['logs'],
          'HTTP accepted and later worker failure are distinct outcomes')
    queue_waits=[s['durationMs'] for d in queue_details for s in d['spans'] if s['name']=='queue.wait']
    check(max(queue_waits)>80 and all(s['durationMs']>=0 for d in queue_details for s in d['spans']),
          'worker records real queue wait separately from processing')
    state=stats['edge']['jobs']
    with sqlite3.connect(out/'edge.db') as db: grants=db.execute('SELECT COUNT(*) FROM Rewards').fetchone()[0]
    check(state['accepted']==len(accepted)==state['completed']+state['failed'] and state['rejected']==len(rejected)
          and grants==state['completed'],'queue admission completion failure and actual reward rows reconcile')
    check('SECRET_' not in json.dumps([spans,metrics,logs]),'query header and exception secret markers are absent from persisted telemetry')
    for name,d in [('sequential',seq),('parallel',par),('distributed',distributed),('distributed-error',bad),('failed-job',failed)]:
        (out/(name+'.trace.json')).write_text(json.dumps(d,indent=2,ensure_ascii=False),encoding='utf-8')
    return {'run':run,'fixture':True,'checksPassed':len(checks),'httpRequestsFromClient':len(records),
        'load':loads,'sequentialClientMedianMs':statistics.median(r['elapsedMs'] for r in samples['sequential']),
        'parallelClientMedianMs':statistics.median(r['elapsedMs'] for r in samples['parallel']),
        'queue':state,'maxQueueWaitMs':max(queue_waits),'entries':len(entries),'spans':len(spans),'metrics':len(metrics),
        'logs':len(logs),'pids':[edge_health['pid'],stock_health['pid']], 'sampleRate':1,
        'traceIds':{'sequential':seq['traceId'],'parallel':par['traceId'],'distributed':distributed['traceId'],
                    'distributedError':bad['traceId'],'failedJob':failed['traceId']}}
