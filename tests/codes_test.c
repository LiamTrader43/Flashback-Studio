/* PC test of Flashback Studio's decoding of frames packed with Huffman codes
   (3ds-app/source/flb.c, built as it is), with frames made as the recorder
   makes them (a copy of its compression, below):
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

/* ---------------------------------------------------------------------------
   A copy of the recorder's compression (flashbackPackPixels, flashbackHuffLengths,
   flashbackHuffCodes and flashbackHuffman in Luma3DS-Flashback's
   sysmodules/rosalina/source/flashback_compress.c), for frames that are upright
   here. Keep it in step with the original.
   --------------------------------------------------------------------------- */
typedef uint8_t u8;
typedef uint16_t u16;
typedef uint32_t u32;
typedef int32_t s32;

#define FLASHBACK_PACK_RUN_MAX  62
#define FLASHBACK_PACK_RAW      0xFE
#define FLASHBACK_HUFF_MAX_BITS 12
#define FLASHBACK_HUFF_HEADER   264
#define FLASHBACK_HUFF_BLOCK    64

typedef struct FlashbackPackState { u32 *index; u8 *out; u32 prev; u32 run; } FlashbackPackState;
typedef struct FlashbackHuffman {
    u32 counts[2][256];
    u32 codes[2][256];
    u32 order[256];
    u32 weight[511];
    u16 parent[511];
    u8  depth[511];
} FlashbackHuffman;
static FlashbackHuffman g_huffState, *g_huff = &g_huffState;

static inline u32 flashbackPackHash(u32 px) { return (px * 0x9E3779B1u) >> 26; }

static bool flashbackPackPixels(const u16 *src, u32 n, u32 step, FlashbackPackState *st, const u8 *limit)
{
    u32 *index = st->index;
    u32 prev = st->prev;
    u32 run = st->run;
    u8 *out = st->out;
    const u16 *end = src + n * step;

    for (; src != end; src += step)
    {
        u32 px = *src;
        if (px == prev)
        {
            if (++run == FLASHBACK_PACK_RUN_MAX)
            {
                if (out > limit)
                    return false;
                *out++ = 0xC0 | (FLASHBACK_PACK_RUN_MAX - 1);
                run = 0;
            }
            continue;
        }

        if (out > limit)
            return false;
        if (run != 0)
        {
            *out++ = (u8)(0xBF + run);      // 0xC0 | (run - 1)
            run = 0;
        }

        u32 h = flashbackPackHash(px);
        if (index[h] == px)
        {
            *out++ = (u8)h;
            prev = px;
            continue;
        }
        index[h] = px;

        s32 dr = (s32)(((px >> 11) - (prev >> 11)) << 27) >> 27;
        s32 dg = (s32)(((px >> 5) - (prev >> 5)) << 26) >> 26;
        s32 db = (s32)((px - prev) << 27) >> 27;

        if ((u32)((dr + 2) | (dg + 2) | (db + 2)) < 4)
            *out++ = (u8)(0x40 + 0x2A + (dr << 4) + (dg << 2) + db);    // 0x40 | (dr+2)<<4 | (dg+2)<<2 | (db+2)
        else
        {
            s32 tr = dr - (dg >> 1), tb = db - (dg >> 1);
            if ((u32)((tr + 8) | (tb + 8)) < 16)
            {
                out[0] = (u8)(0x80 + 32 + dg);                      // 0x80 | (dg + 32)
                out[1] = (u8)(0x88 + (tr << 4) + tb);               // (tr+8)<<4 | (tb+8)
                out += 2;
            }
            else
            {
                out[0] = FLASHBACK_PACK_RAW;
                out[1] = (u8)px;
                out[2] = (u8)(px >> 8);
                out += 3;
            }
        }
        prev = px;
    }

    st->prev = prev;
    st->run = run;
    st->out = out;
    return true;
}

/* flashbackCompress, for an upright frame: its ops, or 0 if they wouldn't be
   smaller than 'cap' bytes. */
static u32 compressFrame(const u16 *up, u32 width, u32 height, u8 *dst, u32 cap)
{
    u32 index[64];
    memset(index, 0, sizeof(index));
    FlashbackPackState st = { index, dst, 0, 0 };
    const u8 *limit = dst + cap - 4;
    for (u32 y = 0; y < height; y++)
        if (!flashbackPackPixels(up + y * width, width, 1, &st, limit))
            return 0;
    if (st.run != 0)
    {
        if (st.out > limit)
            return 0;
        *st.out++ = 0xC0 | (st.run - 1);
    }
    return (u32)(st.out - dst);
}

