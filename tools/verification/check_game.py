"""Exercise the real packet server using a dedicated, disposable game database."""
import json, socket, struct, sys, time, urllib.parse, urllib.request, uuid

port = int(sys.argv[1]) if len(sys.argv) > 1 else 9129
base = sys.argv[2] if len(sys.argv) > 2 else 'http://127.0.0.1:5229'
service = 'Trickcal-APM-Verification'
results = []
def check(value, name):
    assert value, name
    results.append({'test':name,'passed':True})
def send(client, packet_id, payload=b''):
    client.sendall(struct.pack('<HH', len(payload)+4, packet_id)+payload)
def read_exact(client, length):
    value=b''
    while len(value)<length:
        part=client.recv(length-len(value))
        if not part: raise RuntimeError('Unexpected disconnect')
        value+=part
    return value
def receive(client):
    size,packet_id=struct.unpack('<HH',read_exact(client,4))
    return packet_id,read_exact(client,size-4)
def query(path):
    with urllib.request.urlopen(base+path,timeout=30) as response: return json.load(response)
def roots(operation=None):
    params={'server':service,'minutes':15,'limit':200}
    if operation: params['operation']=operation
    return query('/api/traces?'+urllib.parse.urlencode(params))['items']

name=('apm_v2_'+uuid.uuid4().hex[:12]).encode()
client=socket.create_connection(('127.0.0.1',port),timeout=15)
send(client,4004)
send(client,1000)  # malformed login payload
send(client,9000)
check(receive(client)[0]==9000,'server stays responsive after rejected packets')
send(client,1000,struct.pack('<H',len(name))+name)
packet_id,payload=receive(client)
check(packet_id==1001 and payload[0]==1,'real PostgreSQL/Redis login succeeds')
token=payload[9:].decode()
send(client,7000)
check(receive(client)[1][0]==1,'first gacha succeeds')
send(client,7000)
check(receive(client)[1][0]==0,'insufficient currency returns domain rejection')
send(client,2000)
check(receive(client)[1][0]==1,'theater acquires Grain lock')
send(client,7000)  # blocked in dispatcher before handler
send(client,9000)
check(receive(client)[0]==9000,'lock gate rejects gacha without blocking heartbeat')
time.sleep(6)  # allow one real HeartbeatManager sweep while a session is active
send(client,2002)
check(receive(client)[0]==2003,'theater exit releases lock')
send(client,9100)  # existing deliberately faulty DebugHandler
check(receive(client)[0]==9101,'existing injected fault sends its response before throwing')
client.close()
time.sleep(3)

login=next(s for s in roots('LoginRequest') if s['outcome']=='success')
detail=query('/api/traces/'+login['traceId'])
categories={s['category'] for s in detail['spans']}
check({'packet','handler','database','grain','network'} <= categories,'real login links packet, handler, PostgreSQL, Grain and response spans')
check('redis' in categories,'real login includes Redis spans')
check(detail['missingParents']==0 and not detail['truncated'],'real login trace has complete parent links')
check(bool(login['userKey']) and len(login['userKey'])==24,'real login records pseudonymous user key')
gachas=roots('GachaRequest')
check(any(s['outcome']=='success' for s in gachas) and any(s['outcome']=='rejected' for s in gachas),'real business success and rejection distinguished')
check(any(s['resultCode']=='session_locked' for s in gachas),'pre-handler lock rejection recorded')
check(any(s['resultCode']=='malformed_payload' for s in roots('LoginRequest')),'malformed payload recorded')
check(any(s['resultCode']=='unauthenticated' for s in roots('ItemUseRequest')),'unauthenticated request recorded')
fault=roots('DebugGrantCurrencyRequest')[0]
fault_detail=query('/api/traces/'+fault['traceId'])
check(fault['outcome']=='error' and fault_detail['logs'],'injected DebugHandler exception overrides sent success and links error log')
sweeps=roots('HeartbeatManager.Sweep')
check(any(any(s['name']=='HeartbeatManager.GetLockIdleDuration' for s in query('/api/traces/'+r['traceId'])['spans']) for r in sweeps[:5]),'original sequential HeartbeatManager loop is measured')
serialized=json.dumps([detail,fault_detail])
check(name.decode() not in serialized and token not in serialized,'raw player name and reconnect token excluded from telemetry')
print(json.dumps({'service':service,'loginTraceId':login['traceId'],'faultTraceId':fault['traceId'],
    'userKey':login['userKey'],'categories':sorted(categories),'results':results},indent=2))
