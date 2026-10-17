"""Stage verified candidate assets, then publish only after remote digest checks.

Publication requires GitHub CLI and GH_TOKEN, supplied by the Actions job.
Existing published releases are never changed; a matching draft can be resumed.
"""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import re
import shutil
import subprocess
from urllib.parse import quote

VERSION = '0.2.0-rc.9'
TAG = 'v' + VERSION
REPOSITORY = 'JuuWaii/Melvune'
ZIP = f'Melvune-{VERSION}-win-x64.zip'
PORTABLE = f'Melvune-{VERSION}-win-x64-portable.exe'
INSTALLERS = {f'Melvune-{VERSION}-win-x64-setup.exe', f'Melvune-{VERSION}-win-x64-setup.msi', 'WIX_NOTICE.md', 'WIX_LICENSE.txt'}
CONTENT = {ZIP, PORTABLE, 'USER_GUIDE.md', 'RELEASE_NOTES.md', 'AI_DISCLOSURE.md'} | INSTALLERS
ASSETS = CONTENT | {'release.json', 'SHA256SUMS.txt'}


def require(value, message):
    if not value:
        raise ValueError(message)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def record(path):
    return dict(name=path.name, size=path.stat().st_size, sha256=digest(path))


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def identity(source, run_url):
    require(bool(re.fullmatch('[a-f0-9]{40}', source)), 'Invalid source commit')
    require(bool(re.fullmatch(r'https://github\.com/JuuWaii/Melvune/actions/runs/[0-9]+', run_url)), 'Invalid workflow URL')


