<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="3ds-app/assets/wordmark-dark-2472w.png">
    <img src="3ds-app/assets/wordmark-2472w.png" alt="Flashback Studio" width="600">
  </picture>
</p>

Tools for decompressing, converting, viewing and editing `.flb` clips recorded by [Luma3DS-Flashback](https://github.com/LiamTrader43/Luma3DS-Flashback), the 3DS "replay buffer" that keeps the last 10 seconds to 2 minutes of gameplay and saves them on a button press.

`.flb` clips use Flashback's own compressed format, so video players, video editors and other tools (plain ffmpeg included) can't open them. **Use the converter here to turn them into normal videos.**

## What's included

| Tool | Status | What it does |
|---|---|---|
| Flashback Converter | Available (Windows) | Converts `.flb` clips into MP4 videos, with the input display over them if you like |
| Flashback Studio (3DS app) | Available (`.3dsx` or `.cia`) | Plays clips on the 3DS itself, with sound, and trims, renames and compacts them (to about half their size) |

## Converting clips to MP4 (Windows)

### What you need

* **Windows 10 or 11.** The converter uses .NET Framework 4.8, which comes with them. There's no macOS or Linux version yet.
* That's all: the converter comes with [ffmpeg](https://ffmpeg.org), which does the video encoding.

### How to use it

1. Download `FlashbackConverter.zip` from the [latest release](https://github.com/LiamTrader43/Flashback-Studio/releases/latest), and extract it anywhere. Keep `ffmpeg.exe` next to `FlashbackConverter.exe`.
2. Copy your clips from the 3DS SD card (they're in `/luma/flashback/`, named `clip_<date>.flb`) to your PC.
3. Open `FlashbackConverter.exe`, and drag clips, or folders of clips, into its window (or use **Add clips…** and **Add folder…**). Dragging them onto `FlashbackConverter.exe` itself opens it with them.
4. Press **Convert**.

The list shows each clip's length, resolution and frame rate, and whether it has sound and inputs. Then, as it goes, how converting each one went.

Each clip becomes an MP4 next to it (`clip_<date>.mp4`): H.264 video at twice the clip's size with sharp, unblurred pixels, at the frame rate it was recorded at, with the game's sound as an AAC track if the clip was recorded with audio. It plays in any video player and imports into video editors like DaVinci Resolve.

**Input display.** With **Show the input display in the videos** ticked (it is to start with), clips recorded with their inputs show them in the video's bottom-left corner, just as Flashback Studio shows them: the buttons held during each frame, where the Circle Pad was, and where the touch screen was touched. Untick it for videos of just the game. Clips recorded before Flashback recorded the inputs convert without it.

Clips that already have an MP4 are skipped, so it's safe to add the same folder again. To convert them again (with the input display switched off, for example), tick **Replace MP4s that already exist**. **Stop** stops converting: the clip being converted keeps the MP4 it had, if any, and the clips already done keep their new ones.

Clips compacted or trimmed by the 3DS app, and clips saved by older Flashback builds (a `.raw` file with a `.txt` next to it), convert the same way.

### If something goes wrong

The Status column says why a clip can't be converted, or why converting it failed. Hold the mouse over a clip to see all of a long message.

| Message | What to do |
|---|---|
| `ffmpeg.exe wasn't found` | Extract the whole zip, and keep `ffmpeg.exe` next to `FlashbackConverter.exe`. Opening the converter from inside the zip, without extracting it, doesn't work. |
| Windows says "Windows protected your PC" | The converter isn't signed, so Windows doesn't know it. Click **More info**, then **Run anyway**. |
| `Not a Flashback clip (or the copy is incomplete)`, `Not a Flashback clip (file too small)` or `The file ends early` | The file was cut off, usually by removing the SD card or stopping the copy too early, or it isn't a Flashback clip. Copy the clip from the SD card again. |
| `Made by a newer Flashback version` | Download Flashback Converter from the [latest release](https://github.com/LiamTrader43/Flashback-Studio/releases/latest). |
| `... is damaged` or `The clip's frame count doesn't match its index` | The clip file is corrupted. Copy it from the SD card again; if that doesn't help, the recording itself was damaged. |
| `Failed: ffmpeg failed: ...` | ffmpeg's own message follows. The most common cause is a full disk. |
| `Its .txt is missing` / `Couldn't read the resolution or frame rate from its .txt` | Only for older `.raw` clips: copy the `.txt` with the same name from the SD card next to the `.raw`. |
| `Its MP4 already exists` | Already converted. Tick **Replace MP4s that already exist** to convert it again. |

### Building it

The converter is in [`windows-app/`](windows-app): a small Windows Forms app for .NET Framework 4.8. Build it on Windows with the [.NET SDK](https://dotnet.microsoft.com/download): `dotnet build windows-app -c Release` builds `windows-app/bin/Release/net48/FlashbackConverter.exe`. To convert with it, put `ffmpeg.exe` next to it, or have ffmpeg on your PATH.

## Watching clips on the 3DS

Flashback Studio is a homebrew app that lists the clips on your SD card and plays them on the top screen, right on the console.

### Installing it

It comes two ways; use either (or both: they're separate apps).

**As a `.3dsx`, from the Homebrew Launcher:**

1. Download `FlashbackStudio.3dsx` from the [latest release](https://github.com/LiamTrader43/Flashback-Studio/releases/latest) (or build it yourself, see below).
2. Copy it to the `/3ds/` folder on your SD card.
3. Start it from the Homebrew Launcher.

**As a `.cia`, on the HOME Menu:**

1. Download `FlashbackStudio.cia` from the [latest release](https://github.com/LiamTrader43/Flashback-Studio/releases/latest) (or build it yourself, see below).
2. Copy it to your SD card and install it with [FBI](https://github.com/Steveice10/FBI).
3. Start it from the HOME Menu, like any other app.

### Using it

The bottom screen shows the clips in `/luma/flashback/` as a grid, newest first: each one's first frame, with the time it was saved and its length under it. The selected clip's first frame shows full size on the top screen, and its resolution, frame rate, size, whether it has sound, and number of missed frames below the grid. Half resolution clips are shown at twice their size, so they fill the screen.

| Clip list | |
|---|---|
| D-Pad | Select a clip |
| Touch a clip | Select it (touch it again to play it) |
| A | Play the clip |
| X | Delete the clip (asks first) |
| R | Compact the clip (asks first; see below) |
| L | Rename the clip, with the 3DS keyboard (spaces become dashes; names a file can't have, or Windows can't copy, are refused) |
| SELECT | Trim the clip (see below) |
| Y | Refresh the list |
| START | Exit |

| Playing a clip | |
|---|---|
| A | Play/pause |
| Left/Right | Previous/next frame (pauses; hold to repeat) |
| L/R | Back/forward 1 second |
| Up/Down | Speed: 1/4x, 1/2x, 1x, 2x |
| X | Settings (see below) |
| Touch the bar | Jump to that point |
| SELECT | Trim (see below) |
| B | Back to the clip list |

X opens the player's settings on the bottom screen, while the clip carries on playing. Choose one with Up/Down, change it with A (or Left/Right), and close them with X or B. They stay as you left them until you exit the app.

| Setting | |
|---|---|
| Loop | Play the clip again from the start when it ends |
| Input display | Show what was pressed (see below) |
| Timing details | How long each frame takes to read and draw, and how close the sound is to the picture |

**Input display.** Clips recorded since Flashback records the inputs show them in the top screen's bottom-left corner, over the picture. It shows the buttons held during each frame in red (a quick tap shows too), where the Circle Pad was, and where the touch screen was touched. Clips recorded before then have no inputs to show.

Red marks under the progress bar show where the recorder missed frames (they're filled with copies of the frame before, so the clip plays at the right speed). On a missed frame, the player says so.

Clips recorded with audio play their sound at normal speed. It's muted while paused, while stepping through frames, while you touch the bar, and at the other speeds. The player shows which, below the time. The sound needs the DSP firmware dump `/3ds/dspfirm.cdc` on the SD card, like every homebrew app with sound. If you've used one before, you have it; otherwise make it with [DSP1](https://github.com/zoogie/DSP1). Without it, clips play silently.

While a clip plays, the app reads and decodes the next few frames ahead of time, so a slow moment reading the SD card doesn't hold up the picture.

**If playback stutters,** check that Flashback recording is off (Rosalina menu > Flashback recorder). While it's on, it records the app too, and its SD card writes compete with the app reading the clip. Timing details (in the settings, X) shows how long each frame takes to read and draw, and how many frames were skipped to keep up.

### Trimming clips

Trimming saves part of a clip as a new clip, with its sound and inputs; the original is kept. Press SELECT in the clip list (or while a clip plays), find where the part you want starts and press L, then where it ends and press R. The part that will be cut off is darkened on the bar, and the selection plays (and loops) on its own so you can check it. Press X to save it.

| Trimming | |
|---|---|
| L / R | Start / end the selection at this frame |
| A | Play/pause the selection |
| Left/Right | Previous/next frame |
| Up/Down | Forward/back 1 second |
| X | Save the selection as a new clip (asks first) |
| Y | Loop on/off |
| Touch the bar | Jump to that point |
| B or SELECT | Stop trimming |

The new clip is saved next to the original as `<name>-trim.flb` (or `-trim2`, and so on), already compacted, and selected in the list when it's done. Like compacting, it needs free space for the new clip, B cancels it, and nothing is left behind if it fails.

### Compacting clips

A clip is as big as the uncompressed footage (at full resolution with sound, about 362 MB for 60 seconds and 724 MB for 2 minutes), even though its frames are compressed: Flashback saves its recording buffer as-is so saving is instant, and that buffer has room for every frame uncompressed. Compacting rewrites the clip with just its frame data, typically to about half the size (one such clip became 159 MB). The video is exactly the same, but the clip copies to a PC faster (most noticeable over Wi-Fi) and takes less space.

Select a clip and press R. The details under the list show how big it will be. Compacting a 60-second clip takes about 20 seconds (a 2-minute one about twice as long), and B cancels it at any time. When it's done, the app shows the clip's new size and how long compacting took.

* **It needs free space** for the compacted copy (the app checks first): the copy is written next to the clip and only replaces it once it's complete and reads back correctly.
* **The clip is never at risk:** if compacting fails, is cancelled, or the console turns off part way, the clip is left as it was (the app tidies up any leftover files the next time it starts).
* **Compacted clips** play in the app and convert with Flashback Converter like any other clip, sound and inputs included (only the sound the clip's frames last is kept).

### Building it

Install [devkitPro](https://devkitpro.org/wiki/Getting_Started) with the 3DS tools (devkitARM and libctru), then run `make` in the `3ds-app` folder. It builds `FlashbackStudio.3dsx`.

`make cia` builds `FlashbackStudio.cia` too. It also needs [bannertool](https://github.com/carstene1ns/3ds-bannertool), which devkitPro doesn't include: put `bannertool` in devkitPro's `tools/bin` folder (next to `makerom`), or run `make cia BANNERTOOL=<path to bannertool>`.

The clip reader (`3ds-app/source/flb.c`) is plain C, so it also builds on a PC.

[`tests/`](tests) has PC tests that run the app's own code (the clip reader and compactor, and the player's read-ahead) and the converter's. Run them with `python tests/run_tests.py` (see [tests/README.md](tests/README.md) for what you need).

## The `.flb` clip format

A `.flb` clip saved by Flashback is its recording buffer saved as-is, which is why the 3DS can save a clip in a few seconds whatever its length:

* **Sound** (when the clip was recorded with audio): stereo 16-bit at the 3DS's 32,728 Hz, at the start of the file.
* **Frames:** RGB565, top row first, each in a fixed-size slot, compressed losslessly with a small QOI-style format (or stored as-is when compression wouldn't make it smaller).
* **Index:** the clip's frames in order, with how many times each one repeats (missed frames are filled with copies of the previous one so clips play at the right speed) and, with sound, where each frame starts in it.
* **Inputs** (in clips recorded since Flashback records them): the buttons held during each frame, and where the Circle Pad and the touch screen were. They come just before the index, and readers that don't know them skip them.
* **Trailer:** the last 48 bytes, starting with the magic `FLBK` and holding the format version, resolution, frame rate, frame count and where the index and inputs start. With sound, 32 bytes describing it come just before.

The trailer's version number belongs to the file format. It isn't the version of Flashback or of this app:

| Format version | Clip |
|---|---|
| 1 | Recorded without audio |
| 2 | Compacted, without audio |
| 3 | With audio, recorded or compacted |

A compacted clip has the same frames back to back, with no unused space, and each index entry gives its frame's position in the file instead of its slot.

The recorded clip format is documented in [`sysmodules/rosalina/source/flashback_clip.c`](https://github.com/LiamTrader43/Luma3DS-Flashback/blob/flashback/sysmodules/rosalina/source/flashback_clip.c) of Luma3DS-Flashback, and the frame compression next to `flashbackCompress` in [`flashback_compress.c`](https://github.com/LiamTrader43/Luma3DS-Flashback/blob/flashback/sysmodules/rosalina/source/flashback_compress.c). All three versions are described in [`3ds-app/source/flb.h`](3ds-app/source/flb.h); the decoders in the converter and the 3DS app follow those descriptions.

## License

GPLv3, like Luma3DS-Flashback. See [LICENSE](LICENSE).
