/* PC test of Flashback Studio's decoding of frames packed with Huffman codes
   (3ds-app/source/flb.c, built as it is), with frames made as the recorder
   makes them (a copy of its compression, recorder_copy.h):
   - frames of many kinds, at both resolutions, packed one after another as
     recording does (each frame's codes from the counts of the frame before),
     must decode to exactly the frame, and so must their ops unpacked;
   - damaged packed frames (cut short, the wrong format, too many or too few
     bytes, codes that don't fit, a code too long) must fail, and with bits
     flipped at random must never write past the frame;
   - clips with packed frames, as the recorder saves them (with the version
     flag, with and without sound), must open and play, and keep the flag and
     every frame when compacted and trimmed. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "flb.h"

static long errors, checks;
#define FAIL(...) do { errors++; if (errors < 30) printf("  ERROR: " __VA_ARGS__); } while (0)

/* The recorder's compression: a copy (recorder_copy.h), shared with screens_test.c. */
#include "recorder_copy.h"

static FlashbackHuffman g_huffState;

/* ---------------------------------------------------------------------------
   Frames
   --------------------------------------------------------------------------- */
static u32 g_rng = 12345;
static u32 rnd(void) { g_rng ^= g_rng << 13; g_rng ^= g_rng >> 17; g_rng ^= g_rng << 5; return g_rng; }
static u16 rgb(int r, int g, int b) { return (u16)((r & 31) << 11 | (g & 63) << 5 | (b & 31)); }
static int clampTo(int v, int max) { return v < 0 ? 0 : v > max ? max : v; }

enum { GAME, TEXTURE, FLAT, PALETTE, NOISE, SKEWED, KINDS };

/* Frame t of a kind, upright. */
static void makeFrame(u16 *up, u32 w, u32 h, int kind, u32 t)
{
    u16 palette[16];
    for (int i = 0; i < 16; i++)
        palette[i] = (u16)rnd();
    for (u32 y = 0; y < h; y++)
        for (u32 x = 0; x < w; x++)
        {
            int n = (int)(rnd() % 7) - 3;
            u16 *p = &up[y * w + x];
            switch (kind)
            {
            case GAME:
                if (x < w / 5 && y > h * 4 / 5)
                    *p = rgb(4, 40, 20);
                else if (y < h / 3)
                    *p = rgb(8 + (int)(y * 12 / h), 30 + (int)((y + t) * 20 / h), 28);
                else
                    *p = rgb(clampTo(6 + n + (int)((x + t) % 9), 31), clampTo(30 + 2 * n + (int)(y % 13), 63), clampTo(5 + n, 31));
                break;
            case TEXTURE:
                *p = rgb(clampTo(12 + (int)(x / 40) + n * 2, 31), clampTo(24 + (int)(y / 20) + n * 3, 63), clampTo(10 + n * 2, 31));
                break;
            case FLAT:
                *p = rgb((int)((x / 100 + t) % 4) * 8, (int)(y / 60) * 16, 12);
                break;
            case PALETTE:
                *p = palette[rnd() % 16];
                break;
            case NOISE:
                *p = (u16)rnd();
                break;
            case SKEWED:
                *p = rnd() % 97 == 0 ? (u16)rnd() : rgb((int)(x + y), (int)(x + y), (int)(x + y));
                break;
            }
        }
}

/* ---------------------------------------------------------------------------
   Clips with packed frames
   --------------------------------------------------------------------------- */
#define CLIP_W 200
#define CLIP_H 120
#define CLIP_PIXELS (CLIP_W * CLIP_H)
#define CLIP_SLOT (CLIP_PIXELS * 2)
#define CLIP_STRIDE (CLIP_SLOT + 512)
#define CLIP_ENTRIES 24
#define FPS 30
#define RATE_MHZ 32728498u

static u16 g_clipFrames[CLIP_ENTRIES][CLIP_PIXELS];

static void put32(FILE *f, uint32_t v) { uint8_t b[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) }; fwrite(b, 1, 4, f); }
static void put16(FILE *f, uint16_t v) { uint8_t b[2] = { (uint8_t)v, (uint8_t)(v >> 8) }; fwrite(b, 1, 2, f); }

/* A clip as the recorder saves it, with the version flag: CLIP_ENTRIES frames
   (packed with codes where they come out smaller, else their ops, or raw),
   entry e shown 1 + e % 3 times. Returns its frame count; sets how many
   frames were packed. */
