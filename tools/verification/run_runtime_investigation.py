"""Bounded Windows loopback experiment. New DB/Redis/APM files per run; no reset/delete."""
import argparse, ctypes, datetime as dt, hashlib, json, os, pathlib, shutil
import socket, sqlite3, subprocess, sys, time, urllib.request
from ctypes import wintypes
from win32_observer import memory, process, tcp, system_times
from host_observer import HostObserver

P = argparse.ArgumentParser()
P.add_argument('--work',type=pathlib.Path,required=True)
P.add_argument('--server-root',type=pathlib.Path,required=True)
P.add_argument('--apm-root',type=pathlib.Path,required=True)
P.add_argument('--case',required=True)
P.add_argument('--apm',choices=['on','off'],default='on')
P.add_argument('--min-threads',type=int,default=0)
P.add_argument('--split-cpus',action='store_true')
P.add_argument('--stacks',action='store_true')
P.add_argument('--count',type=int,default=30000)
P.add_argument('--no-probe',action='store_true')
P.add_argument('--game-dir',type=pathlib.Path)
P.add_argument('--no-tcp-monitor',action='store_true')
P.add_argument('--hold',type=int,default=60)
P.add_argument('--allow-timewait-overlap',action='store_true',help='Diagnostic reproduction only; marks contaminated host state explicitly')
args=P.parse_args()
if not args.case.replace('_','').isalnum() or not 1<=args.count<=30000 or not 10<=args.hold<=180: raise ValueError('invalid case/count/hold')
W=args.work.resolve(); F=W/args.case; F.mkdir(exist_ok=False)
GAME=args.game_dir.resolve() if args.game_dir else W/'game'
PG=pathlib.Path('C:/Program Files/PostgreSQL/18/bin'); REDIS=pathlib.Path('C:/Program Files/Redis')
PGPORT=25446; RPORT=26396; CPORT=5256; TCP=9321
FLAGS=subprocess.CREATE_NO_WINDOW
def utc():return dt.datetime.now(dt.timezone.utc).isoformat()
def save(path,value):path.write_text(json.dumps(value,indent=2),encoding='utf-8')
def event(**kw):print(json.dumps(dict(utc=utc(),case=args.case,**kw)),flush=True)
def cmd(command,timeout=60):
    p=subprocess.run([str(a) for a in command],capture_output=True,text=True,creationflags=FLAGS,timeout=timeout)
    if p.returncode:raise RuntimeError(str(command)+': '+p.stdout[-1200:]+p.stderr[-1200:])
    return p.stdout
def sql(db,query):return cmd([PG/'psql.exe','-h','127.0.0.1','-p',PGPORT,'-U','diagtest','-d',db,'-v','ON_ERROR_STOP=1','-At','-c',query])
def free(port):
    with socket.socket() as s:s.bind(('127.0.0.1',port))
def start(command,name,env=None):
    with (F/(name+'.log')).open('w',encoding='utf-8') as log:
        return subprocess.Popen([str(a) for a in command],cwd=F,env=env,stdout=log,stderr=subprocess.STDOUT,creationflags=FLAGS)
def stop(child):
    if child and child.poll() is None:
        child.terminate()
        try:child.wait(timeout=15)
        except subprocess.TimeoutExpired:child.kill();child.wait(timeout=10)
def port_ready(port,child):
    for _ in range(90):
        if child.poll() is not None:raise RuntimeError('Child exited: '+str(child.pid))
        try:
            with socket.create_connection(('127.0.0.1',port),timeout=.3):return
        except OSError:time.sleep(.5)
    raise TimeoutError(str(port))
def affinity(child,mask):
    k=ctypes.windll.kernel32;k.OpenProcess.restype=wintypes.HANDLE
    k.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
    k.SetProcessAffinityMask.argtypes=[wintypes.HANDLE,ctypes.c_size_t]
    h=k.OpenProcess(0x0200|0x0400,False,child.pid)
    if not h:raise OSError('OpenProcess affinity')
    try:
        if not k.SetProcessAffinityMask(h,mask):raise OSError('SetProcessAffinityMask')
    finally:k.CloseHandle(h)
