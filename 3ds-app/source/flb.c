// Clips of long recordings at full resolution are over 2 GB, so file offsets
// are 64-bit (off_t, with fseeko/ftello): they are on a 3DS anyway, and this
// makes them so on a PC too, where the tests run.
#define _FILE_OFFSET_BITS 64

#include "flb.h"

#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define FLB_MAGIC           0x4B424C46  // "FLBK"
#define FLB_VERSION_RECORDED 1
#define FLB_VERSION_COMPACT  2
#define FLB_VERSION_AUDIO    3
#define FLB_AUDIO_INFO_SIZE  32
#define FLB_INPUT_MAGIC      0x49424C46  // "FLBI"
#define FLB_INPUT_VERSION    1
#define FLB_INPUT_HEADER_SIZE 16
#define FLB_INPUT_RECORD_SIZE 16    // (what's written; reading takes any size from 16 up)

// A compacted or trimmed copy is written in blocks of this many bytes, each at
// a multiple of it in the file (the last one is shorter): every SD write has a
// fixed cost, so fewer, bigger, aligned writes are faster.
#define FLB_COMPACT_BUFFER  (512 * 1024)

// SD card sector size: frames are read in whole sectors (see flbReadStored).
#define FLB_SECTOR          512

static char g_error[96];

static uint32_t flbU32(const uint8_t *p)
{
    return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint16_t flbU16(const uint8_t *p)
{
    return (uint16_t)(p[0] | (p[1] << 8));
}

static void flbPut32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
    p[2] = (uint8_t)(v >> 16);
    p[3] = (uint8_t)(v >> 24);
}

static void flbPut16(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
}

static uint32_t flbEntrySize(const FlbInfo *info)
{
    return info->hasScreens ? 24 : info->version == FLB_VERSION_AUDIO ? 16 : 12;
}

static uint32_t flbAudioInfoSize(const FlbInfo *info)
{
    return info->version == FLB_VERSION_AUDIO ? FLB_AUDIO_INFO_SIZE : 0;
}

// The size of an uncompressed frame of screen 's'.
static uint32_t flbScreenBytes(const FlbInfo *info, int s)
{
    return s == FLB_TOP ? info->slotBytes : info->bottomWidth * info->bottomHeight * 2;
}

// Where the bottom screen's frame starts in an entry's stored data: at the
// next multiple of 4 bytes after the top screen's.
static uint32_t flbBottomAt(const FlbEntry *entry)
{
    return (entry->size + 3) & ~3u;
}

// How many bytes an entry's stored data takes: the top screen's frame, and the
// bottom screen's if it has one.
static uint32_t flbEntryData(const FlbEntry *entry)
{
    return entry->bottomSize != 0 ? flbBottomAt(entry) + entry->bottomSize : entry->size;
}

// The size of the inputs of 'frames' frames of the clip, header included (0
// if it has none).
static uint32_t flbInputsSize(const FlbClip *clip, uint32_t frames)
{
    return clip->inputs != NULL ? FLB_INPUT_HEADER_SIZE + frames * FLB_INPUT_RECORD_SIZE : 0;
}

bool flbIsCompacted(const FlbInfo *info)
{
    return info->version == FLB_VERSION_COMPACT || (info->version == FLB_VERSION_AUDIO && info->slotStride == 0);
}

// How many audio samples 'frames' frames of the clip last, rounded.
static uint32_t flbAudioSamplesIn(const FlbInfo *info, uint32_t frames)
{
    return (uint32_t)(((uint64_t)frames * info->audioRateMhz / info->fps + 500) / 1000);
}

uint32_t flbAudioSamples(const FlbInfo *info)
{
    return flbAudioSamplesIn(info, info->frameCount);
}

// How many audio samples into its entry's audio copy number 'frame' of it
// starts (each copy comes sampleRate / fps samples after the one before).
static uint32_t flbAudioOffsetOf(const FlbInfo *info, uint32_t frame)
{
    return (uint32_t)((uint64_t)frame * info->audioRateMhz / ((uint64_t)info->fps * 1000));
}

// The same checks as the Windows app (windows-app/FlbClip.cs), with the same messages.
bool flbReadInfo(FILE *file, FlbInfo *info, const char **error)
{
    uint8_t t[FLB_TRAILER_SIZE];

    if (fseeko(file, 0, SEEK_END) != 0)
    {
        *error = "the file couldn't be read";
        return false;
    }
    off_t length = ftello(file);
    if (length < FLB_TRAILER_SIZE)
    {
        *error = "not a Flashback clip (file too small)";
        return false;
    }
    if (fseeko(file, length - FLB_TRAILER_SIZE, SEEK_SET) != 0 || fread(t, 1, sizeof(t), file) != sizeof(t))
    {
        *error = "the file couldn't be read";
        return false;
    }
    if (flbU32(t) != FLB_MAGIC)
    {
        *error = "not a Flashback clip (or the copy is incomplete)";
        return false;
    }
    memset(info, 0, sizeof(*info));
    info->version = flbU32(t + 4) & ~(uint32_t)(FLB_VERSION_CODES | FLB_VERSION_SCREENS);
    info->hasCodes = (flbU32(t + 4) & FLB_VERSION_CODES) != 0;
    info->hasScreens = (flbU32(t + 4) & FLB_VERSION_SCREENS) != 0;
    if (info->version < FLB_VERSION_RECORDED || info->version > FLB_VERSION_AUDIO)
    {
        *error = "made by a newer Flashback version; get the latest Flashback Studio";
        return false;
    }

    info->width = flbU32(t + 8);
    info->height = flbU32(t + 12);
    info->fps = flbU32(t + 16);
    info->slotStride = flbU32(t + 20);
    info->slotBytes = flbU32(t + 24);
    info->entryCount = flbU32(t + 28);
    info->frameCount = flbU32(t + 32);
    info->indexOffset = flbU32(t + 36);
    info->inputOffset = flbU32(t + 40);
    info->fileSize = (uint64_t)length;
    // (The bottom screen's size is only there with FLB_VERSION_SCREENS: the
    // word wasn't used before.)
    if (info->hasScreens)
    {
        info->bottomWidth = flbU32(t + 44) & 0xFFFF;
        info->bottomHeight = flbU32(t + 44) >> 16;
    }
    // Missed frames: every copy, in the clips from before FLB_VERSION_SCREENS;
    // since, the index says which copies are.
    info->missedFrames = info->hasScreens ? UINT32_MAX :
                         info->frameCount > info->entryCount ? info->frameCount - info->entryCount : 0;

    if (info->width == 0 || info->height == 0 || info->width > 400 || info->height > 240 ||
        info->slotBytes != info->width * info->height * 2 || info->fps == 0 || info->fps > 60 ||
        (info->version == FLB_VERSION_RECORDED && info->slotStride < info->slotBytes) ||
        (info->bottomWidth == 0) != (info->bottomHeight == 0) || info->bottomWidth > 320 || info->bottomHeight > 240 ||
        info->entryCount > (uint64_t)length / 12 ||
        (uint64_t)info->indexOffset + (uint64_t)info->entryCount * flbEntrySize(info) + flbAudioInfoSize(info) +
            FLB_TRAILER_SIZE != info->fileSize)
    {
        *error = "the clip's header is damaged";
        return false;
    }

    if (info->version == FLB_VERSION_AUDIO)
    {
        uint8_t a[FLB_AUDIO_INFO_SIZE];
        if (fseeko(file, (off_t)info->indexOffset + (off_t)info->entryCount * flbEntrySize(info), SEEK_SET) != 0 ||
            fread(a, 1, sizeof(a), file) != sizeof(a))
        {
            *error = "the file couldn't be read";
            return false;
        }
        info->hasAudio = true;
        info->audioRateMhz = flbU32(a);
        info->audioOffset = flbU32(a + 8);
        info->audioCapacity = flbU32(a + 12);
        info->audioFirst = flbU32(a + 16);
        info->audioEnd = flbU32(a + 20);
        info->audioMissed = flbU32(a + 24);
        uint32_t channels = a[4] | (a[5] << 8), bits = a[6] | (a[7] << 8);
        if (channels != 2 || bits != 16 || info->audioRateMhz < 8000000 || info->audioRateMhz > 96000000 ||
            info->audioCapacity == 0 || (uint64_t)info->audioOffset + (uint64_t)info->audioCapacity * 4 > info->indexOffset ||
            info->audioFirst > info->audioEnd || info->audioEnd - info->audioFirst > info->audioCapacity)
        {
            *error = "the clip's audio header is damaged";
            return false;
        }
    }
    return true;
}

