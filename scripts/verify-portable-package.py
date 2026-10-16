"""Verify a portable launcher and its complete payload without executing either."""
import argparse
import hashlib
import io
import json
import pathlib
import re
import stat
import struct
import zipfile

MAGIC = b'FNMUSIC-PAYLOAD-1'
TRAILER_LENGTH = len(MAGIC) + 32 + 8
VERSIONS = {'0.2.0-rc.1', '0.2.0-rc.2', '0.2.0-rc.3','0.2.0-rc.4', '0.2.0-rc.5', '0.2.0-rc.6', '0.2.0-rc.7'}
MAX_FILES = 10001
MAX_PAYLOAD = 8 * 1024 ** 3


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def linked(path):
    return path.is_symlink() or bool(getattr(path.lstat(), 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0))


def read_json(path, limit):
    require(path.is_file() and not linked(path), 'Missing or linked metadata file')
    require(path.stat().st_size <= limit, 'Metadata exceeds verification bound')
    return json.loads(path.read_text(encoding='utf-8-sig'))


def relative_name(value):
    require(isinstance(value, str), 'Invalid archive path type')
    value = value.replace('\\', '/')
    parts = value.split('/')
    require(value and all(part and part not in ('.', '..') for part in parts), 'Unsafe archive path')
    require(not any(ord(char) < 32 or char in ':\x7f' for char in value), 'Unsafe archive path character')
    for part in parts:
        require(not part.endswith((' ', '.')), 'Windows path alias found')
        require(not re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?', part), 'Windows device path found')
    return value


class PayloadStream(io.RawIOBase):
    """Give ZIP parsing a bounded view of the payload, excluding launcher/trailer."""
    def __init__(self, stream, offset, length):
        self.stream, self.offset, self.length, self.position = stream, offset, length, 0

    def seekable(self):
        return True

    def readable(self):
        return True

    def tell(self):
        return self.position

    def seek(self, offset, whence=io.SEEK_SET):
        target = offset if whence == io.SEEK_SET else self.position + offset if whence == io.SEEK_CUR else self.length + offset if whence == io.SEEK_END else -1
        require(0 <= target <= self.length, 'ZIP attempted to read outside payload')
        self.position = target
        return target

    def read(self, size=-1):
        count = self.length - self.position if size is None or size < 0 else min(size, self.length - self.position)
        self.stream.seek(self.offset + self.position)
        data = self.stream.read(count)
        require(len(data) == count, 'Truncated payload')
        self.position += len(data)
        return data


def verify_portable(packages, payload, version=None):
    packages, payload = pathlib.Path(packages).absolute(), pathlib.Path(payload).absolute()
    require(packages.is_dir() and not linked(packages) and payload.is_dir() and not linked(payload), 'Invalid or linked package directory')
    payload_release = read_json(payload / 'release.json', 1024 ** 2)
    version = version or payload_release.get('version')
    require(version in VERSIONS and payload_release.get('version') == version, 'Unexpected payload version')
    source = payload_release.get('sourceCommit', '')
    require(isinstance(source, str) and re.fullmatch('[a-f0-9]{40}', source), 'Invalid payload source commit')
    require(payload_release.get('architecture') == 'win-x64', 'Unexpected payload architecture')
    expected = {}
    case_names = set()
    for path in payload.rglob('*'):
        require(not linked(path), 'Payload contains a link')
        if path.is_file():
            relative = relative_name(path.relative_to(payload).as_posix())
            require(relative.casefold() not in case_names, 'Payload contains case aliases')
            case_names.add(relative.casefold())
            expected[relative] = digest(path)
    require(1 <= len(expected) <= MAX_FILES, 'Unexpected payload file count')
    manifest = read_json(payload / 'SHA256.json', 16 * 1024 ** 2)
    require(isinstance(manifest, list) and 1 <= len(manifest) < MAX_FILES, 'Invalid payload manifest')
    listed, listed_names = {}, set()
    for entry in manifest:
        require(isinstance(entry, dict), 'Invalid manifest row')
        name = relative_name(entry.get('path'))
        checksum = entry.get('sha256', '')
        require(isinstance(checksum, str) and re.fullmatch('[a-fA-F0-9]{64}', checksum), 'Invalid manifest digest')
        require(name.casefold() not in listed_names, 'Duplicate manifest path')
        listed_names.add(name.casefold())
        listed[name] = checksum.upper()
    require(listed == {name: checksum for name, checksum in expected.items() if name != 'SHA256.json'}, 'Payload manifest does not match every file')
    archive_path = pathlib.Path(str(payload) + '.zip')
    require(archive_path.is_file() and not linked(archive_path), 'Matching payload ZIP missing')
    archive_hash = digest(archive_path)
    sidecar = pathlib.Path(str(payload) + '.SHA256SUMS.txt')
    require(sidecar.is_file() and sidecar.stat().st_size < 4096 and not linked(sidecar), 'Payload ZIP checksum missing')
    require(sidecar.read_text(encoding='ascii').strip() == archive_hash + '  ' + archive_path.name, 'Payload ZIP checksum mismatch')
    portable = packages / f'FnMusic-{version}-win-x64-portable.exe'
    require(portable.is_file() and not linked(portable), 'Portable executable missing')
    with portable.open('rb') as stream:
        size = portable.stat().st_size
        require(size > TRAILER_LENGTH + 2, 'Portable executable is truncated')
        require(stream.read(2) == b'MZ', 'Portable launcher is not a Windows executable')
        stream.seek(size - TRAILER_LENGTH)
        require(stream.read(len(MAGIC)) == MAGIC, 'Portable trailer magic mismatch')
        payload_hash = stream.read(32).hex().upper()
        length = struct.unpack('<q', stream.read(8))[0]
        require(0 < length <= MAX_PAYLOAD and length < size - TRAILER_LENGTH, 'Invalid portable payload length')
        require(length == archive_path.stat().st_size, 'Portable payload length differs from ZIP')
        offset = size - TRAILER_LENGTH - length
        stream.seek(offset)
        computed, remaining = hashlib.sha256(), length
        while remaining:
            block = stream.read(min(1024 ** 2, remaining))
            require(block, 'Truncated portable payload')
            computed.update(block)
            remaining -= len(block)
        require(computed.hexdigest().upper() == payload_hash == archive_hash, 'Portable payload digest mismatch')
        with zipfile.ZipFile(PayloadStream(stream, offset, length)) as archive:
            rows = archive.infolist()
            require(1 <= len(rows) <= MAX_FILES * 2, 'Unexpected ZIP entry count')
            require(sum(row.file_size for row in rows) <= MAX_PAYLOAD, 'ZIP expansion exceeds bound')
            actual, paths, root = {}, set(), None
            for entry in rows:
                require(not entry.flag_bits & 1, 'Encrypted ZIP payload is unsupported')
                require(not stat.S_ISLNK(entry.external_attr >> 16), 'ZIP contains a symbolic link')
                name = relative_name(entry.filename.rstrip('/') if entry.is_dir() else entry.filename)
                require('/' in name or entry.is_dir(), 'ZIP payload must have one root directory')
                prefix, separator, relative = name.partition('/')
                root = root or prefix
                require(root == prefix, 'ZIP has inconsistent roots')
                require(name.casefold() not in paths, 'Duplicate ZIP path')
                paths.add(name.casefold())
                if entry.is_dir():
                    continue
                require(separator and relative, 'Invalid ZIP payload file')
                with archive.open(entry) as contents:
                    actual[relative] = hashlib.file_digest(contents, 'sha256').hexdigest().upper()
            # Reading each full entry above validates CRC, as well as its digest.
            require(actual == expected, 'Portable ZIP differs from verified payload directory')
    metadata = read_json(packages / 'release.json', 1024 ** 2)
    require(metadata.get('version') == version and metadata.get('payloadSource') == source, 'Portable version/source mismatch')
    require(metadata.get('payloadZipSha256', '').upper() == payload_hash, 'Portable metadata digest mismatch')
    require(isinstance(metadata.get('packagingSource'), str) and re.fullmatch('[a-f0-9]{40}', metadata['packagingSource']), 'Invalid packaging source commit')
    require(metadata.get('signed') is False, 'Unexpected package signature declaration')
    if metadata.get('packageKind') == 'portable-only':
        require(metadata.get('architecture') == 'win-x64' and metadata.get('artifacts') == [portable.name], 'Invalid portable-only artifact declaration')
        require('msiVersion' not in metadata and 'wixVersion' not in metadata, 'Portable-only metadata includes an installer')
        require({path.name for path in packages.iterdir()} == {portable.name, 'release.json', 'SHA256SUMS.txt'}, 'Unexpected portable-only output')
    else:
        require(version == '0.2.0-rc.2' and metadata.get('packageKind') in (None, 'windows-full'), 'Unsupported installer package identity')
    checksum_file = packages / 'SHA256SUMS.txt'
    require(checksum_file.is_file() and not linked(checksum_file) and checksum_file.stat().st_size < 1024 ** 2, 'Invalid distribution checksums')
    checksummed = set()
    for line in checksum_file.read_text(encoding='ascii').splitlines():
        checksum, separator, name = line.partition('  ')
        require(separator and re.fullmatch('[A-Fa-f0-9]{64}', checksum), 'Invalid distribution checksum row')
        require(relative_name(name) == name and '/' not in name, 'Unsafe checksum filename')
        require(name.casefold() not in checksummed and name != checksum_file.name, 'Duplicate or recursive checksum row')
        checksummed.add(name.casefold())
        path = packages / name
        require(path.is_file() and not linked(path) and digest(path) == checksum.upper(), 'Distribution checksum mismatch')
    require(checksummed == {path.name.casefold() for path in packages.iterdir() if path.name != checksum_file.name}, 'Distribution checksums do not cover every file')
    return dict(version=version, sourceCommit=source, packagingSource=metadata['packagingSource'], portableFiles=len(actual),
                zipEntries=len(rows), payloadZipSha256=payload_hash, portableSha256=digest(portable),
                trailerVerified=True, crcVerified=True, fileDigestsVerified=True, sourceMatched=True,
                portableOnly=metadata.get('packageKind') == 'portable-only')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packages', type=pathlib.Path, required=True)
    parser.add_argument('--payload', type=pathlib.Path, required=True)
    parser.add_argument('--version')
    parser.add_argument('--report', type=pathlib.Path, required=True)
    args = parser.parse_args()
    record = verify_portable(args.packages, args.payload, args.version)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(record))


if __name__ == '__main__':
    main()
