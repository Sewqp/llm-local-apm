"""Run isolated synthetic architecture examples. Python 3.10+, .NET 10; loopback only."""
import argparse, json, os, pathlib, socket, subprocess, sys, time, urllib.request
from check_architectures import verify

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--collector-dll', required=True)
    parser.add_argument('--demo-dll', required=True)
    parser.add_argument('--repo', type=pathlib.Path, required=True)
    parser.add_argument('--out', type=pathlib.Path, required=True)
    parser.add_argument('--keep-running', action='store_true')
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    for port in (5239, 5241, 5242):
        with socket.socket() as probe:
            if probe.connect_ex(('127.0.0.1', port)) == 0:
                raise RuntimeError(f'Port {port} is already in use. Existing processes are never stopped.')
    run = 'architectures-' + time.strftime('%Y%m%d-%H%M%S')
    processes, streams = [], []
    env = os.environ.copy()
    env.update({'ConnectionStrings__Apm': f'Data Source={out / "collector.db"}',
        'Logging__LogLevel__Default': 'Warning', 'Telemetry__RetentionDays': '30',
        'LmStudio__TimeoutSeconds': '150', 'LmStudio__Model': 'google_gemma-4-26b-a4b-it'})
    source = str(args.repo.resolve() / 'examples' / 'ArchitectureDemo')
    env['Graphify__ServerSourceDirs__ArchitectureEdge'] = source
    env['Graphify__ServerSourceDirs__ArchitectureStock'] = source
    specs = [
        ('collector', ['dotnet', str(pathlib.Path(args.collector_dll).resolve()), '--urls', 'http://127.0.0.1:5239'], args.repo/'src'/'CollectionServer', 'http://127.0.0.1:5239/api/traces'),
        ('stock', ['dotnet', str(pathlib.Path(args.demo_dll).resolve()), '--role', 'stock', '--run', run, '--db', str(out/'stock.db')], args.repo, 'http://127.0.0.1:5242/health'),
        ('edge', ['dotnet', str(pathlib.Path(args.demo_dll).resolve()), '--role', 'edge', '--run', run, '--db', str(out/'edge.db')], args.repo, 'http://127.0.0.1:5241/health')]
    succeeded = False
    try:
        for name, command, cwd, health in specs:
            stream = open(out/(name+'.log'), 'w', encoding='utf-8'); streams.append(stream)
            process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            processes.append(process)
            for attempt in range(100):
                if process.poll() is not None:
                    raise RuntimeError(f'{name} exited: {(out/(name+".log")).read_text()}')
                try:
                    with urllib.request.urlopen(health, timeout=1) as response:
                        if response.status == 200: break
                except Exception: time.sleep(.2)
            else: raise RuntimeError(f'{name} failed readiness')
        (out/'processes.json').write_text(json.dumps({'run':run, 'processes':[
            {'name':spec[0], 'pid':proc.pid, 'command':spec[1]} for spec,proc in zip(specs,processes)]}, indent=2), encoding='utf-8')
        result = verify(out, run)
        succeeded = True
        print(json.dumps(result, indent=2, ensure_ascii=False))
    finally:
        if not (succeeded and args.keep_running):
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                    try: process.wait(timeout=10)
                    except subprocess.TimeoutExpired: process.kill(); process.wait()
        for stream in streams: stream.close()

if __name__ == '__main__': main()