// Loads the clip's inputs, if it has any and they read back sound (see flb.h).
// A clip whose inputs can't be read plays without them.
static void flbReadInputs(FlbClip *clip)
{
    FlbInfo *info = &clip->info;
    uint8_t h[FLB_INPUT_HEADER_SIZE];
    uint32_t count = 0, size = 0;
    uint8_t *raw = NULL;

    bool ok = info->inputOffset != 0 && (uint64_t)info->inputOffset + sizeof(h) <= info->indexOffset &&
              fseeko(clip->file, info->inputOffset, SEEK_SET) == 0 && fread(h, 1, sizeof(h), clip->file) == sizeof(h);
    if (ok)
    {
        count = flbU32(h + 8);
        size = flbU32(h + 12);
        ok = flbU32(h) == FLB_INPUT_MAGIC && flbU32(h + 4) == FLB_INPUT_VERSION && count == info->frameCount &&
             size >= FLB_INPUT_RECORD_SIZE && size <= 256 &&
             (uint64_t)info->inputOffset + sizeof(h) + (uint64_t)count * size <= info->indexOffset;
    }
    if (ok)
    {
        raw = malloc((size_t)count * size);
        clip->inputs = malloc((size_t)count * sizeof(FlbInput));
        ok = raw != NULL && clip->inputs != NULL && fread(raw, size, count, clip->file) == count;
    }
    if (ok)
    {
        for (uint32_t i = 0; i < count; i++)
        {
            const uint8_t *p = raw + (size_t)i * size;
            FlbInput *in = &clip->inputs[i];
            in->buttons = flbU32(p);
            in->circleX = (int16_t)flbU16(p + 4);
            in->circleY = (int16_t)flbU16(p + 6);
            in->touchX = flbU16(p + 8);
            in->touchY = flbU16(p + 10);
            in->cstickX = (int16_t)flbU16(p + 12);
            in->cstickY = (int16_t)flbU16(p + 14);
        }
    }
    else
    {
        free(clip->inputs);
        clip->inputs = NULL;
        info->inputOffset = 0;      // (as good as none)
    }
    free(raw);
}

bool flbOpen(FlbClip *clip, const char *path, const char **error)
{
    memset(clip, 0, sizeof(*clip));
    clip->at[FLB_TOP] = clip->at[FLB_BOTTOM] = UINT32_MAX;
    clip->file = fopen(path, "rb");
    if (clip->file == NULL)
    {
        *error = "the file couldn't be opened";
        return false;
    }
    // Reads are whole frames or long runs of audio: unbuffered, each is one
    // read of the SD card, rather than one per KB through stdio's small buffer.
    setvbuf(clip->file, NULL, _IONBF, 0);
    if (!flbReadInfo(clip->file, &clip->info, error))
        goto fail;

    FlbInfo *info = &clip->info;
    uint32_t entrySize = flbEntrySize(info), bottomBytes = flbScreenBytes(info, FLB_BOTTOM);
    uint8_t *index = malloc((size_t)info->entryCount * entrySize + 1);
    clip->entries = malloc((size_t)info->entryCount * sizeof(FlbEntry) + 1);
    // An entry's frames, read in whole sectors: the top screen's, and the
    // bottom screen's from the next multiple of 4 bytes.
    clip->stored = malloc(info->slotBytes + 4 + bottomBytes + 2 * FLB_SECTOR);
    if (index == NULL || clip->entries == NULL || clip->stored == NULL)
    {
        free(index);
        *error = "not enough memory for this clip";
        goto fail;
    }
    if (fseeko(clip->file, info->indexOffset, SEEK_SET) != 0 ||
        fread(index, entrySize, info->entryCount, clip->file) != info->entryCount)
    {
        free(index);
        *error = "the file couldn't be read";
        goto fail;
    }

    uint32_t frames = 0, missed = 0;
    for (uint32_t e = 0; e < info->entryCount; e++)
    {
        FlbEntry *entry = &clip->entries[e];
        const uint8_t *p = index + e * entrySize;
        uint64_t offset;
        bool ok;
        memset(entry, 0, sizeof(*entry));
        if (info->hasScreens)
        {
            // (Either screen's frame can be left out, and an entry that's only
            // read is shown 0 times.)
            offset = flbU32(p);
            entry->size = flbU32(p + 4);
            entry->bottomSize = flbU32(p + 8);
            entry->copies = flbU32(p + 12);
            entry->audioPos = info->hasAudio ? flbU32(p + 16) : 0;
            entry->flags = (uint8_t)flbU32(p + 20);
            entry->missed = (uint16_t)(flbU32(p + 20) >> 16);
            // (Its missed frames are copies after its first: never more than those.)
            uint32_t most = entry->copies != 0 ? entry->copies - 1 : 0;
            if (entry->missed > most)
                entry->missed = (uint16_t)most;
            ok = entry->size <= info->slotBytes && entry->bottomSize <= bottomBytes &&
                 offset + flbEntryData(entry) <= info->indexOffset;
        }
        else
        {
            uint32_t where = flbU32(p);     // version 1: ring slot, versions 2 and 3: offset
            offset = info->version == FLB_VERSION_RECORDED ? (uint64_t)where * info->slotStride : where;
            entry->size = flbU32(p + 4);
            entry->copies = flbU32(p + 8);
            entry->audioPos = info->hasAudio ? flbU32(p + 12) : 0;
            ok = entry->size != 0 && entry->size <= info->slotBytes && entry->copies != 0 &&
                 offset + entry->size <= info->indexOffset;
            // (Every copy after the first is a missed frame.)
            entry->missed = (uint16_t)(entry->copies - 1 > 0xFFFF ? 0xFFFF : entry->copies - 1);
        }
        entry->firstFrame = frames;
        if (!ok || entry->copies > info->frameCount - frames)
        {
            free(index);
            snprintf(g_error, sizeof(g_error), "frame entry %lu is damaged", (unsigned long)e);
            *error = g_error;
            goto fail;
        }
        entry->offset = (uint32_t)offset;
        frames += entry->copies;
        missed += entry->missed;
    }
    free(index);
    if (frames != info->frameCount || info->entryCount == 0 || frames == 0)
    {
        *error = "the clip's frame count doesn't match its index";
        goto fail;
    }
    if (info->hasScreens)
        info->missedFrames = missed;
    clip->firstShown = flbEntryForFrame(clip, 0);
    flbReadInputs(clip);
    return true;

fail:
    flbClose(clip);
    return false;
}

