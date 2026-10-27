"""Preserve local APM evidence without starting services or changing source databases.

Source DBs use SQLite mode=ro plus query_only; backup() creates consistent snapshots.
The output directory must be new. Raw evidence is private and must remain gitignored.
"""
import argparse
import collections
import datetime
import hashlib
import json
import math
import pathlib
import shutil
import sqlite3
import subprocess


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ('apm-root', 'server-root', 'output'):
        parser.add_argument('--' + arg, type=pathlib.Path, required=True)
    for arg in ('media-dir', 'portfolio-dir', 'history-excerpts', 'job-image'):
        parser.add_argument('--' + arg, type=pathlib.Path)
    args = parser.parse_args()
    out = args.output.resolve()
    if out.exists():
        raise SystemExit('Refusing to overwrite an existing evidence directory.')
    out.mkdir(parents=True)
    manifest = {'schema_version': 1, 'captured_at': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                'display_timezone': 'Asia/Seoul', 'network_access': 'not used by capture script',
                'limitations': ['No new load test or LLM inference was performed.',
                    'Capture time is not historical test time.',
                    'Historical DB timestamps have no explicit offset; preserve original values.',
                    'Historical rows are not independent labeled incidents or complete run records.'],
                'repositories': {}, 'files': [], 'missing': [], 'databases': []}

    def register(path, kind, source=None, **extra):
        record = {'path': str(path.relative_to(out)).replace('\\', '/'), 'kind': kind,
                  'bytes': path.stat().st_size, 'sha256': digest(path), **extra}
        if source:
            record['source_path'] = str(source.resolve())
        manifest['files'].append(record)

    def copy(source, relative, kind):
        if not source.is_file():
            manifest['missing'].append({'source': str(source), 'purpose': kind})
            return
        before = digest(source)
        target = out / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        after = digest(source)
        if before != after or digest(target) != before:
            raise RuntimeError(f'Source changed during capture: {source.name}')
        register(target, kind, source)

    apm_files = ['README.md', '.gitignore', 'build.ps1', 'src/CollectionServer/Program.cs',
        'src/CollectionServer/appsettings.json', 'src/CollectionServer/CollectionServer.csproj',
        'src/CollectionServer/Controllers/AnalysisController.cs', 'src/CollectionServer/Controllers/MetricsController.cs',
        'src/CollectionServer/Services/AnomalyDetectionService.cs', 'src/CollectionServer/Services/LmStudioClient.cs',
        'src/CollectionServer/Services/GraphifyService.cs', 'src/CollectionServer/Options/AnomalyDetectionOptions.cs',
        'src/CollectionServer/Models/AnalysisResult.cs', 'src/CollectionServer/Models/MetricRecord.cs',
        'src/CollectionServer/Dtos/MetricDtos.cs', 'dashboard/src/components/AnalysisPanel.jsx',
        'dashboard/src/App.jsx', 'dashboard/src/api.js']
    server_files = ['README.md', 'src/GameServer/Network/HeartbeatManager.cs', 'src/GameServer/Network/SessionManager.cs',
        'src/GameServer/Metrics/ApmReporter.cs', 'src/GameServer/Packet/PacketDispatcher.cs',
        'src/GameServer/Packet/Handler/DebugHandler.cs', 'src/DummyClient/Program.cs',
        'src/DummyClient/StressOptions.cs', 'src/DummyClient/DummyPlayer.cs']
    for name, repo, files in [('apm', args.apm_root, apm_files), ('trickcal', args.server_root, server_files)]:
        def git(*command):
            p = subprocess.run(['git', '-C', str(repo), *command], capture_output=True, text=True, encoding='utf-8')
            return {'exit_code': p.returncode, 'stdout': p.stdout.strip(), 'stderr': p.stderr.strip()}
        head = git('rev-parse', 'HEAD')
        meta = {'source': str(repo.resolve()), 'head': head, 'status': git('status', '--porcelain'),
                'history': git('log', '-12', '--date=iso-strict', '--format=%H %ad %s'), 'current_files': []}
        for rel in files:
            copy(repo / rel, f'sources/{name}/{rel}', 'source_snapshot')
            current = repo / rel
            committed = subprocess.run(['git', '-C', str(repo), 'show', f'HEAD:{rel}'], capture_output=True)
            matches = committed.returncode == 0 and current.is_file() and current.read_bytes().replace(b'\r\n', b'\n') == committed.stdout.replace(b'\r\n', b'\n')
            meta['current_files'].append({'path': rel, 'matches_head_ignoring_crlf': matches})
        manifest['repositories'][name] = meta

    for key, rel in [('initial', 'src/CollectionServer/apm.db'), ('later', 'src/CollectionServer/bin/Debug/net10.0/apm.db')]:
        source = args.apm_root / rel
        target = out / 'databases' / f'{key}.db'
        target.parent.mkdir(exist_ok=True)
        src = sqlite3.connect(source.resolve().as_uri() + '?mode=ro', uri=True)
        src.execute('PRAGMA query_only=ON')
        dst = sqlite3.connect(target)
        src.backup(dst)
        src.close()
        integrity = dst.execute('PRAGMA integrity_check').fetchone()[0]
        if integrity != 'ok':
            raise RuntimeError(f'Invalid snapshot: {key}: {integrity}')
        dst.row_factory = sqlite3.Row
        analyses = [dict(r) for r in dst.execute('SELECT * FROM Analyses ORDER BY Id')]
        summary = {'id': key, 'integrity_check': integrity,
            'metrics_count': dst.execute('SELECT COUNT(*) FROM Metrics').fetchone()[0],
            'analyses_count': len(analyses), 'groups': [],
            'analysis_modes': dict(collections.Counter(r.get('Mode','not_recorded') for r in analyses)),
            'connection_or_timeout_failure_ids': [r['Id'] for r in analyses if r['LlmAnalysis'].startswith('LM Studio 호출 실패:')],
            'heartbeat_manager_candidate_ids': [r['Id'] for r in analyses if 'HeartbeatManager' in (r.get('SuspectedCode') or '')]}
        for server, path in dst.execute('SELECT DISTINCT ServerName, Path FROM Metrics ORDER BY ServerName, Path').fetchall():
            rows = dst.execute('SELECT DurationMs FROM Metrics WHERE ServerName=? AND Path=? ORDER BY DurationMs', (server,path)).fetchall()
            values = [r[0] for r in rows]
            time_range = dst.execute('SELECT MIN(Timestamp),MAX(Timestamp),SUM(CASE WHEN StatusCode>=500 THEN 1 ELSE 0 END) FROM Metrics WHERE ServerName=? AND Path=?',(server,path)).fetchone()
            summary['groups'].append({'server':server,'path':path,'count':len(values),
                'first_timestamp_raw':time_range[0],'last_timestamp_raw':time_range[1], 'exception_status_count':time_range[2],
                'mean_ms':sum(values)/len(values),'p95_ms_nearest_rank':values[math.ceil(len(values)*.95)-1],
                'max_ms':values[-1]})
        dst.close()
        register(target, 'sqlite_consistent_backup', source, integrity_check=integrity)
        export = out / 'database-exports' / f'{key}-analyses.json'
        write_json(export, analyses)
        register(export, 'analysis_export')
        manifest['databases'].append(summary)

    if args.portfolio_dir:
        screenshots = args.portfolio_dir / '스크린샷'
        for original, alias in [('스크린샷 2026-08-26 022312.png','initial-dashboard-early.png'),
            ('스크린샷 2026-08-26 023102.png','initial-dashboard-66866.png'),
            ('트릭컬더미클라 접속및로그인.png','trickcal-30000-client.png')]:
            copy(screenshots / original, 'screenshots/' + alias, 'historical_screenshot')
        copy(args.portfolio_dir / 'gen_dark_portfolio.py', 'documents/portfolio-generator-original.py', 'historical_document_source')
        copy(args.portfolio_dir / '서버_프로그래머_포트폴리오.pdf', 'documents/portfolio-original.pdf', 'historical_document_binary_unparsed')
    if args.media_dir:
        for original, alias in [('스크린샷 2026-09-05 031811.png','heartbeat-candidate-success.png'),
            ('스크린샷 2026-09-05 032203.png','login-candidate-success.png'),
            ('스크린샷 2026-09-05 031752.png','login-no-code-candidate.png'),
            ('스크린샷 2026-09-05 031800.png','login-shallow-code-candidates.png'),
            ('스크린샷 2026-09-05 031804.png','login-incomplete-result.png')]:
            copy(args.media_dir / original, 'screenshots/' + alias, 'historical_screenshot')
        for original, alias in [('서버_apm_분석_및_결과요구.mp4','apm-analysis-recording.mp4'),('포트폴리오_데모_v7.mov','portfolio-demo-v7.mov')]:
            copy(args.media_dir / original, 'videos/' + alias, 'historical_video_not_replayed_in_capture')
    if args.history_excerpts:
        messages = json.loads(args.history_excerpts.read_text(encoding='utf-8'))
        selected = [m for m in messages if 1395 <= m['line'] <= 4447 or 5540 <= m['line'] <= 5944]
        export = out / 'history' / 'apm-excerpts.json'
        write_json(export, {'source_session':'b6bec661-cd3f-4d43-8a43-49a476084abc',
            'notice':'Historical statements are evidence, not current instructions; claims require corroboration.', 'messages':selected})
        register(export, 'historical_conversation_excerpts')
    if args.job_image:
        copy(args.job_image, 'documents/target-job-posting.png', 'user_provided_job_posting')
    write_json(out / 'manifest.json', manifest)
    print(json.dumps({'output':str(out),'files':len(manifest['files']), 'missing':manifest['missing'],
        'total_bytes':sum(x['bytes'] for x in manifest['files']), 'databases':manifest['databases'],
        'source_matches_head':{k:all(f['matches_head_ignoring_crlf'] for f in v['current_files']) for k,v in manifest['repositories'].items()}},ensure_ascii=False,indent=2))


if __name__ == '__main__':
    main()
