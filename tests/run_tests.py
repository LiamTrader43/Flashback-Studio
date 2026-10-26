"""Runs Flashback Studio's PC tests (see README.md) against 3ds-app/source and
windows-app, and says which passed. Exits with 1 if any failed.

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
SOURCE = os.path.normpath(os.path.join(HERE, '..', '3ds-app', 'source'))
MAIN_C = os.path.join(SOURCE, 'main.c')
BUILD = os.path.join(HERE, 'build')
EXE = '.exe' if platform.system() == 'Windows' else ''


def extract_ahead():
    """The "Reading ahead" section of main.c, from its divider up to the
    Player section's, for ahead_test.c."""
    src = open(MAIN_C, encoding='utf-8').read().replace('\r\n', '\n')
    start = src.rindex('// ----', 0, src.index('// Reading ahead\n'))
    end = src.rindex('// ----', 0, src.index('// Player\n', start))
    open(os.path.join(BUILD, 'ahead_extracted.c'), 'w', encoding='utf-8').write(src[start:end])


def extract_overlay():
    """main.c's input display, from its comment to the end of drawInputs, for
    overlay_ref.c."""
    src = open(MAIN_C, encoding='utf-8').read().replace('\r\n', '\n')
    start = src.index('// The input display:')
    end = src.index('\n}\n', src.index('static void drawInputs', start)) + 3
    open(os.path.join(BUILD, 'overlay_extracted.c'), 'w', encoding='utf-8').write(src[start:end])


def converter_test(cc):
    """Builds overlay_ref and the converter test, and runs it in build/converter/;
    returns whether it passed, or None if there's no .NET SDK to build it."""
    dotnet = shutil.which('dotnet')
    if dotnet is None:
        print('No .NET SDK ("dotnet") found: skipped (see README.md).')
        return None
    extract_overlay()
    ref = os.path.join(BUILD, 'overlay_ref' + EXE)
    p = subprocess.run([cc, '-O2', '-I', SOURCE, '-I', BUILD, '-o', ref, os.path.join(HERE, 'overlay_ref.c'), '-lm'],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        print(p.stdout.rstrip() or f'{cc} failed, with no output')
        return False
    out = os.path.join(BUILD, 'converter_test')
    p = subprocess.run([dotnet, 'build', os.path.join(HERE, 'converter_test', 'ConverterTest.csproj'), '-c', 'Release',
                        '-o', out, '-nologo', '-v', 'quiet'], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        print(p.stdout.rstrip() or 'dotnet build failed, with no output')
        return False
    work = os.path.join(BUILD, 'converter')
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    return subprocess.run([os.path.join(out, 'ConverterTest.exe'), ref], cwd=work).returncode == 0


def build_and_run(cc, name, sources, flags=(), libs=()):
    """Builds a test and runs it in build/; returns whether it passed."""
    exe = os.path.join(BUILD, name + EXE)
    p = subprocess.run([cc, '-O2', *flags, '-o', exe, *sources, *libs],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        print(p.stdout.rstrip() or f'{cc} failed, with no output')
        return False
    return subprocess.run([exe], cwd=BUILD).returncode == 0


def main():
    ap = argparse.ArgumentParser(description='Runs Flashback Studio\'s PC tests.')
    ap.add_argument('--cc', default='gcc')
    args = ap.parse_args()
    cc = shutil.which(args.cc)
    if cc is None:
        sys.exit(f'No C compiler "{args.cc}" found: pass one with --cc (see README.md).')
    # (A MinGW gcc needs its own folder on the PATH to find its DLLs, and so
    # do the programs it builds.)
    os.environ['PATH'] = os.path.dirname(cc) + os.pathsep + os.environ.get('PATH', '')
    os.makedirs(BUILD, exist_ok=True)

    results = []
    print('=== clip reader and compactor test', flush=True)
    results.append(('clip reader and compactor test',
                    build_and_run(cc, 'flb_test', [os.path.join(HERE, 'flb_test.c'), os.path.join(SOURCE, 'flb.c')],
                                  ['-I', SOURCE])))
    print('\n=== read-ahead test', flush=True)
    extract_ahead()
    results.append(('read-ahead test', build_and_run(cc, 'ahead_test', [os.path.join(HERE, 'ahead_test.c')],
                                                     ['-I', BUILD], ['-lpthread'])))
    print('\n=== converter test', flush=True)
    results.append(('converter test', converter_test(cc)))

    print('\n=== summary')
    for name, ok in results:
        print(f'  {"passed" if ok else "skipped" if ok is None else "FAILED"}  {name}')
    failed = [n for n, ok in results if ok is False]
    skipped = [n for n, ok in results if ok is None]
    print(f'{len(failed)} FAILED' if failed else f'the rest passed ({len(skipped)} skipped)' if skipped else 'all passed')
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