void flbClose(FlbClip *clip)
{
    if (clip->file != NULL)
        fclose(clip->file);
    free(clip->entries);
    free(clip->stored);
    free(clip->inputs);
    free(clip->codes);
    free(clip->picture[FLB_TOP]);
    free(clip->picture[FLB_BOTTOM]);
    memset(clip, 0, sizeof(*clip));
}

// (Entries that are only read are shown at no frame: of the entries first
// shown at the same frame, this finds the last, which is the one shown.)
uint32_t flbEntryForFrame(const FlbClip *clip, uint32_t frame)
{
    uint32_t lo = 0, hi = clip->info.entryCount - 1;
    while (lo < hi)
    {
        uint32_t mid = (lo + hi + 1) / 2;
        if (clip->entries[mid].firstFrame <= frame)
            lo = mid;
        else
            hi = mid - 1;
    }
    return lo;
}

// Reads 'size' bytes of the file from 'offset' into clip->stored, and returns
// where they are in it (NULL if they can't be read). Reads start on a sector
// of the SD card: a compacted clip's frames start anywhere in the file, and on
// a 3DS a read that doesn't start on a sector is much slower (an Old 3DS played
// compacted clips slower than recorded ones). So the whole sectors holding the
// bytes are read. (The end of the last sector can be past the end of the file:
// only the bytes themselves have to be there.)
static const uint8_t *flbReadStored(FlbClip *clip, uint64_t offset, uint32_t size)
{
    uint32_t skip = (uint32_t)(offset % FLB_SECTOR);
    uint32_t len = (skip + size + FLB_SECTOR - 1) / FLB_SECTOR * FLB_SECTOR;
    if (fseeko(clip->file, (off_t)(offset - skip), SEEK_SET) != 0 ||
        fread(clip->stored, 1, len, clip->file) < skip + size)
        return NULL;
    return clip->stored + skip;
}

// An entry's frame of a clip from before FLB_VERSION_SCREENS: each is whole.
static bool flbDecodeWhole(FlbClip *clip, uint32_t entry, uint16_t *dst, const char **error)
{
    const FlbInfo *info = &clip->info;
    const FlbEntry *e = &clip->entries[entry];
    bool raw = e->size == info->slotBytes;

    // (A recorded clip's frames start on a sector: frames stored as they are
    // go straight to 'dst'.)
    const uint8_t *data;
    if (raw && e->offset % FLB_SECTOR == 0)
    {
        if (fseeko(clip->file, e->offset, SEEK_SET) != 0 || fread(dst, 1, e->size, clip->file) != e->size)
        {
            *error = "the file couldn't be read";
            return false;
        }
        return true;
    }
    data = flbReadStored(clip, e->offset, e->size);
    if (data == NULL)
    {
        *error = "the file couldn't be read";
        return false;
    }
    if (raw)
    {
        memcpy(dst, data, e->size);
        return true;
    }
    // (Frames packed with Huffman codes are decoded through the clip's
    // FlbCodes, made for the first one.)
    if (data[0] == 0xFF && clip->codes == NULL && (clip->codes = malloc(sizeof(FlbCodes))) == NULL)
    {
        *error = "not enough memory for this clip";
        return false;
    }
    if (!flbDecompress(data, e->size, dst, info->width * info->height, clip->codes))
    {
        snprintf(g_error, sizeof(g_error), "frame %lu is damaged", (unsigned long)entry);
        *error = g_error;
        return false;
    }
    return true;
}

// Whether entry 'e' has a whole frame of screen 's' (one that takes no other).
static bool flbIsWhole(const FlbClip *clip, uint32_t e, int s)
{
    const FlbEntry *entry = &clip->entries[e];
    return (s == FLB_TOP ? entry->size : entry->bottomSize) != 0 &&
           !(entry->flags & (s == FLB_TOP ? FLB_FRAME_TOP_DELTA : FLB_FRAME_BOTTOM_DELTA));
}

// Whether screen 's''s next frame after entry 'e' is one of what changed (and
// so needs the picture as of 'e').
static bool flbNextIsDelta(const FlbClip *clip, uint32_t e, int s)
{
    for (uint32_t i = e + 1; i < clip->info.entryCount; i++)
        if ((s == FLB_TOP ? clip->entries[i].size : clip->entries[i].bottomSize) != 0)
            return (clip->entries[i].flags & (s == FLB_TOP ? FLB_FRAME_TOP_DELTA : FLB_FRAME_BOTTOM_DELTA)) != 0;
    return false;
}

