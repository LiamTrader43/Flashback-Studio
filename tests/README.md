# Flashback Studio's PC tests

These test the app's own code on a PC: the clip reader and compactor
(`3ds-app/source/flb.c`), and the player's read-ahead thread (the "Reading
ahead" section of `3ds-app/source/main.c`). They also test the converter's code
(`windows-app/`). Run them after changing any of those:

```
python run_tests.py --cc "C:/Program Files/JetBrains/CLion 2025.2.4/bin/mingw/bin/gcc.exe"
```

They take about two minutes, and the runner lists which passed. If any failed,
it exits with 1.

- `--cc`: the C compiler. The default is `gcc`. On Windows, use a MinGW gcc,
  such as CLion's or MSYS2's.

You need Python 3 and a C compiler for the PC with pthreads. The converter test
also needs Windows and the [.NET SDK](https://dotnet.microsoft.com/download)
(without it, it's skipped), and ffmpeg on the PATH for its conversions.
Everything is built in `build/`, which git ignores.

## Clip reader and compactor test

`flb_test.c` builds `flb.c` as it is (it's plain C and stdio). It writes small
clips the way the recorder saves them, and varies them:
- with and without sound;
- with and without inputs;
- with copies of missed frames.

Then it compacts each clip, and trims it from the start, from the end, and at
random, sometimes part way through a frame's copies. It trims both the recorded
clip and the compacted one. Every frame of every copy must show the same
picture and the same inputs as in the original.

It also damages a clip's inputs in several ways, and the clip must still open
and play, without them.

## Read-ahead test

The runner copies the "Reading ahead" section, unchanged, into
`build/ahead_extracted.c`. The copy runs from the section's divider line up to
the Player section's. `ahead_test.c` includes it, with pthreads standing in for
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

## Converter test

`converter_test/ConverterTest.cs` is built with the converter's own clip reader,
input display and conversion code (`windows-app/FlbClip.cs`, `InputOverlay.cs`
and `Converter.cs`), and runs in `build/converter/`.

It writes clips of every version: as the recorder saves them, with the ring of
frames and the sound wrapping around, and as Studio compacts them. It varies
them:
- with and without sound;
- with and without inputs;
- with copies of missed frames;
- with some frames compressed, and some stored as they are.

Every frame, input and sound sample must read back as written. Clips from older
builds (`.raw` and `.txt`) are read too. Damaged clips must fail with the right
message, and a clip with damaged inputs must still read, without them.

The input display is checked against Studio's own. The runner copies main.c's
drawing code (from `// The input display:` to the end of `drawInputs`),
unchanged, into `build/overlay_extracted.c`, which `overlay_ref.c` includes.
For many inputs, the converter's display must match Studio's, but for the
rounding of the 3DS's 16-bit colours. So a change to one needs the same change
to the other.

With ffmpeg on the PATH, it also converts clips with sound and inputs, and
without them. Every frame of the MP4 must show the right picture, with the input
display if asked, and the sound must last as long as the clip. Stopping
part way, and a clip that turns out damaged part way, must leave the MP4 that
was there as it was, and nothing else behind.
