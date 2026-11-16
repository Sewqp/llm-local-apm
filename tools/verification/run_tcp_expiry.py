"""Windows-only TCP expiration experiment. Output must be a new directory."""
import argparse, datetime as dt, json, os, pathlib, subprocess, time
from host_observer import HostObserver
from win32_observer import memory, process, system_times, tcp

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--probe', type=pathlib.Path, required=True, help='Built TcpExpiryProbe.dll')
parser.add_argument('--runtime-probe', type=pathlib.Path, required=True, help='Built RuntimeProbe.dll')
parser.add_argument('--count', type=int, default=30000)
args = parser.parse_args()
if not 1 <= args.count <= 30000:
    parser.error('count must be between 1 and 30000')
probe = args.probe.resolve(strict=True)
runtime_probe = args.runtime_probe.resolve(strict=True)
folder = args.output.resolve()
folder.mkdir(parents=True, exist_ok=False)
host = HostObserver()
child = None
try:
    initial = tcp(0, 0)
    (folder / 'preflight.json').write_text(json.dumps(initial, indent=2), encoding='utf-8')
    if initial.get('systemTimeWait', 30000) >= 2000:
        raise RuntimeError('Wait for previous TIME_WAIT connections to drain before starting')
    env = os.environ.copy()
    env.update(DOTNET_STARTUP_HOOKS=str(runtime_probe), APM_DIAG_OUTPUT=str(folder / 'runtime.jsonl'))
    with (folder / 'events.jsonl').open('w', encoding='utf-8') as log:
        child = subprocess.Popen(['dotnet', str(probe), str(args.count)], cwd=folder, env=env,
            stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    start = time.monotonic()
    last = -20
    closed_seen = False
    port = 0
    with (folder / 'host.jsonl').open('w', encoding='utf-8') as output:
        while child.poll() is None:
            elapsed = time.monotonic() - start
            events = []
            for line in (folder / 'events.jsonl').read_text(encoding='utf-8').splitlines():
                try:
                    events.append(json.loads(line))
                except ValueError:
                    pass
            if events:
                port = events[0]['port']
            row = {'utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'elapsed': elapsed,
                'memory': memory(), 'systemTimes': system_times(), 'process': process(child.pid),
                'host': host.sample()}
            # No TCP-table queries during expiration; only post-close and final snapshots.
            if not closed_seen and any(e['phase'] == 'closed' for e in events):
                row['tcpImmediatelyAfterClose'] = tcp(child.pid, port)
                closed_seen = True
            output.write(json.dumps(row) + '\n')
            output.flush()
            if elapsed - last >= 20:
                print(json.dumps({'elapsed': round(elapsed), 'phase': events[-1]['phase'] if events else None}), flush=True)
                last = elapsed
            if elapsed > 240 or row['memory']['availableMb'] < 750:
                raise RuntimeError('Resource/time bound reached')
            time.sleep(.5)
    result = {'exitCode': child.wait(), 'endTcp': tcp(child.pid, port), 'noTcpPollingDuringExpiry': True}
    (folder / 'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    if result['exitCode']:
        raise RuntimeError('TCP probe failed; inspect events.jsonl')
finally:
    host.close()
    if child is not None and child.poll() is None:
        child.terminate()
        child.wait(timeout=10)
