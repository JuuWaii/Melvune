"""One-time, idempotent cleanup of the two superseded public GitHub releases."""
import json
import subprocess


REPO = 'JuuWaii/fnmusic_pc'
KEEP = 'v0.2.0-rc.1'
REMOVE = {'v0.1.17', 'v0.1.18'}
PACKAGE_HASHES = {
    'FnMusic-0.2.0-rc.1-win-x64.zip': 'c67f4bdeade1af7084dd6792cfc3b4f670d7df40437cad06647f297dd8ace1d1',
    'FnMusic-0.2.0-rc.1-win-x64-portable.exe': 'e896cb8c95b9de816946211a8aa1c41fac24534af95e6733e75eaffe7c6b8444',
    'FnMusic-0.2.0-rc.1-win-x64-setup.exe': '5fa27987670a4003eb112e7073183109ac07ab49ef7db4e25b0a7d3872b69fa7',
    'FnMusic-0.2.0-rc.1-win-x64-setup.msi': '28b1470a40fdca19faef5b6ea0cfa26ebeae8faf6845ca1037f4df45534f1d44',
}


def api(path, method='GET', paginate=False):
    args = ['gh', 'api', 'repos/' + REPO + '/' + path, '--method', method]
    if paginate:
        args += ['--paginate', '--slurp']
    result = subprocess.run(args, text=True, capture_output=True, check=True)
    return json.loads(result.stdout) if result.stdout.strip() else None


def releases():
    return [release for page in api('releases?per_page=100', paginate=True) for release in page]


def snapshot(release):
    return {
        key: release[key] for key in ('id', 'tag_name', 'name', 'body', 'draft', 'prerelease', 'target_commitish')
    } | {'assets': sorted([
        {key: asset[key] for key in ('id', 'name', 'size', 'digest', 'state')}
        for asset in release['assets']
    ], key=lambda asset: asset['id'])}


def main():
    current = releases()
    tags = [release['tag_name'] for release in current]
    if len(tags) != len(set(tags)) or set(tags) - (REMOVE | {KEEP}):
        raise RuntimeError('Unexpected or duplicate releases; no releases deleted')
    retained = [release for release in current if release['tag_name'] == KEEP]
    if len(retained) != 1 or retained[0]['draft'] or not retained[0]['prerelease']:
        raise RuntimeError('The published candidate must exist before cleanup')
    before = snapshot(retained[0])
    assets = {asset['name']: asset for asset in retained[0]['assets']}
    if len(assets) != 9 or any(asset['state'] != 'uploaded' for asset in assets.values()):
        raise RuntimeError('The retained candidate does not have nine complete assets')
    for name, checksum in PACKAGE_HASHES.items():
        if assets.get(name, {}).get('digest') != 'sha256:' + checksum:
            raise RuntimeError('The retained package digest does not match: ' + name)
    tag_refs = {tag: api('git/ref/tags/' + tag)['object'] for tag in sorted(REMOVE | {KEEP})}
    print('Before cleanup:', ', '.join(sorted(tags)), flush=True)
    for release in sorted(current, key=lambda item: item['tag_name']):
        if release['tag_name'] in REMOVE:
            # Delete only the Release object and its uploaded assets, never a Git ref.
            api('releases/' + str(release['id']), method='DELETE')
            print('Deleted Release and assets:', release['tag_name'], flush=True)
    remaining = releases()
    if len(remaining) != 1 or snapshot(remaining[0]) != before:
        raise RuntimeError('Post-cleanup release or retained asset verification failed')
    for tag, expected in tag_refs.items():
        if api('git/ref/tags/' + tag)['object'] != expected:
            raise RuntimeError('Git tag changed: ' + tag)
    print('Verified: only ' + KEEP + ' remains; all nine assets and all three Git tags are unchanged.', flush=True)


if __name__ == '__main__':
    main()