// Each of the screens wanted is made from its last whole frame at or before
// 'e', and the frames of what changed after it; if the clip's picture is from
// between those already (playing on), only from the entries after it. A screen
// with no whole frame yet is unknown: its frames of what changed are passed
// over. Each entry's frames are read in one go.
//
// With 'into', when 'e' has a whole frame of a screen that the next one
// doesn't need (it's whole too, as most are), that frame is decoded straight
// into into[] for that screen instead (if it's not NULL), and direct[] set:
// the clip then keeps no picture of that screen (at[] is UINT32_MAX), which
// saves copying it out.
static bool flbSeek(FlbClip *clip, uint32_t e, bool top, bool bottom, uint16_t *const *into, bool *direct,
                    const char **error)
{
    if (direct != NULL)
        direct[FLB_TOP] = direct[FLB_BOTTOM] = false;
    const FlbInfo *info = &clip->info;
    if (!info->hasScreens)
        return true;    // (nothing kept: every frame is whole)
    bool want[2] = { top, bottom && info->bottomWidth != 0 };
    uint32_t from[2] = { e + 1, e + 1 };     // each screen's first entry to take (e + 1: none)

    for (int s = 0; s < 2; s++)
    {
        if (!want[s])
            continue;
        if (clip->picture[s] == NULL && (clip->picture[s] = malloc(flbScreenBytes(info, s))) == NULL)
        {
            *error = "not enough memory for this clip";
            return false;
        }
        // (A picture from after 'e', or none: it's made anew.)
        bool held = clip->at[s] != UINT32_MAX && clip->at[s] <= e;
        uint32_t start = held ? clip->at[s] + 1 : 0;
        from[s] = start;
        for (uint32_t i = e + 1; i-- > start;)
            if (flbIsWhole(clip, i, s))
            {
                from[s] = i;
                break;
            }
        if (!held && !(from[s] <= e && flbIsWhole(clip, from[s], s)))
        {
            // No whole frame at or before 'e': the screen is unknown there.
            if (clip->known[s])
                clip->changes[s]++;
            clip->known[s] = false;
            from[s] = e + 1;
        }
        clip->at[s] = UINT32_MAX;   // (until it's there: a frame that can't be read leaves the picture part made)
    }

    for (uint32_t i = from[FLB_TOP] < from[FLB_BOTTOM] ? from[FLB_TOP] : from[FLB_BOTTOM]; i <= e; i++)
    {
        const FlbEntry *entry = &clip->entries[i];
        uint32_t size[2] = { entry->size, entry->bottomSize }, at[2] = { 0, flbBottomAt(entry) };
        bool take[2];
        for (int s = 0; s < 2; s++)
            take[s] = want[s] && i >= from[s] && size[s] != 0 && (clip->known[s] || flbIsWhole(clip, i, s));
        if (!take[FLB_TOP] && !take[FLB_BOTTOM])
            continue;

        uint32_t a = take[FLB_TOP] ? 0 : at[FLB_BOTTOM];
        uint32_t b = take[FLB_BOTTOM] ? at[FLB_BOTTOM] + size[FLB_BOTTOM] : size[FLB_TOP];
        const uint8_t *data = flbReadStored(clip, (uint64_t)entry->offset + a, b - a);
        if (data == NULL)
        {
            *error = "the file couldn't be read";
            return false;
        }
        for (int s = 0; s < 2; s++)
        {
            if (!take[s])
                continue;
            const uint8_t *src = data + at[s] - a;
            bool delta = (entry->flags & (s == FLB_TOP ? FLB_FRAME_TOP_DELTA : FLB_FRAME_BOTTOM_DELTA)) != 0;
            bool coded = (entry->flags & (s == FLB_TOP ? FLB_FRAME_TOP_CODES : FLB_FRAME_BOTTOM_CODES)) != 0;
            uint32_t w = s == FLB_TOP ? info->width : info->bottomWidth, h = s == FLB_TOP ? info->height : info->bottomHeight;
            uint16_t *dst = clip->picture[s];
            if (into != NULL && into[s] != NULL && i == e && !delta && !flbNextIsDelta(clip, e, s))
            {
                dst = into[s];
                direct[s] = true;
            }
            if (!delta && size[s] == w * h * 2)
                memcpy(dst, src, size[s]);      // (stored as it is)
            else
            {
                if (coded && clip->codes == NULL && (clip->codes = malloc(sizeof(FlbCodes))) == NULL)
                {
                    *error = "not enough memory for this clip";
                    return false;
                }
                if (!flbDecodeFrame(src, size[s], dst, w, h, delta, coded, clip->codes))
                {
                    snprintf(g_error, sizeof(g_error), "frame %lu is damaged", (unsigned long)i);
                    *error = g_error;
                    return false;
                }
            }
            clip->known[s] = true;
            clip->changes[s]++;
        }
    }
    for (int s = 0; s < 2; s++)
        if (want[s])
            clip->at[s] = direct != NULL && direct[s] ? UINT32_MAX : e;
    return true;
}

bool flbSeekScreens(FlbClip *clip, uint32_t e, bool top, bool bottom, const char **error)
{
    return flbSeek(clip, e, top, bottom, NULL, NULL, error);
}

bool flbDecodeScreens(FlbClip *clip, uint32_t entry, uint16_t *top, uint16_t *bottom, const char **error)
{
    const FlbInfo *info = &clip->info;
    if (!info->hasScreens)
        return top == NULL || flbDecodeWhole(clip, entry, top, error);

    uint16_t *into[2] = { top, bottom };
    bool direct[2];
    if (!flbSeek(clip, entry, top != NULL, bottom != NULL, into, direct, error))
        return false;
    // (A screen that's unknown, or that the clip hasn't got, is black.)
    if (top != NULL && !direct[FLB_TOP] && clip->known[FLB_TOP])
        memcpy(top, clip->picture[FLB_TOP], info->slotBytes);
    else if (top != NULL && !direct[FLB_TOP])
        memset(top, 0, info->slotBytes);
    if (bottom != NULL && !direct[FLB_BOTTOM] && info->bottomWidth != 0 && clip->known[FLB_BOTTOM])
        memcpy(bottom, clip->picture[FLB_BOTTOM], flbScreenBytes(info, FLB_BOTTOM));
    else if (bottom != NULL && !direct[FLB_BOTTOM])
        memset(bottom, 0, flbScreenBytes(info, FLB_BOTTOM));
    return true;
}

bool flbDecodeEntry(FlbClip *clip, uint32_t entry, uint16_t *dst, const char **error)
{
    return flbDecodeScreens(clip, entry, dst, NULL, error);
}

bool flbReadAudio(FlbClip *clip, uint32_t first, uint32_t count, int16_t *dst, const char **error)
{
    const FlbInfo *info = &clip->info;
    memset(dst, 0, (size_t)count * 4);
    if (!info->hasAudio)
        return true;

    // The part of these samples the clip holds, in at most two pieces (a ring wraps).
    uint64_t a = first > info->audioFirst ? first : info->audioFirst;
    uint64_t b = (uint64_t)first + count < info->audioEnd ? (uint64_t)first + count : info->audioEnd;
    while (a < b)
    {
        uint32_t pos = (uint32_t)(a % info->audioCapacity);
        uint32_t n = (uint32_t)(b - a < info->audioCapacity - pos ? b - a : info->audioCapacity - pos);
        if (fseeko(clip->file, (off_t)info->audioOffset + (off_t)pos * 4, SEEK_SET) != 0 ||
            fread(dst + (a - first) * 2, 4, n, clip->file) != n)
        {
            *error = "the clip's audio couldn't be read";
            return false;
        }
        a += n;
    }
    return true;
}

