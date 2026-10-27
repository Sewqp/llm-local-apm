"""Copy legacy evidence read-only, then migrate only the copies using the actual app."""
import hashlib, json, os, pathlib, sqlite3, subprocess, sys, time, urllib.request

root=pathlib.Path(__file__).resolve().parents[2]
output=pathlib.Path(sys.argv[1]).resolve()
output.mkdir(parents=True,exist_ok=True)
dll=root/'src/CollectionServer/bin/Debug/net10.0/CollectionServer.dll'
results=[]
def content_digest(db, original_columns=None):
    values={}
    for table in ('Metrics','Analyses'):
        columns=original_columns[table] if original_columns else [r[1] for r in db.execute(f'PRAGMA table_info({table})')]
        # New nullable metric fields must not change the digest of historical columns.
        columns=[c for c in columns if c not in ('TraceId','RunId','Outcome','UserKey','SessionId')]
        digest=hashlib.sha256();count=0
        for row in db.execute('SELECT '+','.join(columns)+' FROM '+table+' ORDER BY Id'):
            digest.update(json.dumps(row,ensure_ascii=False).encode());count+=1
        values[table]={'count':count,'sha256':digest.hexdigest()}
    return values

for label,source in [('initial',root/'src/CollectionServer/apm.db'),('later',root/'src/CollectionServer/bin/Debug/net10.0/apm.db')]:
    target=output/f'migration-{label}.db'
    if target.exists(): raise RuntimeError(f'Refusing to overwrite {target}')
    original_hash=hashlib.sha256(source.read_bytes()).hexdigest()
    with sqlite3.connect(source.as_uri()+'?mode=ro',uri=True) as original,sqlite3.connect(target) as copied:
        original.execute('PRAGMA query_only=ON');original.backup(copied)
        original_columns={table:[r[1] for r in copied.execute(f'PRAGMA table_info({table})')] for table in ('Metrics','Analyses')}
        before=content_digest(copied,original_columns)
    env=os.environ.copy();env.update(ConnectionStrings__Apm='Data Source='+str(target),
        ASPNETCORE_URLS='http://127.0.0.1:5239',Logging__LogLevel__Default='Warning')
    with (output/f'migration-{label}.log').open('w',encoding='utf-8') as log:
        process=subprocess.Popen(['dotnet',str(dll)],cwd=str(root/'src/CollectionServer'),env=env,
            stdout=log,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            ready=False
            for _ in range(60):
                try:
                    with urllib.request.urlopen('http://127.0.0.1:5239/api/health',timeout=1) as response:
                        ready=response.status==200
                    if ready: break
                except (OSError,TimeoutError): time.sleep(.25)
            assert ready, 'Migration server failed to start'
        finally:
            process.terminate();process.wait(timeout=15)
    with sqlite3.connect(target) as copied:
        after=content_digest(copied,original_columns)
        assert copied.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
        assert copied.execute("SELECT count(*) FROM sqlite_master WHERE name='Spans'").fetchone()[0]==1
    assert before==after,'Historical rows changed'
    assert hashlib.sha256(source.read_bytes()).hexdigest()==original_hash,'Original DB bytes changed'
    results.append({'database':label,'passed':True,'historicalRows':before,'originalUnchanged':True})
print(json.dumps(results,indent=2))
