"""Archive the 2026-10-26 upgrade's verified results; never modify original evidence."""
import argparse, datetime, hashlib, json, pathlib, shutil, sqlite3, subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--work', type=pathlib.Path, required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
args = parser.parse_args()
work, output = args.work.resolve(), args.output.resolve()
repo = pathlib.Path(__file__).resolve().parents[2]
output.mkdir(parents=True, exist_ok=False)

def read_json(path):
    raw = path.read_bytes()
    return json.loads(raw.decode('utf-16' if raw[:2] in (b'\xff\xfe', b'\xfe\xff') else 'utf-8-sig'))

def save(name, value):
    (output/name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

def sha(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024*1024), b''): value.update(chunk)
    return value.hexdigest()

for name in ('agent-checks.json', 'api-checks.json', 'game-checks.json', 'source-path-checks.json',
             'load-checks.json', 'launcher-checks.json', 'baseline-final-checks.json',
             'llm-debug-result.json', 'llm-debug-with-code.json', 'processes.json'):
    save(name, read_json(work/name))
for name in ('apm-v2-waterfall.png', 'apm-v2-local-llm.png'):
    shutil.copy2(work/name, output/name)

with sqlite3.connect((work/'verification.db').as_uri()+'?mode=ro', uri=True) as original:
    original.execute('PRAGMA query_only=ON')
    with sqlite3.connect(output/'verification.db') as target:
        original.backup(target)
        assert target.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        counts = {table: target.execute('SELECT COUNT(*) FROM '+table).fetchone()[0]
                  for table in ('Metrics','Analyses','Spans','TelemetryLogs','RuntimeSamples','TraceInvestigations')}
        trace = 'cbf7ed75e13dff3a66562bcc8231dc8e'
        request_ms = target.execute('SELECT DurationMs FROM Spans WHERE TraceId=? AND IsEntry=1',(trace,)).fetchone()[0]
        database_ms = target.execute("SELECT MAX(DurationMs) FROM Spans WHERE TraceId=? AND Category='database'",(trace,)).fetchone()[0]
        save('db-delay-check.json', dict(traceId=trace, requestMs=request_ms, databaseSpanMs=database_ms,
             clientRttMs=814.8957, clientRttSource='DbDelayProbe terminal output recorded during the experiment',
             injection='800ms ACCESS EXCLUSIVE table lock in the isolated database', passed=database_ms >= 700))

def row_digest(connection, table, columns):
    value = hashlib.sha256(); count = 0
    for row in connection.execute('SELECT '+','.join(columns)+' FROM '+table+' ORDER BY Id'):
        value.update(json.dumps(row,ensure_ascii=False).encode()); count += 1
    return {'count':count, 'sha256':value.hexdigest()}

migrations=[]
for label, path in [('initial',repo/'src/CollectionServer/apm.db'),
                    ('later',repo/'src/CollectionServer/bin/Debug/net10.0/apm.db')]:
    migrated=work/'migration-checks-v2'/f'migration-{label}.db'
    archived=repo/'.local-evidence/2026-10-26/databases'/f'{label}.db'
    comparisons={}
    with sqlite3.connect(path.as_uri()+'?mode=ro',uri=True) as source, \
         sqlite3.connect(migrated.as_uri()+'?mode=ro',uri=True) as target, \
         sqlite3.connect(archived.as_uri()+'?mode=ro',uri=True) as baseline:
        for table in ('Metrics','Analyses'):
            columns=[r[1] for r in source.execute('PRAGMA table_info('+table+')')]
            before=row_digest(source,table,columns)
            assert before == row_digest(target,table,columns) == row_digest(baseline,table,columns)
            comparisons[table]=before
        assert target.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    migrations.append({'id':label,'passed':True,'originalColumnsCompared':comparisons,
        'originalFileSha256AtArchive':sha(path),'migratedCopySha256':sha(migrated),
        'note':'Read-only final comparison of the original, preserved baseline and previously migrated copy.'})
save('migration-checks.json',migrations)

installed=read_json(work/'installed.json')
sources=[]
for relative in installed:
    source=work/relative
    target=output/'source'/relative
    target.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(source,target)
    sources.append(relative)
save('source-inventory.json',sources)
files=[{'path':p.relative_to(output).as_posix(),'bytes':p.stat().st_size,'sha256':sha(p)}
       for p in sorted(output.rglob('*')) if p.is_file()]
save('manifest.json',{'capturedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'scope':'Local APM v2 verification; not an enterprise equivalence, 30k re-test or fine-tuning claim.',
    'counts':counts,'files':files,'gitHead':subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),
    'sourceState':'Uncommitted working tree; see source inventory and hashes.'})
assert all(sha(output/f['path'])==f['sha256'] for f in files)
print(json.dumps({'ok':True,'files':len(files),'databaseCounts':counts,'output':str(output)},ensure_ascii=False))
