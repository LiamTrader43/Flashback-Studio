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

## Clips that store only what changes

Clips saved by Flashback v4.0 and newer, which stores only what changes, have 512 (`0x200`) added to their version as well. They can hold the bottom screen too (320x240, or 160x120 at half resolution): the trailer's last word is its width, plus its height times 65536, or 0 without it. They differ from the clips above in these ways:

* **Index entries are 24 bytes**, six 32-bit numbers, in every version: where the entry's frames are in the file, the size of the top screen's frame, the size of the bottom screen's, how many frames the entry is shown for, where it starts in the sound, and flags.
* **An entry holds what's new at one moment:** the top screen's frame, the bottom screen's, or both. The bottom screen's frame comes after the top screen's, from the next multiple of 4 bytes. A screen whose size is 0 is as it was after the entry before. When neither screen has changed, there's no entry: the one before is shown for one more frame.
* **A frame can be stored as what changed** since the frame before it on the same screen (flags `0x01` for the top screen's frame, `0x04` for the bottom screen's). It has one more operation than a whole frame, which leaves a run of pixels as they were, and its pixels come in the screen's own order (column by column, each from the bottom row up) instead of row by row. Reading it takes the screen's picture after the entry before, so a screen's picture is made from its last whole frame on. The recorder stores both screens whole at least every 30 entries. Until a screen's first whole frame, it's unknown, and readers show it black.
* **Huffman codes are flagged** (`0x02` for the top screen's frame, `0x08` for the bottom screen's), instead of told by the frame's first byte.
* **An entry shown for 0 frames is only read**, for what it leaves on the screens. Such entries come before a clip's first frame, and lead in to it: they go back to the last whole frame of each screen it's made from. One can also come anywhere after that, holding one screen's whole frame alone: when the two screens' frames of a moment don't fit in one entry, the recorder stores the whole frame of the screen that comes second (the bottom one, unless it was given priority) in an entry of its own, just before the entry with the other screen's frame.
* **Missed frames are counted:** the flags' top 16 bits are how many of the entry's frames had their capture missed (they're copies). The others after the first are frames where nothing had changed.

Compacting such a clip keeps every entry. Trimming one keeps, before the first frame kept, the entries that lead in to it: from the last one with the top screen whole, or with the bottom screen whole, whichever comes first.

The recorded clip format is documented in [`sysmodules/rosalina/source/flashback_clip.c`](https://github.com/LiamTrader43/Luma3DS-Flashback/blob/flashback/sysmodules/rosalina/source/flashback_clip.c) of Luma3DS-Flashback, and the frame compression next to `flashbackCompress` in [`flashback_compress.c`](https://github.com/LiamTrader43/Luma3DS-Flashback/blob/flashback/sysmodules/rosalina/source/flashback_compress.c). All three versions, and both additions to them, are described in [`3ds-app/source/flb.h`](../3ds-app/source/flb.h); the readers in the Windows app and the 3DS app follow those descriptions.
