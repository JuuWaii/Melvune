"""Redacted, read-only audit of tracked/worktree files and reachable Git blobs.

Findings are review candidates, never a claim that pattern scanning proves safety.
Only locations, category and a short fingerprint are emitted, not matched values.
"""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent.parent
GIT = ['git', '-c', 'safe.directory=' + ROOT.as_posix()]
PATTERNS = {
    'private-key': rb'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----',
    'service-token': rb'\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[A-Z0-9]{16}|sk-[A-Za-z0-9_-]{32,})\b',
    'embedded-url-credential': rb'https?://[^\s/:]+:[^\s/@]+@',
    'assigned-credential': rb'(?i)(?:token|secret|password|passwd|api[_-]?key|authorization|sessionid|cookie)\s*[:=]\s*([\x22\x27])[^\x22\x27\r\n]{8,}\1',
    'private-address': rb'\b(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3})\b',
    'personal-connect-domain': rb'(?i)\b(?:[a-z0-9-]+\.)+(?:fnos\.net|5ddd\.com|trzznas\.com)\b',
    'windows-profile-path': rb'(?i)[A-Z]:[\\/]+Users[\\/]+[^\\/\s\x22\x27]+',
    'email': rb'\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b',
}
FORBIDDEN = re.compile(r'(^|/)(dev\.config\.json|settings\.json|session[^/]*\.(json|bin)|.*\.(pfx|p12|pem|key))$|^(\.review-audit|\.tools|\.chrome-debug)/', re.I)
# Exact reviewed synthetic fixtures, including removed historical tests. No
# blanket exception for a test file: a different value remains a blocker.
REVIEWED_FIXTURES = {
    ('tests/nas-browser-probe.test.cjs', 'assigned-credential', 'c2a1e943e897abe2'),
    ('scripts/test-headless.js', 'assigned-credential', '31773c851b2d9f01'),
    ('scripts/test-headless.js', 'personal-connect-domain', '24619caa7ecbe974'),
}
def git(*args):
    return subprocess.check_output([*GIT, *args], cwd=ROOT)
def scan(content, location, scope, findings):
    if len(content) > 8 * 1024 * 1024 or b'\0' in content[:8192]: return
    for category, pattern in PATTERNS.items():
        for match in re.finditer(pattern, content):
            value = match.group()
            # Documentation's single neutral example and reserved sample identities.
            if category == 'personal-connect-domain' and value.lower().startswith(b'xxxx.'): continue
            if category == 'windows-profile-path' and re.search(rb'(?:<[^>]+>|%[^%]+%|example|user)[\\/]?$', value, re.I): continue
            findings.append(dict(scope=scope, location=location, line=content[:match.start()].count(b'\n') + 1,
                                 category=category, fingerprint=hashlib.sha256(value).hexdigest()[:16]))
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--ref', default='HEAD')
    args = parser.parse_args()
    findings = []
    tracked = [p.decode('utf-8') for p in git('ls-files', '-z').split(b'\0') if p]
    extra = [p.decode('utf-8') for p in git('ls-files', '--others', '--exclude-standard', '-z').split(b'\0') if p]
    for name in tracked + extra:
        file = ROOT / name
        scope = 'tracked-worktree' if name in tracked else 'untracked-worktree'
        if FORBIDDEN.search(name): findings.append(dict(scope=scope, location=name, category='forbidden-file'))
        if file.is_file(): scan(file.read_bytes(), name, scope, findings)
    objects = git('rev-list', '--objects', args.ref).decode('utf-8').splitlines()
    count = 0
    batch = subprocess.Popen([*GIT, 'cat-file', '--batch'], cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    try:
        for item in objects:
            oid, _, name = item.partition(' ')
            batch.stdin.write((oid + '\n').encode()); batch.stdin.flush()
            header = batch.stdout.readline().split()
            size = int(header[2]); content = batch.stdout.read(size); batch.stdout.read(1)
            if header[1] == b'blob':
                count += 1
                location = oid[:12] + ':' + name
                if FORBIDDEN.search(name): findings.append(dict(scope='git-history', location=location, category='forbidden-file'))
                scan(content, location, 'git-history', findings)
            elif header[1] == b'commit':
                # Include commit messages and identity metadata. Emails need human review.
                scan(content, oid[:12] + ':commit', 'git-metadata', findings)
                for email in re.findall(rb'(?m)^(?:author|committer) .*? <([^>]+)>', content):
                    if not email.endswith(b'@users.noreply.github.com'):
                        findings.append(dict(scope='git-metadata', location=oid[:12] + ':commit', category='private-git-email',
                                             fingerprint=hashlib.sha256(email).hexdigest()[:16]))
    finally:
        batch.stdin.close(); batch.wait()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    blockers = []
    for finding in findings:
        file = finding['location'].split(':', 1)[-1] if finding['scope'] == 'git-history' else finding['location']
        reviewed = (file, finding['category'], finding.get('fingerprint')) in REVIEWED_FIXTURES
        finding['reviewedSyntheticFixture'] = reviewed
        # Email addresses include upstream copyright contacts and Git metadata;
        # these remain visible as redacted review candidates, not secret gates.
        if finding['category'] != 'email' and not reviewed:
            blockers.append(finding)
    summary = dict(ref=args.ref, trackedFiles=len(tracked), untrackedFiles=len(extra), historyBlobs=count,
                   blockerCount=len(blockers), findings=findings)
    args.output.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in summary.items() if k != 'findings'}))
    categories = {}
    for finding in findings: categories[finding['category']] = categories.get(finding['category'], 0) + 1
    print('Review candidates:', json.dumps(categories))
    return 1 if blockers else 0
if __name__ == '__main__':
    raise SystemExit(main())