// The index entries a copy of frames firstFrame .. firstFrame + frameCount - 1
// holds: *first .. *end - 1. *shown is the one shown at the first frame, and
// *end is one past the one holding the last. The entries before *shown are
// only read in the copy (FLB_VERSION_SCREENS): 'whole' (compacting) keeps
// every entry of the clip; otherwise (trimming) they're the ones the first
// frame takes to make out, from the last entry at or before *shown with the
// top screen whole, or with the bottom screen whole, whichever comes first (a
// screen with no whole frame by then doesn't count; without one of the top
// screen's, from *shown).
static void flbCopyRange(const FlbClip *clip, uint32_t firstFrame, uint32_t frameCount, bool whole,
                         uint32_t *first, uint32_t *shown, uint32_t *end)
{
    *shown = flbEntryForFrame(clip, firstFrame);
    *end = flbEntryForFrame(clip, firstFrame + frameCount - 1) + 1;
    *first = *shown;
    if (!clip->info.hasScreens)
        return;
    if (whole)
    {
        *first = 0;
        *end = clip->info.entryCount;
        return;
    }
    uint32_t key[2] = { *shown, *shown };
    for (int s = 0; s < 2; s++)
    {
        bool found = false;
        for (uint32_t i = *shown + 1; i-- > 0 && !found;)
            if (flbIsWhole(clip, i, s))
            {
                key[s] = i;
                found = true;
            }
        if (s == FLB_BOTTOM && !found)
            key[s] = key[FLB_TOP];
    }
    *first = key[FLB_TOP] < key[FLB_BOTTOM] ? key[FLB_TOP] : key[FLB_BOTTOM];
}

// How many of those frames entry 'e' is shown for (fewer than its copies if the
// range starts or ends part way through them; none if it comes before them).
static uint32_t flbCopiesIn(const FlbClip *clip, uint32_t e, uint32_t firstFrame, uint32_t frameCount)
{
    const FlbEntry *entry = &clip->entries[e];
    uint32_t from = entry->firstFrame > firstFrame ? entry->firstFrame : firstFrame;
    uint32_t to = entry->firstFrame + entry->copies;
    if (to > firstFrame + frameCount)
        to = firstFrame + frameCount;
    return to > from ? to - from : 0;
}

// The size of a copy of those frames (see flbCopyRange).
static uint64_t flbCopySize(const FlbClip *clip, uint32_t firstFrame, uint32_t frameCount, bool whole)
{
    const FlbInfo *info = &clip->info;
    uint32_t first, shown, end;
    flbCopyRange(clip, firstFrame, frameCount, whole, &first, &shown, &end);
    uint64_t size = (uint64_t)(end - first) * flbEntrySize(info) + flbAudioInfoSize(info) + FLB_TRAILER_SIZE +
                    flbInputsSize(clip, frameCount);
    for (uint32_t e = first; e < end; e++)
        size += flbEntryData(&clip->entries[e]);
    if (info->hasAudio)
        size += (uint64_t)flbAudioSamplesIn(info, frameCount) * 4;
    return size;
}

uint64_t flbTrimSize(const FlbClip *clip, uint32_t firstFrame, uint32_t frameCount)
{
    return flbCopySize(clip, firstFrame, frameCount, false);
}

uint64_t flbCompactSize(const FlbClip *clip)
{
    return flbCopySize(clip, 0, clip->info.frameCount, true);
}

static void flbCompactFree(FlbCompactor *c)
{
    if (c->out != NULL)
        fclose(c->out);
    flbClose(&c->clip);
    free(c->buf);
    free(c->offsets);
    c->out = NULL;
    c->buf = NULL;
    c->offsets = NULL;
}

void flbCompactCancel(FlbCompactor *c)
{
    flbCompactFree(c);
    remove(c->tmpPath);
}

// Starts copying frames firstFrame .. firstFrame + frameCount - 1 (all of them
// if frameCount is UINT32_MAX) of the clip at 'path' to 'outPath': the clip
// itself when compacting.
static bool flbCopyBegin(FlbCompactor *c, const char *path, const char *outPath, uint32_t firstFrame,
                         uint32_t frameCount, const char **error)
{
    memset(c, 0, sizeof(*c));
    if (strlen(path) >= sizeof(c->path) || strlen(outPath) >= sizeof(c->outPath))
    {
        *error = "the clip's name is too long";
        return false;
    }
    strcpy(c->path, path);
    strcpy(c->outPath, outPath);
    snprintf(c->tmpPath, sizeof(c->tmpPath), "%s.tmp", outPath);

    if (!flbOpen(&c->clip, path, error))
        return false;
    const FlbInfo *info = &c->clip.info;
    bool compacting = strcmp(path, outPath) == 0;
    if (compacting && flbIsCompacted(info))
    {
        flbClose(&c->clip);
        *error = "this clip is already compacted";
        return false;
    }
    if (frameCount == UINT32_MAX)
        frameCount = info->frameCount;
    if (frameCount == 0 || firstFrame >= info->frameCount || frameCount > info->frameCount - firstFrame)
    {
        flbClose(&c->clip);
        *error = "those frames aren't in the clip";
        return false;
    }
    c->firstFrame = firstFrame;
    c->frameCount = frameCount;
    flbCopyRange(&c->clip, firstFrame, frameCount, compacting, &c->firstEntry, &c->shownEntry, &c->endEntry);
    c->next = c->firstEntry;

    // Just the audio the frames last, from where the first one's starts (its
    // entry's, plus the copies of it skipped): readers play a clip's audio
    // straight on from its first shown entry's position, which is then 0.
    if (info->hasAudio)
    {
        const FlbEntry *first = &c->clip.entries[c->shownEntry];
        c->audioStart = first->audioPos + flbAudioOffsetOf(info, firstFrame - first->firstFrame);
        c->audioCount = flbAudioSamplesIn(info, frameCount);
    }
    uint64_t size = flbCopySize(&c->clip, firstFrame, frameCount, compacting);
    c->total = size - (uint64_t)(c->endEntry - c->firstEntry) * flbEntrySize(info) -
               flbAudioInfoSize(info) - FLB_TRAILER_SIZE - flbInputsSize(&c->clip, frameCount);
    c->buf = malloc(FLB_COMPACT_BUFFER);
    c->offsets = malloc((size_t)(c->endEntry - c->firstEntry) * sizeof(uint32_t));
    if (c->buf == NULL || c->offsets == NULL)
    {
        flbCompactFree(c);
        *error = "not enough memory to copy this clip";
        return false;
    }
    // A trimmed clip is a new one: never write over another.
    if (!compacting)
    {
        FILE *existing = fopen(outPath, "rb");
        if (existing != NULL)
        {
            fclose(existing);
            flbCompactFree(c);
            *error = "there's already a clip with that name";
            return false;
        }
    }
    c->out = fopen(c->tmpPath, "wb");
    if (c->out == NULL)
    {
        flbCompactFree(c);
        *error = "the copy couldn't be created";
        return false;
    }
    setvbuf(c->out, NULL, _IONBF, 0);   // (it's written in whole blocks already)
    // The copy at its full size up front: its SD space is claimed once,
    // instead of the file growing with every block.
    if (ftruncate(fileno(c->out), (off_t)size) != 0)
    {
        flbCompactCancel(c);
        *error = "the copy couldn't be created (is the SD card full?)";
        return false;
    }
    return true;
}

