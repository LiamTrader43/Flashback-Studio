"""Runs Flashback Studio's PC test (see README.md) against 3ds-app/source/main.c.
Exits with 1 if it failed.

usage: python run_tests.py [--cc COMPILER]
  --cc  a C compiler for this PC (default: gcc); on Windows, a MinGW gcc
"""
import argparse
import os
import platform
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN_C = os.path.normpath(os.path.join(HERE, '..', '3ds-app', 'source', 'main.c'))
BUILD = os.path.join(HERE, 'build')
EXE = '.exe' if platform.system() == 'Windows' else ''


def extract_ahead():
    """The "Reading ahead" section of main.c, from its divider up to the
    Player section's, for ahead_test.c."""
    src = open(MAIN_C, encoding='utf-8').read().replace('\r\n', '\n')
    start = src.rindex('// ----', 0, src.index('// Reading ahead\n'))
    end = src.rindex('// ----', 0, src.index('// Player\n', start))
    open(os.path.join(BUILD, 'ahead_extracted.c'), 'w', encoding='utf-8').write(src[start:end])


def main():
    ap = argparse.ArgumentParser(description='Runs Flashback Studio\'s PC test.')
    ap.add_argument('--cc', default='gcc')
    args = ap.parse_args()
    cc = shutil.which(args.cc)
    if cc is None:
        sys.exit(f'No C compiler "{args.cc}" found: pass one with --cc (see README.md).')
    # (A MinGW gcc needs its own folder on the PATH to find its DLLs, and so
    # does the program it builds.)
    os.environ['PATH'] = os.path.dirname(cc) + os.pathsep + os.environ.get('PATH', '')
    os.makedirs(BUILD, exist_ok=True)

    extract_ahead()
    exe = os.path.join(BUILD, 'ahead_test' + EXE)
    p = subprocess.run([cc, '-O2', '-I', BUILD, '-o', exe, os.path.join(HERE, 'ahead_test.c'), '-lpthread'],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        print(p.stdout.rstrip() or f'{cc} failed, with no output')
        print('read-ahead test: FAILED (it didn\'t build)')
        sys.exit(1)
    ok = subprocess.run([exe]).returncode == 0
    print(f'read-ahead test: {"passed" if ok else "FAILED"}')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
