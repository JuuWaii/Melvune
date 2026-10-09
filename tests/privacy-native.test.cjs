'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { randomUUID } = require('node:crypto');

function scan(files) {
  const root = path.join(__dirname, '..', '.review-audit', 'privacy-tests', randomUUID());
  fs.mkdirSync(path.join(root, 'scripts'), { recursive: true });
  fs.copyFileSync(path.join(__dirname, '..', 'scripts', 'check-privacy.js'), path.join(root, 'scripts', 'check-privacy.js'));
  for (const [name, content] of Object.entries(files)) {
    const file = path.join(root, name);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, content);
  }
  return spawnSync(process.execPath, [path.join(root, 'scripts', 'check-privacy.js')], { encoding: 'utf8' });
}
test('native C# credentials are detected but not printed', () => {
  const value = 'q'.repeat(20);
  const result = scan({ 'native/src/Example.cs': 'string token = "' + value + '";' });
  assert.equal(result.status, 1);
  assert.ok(result.stdout.includes('native/src/Example.cs'));
  assert.ok(!result.stdout.includes(value));
});
test('scanner checks later address matches after an allowed loopback match', () => {
  const address = [203, 0, 113, 9].join('.');
  const result = scan({ 'native/src/Resources.resw': '<value>127.0.0.1 ' + address + '</value>' });
  assert.equal(result.status, 1);
  assert.ok(!result.stdout.includes(address));
});
test('C# cookie construction does not join separate lines into a secret', () => {
  const source = [
    'var header = "music-token=" + Escape(value);',
    'request.Headers.Accept.ParseAdd("application/json");'
  ].join('\n');
  assert.equal(scan({ 'native/src/Client.cs': source }).status, 0);
});
test('untracked native build outputs are excluded from source scan', () => {
  const value = 'q'.repeat(20);
  const result = scan({
    'native/src/App/bin/Generated.cs': 'string token = "' + value + '";',
    'native/src/App/Resources.resw': '<value>连接与设置</value>'
  });
  assert.equal(result.status, 0);
});

test('portable launcher and installer authoring secrets are detected and redacted', () => {
  const value = 'q'.repeat(24);
  for (const name of ['packaging/portable/Program.cs', 'packaging/Setup.wxs']) {
    const result = scan({ [name]: 'token = "' + value + '"' });
    assert.equal(result.status, 1);
    assert.ok(result.stdout.includes(name));
    assert.ok(!result.stdout.includes(value));
  }
});

test('portable build outputs stay excluded while source remains checked', () => {
  const value = 'q'.repeat(24);
  const result = scan({
    'packaging/portable/obj/Generated.cs': 'token = "' + value + '"',
    'packaging/portable/Program.cs': 'class Launcher {}'
  });
  assert.equal(result.status, 0);
});

test('signing private key containers are forbidden even without text secrets', () => {
  const result = scan({ 'packaging/signing.pfx': Buffer.from([0, 1, 2, 3]) });
  assert.equal(result.status, 1);
  assert.ok(result.stdout.includes('packaging/signing.pfx'));
});

test('fixed DLL metadata permits the version but still detects later addresses', () => {
  const version = [2, 1, 0, 1].join('.');
  const address = [203, 0, 113, 9].join('.');
  const name = 'docs/NATIVE_MP3_PREROLL.md';
  const metadata = '桥接 DLL 文件版本为 `' + version + '`';
  assert.equal(scan({ [name]: metadata }).status, 0);
  assert.equal(scan({ [name]: metadata + '\nhttps://' + address }).status, 1);
  assert.equal(scan({ [name]: metadata + '\nhttps://' + version }).status, 1);
});

test('fixed MSVC component numbers do not exempt the same value in an endpoint', () => {
  const version = [14, 44, 17, 14].join('.');
  const name = 'native/decoder/toolchain-lock.json';
  const component = 'Microsoft.VC.' + version + '.Tools.HostX64.TargetX64.base.vsix';
  assert.equal(scan({ [name]: JSON.stringify({ fileName: component }) }).status, 0);
  assert.equal(scan({ [name]: JSON.stringify({ fileName: component, endpoint: 'https://' + version }) }).status, 1);
});

test('preview decoder metadata exemptions reject the same digits in endpoints or other fields', () => {
  const version = [2, 1, 0, 1].join('.');
  const fixtures = {
    'docs/PREVIEW7_ACCEPTANCE.md': 'MP3 预滚 DLL 版本 `' + version + '`',
    'scripts/check-preview.ps1': "$decoder.version -ne '" + version + "-fnmusic-mp3-preroll'"
  };
  for (const [name, metadata] of Object.entries(fixtures)) {
    assert.equal(scan({ [name]: metadata }).status, 0);
    assert.equal(scan({ [name]: metadata + '\nhttps://' + version }).status, 1);
    assert.equal(scan({ [name]: metadata + '\nendpoint = "' + version + '"' }).status, 1);
  }
});

test('Python build inputs are scanned for credentials without printing values', () => {
  const value = 'q'.repeat(20);
  const result = scan({ 'scripts/build-native-decoder.py': 'api_key = "' + value + '"' });
  assert.equal(result.status, 1);
  assert.ok(result.stdout.includes('scripts/build-native-decoder.py'));
  assert.ok(!result.stdout.includes(value));
});