def runtime():
    try:
        with urllib.request.urlopen(f'http://127.0.0.1:{CPORT}/api/traces/runtime?server=Trickcal-Runtime-Diag',timeout=.8) as r:
            return next((x for x in json.load(r) if x['runId']==args.case),None)
    except Exception as e:return {'error':str(e)}
def hook_env(env,name):
    if not args.no_probe:
        env['DOTNET_STARTUP_HOOKS']=str(args.apm_root/'tools/verification/RuntimeProbe/bin/Release/net10.0/RuntimeProbe.dll')
        env['APM_DIAG_OUTPUT']=str(F/(name+'-runtime.jsonl'))
    return env
def collect_client(name,count,rate,hold,game,collector,redis):
    env=hook_env(os.environ.copy(),name)
    client=start(['dotnet',W/'client/ConcurrentLoad.dll','--count',count,'--rate',rate,'--hold',hold,
                  '--heartbeat',5,'--port',TCP,'--run',args.case+'-'+name,'--output',F/(name+'-result.json')],name,env)
    if args.split_cpus:affinity(client,0xFF00)
    save(F/(name+'-processes.json'),{'game':game.pid,'client':client.pid,'collector':collector.pid if collector else None,'redis':redis.pid})
    started=time.monotonic();last_event=-15;last_api=-5;last_tcp=-5;stack_taken=False
    host=HostObserver()
    try:
        with (F/(name+'-observer.jsonl')).open('w',encoding='utf-8') as output:
            while True:
                elapsed=time.monotonic()-started
                row={'utc':utc(),'elapsed':elapsed,'memory':memory(),'systemTimes':system_times(),
                     'game':process(game.pid),'client':process(client.pid),'redis':process(redis.pid)}
                if collector:row['collector']=process(collector.pid)
                if collector and elapsed-last_api>=5:row['runtime']=runtime();last_api=elapsed
                row['host']=host.sample()
                if not args.no_tcp_monitor and elapsed-last_tcp>=5:
                    check=time.perf_counter();row['tcp']=tcp(game.pid,TCP);last_tcp=elapsed
                    row['tcpQueryMs']=(time.perf_counter()-check)*1000
                output.write(json.dumps(row)+'\n');output.flush()
                if client.poll() is not None:break
                if game.poll() is not None:raise RuntimeError('game exited')
                if row['memory']['availableMb']<750 or elapsed>hold+180:raise RuntimeError('resource/time bound')
                if args.stacks and not stack_taken and name=='main' and elapsed>20:
                    probe=F/'game-runtime.jsonl'
                    if probe.exists():
                        tail=probe.read_text(encoding='utf-8').splitlines()
                        last=json.loads(tail[-1]) if tail else {}
                        if last.get('probePendingMs',0)>500 or last.get('threadPoolPending',0)>500:
                            tool=W/'diagnostic-tools/dotnet-stack.exe'
                            with (F/'stack-capture.txt').open('w',encoding='utf-8') as log:
                                stack=start([tool,'report','-p',game.pid],'stack-capture')
                            save(F/'stack-capture-event.json',{'utc':utc(),'trigger':last,'note':'Stack capture can perturb execution.'})
                            stack_taken=True
                if elapsed-last_event>=20:
                    progress=[]
                    for line in (F/(name+'.log')).read_text(encoding='utf-8',errors='replace').splitlines()[-4:]:
                        try:progress.append(json.loads(line))
                        except ValueError:pass
                    event(phase=name,elapsed=round(elapsed),progress=progress[-1:] ,memory=row['memory'])
                    last_event=elapsed
                time.sleep(.5)
        result=json.loads((F/(name+'-result.json')).read_text())
        if client.returncode or not result['passed']:raise RuntimeError('Client functional check failed')
        return result
    finally:stop(client);host.close()
