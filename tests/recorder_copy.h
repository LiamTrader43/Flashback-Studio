/* A copy of the recorder's frame compression (Luma3DS-Flashback's
   sysmodules/rosalina/source/flashback_compress.c: flashbackPackPixels,
   flashbackCompressDelta, flashbackHuffLengths, flashbackHuffCodes and
   flashbackHuffman, unchanged but for being static), so the tests can make
   frames the way the recorder does, without that repository. Keep it in step
   with the original. (compressFrame, at the end, is flashbackCompress for a
   frame that's upright already. Not every test uses flashbackCompressDelta:
   it's marked so, for the compiler.) */
#pragma once

#include <stdbool.h>
#include <stdint.h>
#include <string.h>

typedef uint8_t u8;
typedef uint16_t u16;
typedef uint32_t u32;
typedef int32_t s32;

#define FLASHBACK_PACK_RUN_MAX  62
#define FLASHBACK_PACK_RAW      0xFE
#define FLASHBACK_PACK_SKIP     0xFF
#define FLASHBACK_HUFF_MAX_BITS 12
#define FLASHBACK_HUFF_HEADER   264
#define FLASHBACK_HUFF_BLOCK    64

/* (Frames are compared two pixels at a time: their buffers start on words.) */
typedef u32 __attribute__((may_alias)) FlashbackWord;

typedef struct FlashbackPackState { u32 *index; u8 *out; u32 prev; u32 run; } FlashbackPackState;
typedef struct FlashbackHuffman {
    u32 counts[2][256];
    u32 codes[2][256];
    u32 order[256];
    u32 weight[511];
    u16 parent[511];
    u8  depth[511];
} FlashbackHuffman;

static inline u32 flashbackPackHash(u32 px)
{
    return (px * 0x9E3779B1u) >> 26;    // 6 bits
}

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

static inline u8 *flashbackPutSkip(u8 *out, u32 n)
{
    for (; n > 0x10000; n -= 0x10000)
    {
        out[0] = FLASHBACK_PACK_SKIP;
        out[1] = out[2] = out[3] = 0xFF;
        out += 4;
    }
    if (n > 255)
    {
        out[0] = FLASHBACK_PACK_SKIP;
        out[1] = 0xFF;
        out[2] = (u8)(n - 1);
        out[3] = (u8)((n - 1) >> 8);
        return out + 4;
    }
    out[0] = FLASHBACK_PACK_SKIP;
    out[1] = (u8)(n - 1);
    return out + 2;
}

