#!/usr/bin/env python3
"""Check a source checkout before sharing it; never print matched secret content.

Usage: python tools/check-source-tree.py [root] [--forbid FRAGMENT ...]
The optional fragments are supplied privately, not stored in this checkout.
Ignored dependency/build directories are not part of the source review.
"""
import argparse
import hashlib
import os
from pathlib import Path
import re
import stat
import sys

# Reviewed binary differences and compiled Android network policy for the 2.5.0 client.
CLIENT_ARTIFACTS = {
    'client/patches/001.bsdiff': (774, 'f6800f77015117dd71e0dd95f882a1def953193e8c04a24ad77b78a6c85707c2'),
    'client/patches/002.bsdiff': (211, 'f6614090eb240fbddf0d4447d2cc64949966688af1737535ac7d5807cab2c547'),
    'client/patches/003.bsdiff': (214, '0e00a73e5926bc29fc984f97b279e18960da5ea5a30508a386afee94b516dcda'),
    'client/patches/004.bsdiff': (396924, '8cb9aee8e4d3dba71a16a7e994ec0d10a6e807c1a470900aa3fabaa14005dfe6'),
    'client/patches/005.bsdiff': (138150, '0125425234c6483c045ce6a383839ea903dba97543c4a674b08c4b3ee307f72c'),
    'client/patches/006.bsdiff': (57797, '58cce359fa7bddbb34e1a2c1f2e7bc7d7f9d7b08c5cacfa3c67f42829936e324'),
    'client/patches/007.bsdiff': (34522, '891e8152160966c1b91df939a44ec736e0c299cd57e63d9d6f988a5428e204d2'),
    'client/additions/008-network_security_config.axml': (528, 'ae5f4021658e10aff2baafb1801e8800683aa52e9bd7a0378655bff9bf47559d'),
    'client/patches/009.bsdiff': (38042, '1eece3fd2f9f2646df511e0862c819d64d6bba4ea13feb8e80c878f1e3b1bb6c'),
}

EXCLUDED = {'.git', 'bin', 'obj', 'node_modules', '__pycache__'}
FORBIDDEN_SUFFIXES = {
    '.apk', '.xapk', '.aab', '.zip', '.7z', '.rar', '.tar', '.gz', '.bms',
    '.db', '.sqlite', '.sqlite3', '.db3', '.pem', '.key', '.pfx', '.p12',
    '.keystore', '.jks', '.png', '.jpg', '.jpeg', '.webp', '.gif', '.dds',
    '.unity3d', '.bundle', '.assetbundle', '.bytes', '.dll', '.exe', '.pdb',
    '.bin', '.dat', '.so', '.dylib', '.jar', '.aar', '.mp3', '.ogg', '.wav',
    '.mp4', '.webm', '.ttf', '.woff', '.woff2', '.pdf', '.log', '.jsonl', '.ndjson',
}
PATTERNS = {
    'absolute Windows path': re.compile(r'(?i)\b[a-z]:[\\/](?:[^\s<>"\']+)'),
    'UNC path': re.compile(r'\\\\[A-Za-z0-9_.-]+\\[A-Za-z0-9_$.-]+'),
    'external staging path': re.compile(r'(?i)(?:\bwork[\\/]|outputs[\\/]brs[-]revival)'),
    'private key': re.compile(r'-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE' + r' KEY-----'),
    'device serial assignment': re.compile(r'(?i)(?:device[_-]?serial|adb[_-]?serial)\s*[:=]\s*["\']?[a-z0-9][a-z0-9_.:-]{7,}'),
    'large encoded payload': re.compile(r'[A-Za-z0-9+/]{4096,}={0,2}'),
}


def is_link_or_reparse(path):
    """lstat also catches Windows junctions, before walking or exclusions."""
    info = path.lstat()
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0x400)
    )


def private_filename(name):
    name = name.casefold()
    if (name == '.env' or name.startswith('.env.')) and name != '.env.example':
        return True
    if name.endswith(('.local.props', '.local.json')):
        return True
    if re.search(r'\.(?:db|sqlite3?)-(?:wal|shm|journal)$', name):
        return True
    return name.endswith('.json') and bool(re.search(
        r'(?:^|[._-])(?:credentials?|secrets?|sessions?|tokens?|logs?|state|account-export)(?:[._-]|$)', name
    ))


def scan(root, fragments):
    findings = []
    checked = 0
    if is_link_or_reparse(root):
        return checked, [(Path('.'), 'source root symlink or reparse point')]
    for folder, dirs, files in os.walk(root, followlinks=False):
        current = Path(folder)
        for name in list(dirs):
            child = current / name
            if is_link_or_reparse(child):
                findings.append((child.relative_to(root), 'directory symlink or reparse point'))
                dirs.remove(name)
            elif name in EXCLUDED:
                dirs.remove(name)
        for name in files:
            file = current / name
            relative = file.relative_to(root)
            checked += 1
            if is_link_or_reparse(file):
                findings.append((relative, 'file symlink or reparse point'))
                continue
            approved = CLIENT_ARTIFACTS.get(relative.as_posix())
            if approved is not None:
                length, digest = approved
                if file.stat().st_size != length or hashlib.sha256(file.read_bytes()).hexdigest() != digest:
                    findings.append((relative, 'client patch differs from reviewed version'))
                continue
            if any(part.lower() in {'masterjson', 'raw-assets', 'original-assets'} for part in relative.parts):
                findings.append((relative, 'original resource directory'))
            if file.suffix.lower() in FORBIDDEN_SUFFIXES or private_filename(name):
                findings.append((relative, 'non-source/private file type'))
            if re.search(r'(?i)(?:master(?:data)?|gamedata)\.json$', name):
                findings.append((relative, 'original master-data filename'))
            try:
                raw = file.read_bytes()
                content = raw.decode('utf-8-sig')
                if '\x00' in content:
                    raise UnicodeError('NUL byte')
            except (UnicodeError, OSError):
                findings.append((relative, 'binary or unreadable file'))
                continue
            for label, pattern in PATTERNS.items():
                if pattern.search(content):
                    findings.append((relative, label))
            path_and_content = str(relative).casefold() + '\n' + content.casefold()
            for index, fragment in enumerate(fragments, 1):
                if fragment.casefold() in path_and_content:
                    findings.append((relative, 'private forbidden fragment #' + str(index)))
    return checked, findings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', nargs='?', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--forbid', action='append', default=[], metavar='FRAGMENT')
    args = parser.parse_args()
    root = args.root.absolute()
    if not root.is_dir():
        parser.error('root must be an existing directory')
    if any(not fragment for fragment in args.forbid):
        parser.error('forbidden fragments must not be empty')
    checked, findings = scan(root, args.forbid)
    for file, reason in findings:
        print(f'FAIL {file.as_posix()}: {reason}')
    print(f'Source scan: {checked} files, {len(findings)} findings.')
    return 1 if findings else 0


if __name__ == '__main__':
    sys.exit(main())