static uint32_t writeClip(const char *path, bool audio, uint32_t *packed)
{
    static u8 ops[CLIP_SLOT], coded[CLIP_SLOT];
    uint32_t frames = 0, sizes[CLIP_ENTRIES];
    for (uint32_t e = 0; e < CLIP_ENTRIES; e++) frames += 1 + e % 3;
    uint32_t capacity = (uint32_t)((uint64_t)(frames + FPS) * RATE_MHZ / (FPS * 1000));
    uint32_t videoOffset = audio ? (capacity * 4 + 0xFFFF) / 0x10000 * 0x10000 : 0;
    uint32_t dataEnd = videoOffset + CLIP_ENTRIES * CLIP_STRIDE;

    FILE *f = fopen(path, "wb");
    for (uint32_t i = 0; audio && i < capacity; i++) { put16(f, (uint16_t)i); put16(f, (uint16_t)~i); }
    memset(&g_huffState, 0, sizeof(g_huffState));
    *packed = 0;
    for (uint32_t e = 0; e < CLIP_ENTRIES; e++)
    {
        makeFrame(g_clipFrames[e], CLIP_W, CLIP_H, e < 8 ? GAME : e < 16 ? TEXTURE : e % 4 == 0 ? NOISE : FLAT, e);
        uint32_t n = compressFrame(g_clipFrames[e], CLIP_W, CLIP_H, ops, CLIP_SLOT);
        uint32_t len = n ? flashbackHuffman(&g_huffState, ops, n, coded) : 0;
        const void *data = len ? (const void *)coded : n ? (const void *)ops : (const void *)g_clipFrames[e];
        sizes[e] = len ? len : n ? n : CLIP_SLOT;
        *packed += len != 0;
        fseek(f, (long)(videoOffset + e * CLIP_STRIDE), SEEK_SET);
        fwrite(data, 1, sizes[e], f);
    }
    fseek(f, (long)dataEnd, SEEK_SET);
    uint32_t first = 0;
    for (uint32_t e = 0; e < CLIP_ENTRIES; e++)
    {
        put32(f, audio ? videoOffset + e * CLIP_STRIDE : e);
        put32(f, sizes[e]);
        put32(f, 1 + e % 3);
        if (audio) put32(f, (uint32_t)((uint64_t)first * RATE_MHZ / (FPS * 1000)));
        first += 1 + e % 3;
    }
    if (audio)
    {
        put32(f, RATE_MHZ); put16(f, 2); put16(f, 16); put32(f, 0); put32(f, capacity);
        put32(f, 0); put32(f, capacity); put32(f, 0); put32(f, 0);
    }
    put32(f, 0x4B424C46); put32(f, (audio ? 3 : 1) | FLB_VERSION_CODES); put32(f, CLIP_W); put32(f, CLIP_H); put32(f, FPS);
    put32(f, CLIP_STRIDE); put32(f, CLIP_SLOT); put32(f, CLIP_ENTRIES); put32(f, frames);
    put32(f, dataEnd); put32(f, 0); put32(f, 0);
    fclose(f);
    return frames;
}

