# Flashback Studio's PC test

This tests the player's read-ahead thread on a PC. The thread is the "Reading
ahead" section of `3ds-app/source/main.c`. Run the test after changing that
section:

```
python run_tests.py --cc "C:/Program Files/JetBrains/CLion 2025.2.4/bin/mingw/bin/gcc.exe"
```

It takes about a minute and a half. If the test failed, it exits with 1.

- `--cc`: the C compiler. The default is `gcc`. On Windows, use a MinGW gcc,
  such as CLion's or MSYS2's.

You need Python 3 and a C compiler for the PC with pthreads.

The runner copies the section, unchanged, into `build/ahead_extracted.c`
(which git ignores). The copy runs from the section's divider line up to the
Player section's. `ahead_test.c` includes it, with pthreads standing in for
libctru, and a stand-in decoder that takes a random while.

A simulated player then uses the clip at random. It plays (sometimes skipping a
frame, as at 2x), jumps and scrubs, pauses, changes the trimmed part and the
loop, and waits a while now and then. Some clips have a damaged frame. The test
checks:
- every frame the player gets is the one it asked for;
- a frame doesn't change while the player draws it;
- a damaged frame comes back as an error (only when the player reaches it), and
  never as a hang;
- stopping closes the clip;
- in steady play, reading ahead keeps up: once the first frames are read, the
  player never waits.

A hang (no progress for 10 s) fails the test.

A new libctru call in that section needs a stand-in in `ahead_test.c`, and the
test won't build until it has one.
