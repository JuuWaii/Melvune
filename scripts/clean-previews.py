"""Clean only marked development previews, keeping newest 3 and anything under 7 days.

Default is a dry run. Releases, audit evidence and legacy outputs are out of scope.
"""
import argparse
import datetime as dt
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess


def linked(path):
    return path.is_symlink() or bool(getattr(path.lstat(), 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0))


def player_running():
    if os.name != 'nt':
        return False
    result = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq Melvune.App.exe', '/FO', 'CSV', '/NH'], capture_output=True)
    return result.returncode != 0 or b'melvune.app.exe' in result.stdout.lower()


def clean(repository, apply=False, now=None, running=player_running):
    repository = pathlib.Path(repository)
    if not repository.is_dir() or linked(repository):
        raise ValueError('Repository missing or linked; cleanup stopped')
    base = repository / 'dist' / 'previews'
    report = dict(applied=apply, kept=[], candidates=[], removed=[], skipped=[])
    if not base.exists():
        return report
    if linked(repository / 'dist') or linked(base):
        raise ValueError('Preview root is linked; cleanup stopped')
    boundary = base.resolve(strict=True)
    now = now or dt.datetime.now(dt.timezone.utc)
    entries = []
    for path in base.iterdir():
        if not re.fullmatch(r'[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}', path.name) or not path.is_dir() or linked(path):
            report['skipped'].append(path.name)
            continue
        marker = path / 'preview.json'
        try:
            if not marker.is_file() or linked(marker) or marker.stat().st_size > 4096:
                raise ValueError('Missing or invalid marker')
            data = json.loads(marker.read_text(encoding='utf-8-sig'))
            if (data.get('product'), data.get('kind'), data.get('formatVersion'), data.get('configuration'), data.get('state')) != ('Melvune', 'development-preview', 1, 'Debug', 'ready'):
                raise ValueError('Unknown preview identity')
            created = dt.datetime.fromisoformat(data['createdUtc'].replace('Z', '+00:00'))
            if created.tzinfo is None or path.name[:16] != created.astimezone(dt.timezone.utc).strftime('%Y%m%dT%H%M%SZ'):
                raise ValueError('Marker timestamp differs from directory')
            entries.append((created, path, marker.read_bytes()))
        except (OSError, ValueError, KeyError, TypeError):
            report['skipped'].append(path.name)
    active = running() if apply and entries else False
    for position, (created, path, marker_bytes) in enumerate(sorted(entries, reverse=True)):
        if position < 3 or now - created < dt.timedelta(days=7) or active:
            report['kept'].append(path.name)
            continue
        # Recheck canonical containment and all links immediately before deletion.
        if path.resolve(strict=True).parent != boundary or linked(path) or any(linked(p) for p in path.rglob('*')):
            raise ValueError('Preview escaped its boundary or contains a link')
        if (path / 'preview.json').read_bytes() != marker_bytes:
            raise ValueError('Preview marker changed during cleanup')
        report['candidates'].append(path.name)
        if apply:
            shutil.rmtree(path)
            report['removed'].append(path.name)
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    options = parser.parse_args()
    print(json.dumps(clean(pathlib.Path(__file__).resolve().parents[1], options.apply), indent=2))