bool flbCompactBegin(FlbCompactor *c, const char *path, const char **error)
{
    return flbCopyBegin(c, path, path, 0, UINT32_MAX, error);
}

bool flbTrimBegin(FlbCompactor *c, const char *path, const char *outPath, uint32_t firstFrame,
                  uint32_t frameCount, const char **error)
{
    if (strcmp(path, outPath) == 0)
    {
        *error = "the trimmed clip needs a name of its own";
        return false;
    }
    return flbCopyBegin(c, path, outPath, firstFrame, frameCount, error);
}

static bool flbCompactFlush(FlbCompactor *c)
{
    bool ok = c->bufLen == 0 || fwrite(c->buf, 1, c->bufLen, c->out) == c->bufLen;
    c->bufLen = 0;
    return ok;
}

// Adds 'len' bytes to the copy, writing each block out once it's full.
static bool flbCompactPut(FlbCompactor *c, const uint8_t *src, uint32_t len)
{
    while (len > 0)
    {
        uint32_t n = FLB_COMPACT_BUFFER - c->bufLen;
        if (n > len)
            n = len;
        memcpy(c->buf + c->bufLen, src, n);
        c->bufLen += n;
        src += n;
        len -= n;
        if (c->bufLen == FLB_COMPACT_BUFFER && !flbCompactFlush(c))
            return false;
    }
    return true;
}

// Writes the inputs of the frames copied (if the clip has inputs), the index
// (audio info) and trailer, checks the copy reads back, then makes it the clip.
// Compacting swaps it in for the clip: the clip is renamed out of the way
// first, so there's always a whole clip on the SD card (see
// flbCleanUpLeftover). Trimming gives it its own name.
static bool flbCompactFinish(FlbCompactor *c, const char **error)
{
    const FlbInfo *info = &c->clip.info;
    uint32_t entries = c->endEntry - c->firstEntry;
    uint32_t entrySize = flbEntrySize(info);
    uint32_t inputSize = flbInputsSize(&c->clip, c->frameCount);
    uint32_t indexSize = entries * entrySize + flbAudioInfoSize(info);
    uint8_t *tail = malloc(inputSize + indexSize + FLB_TRAILER_SIZE);
    if (tail == NULL)
    {
        *error = "not enough memory to copy this clip";
        return false;
    }

    uint32_t inputOffset = 0;
    if (inputSize != 0)
    {
        inputOffset = (uint32_t)c->copied;
        flbPut32(tail, FLB_INPUT_MAGIC);
        flbPut32(tail + 4, FLB_INPUT_VERSION);
        flbPut32(tail + 8, c->frameCount);
        flbPut32(tail + 12, FLB_INPUT_RECORD_SIZE);
        for (uint32_t f = 0; f < c->frameCount; f++)
        {
            const FlbInput *in = &c->clip.inputs[c->firstFrame + f];
            uint8_t *p = tail + FLB_INPUT_HEADER_SIZE + f * FLB_INPUT_RECORD_SIZE;
            flbPut32(p, in->buttons);
            flbPut16(p + 4, (uint16_t)in->circleX);
            flbPut16(p + 6, (uint16_t)in->circleY);
            flbPut16(p + 8, in->touchX);
            flbPut16(p + 10, in->touchY);
            flbPut16(p + 12, (uint16_t)in->cstickX);
            flbPut16(p + 14, (uint16_t)in->cstickY);
        }
    }

    uint8_t *index = tail + inputSize;
    for (uint32_t i = 0; i < entries; i++)
    {
        uint32_t e = c->firstEntry + i;
        const FlbEntry *entry = &c->clip.entries[e];
        uint8_t *p = index + i * entrySize;
        // (The entries before the one first shown are only read: no copies.)
        uint32_t copies = e < c->shownEntry ? 0 : flbCopiesIn(&c->clip, e, c->firstFrame, c->frameCount);
        // The copy's audio starts at its first frame, and the entry shown
        // there may be kept from part way through its copies.
        uint32_t audioPos = 0;
        if (info->hasAudio && copies != 0)
        {
            uint32_t skipped = entry->firstFrame < c->firstFrame ? c->firstFrame - entry->firstFrame : 0;
            uint32_t pos = entry->audioPos + flbAudioOffsetOf(info, skipped);
            audioPos = pos > c->audioStart ? pos - c->audioStart : 0;
        }
        flbPut32(p, c->offsets[i]);
        flbPut32(p + 4, entry->size);
        if (info->hasScreens)
        {
            // (Its missed frames are copies after its first: with copies cut,
            // at most those left.)
            uint32_t missed = copies == 0 ? 0 : entry->missed < copies - 1 ? entry->missed : copies - 1;
            flbPut32(p + 8, entry->bottomSize);
            flbPut32(p + 12, copies);
            flbPut32(p + 16, audioPos);
            flbPut32(p + 20, entry->flags | missed << 16);
        }
        else
        {
            flbPut32(p + 8, copies);
            if (info->hasAudio)
                flbPut32(p + 12, audioPos);
        }
    }
    if (info->hasAudio)
    {
        uint8_t *a = index + entries * entrySize;
        memset(a, 0, FLB_AUDIO_INFO_SIZE);
        flbPut32(a, info->audioRateMhz);
        a[4] = 2;       // channels
        a[6] = 16;      // bits per sample
        flbPut32(a + 8, c->audioOffset);
        flbPut32(a + 12, c->audioCount);
        flbPut32(a + 16, 0);
        flbPut32(a + 20, c->audioCount);
        flbPut32(a + 24, info->audioMissed);
    }
    uint8_t *t = index + indexSize;
    memset(t, 0, FLB_TRAILER_SIZE);
    flbPut32(t, FLB_MAGIC);
    flbPut32(t + 4, (info->hasAudio ? FLB_VERSION_AUDIO : FLB_VERSION_COMPACT) | (info->hasCodes ? FLB_VERSION_CODES : 0) |
                    (info->hasScreens ? FLB_VERSION_SCREENS : 0));
    flbPut32(t + 8, info->width);
    flbPut32(t + 12, info->height);
    flbPut32(t + 16, info->fps);
    flbPut32(t + 20, 0);    // no slots
    flbPut32(t + 24, info->slotBytes);
    flbPut32(t + 28, entries);
    flbPut32(t + 32, c->frameCount);
    flbPut32(t + 36, (uint32_t)(c->copied + inputSize));
    flbPut32(t + 40, inputOffset);
    flbPut32(t + 44, info->bottomWidth | info->bottomHeight << 16);     // (0 before FLB_VERSION_SCREENS)

    bool ok = flbCompactPut(c, tail, inputSize + indexSize + FLB_TRAILER_SIZE) && flbCompactFlush(c);
    free(tail);
    c->copied += inputSize;
    ok = fclose(c->out) == 0 && ok;
    c->out = NULL;
    if (!ok)
    {
        *error = "the copy couldn't be written (is the SD card full?)";
        return false;
    }

    // Check the copy reads back as a whole clip before it's used (its inputs too).
    FlbClip check;
    if (!flbOpen(&check, c->tmpPath, error))
        return false;
    ok = check.info.frameCount == c->frameCount && check.info.entryCount == entries &&
         check.info.fileSize == c->copied + indexSize + FLB_TRAILER_SIZE &&
         (check.inputs != NULL) == (inputSize != 0);
    flbClose(&check);
    if (!ok)
    {
        *error = "the copy didn't read back correctly";
        return false;
    }

    if (strcmp(c->path, c->outPath) != 0)
    {
        if (rename(c->tmpPath, c->outPath) != 0)
        {
            *error = "the trimmed clip couldn't be saved";
            return false;
        }
        return true;
    }

    flbClose(&c->clip);     // (it can't be renamed while open)
    char oldPath[sizeof(c->path) + 4];
    snprintf(oldPath, sizeof(oldPath), "%s.old", c->path);
    if (rename(c->path, oldPath) != 0)
    {
        *error = "the clip couldn't be replaced";
        return false;
    }
    if (rename(c->tmpPath, c->path) != 0)
    {
        rename(oldPath, c->path);
        *error = "the clip couldn't be replaced";
        return false;
    }
    remove(oldPath);
    return true;
}

