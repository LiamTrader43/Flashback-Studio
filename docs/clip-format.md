# The `.flb` clip format

A `.flb` clip saved by Flashback is its recording buffer saved as-is, which is why the 3DS can save a clip in a few seconds whatever its length:

* **Sound** (when the clip was recorded with audio): stereo 16-bit at the 3DS's 32,728 Hz, at the start of the file.
* **Frames:** RGB565, top row first, each in a fixed-size slot, compressed losslessly with a small QOI-style format (on a New 3DS, since Flashback v3.0, often packed further with Huffman codes), or stored as-is when compression wouldn't make it smaller.
* **Index:** the clip's frames in order, with how many times each one repeats (missed frames are filled with copies of the previous one so clips play at the right speed) and, with sound, where each frame starts in it.
* **Inputs** (in clips recorded since Flashback records them): the buttons held during each frame, and where the Circle Pad and the touch screen were. They come just before the index, and readers that don't know them skip them.
* **Trailer:** the last 48 bytes, starting with the magic `FLBK` and holding the format version, resolution, frame rate, frame count and where the index and inputs start. With sound, 32 bytes describing it come just before.

The trailer's version number belongs to the file format. It isn't the version of Flashback or of this app:

| Format version | Clip |
|---|---|
| 1 | Recorded without audio |
| 2 | Compacted, without audio |
| 3 | With audio, recorded or compacted |

Clips saved by Flashback v3.0 and newer, on any model, have 256 (`0x100`) added to their version, as their frames may be packed with Huffman codes. Readers that don't know the codes then turn them down instead of showing garbled frames.

A compacted clip has the same frames back to back, with no unused space, and each index entry gives its frame's position in the file instead of its slot.

The recorded clip format is documented in [`sysmodules/rosalina/source/flashback_clip.c`](https://github.com/LiamTrader43/Luma3DS-Flashback/blob/flashback/sysmodules/rosalina/source/flashback_clip.c) of Luma3DS-Flashback, and the frame compression next to `flashbackCompress` in [`flashback_compress.c`](https://github.com/LiamTrader43/Luma3DS-Flashback/blob/flashback/sysmodules/rosalina/source/flashback_compress.c). All three versions are described in [`3ds-app/source/flb.h`](../3ds-app/source/flb.h); the readers in the Windows app and the 3DS app follow those descriptions.
