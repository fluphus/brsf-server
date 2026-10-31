#!/usr/bin/env python3
"""Check a source checkout before sharing it; never print matched secret content.

Usage: python tools/check-source-tree.py [root] [--forbid FRAGMENT ...]
The optional fragments are supplied privately, not stored in this checkout.
Ignored dependency/build directories are not part of the source review.
"""
import argparse
import os
from pathlib import Path
import re
import stat
import sys

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
