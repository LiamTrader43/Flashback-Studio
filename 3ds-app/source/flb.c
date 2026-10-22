#include "flb.h"

#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define FLB_MAGIC           0x4B424C46  // "FLBK"
#define FLB_VERSION_RECORDED 1
#define FLB_VERSION_COMPACT  2
#define FLB_VERSION_AUDIO    3
#define FLB_AUDIO_INFO_SIZE  32

// A compacted copy is written in blocks of this many bytes, each at a multiple
// of it in the file (the last one is shorter): every SD write has a fixed cost,
// so fewer, bigger, aligned writes are faster.
#define FLB_COMPACT_BUFFER  (512 * 1024)

static char g_error[96];

static uint32_t flbU32(const uint8_t *p)
{
    return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24);
}

static void flbPut32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
    p[2] = (uint8_t)(v >> 16);
    p[3] = (uint8_t)(v >> 24);
}

static uint32_t flbEntrySize(const FlbInfo *info)
{
    return info->version == FLB_VERSION_AUDIO ? 16 : 12;
}

static uint32_t flbAudioInfoSize(const FlbInfo *info)
{
    return info->version == FLB_VERSION_AUDIO ? FLB_AUDIO_INFO_SIZE : 0;
}

bool flbIsCompacted(const FlbInfo *info)
{
    return info->version == FLB_VERSION_COMPACT || (info->version == FLB_VERSION_AUDIO && info->slotStride == 0);
}

uint32_t flbAudioSamples(const FlbInfo *info)
{
    return (uint32_t)(((uint64_t)info->frameCount * info->audioRateMhz / info->fps + 500) / 1000);
}

