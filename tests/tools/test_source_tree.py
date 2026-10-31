"""Synthetic source sharing checks. Run: python tests/tools/test_source_tree.py"""
import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location(
    'source_scan', Path(__file__).resolve().parents[2] / 'tools' / 'check-source-tree.py'
)
scanner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scanner)


class SourceScanTests(unittest.TestCase):
    def test_private_filenames_in_ordinary_directories(self):
        names = [
            '.env', '.env.dev', '.ENV.production', '.env.example.local',
            'assets.local.props', 'runtime.local.json', 'account.db-journal',
            'account.sqlite-journal', 'account.sqlite3-journal', 'account.sqlite-wal',
            'account.db-shm', 'credentials.json', 'owner-credentials.json',
            'credentials-export.json', 'logs.json', 'request.log.json',
            'runtime-state.json', 'tokens.json', 'private-session.json',
            'run.log', 'requests.jsonl',
        ]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            ordinary = root / 'ordinary'
            ordinary.mkdir()
            for name in names:
                (ordinary / name).write_text('{}', encoding='utf-8')
            checked, findings = scanner.scan(root, [])
            self.assertEqual(checked, len(names))
            rejected = {path.name for path, _ in findings}
            for name in names:
                with self.subTest(name=name):
                    self.assertIn(name, rejected)

    def test_private_input_documentation_and_env_example_remain_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'PrivateRuntimeAssets.props').write_text(
                '<Project><PropertyGroup><PrivateAssetsDirectory>../private-assets</PrivateAssetsDirectory>'
                '</PropertyGroup></Project>', encoding='utf-8'
            )
            (root / 'README.md').write_text(
                'Supply credentials.json and an account database from a private directory outside the source tree.',
                encoding='utf-8'
            )
            (root / '.env.example').write_text('PRIVATE_ASSETS=../private-assets\n', encoding='utf-8')
            self.assertEqual(scanner.scan(root, [])[1], [])

    def test_forbidden_fragments_in_any_text_file_or_filename(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'README.md').write_text('fixture-forbidden-value', encoding='utf-8')
            (root / 'fixture-forbidden-value.txt').write_text('safe', encoding='utf-8')
            _, findings = scanner.scan(root, ['FIXTURE-FORBIDDEN-VALUE'])
            self.assertEqual(len(findings), 2)
            self.assertTrue(all(reason == 'private forbidden fragment #1' for _, reason in findings))

    @unittest.skipUnless(os.name == 'nt', 'Windows junction behavior')
    def test_windows_junction_is_rejected_before_exclusion_and_not_followed(self):
        import _winapi
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            external = root / 'external'
            source = root / 'source'
            external.mkdir()
            source.mkdir()
            (external / 'payload.txt').write_text('outside', encoding='utf-8')
            for name in ['ordinary', 'node_modules']:
                junction = source / name
                _winapi.CreateJunction(str(external), str(junction))
                try:
                    self.assertTrue(scanner.is_link_or_reparse(junction))
                    checked, findings = scanner.scan(source, [])
                    self.assertEqual(checked, 0)
                    self.assertEqual(len(findings), 1)
                    self.assertEqual(findings[0][0], Path(name))
                    self.assertIn('reparse', findings[0][1])
                    self.assertIn('source root', scanner.scan(junction, [])[1][0][1])
                finally:
                    # Remove only this known junction entry, never its target.
                    self.assertEqual(junction.parent, source)
                    self.assertTrue(scanner.is_link_or_reparse(junction))
                    os.rmdir(junction)
            self.assertEqual((external / 'payload.txt').read_text(encoding='utf-8'), 'outside')


if __name__ == '__main__':
    unittest.main()