int flbCompactStep(FlbCompactor *c, const char **error)
{
    const FlbInfo *info = &c->clip.info;

    if (c->next < c->endEntry)
    {
        // (Read into the clip's frame buffer: an entry can straddle two
        // blocks. It's both screens' frames, as they're stored: the top
        // screen's, then the bottom screen's from the next multiple of 4
        // bytes. What's between them is written as zeros, whatever it was.)
        const FlbEntry *e = &c->clip.entries[c->next];
        uint32_t len = flbEntryData(e);
        if (len != 0 && (fseeko(c->clip.file, e->offset, SEEK_SET) != 0 ||
                         fread(c->clip.stored, 1, len, c->clip.file) != len))
        {
            *error = "the clip couldn't be read";
            goto fail;
        }
        if (e->bottomSize != 0)
            memset(c->clip.stored + e->size, 0, flbBottomAt(e) - e->size);
        if (!flbCompactPut(c, c->clip.stored, len))
        {
            *error = "the copy couldn't be written (is the SD card full?)";
            goto fail;
        }
        c->offsets[c->next++ - c->firstEntry] = (uint32_t)c->copied;
        c->copied += len;
        return 0;
    }

    if (c->audioDone < c->audioCount)
    {
        if (c->audioDone == 0)
            c->audioOffset = (uint32_t)c->copied;
        uint32_t n = info->slotBytes / 4;   // (through the frame buffer too)
        if (n > c->audioCount - c->audioDone)
            n = c->audioCount - c->audioDone;
        if (!flbReadAudio(&c->clip, c->audioStart + c->audioDone, n, (int16_t *)c->clip.stored, error))
            goto fail;
        if (!flbCompactPut(c, c->clip.stored, n * 4))
        {
            *error = "the copy couldn't be written (is the SD card full?)";
            goto fail;
        }
        c->copied += n * 4;
        c->audioDone += n;
        return 0;
    }

    if (!flbCompactFinish(c, error))
        goto fail;
    flbCompactFree(c);
    return 1;

fail:
    flbCompactCancel(c);
    return -1;
}

static bool flbEndsWith(const char *s, const char *suffix)
{
    size_t n = strlen(s), m = strlen(suffix);
    return n >= m && strcmp(s + n - m, suffix) == 0;
}

bool flbCleanUpLeftover(const char *path)
{
    if (flbEndsWith(path, ".flb.tmp"))
    {
        remove(path);   // an unfinished copy
        return true;
    }
    if (!flbEndsWith(path, ".flb.old"))
        return false;

    // The clip, renamed out of the way while its copy replaced it.
    char clip[300];
    size_t n = strlen(path) - 4;
    if (n >= sizeof(clip))
        return true;
    memcpy(clip, path, n);
    clip[n] = '\0';
    FILE *f = fopen(clip, "rb");
    if (f != NULL)
    {
        fclose(f);
        remove(path);       // the copy made it: the original isn't needed
    }
    else
        rename(path, clip); // it didn't: put the original back
    return true;
}

// Decodes the recorder's frame compression, its ops (see flashbackCompress and
// flashbackCompressDelta in Luma3DS-Flashback's flashback_compress.c), into
// the upright w x h frame at 'dst'.
//
// A whole frame's pixels come upright, top row first. A frame stored as what
// changed since the one before ('delta') has them in the screen's own order:
// column by column, left to right, each from the bottom row up (the k-th goes
// to (h - 1 - k % h) * w + k / h). 'dst' then holds the frame before it on the
// same screen, and the pixels SKIP ops pass over stay as they are; only such
// frames have SKIP ops. Either way, 'at' is where the next pixel goes; for a
// delta, 'step' is what's added to it for the one after (a row up), and 'left'
// how many more go before a new column starts.
//
// It's put in twice, with 'delta' a constant (flbDecodeOps): a whole frame's
// pixels, most of them, then only take '*at++' (an Old 3DS decodes a frame in
// about a frame's time, so the bookkeeping for columns showed).
static inline __attribute__((always_inline)) bool flbDecodeOpsAs(const uint8_t *src, uint32_t len, uint16_t *dst,
                                                                 uint32_t w, uint32_t h, const bool delta)
{
    uint16_t index[64];
    memset(index, 0, sizeof(index));
    uint16_t px = 0;
    const uint8_t *p = src, *end = src + len;
    uint32_t i = 0, pixels = w * h;
    uint32_t column = delta ? h : pixels;
    uint16_t *at = delta ? dst + (size_t)(h - 1) * w : dst;
    ptrdiff_t step = delta ? -(ptrdiff_t)w : 1;
    uint32_t left = column;

// (Puts a pixel, and moves on: to the foot of the next column after a column's last.)
#define FLB_PUT(value) \
    do \
    { \
        *at = (value); \
        i++; \
        if (!delta) \
            at++; \
        else if (--left != 0) \
            at += step; \
        else \
        { \
            left = column; \
            at = dst + (size_t)(h - 1) * w + i / h; \
        } \
    } while (0)

    while (i < pixels)
    {
        if (p >= end)
            return false;
        uint8_t b = *p++;

        if (b == 0xFF)
        {
            if (!delta || p >= end)
                return false;
            uint32_t count = *p++;
            if (count == 0xFF)
            {
                if (end - p < 2)
                    return false;
                count = p[0] | (p[1] << 8);
                p += 2;
            }
            count++;
            if (count > pixels - i)
                return false;
            // (The previous pixel is now the last one passed over.)
            i += count;
            px = dst[(size_t)(h - 1 - (i - 1) % h) * w + (i - 1) / h];
            left = h - i % h;
            at = dst + (size_t)(h - 1 - i % h) * w + i / h;
            continue;
        }
        else if (b == 0xFE)
        {
            if (end - p < 2)
                return false;
            px = p[0] | (p[1] << 8);
            p += 2;
        }
        else if ((b & 0xC0) == 0xC0)
        {
            uint32_t run = (b & 0x3F) + 1;
            if (run > pixels - i)
                return false;
            while (run-- != 0)
                FLB_PUT(px);
            continue;
        }
        else if ((b & 0xC0) == 0x00)
        {
            px = index[b];
            FLB_PUT(px);
            continue;
        }
        else
        {
            int32_t dr, dg, db;
            if ((b & 0xC0) == 0x40)
            {
                dr = ((b >> 4) & 3) - 2;
                dg = ((b >> 2) & 3) - 2;
                db = (b & 3) - 2;
            }
            else
            {
                if (p >= end)
                    return false;
                uint8_t b2 = *p++;
                dg = (b & 0x3F) - 32;
                dr = ((b2 >> 4) - 8) + (dg >> 1);
                db = ((b2 & 0xF) - 8) + (dg >> 1);
            }
            uint32_t r = ((px >> 11) + dr) & 0x1F;
            uint32_t g = (((px >> 5) & 0x3F) + dg) & 0x3F;
            uint32_t bl = ((px & 0x1F) + db) & 0x1F;
            px = (uint16_t)((r << 11) | (g << 5) | bl);
        }
        index[((uint32_t)px * 0x9E3779B1u) >> 26] = px;
        FLB_PUT(px);
    }
#undef FLB_PUT
    return p == end;
}

