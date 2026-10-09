"""Generate WiX authoring from the already verified portable directory."""
import argparse
import hashlib
import pathlib
import xml.etree.ElementTree as ET
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('payload', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
args = parser.parse_args()
ns = 'http://wixtoolset.org/schemas/v4/wxs'
ET.register_namespace('', ns)
def node(parent, name, **attrs):
    return ET.SubElement(parent, '{' + ns + '}' + name, attrs)
def ident(prefix, relative):
    return prefix + hashlib.sha256(relative.encode()).hexdigest()[:24]
def component_guid(key):
    return str(uuid.uuid5(uuid.UUID('fd052535-9ef6-4949-9fd2-5f4162d8f9a8'), key)).upper()
root = ET.Element('{' + ns + '}Wix')
package = node(root, 'Package', Name='FnMusic 0.2.0-rc.1', Manufacturer='FnMusic contributors',
               Version='0.1.999', UpgradeCode='67547B43-CFA0-4C8C-8BF6-973F6567D305',
               ProductCode='DD474E6E-6605-4CDD-AC57-CDBB5C202001', Scope='perUser', InstallerVersion='500')
node(package, 'MajorUpgrade', DowngradeErrorMessage='A newer FnMusic version is already installed.')
node(package, 'MediaTemplate', EmbedCab='yes', CompressionLevel='high')
build_property = node(package, 'Property', Id='WINDOWSBUILDNUMBER', Secure='yes')
node(build_property, 'RegistrySearch', Id='ActualWindowsBuild', Root='HKLM', Key='SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion', Name='CurrentBuildNumber', Type='raw', Bitness='always64')
node(package, 'Launch', Condition='Installed OR (VersionNT64 AND WINDOWSBUILDNUMBER >= 19041)', Message='Windows 10 version 2004 or later (x64) is required.')
local = node(package, 'StandardDirectory', Id='LocalAppDataFolder')
programs = node(local, 'Directory', Id='UserPrograms', Name='Programs')
install = node(programs, 'Directory', Id='INSTALLFOLDER', Name='FnMusic')
directories = {'.': install}
feature = node(package, 'Feature', Id='Main', Title='FnMusic', Level='1')
files = [(file, file.relative_to(args.payload).as_posix()) for file in sorted(args.payload.rglob('*')) if file.is_file()]
for name in ['WIX_NOTICE.md', 'WIX_LICENSE.txt']:
    files.append((pathlib.Path(__file__).resolve().parent.parent / 'packaging' / name, 'licenses/installer/' + name))
for file, relative in files:
    if file.is_symlink(): raise ValueError('Symlinks are forbidden')
    if not file.is_file(): continue
    parent = pathlib.PurePosixPath(relative).parent
    for ancestor in reversed([parent, *parent.parents]):
        key = str(ancestor)
        if key not in directories:
            directories[key] = node(directories[str(ancestor.parent)], 'Directory', Id=ident('D', key), Name=ancestor.name)
    component_id = ident('C', relative)
    component = node(directories[str(parent)], 'Component', Id=component_id, Guid=component_guid('file:' + relative), Bitness='always64')
    # Per-user registry keypaths avoid ICE38/ICE64 and support repair.
    node(component, 'RegistryValue', Root='HKCU', Key='Software\\FnMusic\\Installer\\Files', Name=component_id, Type='integer', Value='1', KeyPath='yes')
    node(component, 'File', Id=ident('F', relative), Source=str(file.resolve()), Name=file.name)
    node(feature, 'ComponentRef', Id=component_id)
menu = node(package, 'StandardDirectory', Id='ProgramMenuFolder')
component = node(menu, 'Component', Id='StartMenuShortcut', Guid=component_guid('shortcut'), Bitness='always64')
node(component, 'Shortcut', Id='FnMusicShortcut', Name='FnMusic', Target='[INSTALLFOLDER]FnMusic.App.exe', WorkingDirectory='INSTALLFOLDER')
node(component, 'RegistryValue', Root='HKCU', Key='Software\\FnMusic\\Installer', Name='StartMenu', Type='integer', Value='1', KeyPath='yes')
node(feature, 'ComponentRef', Id='StartMenuShortcut')
# Explicitly remove empty app directories, never the separate user configuration.
for key, directory in directories.items():
    component_id = ident('R', key)
    component = node(directory, 'Component', Id=component_id, Guid=component_guid('directory:' + key), Bitness='always64')
    node(component, 'RegistryValue', Root='HKCU', Key='Software\\FnMusic\\Installer\\Directories', Name=component_id, Type='integer', Value='1', KeyPath='yes')
    node(component, 'RemoveFolder', Id=ident('E', key), On='uninstall')
    node(feature, 'ComponentRef', Id=component_id)
ET.indent(root)
args.output.parent.mkdir(parents=True, exist_ok=True)
ET.ElementTree(root).write(args.output, encoding='utf-8', xml_declaration=True)
print('Authored', len(files), 'payload and installer notice files')
