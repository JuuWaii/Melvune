"""Offline release regressions: no credentials, network, upload or EXE execution."""
import copy
import contextlib
import importlib.util
import io
import json
import pathlib
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('release_candidate', pathlib.Path(__file__).resolve().parents[1] / 'scripts/release-candidate.py')
RELEASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RELEASE)
SOURCE = 'a' * 40
RUN = 'https://github.com/JuuWaii/Melvune/actions/runs/123'


class FakeGitHub:
    def __init__(self, local):
        self.local = local
        self.release = None
        self.assets = []
        self.mutations = []
        self.tag = SOURCE
        self.corrupt_upload = False
        self.annotated = False

    def pages(self, endpoint):
        if endpoint.endswith('/assets'):
            return copy.deepcopy(self.assets)
        return [copy.deepcopy(self.release)] if self.release else []

    def call(self, endpoint, method='GET', data=None, file=None):
        if method != 'GET':
            self.mutations.append((method, endpoint))
        if '/git/ref/' in endpoint:
            return dict(object=dict(type='tag' if self.annotated else 'commit', sha=self.tag))
        if '/git/tags/' in endpoint:
            return dict(object=dict(type='commit', sha=self.tag))
        if endpoint.startswith('https://uploads.github.com/'):
            item = next(item for item in self.local if item['name'] == file.name)
            self.assets.append(dict(name=item['name'], size=item['size'], state='uploaded',
                                    digest='sha256:' + ('0' * 64 if self.corrupt_upload else item['sha256'])))
            return self.assets[-1]
        if method == 'POST':
            self.release = dict(data, id=7, html_url='https://github.com/JuuWaii/Melvune/releases/tag/' + RELEASE.TAG)
        if method == 'PATCH':
            self.release.update(data)
        return copy.deepcopy(self.release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='melvune-release-')
        self.directory = pathlib.Path(self.scratch.name)
        for name in RELEASE.CONTENT:
            (self.directory / name).write_text('synthetic release fixture ' + name, encoding='utf-8')
        self.metadata = dict(product='Melvune', version=RELEASE.VERSION, architecture='win-x64',
                             packageKind='zip-and-portable', signed=False, sourceCommit=SOURCE,
                             packagingSource=SOURCE, workflowRun=RUN,
                             artifacts=[RELEASE.record(self.directory / name) for name in sorted(RELEASE.CONTENT)])
        self.write_metadata()
        self.github = FakeGitHub(RELEASE.validate(self.directory, SOURCE, RUN))

    def tearDown(self):
        self.scratch.cleanup()

    def checksums(self):
        names = sorted(RELEASE.ASSETS - {'SHA256SUMS.txt'})
        (self.directory / 'SHA256SUMS.txt').write_text('\n'.join(RELEASE.digest(self.directory / name) + '  ' + name for name in names) + '\n')

    def write_metadata(self):
        (self.directory / 'release.json').write_text(json.dumps(self.metadata))
        self.checksums()

    def publish(self):
        with contextlib.redirect_stdout(io.StringIO()):
            return RELEASE.publish(self.directory, SOURCE, RUN, self.github)

    def test_complete_draft_is_published_only_after_uploads(self):
        self.publish()
        self.assertFalse(self.github.release['draft'])
        self.assertTrue(self.github.release['prerelease'])
        self.assertEqual(self.github.release['make_latest'], 'false')
        self.assertEqual(len(self.github.assets), 7)
        self.assertEqual(self.github.mutations[-1][0], 'PATCH')

    def test_annotated_tag_resolves_to_same_commit(self):
        self.github.annotated = True
        self.publish()

    def test_wrong_remote_tag_has_no_mutation(self):
        self.github.tag = 'b' * 40
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(self.github.mutations, [])

    def test_published_release_is_immutable(self):
        self.publish()
        mutations = len(self.github.mutations)
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(len(self.github.mutations), mutations)

    def test_matching_partial_draft_resumes_missing_assets_only(self):
        self.publish()
        self.github.release['draft'] = True
        self.github.assets.pop()
        self.github.mutations.clear()
        self.publish()
        self.assertEqual([m[0] for m in self.github.mutations], ['POST', 'PATCH'])

    def test_foreign_draft_is_rejected_without_mutation(self):
        self.github.release = dict(tag_name=RELEASE.TAG, draft=True, target_commitish='b' * 40)
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(self.github.mutations, [])

    def test_bad_uploaded_digest_stays_draft(self):
        self.github.corrupt_upload = True
        with self.assertRaises(ValueError):
            self.publish()
        self.assertTrue(self.github.release['draft'])
        self.assertFalse(any(m[0] == 'PATCH' for m in self.github.mutations))

    def test_unexpected_remote_asset_is_rejected(self):
        self.publish()
        self.github.release['draft'] = True
        self.github.assets.append(dict(name='private.json'))
        self.github.mutations.clear()
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(self.github.mutations, [])

    def test_changed_local_asset_fails_before_api(self):
        (self.directory / RELEASE.ZIP).write_bytes(b'changed')
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(self.github.mutations, [])

    def test_missing_checksum_row_fails(self):
        path = self.directory / 'SHA256SUMS.txt'
        path.write_text('\n'.join(path.read_text().splitlines()[1:]))
        with self.assertRaises(ValueError):
            self.publish()

    def test_extra_local_file_fails(self):
        (self.directory / 'private.json').write_text('fixture')
        with self.assertRaises(ValueError):
            self.publish()

    def test_mixed_source_fails_even_with_updated_checksums(self):
        self.metadata['packagingSource'] = 'b' * 40
        self.write_metadata()
        with self.assertRaises(ValueError):
            self.publish()

    def test_signed_declaration_is_rejected(self):
        self.metadata['signed'] = True
        self.write_metadata()
        with self.assertRaises(ValueError):
            self.publish()

    def test_stage_real_portable_fixture_is_cross_platform(self):
        spec = importlib.util.spec_from_file_location('portable_fixtures', pathlib.Path(RELEASE.__file__).with_name('test-portable-verification.py'))
        fixtures = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(fixtures)
        fixture = fixtures.PortableVerificationTests()
        fixture.setUp()
        try:
            target = fixture.root / RELEASE.ZIP.removesuffix('.zip')
            fixture.payload.rename(target)
            fixture.payload = target
            fixture.zip = pathlib.Path(str(target) + '.zip')
            fixture.create_package()
            destination = fixture.root / 'public-assets'
            root = pathlib.Path(RELEASE.__file__).resolve().parents[1]
            RELEASE.stage(root, fixture.payload, fixture.packages, destination, SOURCE, RUN)
            self.assertEqual(len(RELEASE.validate(destination, SOURCE, RUN)), 7)
        finally:
            fixture.tearDown()


if __name__ == '__main__':
    unittest.main()