// The same checks as convert_flashback.bat, with the same messages.
bool flbReadInfo(FILE *file, FlbInfo *info, const char **error)
{
    uint8_t t[FLB_TRAILER_SIZE];

    if (fseek(file, 0, SEEK_END) != 0)
    {
        *error = "the file couldn't be read";
        return false;
    }
    long length = ftell(file);
    if (length < FLB_TRAILER_SIZE)
    {
        *error = "not a Flashback clip (file too small)";
        return false;
    }
    if (fseek(file, length - FLB_TRAILER_SIZE, SEEK_SET) != 0 || fread(t, 1, sizeof(t), file) != sizeof(t))
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
    info->version = flbU32(t + 4);
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
    info->fileSize = (uint64_t)length;

    if (info->width == 0 || info->height == 0 || info->width > 400 || info->height > 240 ||
        info->slotBytes != info->width * info->height * 2 || info->fps == 0 || info->fps > 60 ||
        (info->version == FLB_VERSION_RECORDED && info->slotStride < info->slotBytes) ||
        info->entryCount > (uint32_t)length / 12 ||
        (uint64_t)info->indexOffset + (uint64_t)info->entryCount * flbEntrySize(info) + flbAudioInfoSize(info) +
            FLB_TRAILER_SIZE != info->fileSize)
    {
        *error = "the clip's header is damaged";
        return false;
    }

    if (info->version == FLB_VERSION_AUDIO)
    {
        uint8_t a[FLB_AUDIO_INFO_SIZE];
        if (fseek(file, (long)(info->indexOffset + info->entryCount * 16), SEEK_SET) != 0 ||
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

bool flbOpen(FlbClip *clip, const char *path, const char **error)
{
    memset(clip, 0, sizeof(*clip));
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

    const FlbInfo *info = &clip->info;
    uint32_t entrySize = flbEntrySize(info);
    uint8_t *index = malloc((size_t)info->entryCount * entrySize + 1);
    clip->entries = malloc((size_t)info->entryCount * sizeof(FlbEntry) + 1);
    clip->stored = malloc(info->slotBytes);
    if (index == NULL || clip->entries == NULL || clip->stored == NULL)
    {
        free(index);
        *error = "not enough memory for this clip";
        goto fail;
    }
    if (fseek(clip->file, info->indexOffset, SEEK_SET) != 0 ||
        fread(index, entrySize, info->entryCount, clip->file) != info->entryCount)
    {
        free(index);
        *error = "the file couldn't be read";
        goto fail;
    }

    uint32_t frames = 0;
    for (uint32_t e = 0; e < info->entryCount; e++)
    {
        FlbEntry *entry = &clip->entries[e];
        const uint8_t *p = index + e * entrySize;
        uint32_t where = flbU32(p);     // version 1: ring slot, versions 2 and 3: offset
        uint64_t offset = info->version == FLB_VERSION_RECORDED ? (uint64_t)where * info->slotStride : where;
        entry->size = flbU32(p + 4);
        entry->copies = flbU32(p + 8);
        entry->audioPos = info->hasAudio ? flbU32(p + 12) : 0;
        entry->firstFrame = frames;
        if (entry->size == 0 || entry->size > info->slotBytes || entry->copies == 0 ||
            entry->copies > info->frameCount - frames || offset + entry->size > info->indexOffset)
        {
            free(index);
            snprintf(g_error, sizeof(g_error), "frame entry %lu is damaged", (unsigned long)e);
            *error = g_error;
            goto fail;
        }
        entry->offset = (uint32_t)offset;
        frames += entry->copies;
    }
    free(index);
    if (frames != info->frameCount || info->entryCount == 0)
    {
        *error = "the clip's frame count doesn't match its index";
        goto fail;
    }
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
    memset(clip, 0, sizeof(*clip));
}

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

bool flbDecodeEntry(FlbClip *clip, uint32_t entry, uint16_t *dst, const char **error)
{
    const FlbInfo *info = &clip->info;
    const FlbEntry *e = &clip->entries[entry];
    bool raw = e->size == info->slotBytes;
    uint8_t *buf = raw ? (uint8_t *)dst : clip->stored;

    if (fseek(clip->file, (long)e->offset, SEEK_SET) != 0 ||
        fread(buf, 1, e->size, clip->file) != e->size)
    {
        *error = "the file couldn't be read";
        return false;
    }
    if (!raw && !flbDecompress(buf, e->size, dst, info->width * info->height))
    {
        snprintf(g_error, sizeof(g_error), "frame %lu is damaged", (unsigned long)entry);
        *error = g_error;
        return false;
    }
    return true;
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
        if (fseek(clip->file, (long)(info->audioOffset + (uint64_t)pos * 4), SEEK_SET) != 0 ||
            fread(dst + (a - first) * 2, 4, n, clip->file) != n)
        {
            *error = "the clip's audio couldn't be read";
            return false;
        }
        a += n;
    }
    return true;
}

uint64_t flbCompactSize(const FlbClip *clip)
{
    const FlbInfo *info = &clip->info;
    uint64_t size = (uint64_t)info->entryCount * flbEntrySize(info) + flbAudioInfoSize(info) + FLB_TRAILER_SIZE;
    for (uint32_t e = 0; e < info->entryCount; e++)
        size += clip->entries[e].size;
    if (info->hasAudio)
        size += (uint64_t)flbAudioSamples(info) * 4;
    return size;
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

bool flbCompactBegin(FlbCompactor *c, const char *path, const char **error)
{
    memset(c, 0, sizeof(*c));
    if (strlen(path) >= sizeof(c->path))
    {
        *error = "the clip's name is too long";
        return false;
    }
    strcpy(c->path, path);
    snprintf(c->tmpPath, sizeof(c->tmpPath), "%s.tmp", path);

    if (!flbOpen(&c->clip, path, error))
        return false;
    const FlbInfo *info = &c->clip.info;
    if (flbIsCompacted(info))
    {
        flbClose(&c->clip);
        *error = "this clip is already compacted";
        return false;
    }

    // Just the audio the clip's frames last, from the first one on.
    if (info->hasAudio)
    {
        c->audioStart = c->clip.entries[0].audioPos;
        c->audioCount = flbAudioSamples(info);
    }
    c->total = flbCompactSize(&c->clip) - (uint64_t)info->entryCount * flbEntrySize(info) -
               flbAudioInfoSize(info) - FLB_TRAILER_SIZE;
    c->buf = malloc(FLB_COMPACT_BUFFER);
    c->offsets = malloc((size_t)c->clip.info.entryCount * sizeof(uint32_t));
    if (c->buf == NULL || c->offsets == NULL)
    {
        flbCompactFree(c);
        *error = "not enough memory to compact this clip";
        return false;
    }
    c->out = fopen(c->tmpPath, "wb");
    if (c->out == NULL)
    {
        flbCompactFree(c);
        *error = "the compacted copy couldn't be created";
        return false;
    }
    setvbuf(c->out, NULL, _IONBF, 0);   // (it's written in whole blocks already)
    // The copy at its full size up front: its SD space is claimed once,
    // instead of the file growing with every block.
    if (ftruncate(fileno(c->out), (off_t)flbCompactSize(&c->clip)) != 0)
    {
        flbCompactCancel(c);
        *error = "the compacted copy couldn't be created (is the SD card full?)";
        return false;
    }
    return true;
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

// Writes the index (audio info) and trailer, then swaps the copy in for the
// clip: the clip is renamed out of the way first, so there's always a whole
// clip on the SD card (see flbCleanUpLeftover).
static bool flbCompactFinish(FlbCompactor *c, const char **error)
{
    const FlbInfo *info = &c->clip.info;
    uint32_t entrySize = flbEntrySize(info);
    uint32_t indexSize = info->entryCount * entrySize + flbAudioInfoSize(info);
    uint8_t *index = malloc(indexSize + FLB_TRAILER_SIZE);
    if (index == NULL)
    {
        *error = "not enough memory to compact this clip";
        return false;
    }
    for (uint32_t e = 0; e < info->entryCount; e++)
    {
        const FlbEntry *entry = &c->clip.entries[e];
        uint8_t *p = index + e * entrySize;
        flbPut32(p, c->offsets[e]);
        flbPut32(p + 4, entry->size);
        flbPut32(p + 8, entry->copies);
        if (info->hasAudio)     // (the copy's audio starts at the first frame)
            flbPut32(p + 12, entry->audioPos > c->audioStart ? entry->audioPos - c->audioStart : 0);
    }
    if (info->hasAudio)
    {
        uint8_t *a = index + info->entryCount * entrySize;
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
    flbPut32(t + 4, info->hasAudio ? FLB_VERSION_AUDIO : FLB_VERSION_COMPACT);
    flbPut32(t + 8, info->width);
    flbPut32(t + 12, info->height);
    flbPut32(t + 16, info->fps);
    flbPut32(t + 20, 0);    // no slots
    flbPut32(t + 24, info->slotBytes);
    flbPut32(t + 28, info->entryCount);
    flbPut32(t + 32, info->frameCount);
    flbPut32(t + 36, (uint32_t)c->copied);

    bool ok = flbCompactPut(c, index, indexSize + FLB_TRAILER_SIZE) && flbCompactFlush(c);
    free(index);
    ok = fclose(c->out) == 0 && ok;
    c->out = NULL;
    if (!ok)
    {
        *error = "the compacted copy couldn't be written (is the SD card full?)";
        return false;
    }

    // Check the copy reads back as a whole clip before it replaces the original.
    FlbClip check;
    if (!flbOpen(&check, c->tmpPath, error))
        return false;
    ok = check.info.frameCount == info->frameCount && check.info.entryCount == info->entryCount &&
         check.info.fileSize == c->copied + indexSize + FLB_TRAILER_SIZE;
    flbClose(&check);
    if (!ok)
    {
        *error = "the compacted copy didn't read back correctly";
        return false;
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

    if (c->next < info->entryCount)
    {
        // (Read into the clip's frame buffer: a frame can straddle two blocks.)
        const FlbEntry *e = &c->clip.entries[c->next];
        if (fseek(c->clip.file, (long)e->offset, SEEK_SET) != 0 ||
            fread(c->clip.stored, 1, e->size, c->clip.file) != e->size)
        {
            *error = "the clip couldn't be read";
            goto fail;
        }
        if (!flbCompactPut(c, c->clip.stored, e->size))
        {
            *error = "the compacted copy couldn't be written (is the SD card full?)";
            goto fail;
        }
        c->offsets[c->next++] = (uint32_t)c->copied;
        c->copied += e->size;
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
            *error = "the compacted copy couldn't be written (is the SD card full?)";
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

// Decodes the recorder's frame compression (see flashbackCompress in
// Luma3DS-Flashback's flashback.c).
bool flbDecompress(const uint8_t *src, uint32_t len, uint16_t *dst, uint32_t pixels)
{
    uint16_t index[64];
    memset(index, 0, sizeof(index));
    uint16_t px = 0;
    const uint8_t *p = src, *end = src + len;
    uint32_t i = 0;

    while (i < pixels)
    {
        if (p >= end)
            return false;
        uint8_t b = *p++;

        if (b == 0xFE)
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
                dst[i++] = px;
            continue;
        }
        else if ((b & 0xC0) == 0x00)
        {
            px = index[b];
            dst[i++] = px;
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
        dst[i++] = px;
    }
    return p == end;
}
