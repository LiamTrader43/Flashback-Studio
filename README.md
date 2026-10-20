# Flashback-Studio

Tools for decompressing, converting, viewing and editing `.flb` clips recorded by [Luma3DS-Flashback](https://github.com/LiamTrader43/Luma3DS-Flashback), the 3DS "replay buffer" that keeps the last few seconds of gameplay and saves them on a button press.

`.flb` clips use Flashback's own compressed format, so video players, video editors and other tools (plain ffmpeg included) can't open them. **Use the converter here to turn them into normal videos.**

## What's included

| Tool | Status | What it does |
|---|---|---|
| `convert_flashback.bat` | Available (Windows) | Converts `.flb` clips into MP4 videos |
| 3DS app | Planned | View clips on the 3DS itself, trim them, and export them |

## Converting clips to MP4 (Windows)

### What you need

* **Windows.** The converter uses PowerShell, which comes with Windows. There's no macOS or Linux version yet.
* **[ffmpeg](https://ffmpeg.org/download.html).** Its `bin` folder must be on your PATH: typing `ffmpeg` in a terminal should run it.

### How to use it

1. Download `convert_flashback.bat` from this repository.
2. Copy your clips from the 3DS SD card (they're in `/luma/flashback/`, named `clip_<date>.flb`) to your PC.
3. Either:
   * **drag** one or more clips onto `convert_flashback.bat`, or
   * put `convert_flashback.bat` in the folder with your clips and **double-click** it to convert all of them.

Each clip becomes an MP4 next to it (`clip_<date>.mp4`): H.264 video at twice the clip's size with sharp, unblurred pixels, at the frame rate it was recorded at. It plays in any video player and imports into video editors like DaVinci Resolve.

Clips that already have an MP4 are skipped, so it's safe to run again on the same folder. Clips saved by older Flashback builds (a `.raw` file with a `.txt` next to it) convert the same way.

### If something goes wrong

| Message | What to do |
|---|---|
| `ffmpeg was not found` | Install ffmpeg and add its `bin` folder to PATH, then open a new window and try again. |
| `not a Flashback clip (or the copy is incomplete)`, `not a Flashback clip (file too small)` or `the file ends early` | The file was cut off, usually by removing the SD card or stopping the copy too early, or it isn't a Flashback clip. Copy the clip from the SD card again. |
| `made by a newer Flashback version` | Download the latest `convert_flashback.bat` from this repository. |
| `... is damaged` or `the clip's frame count doesn't match its index` | The clip file is corrupted. Copy it from the SD card again; if that doesn't help, the recording itself was damaged. |
| `ffmpeg failed` | ffmpeg's own message is shown just above it. The most common cause is a full disk. |
| `its .txt is missing` / `couldn't read the resolution or frame rate from its .txt` | Only for older `.raw` clips: copy the `.txt` with the same name from the SD card next to the `.raw`. |
| `Skipping ...: clip_<date>.mp4 already exists` | Already converted. Delete or rename the MP4 to convert the clip again. |

## The `.flb` clip format

A `.flb` file is Flashback's recording buffer saved as-is, which is why the 3DS can save a clip in a few seconds whatever its length:

* **Frames:** RGB565, top row first, each in a fixed-size slot, compressed losslessly with a small QOI-style format (or stored as-is when compression wouldn't make it smaller).
* **Index:** the clip's frames in order, with how many times each one repeats (missed frames are filled with copies of the previous one so clips play at the right speed).
* **Trailer:** the last 48 bytes, starting with the magic `FLBK` and holding the version, resolution, frame rate and frame count.

The format is documented next to `flashbackSaveClipFile` in [`sysmodules/rosalina/source/flashback.c`](https://github.com/LiamTrader43/Luma3DS-Flashback/blob/flashback/sysmodules/rosalina/source/flashback.c) of Luma3DS-Flashback, and the converter's decoder is a direct port of the 3DS code.

## License

GPLv3, like Luma3DS-Flashback. See [LICENSE](LICENSE).
