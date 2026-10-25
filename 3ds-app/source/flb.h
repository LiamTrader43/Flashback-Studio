// Reader for Flashback clip files (.flb), recorded by Luma3DS-Flashback, and
// the compactor that rewrites them smaller.
//
// Version 1 is what the recorder saves: its ring buffer file, renamed, with an
// index and trailer appended. The layout is documented next to
// flashbackSaveClipFile in Luma3DS-Flashback's sysmodules/rosalina/source/flashback_clip.c;
// keep this reader in step with it:
//
//   [ring slots]  slot i at offset i * slotStride: a compressed frame, or the
//                 raw frame if its stored size is slotBytes
//   [index]       entryCount x { u32 slot, u32 size, u32 copies }, oldest first
//   [trailer]     the last 48 bytes: magic "FLBK", version, width, height, fps,
//                 slotStride, slotBytes, entryCount, frameCount, indexOffset
//
// Each slot is big enough for an uncompressed frame, so a version 1 clip is as
// big as the uncompressed footage. Version 2 is a compacted clip (written by
// flbCompact*): the same frames back to back, oldest first, with no unused space:
//
//   [frames]      each frame's stored bytes
//   [index]       entryCount x { u32 offset, u32 size, u32 copies }, oldest first
//   [trailer]     as in version 1, with version 2 and slotStride 0
//
// Version 3 is a clip with audio, as recorded or compacted:
//
//   [audio]       stereo PCM16 samples (recorded: a ring at the start of the file)
//   [frames]      as in version 1 (recorded) or 2 (compacted)
//   [index]       entryCount x { u32 offset, u32 size, u32 copies, u32 audioPos }:
//                 audioPos is the audio sample at which the frame is first
//                 shown (each copy comes sampleRate / fps samples later)
//   [audio info]  32 bytes: { u32 sampleRate (mHz), u16 channels (2), u16 bits
//                 (16), u32 offset, u32 capacity, u32 first, u32 end, u32 missed,
//                 u32 full }: sample i is at offset + (i % capacity) * 4, and
//                 samples first .. end - 1 are held
//   [trailer]     as in version 1, with version 3 (slotStride 0 if compacted)
//
// All values little-endian. Frames are RGB565, top row first. Every frame is
// compressed on its own, so any frame can be decoded without the ones before it.
//
// Plain C and stdio only, so it also builds and is tested on a PC.

#pragma once

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>

#define FLB_TRAILER_SIZE 48

typedef struct FlbInfo {
    uint32_t version;       // 1: as recorded, 2: compacted, 3: with audio (either)
    uint32_t width, height, fps;
    uint32_t slotStride;    // bytes between ring slots (0 once compacted)
    uint32_t slotBytes;     // size of an uncompressed frame
    uint32_t entryCount;    // stored frames
    uint32_t frameCount;    // frames in the clip, copies included
    uint32_t indexOffset;   // where the index starts
    uint64_t fileSize;
    // Audio (version 3)
    bool hasAudio;
    uint32_t audioRateMhz;  // sample rate in millihertz
    uint32_t audioOffset, audioCapacity, audioFirst, audioEnd, audioMissed;
} FlbInfo;

typedef struct FlbEntry {
    uint32_t offset, size, copies;  // where the stored frame is in the file, its size, times shown
    uint32_t firstFrame;            // the clip frame this entry is first shown at
    uint32_t audioPos;              // the audio sample it's first shown at (with audio)
} FlbEntry;

typedef struct FlbClip {
    FILE *file;
    FlbInfo info;
    FlbEntry *entries;
    uint8_t *stored;        // one stored frame, as read from the file
} FlbClip;

// Reads and checks a clip's trailer. On failure returns false and points
// 'error' at a message.
bool flbReadInfo(FILE *file, FlbInfo *info, const char **error);

// Opens a clip and loads its index (the frames themselves are read by
// flbDecodeEntry). On failure returns false, points 'error' at a message
// (valid until the next call) and leaves nothing open.
bool flbOpen(FlbClip *clip, const char *path, const char **error);
void flbClose(FlbClip *clip);

