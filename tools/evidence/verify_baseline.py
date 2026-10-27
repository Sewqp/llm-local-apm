"""Validate a private evidence bundle and case references; does not run the APM or LLM."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3


def sha256(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def within(root, relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root):
        raise ValueError('Evidence reference escapes the bundle root')
    return path


def verify(bundle, cases_path=None):
    bundle = bundle.resolve()
    manifest = json.loads((bundle / 'manifest.json').read_text(encoding='utf-8'))
    registered = set()
    errors = []
    for record in manifest['files']:
        rel = record['path']
        if rel in registered:
            errors.append(f'Duplicate manifest path: {rel}')
        registered.add(rel)
        path = within(bundle, rel)
        if not path.is_file():
            errors.append(f'Missing: {rel}')
        elif path.stat().st_size != record['bytes'] or sha256(path) != record['sha256']:
            errors.append(f'Content mismatch: {rel}')

    db_results = []
    analysis_ids = {}
    for summary in manifest['databases']:
        key = summary['id']
        path = within(bundle, f'databases/{key}.db')
        connection = sqlite3.connect(path.as_uri() + '?mode=ro', uri=True)
        try:
            connection.execute('PRAGMA query_only=ON')
            integrity = connection.execute('PRAGMA integrity_check').fetchone()[0]
            counts = {name: connection.execute(f'SELECT COUNT(*) FROM {name}').fetchone()[0]
                      for name in ('Metrics', 'Analyses')}
            analysis_ids[key] = {r[0] for r in connection.execute('SELECT Id FROM Analyses')}
        finally:
            connection.close()
        if integrity != 'ok' or counts['Metrics'] != summary['metrics_count'] or counts['Analyses'] != summary['analyses_count']:
            errors.append(f'Database summary mismatch: {key}')
        db_results.append({'id': key, 'integrity_check': integrity, **counts})

    case_count = 0
    if cases_path:
        catalog = json.loads(cases_path.read_text(encoding='utf-8'))
        seen = set()
        for case in catalog['cases']:
            case_count += 1
            key = case['id']
            if key in seen:
                errors.append(f'Duplicate case id: {key}')
            seen.add(key)
            for ref in case['evidence']:
                if ref['path'] not in registered:
                    errors.append(f'Unregistered evidence: {key}: {ref["path"]}')
                if 'database' in ref and not set(ref.get('analysis_ids', [])).issubset(analysis_ids[ref['database']]):
                    errors.append(f'Unknown analysis row: {key}')
            if case['evaluation_status'] != 'not_run':
                errors.append(f'Unexpected evaluation claim in historical catalog: {key}')
    return {'ok': not errors, 'files_checked': len(registered), 'case_references_checked': case_count,
            'databases': db_results, 'errors': errors,
            'scope': 'Archive integrity and reference validation only; no load test or model evaluation.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--cases', type=Path)
    args = parser.parse_args()
    result = verify(args.bundle, args.cases)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    raise SystemExit(0 if result['ok'] else 1)


if __name__ == '__main__':
    main()
