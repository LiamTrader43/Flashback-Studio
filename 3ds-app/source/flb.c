#include "flb.h"

#include <stdlib.h>
#include <string.h>

#define FLB_MAGIC           0x4B424C46  // "FLBK"
#define FLB_VERSION_RECORDED 1
#define FLB_VERSION_COMPACT  2

// Frames are written to a compacted copy in batches of up to this many bytes:
// every SD write has a fixed cost, so fewer, bigger writes are faster.
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
    info->version = flbU32(t + 4);
    if (info->version != FLB_VERSION_RECORDED && info->version != FLB_VERSION_COMPACT)
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
        (uint64_t)info->indexOffset + (uint64_t)info->entryCount * 12 + FLB_TRAILER_SIZE != info->fileSize)
    {
        *error = "the clip's header is damaged";
        return false;
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
    if (!flbReadInfo(clip->file, &clip->info, error))
        goto fail;

    const FlbInfo *info = &clip->info;
    uint8_t *index = malloc((size_t)info->entryCount * 12 + 1);
    clip->entries = malloc((size_t)info->entryCount * sizeof(FlbEntry) + 1);
    clip->stored = malloc(info->slotBytes);
    if (index == NULL || clip->entries == NULL || clip->stored == NULL)
    {
        free(index);
        *error = "not enough memory for this clip";
        goto fail;
    }
    if (fseek(clip->file, info->indexOffset, SEEK_SET) != 0 ||
        fread(index, 12, info->entryCount, clip->file) != info->entryCount)
    {
        free(index);
        *error = "the file couldn't be read";
        goto fail;
    }

    uint32_t frames = 0;
    for (uint32_t e = 0; e < info->entryCount; e++)
    {
        FlbEntry *entry = &clip->entries[e];
        uint32_t where = flbU32(index + e * 12);    // version 1: ring slot, version 2: offset
        uint64_t offset = info->version == FLB_VERSION_RECORDED ? (uint64_t)where * info->slotStride : where;
        entry->size = flbU32(index + e * 12 + 4);
        entry->copies = flbU32(index + e * 12 + 8);
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

uint64_t flbCompactSize(const FlbClip *clip)
{
    uint64_t size = (uint64_t)clip->info.entryCount * 12 + FLB_TRAILER_SIZE;
    for (uint32_t e = 0; e < clip->info.entryCount; e++)
        size += clip->entries[e].size;
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
    if (c->clip.info.version != FLB_VERSION_RECORDED)
    {
        flbClose(&c->clip);
        *error = "this clip is already compacted";
        return false;
    }

    c->total = flbCompactSize(&c->clip) - (uint64_t)c->clip.info.entryCount * 12 - FLB_TRAILER_SIZE;
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
    setvbuf(c->out, NULL, _IONBF, 0);   // (it's written in big batches already)
    return true;
}

static bool flbCompactFlush(FlbCompactor *c)
{
    bool ok = c->bufLen == 0 || fwrite(c->buf, 1, c->bufLen, c->out) == c->bufLen;
    c->bufLen = 0;
    return ok;
}

// Writes the index and trailer, then swaps the copy in for the clip: the clip
// is renamed out of the way first, so there's always a whole clip on the SD
// card (see flbCleanUpLeftover).
static bool flbCompactFinish(FlbCompactor *c, const char **error)
{
    const FlbInfo *info = &c->clip.info;
    uint32_t indexSize = info->entryCount * 12;
    uint8_t *index = malloc(indexSize + FLB_TRAILER_SIZE);
    if (index == NULL)
    {
        *error = "not enough memory to compact this clip";
        return false;
    }
    for (uint32_t e = 0; e < info->entryCount; e++)
    {
        flbPut32(index + e * 12, c->offsets[e]);
        flbPut32(index + e * 12 + 4, c->clip.entries[e].size);
        flbPut32(index + e * 12 + 8, c->clip.entries[e].copies);
    }
    uint8_t *t = index + indexSize;
    memset(t, 0, FLB_TRAILER_SIZE);
    flbPut32(t, FLB_MAGIC);
    flbPut32(t + 4, FLB_VERSION_COMPACT);
    flbPut32(t + 8, info->width);
    flbPut32(t + 12, info->height);
    flbPut32(t + 16, info->fps);
    flbPut32(t + 20, 0);    // no slots
    flbPut32(t + 24, info->slotBytes);
    flbPut32(t + 28, info->entryCount);
    flbPut32(t + 32, info->frameCount);
    flbPut32(t + 36, (uint32_t)c->copied);

    bool ok = fwrite(index, 1, indexSize + FLB_TRAILER_SIZE, c->out) == indexSize + FLB_TRAILER_SIZE;
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
        const FlbEntry *e = &c->clip.entries[c->next];
        if (c->bufLen + e->size > FLB_COMPACT_BUFFER && !flbCompactFlush(c))
        {
            *error = "the compacted copy couldn't be written (is the SD card full?)";
            goto fail;
        }
        if (fseek(c->clip.file, (long)e->offset, SEEK_SET) != 0 ||
            fread(c->buf + c->bufLen, 1, e->size, c->clip.file) != e->size)
        {
            *error = "the clip couldn't be read";
            goto fail;
        }
        c->offsets[c->next++] = (uint32_t)c->copied;
        c->bufLen += e->size;
        c->copied += e->size;
        return 0;
    }

    if (!flbCompactFlush(c))
    {
        *error = "the compacted copy couldn't be written (is the SD card full?)";
        goto fail;
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

// Ported from flashbackDecompress in Luma3DS-Flashback's flashback.c.
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