// The index entry shown at clip frame 'frame' (0 .. frameCount - 1).
uint32_t flbEntryForFrame(const FlbClip *clip, uint32_t frame);

// Reads and decodes entry 'entry' into 'dst' (width * height pixels).
// On failure returns false and points 'error' at a message.
bool flbDecodeEntry(FlbClip *clip, uint32_t entry, uint16_t *dst, const char **error);

// Decompresses exactly 'pixels' pixels from exactly 'len' bytes. Returns false
// if the data is malformed (never writes past 'dst' + 'pixels').
bool flbDecompress(const uint8_t *src, uint32_t len, uint16_t *dst, uint32_t pixels);

// Compacted already (version 2, or 3 without slots)?
bool flbIsCompacted(const FlbInfo *info);

// How many audio samples a clip's frames last (its audio track starts at the
// first entry's audioPos).
uint32_t flbAudioSamples(const FlbInfo *info);

// Reads stereo PCM16 samples first .. first + count - 1 into 'dst'; samples the
// clip doesn't hold are silence. On failure returns false and sets 'error'.
bool flbReadAudio(FlbClip *clip, uint32_t first, uint32_t count, int16_t *dst, const char **error);

// The size of an open clip once compacted.
uint64_t flbCompactSize(const FlbClip *clip);

// The size of frames firstFrame .. firstFrame + frameCount - 1 of an open clip,
// trimmed (as a compacted clip).
uint64_t flbTrimSize(const FlbClip *clip, uint32_t firstFrame, uint32_t frameCount);

// Compacting and trimming: the clip's frames (all of them, or the ones kept)
// and their audio are copied into "<copy>.tmp", a few at a time (so the caller
// can show progress), which then replaces the clip (compacting) or becomes a
// new clip (trimming). The clip itself is only touched at the very end of a
// compaction, and is left as it was if anything fails or it's cancelled.
typedef struct FlbCompactor {
    FlbClip clip;
    FILE *out;
    char path[256];         // the clip
    char outPath[256];      // the copy: the clip itself when compacting
    char tmpPath[264];      // the copy, until it's finished
    uint32_t firstFrame, frameCount;    // the frames copied
    uint32_t firstEntry, endEntry;      // the index entries they use
    uint8_t *buf;           // the block of the copy being filled
    uint32_t bufLen;
    uint32_t *offsets;      // each copied entry's offset in the copy
    uint32_t next;          // the next entry to copy
    uint32_t audioStart, audioCount, audioDone, audioOffset;   // the audio track: copied after the frames
    uint64_t copied, total; // bytes copied so far, and in all
} FlbCompactor;

// Starts compacting the clip at 'path'. On failure returns false and points
// 'error' at a message.
bool flbCompactBegin(FlbCompactor *c, const char *path, const char **error);

// Starts copying frames firstFrame .. firstFrame + frameCount - 1 of the clip
// at 'path', with their audio, into a new clip at 'outPath' (which mustn't
// exist). On failure returns false and points 'error' at a message.
bool flbTrimBegin(FlbCompactor *c, const char *path, const char *outPath, uint32_t firstFrame,
                  uint32_t frameCount, const char **error);

// Copies the next frame. Returns 0 while there's more to do, 1 once the copy is
// finished (the clip replaced by its compacted copy, or the trimmed clip
// saved), or -1 on failure (with 'error' set; the copy is deleted and the clip
// left as it was). Either way, once it returns 1 or -1 the compactor is
// finished with.
int flbCompactStep(FlbCompactor *c, const char **error);

// Stops and deletes the copy; the clip is left as it was.
void flbCompactCancel(FlbCompactor *c);

// Cleans up after a compaction that was cut off (the console turned off, for
// example) while the copy was replacing the clip. 'path' is a file in the clip
// folder: if it's a leftover "<clip>.tmp" or "<clip>.old", it's deleted, or
// renamed back to the clip if the clip itself is missing. Returns true if
// 'path' was such a leftover.
bool flbCleanUpLeftover(const char *path);