static void flashbackHuffLengths(const u32 *counts, u8 *lengths)
{
    FlashbackHuffman *h = g_huff;
    u32 *order = h->order, *weight = h->weight;

    for (u32 v = 0; v < 256; v++)
        order[v] = (counts[v] + 1) << 8 | v;
    static const u8 gaps[] = { 132, 57, 23, 10, 4, 1 };
    for (u32 g = 0; g < sizeof(gaps); g++)
        for (u32 i = gaps[g]; i < 256; i++)
        {
            u32 key = order[i], j = i;
            for (; j >= gaps[g] && order[j - gaps[g]] > key; j -= gaps[g])
                order[j] = order[j - gaps[g]];
            order[j] = key;
        }

    for (u32 i = 0; i < 256; i++)
        weight[i] = order[i] >> 8;
    u32 leaf = 0, node = 256;
    for (u32 made = 256; made < 511; made++)
    {
        u32 a = leaf < 256 && (node == made || weight[leaf] <= weight[node]) ? leaf++ : node++;
        u32 b = leaf < 256 && (node == made || weight[leaf] <= weight[node]) ? leaf++ : node++;
        weight[made] = weight[a] + weight[b];
        h->parent[a] = h->parent[b] = (u16)made;
    }
    h->depth[510] = 0;
    for (u32 i = 510; i-- != 0;)
        h->depth[i] = h->depth[h->parent[i]] + 1;

    u32 perLength[FLASHBACK_HUFF_MAX_BITS + 1] = { 0 };
    for (u32 i = 0; i < 256; i++)
        perLength[h->depth[i] < FLASHBACK_HUFF_MAX_BITS ? h->depth[i] : FLASHBACK_HUFF_MAX_BITS]++;
    u32 space = 0;
    for (u32 n = 1; n <= FLASHBACK_HUFF_MAX_BITS; n++)
        space += perLength[n] << (FLASHBACK_HUFF_MAX_BITS - n);
    for (; space > 1u << FLASHBACK_HUFF_MAX_BITS; space--)
    {
        perLength[FLASHBACK_HUFF_MAX_BITS]--;
        for (u32 n = FLASHBACK_HUFF_MAX_BITS - 1; n > 0; n--)
            if (perLength[n] != 0)
            {
                perLength[n]--;
                perLength[n + 1] += 2;
                break;
            }
    }

    u32 i = 0;
    for (u32 n = FLASHBACK_HUFF_MAX_BITS; n > 0; n--)
        for (u32 k = perLength[n]; k != 0; k--)
            lengths[order[i++] & 0xFF] = (u8)n;
}

static void flashbackHuffCodes(const u8 *lengths, u32 *codes)
{
    u32 perLength[FLASHBACK_HUFF_MAX_BITS + 1] = { 0 }, next[FLASHBACK_HUFF_MAX_BITS + 1];
    for (u32 v = 0; v < 256; v++)
        perLength[lengths[v]]++;
    u32 code = 0;
    for (u32 n = 1; n <= FLASHBACK_HUFF_MAX_BITS; n++)
    {
        code = (code + perLength[n - 1]) << 1;
        next[n] = code;
    }
    for (u32 v = 0; v < 256; v++)
    {
        u32 n = lengths[v], c = next[n]++, reversed = 0;
        for (u32 k = 0; k < n; k++)
            reversed = reversed << 1 | (c >> k & 1);
        codes[v] = reversed | n << 16;
    }
}

static u32 flashbackHuffman(const u8 *src, u32 len, u8 *dst)
{
    if (len <= FLASHBACK_HUFF_HEADER + 2 * FLASHBACK_HUFF_BLOCK)
        return 0;

    FlashbackHuffman *h = g_huff;
    u8 lengths[256];
    dst[0] = 0xFF;
    dst[1] = 1;
    dst[2] = dst[3] = 0;
    memcpy(dst + 4, &len, 4);
    for (u32 set = 0; set < 2; set++)
    {
        flashbackHuffLengths(h->counts[set], lengths);
        flashbackHuffCodes(lengths, h->codes[set]);
        for (u32 v = 0; v < 256; v += 2)
            dst[8 + set * 128 + v / 2] = (u8)(lengths[v] | lengths[v + 1] << 4);
    }
    memset(h->counts, 0, sizeof(h->counts));

    u32 *opCounts = h->counts[0], *argCounts = h->counts[1];
    const u32 *opCodes = h->codes[0], *argCodes = h->codes[1];
    const u8 *p = src, *end = src + len;
    u16 *out = (u16 *)(dst + FLASHBACK_HUFF_HEADER);
    const u16 *last = (const u16 *)(dst + len - (FLASHBACK_HUFF_BLOCK + 2) * 3 / 2 - 2 - 1);
    u32 bits = 0, n = 0;
    while (p < end)
    {
        if (out > last)
            return 0;
        const u8 *stop = (u32)(end - p) > FLASHBACK_HUFF_BLOCK ? p + FLASHBACK_HUFF_BLOCK : end;
        do
        {
            u32 b = *p++;
            opCounts[b]++;
            u32 c = opCodes[b];
            bits |= (c & 0xFFFF) << n;
            n += c >> 16;
            if (n >= 16)
            {
                *out++ = (u16)bits;
                bits >>= 16;
                n -= 16;
            }
            if (b - 0x80 < 0x40 || b == FLASHBACK_PACK_RAW)
            {
                for (u32 k = b == FLASHBACK_PACK_RAW ? 2 : 1; k != 0; k--)
                {
                    b = *p++;
                    argCounts[b]++;
                    c = argCodes[b];
                    bits |= (c & 0xFFFF) << n;
                    n += c >> 16;
                    if (n >= 16)
                    {
                        *out++ = (u16)bits;
                        bits >>= 16;
                        n -= 16;
                    }
                }
            }
        } while (p < stop);
    }
    if (n != 0)
        *out++ = (u16)bits;
    return (u32)((u8 *)out - dst);
}

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
        uint32_t len = n ? flashbackHuffman(ops, n, coded) : 0;
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
        u32 len = flashbackHuffman(ops, n, coded);
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
