"""Scans the working tree for text that must not be in this repository: Vietnamese characters and a few reserved words.
Run from the repository root:  python eng/check-clean.py [extra-word ...]   (exit code 1 if anything is found)"""
import os
import re
import sys

SKIP_DIRS = {'bin', 'obj', '.git', '.work', 'artifacts', 'runtimes', 'TestResults', '.vs'}
BINARY = ('.dll', '.so', '.dylib', '.png', '.jpg', '.webp', '.mp4', '.webm', '.nupkg', '.snupkg', '.pdb')
# Vietnamese-specific letters and the tone-marked block U+1EA0..U+1EF9
VIETNAMESE = re.compile('[ĂăĐđƠơƯưẠ-ỹ]')


def main(words):
    pattern = re.compile('|'.join(re.escape(w) for w in words), re.IGNORECASE) if words else None
    found = 0
    for root, dirs, files in os.walk('.'):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            path = os.path.join(root, name)
            if pattern and pattern.search(name):
                print(f'{path}: file name')
                found += 1
            if name.lower().endswith(BINARY) or os.path.abspath(path) == os.path.abspath(__file__):
                continue
            try:
                text = open(path, encoding='utf-8').read()
            except (UnicodeDecodeError, OSError):
                continue
            for number, line in enumerate(text.split('\n'), 1):
                if VIETNAMESE.search(line) or (pattern and pattern.search(line)):
                    print(f'{path}:{number}: {line.strip()[:120]}'.encode('ascii', 'backslashreplace').decode())
                    found += 1
    print(f'{found} finding(s)')
    return 1 if found else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