def stage(root, payload, packages, output, source, run_url, installers):
    identity(source, run_url)
    require(not output.exists(), 'Release staging directory already exists')
    spec = importlib.util.spec_from_file_location('portable_verifier', root / 'scripts/verify-portable-package.py')
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    verified = verifier.verify_portable(packages, payload, VERSION)
    require(verified['sourceCommit'] == source, 'Verified payload differs from tag commit')
    archive = pathlib.Path(str(payload) + '.zip')
    require(archive.name == ZIP, 'Unexpected ZIP name')
    installer_metadata = read_json(installers / 'release.json')
    require(installer_metadata.get('product') == 'Melvune' and installer_metadata.get('version') == VERSION and
            installer_metadata.get('architecture') == 'win-x64' and installer_metadata.get('packageKind') == 'installers' and
            installer_metadata.get('signed') is False and installer_metadata.get('msiVersion') == '0.2.1009' and
            installer_metadata.get('wixVersion') == '4.0.6', 'Installer identity mismatch')
    require(installer_metadata.get('payloadSource') == source and installer_metadata.get('packagingSource') == source and
            installer_metadata.get('payloadZipSha256', '').lower() == verified['payloadZipSha256'].lower(), 'Installer source differs from portable payload')
    require(installer_metadata.get('artifacts') == [f'Melvune-{VERSION}-win-x64-setup.exe', f'Melvune-{VERSION}-win-x64-setup.msi'], 'Installer declarations missing EXE or MSI')
    installer_names = INSTALLERS | {'release.json', 'SHA256SUMS.txt'}
    require({p.name for p in installers.iterdir()} == installer_names, 'Unexpected installer file set')
    expected_rows = {digest(installers / name).upper() + '  ' + name for name in installer_names - {'SHA256SUMS.txt'}}
    rows = (installers / 'SHA256SUMS.txt').read_text(encoding='ascii').splitlines()
    require(len(rows) == len(expected_rows) and set(rows) == expected_rows, 'Installer checksum mismatch')
    files = {ZIP: archive, PORTABLE: packages / PORTABLE,
             'USER_GUIDE.md': root / 'docs/USER_GUIDE.md',
             'RELEASE_NOTES.md': root / f'docs/releases/{VERSION}.md',
             'AI_DISCLOSURE.md': root / 'AI_DISCLOSURE.md'}
    files.update({name: installers / name for name in INSTALLERS})
    output.mkdir(parents=True)
    for name, path in files.items():
        shutil.copyfile(path, output / name)
    metadata = dict(product='Melvune', version=VERSION, architecture='win-x64',
                    packageKind='windows-full', msiVersion='0.2.1009', wixVersion='4.0.6', signed=False, sourceCommit=source,
                    packagingSource=source, workflowRun=run_url,
                    artifacts=[record(output / name) for name in sorted(CONTENT)])
    (output / 'release.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
    # Sort names explicitly: WindowsPath ordering is case-insensitive, while
    # Linux release verification and GitHub asset names are case-sensitive.
    rows = [digest(output / name) + '  ' + name for name in sorted(CONTENT | {'release.json'})]
    (output / 'SHA256SUMS.txt').write_text('\n'.join(rows) + '\n', encoding='ascii')
    validate(output, source, run_url)


def validate(directory, source, run_url):
    identity(source, run_url)
    require({path.name for path in directory.iterdir()} == ASSETS, 'Unexpected or missing release asset')
    for path in directory.iterdir():
        require(path.is_file() and not path.is_symlink() and path.stat().st_size > 0, 'Invalid release asset')
    metadata = read_json(directory / 'release.json')
    require(metadata.get('product') == 'Melvune' and metadata.get('version') == VERSION, 'Release identity mismatch')
    require(metadata.get('sourceCommit') == source and metadata.get('packagingSource') == source, 'Release source mismatch')
    require(metadata.get('architecture') == 'win-x64' and metadata.get('packageKind') == 'windows-full' and
            metadata.get('msiVersion') == '0.2.1009' and metadata.get('wixVersion') == '4.0.6', 'Release kind mismatch')
    require(metadata.get('signed') is False and metadata.get('workflowRun') == run_url, 'Release build declaration mismatch')
    expected = [record(directory / name) for name in sorted(CONTENT)]
    require(metadata.get('artifacts') == expected, 'Release artifact digest mismatch')
    rows = (directory / 'SHA256SUMS.txt').read_text(encoding='ascii').splitlines()
    expected_rows = [digest(directory / name) + '  ' + name for name in sorted(ASSETS - {'SHA256SUMS.txt'})]
    require(rows == expected_rows, 'Checksums must cover every other release asset exactly once')
    return [record(directory / name) for name in sorted(ASSETS)]


class GitHub:
    def call(self, endpoint, method='GET', data=None, file=None):
        args = ['gh', 'api', endpoint, '--method', method, '-H', 'Accept: application/vnd.github+json',
                '-H', 'X-GitHub-Api-Version: 2022-11-28']
        if file is not None:
            args += ['-H', 'Content-Type: application/octet-stream', '--input', str(file)]
        elif data is not None:
            args += ['--input', '-']
        result = subprocess.run(args, input=json.dumps(data) if data is not None else None,
                                text=True, encoding='utf-8', capture_output=True, check=False)
        # Never echo command environment or auth headers into release logs.
        require(result.returncode == 0, f'GitHub {method} failed: {result.stderr.strip()}')
        return json.loads(result.stdout) if result.stdout.strip() else None

    def pages(self, endpoint):
        rows = []
        page = 1
        while True:
            batch = self.call(f'{endpoint}?per_page=100&page={page}')
            require(isinstance(batch, list), 'Invalid paginated API response')
            rows.extend(batch)
            if len(batch) < 100:
                return rows
            page += 1


def matching_assets(remote, local, complete=False):
    expected = {item['name']: item for item in local}
    seen = set()
    for asset in remote:
        name = asset['name']
        require(name in expected and name not in seen, 'Unexpected or duplicate remote asset')
        item = expected[name]
        require(asset.get('state') == 'uploaded' and asset.get('size') == item['size'] and
                asset.get('digest') == 'sha256:' + item['sha256'], 'Remote asset differs from verified build: ' + name)
        seen.add(name)
    if complete:
        require(seen == set(expected), 'Remote asset set is incomplete')
    return seen


def publish(directory, source, run_url, github=None):
    local = validate(directory, source, run_url)
    github = github or GitHub()
    api = 'repos/' + REPOSITORY
    # Resolve both lightweight and annotated tags. Never create/move a tag here.
    ref = github.call(api + '/git/ref/tags/' + TAG)['object']
    for _ in range(4):
        if ref['type'] != 'tag':
            break
        ref = github.call(api + '/git/tags/' + ref['sha'])['object']
    require(ref['type'] == 'commit' and ref['sha'] == source, 'Remote tag does not match verified source')
    releases = [item for item in github.pages(api + '/releases') if item['tag_name'] == TAG]
    require(len(releases) <= 1, 'Ambiguous release tag')
    metadata = read_json(directory / 'release.json')
    notes = (directory / 'RELEASE_NOTES.md').read_text(encoding='utf-8')
    notes += f'\n\n## 本次云端构建\n\n源码：`{source}`。\n\n[GitHub Actions 构建记录]({run_url})。'
    notes += '\n\n附件均由该构建重新生成，SHA-256 见 `SHA256SUMS.txt`，来源与附件摘要见 `release.json`。'
    declaration = dict(tag_name=TAG, target_commitish=source, name=f'Melvune {VERSION} · Windows x64',
                       body=notes, draft=True, prerelease=True, make_latest='false')
    if releases:
        release = releases[0]
        require(release.get('draft') is True, 'Refusing to change an already published release')
        require(release.get('target_commitish') == source and release.get('body') == notes and
                release.get('prerelease') is True, 'Existing draft does not belong to this exact build')
    else:
        release = github.call(api + '/releases', 'POST', declaration)
    release_id = release['id']
    endpoint = api + f'/releases/{release_id}'
    uploaded = matching_assets(github.pages(endpoint + '/assets'), local)
    for item in local:
        if item['name'] not in uploaded:
            upload = f'https://uploads.github.com/{api}/releases/{release_id}/assets?name=' + quote(item['name'], safe='')
            github.call(upload, 'POST', file=directory / item['name'])
    matching_assets(github.pages(endpoint + '/assets'), local, complete=True)
    # Publishing is the final mutation, after every remote digest matches.
    github.call(endpoint, 'PATCH', dict(draft=False, prerelease=True, make_latest='false'))
    final = github.call(endpoint)
    require(final.get('draft') is False and final.get('prerelease') is True and
            final.get('tag_name') == TAG and final.get('target_commitish') == source, 'Publication state mismatch')
    matching_assets(github.pages(endpoint + '/assets'), local, complete=True)
    receipt = dict(releaseId=release_id, url=final['html_url'], tag=TAG,
                   sourceCommit=metadata['sourceCommit'], workflowRun=run_url, assets=local)
    print(json.dumps(receipt, indent=2))
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['stage', 'verify', 'publish'])
    parser.add_argument('--directory', type=pathlib.Path, required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--run-url', required=True)
    parser.add_argument('--payload', type=pathlib.Path)
    parser.add_argument('--packages', type=pathlib.Path)
    parser.add_argument('--installers', type=pathlib.Path)
    args = parser.parse_args()
    if args.command == 'stage':
        require(args.payload is not None and args.packages is not None and args.installers is not None, 'Staging requires payload, portable and installer packages')
        stage(pathlib.Path(__file__).resolve().parents[1], args.payload, args.packages, args.directory, args.source, args.run_url, args.installers)
    elif args.command == 'verify':
        print(json.dumps(validate(args.directory, args.source, args.run_url), indent=2))
    else:
        publish(args.directory, args.source, args.run_url)


if __name__ == '__main__':
    main()
