/* PC test of Flashback Studio's clip reader and compactor (3ds-app/source/flb.c,
   built as it is): small clips as the recorder saves them (with and without
   sound, with and without inputs, with copies of missed frames) are opened,
   compacted and trimmed (from the start, the end and part way through a
   frame's copies), and every frame of every copy must show the same picture
   and the same inputs as in the original. A clip whose inputs are damaged must
   still open and play, without them. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "flb.h"

#define W 8
#define H 6
#define SLOT_BYTES (W * H * 2)
#define SLOT_STRIDE (SLOT_BYTES + 32)
#define FPS 30
#define RATE_MHZ 32728498u
#define MAX_ENTRIES 64

static long errors, checks;
#define FAIL(...) do { errors++; if (errors < 30) printf("  ERROR: " __VA_ARGS__); } while (0)

static void put32(FILE *f, uint32_t v) { uint8_t b[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) }; fwrite(b, 1, 4, f); }
static void put16(FILE *f, uint16_t v) { uint8_t b[2] = { (uint8_t)v, (uint8_t)(v >> 8) }; fwrite(b, 1, 2, f); }
static void zeros(FILE *f, long n) { while (n-- > 0) fputc(0, f); }

/* frame f's inputs, as the recorder would have recorded them */
static FlbInput inputOf(uint32_t f)
{
    FlbInput in = { (f * 2654435761u) >> 3 | 1u << 20, (int16_t)(f * 3 % 313 - 156), (int16_t)(156 - f * 7 % 313),
                    (uint16_t)(f % 320), (uint16_t)(f % 240), (int16_t)(f & 0xFF), (int16_t)-(int32_t)(f & 0x7F) };
    return in;
}

/* entry e's picture */
static uint16_t pixelOf(uint32_t e, uint32_t i) { return (uint16_t)(e * 977 + i * 31 + 5); }

/* A clip as the recorder saves it: 'entries' frames, entry e shown copies[e] times. */
static uint32_t writeRecorded(const char *path, bool audio, bool inputs, uint32_t entries, const uint32_t *copies)
{
    uint32_t frames = 0;
    for (uint32_t e = 0; e < entries; e++) frames += copies[e];
    uint32_t capacity = (uint32_t)((uint64_t)(frames + FPS) * RATE_MHZ / (FPS * 1000));   /* (room to spare) */
    uint32_t videoOffset = audio ? (capacity * 4 + 511) / 512 * 512 : 0;
    uint32_t dataEnd = videoOffset + entries * SLOT_STRIDE;
    uint32_t inputSize = inputs ? 16 + frames * 16 : 0;

    FILE *f = fopen(path, "wb");
    for (uint32_t i = 0; audio && i < capacity; i++) { put16(f, (uint16_t)i); put16(f, (uint16_t)~i); }
    zeros(f, (long)videoOffset - ftell(f));
    for (uint32_t e = 0; e < entries; e++)
    {
        for (uint32_t i = 0; i < W * H; i++) put16(f, pixelOf(e, i));
        zeros(f, SLOT_STRIDE - SLOT_BYTES);
    }
    if (inputs)
    {
        put32(f, 0x49424C46); put32(f, 1); put32(f, frames); put32(f, 16);
        for (uint32_t i = 0; i < frames; i++)
        {
            FlbInput in = inputOf(i);
            put32(f, in.buttons); put16(f, (uint16_t)in.circleX); put16(f, (uint16_t)in.circleY);
            put16(f, in.touchX); put16(f, in.touchY); put16(f, (uint16_t)in.cstickX); put16(f, (uint16_t)in.cstickY);
        }
    }
    uint32_t first = 0;
    for (uint32_t e = 0; e < entries; e++)
    {
        put32(f, audio ? videoOffset + e * SLOT_STRIDE : e);
        put32(f, SLOT_BYTES);
        put32(f, copies[e]);
        if (audio) put32(f, (uint32_t)((uint64_t)first * RATE_MHZ / (FPS * 1000)));
        first += copies[e];
    }
    if (audio)
    {
        put32(f, RATE_MHZ); put16(f, 2); put16(f, 16); put32(f, 0); put32(f, capacity);
        put32(f, 0); put32(f, capacity); put32(f, 0); put32(f, 0);
    }
    put32(f, 0x4B424C46); put32(f, audio ? 3 : 1); put32(f, W); put32(f, H); put32(f, FPS);
    put32(f, SLOT_STRIDE); put32(f, SLOT_BYTES); put32(f, entries); put32(f, frames);
    put32(f, dataEnd + inputSize); put32(f, inputs ? dataEnd : 0); put32(f, 0);
    fclose(f);
    return frames;
}