/* Every entry of 'path' must be the clip's frames from 'firstEntry' on, and it must have the flag. */
static void checkClip(const char *what, const char *path, uint32_t firstEntry, uint32_t wantVersion)
{
    FlbClip clip;
    const char *error = NULL;
    static u16 px[CLIP_PIXELS];
    checks++;
    if (!flbOpen(&clip, path, &error)) { FAIL("%s: won't open: %s\n", what, error); return; }
    if (clip.info.version != wantVersion || !clip.info.hasCodes)
        FAIL("%s: version %u%s, want %u with codes\n", what, clip.info.version, clip.info.hasCodes ? " with codes" : "", wantVersion);
    for (uint32_t e = 0; e < clip.info.entryCount; e++)
    {
        if (!flbDecodeEntry(&clip, e, px, &error)) { FAIL("%s: entry %u: %s\n", what, e, error); break; }
        if (firstEntry + e >= CLIP_ENTRIES || memcmp(px, g_clipFrames[firstEntry + e], sizeof(px)) != 0)
        { FAIL("%s: entry %u shows the wrong picture\n", what, e); break; }
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

int main(void)
{
    static FlbCodes codes;
    static u16 up[400 * 240], back[400 * 240 + 64];
    static u8 ops[400 * 240 * 2], coded[400 * 240 * 2], damaged[400 * 240 * 2];

    /* frames one after another, as recording packs them */
    long frames = 0, packed = 0, damagedChecks = 0;
    double opsBytes = 0, packedBytes = 0;
    int kind = GAME;
    u32 t = 0;
    for (int f = 0; f < 1200; f++, t++)
    {
        if (rnd() % 30 == 0)
        {
            kind = (int)(rnd() % KINDS);
            t = 0;
        }
        bool full = (f / 300) % 2 == 0;
        u32 w = full ? 400 : 200, h = full ? 240 : 120, pixels = w * h;
        makeFrame(up, w, h, kind, t);
        frames++;
        u32 n = compressFrame(up, w, h, ops, pixels * 2);
        if (n == 0)
            continue;
        checks++;
        if (!flbDecompress(ops, n, back, pixels, &codes) || memcmp(back, up, pixels * 2) != 0)
            FAIL("frame %d: its ops don't decode to it\n", f);
        u32 len = flashbackHuffman(&g_huffState, ops, n, coded);
        if (len == 0)
            continue;
        packed++;
        opsBytes += n;
        packedBytes += len;
        for (u32 i = 0; i < 64; i++) back[pixels + i] = 0xA5A5;
        if (!flbDecompress(coded, len, back, pixels, &codes) || memcmp(back, up, pixels * 2) != 0)
            FAIL("frame %d: packed with codes, it doesn't decode to the frame\n", f);
        if (flbDecompress(coded, len, back, pixels, NULL))
            FAIL("frame %d: decoded without anywhere to decode its codes\n", f);

        /* damaged: these must fail */
        if (f % 4 == 0)
        {
            struct { const char *what; u32 len; int at; u8 value; u32 count; } bad[] = {
                { "cut to half", len / 2, -1, 0, 0 },
                { "cut to its header", 264, -1, 0, 0 },
                { "shorter than its header", 263, -1, 0, 0 },
                { "the wrong format", len, 1, 2, 0 },
                { "a byte too many", len, -1, 0, n + 1 },
                { "a byte too few", len, -1, 0, n - 1 },
                { "more bytes than a frame", len, -1, 0, pixels * 2 + 1 },
                { "every code 1 bit (too many)", len, -2, 0x11, 0 },
                { "a 13-bit code", len, 8, 0xDD, 0 },
            };
            for (u32 b = 0; b < sizeof(bad) / sizeof(bad[0]); b++)
            {
                memcpy(damaged, coded, len);
                if (bad[b].at >= 0)
                    damaged[bad[b].at] = bad[b].value;
                if (bad[b].at == -2)
                    memset(damaged + 8, bad[b].value, 128);
                if (bad[b].count != 0)
                    memcpy(damaged + 4, &bad[b].count, 4);
                damagedChecks++;
                if (flbDecompress(damaged, bad[b].len, back, pixels, &codes))
                    FAIL("frame %d, %s: decoded anyway\n", f, bad[b].what);
                for (u32 i = 0; i < 64; i++)
                    if (back[pixels + i] != 0xA5A5) { FAIL("frame %d, %s: written past the frame\n", f, bad[b].what); break; }
            }
            /* bits flipped at random: anything but writing past the frame */
            for (int k = 0; k < 20; k++)
            {
                memcpy(damaged, coded, len);
                for (int flips = 1 + (int)(rnd() % 4); flips > 0; flips--)
                    damaged[rnd() % len] ^= (u8)(1 << rnd() % 8);
                damagedChecks++;
                flbDecompress(damaged, len, back, pixels, &codes);
                for (u32 i = 0; i < 64; i++)
                    if (back[pixels + i] != 0xA5A5) { FAIL("frame %d, bits flipped: written past the frame\n", f); break; }
            }
        }
    }
    printf("%ld frames: %ld packed with codes (to %.1f%% of their ops), all decoded; %ld damaged ones tried\n", frames,
           packed, packed ? 100.0 * packedBytes / opsBytes : 0.0, damagedChecks);
    if (packed == 0) FAIL("no frame was packed with codes\n");

    /* clips: recorded, compacted, trimmed */
    const char *rec = "codestest_rec.flb", *cmp = "codestest_cmp.flb", *trim = "codestest_trim.flb";
    for (int audio = 0; audio < 2; audio++)
    {
        uint32_t clipPacked;
        writeClip(rec, audio, &clipPacked);
        if (clipPacked < CLIP_ENTRIES / 2) FAIL("only %u of the clip's frames were packed with codes\n", clipPacked);
        checkClip(audio ? "recorded, with sound" : "recorded", rec, 0, audio ? 3 : 1);

        FlbCompactor c;
        const char *error = NULL;
        copyFile(rec, cmp);
        if (flbCompactBegin(&c, cmp, &error) && runCopy(&c))
            checkClip("compacted", cmp, 0, audio ? 3 : 2);
        else if (error) FAIL("compacting: %s\n", error);

        /* 20 frames from entry 5's first */
        uint32_t firstOf5 = 0;
        for (uint32_t e = 0; e < 5; e++) firstOf5 += 1 + e % 3;
        for (int from = 0; from < 2; from++)
        {
            remove(trim);
            error = NULL;
            if (flbTrimBegin(&c, from ? cmp : rec, trim, firstOf5, 20, &error) && runCopy(&c))
                checkClip(from ? "trimmed (compacted)" : "trimmed", trim, 5, audio ? 3 : 2);
            else if (error) FAIL("trimming: %s\n", error);
        }
    }
    remove(rec);
    remove(cmp);
    remove(trim);

    printf("%ld checks\n%s (%ld errors)\n", checks, errors ? "FAILED" : "all good", errors);
    return errors != 0;
}