static bool flbDecodeOps(const uint8_t *src, uint32_t len, uint16_t *dst, uint32_t w, uint32_t h, bool delta)
{
    return delta ? flbDecodeOpsAs(src, len, dst, w, h, true) : flbDecodeOpsAs(src, len, dst, w, h, false);
}

#define FLB_CODES_HEADER    264     // a frame packed with Huffman codes: bytes before the codes

// Turns a frame packed with Huffman codes (see flashbackHuffman in
// Luma3DS-Flashback's flashback_compress.c) back into the compressed bytes they
// stand for, at most 'cap' of them, into codes->bytes; sets *count.
static bool flbDecodeCodes(const uint8_t *src, uint32_t len, FlbCodes *codes, uint32_t cap, uint32_t *count)
{
    if (len < FLB_CODES_HEADER || src[0] != 0xFF || src[1] != 1)
        return false;
    uint32_t n = flbU32(src + 4);
    if (n > cap || n > sizeof(codes->bytes))
        return false;

    // Each set's codes by their next FLB_CODE_BITS bits (first bit lowest). The
    // codes are canonical: shorter first, and among the same length, lower
    // byte values first.
    for (uint32_t set = 0; set < 2; set++)
    {
        const uint8_t *lengths = src + 8 + set * 128;
        uint16_t *table = codes->table[set];
        uint32_t perLength[FLB_CODE_BITS + 1] = { 0 }, next[FLB_CODE_BITS + 1];
        for (uint32_t v = 0; v < 256; v++)
        {
            uint32_t length = lengths[v / 2] >> (v & 1) * 4 & 0xF;
            if (length > FLB_CODE_BITS)
                return false;
            perLength[length]++;
        }
        uint32_t code = 0;
        perLength[0] = 0;
        for (uint32_t length = 1; length <= FLB_CODE_BITS; length++)
        {
            code = (code + perLength[length - 1]) << 1;
            next[length] = code;
        }
        memset(table, 0, sizeof(codes->table[set]));
        for (uint32_t v = 0; v < 256; v++)
        {
            uint32_t length = lengths[v / 2] >> (v & 1) * 4 & 0xF;
            if (length == 0)
                continue;
            uint32_t c = next[length]++, reversed = 0;
            if (c >= 1u << length)
                return false;   // (more codes than there's room for)
            for (uint32_t k = 0; k < length; k++)
                reversed = reversed << 1 | (c >> k & 1);
            for (uint32_t k = reversed; k < 1u << FLB_CODE_BITS; k += 1u << length)
                table[k] = (uint16_t)(v | length << 8);
        }
    }

    // The bytes, each set's codes in turn: an op byte, then the bytes after it
    // (LUMA's second, RAW's two, SKIP's count: one, or three if the first is 0xFF).
    uint32_t pos = FLB_CODES_HEADER, bits = 0, have = 0, after = 0;
    bool skipCount = false;     // the next byte is the first after a SKIP
    for (uint32_t i = 0; i < n; i++)
    {
        while (have <= 24)
        {
            bits |= (uint32_t)(pos < len ? src[pos] : 0) << have;
            pos++;
            have += 8;
        }
        uint32_t e = codes->table[after != 0][bits & ((1u << FLB_CODE_BITS) - 1)], used = e >> 8;
        if (used == 0)
            return false;
        bits >>= used;
        have -= used;
        uint8_t b = (uint8_t)e;
        codes->bytes[i] = b;
        if (after != 0)
        {
            after--;
            if (skipCount && b == 0xFF)
                after += 2;
            skipCount = false;
        }
        else
        {
            after = b >= 0x80 && b < 0xC0 ? 1 : b == 0xFE ? 2 : b == 0xFF ? 1 : 0;
            skipCount = b == 0xFF;
        }
    }
    // (The codes must end in the frame's last 2 bytes: it's padded to 2.)
    uint64_t used = (uint64_t)(pos - FLB_CODES_HEADER) * 8 - have, held = (uint64_t)(len - FLB_CODES_HEADER) * 8;
    if (used > held || used + 16 <= held)
        return false;
    *count = n;
    return true;
}

bool flbDecodeFrame(const uint8_t *src, uint32_t len, uint16_t *dst, uint32_t w, uint32_t h, bool delta, bool coded,
                    FlbCodes *codes)
{
    if (coded)
    {
        uint32_t count;
        return codes != NULL && flbDecodeCodes(src, len, codes, w * h * 2, &count) &&
               flbDecodeOps(codes->bytes, count, dst, w, h, delta);
    }
    return flbDecodeOps(src, len, dst, w, h, delta);
}

bool flbDecompress(const uint8_t *src, uint32_t len, uint16_t *dst, uint32_t pixels, FlbCodes *codes)
{
    return flbDecodeFrame(src, len, dst, pixels, 1, false, len != 0 && src[0] == 0xFF, codes);
}