/* Every frame of 'path' must be frames first.. of the original: the same picture and inputs. */
static void checkCopy(const char *what, const char *path, uint32_t first, uint32_t count, const uint32_t *entryOfFrame,
                      bool inputs, uint32_t wantVersion)
{
    FlbClip clip;
    const char *error = NULL;
    static uint16_t px[W * H];
    checks++;
    if (!flbOpen(&clip, path, &error)) { FAIL("%s: won't open: %s\n", what, error); return; }
    if (clip.info.frameCount != count) FAIL("%s: %u frames, want %u\n", what, clip.info.frameCount, count);
    if (clip.info.version != wantVersion) FAIL("%s: version %u, want %u\n", what, clip.info.version, wantVersion);
    if ((clip.inputs != NULL) != inputs) FAIL("%s: inputs %s\n", what, inputs ? "lost" : "where there were none");
    for (uint32_t i = 0; i < clip.info.frameCount && i < count; i++)
    {
        uint32_t e = flbEntryForFrame(&clip, i);
        if (!flbDecodeEntry(&clip, e, px, &error)) { FAIL("%s: frame %u: %s\n", what, i, error); break; }
        if (px[0] != pixelOf(entryOfFrame[first + i], 0) || px[W * H - 1] != pixelOf(entryOfFrame[first + i], W * H - 1))
        { FAIL("%s: frame %u shows the wrong picture\n", what, i); break; }
        if (inputs && clip.inputs != NULL)
        {
            FlbInput want = inputOf(first + i);
            if (memcmp(&clip.inputs[i], &want, sizeof(want)) != 0)
            { FAIL("%s: frame %u's inputs are %08x, want %08x\n", what, i, clip.inputs[i].buttons, want.buttons); break; }
        }
    }
    flbClose(&clip);
}

static bool runCopy(FlbCompactor *c)
{
    const char *error = NULL;
    int r;
    while ((r = flbCompactStep(c, &error)) == 0)
        ;
    if (r < 0) FAIL("copying failed: %s\n", error);
    return r > 0;
}

static void copyFile(const char *from, const char *to)
{
    FILE *a = fopen(from, "rb"), *b = fopen(to, "wb");
    int ch;
    while ((ch = fgetc(a)) != EOF) fputc(ch, b);
    fclose(a);
    fclose(b);
}

/* Changes 4 bytes at 'offset' (from the end if negative) of a file. */
static void patch32(const char *path, long offset, uint32_t v)
{
    FILE *f = fopen(path, "r+b");
    fseek(f, offset, offset < 0 ? SEEK_END : SEEK_SET);
    put32(f, v);
    fclose(f);
}

int main(void)
{
    srand(99);
    const char *rec = "flbtest_rec.flb", *cmp = "flbtest_cmp.flb", *trim = "flbtest_trim.flb";

    for (int run = 0; run < 60; run++)
    {
        bool audio = run % 3 != 0, inputs = run % 4 != 1;
        uint32_t entries = 1 + rand() % MAX_ENTRIES, copies[MAX_ENTRIES], entryOfFrame[MAX_ENTRIES * 4];
        uint32_t n = 0;
        for (uint32_t e = 0; e < entries; e++)
        {
            copies[e] = rand() % 4 == 0 ? 2 + rand() % 3 : 1;     /* (missed frames: copies) */
            for (uint32_t k = 0; k < copies[e]; k++) entryOfFrame[n++] = e;
        }
        uint32_t frames = writeRecorded(rec, audio, inputs, entries, copies);
        uint32_t compactVersion = audio ? 3 : 2;
        checkCopy("recorded", rec, 0, frames, entryOfFrame, inputs, audio ? 3 : 1);

        /* compacted: every frame, and the inputs */
        FlbCompactor c;
        const char *error = NULL;
        copyFile(rec, cmp);
        if (flbCompactBegin(&c, cmp, &error) && runCopy(&c))
            checkCopy("compacted", cmp, 0, frames, entryOfFrame, inputs, compactVersion);
        else if (error) FAIL("compacting: %s\n", error);

        /* trimmed, from the recorded clip and from the compacted one */
        for (int t = 0; t < 6; t++)
        {
            uint32_t first = rand() % frames, count = 1 + rand() % (frames - first);
            if (t == 0) { first = 0; count = frames > 1 ? frames - 1 : 1; }     /* the start */
            if (t == 1) { first = frames > 1 ? 1 : 0; count = frames - first; } /* the end */
            const char *from = t % 2 ? cmp : rec;
            remove(trim);
            if (flbTrimBegin(&c, from, trim, first, count, &error) && runCopy(&c))
                checkCopy(t % 2 ? "trimmed (compacted)" : "trimmed", trim, first, count, entryOfFrame, inputs, compactVersion);
            else if (error) FAIL("trimming %u+%u: %s\n", first, count, error);
            error = NULL;
        }
    }

    /* damaged inputs: the clip still opens and plays, without them */
    uint32_t copies[3] = { 1, 3, 1 }, entryOfFrame[5] = { 0, 1, 1, 1, 2 };
    struct { const char *what; long offset; uint32_t value; } damage[] = {
        { "inputs' magic wrong", 0, 0x12345678 },
        { "inputs' count wrong", 8, 4 },
        { "inputs' record size too small", 12, 8 },
        { "inputs past the index", -1, 0 },
    };
    for (int d = 0; d < 4; d++)
    {
        uint32_t frames = writeRecorded(rec, true, true, 3, copies);
        FlbClip clip;
        const char *error = NULL;
        flbOpen(&clip, rec, &error);
        uint32_t inputOffset = clip.info.inputOffset, indexOffset = clip.info.indexOffset;
        flbClose(&clip);
        if (damage[d].offset >= 0)
            patch32(rec, inputOffset + damage[d].offset, damage[d].value);
        else
            patch32(rec, -8, indexOffset - 8);      /* (the trailer's input offset: too near the index) */
        checkCopy(damage[d].what, rec, 0, frames, entryOfFrame, false, 3);
    }

    remove(rec);
    remove(cmp);
    remove(trim);
    printf("%ld clips checked\n%s (%ld errors)\n", checks, errors ? "FAILED" : "all good", errors);
    return errors != 0;
}
