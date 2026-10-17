import datetime as dt
import importlib.util
import json
import pathlib
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('clean_previews', pathlib.Path(__file__).resolve().parents[1] / 'scripts/clean-previews.py')
CLEAN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CLEAN)


class CleanupTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='melvune-cleanup-fixture-')
        self.root = pathlib.Path(self.scratch.name)
        self.now = dt.datetime(2026, 10, 17, tzinfo=dt.timezone.utc)
        self.paths = []
        for days in [1, 2, 3, 5, 10, 20]:
            created = self.now - dt.timedelta(days=days)
            path = self.root / 'dist/previews' / (created.strftime('%Y%m%dT%H%M%SZ') + '-abcdef12')
            path.mkdir(parents=True)
            data = dict(product='Melvune', kind='development-preview', formatVersion=1, configuration='Debug', state='ready', createdUtc=created.isoformat())
            (path / 'preview.json').write_text(json.dumps(data))
            (path / 'Melvune.App.exe').write_bytes(b'MZ-synthetic-preview')
            self.paths.append(path)

    def tearDown(self):
        self.scratch.cleanup()

    def run_cleanup(self, apply=False, running=False):
        return CLEAN.clean(self.root, apply, self.now, lambda: running)

    def test_dry_run_keeps_all_files(self):
        result = self.run_cleanup()
        self.assertEqual(len(result['candidates']), 2)
        self.assertTrue(all(p.exists() for p in self.paths))

    def test_retains_latest_three_and_all_recent_previews(self):
        result = self.run_cleanup(True)
        self.assertEqual(len(result['removed']), 2)
        self.assertTrue(all(p.exists() for p in self.paths[:4]))

    def test_active_player_prevents_cleanup(self):
        self.assertEqual(self.run_cleanup(True, True)['removed'], [])

    def test_missing_marker_is_preserved(self):
        (self.paths[-1] / 'preview.json').unlink()
        self.run_cleanup(True)
        self.assertTrue(self.paths[-1].exists())

    def test_unknown_or_incomplete_markers_are_preserved(self):
        for value in ['building', 'unknown']:
            p = self.paths[-1] / 'preview.json'
            data = json.loads(p.read_text()); data['state'] = value; p.write_text(json.dumps(data))
            self.run_cleanup(True)
            self.assertTrue(self.paths[-1].exists())

    def test_release_and_unmarked_folders_are_never_candidates(self):
        p = self.root / 'dist/Melvune-0.2.0-rc.8-win-x64'; p.mkdir()
        q = self.root / 'dist/previews/unmarked'; q.mkdir()
        self.run_cleanup(True)
        self.assertTrue(p.exists() and q.exists())

    def test_timestamp_mismatch_is_preserved(self):
        p = self.paths[-1] / 'preview.json'; data = json.loads(p.read_text())
        data['createdUtc'] = self.now.isoformat(); p.write_text(json.dumps(data))
        self.run_cleanup(True)
        self.assertTrue(self.paths[-1].exists())


if __name__ == '__main__':
    unittest.main()
