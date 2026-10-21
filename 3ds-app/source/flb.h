// Reader for Flashback clip files (.flb), recorded by Luma3DS-Flashback, and
// the compactor that rewrites them smaller.
//
// Version 1 is what the recorder saves: its ring buffer file, renamed, with an
// index and trailer appended. The layout is documented next to
// flashbackSaveClipFile in Luma3DS-Flashback's sysmodules/rosalina/source/flashback.c;
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
    uint32_t version;       // 1: as recorded, 2: compacted
    uint32_t width, height, fps;
    uint32_t slotStride;    // bytes between ring slots (version 1 only)
    uint32_t slotBytes;     // size of an uncompressed frame
    uint32_t entryCount;    // stored frames
    uint32_t frameCount;    // frames in the clip, copies included
    uint32_t indexOffset;   // where the index starts
    uint64_t fileSize;
} FlbInfo;

typedef struct FlbEntry {
    uint32_t offset, size, copies;  // where the stored frame is in the file, its size, times shown
    uint32_t firstFrame;            // the clip frame this entry is first shown at
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

// The size of an open clip once compacted.
uint64_t flbCompactSize(const FlbClip *clip);

// Compacting: the clip's frames are copied into "<clip>.tmp", a few at a time
// (so the caller can show progress), which then replaces the clip. The clip
// itself is only touched at the very end, and is left as it was if anything
// fails or the compaction is cancelled.
typedef struct FlbCompactor {
    FlbClip clip;
    FILE *out;
    char path[256];         // the clip
    char tmpPath[264];      // the compacted copy, until it replaces the clip
    uint8_t *buf;           // frames waiting to be written to the copy
    uint32_t bufLen;
    uint32_t *offsets;      // each entry's offset in the copy
    uint32_t next;          // the next entry to copy
    uint64_t copied, total; // frame bytes copied so far, and in all
} FlbCompactor;

// Starts compacting the clip at 'path'. On failure returns false and points
// 'error' at a message.
bool flbCompactBegin(FlbCompactor *c, const char *path, const char **error);

// Copies the next frame. Returns 0 while there's more to do, 1 once the clip
// has been replaced by its compacted copy, or -1 on failure (with 'error' set;
// the copy is deleted and the clip left as it was). Either way, once it returns
// 1 or -1 the compactor is finished with.
int flbCompactStep(FlbCompactor *c, const char **error);

// Stops compacting and deletes the copy; the clip is left as it was.
void flbCompactCancel(FlbCompactor *c);

// Cleans up after a compaction that was cut off (the console turned off, for
// example) while the copy was replacing the clip. 'path' is a file in the clip
// folder: if it's a leftover "<clip>.tmp" or "<clip>.old", it's deleted, or
// renamed back to the clip if the clip itself is missing. Returns true if
// 'path' was such a leftover.
bool flbCleanUpLeftover(const char *path);
