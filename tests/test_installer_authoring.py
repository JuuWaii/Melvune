"""Check installer authoring policy without building or installing a product."""
import pathlib
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


class InstallerAuthoringTests(unittest.TestCase):
    def test_independent_per_user_installer_and_directory_recovery(self):
        root = pathlib.Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory(prefix='melvune-installer-authoring-') as directory:
            base = pathlib.Path(directory)
            payload = base / 'payload'; payload.mkdir()
            (payload / 'Melvune.App.exe').write_bytes(b'MZ-synthetic')
            source = base / 'package.wxs'
            subprocess.run([sys.executable, str(root / 'scripts/create-installer-source.py'), str(payload), str(source)], check=True, capture_output=True)
            xml = ET.parse(source).getroot()
            ns = {'w': 'http://wixtoolset.org/schemas/v4/wxs', 'ui': 'http://wixtoolset.org/schemas/v4/wxs/ui'}
            package = xml.find('w:Package', ns)
            self.assertEqual(package.get('Scope'), 'perUser')
            self.assertEqual(package.get('UpgradeCode'), 'BA264589-54AE-4B53-BBE8-473AA08FC8ED')
            self.assertEqual(package.get('Version'), '0.2.1009')
            self.assertEqual(xml.find('.//w:Directory[@Id="INSTALLFOLDER"]', ns).get('Name'), 'Melvune')
            search = xml.find('.//w:Property[@Id="INSTALLFOLDER"]/w:RegistrySearch', ns)
            self.assertEqual(search.get('Key'), 'Software\\Melvune\\Installer')
            self.assertEqual(search.get('Root'), 'HKCU')
            self.assertTrue(all(v.get('Root') == 'HKCU' for v in xml.findall('.//w:RegistryValue', ns)))
            self.assertEqual(xml.find('.//ui:WixUI', ns).get('InstallDirectory'), 'INSTALLFOLDER')
            self.assertEqual(len(xml.findall('.//w:File', ns)), 3)
            self.assertFalse(xml.findall('.//w:RemoveFile', ns))
            self.assertFalse(xml.findall('.//w:CustomAction', ns))
            self.assertNotIn('Melvune.Native', source.read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