static __attribute__((unused)) u32 flashbackCompressDelta(const u16 *cols, const u16 *ref, u32 pixels, u8 *dst, u32 cap)
{
    if (cap < 16)
        return 0;

    u32 index[64];
    memset(index, 0, sizeof(index));
    u32 prev = 0, run = 0, skip = 0;
    u8 *out = dst;
    const u8 *limit = dst + cap - 16;   // room for a run flush, a SKIP (two long ones, at most) and the largest op

    for (u32 i = 0; i < pixels; i++)
    {
        u32 px = cols[i];
        if (px == ref[i] && (skip != 0 || (i + 1 < pixels && cols[i + 1] == ref[i + 1])))
        {
            if (run != 0)
            {
                if (out > limit)
                    return 0;
                *out++ = (u8)(0xBF + run);
                run = 0;
            }
            skip++;
            // From a whole word on, two pixels at a time while they're the same.
            if ((i & 1) != 0)
            {
                const FlashbackWord *a = (const FlashbackWord *)(cols + i + 1), *b = (const FlashbackWord *)(ref + i + 1);
                u32 n = (pixels - (i + 1)) / 2, k = 0;
                while (k < n && a[k] == b[k])
                    k++;
                skip += 2 * k;
                i += 2 * k;
            }
            prev = cols[i];
            continue;
        }

        if (out > limit)
            return 0;
        if (skip != 0)
        {
            out = flashbackPutSkip(out, skip);
            skip = 0;
        }
        if (px == prev)
        {
            if (++run == FLASHBACK_PACK_RUN_MAX)
            {
                *out++ = 0xC0 | (FLASHBACK_PACK_RUN_MAX - 1);
                run = 0;
            }
            continue;
        }
        if (run != 0)
        {
            *out++ = (u8)(0xBF + run);
            run = 0;
        }

        // (As flashbackPackPixels, from here.)
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
            *out++ = (u8)(0x40 + 0x2A + (dr << 4) + (dg << 2) + db);
        else
        {
            s32 tr = dr - (dg >> 1), tb = db - (dg >> 1);
            if ((u32)((tr + 8) | (tb + 8)) < 16)
            {
                out[0] = (u8)(0x80 + 32 + dg);
                out[1] = (u8)(0x88 + (tr << 4) + tb);
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

    if (out > limit)
        return 0;
    if (skip != 0)
        out = flashbackPutSkip(out, skip);
    if (run != 0)
        *out++ = (u8)(0xBF + run);
    u32 len = (u32)(out - dst);
    return len < cap ? len : 0;
}

static void flashbackHuffLengths(FlashbackHuffman *h, const u32 *counts, u8 *lengths)
{
    u32 *order = h->order, *weight = h->weight;

    // The values by count, fewest first (a Shell sort: 256 of them).
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

    // The tree: the leaves are weight[0..255] (in that order), and each node
    // made joins the two lightest of the leaves and nodes not yet joined (the
    // nodes are made lightest first, so those are at the front of each).
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

    // How many codes of each length, the longest ones cut down to the limit:
    // while the codes don't fit (their share of the code space, sum(2^-length),
    // is over 1), one at the limit goes, and a shorter one splits in two one
    // bit longer (it's still one, and the one that went fits back in).
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

    // The longest codes to the rarest values.
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

static u32 flashbackHuffman(FlashbackHuffman *h, const u8 *src, u32 len, u8 *dst)
{
    if (len <= FLASHBACK_HUFF_HEADER + 2 * FLASHBACK_HUFF_BLOCK)
        return 0;   // (too small to come out smaller)

    u8 lengths[256];
    dst[0] = 0xFF;
    dst[1] = 1;
    dst[2] = dst[3] = 0;
    memcpy(dst + 4, &len, 4);
    for (u32 set = 0; set < 2; set++)
    {
        flashbackHuffLengths(h, h->counts[set], lengths);
        flashbackHuffCodes(lengths, h->codes[set]);
        for (u32 v = 0; v < 256; v += 2)
            dst[8 + set * 128 + v / 2] = (u8)(lengths[v] | lengths[v + 1] << 4);
    }
    memset(h->counts, 0, sizeof(h->counts));

    // The codes go into 'bits' (n of them so far), out 16 at a time: under 16
    // are left after each code, and a code is at most 12 more. A block's codes
    // (FLASHBACK_HUFF_BLOCK bytes, and the up to 3 after its last op) take at
    // most 1.5 bytes each, and the last bits 2 more: as long as each block
    // starts that far (and 1 more) before 'len', the frame comes out smaller.
    u32 *opCounts = h->counts[0], *argCounts = h->counts[1];
    const u32 *opCodes = h->codes[0], *argCodes = h->codes[1];
    const u8 *p = src, *end = src + len;
    u16 *out = (u16 *)(dst + FLASHBACK_HUFF_HEADER);
    const u16 *last = (const u16 *)(dst + len - (FLASHBACK_HUFF_BLOCK + 3) * 3 / 2 - 2 - 1);
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
            if (b - 0x80 < 0x40 || b >= FLASHBACK_PACK_RAW)     // LUMA's second byte, RAW's two, or SKIP's count
            {
                bool skip = b == FLASHBACK_PACK_SKIP;
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
                    if (skip && b == 0xFF)
                        k += 2;     // (the long SKIP: two more bytes)
                    skip = false;
                }
            }
        } while (p < stop);
    }
    if (n != 0)
        *out++ = (u16)bits;
    return (u32)((u8 *)out - dst);
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
