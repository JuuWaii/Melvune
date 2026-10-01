"""Build the pinned x64 FFmpegInteropX MP3 preroll fork without system installation."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

REPO = Path(__file__).resolve().parent.parent
ROOT = REPO / '.tools/native-decoder'
LOCK = REPO / 'native/decoder/toolchain-lock.json'
PATCH = REPO / 'native/decoder/mp3-preroll.patch'


def sha256(path):
    with path.open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest().upper()


def run(arguments, cwd=None, env=None):
    subprocess.run(list(map(str, arguments)), cwd=cwd, env=env, check=True)


def acquire(name, url, digest):
    if urllib.parse.urlparse(url).hostname not in {'download.visualstudio.microsoft.com', 'api.nuget.org'}:
        raise ValueError('Unrecognized dependency host')
    target = ROOT / 'downloads' / name
    target.parent.mkdir(parents=True, exist_ok=True)
    if not target.exists():
        partial = target.with_suffix(target.suffix + '.partial')
        with urllib.request.urlopen(url, timeout=90) as response, partial.open('wb') as output:
            while block := response.read(1024 * 1024):
                output.write(block)
        if sha256(partial) != digest.upper():
            raise ValueError('Dependency hash mismatch: ' + name)
        partial.replace(target)
    if sha256(target) != digest.upper():
        raise ValueError('Dependency hash mismatch: ' + name)
    return target


def extract(package, destination, contents_only=False):
    destination = destination.resolve()
    with zipfile.ZipFile(package) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            path = PurePosixPath(urllib.parse.unquote(entry.filename.replace('\\', '/')))
            if contents_only:
                if not path.parts or path.parts[0] != 'Contents':
                    continue
                path = PurePosixPath(*path.parts[1:])
            target = (destination / path).resolve()
            if not target.is_relative_to(destination):
                raise ValueError('Unsafe dependency archive path')
            data = archive.read(entry)
            target.parent.mkdir(parents=True, exist_ok=True)
            # Compiler helper processes can keep unchanged runtime DLLs open.
            if not target.exists() or target.read_bytes() != data:
                target.write_bytes(data)


def main():
    lock = json.loads(LOCK.read_text(encoding='utf-8-sig'))
    ROOT.mkdir(parents=True, exist_ok=True)
    for item in lock['msvcPayloads']:
        extract(acquire(item['fileName'], item['url'], item['sha256']), ROOT / 'msvc', True)
        print('Verified MSVC payload: ' + item['fileName'], flush=True)
    for item in lock['nugetPackages']:
        name = f"{item['name']}.{item['version']}.nupkg"
        url = f"https://api.nuget.org/v3-flatcontainer/{item['name']}/{item['version']}/{name}"
        extract(acquire(name, url, item['sha256']), ROOT / item['name'])
        print('Verified SDK package: ' + name, flush=True)

    packages = REPO / '.tools/nuget'
    for item in lock['restoredPackages']:
        package = packages / item['name'] / item['version'] / f"{item['name']}.{item['version']}.nupkg"
        if not package.exists() or sha256(package) != item['sha256'].upper():
            raise ValueError('Restore the locked native solution packages first: ' + item['name'])

    checkout = ROOT / 'source'
    git = ['git', '-c', 'safe.directory=' + checkout.as_posix(), '-c', 'core.autocrlf=false', '-C', checkout]
    if not checkout.exists():
        checkout.mkdir()
        run([*git, 'init', '-q'])
        run([*git, 'fetch', '--depth=1', lock['sourceUrl'], lock['sourceCommit']])
        run([*git, 'checkout', '--detach', 'FETCH_HEAD'])
    actual = subprocess.check_output([*map(str, git), 'rev-parse', 'HEAD'], text=True).strip()
    if actual != lock['sourceCommit']:
        raise ValueError('Decoder checkout is not at the pinned source commit')
    status = subprocess.check_output([*map(str, git), 'status', '--porcelain'], text=True).strip()
    if status and status != 'M Source/FFmpegReader.cpp':
        raise ValueError('Unexpected decoder source changes; preserve and inspect them before rebuilding')
    difference = subprocess.check_output([*map(str, git), 'diff', '--', 'Source/FFmpegReader.cpp']).replace(b'\r\n', b'\n')
    if difference:
        if difference != PATCH.read_bytes().replace(b'\r\n', b'\n'):
            raise ValueError('Decoder checkout has a different patch')
    else:
        run([*git, 'apply', '--check', PATCH])
        run([*git, 'apply', PATCH])

    source = checkout / 'Source'
    out = ROOT / 'build'
    generated = out / 'generated'
    generated.mkdir(parents=True, exist_ok=True)
    msvc = ROOT / 'msvc/VC/Tools/MSVC' / lock['vcDirectory']
    compiler = msvc / 'bin/Hostx64/x64/cl.exe'
    sdk = ROOT / 'microsoft.windows.sdk.cpp/c'
    sdk_lib = ROOT / 'microsoft.windows.sdk.cpp.x64/c'
    ffmpeg = packages / 'ffmpeginteropx.desktop.ffmpeg/8.1.2'
    sdk_tools = packages / 'microsoft.windows.sdk.buildtools/10.0.26100.9169/bin/10.0.26100.0/x64'
    metadata = packages / 'ffmpeginteropx.desktop.lib/2.1.0/runtimes/win-x64/native/FFmpegInteropX.winmd'
    ui_metadata = ROOT / 'microsoft.windowsappsdk/lib/uap10.0.17763'
    app_metadata = ROOT / 'microsoft.windowsappsdk/lib/uap10.0'
    windows_metadata = sdk / 'UnionMetadata/10.0.26100.0/Windows.winmd'
    env = os.environ.copy()
    env['PATH'] = str(compiler.parent) + os.pathsep + env['PATH']
    env['INCLUDE'] = ';'.join(map(str, [msvc / 'include', *(sdk / 'Include/10.0.26100.0' / folder for folder in ['ucrt', 'shared', 'um', 'winrt']), generated, source, ffmpeg / 'include']))
    env['LIB'] = ';'.join(map(str, [msvc / 'lib/x64', sdk_lib / 'um/x64', sdk_lib / 'ucrt/x64', ffmpeg / 'runtimes/win-x64/native']))

    def build(*arguments):
        run(arguments, cwd=source, env=env)

    cppwinrt = ROOT / 'microsoft.windows.cppwinrt/bin/cppwinrt.exe'
    build(cppwinrt, '-input', windows_metadata, '-input', ui_metadata, '-input', app_metadata, '-output', generated)
    build(cppwinrt, '-input', metadata, '-reference', windows_metadata, '-reference', ui_metadata, '-output', generated, '-component', '-optimize', '-name', 'FFmpegInteropX')
    flags = ['/nologo', '/utf-8', '/std:c++20', '/EHsc', '/MD', '/O2', '/bigobj', '/W3', '/DUNICODE', '/D_UNICODE', '/D_WINRT_DLL', '/DWin32', '/DNDEBUG', '/DWIN32', '/D_WINDOWS', '/D_WINDLL', '/DWINVER=0x0A00', '/D_WIN32_WINNT=0x0A00', '/wd4244', '/c', '/Fp' + str(out / 'pch.pch')]
    build(compiler, *flags, '/Ycpch.h', '/Fo' + str(out / 'pch.obj'), source / 'pch.cpp')
    project = ET.parse(source / 'FFmpegInteropX.vcxproj')
    ns = {'m': 'http://schemas.microsoft.com/developer/msbuild/2003'}
    sources = [source / item.attrib['Include'] for item in project.findall('.//m:ClCompile', ns) if 'Include' in item.attrib and item.attrib['Include'] != 'pch.cpp' and not item.attrib['Include'].startswith('$')]
    sources.append(generated / 'module.g.cpp')
    build(compiler, *flags, '/Yupch.h', '/MP4', '/Fo' + str(out) + os.sep, *sources)
    build(sdk_tools / 'rc.exe', '/nologo', '/d', 'LIB_VERSION_NUM=2,1,0,1', '/d', 'LIB_VERSION_STR="2.1.0.1"', '/fo', out / 'FFmpegInteropX.res', source / 'FFmpegInteropX.rc')
    objects = [out / (item.stem + '.obj') for item in [source / 'pch.cpp', *sources]]
    build(compiler.parent / 'link.exe', '/NOLOGO', '/DLL', '/MACHINE:X64', '/SUBSYSTEM:CONSOLE', '/OPT:REF', '/OPT:ICF', '/INCREMENTAL:NO', '/Brepro', '/DEF:' + str(source / 'FFmpegInteropX.def'), '/OUT:' + str(out / 'FFmpegInteropX.dll'), *objects, out / 'FFmpegInteropX.res',
          'kernel32.lib', 'user32.lib', 'advapi32.lib', 'shell32.lib', 'ole32.lib', 'oleaut32.lib', 'uuid.lib', 'shcore.lib', 'runtimeobject.lib', 'mfuuid.lib', 'dxguid.lib', 'd3d11.lib',
          'avcodec.lib', 'avdevice.lib', 'avfilter.lib', 'avformat.lib', 'avutil.lib', 'swresample.lib', 'swscale.lib')
    provenance = {'sourceCommit': lock['sourceCommit'], 'patchSha256': sha256(PATCH), 'scriptSha256': sha256(Path(__file__)), 'lockSha256': sha256(LOCK), 'decoderSha256': sha256(out / 'FFmpegInteropX.dll'), 'version': '2.1.0.1-fnmusic-mp3-preroll', 'msvcPayloads': lock['msvcPayloads'], 'nugetPackages': lock['nugetPackages'], 'restoredPackages': lock['restoredPackages']}
    (out / 'build-manifest.json').write_text(json.dumps(provenance, indent=2), encoding='utf-8')
    properties = ET.Element('Project')
    group = ET.SubElement(properties, 'PropertyGroup')
    for key in ['patchSha256', 'scriptSha256', 'lockSha256', 'decoderSha256']:
        ET.SubElement(group, 'FnMusicDecoder' + key[0].upper() + key[1:]).text = provenance[key]
    ET.ElementTree(properties).write(out / 'decoder.props', encoding='utf-8', xml_declaration=True)
    print('Built pinned native decoder: ' + provenance['decoderSha256'], flush=True)


if __name__ == '__main__':
    main()
