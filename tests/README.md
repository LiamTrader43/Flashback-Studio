# Flashback Studio's PC tests

These test the app's own code on a PC: the clip reader and compactor
(`3ds-app/source/flb.c`), and the player's read-ahead threads (the "Reading
ahead" and "Reading the bottom screen ahead" sections of
`3ds-app/source/main.c`). They also test the Windows app's
code (`windows-app/`). Run them after changing any of those:

```
python run_tests.py --cc "C:/Program Files/JetBrains/CLion 2025.2.4/bin/mingw/bin/gcc.exe"
```

They take about two minutes, and the runner lists which passed. If any failed,
it exits with 1.

- `--cc`: the C compiler. The default is `gcc`. On Windows, use a MinGW gcc,
  such as CLion's or MSYS2's.

You need Python 3 and a C compiler for the PC with pthreads. The Windows app
test also needs Windows and the [.NET SDK](https://dotnet.microsoft.com/download)
(without it, it's skipped), and ffmpeg on the PATH for its exports.
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

Last, it does the same with a clip over 2 GB, as a long recording at full
resolution is (up to 3.6 GB): its frames, inputs and index are all past 2 GB.
It's a sparse file, so the gap before the frames takes no room on the disk. On
Windows, if the drive can't hold sparse files (FAT32 or exFAT), it's left out.

## Huffman codes test

`codes_test.c` checks `flb.c`'s decoding of frames packed with Huffman codes,
as a New 3DS records them. It makes its frames with a copy of the recorder's
own compression (`recorder_copy.h`, from Luma3DS-Flashback's
`flashback_compress.c`), so keep that copy in step with the recorder:
- frames of several kinds, at both resolutions, are packed one after another,
  as recording does (each frame's codes come from the counts of the frame
  before), and every one must decode to exactly the frame;
- damaged packed frames (cut short, the wrong format, too many or too few
  bytes, codes that don't fit, a code too long) must fail, and with bits
  flipped at random, decoding must never write past the frame;
- clips with packed frames, as the recorder saves them (with the version flag,
  with and without sound), must open and play, and keep the flag and every
  frame when compacted and trimmed.

## Changed-only clips test

`screens_test.c` checks `flb.c` on the clips Flashback saves since it stores
only what changes (the version flag `0x200`: see `docs/clip-format.md`). It
makes up recordings, and saves each as a clip the way the recorder does, with
frames from the same copy of the recorder's compression:
- the top screen alone, or with the bottom screen, at small sizes and at the
  real ones;
- frames stored whole (as they are, compressed, or packed with Huffman codes)
  and as what changed since the frame before;
- a screen left out of an entry when it hasn't changed, no entry when neither
  has, and captures missed;
- both screens whole every so many entries, the bottom screen's sometimes late
  or lost (the next one is then whole, or what changed since the last one
  stored);
- entries before the first frame that are only read, to lead in to it, and
  entries after it with one screen's whole frame alone (the bottom screen's,
  or the top screen's), shown for no frame.

What every frame must show is worked out from the format's rules alone (a
screen is black until its first whole frame). The test then checks that:
- every frame reads back as that, in order and in any order, both screens
  together and each on its own, with its inputs, and the clip's missed frames
  are the ones its index counts;
- compacted, the clip keeps every entry as it was, back to back, with zeros
  between an entry's two frames, and shows the same;
- trimmed (the recorded clip, the compacted one, and a trimmed one again), it
  starts from the entry the format says, shows the same frames with their
  inputs and their sound, counts the missed frames that are left, and is the
  size foretold;
- frames of what changed that are damaged (cut short, bits flipped) are never
  written outside the frame, and damaged clips fail to open, or open and don't
  crash.

## Read-ahead test

The runner copies the "Reading ahead" section, unchanged, into
`build/ahead_extracted.c`. The copy runs from the section's divider line up to
the Player section's. `ahead_test.c` includes it, with pthreads standing in for
libctru, and a stand-in decoder that takes a random while. The copy also has
the next section, "Reading the bottom screen ahead". The stand-in clip has
frames of its bottom screen at random entries (most clips have one; some
don't), and the system core is refused in some sessions.

A simulated player then uses the clip at random. It plays (sometimes skipping a
frame, as at 2x), jumps and scrubs, pauses, changes the trimmed part and the
loop, puts the clip's bottom screen up and down (Y), and waits a while now and
then. Some clips have a damaged frame. The test checks:
- every frame the player gets is the one it asked for;
- while the bottom screen is up, every picture of it the player gets is the one
  shown at that frame (black before its first whole frame), and paused, it
  comes soon (playing, the player doesn't wait for it, and pictures that would
  be read too late to be shown are passed over);
- neither changes while the player draws it;
- a damaged frame comes back as an error (only when the player reaches it), and
  never as a hang;
- the bottom screen is read on the system core when the app may have some of
  its time (asked for once), and on the app core as well (or only);
- stopping closes the clip and ends both threads;
- in steady play, reading ahead keeps up: once the first frames are read, the
  player never waits, nor, with it up, is the bottom screen's picture late;
- a bottom screen reader slower than its pictures come (as on an Old 3DS: a
  picture every other frame, 2.5 frames to read one) still shows at least
  half of them (at most 80% can be), rather than reading each one too late;
  and two of them (on both cores) show 90% or more.

A hang (no progress for 10 s) fails the test.

A new libctru call in that section needs a stand-in in `ahead_test.c`, and the
test won't build until it has one.

## Windows app test

`windows_app_test/WindowsAppTest.cs` is built with the Windows app's own clip
reader, compactor, input display and export code (`windows-app/FlbClip.cs`,
`FlbRecording.cs`, `FlbCompactor.cs`, `InputOverlay.cs` and `Converter.cs`),
and runs in `build/windows_app/`.

It writes clips of every version: as the recorder saves them, with the ring of
frames and the sound wrapping around, and as Studio compacts them. It varies
them:
- with and without sound;
- with and without inputs;
- with copies of missed frames;
- with some frames compressed, and some stored as they are;
- with the version flag for Huffman codes, and at full resolution, some frames
  packed with them.

Every frame, input and sound sample must read back as written, and so must the
sound of part of a clip. Clips from older builds (`.raw` and `.txt`) are read
too. Damaged clips must fail with the right message (a damaged packed frame
too), and a clip with damaged inputs must still read, without them.

Compacting and trimming are checked against the 3DS app's: `compact_ref.c` does
them with `flb.c`. The test compacts clips like those, and trims them (from the
start, from the end and at random, sometimes part way through a frame's
copies), both ways, and the two must write exactly the same bytes. What's
refused must be refused with the same messages.

The input display is checked against the 3DS app's too. The runner copies
main.c's drawing code (from `// The input display:` to the end of
`drawInputs`), unchanged, into `build/overlay_extracted.c`, which
`overlay_ref.c` includes. For many inputs, the Windows app's display must match
the 3DS app's, but for the rounding of the 3DS's 16-bit colours.

The Windows app's display can also be moved, resized and given another colour
(by dragging it in the player). Moved and resized, it must draw exactly what it
draws where it starts on a frame as much bigger, and in a colour, each channel
exactly as it is in that much grey. Anywhere and at any size, it must stay on
the screen and on the frame; dragging it must too, and resizing it by a corner
must leave the opposite corner where it was. Its line in `settings.txt` must
read back as it was written, whatever the PC's language, and nonsense mustn't
be read.

So a change to `flb.c`'s compacting or to the input display needs the same
change in the Windows app.

With ffmpeg on the PATH, it also exports clips with sound and inputs, without
them, and a trimmed part, at twice their size with sharp pixels (scaled by the
app), and at other sizes and scalings (scaled by a second ffmpeg), some with
the input display moved, resized and in another colour. Every frame
of the MP4 must show the right picture (for ffmpeg's scaling, the picture as
ffmpeg scales it on its own, with its colour at half the size, as H.264 keeps
it), with the input display if asked, and the sound must last as long as the
frames. Stopping part way, and a clip that turns out damaged part way, both
ways, must leave the MP4 that was there as it was, and nothing else behind. If
the PC has an NVIDIA graphics card, exports encoded by it (NVENC) are checked
the same way.

Discord exports are checked too: their bitrates for clips of every length
(worked out without ffmpeg), and exports in two passes, with sharp pixels and
scaled by ffmpeg (with the GPU chosen: they're the CPU's anyway), each under
20 MB with its index first (so it plays while it downloads), and the progress
counting both passes. A clip of busy pictures must fit in much smaller limits
and come close to them, once after doing its second pass again; a limit it
can't get under must be refused after two more tries, and one too small to
try, at once. Stopping in either pass, and a damaged clip, must leave nothing
behind, x264's files from the first pass included.

Recordings (`RecordingTest.cs`, with `windows-app/FlbRecording.cs`): a
recording's parts, written as clips with pictures of their own (as recorded
and compacted, with sound and inputs), must read as one: every frame, from any
frame to any other, its part's picture; the inputs, one part's after another's;
and the sound, each part's for its frames, as many samples in all as the frames
last. Parts are found by their names (`<name>_part01.flb`, ...): a part on its
own isn't a recording, and a part missing ends one there. A part recorded at
another size is turned down, naming it. With ffmpeg, the whole recording, and
part of it across where a part starts, are exported as one MP4, and every frame
must show its part's picture, with the sound lasting as long.
