import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


versions = load('check-versions')
pointer = load('release-pointer')


class ReleaseTests(unittest.TestCase):
    def test_activate_and_rollback_leave_previous_tree_intact(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in ('old', 'new'):
                (root / name / 'en').mkdir(parents=True)
                (root / name / 'index.html').write_text(name)
                (root / name / 'en/index.html').write_text(name + '-en')
            current = root / 'current'
            pointer.activate(current, root / 'old')
            for name in ('new', 'old', 'new'):
                pointer.activate(current, root / name)
                self.assertEqual((current / 'index.html').read_text(), name)
                self.assertEqual((current / 'en/index.html').read_text(), name + '-en')
            self.assertEqual((root / 'old/index.html').read_text(), 'old')
            incomplete = root / 'incomplete'
            incomplete.mkdir()
            with self.assertRaises(ValueError):
                pointer.activate(current, incomplete)
            self.assertEqual(current.resolve(), (root / 'new').resolve())
            self.assertEqual(list(root.glob('*.next-*')), [])

    def test_real_directory_cannot_be_replaced(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'en').mkdir()
            (root / 'index.html').touch()
            (root / 'en/index.html').touch()
            with self.assertRaises(ValueError):
                pointer.activate(root, root)
            self.assertTrue(root.is_dir())

    def test_catalog_rejects_stale_version_and_external_download(self):
        project = {'id': 'demo', 'repo': 'https://github.com/leeeway/Demo', 'downloads': [
            {'version': '1.2.3', 'href': 'https://github.com/leeeway/Demo/releases/download/v1.2.3/app.zip'}]}
        self.assertEqual(versions.validate([project]), 1)
        project['downloads'][0]['version'] = '1.2.4'
        with self.assertRaises(ValueError):
            versions.validate([project])
        project['downloads'][0]['href'] = 'https://example.com/app.zip'
        with self.assertRaises(ValueError):
            versions.validate([project])

    def test_release_rejects_missing_assets_or_inconsistent_checksum(self):
        digest = 'a' * 64
        release = {'draft': False, 'assets': [{'name': 'app.zip', 'state': 'uploaded',
                    'size': 123, 'digest': 'sha256:' + digest}]}
        versions.verify_asset(release, 'app.zip', digest + '  app.zip\n')
        for name, sums in [('absent.zip', digest + '  app.zip'),
                           ('app.zip', digest + '  other.zip'),
                           ('app.zip', 'b' * 64 + '  app.zip')]:
            with self.assertRaises(ValueError):
                versions.verify_asset(release, name, sums)


if __name__ == '__main__':
    unittest.main()
