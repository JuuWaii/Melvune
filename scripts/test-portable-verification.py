"""Isolated corruption regressions for verify-portable-package.py; no EXE runs."""
import hashlib
import importlib.util
import json
import pathlib
import struct
import tempfile
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location('portable_verifier', pathlib.Path(__file__).with_name('verify-portable-package.py'))
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


class PortableVerificationTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='melvune-portable-verify-')
        self.root = pathlib.Path(self.scratch.name)
        self.payload, self.packages = self.root / 'payload', self.root / 'packages'
        self.payload.mkdir()
        self.packages.mkdir()
        self.version = '0.2.0-rc.8'
        self.source = 'a' * 40
        (self.payload / 'Melvune.App.exe').write_bytes(b'MZ-fixture-app')
        (self.payload / 'release.json').write_text(json.dumps(dict(product='Melvune', version=self.version, sourceCommit=self.source, architecture='win-x64')))
        self.write_manifest()
        self.portable = self.packages / f'Melvune-{self.version}-win-x64-portable.exe'
        self.zip = pathlib.Path(str(self.payload) + '.zip')
        self.create_package()

    def tearDown(self):
        self.scratch.cleanup()

    def create_package(self, extra=None):
        with zipfile.ZipFile(self.zip, 'w', compression=zipfile.ZIP_STORED) as archive:
            for path in self.payload.iterdir():
                archive.write(path, 'payload/' + path.name)
            if extra is not None:
                archive.writestr(extra, b'unsafe fixture')
        data = self.zip.read_bytes()
        checksum = hashlib.sha256(data).digest()
        pathlib.Path(str(self.payload) + '.SHA256SUMS.txt').write_text(checksum.hex().upper() + '  ' + self.zip.name + '\n')
        self.portable.write_bytes(b'MZ-fixture-launcher' + data + VERIFIER.MAGIC + checksum + struct.pack('<q', len(data)))
        metadata = dict(version=self.version, architecture='win-x64', packageKind='portable-only',
                        payloadSource=self.source, packagingSource=self.source, payloadZipSha256=checksum.hex().upper(),
                        signed=False, artifacts=[self.portable.name])
        product = json.loads((self.payload / 'release.json').read_text()).get('product')
        if product:
            metadata['product'] = product
        (self.packages / 'release.json').write_text(json.dumps(metadata))
        self.write_checksums()

    def write_checksums(self):
        rows = [VERIFIER.digest(path) + '  ' + path.name for path in self.packages.iterdir() if path.name != 'SHA256SUMS.txt']
        (self.packages / 'SHA256SUMS.txt').write_text('\n'.join(rows) + '\n')

    def write_manifest(self):
        rows = [dict(path=path.name, sha256=VERIFIER.digest(path))
                for path in self.payload.iterdir() if path.name != 'SHA256.json']
        (self.payload / 'SHA256.json').write_text(json.dumps(rows))

    def change_payload_release(self, update):
        path = self.payload / 'release.json'
        release = json.loads(path.read_text())
        update(release)
        path.write_text(json.dumps(release))
        self.write_manifest()
        self.create_package()

    def verify(self):
        return VERIFIER.verify_portable(self.packages, self.payload)

    def rejects(self):
        with self.assertRaises((ValueError, zipfile.BadZipFile, EOFError, OSError)):
            self.verify()

    def test_valid_payload(self):
        report = self.verify()
        self.assertEqual(report['portableFiles'], 3)
        self.assertTrue(report['crcVerified'] and report['sourceMatched'] and report['portableOnly'])

    def test_melvune_trailer_and_source_identity(self):
        report = self.verify()
        self.assertEqual(report['product'], 'Melvune')
        self.assertEqual(report['trailerFormat'], 'MELVUNE-PAYLOAD-1')
        self.assertEqual(report['sourceCommit'], report['packagingSource'])

    def test_mismatched_brand_is_rejected(self):
        metadata_path = self.packages / 'release.json'
        metadata = json.loads(metadata_path.read_text())
        metadata['product'] = 'Unsupported'
        metadata_path.write_text(json.dumps(metadata))
        self.write_checksums()
        self.rejects()

    def test_missing_payload_product_is_rejected(self):
        self.change_payload_release(lambda release: release.pop('product'))
        self.rejects()

    def test_legacy_payload_product_is_rejected(self):
        self.change_payload_release(lambda release: release.update(product='Unsupported'))
        self.rejects()

    def test_legacy_payload_version_is_rejected(self):
        self.change_payload_release(lambda release: release.update(version='0.2.0-rc.7'))
        self.rejects()

    def test_legacy_trailer_is_rejected_even_with_valid_checksums(self):
        data = self.portable.read_bytes()
        self.portable.write_bytes(data[:-57] + b'RETIRED-PAYLOAD-1' + data[-40:])
        self.write_checksums()
        self.rejects()

    def test_mixed_product_executables_are_rejected(self):
        (self.payload / 'Other.App.exe').write_bytes(b'MZ-other-app')
        self.write_manifest()
        self.create_package()
        self.rejects()

    def test_missing_product_executable_is_rejected(self):
        (self.payload / 'Melvune.App.exe').unlink()
        self.write_manifest()
        self.create_package()
        self.rejects()

    def test_packaging_source_mismatch_is_rejected(self):
        path = self.packages / 'release.json'
        metadata = json.loads(path.read_text())
        metadata['packagingSource'] = 'b' * 40
        path.write_text(json.dumps(metadata))
        self.write_checksums()
        self.rejects()

    def test_full_installer_metadata_is_rejected(self):
        path = self.packages / 'release.json'
        metadata = json.loads(path.read_text())
        metadata['packageKind'] = 'windows-full'
        path.write_text(json.dumps(metadata))
        self.write_checksums()
        self.rejects()

    def test_truncated_executable(self):
        self.portable.write_bytes(b'MZ')
        self.rejects()

    def test_bad_magic(self):
        data = bytearray(self.portable.read_bytes())
        data[-57] ^= 1
        self.portable.write_bytes(data)
        self.rejects()

    def test_negative_and_oversized_length(self):
        original = self.portable.read_bytes()
        for length in (-1, 0, len(original), VERIFIER.MAX_PAYLOAD + 1):
            with self.subTest(length=length):
                self.portable.write_bytes(original[:-8] + struct.pack('<q', length))
                self.rejects()

    def test_payload_digest_corruption(self):
        data = bytearray(self.portable.read_bytes())
        data[30] ^= 1
        self.portable.write_bytes(data)
        self.rejects()

    def test_escape_and_windows_alias_paths(self):
        for name in ('payload/../outside', 'payload/CON.txt', 'payload/C:/outside', 'payload/bad.', 'other/root', 'payload/MELVUNE.APP.EXE'):
            with self.subTest(name=name):
                self.create_package(extra=name)
                self.rejects()

    def test_crc_corruption_with_repaired_outer_digest(self):
        data = bytearray(self.zip.read_bytes())
        position = data.index(b'MZ-fixture-app')
        data[position] ^= 1
        self.zip.write_bytes(data)
        checksum = hashlib.sha256(data).digest()
        pathlib.Path(str(self.payload) + '.SHA256SUMS.txt').write_text(checksum.hex().upper() + '  ' + self.zip.name)
        self.portable.write_bytes(b'MZ-fixture-launcher' + data + VERIFIER.MAGIC + checksum + struct.pack('<q', len(data)))
        self.rejects()

    def test_manifest_missing_file(self):
        manifest = json.loads((self.payload / 'SHA256.json').read_text())
        (self.payload / 'SHA256.json').write_text(json.dumps(manifest[:-1]))
        self.create_package()
        self.rejects()

    def test_manifest_duplicate_case_alias(self):
        path = self.payload / 'SHA256.json'
        rows = json.loads(path.read_text())
        rows.append(dict(path=rows[0]['path'].upper(), sha256=rows[0]['sha256']))
        path.write_text(json.dumps(rows))
        self.create_package()
        self.rejects()

    def test_source_mismatch(self):
        path = self.packages / 'release.json'
        data = json.loads(path.read_text())
        data['payloadSource'] = 'c' * 40
        path.write_text(json.dumps(data))
        self.write_checksums()
        self.rejects()

    def test_installer_artifact_rejected(self):
        (self.packages / 'Melvune-setup.exe').write_bytes(b'MZ')
        self.write_checksums()
        self.rejects()

    def test_checksum_omission(self):
        (self.packages / 'SHA256SUMS.txt').write_text(VERIFIER.digest(self.portable) + '  ' + self.portable.name + '\n')
        self.rejects()

    def test_duplicate_checksum(self):
        path = self.packages / 'SHA256SUMS.txt'
        path.write_text(path.read_text() + path.read_text().splitlines()[0] + '\n')
        self.rejects()

    def test_zip_sidecar_does_not_accept_digest_substring(self):
        path = pathlib.Path(str(self.payload) + '.SHA256SUMS.txt')
        path.write_text('prefix ' + path.read_text())
        self.rejects()


if __name__ == '__main__':
    unittest.main()
