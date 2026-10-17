"""Prepare pinned corresponding decoder sources and resolved runtime notices."""
import argparse
import concurrent.futures
import ctypes
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import tarfile
import urllib.request
import zipfile

REPO = Path(__file__).resolve().parent.parent


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def prepare(destination):
    lock = json.loads((REPO / 'native/decoder/distribution-lock.json').read_text('utf-8'))
    cache = REPO / '.tools/distribution'
    cache.mkdir(parents=True, exist_ok=True)

    def acquire(item):
        path = cache / item['file']
        if not path.exists():
            if not item['url'].startswith('https://codeload.github.com/'):
                raise ValueError('Unexpected source host')
            partial = path.with_suffix('.partial')
            with urllib.request.urlopen(item['url'], timeout=60) as response, partial.open('wb') as output:
                while block := response.read(1024 * 1024):
                    output.write(block)
            if digest(partial) != item['sha256']:
                raise ValueError('Source digest mismatch: ' + item['name'])
            partial.replace(path)
        if digest(path) != item['sha256']:
            raise ValueError('Source digest mismatch: ' + item['name'])
        return item, path

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        sources = list(pool.map(acquire, lock['components']))
    checkout = REPO / '.tools/native-decoder/source'
    git = ['git', '-c', 'safe.directory=' + checkout.as_posix(), '-C', str(checkout)]
    if subprocess.check_output(git + ['rev-parse', 'HEAD'], text=True).strip() != lock['bridgeCommit']:
        raise ValueError('Bridge source commit mismatch')
    tree = subprocess.check_output(git + ['ls-tree', 'HEAD', 'Libs/'], text=True)
    for item, _ in sources:
        if f"commit {item['commit']}\tLibs/{item['name']}" not in tree:
            raise ValueError('Source does not match pinned build submodule')
    bridge = subprocess.check_output(git + ['archive', '--format=zip', lock['bridgeCommit']])
    destination.mkdir(parents=True, exist_ok=False)
    archive_path = destination / 'decoder-corresponding-source.zip'
    with zipfile.ZipFile(archive_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        with zipfile.ZipFile(io.BytesIO(bridge)) as original:
            for entry in original.infolist():
                if not entry.is_dir():
                    output.writestr('FFmpegInteropX/' + entry.filename, original.read(entry))
        for item, path in sources:
            # Preserve the original archive too, including symbolic links and modes.
            output.write(path, 'upstream-archives/' + item['file'])
            with tarfile.open(path) as archive:
                for entry in archive:
                    if not entry.isfile():
                        continue
                    relative = PurePosixPath(entry.name)
                    if relative.is_absolute() or '..' in relative.parts:
                        raise ValueError('Unsafe source archive')
                    output.writestr('FFmpegInteropX/Libs/' + item['name'] + '/' + '/'.join(relative.parts[1:]), archive.extractfile(entry).read())
        for relative in ['native/decoder/mp3-preroll.patch', 'native/decoder/toolchain-lock.json',
                         'native/decoder/distribution-lock.json', 'scripts/build-native-decoder.py']:
            output.write(REPO / relative, 'melvune/' + relative)
    ffmpeg = REPO / '.tools/nuget/ffmpeginteropx.desktop.ffmpeg/8.1.2/runtimes/win-x64/native'
    with os.add_dll_directory(str(ffmpeg)):
        lib = ctypes.CDLL(str(ffmpeg / 'avutil-60.dll'))
        facts = {}
        for name in ['av_version_info', 'avutil_configuration', 'avutil_license']:
            function = getattr(lib, name)
            function.restype = ctypes.c_char_p
            facts[name] = function().decode('utf-8')
    if facts['av_version_info'] != 'n8.1.2' or '--disable-gpl' not in facts['avutil_configuration'] or '--disable-version3' not in facts['avutil_configuration']:
        raise ValueError('Unexpected FFmpeg binary configuration')
    facts['binarySha256'] = {p.name: digest(p) for p in ffmpeg.glob('*.dll')}
    with zipfile.ZipFile(archive_path, 'a', zipfile.ZIP_DEFLATED) as output:
        output.writestr('FFmpegInteropX/Libs/ffmpeg/MELVUNE-BINARY-CONFIG.txt',
                        json.dumps(facts, indent=2) + '\nUnmodified FFmpeg from locked FFmpegInteropX.Desktop.FFmpeg 8.1.2.\n')
        output.write(REPO / 'docs/BUILDING_SOURCES.md', 'BUILDING_SOURCES.md')
    facts['sourceArchiveSha256'] = digest(archive_path)
    facts['sources'] = lock
    (destination / 'decoder-distribution.json').write_text(json.dumps(facts, indent=2) + '\n', encoding='utf-8')
    # Preserve package-specific terms, including runtime packs omitted from compile dependencies.
    assets = json.loads((REPO / 'native/src/Melvune.App/obj/Melvune/project.assets.json').read_text('utf-8'))
    packages = REPO / '.tools/nuget'
    resolved = {name.lower() for name, value in assets['libraries'].items() if value['type'] == 'package'}
    for family in ['microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64']:
        folder = packages / family
        if folder.exists():
            # The runtime patch follows the repository SDK, rather than selecting an arbitrary cached pack.
            version = subprocess.check_output([str(REPO / '.tools/dotnet/dotnet.exe'), '--list-runtimes'], text=True)
            for line in version.splitlines():
                if line.startswith('Microsoft.NETCore.App '):
                    resolved.add(family + '/' + line.split()[1])
    notices = []
    for name in sorted(resolved):
        package = packages / name
        copied = []
        for path in package.rglob('*'):
            if path.is_file() and ('license' in path.name.lower() or 'notice' in path.name.lower()) and path.suffix.lower() in ['', '.txt', '.md', '.rtf']:
                target = destination / 'runtime-licenses' / name / path.relative_to(package)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(path.read_bytes())
                copied.append(target.relative_to(destination).as_posix())
        notices.append({'package': name, 'materials': copied})
    (destination / 'runtime-license-index.json').write_text(json.dumps(notices, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'sourceArchive': str(archive_path), 'sourceArchiveSha256': facts['sourceArchiveSha256'], 'packages': len(notices), 'withoutLicenseFiles': [x['package'] for x in notices if not x['materials']]}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('destination', type=Path)
    prepare(parser.parse_args().destination.resolve())
