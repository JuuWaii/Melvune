"""Publish only the allowlisted, verified Windows build assets via GitHub CLI."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def gh(*args, allow_missing=False):
    result = subprocess.run(['gh', *args], text=True, capture_output=True)
    if result.returncode:
        if allow_missing and 'HTTP 404' in result.stderr:
            return None
        raise RuntimeError('GitHub command failed: ' + result.stderr.strip())
    return result.stdout


def verified_assets(root, tag, source_sha):
    version = tag.removeprefix('v')
    if not re.fullmatch(r'0\.2\.0-rc\.\d+', version):
        raise ValueError('Only 0.2.0 release candidate tags are supported')
    prefix = 'FnMusic-' + version + '-win-x64'
    packages = root / (prefix + '-windows')
    metadata = json.loads((packages / 'release.json').read_text(encoding='utf-8-sig'))
    if metadata['version'] != version:
        raise ValueError('Tag does not match the packaged application version')
    if metadata['payloadSource'] != source_sha or metadata['packagingSource'] != source_sha:
        raise ValueError('Package sources do not match the workflow commit')
    paths = [root / (prefix + '.zip'), root / (prefix + '.SHA256SUMS.txt')]
    paths += [packages / name for name in (
        prefix + '-portable.exe', prefix + '-setup.exe', prefix + '-setup.msi',
        'SHA256SUMS.txt', 'release.json', 'WIX_LICENSE.txt', 'WIX_NOTICE.md')]
    if any(not path.is_file() or path.is_symlink() for path in paths):
        raise ValueError('Missing or linked release asset')
    actual = {path.name: digest(path) for path in paths}
    if metadata['payloadZipSha256'].lower() != actual[prefix + '.zip']:
        raise ValueError('ZIP does not match the package source record')
    for checksum_file in paths:
        if not checksum_file.name.endswith('SHA256SUMS.txt'):
            continue
        entries = checksum_file.read_text(encoding='utf-8-sig').splitlines()
        expected_names = ({prefix + '.zip'} if checksum_file.parent == root else
                          {p.name for p in paths if p.parent == packages and p != checksum_file})
        listed = set()
        for line in entries:
            checksum, name = line.split('  ', 1)
            if name not in expected_names or name in listed or actual.get(name) != checksum.lower():
                raise ValueError('Asset checksum mismatch or unexpected entry: ' + name)
            listed.add(name)
        if listed != expected_names:
            raise ValueError('Incomplete release checksums')
    return paths, actual


def verify_remote(assets, paths, checksums, require_all):
    allowed = {path.name: path.stat().st_size for path in paths}
    seen = set()
    for asset in assets:
        name = asset['name']
        if name not in allowed or name in seen:
            raise ValueError('Unexpected or duplicate remote asset: ' + name)
        if asset['state'] != 'uploaded' or asset['size'] != allowed[name]:
            raise ValueError('Incomplete remote asset: ' + name)
        if asset.get('digest') != 'sha256:' + checksums[name]:
            raise ValueError('Remote asset digest mismatch: ' + name)
        seen.add(name)
    if require_all and seen != set(allowed):
        raise ValueError('Release assets are incomplete')
    return seen


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--artifacts', type=pathlib.Path, required=True)
    parser.add_argument('--tag', required=True)
    args = parser.parse_args()
    repo = os.environ['GITHUB_REPOSITORY']
    source_sha = os.environ['GITHUB_SHA']
    paths, checksums = verified_assets(args.artifacts, args.tag, source_sha)
    notes = pathlib.Path('docs/releases/0.2.0-rc.1.md').read_text(encoding='utf-8')
    run_url = os.environ['GITHUB_SERVER_URL'] + '/' + repo + '/actions/runs/' + os.environ['GITHUB_RUN_ID']
    notes += '\n\n## 本次构建来源\n\n源码提交：`' + source_sha + '`。\n\n[通过检查的构建记录](' + run_url + ')。\n\n'
    notes += '| 附件 | SHA-256 |\n| --- | --- |\n'
    notes += ''.join('| ' + p.name + ' | ' + checksums[p.name] + ' |\n' for p in paths if p.suffix in {'.zip', '.exe', '.msi'})
    notes_path = pathlib.Path(os.environ.get('RUNNER_TEMP', '.review-audit')) / 'fnmusic-release-notes.md'
    notes_path.parent.mkdir(parents=True, exist_ok=True)
    notes_path.write_text(notes, encoding='utf-8')
    release = gh('api', 'repos/' + repo + '/releases/tags/' + args.tag, allow_missing=True)
    if release is None:
        gh('release', 'create', args.tag, '--repo', repo, '--verify-tag', '--target', source_sha, '--draft',
           '--prerelease', '--latest=false', '--title', 'FnMusic ' + args.tag[1:] + ' · Windows x64',
           '--notes-file', str(notes_path))
        release = gh('api', 'repos/' + repo + '/releases/tags/' + args.tag)
    release = json.loads(release)
    if not release['draft']:
        if not release['prerelease']:
            raise ValueError('Existing candidate release is marked stable')
        verify_remote(release['assets'], paths, checksums, require_all=True)
        print('Existing published release matches all verified assets: ' + release['html_url'])
        return
    if release['target_commitish'] != source_sha:
        raise ValueError('Existing draft belongs to a different commit')
    present = verify_remote(release['assets'], paths, checksums, require_all=False)
    missing = [str(path) for path in paths if path.name not in present]
    if missing:
        gh('release', 'upload', args.tag, *missing, '--repo', repo)
    release = json.loads(gh('api', 'repos/' + repo + '/releases/tags/' + args.tag))
    verify_remote(release['assets'], paths, checksums, require_all=True)
    gh('release', 'edit', args.tag, '--repo', repo, '--draft=false', '--prerelease', '--latest=false')
    release = json.loads(gh('api', 'repos/' + repo + '/releases/tags/' + args.tag))
    if release['draft'] or not release['prerelease']:
        raise ValueError('Release publication state is incorrect')
    verify_remote(release['assets'], paths, checksums, require_all=True)
    print('Published and verified ' + str(len(paths)) + ' assets: ' + release['html_url'])


if __name__ == '__main__':
    main()
