"""Verify package payloads after WiX extraction; never installs a product."""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--packages', type=pathlib.Path, required=True)
parser.add_argument('--payload', type=pathlib.Path, required=True)
parser.add_argument('--msi-source', type=pathlib.Path, required=True)
parser.add_argument('--msi-extracted', type=pathlib.Path, required=True)
parser.add_argument('--burn-extracted', type=pathlib.Path, required=True)
parser.add_argument('--burn-ba', type=pathlib.Path, required=True)
parser.add_argument('--report', type=pathlib.Path, required=True)
parser.add_argument('--version', default='0.2.0-rc.2')
parser.add_argument('--msi-version', default='0.1.1002')
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parent.parent
def digest(file):
    with file.open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest().upper()
expected = {p.relative_to(args.payload).as_posix(): digest(p) for p in args.payload.rglob('*') if p.is_file()}
portable_spec = importlib.util.spec_from_file_location('portable_verification', pathlib.Path(__file__).with_name('verify-portable-package.py'))
portable_verification = importlib.util.module_from_spec(portable_spec)
portable_spec.loader.exec_module(portable_verification)
portable_record = portable_verification.verify_portable(args.packages, args.payload, args.version)
payload_hash = portable_record['payloadZipSha256']
for name in ['WIX_NOTICE.md', 'WIX_LICENSE.txt']:
    expected['licenses/installer/' + name] = digest(root / 'packaging' / name)
ns = {'w': 'http://wixtoolset.org/schemas/v4/wxs'}
source = ET.parse(args.msi_source).getroot()
package = source.find('w:Package', ns)
assert package is not None and package.get('Scope') == 'perUser'
assert package.get('Version') == args.msi_version
folder_search = source.find('.//w:Property[@Id="INSTALLFOLDER"]/w:RegistrySearch', ns)
assert folder_search is not None and folder_search.get('Root') == 'HKCU'
assert folder_search.get('Key') == 'Software\\FnMusic\\Installer' and folder_search.get('Name') == 'InstallFolder'
assert source.find('.//w:RegistryValue[@Name="InstallFolder"]', ns).get('Value') == '[INSTALLFOLDER]'
assert source.find('.//w:Property[@Id="ARPPRODUCTICON"]', ns).get('Value') == 'FnMusicAppIcon'
assert source.find('.//w:Shortcut[@Id="FnMusicShortcut"]', ns).get('Icon') == 'FnMusicAppIcon'
ui_actions = {'WixUIValidatePath_X64', 'WixUIPrintEula_X64'}
actions = source.findall('.//w:CustomAction', ns)
assert all(e.get('Id') in ui_actions for e in actions), 'Unexpected installer custom action'
assert not source.findall('.//w:InstallExecuteSequence/w:Custom', ns), 'Unexpected execute-sequence custom action'
assert source.find('.//w:Dialog[@Id="InstallDirDlg"]', ns) is not None, 'MSI directory selection is missing'
assert source.find('.//w:Control[@Type="PathEdit"]', ns) is not None, 'MSI path editor is missing'
assert not source.findall('.//w:ServiceInstall', ns), 'Unexpected installed service'
assert all(e.get('Root') == 'HKCU' for e in source.findall('.//w:RegistryValue', ns)), 'Machine-wide registry write'
assert source.find('.//w:StandardDirectory[@Id="LocalAppDataFolder"]', ns) is not None
assert 'WINDOWSBUILDNUMBER >= 19041' in source.find('.//w:Launch', ns).get('Condition', '')
file_rows = {e.get('Id'): e for e in source.findall('.//w:File', ns)}
assert len(file_rows) == len(expected), 'MSI file count mismatch'
for relative, checksum in expected.items():
    file_id = 'F' + hashlib.sha256(relative.encode()).hexdigest()[:24]
    assert file_id in file_rows, 'Missing MSI file: ' + relative
    assert digest(args.msi_extracted / 'File' / file_id) == checksum, 'MSI payload differs: ' + relative
msi = args.packages / f'FnMusic-{args.version}-win-x64-setup.msi'
embedded = [p for p in args.burn_extracted.rglob('*') if p.is_file()]
assert any(digest(p) == digest(msi) for p in embedded), 'EXE does not contain the standalone MSI'
burn_ns = {'b': 'http://wixtoolset.org/schemas/v4/2008/Burn'}
burn = ET.parse(args.burn_ba / 'manifest.xml').getroot()
assert burn.find('b:Registration', burn_ns).get('PerMachine') == 'no'
assert burn.find('.//b:MsiPackage', burn_ns).get('PerMachine') == 'no'
assert burn.find('.//b:MsiProperty[@Id="INSTALLFOLDER"]', burn_ns).get('Value') == '[InstallFolder]'
install_folder = burn.find('b:Variable[@Id="InstallFolder"]', burn_ns)
assert install_folder is not None and install_folder.get('Type') == 'formatted', 'Default install path must be formatted'
assert install_folder.get('Persisted') == 'yes', 'Chosen install path must be persisted'
assert install_folder.get('Value') == '[LocalAppDataFolder]Programs\\FnMusic'
assert burn.find('b:Variable[@Id="WixStdBASuppressOptionsUI"]', burn_ns) is None, 'EXE directory options are suppressed'
theme = ET.parse(args.burn_ba / 'thm.xml').getroot()
theme_ns = {'t': 'http://wixtoolset.org/schemas/v4/thmutil'}
assert theme.find('.//t:Editbox[@Name="InstallFolder"]', theme_ns) is not None
assert theme.find('.//t:BrowseDirectoryAction[@VariableName="InstallFolder"]', theme_ns) is not None
for line in (args.packages / 'SHA256SUMS.txt').read_text().splitlines():
    checksum, name = line.split('  ', 1)
    assert pathlib.PurePath(name).name == name, 'Invalid checksum filename'
    assert digest(args.packages / name) == checksum, 'Distribution checksum mismatch'
record = dict(portableFiles=portable_record['portableFiles'], msiFiles=len(expected), msiScope=package.get('Scope'),
              uiCustomActions=len(actions), executeCustomActions=0, directorySelection=True,
              embeddedMsiMatches=True, payloadZipSha256=payload_hash,
              portableSha256=digest(args.packages / f'FnMusic-{args.version}-win-x64-portable.exe'),
              msiSha256=digest(msi), setupExeSha256=digest(args.packages / f'FnMusic-{args.version}-win-x64-setup.exe'))
args.report.parent.mkdir(parents=True, exist_ok=True)
args.report.write_text(json.dumps(record, indent=2) + '\n')
print(json.dumps(record))
