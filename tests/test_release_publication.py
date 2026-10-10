"""Offline checks for public release integrity gates; no GitHub writes."""
import importlib.util
import json
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('publisher', ROOT / 'scripts/publish-github-release.py')
publisher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(publisher)


class PublicationIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.prefix = 'FnMusic-0.2.0-rc.1-win-x64'
        self.packages = self.root / (self.prefix + '-windows')
        self.packages.mkdir()
        self.sha = 'a' * 40
        self.zip = self.root / (self.prefix + '.zip')
        self.zip.write_bytes(b'isolated release ZIP fixture')
        for suffix in ['-portable.exe', '-setup.exe', '-setup.msi']:
            (self.packages / (self.prefix + suffix)).write_bytes(b'isolated binary fixture')
        for name in ['WIX_LICENSE.txt', 'WIX_NOTICE.md']:
            (self.packages / name).write_text('license fixture')
        self.metadata = dict(version='0.2.0-rc.1', payloadSource=self.sha,
                             packagingSource=self.sha, payloadZipSha256=publisher.digest(self.zip))
        self.metadata_path = self.packages / 'release.json'
        self.metadata_path.write_text(json.dumps(self.metadata))
        self.package_sums = self.packages / 'SHA256SUMS.txt'
        self.refresh_checksums()

    def refresh_checksums(self):
        self.package_sums.write_text(''.join(publisher.digest(p) + '  ' + p.name + '\n'
                                            for p in sorted(self.packages.iterdir()) if p != self.package_sums))
        (self.root / (self.prefix + '.SHA256SUMS.txt')).write_text(publisher.digest(self.zip) + '  ' + self.zip.name + '\n')

    def verified(self):
        return publisher.verified_assets(self.root, 'v0.2.0-rc.1', self.sha)

    def remote(self):
        paths, sums = self.verified()
        return paths, sums, [dict(name=p.name, size=p.stat().st_size, state='uploaded',
                                 digest='sha256:' + sums[p.name]) for p in paths]

    def test_complete_assets_pass(self):
        paths, sums, assets = self.remote()
        self.assertEqual(9, len(paths))
        self.assertEqual(set(sums), publisher.verify_remote(assets, paths, sums, require_all=True))

    def test_changed_binary_is_rejected(self):
        (self.packages / (self.prefix + '-setup.exe')).write_bytes(b'changed payload')
        with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
            self.verified()

    def test_matching_checksum_cannot_hide_wrong_source(self):
        self.metadata['packagingSource'] = 'b' * 40
        self.metadata_path.write_text(json.dumps(self.metadata))
        self.refresh_checksums()
        with self.assertRaisesRegex(ValueError, 'sources do not match'):
            self.verified()

    def test_zip_must_match_metadata_even_after_rehash(self):
        self.zip.write_bytes(b'replaced ZIP fixture')
        self.refresh_checksums()
        with self.assertRaisesRegex(ValueError, 'ZIP does not match'):
            self.verified()

    def test_duplicate_checksum_entry_is_rejected(self):
        content = self.package_sums.read_text()
        self.package_sums.write_text(content + content.splitlines()[0] + '\n')
        with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
            self.verified()

    def test_partial_upload_cannot_be_published(self):
        paths, sums, assets = self.remote()
        with self.assertRaisesRegex(ValueError, 'incomplete'):
            publisher.verify_remote(assets[:-1], paths, sums, require_all=True)

    def test_corrupt_remote_digest_is_rejected(self):
        paths, sums, assets = self.remote()
        assets[0]['digest'] = 'sha256:' + '0' * 64
        with self.assertRaisesRegex(ValueError, 'digest mismatch'):
            publisher.verify_remote(assets, paths, sums, require_all=True)


if __name__ == '__main__':
    unittest.main()