def drain(game,collector):
    stable=0
    for _ in range(60):
        row=runtime() if collector else None
        empty=tcp(game.pid,TCP).get('serverEstablished')==0
        if collector:empty=empty and row and not row.get('error') and all(row.get(k,0)==0 for k in ('activeSessions','queueDepth','requestQueueDepth','spoolPendingBatches'))
        stable=stable+1 if empty else 0
        if stable>=2:return row
        time.sleep(2)
    raise TimeoutError('drain')

game=collector=redis=None; pg_started=False; result={'status':'starting'}
try:
    preflight=[]; stable=0; deadline=time.monotonic()+300
    while True:
        snapshot={'utc':utc(),**tcp(0,0)};preflight.append(snapshot)
        if 'error' in snapshot:raise RuntimeError('TCP preflight could not be read')
        if args.allow_timewait_overlap:break
        stable=stable+1 if snapshot['systemTimeWait']<2000 else 0
        if stable>=2:break
        if time.monotonic()>deadline:raise TimeoutError('Previous TIME_WAIT connections have not drained; no load started')
        if len(preflight)%10==1:event(phase='waiting-for-previous-timewait',count=snapshot['systemTimeWait'])
        time.sleep(2)
    save(F/'tcp-preflight.json',{'allowOverlap':args.allow_timewait_overlap,'threshold':2000,'observations':preflight})
    for port in (PGPORT,RPORT,CPORT,TCP,11321,30321,8931):free(port)
    native=W/'native';native.mkdir(exist_ok=True);data=native/'pg'
    if not data.exists():
        cmd([PG/'initdb.exe','-D',data,'-U','diagtest','-A','trust','--encoding=UTF8','--locale=C'],120)
        with (data/'postgresql.conf').open('a') as f:f.write(f"\nlisten_addresses='127.0.0.1'\nport={PGPORT}\nmax_connections=300\nshared_buffers='256MB'\n")
    with (native/'pg-control.log').open('a') as log:
        subprocess.run([str(PG/'pg_ctl.exe'),'-D',str(data),'-l',str(native/'postgres.log'),'-w','start'],stdout=log,stderr=subprocess.STDOUT,creationflags=FLAGS,timeout=60,check=True)
    pg_started=True
    database='diag_'+args.case.lower()
    sql('postgres','CREATE DATABASE '+database)
    for filename in ('schema.sql','seed_characters.sql'):
        cmd([PG/'psql.exe','-h','127.0.0.1','-p',PGPORT,'-U','diagtest','-d',database,'-v','ON_ERROR_STOP=1','-f',args.server_root/'DB'/filename])
    (F/'redis.conf').write_text(f'bind 127.0.0.1\nport {RPORT}\ndir "{F.as_posix()}"\nappendonly yes\nappendfsync everysec\nmaxmemory 2gb\nmaxmemory-policy noeviction\nsave ""\n',encoding='utf-8')
    redis=start([REDIS/'redis-server.exe',F/'redis.conf'],'redis');port_ready(RPORT,redis)
    if args.apm=='on':
        env=hook_env(os.environ.copy(),'collector')
        env.update(ASPNETCORE_URLS=f'http://127.0.0.1:{CPORT}',ConnectionStrings__Apm='Data Source='+str(F/'collector.db'),Logging__LogLevel__Default='Warning',Logging__LogLevel__Microsoft='Warning')
        collector=start(['dotnet',W/'collector/CollectionServer.dll'],'collector',env);port_ready(CPORT,collector)
    env=hook_env(os.environ.copy(),'game')
    env.update(TCP_PORT=str(TCP),TCP_BACKLOG='512',TCP_ACCEPT_DELAY_MS='0',
        TRICKCAL_POSTGRES_CONN=f'Host=127.0.0.1;Port={PGPORT};Database={database};Username=diagtest;Pooling=true;Minimum Pool Size=5;Maximum Pool Size=120;',
        TRICKCAL_REDIS_CONN=f'127.0.0.1:{RPORT}',ORLEANS_CLUSTER_ID=args.case,ORLEANS_SERVICE_ID='Trickcal-Runtime-Diag',
        ORLEANS_SILO_PORT='11321',ORLEANS_GATEWAY_PORT='30321',DASHBOARD_PORT='8931',APM_COLLECTOR_ENDPOINT=f'http://127.0.0.1:{CPORT}',
        APM_ENABLED='1' if args.apm=='on' else '0',OTEL_EXPORTER_OTLP_ENABLED='0',APM_RUN_ID=args.case,
        APM_CODE_VERSION='runtime-cpu-fix-20261116',APM_DETAIL_SAMPLE_RATE='0.01',APM_SLOW_TRACE_MS='100',
        APM_SPOOL_DIRECTORY=str(F/'spool'),WATCH_PLAYER_NAME='',TRICKCAL_ENABLE_DEBUG_COMMANDS='0',TRICKCAL_ENABLE_FAULT_INJECTION='0')
    if args.min_threads:env['APM_DIAG_MIN_THREADS']=str(args.min_threads)
    save(F/'protocol.json',{'startedUtc':utc(),'apm':args.apm,'count':args.count,'holdSeconds':args.hold,'heartbeatSeconds':5,'minThreads':args.min_threads,
        'splitCpus':args.split_cpus,'startupProbe':not args.no_probe,'stackCapture':args.stacks,'tcpMonitor':not args.no_tcp_monitor,
        'allowTimewaitOverlap':args.allow_timewait_overlap,
        'scope':'Same host. CPU-affinity separation is not physical host isolation.',
        'binaryHashes':{name:hashlib.sha256(path.read_bytes()).hexdigest() for name,path in {'game':GAME/'GameServer.dll','agent':GAME/'Apm.Agent.dll','collector':W/'collector/CollectionServer.dll','client':W/'client/ConcurrentLoad.dll'}.items()}})
    game=start(['dotnet',GAME/'GameServer.dll'],'game',env)
    if args.split_cpus:affinity(game,0x00FF)
    port_ready(TCP,game)
    if f'[APM] enabled={args.apm=="on"}, OTLP=False' not in (F/'game.log').read_text(encoding='utf-8',errors='replace'):raise RuntimeError('APM mode not confirmed')
    warm=collect_client('warmup',1000,1000,10,game,collector,redis);drain(game,collector)
    main=collect_client('main',args.count,3000,args.hold,game,collector,redis);last=drain(game,collector)
    result={'status':'passed','warmup':warm,'main':main,'finalRuntime':last,'endedUtc':utc()}
except Exception as error:
    result={'status':'failed','error':repr(error),'endedUtc':utc()};raise
finally:
    stop(game);stop(collector)
    if redis and redis.poll() is None:
        try:cmd([REDIS/'redis-cli.exe','-p',RPORT,'shutdown'],15)
        except Exception:stop(redis)
    if pg_started:
        with (W/'native/pg-control.log').open('a') as log:
            p=subprocess.run([str(PG/'pg_ctl.exe'),'-D',str(W/'native/pg'),'-m','fast','-w','stop'],stdout=log,stderr=subprocess.STDOUT,creationflags=FLAGS,timeout=60)
            result['postgresStopped']=p.returncode==0
    save(F/'result.json',result)
    event(phase='finished',status=result['status'])
if collector:
    with sqlite3.connect((F/'collector.db').resolve().as_uri()+'?mode=ro',uri=True) as connection:
        counts=dict(connection.execute("SELECT Path,count(*) FROM Metrics WHERE Method='PACKET' AND RunId=? GROUP BY Path",(args.case,)))
        expected={'/packet/LoginRequest':1000+args.count,'/packet/Heartbeat':warm['heartbeatAck']+main['heartbeatAck']}
        save(F/'ledger.json',{'expected':expected,'actual':counts,'matched':counts==expected,'quickCheck':connection.execute('PRAGMA quick_check').fetchone()[0]})
        result['telemetryLedgerMatched']=counts==expected
        if counts!=expected:result.update(status='failed',error='Telemetry ledger mismatch')
        save(F/'result.json',result)
        if counts!=expected:raise RuntimeError('Telemetry ledger mismatch')
