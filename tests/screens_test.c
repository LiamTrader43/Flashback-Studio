/* PC test of Flashback Studio's reading, compacting and trimming of clips that
   store only what changes (FLB_VERSION_SCREENS; 3ds-app/source/flb.c, built as
   it is), with frames made as the recorder makes them (a copy of its
   compression: recorder_copy.h).

   A made-up recording is saved as a clip the way the recorder saves one: the
   top screen, with or without the bottom screen, each frame whole (as it is,
   as ops, or packed with Huffman codes) or as what changed since the frame
   before; a screen left out of an entry when it's the same as before, and no
   entry at all when both are (the entry before is shown again); captures
   missed; both screens whole every so many entries, the bottom screen's
   sometimes late or lost; and entries that only lead in to the first frame.
   Then:
   - every frame of the clip, in order and in any order, on both screens, on
     one or the other, must show the picture a reader following the format's
     rules gets (a screen is black until its first whole frame), with the
     right inputs, and the clip's missed frames must be the index's;
   - compacted, the clip must keep every entry as it was, its frames back to
     back (with zeros between an entry's two frames), and show the same;
   - trimmed (from the recorded clip, the compacted one, and a trimmed one),
     it must start from the entry the format says, show the same frames, with
     their inputs and their sound, and count the missed frames that are left;
   - damaged frames must never be written outside the frame, a whole frame
     with a SKIP op must fail, and damaged clips must fail to open, or open
     and never crash. */
#define _FILE_OFFSET_BITS 64    /* (as flb.c) */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "flb.h"
#include "recorder_copy.h"

static long errors, checks, framesChecked, damagedChecks;
#define FAIL(...) do { errors++; if (errors < 30) printf("  ERROR: " __VA_ARGS__); } while (0)

static u32 g_rng = 20261115;
static u32 rnd(void) { g_rng ^= g_rng << 13; g_rng ^= g_rng >> 17; g_rng ^= g_rng << 5; return g_rng; }
static u16 rgb(int r, int g, int b) { return (u16)((r & 31) << 11 | (g & 63) << 5 | (b & 31)); }

#define FPS 30
#define RATE_MHZ 32728498u
#define MAX_PIXELS (400 * 240)

/* ---------------------------------------------------------------------------
   Pictures: each screen's, by number, as the game showed them
   --------------------------------------------------------------------------- */
static u32 g_dim[2][2];     /* each screen's width and height in the clip under test (the bottom's 0 without it) */
static u32 pixelsOf(int s) { return g_dim[s][0] * g_dim[s][1]; }
static u32 rawOf(int s) { return pixelsOf(s) * 2; }

#define MAX_PICS 8192
static u16 *g_pic[2][MAX_PICS];
static int g_pics[2];

static void freePics(void)
{
    for (int s = 0; s < 2; s++)
    {
        for (int i = 0; i < g_pics[s]; i++)
            free(g_pic[s][i]);
        g_pics[s] = 0;
    }
}

static void paint(u16 *up, u32 w, u32 h, u32 seed)
{
    for (u32 y = 0; y < h; y++)
        for (u32 x = 0; x < w; x++)
            up[y * w + x] = (x / 6 + y / 5 + seed) % 7 == 0 ? (u16)rnd()
                          : rgb((int)(x * 31 / w + seed), (int)(y * 63 / h), (int)((x / 4 + seed) & 31));
}

/* A new picture for screen s: what comes after picture 'from' (the first, if it's -1), never the same. */
static int newPic(int s, int from)
{
    u32 w = g_dim[s][0], h = g_dim[s][1];
    if (g_pics[s] == MAX_PICS) { printf("too many pictures\n"); exit(2); }
    u16 *up = malloc(w * h * 2);
    if (from < 0)
        paint(up, w, h, rnd() % 64);
    else
    {
        memcpy(up, g_pic[s][from], w * h * 2);
        switch (rnd() % 6)
        {
        case 0:     /* one pixel */
            up[rnd() % (w * h)] ^= (u16)(1 + rnd() % 0xFFFF);
            break;
        case 1:     /* a box, of noise or of one color */
        case 2:
        {
            u32 bw = 1 + rnd() % (w / 2), bh = 1 + rnd() % (h / 2), x0 = rnd() % (w - bw + 1), y0 = rnd() % (h - bh + 1);
            bool flat = rnd() % 2;
            u16 color = (u16)rnd();
            for (u32 y = y0; y < y0 + bh; y++)
                for (u32 x = x0; x < x0 + bw; x++)
                    up[y * w + x] = flat ? color : (u16)rnd();
            break;
        }
        case 3:     /* a few pixels here and there */
            for (u32 n = 1 + rnd() % 40; n != 0; n--)
                up[rnd() % (w * h)] = (u16)rnd();
            break;
        case 4:     /* a band of rows moves sideways */
        {
            u32 y0 = rnd() % h, bh = 1 + rnd() % (h - y0), by = 1 + rnd() % (w / 2);
            for (u32 y = y0; y < y0 + bh; y++)
                memmove(&up[y * w], &up[y * w + by], (w - by) * 2);
            break;
        }
        case 5:     /* another picture altogether */
            paint(up, w, h, rnd() % 64);
            break;
        }
        if (memcmp(up, g_pic[s][from], w * h * 2) == 0)
            up[0] ^= 1;
    }
    g_pic[s][g_pics[s]] = up;
    return g_pics[s]++;
}

/* Whether 'got' is screen s's picture 'id' (-1: not known yet, black). */
static bool isPic(const u16 *got, int s, int id)
{
    if (id >= 0)
        return memcmp(got, g_pic[s][id], rawOf(s)) == 0;
    for (u32 i = 0; i < pixelsOf(s); i++)
        if (got[i] != 0)
            return false;
    return true;
}

/* ---------------------------------------------------------------------------
   Frames, stored as the recorder stores them
   --------------------------------------------------------------------------- */
static FlashbackHuffman g_huff[2];  /* each screen's codes, as the recorder keeps them */
static u8 *g_ops, *g_coded;
static u16 *g_colsA, *g_colsB;

/* An upright frame in the screen's order (as captured): column by column, bottom row first. */
static void toColumns(const u16 *up, u32 w, u32 h, u16 *cols)
{
    for (u32 y = 0; y < h; y++)
        for (u32 x = 0; x < w; x++)
            cols[x * h + (h - 1 - y)] = up[y * w + x];
}

/* Screen s's picture whole: as it is, its ops, or those packed with codes. Returns its size. */
static u32 storeWhole(int s, const u16 *up, u8 *out, bool *coded)
{
    u32 raw = rawOf(s);
    *coded = false;
    u32 n = rnd() % 5 == 0 ? 0 : compressFrame(up, g_dim[s][0], g_dim[s][1], g_ops, raw);
    if (n == 0 || n >= raw)
    {
        memcpy(out, up, raw);
        return raw;
    }
    u32 len = rnd() % 3 != 0 ? flashbackHuffman(&g_huff[s], g_ops, n, g_coded) : 0;
    *coded = len != 0;
    memcpy(out, len ? g_coded : g_ops, len ? len : n);
    return len ? len : n;
}

/* Screen s's picture as what changed since 'before' (plain, or packed with
   codes). Returns its size, or 0 if it wouldn't be smaller than the frame. */
static u32 storeDelta(int s, const u16 *up, const u16 *before, u8 *out, bool *coded)
{
    *coded = false;
    toColumns(up, g_dim[s][0], g_dim[s][1], g_colsA);
    toColumns(before, g_dim[s][0], g_dim[s][1], g_colsB);
    u32 n = flashbackCompressDelta(g_colsA, g_colsB, pixelsOf(s), g_ops, rawOf(s));
    if (n == 0)
        return 0;
    u32 len = rnd() % 3 != 0 ? flashbackHuffman(&g_huff[s], g_ops, n, g_coded) : 0;
    *coded = len != 0;
    memcpy(out, len ? g_coded : g_ops, len ? len : n);
    return len ? len : n;
}

/* ---------------------------------------------------------------------------
   A recording: the entries the recorder would have made
   --------------------------------------------------------------------------- */
typedef struct Entry {
    u32 size[2];            /* each screen's stored frame (0: none: it's as before) */
    bool whole[2], coded[2];
    int pic[2];             /* the picture each stored frame gives */
    u8 *data[2];
    u32 copies, missed;     /* grid slots it's shown for, and how many of them had their capture missed */
} Entry;

#define MAX_ENTRIES 700
static Entry g_rec[MAX_ENTRIES];
static u32 g_recCount;

static void freeRecording(void)
{
    for (u32 i = 0; i < g_recCount; i++)
    {
        free(g_rec[i].data[0]);
        free(g_rec[i].data[1]);
    }
    g_recCount = 0;
    freePics();
}

/* Records 'grids' grid slots. Now and then a capture is missed; each screen
   stays the same some of the time (the bottom screen more often); an entry is
   only made when something is new; every 'key'-th entry has both screens whole,
   and other frames are what changed since the one before, or whole now and
   then. The bottom screen's frame is sometimes lost (as when it doesn't fit):
   its next one is then whole, or what changed since the last one stored. And
   a screen's whole frame is sometimes in an entry of its own, shown for no
   time, before the entry with the other screen's frame of the same moment (as
   when the two don't fit in one). */
static void makeRecording(bool bottom, u32 grids)
{
    u32 key = 2 + rnd() % 29;
    u32 same[2] = { rnd() % 3 == 0 ? 60 : 10, 40 + rnd() % 55 };
    int cur[2] = { -1, -1 }, reader[2] = { -1, -1 };
    bool known[2] = { false, false }, bottomDue = false;
    memset(g_huff, 0, sizeof(g_huff));

    for (u32 g = 0; g < grids && g_recCount < MAX_ENTRIES; g++)
    {
        if (g != 0 && rnd() % 9 == 0)
        {
            g_rec[g_recCount - 1].copies++;     /* a capture missed */
            g_rec[g_recCount - 1].missed++;
            continue;
        }
        for (int s = 0; s < (bottom ? 2 : 1); s++)
            if (cur[s] < 0 || rnd() % 100 >= same[s])
                cur[s] = newPic(s, cur[s]);
        if (g_recCount != 0 && cur[0] == reader[0] && (!bottom || (cur[1] == reader[1] && !bottomDue)))
        {
            g_rec[g_recCount - 1].copies++;     /* nothing new */
            continue;
        }

        bool isKey = g_recCount % key == 0, store[2], whole[2];
        store[0] = cur[0] != reader[0] || isKey;
        whole[0] = isKey || !known[0] || rnd() % 7 == 0;
        store[1] = bottom && (cur[1] != reader[1] || isKey || bottomDue);
        whole[1] = isKey || bottomDue || !known[1] || rnd() % 7 == 0;
        if (store[1] && rnd() % 8 == 0)
        {
            store[1] = false;       /* lost: the next one whole, or not */
            bottomDue = rnd() % 2 == 0;
        }
        if (!store[0] && !store[1])
        {
            g_rec[g_recCount - 1].copies++;
            continue;
        }

        Entry *e = &g_rec[g_recCount++];
        memset(e, 0, sizeof(*e));
        e->copies = 1;
        for (int s = 0; s < 2; s++)
        {
            if (!store[s])
                continue;
            e->data[s] = malloc(rawOf(s) + 64);
            if (!whole[s])
            {
                e->size[s] = storeDelta(s, g_pic[s][cur[s]], g_pic[s][reader[s]], e->data[s], &e->coded[s]);
                whole[s] = e->size[s] == 0;
            }
            if (whole[s])
                e->size[s] = storeWhole(s, g_pic[s][cur[s]], e->data[s], &e->coded[s]);
            e->whole[s] = whole[s];
            e->pic[s] = cur[s];
            reader[s] = cur[s];
            known[s] = true;
            if (s == 1 && whole[s])
                bottomDue = false;
        }

        /* the bottom screen's whole frame in an entry of its own: this one, shown
           for no time, and the top screen's frame in the next */
        if (g_recCount > 1 && g_recCount < MAX_ENTRIES && e->size[0] != 0 && e->size[1] != 0 && e->whole[1] && rnd() % 3 == 0)
        {
            Entry *a = &g_rec[g_recCount++];
            *a = *e;
            a->size[1] = 0;
            a->data[1] = NULL;
            a->whole[1] = a->coded[1] = false;
            e->size[0] = 0;
            e->data[0] = NULL;
            e->whole[0] = e->coded[0] = false;
            e->copies = 0;
        }
        /* ...or the top screen's (as the recorder stores them with the bottom screen first) */
        else if (g_recCount > 1 && g_recCount < MAX_ENTRIES && e->size[0] != 0 && e->size[1] != 0 && e->whole[0] && rnd() % 3 == 0)
        {
            Entry *a = &g_rec[g_recCount++];
            *a = *e;
            a->size[0] = 0;
            a->data[0] = NULL;
            a->whole[0] = a->coded[0] = false;
            e->size[1] = 0;
            e->data[1] = NULL;
            e->whole[1] = e->coded[1] = false;
            e->copies = 0;
        }
    }
}

/* ---------------------------------------------------------------------------
   The clip, as the recorder saves it
   --------------------------------------------------------------------------- */
#define MAX_FRAMES 4096
static int g_expTop[MAX_FRAMES], g_expBot[MAX_FRAMES];  /* the picture each screen shows at each frame (-1: black) */
static u32 g_clipFrames, g_clipMissed, g_clipEntries, g_clipLead;

static void put32(FILE *f, uint32_t v) { uint8_t b[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) }; fwrite(b, 1, 4, f); }
static void put16(FILE *f, uint16_t v) { uint8_t b[2] = { (uint8_t)v, (uint8_t)(v >> 8) }; fwrite(b, 1, 2, f); }

/* frame f's inputs, as the recorder would have recorded them */
static FlbInput inputOf(uint32_t f)
{
    FlbInput in = { (f * 2654435761u) >> 3 | 1u << 20, (int16_t)(f * 3 % 313 - 156), (int16_t)(156 - f * 7 % 313),
                    (uint16_t)(f % 320), (uint16_t)(f % 240), (int16_t)(f & 0xFF), (int16_t)-(int32_t)(f & 0x7F) };
    return in;
}

static u32 bottomAt(u32 topSize) { return (topSize + 3) & ~3u; }
static u32 dataOf(u32 topSize, u32 bottomSize) { return bottomSize ? bottomAt(topSize) + bottomSize : topSize; }

/* Saves the recording's last 'want' frames (all of them, if it has fewer) as a
   clip: the entries that show them, the first cut to the copies of it that
   fall inside, and before them the entries that lead in to it, from the last
   one with the top screen whole (or, with 'moreLead', the one before that; or
   half the time, from the last one with the bottom screen whole if that's
   earlier, as the recorder does), not shown. The entries are in a ring of
   slots, starting anywhere, with
   leftovers in the room they don't use. Sets what a reader following the
   format's rules shows at each frame (g_expTop, g_expBot) and the clip's
   counts. */
static void writeClip(const char *path, u32 want, bool audio, bool inputs, bool moreLead)
{
    u32 total = 0;
    for (u32 i = 0; i < g_recCount; i++)
        total += g_rec[i].copies;
    if (want > total)
        want = total;
    if (want > MAX_FRAMES)
        want = MAX_FRAMES;

    /* the entry shown at the first frame, and how many of its copies come before it */
    u32 e0 = 0, before = total - want;
    while (before >= g_rec[e0].copies)
        before -= g_rec[e0++].copies;
    u32 lead = e0;
    while (!(g_rec[lead].size[0] != 0 && g_rec[lead].whole[0]))
        lead--;
    if (moreLead && lead > 0)
        for (lead--; !(g_rec[lead].size[0] != 0 && g_rec[lead].whole[0]); lead--)
            ;
    if (g_dim[1][0] != 0 && rnd() % 2 == 0)
        for (u32 b = e0 + 1; b-- > 0;)
            if (g_rec[b].size[1] != 0 && g_rec[b].whole[1])
            {
                if (b < lead)
                    lead = b;
                break;
            }
    u32 entries = g_recCount - lead;

    /* each entry's copies and missed frames in the clip, and what a reader shows */
    static u32 copies[MAX_ENTRIES], missed[MAX_ENTRIES];
    int shown[2] = { -1, -1 };
    bool known[2] = { false, false };
    g_clipFrames = g_clipMissed = 0;
    for (u32 i = 0; i < entries; i++)
    {
        const Entry *e = &g_rec[lead + i];
        copies[i] = lead + i < e0 ? 0 : lead + i == e0 ? e->copies - before : e->copies;
        missed[i] = copies[i] == 0 ? 0 : e->missed < copies[i] - 1 ? e->missed : copies[i] - 1;
        for (int s = 0; s < 2; s++)
            if (e->size[s] != 0 && (e->whole[s] || known[s]))
            {
                shown[s] = e->pic[s];
                known[s] = true;
            }
        for (u32 k = 0; k < copies[i]; k++, g_clipFrames++)
        {
            g_expTop[g_clipFrames] = shown[0];
            g_expBot[g_clipFrames] = shown[1];
        }
        g_clipMissed += missed[i];
    }
    g_clipEntries = entries;
    g_clipLead = e0 - lead;

    u32 base = 1000 + rnd() % 5000;     /* where the clip's sound starts in the ring */
    u32 capacity = base + (u32)((uint64_t)(g_clipFrames + FPS) * RATE_MHZ / (FPS * 1000));
    u32 videoOffset = audio ? (capacity * 4 + 0xFFFF) / 0x10000 * 0x10000 : 0;
    u32 stride = (dataOf(rawOf(0), g_dim[1][0] ? rawOf(1) : 0) + 511) / 512 * 512 + 1024;
    u32 slots = entries + 1 + rnd() % 4, start = rnd() % slots;
    u32 dataEnd = videoOffset + slots * stride;
    u32 inputSize = inputs ? 16 + g_clipFrames * 16 : 0;

    FILE *f = fopen(path, "wb");
    for (uint32_t i = 0; audio && i < capacity; i++) { put16(f, (uint16_t)i); put16(f, (uint16_t)~i); }
    fseeko(f, videoOffset, SEEK_SET);
    for (u32 i = 0; i < slots * stride; i++)
        fputc(0xA5, f);     /* (what the ring held before) */
    static u32 offsets[MAX_ENTRIES];
    for (u32 i = 0; i < entries; i++)
    {
        const Entry *e = &g_rec[lead + i];
        offsets[i] = videoOffset + (start + i) % slots * stride;
        fseeko(f, offsets[i], SEEK_SET);
        if (e->size[0] != 0)
            fwrite(e->data[0], 1, e->size[0], f);
        if (e->size[1] != 0)
        {
            fseeko(f, offsets[i] + bottomAt(e->size[0]), SEEK_SET);
            fwrite(e->data[1], 1, e->size[1], f);
        }
    }
    fseeko(f, dataEnd, SEEK_SET);
    if (inputs)
    {
        put32(f, 0x49424C46); put32(f, 1); put32(f, g_clipFrames); put32(f, 16);
        for (u32 i = 0; i < g_clipFrames; i++)
        {
            FlbInput in = inputOf(i);
            put32(f, in.buttons); put16(f, (uint16_t)in.circleX); put16(f, (uint16_t)in.circleY);
            put16(f, in.touchX); put16(f, in.touchY); put16(f, (uint16_t)in.cstickX); put16(f, (uint16_t)in.cstickY);
        }
    }
    u32 first = 0;
    for (u32 i = 0; i < entries; i++)
    {
        const Entry *e = &g_rec[lead + i];
        put32(f, offsets[i]);
        put32(f, e->size[0]);
        put32(f, e->size[1]);
        put32(f, copies[i]);
        put32(f, audio && copies[i] != 0 ? base + (u32)((uint64_t)first * RATE_MHZ / (FPS * 1000)) : 0);
        put32(f, (e->size[0] != 0 && !e->whole[0] ? FLB_FRAME_TOP_DELTA : 0) | (e->coded[0] ? FLB_FRAME_TOP_CODES : 0) |
                 (e->size[1] != 0 && !e->whole[1] ? FLB_FRAME_BOTTOM_DELTA : 0) | (e->coded[1] ? FLB_FRAME_BOTTOM_CODES : 0) |
                 missed[i] << 16);
        first += copies[i];
    }
    if (audio)
    {
        put32(f, RATE_MHZ); put16(f, 2); put16(f, 16); put32(f, 0); put32(f, capacity);
        put32(f, 0); put32(f, capacity); put32(f, 0); put32(f, 0);
    }
    put32(f, 0x4B424C46); put32(f, (audio ? 3 : 1) | FLB_VERSION_CODES | FLB_VERSION_SCREENS);
    put32(f, g_dim[0][0]); put32(f, g_dim[0][1]); put32(f, FPS);
    put32(f, stride); put32(f, rawOf(0)); put32(f, entries); put32(f, g_clipFrames);
    put32(f, dataEnd + inputSize); put32(f, inputs ? dataEnd : 0); put32(f, g_dim[1][0] | g_dim[1][1] << 16);
    fclose(f);
}

/* ---------------------------------------------------------------------------
   Checks
   --------------------------------------------------------------------------- */
static u16 *g_top, *g_bot;
static long topStraight, bottomStraight;    /* frames decoded straight where they were wanted (the clip keeps none) */

/* Frame f of an open clip must show pictures expTop and expBot, read as 'how'
   (0: both screens, 1: the top one only, 2: the bottom one only, from the
   clip's own picture). */
static bool checkFrame(const char *what, FlbClip *clip, u32 f, int expTop, int expBot, int how)
{
    const char *error = NULL;
    bool bottom = clip->info.bottomWidth != 0;
    u32 e = flbEntryForFrame(clip, f);
    framesChecked++;
    if (clip->entries[e].copies == 0 || clip->entries[e].firstFrame > f || f - clip->entries[e].firstFrame >= clip->entries[e].copies)
    {
        FAIL("%s: frame %u's entry (%u) isn't shown then\n", what, f, e);
        return false;
    }
    if (how == 2)
    {
        if (!bottom)
            return true;
        if (!flbSeekScreens(clip, e, false, true, &error)) { FAIL("%s: frame %u: %s\n", what, f, error); return false; }
        if (clip->known[FLB_BOTTOM] != (expBot >= 0) || (expBot >= 0 && !isPic(clip->picture[FLB_BOTTOM], 1, expBot)))
        { FAIL("%s: frame %u shows the wrong bottom screen (asked for on its own)\n", what, f); return false; }
        return true;
    }
    bool ok = how == 0 ? flbDecodeScreens(clip, e, g_top, bottom ? g_bot : NULL, &error) : flbDecodeEntry(clip, e, g_top, &error);
    if (!ok) { FAIL("%s: frame %u: %s\n", what, f, error); return false; }
    if (!isPic(g_top, 0, expTop)) { FAIL("%s: frame %u shows the wrong picture\n", what, f); return false; }
    topStraight += clip->at[FLB_TOP] == UINT32_MAX;
    bottomStraight += how == 0 && bottom && clip->at[FLB_BOTTOM] == UINT32_MAX;
    if (how == 0 && bottom && !isPic(g_bot, 1, expBot)) { FAIL("%s: frame %u shows the wrong bottom screen\n", what, f); return false; }
    return true;
}

/* The clip at 'path' must be frames first .. first + count - 1 of the clip
   saved last: the same pictures (in order, and in any order), inputs and
   missed frames. */
static void checkClip(const char *what, const char *path, u32 first, u32 count, bool inputs, u32 wantVersion, u32 wantMissed)
{
    FlbClip clip;
    const char *error = NULL;
    checks++;
    if (!flbOpen(&clip, path, &error)) { FAIL("%s: won't open: %s\n", what, error); return; }
    const FlbInfo *info = &clip.info;
    if (info->frameCount != count) FAIL("%s: %u frames, want %u\n", what, info->frameCount, count);
    if (info->version != wantVersion || !info->hasCodes || !info->hasScreens)
        FAIL("%s: version %u%s%s, want %u with both flags\n", what, info->version, info->hasCodes ? " +codes" : "",
             info->hasScreens ? " +screens" : "", wantVersion);
    if (info->width != g_dim[0][0] || info->height != g_dim[0][1] || info->bottomWidth != g_dim[1][0] || info->bottomHeight != g_dim[1][1])
        FAIL("%s: screens %ux%u and %ux%u\n", what, info->width, info->height, info->bottomWidth, info->bottomHeight);
    if (info->missedFrames != wantMissed) FAIL("%s: %u missed frames, want %u\n", what, info->missedFrames, wantMissed);
    if ((clip.inputs != NULL) != inputs) FAIL("%s: inputs %s\n", what, inputs ? "lost" : "where there were none");
    if (clip.entries[clip.firstShown].copies == 0 || clip.entries[clip.firstShown].firstFrame != 0)
        FAIL("%s: its first shown entry isn't\n", what);
    u32 missed = 0;
    for (u32 e = 0; e < info->entryCount; e++)
    {
        const FlbEntry *entry = &clip.entries[e];
        missed += entry->missed;
        if (entry->copies == 0 ? entry->missed != 0 : entry->missed >= entry->copies)
            FAIL("%s: entry %u is shown %u times, %u of them missed\n", what, e, entry->copies, entry->missed);
    }
    if (missed != wantMissed) FAIL("%s: its entries have %u missed frames, want %u\n", what, missed, wantMissed);
    if (info->frameCount != count)
    {
        flbClose(&clip);
        return;
    }

    /* in order (as playing), both screens */
    for (u32 i = 0; i < count; i++)
    {
        if (!checkFrame(what, &clip, i, g_expTop[first + i], g_expBot[first + i], 0))
            break;
        if (inputs && clip.inputs != NULL)
        {
            FlbInput in = inputOf(first + i);
            if (memcmp(&clip.inputs[i], &in, sizeof(in)) != 0)
            { FAIL("%s: frame %u's inputs are %08x, want %08x\n", what, i, clip.inputs[i].buttons, in.buttons); break; }
        }
    }
    /* any frame, in any order: on from the picture held, or back to a whole
       frame; both screens, or one of them at a time */
    for (int n = 0; n < 120; n++)
    {
        u32 i = rnd() % count;
        if (!checkFrame(what, &clip, i, g_expTop[first + i], g_expBot[first + i], (int)(rnd() % 3)))
            break;
        /* (and a few on from there, as stepping) */
        for (u32 k = 1; k < 4 && i + k < count && rnd() % 2; k++)
            if (!checkFrame(what, &clip, i + k, g_expTop[first + i + k], g_expBot[first + i + k], (int)(rnd() % 3)))
                break;
    }
    flbClose(&clip);

    /* the top screen alone from the start (as the thumbnail and the player's read-ahead do) */
    if (flbOpen(&clip, path, &error))
    {
        for (u32 i = 0; i < count; i += 1 + rnd() % 3)
            if (!checkFrame(what, &clip, i, g_expTop[first + i], g_expBot[first + i], 1))
                break;
        if (clip.picture[FLB_BOTTOM] != NULL)
            FAIL("%s: the bottom screen was read without being asked for\n", what);
        flbClose(&clip);
    }
}

static uint32_t fileU32(FILE *f, long offset)
{
    uint8_t b[4] = { 0 };
    fseek(f, offset, SEEK_SET);
    if (fread(b, 1, 4, f) != 4) FAIL("a short read at %ld\n", offset);
    return b[0] | b[1] << 8 | b[2] << 16 | (uint32_t)b[3] << 24;
}

/* A compacted or trimmed clip's entries must be back to back, in order, with
   zeros between an entry's two frames. */
static void checkLayout(const char *what, const char *path)
{
    FlbClip clip;
    const char *error = NULL;
    if (!flbOpen(&clip, path, &error)) { FAIL("%s: won't open: %s\n", what, error); return; }
    if (!flbIsCompacted(&clip.info) || clip.info.slotStride != 0) FAIL("%s: not compacted\n", what);
    FILE *f = fopen(path, "rb");
    uint64_t at = clip.info.hasAudio ? UINT64_MAX : 0;
    for (u32 e = 0; e < clip.info.entryCount; e++)
    {
        const FlbEntry *entry = &clip.entries[e];
        if (at != UINT64_MAX && entry->offset != at)
        { FAIL("%s: entry %u is at %u, not right after the one before (%llu)\n", what, e, entry->offset, (unsigned long long)at); break; }
        at = (uint64_t)entry->offset + dataOf(entry->size, entry->bottomSize);
        for (u32 p = entry->size; entry->bottomSize != 0 && p < bottomAt(entry->size); p++)
        {
            fseek(f, (long)(entry->offset + p), SEEK_SET);
            if (fgetc(f) != 0) { FAIL("%s: entry %u: not zeros between its frames\n", what, e); break; }
        }
    }
    /* (without sound the entries start the file; with it they're followed by the sound) */
    if (!clip.info.hasAudio && clip.info.entryCount != 0 && clip.entries[0].offset != 0)
        FAIL("%s: its first entry isn't at the start\n", what);
    fclose(f);
    flbClose(&clip);
}

/* The copy at 'copyPath' of frames first .. first + count - 1 of the clip at
   'srcPath' (all of it, compacted, if 'compacted') must have the entries the
   format says, as they were, the missed frames left, the size foretold, and
   the same sound. */
static void checkCopy(const char *what, const char *srcPath, const char *copyPath, u32 first, u32 count, bool compacted,
                      uint64_t wantSize)
{
    FlbClip src, copy;
    const char *error = NULL;
    static int16_t a[4096 * 2], b[4096 * 2];
    if (!flbOpen(&src, srcPath, &error)) { FAIL("%s: the clip won't open: %s\n", what, error); return; }
    if (!flbOpen(&copy, copyPath, &error)) { FAIL("%s: won't open: %s\n", what, error); flbClose(&src); return; }
    if (copy.info.fileSize != wantSize)
        FAIL("%s: %llu bytes, %llu foretold\n", what, (unsigned long long)copy.info.fileSize, (unsigned long long)wantSize);

    /* which entries: all of them, or from the last one with a whole frame of
       either screen (whichever is first) to the last one shown */
    u32 e0 = flbEntryForFrame(&src, first), e1 = flbEntryForFrame(&src, first + count - 1), from = 0, to = src.info.entryCount - 1;
    if (!compacted)
    {
        u32 key[2] = { e0, e0 };
        bool found[2] = { false, false };
        for (u32 i = e0 + 1; i-- > 0;)
        {
            if (!found[0] && src.entries[i].size != 0 && !(src.entries[i].flags & FLB_FRAME_TOP_DELTA)) { key[0] = i; found[0] = true; }
            if (!found[1] && src.entries[i].bottomSize != 0 && !(src.entries[i].flags & FLB_FRAME_BOTTOM_DELTA)) { key[1] = i; found[1] = true; }
        }
        if (!found[1])
            key[1] = key[0];
        from = key[0] < key[1] ? key[0] : key[1];
        to = e1;
    }
    if (copy.info.entryCount != to - from + 1)
        FAIL("%s: %u entries, want %u (%u to %u)\n", what, copy.info.entryCount, to - from + 1, from, to);
    else
    {
        u32 missed = 0;
        FILE *index = fopen(copyPath, "rb");
        for (u32 i = from; i <= to; i++)
        {
            const FlbEntry *s = &src.entries[i], *c = &copy.entries[i - from];
            /* (as written in the copy's index, not as the reader tidies them) */
            long at = (long)copy.info.indexOffset + (long)(i - from) * 24;
            uint32_t rawCopies = fileU32(index, at + 12), rawFlags = fileU32(index, at + 20);
            /* (its copies inside the frames kept) */
            u32 lo = s->firstFrame > first ? s->firstFrame : first;
            u32 hi = s->firstFrame + s->copies < first + count ? s->firstFrame + s->copies : first + count;
            u32 copies = compacted ? s->copies : i < e0 || hi <= lo ? 0 : hi - lo;
            u32 wantMissed = copies == 0 ? 0 : s->missed < copies - 1 ? s->missed : copies - 1;
            missed += wantMissed;
            if (c->size != s->size || c->bottomSize != s->bottomSize || c->flags != s->flags || c->copies != copies ||
                c->missed != wantMissed)
            {
                FAIL("%s: entry %u is %u+%u bytes, flags %02x, shown %u times (%u missed); want %u+%u, %02x, %u (%u)\n", what,
                     i - from, c->size, c->bottomSize, c->flags, c->copies, c->missed, s->size, s->bottomSize, s->flags, copies,
                     wantMissed);
                break;
            }
            if (copies == 0 && c->audioPos != 0)
                FAIL("%s: entry %u is only read, but has a place in the sound\n", what, i - from);
            if (rawCopies != copies || rawFlags != (s->flags | wantMissed << 16))
            {
                FAIL("%s: entry %u's index says %u copies, flags %08x; want %u, %08x\n", what, i - from, rawCopies, rawFlags,
                     copies, s->flags | wantMissed << 16);
                break;
            }
        }
        fclose(index);
        if (copy.info.missedFrames != missed) FAIL("%s: %u missed frames, want %u\n", what, copy.info.missedFrames, missed);
        if (copy.firstShown != e0 - from && !compacted) FAIL("%s: first shown entry %u, want %u\n", what, copy.firstShown, e0 - from);
    }

    /* the sound: the copy's, from its first frame, is the clip's from that frame's */
    if (src.info.hasAudio && copy.info.hasAudio)
    {
        const FlbEntry *s = &src.entries[e0];
        u32 srcStart = s->audioPos + (u32)((uint64_t)(first - s->firstFrame) * RATE_MHZ / ((uint64_t)FPS * 1000));
        u32 copyStart = copy.entries[copy.firstShown].audioPos, samples = flbAudioSamples(&copy.info);
        if (copyStart != 0) FAIL("%s: its sound starts at %u\n", what, copyStart);
        for (u32 done = 0; done < samples; done += 4096)
        {
            u32 n = samples - done < 4096 ? samples - done : 4096;
            if (!flbReadAudio(&src, srcStart + done, n, a, &error) || !flbReadAudio(&copy, copyStart + done, n, b, &error))
            { FAIL("%s: the sound can't be read: %s\n", what, error); break; }
            if (memcmp(a, b, n * 4) != 0) { FAIL("%s: its sound isn't the clip's (at sample %u)\n", what, done); break; }
        }
    }
    else if (src.info.hasAudio != copy.info.hasAudio)
        FAIL("%s: sound %s\n", what, src.info.hasAudio ? "lost" : "from nowhere");
    flbClose(&src);
    flbClose(&copy);
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
    static char buf[65536];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), a)) != 0)
        fwrite(buf, 1, n, b);
    fclose(a);
    fclose(b);
}

static uint64_t sizeToBe(const char *path, u32 first, u32 count, bool compacted)
{
    FlbClip clip;
    const char *error = NULL;
    if (!flbOpen(&clip, path, &error)) { FAIL("won't open: %s\n", error); return 0; }
    uint64_t size = compacted ? flbCompactSize(&clip) : flbTrimSize(&clip, first, count);
    flbClose(&clip);
    return size;
}

/* Changes 4 bytes at 'offset' (from the end if negative) of a file. */
static void patch32(const char *path, long offset, uint32_t v)
{
    FILE *f = fopen(path, "r+b");
    fseek(f, offset, offset < 0 ? SEEK_END : SEEK_SET);
    put32(f, v);
    fclose(f);
}

/* Every frame of a clip that may be damaged: anything but a crash. */
static void readAll(const char *path)
{
    FlbClip clip;
    const char *error = NULL;
    damagedChecks++;
    if (!flbOpen(&clip, path, &error))
        return;
    for (u32 i = 0; i < clip.info.frameCount; i++)
        flbDecodeScreens(&clip, flbEntryForFrame(&clip, i), g_top, clip.info.bottomWidth ? g_bot : NULL, &error);
    for (int n = 0; n < 30; n++)
        flbDecodeScreens(&clip, flbEntryForFrame(&clip, rnd() % clip.info.frameCount), g_top, clip.info.bottomWidth ? g_bot : NULL, &error);
    flbClose(&clip);
}

int main(void)
{
    g_ops = malloc(MAX_PIXELS * 2 + 64);
    g_coded = malloc(MAX_PIXELS * 2 + 64);
    g_colsA = malloc(MAX_PIXELS * 2);
    g_colsB = malloc(MAX_PIXELS * 2);
    g_top = malloc(MAX_PIXELS * 2);
    g_bot = malloc(MAX_PIXELS * 2);
    const char *rec = "screenstest_rec.flb", *cmp = "screenstest_cmp.flb", *trim = "screenstest_trim.flb",
               *trim2 = "screenstest_trim2.flb", *bad = "screenstest_bad.flb";
    long clips = 0, entries = 0, leadIns = 0, blackStarts = 0, deltas = 0, codedFrames = 0, rawFrames = 0, bottomOnly = 0;

    for (int run = 0; run < 70; run++)
    {
        /* small screens mostly (so there can be many frames); the real sizes
           now and then, at full and half resolution */
        static const u32 small[4][2] = { { 24, 16 }, { 40, 24 }, { 48, 40 }, { 64, 48 } };
        bool bottom = run % 4 != 3, audio = run % 3 != 0, inputs = run % 5 != 1;
        u32 grids = 20 + rnd() % 260;
        if (run % 23 == 11 || run % 23 == 12)
        {
            bool full = run % 23 == 11;
            g_dim[0][0] = full ? 400 : 200; g_dim[0][1] = full ? 240 : 120;
            g_dim[1][0] = bottom ? (full ? 320 : 160) : 0; g_dim[1][1] = bottom ? (full ? 240 : 120) : 0;
            grids = 30;
        }
        else
        {
            g_dim[0][0] = small[rnd() % 4][0]; g_dim[0][1] = small[rnd() % 4][1];
            g_dim[1][0] = bottom ? small[rnd() % 4][0] : 0; g_dim[1][1] = bottom ? small[rnd() % 4][1] : 0;
        }
        makeRecording(bottom, grids);
        u32 total = 0;
        for (u32 i = 0; i < g_recCount; i++)
        {
            total += g_rec[i].copies;
            for (int s = 0; s < 2; s++)
            {
                deltas += g_rec[i].size[s] != 0 && !g_rec[i].whole[s];
                codedFrames += g_rec[i].coded[s];
                rawFrames += g_rec[i].size[s] != 0 && g_rec[i].whole[s] && g_rec[i].size[s] == rawOf(s);
            }
            bottomOnly += g_rec[i].size[0] == 0 && g_rec[i].size[1] != 0;
        }

        /* the clip: all of the recording, or its last frames (cut inside an
           entry's copies, or not), led in to */
        writeClip(rec, run % 3 == 0 ? total : 1 + rnd() % total, audio, inputs, run % 4 == 0);
        u32 frames = g_clipFrames, missed = g_clipMissed;
        clips++;
        entries += g_clipEntries;
        leadIns += g_clipLead;
        blackStarts += bottom && g_expBot[0] < 0;
        checkClip("recorded", rec, 0, frames, inputs, audio ? 3 : 1, missed);

        /* compacted */
        FlbCompactor c;
        const char *error = NULL;
        u32 compactVersion = audio ? 3 : 2;
        copyFile(rec, cmp);
        uint64_t size = sizeToBe(rec, 0, frames, true);
        if (flbCompactBegin(&c, cmp, &error) && runCopy(&c))
        {
            checkClip("compacted", cmp, 0, frames, inputs, compactVersion, missed);
            checkCopy("compacted", rec, cmp, 0, frames, true, size);
            checkLayout("compacted", cmp);
        }
        else if (error) FAIL("compacting: %s\n", error);

        /* trimmed, from the recorded clip and from the compacted one; and a trimmed one trimmed again */
        for (int t = 0; t < 6; t++)
        {
            u32 first = rnd() % frames, count = 1 + rnd() % (frames - first);
            if (t == 0) { first = 0; count = frames > 1 ? frames - 1 : 1; }     /* the start */
            if (t == 1) { first = frames > 1 ? 1 : 0; count = frames - first; } /* the end */
            if (t == 2) { first = 0; count = frames; }                          /* all of it */
            const char *from = t % 2 ? cmp : rec;
            remove(trim);
            error = NULL;
            size = sizeToBe(from, first, count, false);
            if (!(flbTrimBegin(&c, from, trim, first, count, &error) && runCopy(&c)))
            {
                if (error) FAIL("trimming %u+%u: %s\n", first, count, error);
                continue;
            }
            /* (the missed frames left: checkCopy works them out from the clip's entries) */
            FlbClip got;
            u32 left = 0;
            if (flbOpen(&got, trim, &error)) { left = got.info.missedFrames; flbClose(&got); }
            checkClip(t % 2 ? "trimmed (compacted)" : "trimmed", trim, first, count, inputs, compactVersion, left);
            checkCopy(t % 2 ? "trimmed (compacted)" : "trimmed", from, trim, first, count, false, size);
            checkLayout("trimmed", trim);

            if (count > 1 && t >= 3)
            {
                u32 first2 = rnd() % count, count2 = 1 + rnd() % (count - first2);
                remove(trim2);
                size = sizeToBe(trim, first2, count2, false);
                if (flbTrimBegin(&c, trim, trim2, first2, count2, &error) && runCopy(&c))
                {
                    if (flbOpen(&got, trim2, &error)) { left = got.info.missedFrames; flbClose(&got); }
                    checkClip("trimmed twice", trim2, first + first2, count2, inputs, compactVersion, left);
                    checkCopy("trimmed twice", trim, trim2, first2, count2, false, size);
                    checkLayout("trimmed twice", trim2);
                }
                else if (error) FAIL("trimming a trimmed clip %u+%u: %s\n", first2, count2, error);
            }
        }

        /* damaged clips: what must fail does, and nothing crashes */
        if (run % 7 == 0)
        {
            FlbClip clip;
            flbOpen(&clip, cmp, &error);
            uint32_t indexOffset = clip.info.indexOffset, entryCount = clip.info.entryCount;
            uint64_t fileSize = clip.info.fileSize;
            flbClose(&clip);

            copyFile(cmp, bad);
            FILE *vf = fopen(bad, "rb");
            uint32_t version = fileU32(vf, (long)fileSize - 44);
            fclose(vf);
            patch32(bad, -44, version | 0x400);
            if (flbOpen(&clip, bad, &error)) { FAIL("a clip with a flag from the future opened\n"); flbClose(&clip); }
            else if (strstr(error, "newer") == NULL) FAIL("a clip with a flag from the future: %s\n", error);
            damagedChecks++;

            copyFile(cmp, bad);
            patch32(bad, (long)indexOffset + 4, rawOf(0) + 1);      /* a top frame bigger than a frame */
            if (flbOpen(&clip, bad, &error)) { FAIL("a clip with an entry too big opened\n"); flbClose(&clip); }
            damagedChecks++;

            copyFile(cmp, bad);
            patch32(bad, (long)indexOffset, indexOffset);           /* an entry's frames in the index */
            if (flbOpen(&clip, bad, &error)) { FAIL("a clip with an entry past its frames opened\n"); flbClose(&clip); }
            damagedChecks++;

            if (!bottom)
            {
                copyFile(cmp, bad);
                patch32(bad, (long)indexOffset + 8, 4);     /* a bottom screen's frame, in a clip without one */
                if (flbOpen(&clip, bad, &error)) { FAIL("a clip with a bottom frame and no bottom screen opened\n"); flbClose(&clip); }
                damagedChecks++;
            }

            /* bytes changed at random, in the frames and in the index */
            for (int k = 0; k < 25; k++)
            {
                copyFile(cmp, bad);
                FILE *f = fopen(bad, "r+b");
                for (int flips = 1 + (int)(rnd() % 6); flips > 0; flips--)
                {
                    long at = k % 2 ? (long)(indexOffset + rnd() % (entryCount * 24)) : (long)(rnd() % indexOffset);
                    fseek(f, at, SEEK_SET);
                    int ch = fgetc(f);
                    fseek(f, at, SEEK_SET);
                    fputc(ch ^ (1 << rnd() % 8), f);
                }
                fclose(f);
                readAll(bad);
            }
        }
        freeRecording();
    }
    printf("%ld clips (%ld entries, %ld of them only leading in; %ld start with the bottom screen unknown):\n"
           "  frames as what changed %ld, packed with codes %ld, stored as they are %ld; entries with the bottom screen's alone %ld\n"
           "  %ld clips and copies checked, %ld frames read\n",
           clips, entries, leadIns, blackStarts, deltas, codedFrames, rawFrames, bottomOnly, checks, framesChecked);
    printf("frames decoded straight where they were wanted: %ld of the top screen, %ld of the bottom one\n", topStraight,
           bottomStraight);
    if (leadIns == 0 || blackStarts == 0 || deltas == 0 || codedFrames == 0 || rawFrames == 0 || bottomOnly == 0 ||
        topStraight == 0 || bottomStraight == 0)
        FAIL("not every case came up\n");

    /* frames of what changed, damaged: never written outside the frame */
    static FlbCodes codes;
    static FlashbackHuffman huff;
    u16 *guarded = malloc((MAX_PIXELS + 128) * 2), *back = guarded + 64;
    u8 *damaged = malloc(MAX_PIXELS * 2 + 64), *plain = malloc(MAX_PIXELS * 2 + 64);
    long frameChecks = 0;
    for (int f = 0; f < 400; f++)
    {
        static const u32 sizes[6][2] = { { 400, 240 }, { 320, 240 }, { 200, 120 }, { 160, 120 }, { 48, 40 }, { 7, 5 } };
        u32 w = sizes[f % 6][0], h = sizes[f % 6][1], pixels = w * h;
        g_dim[0][0] = w; g_dim[0][1] = h;
        int a = newPic(0, -1), b = newPic(0, a);
        toColumns(g_pic[0][b], w, h, g_colsA);
        toColumns(g_pic[0][a], w, h, g_colsB);
        u32 n = flashbackCompressDelta(g_colsA, g_colsB, pixels, plain, pixels * 2);
        if (n == 0)
        {
            freePics();
            continue;
        }
        u32 len = flashbackHuffman(&huff, plain, n, g_coded);
        for (int coded = 0; coded < (len ? 2 : 1); coded++)
        {
            const u8 *src = coded ? g_coded : plain;
            u32 size = coded ? len : n;
            for (u32 i = 0; i < 64; i++) guarded[i] = back[pixels + i] = 0xA5A5;
            memcpy(back, g_pic[0][a], pixels * 2);
            frameChecks++;
            if (!flbDecodeFrame(src, size, back, w, h, true, coded, &codes) || memcmp(back, g_pic[0][b], pixels * 2) != 0)
                FAIL("frame %d: what changed%s doesn't decode to the frame\n", f, coded ? ", packed with codes" : "");
            /* (a whole frame has no SKIPs: one that starts with one can't be read as whole) */
            memcpy(back, g_pic[0][a], pixels * 2);
            if (!coded && plain[0] == 0xFF && flbDecodeFrame(src, size, back, w, h, false, false, &codes))
                FAIL("frame %d: a whole frame starting with a SKIP decoded\n", f);
            /* cut short, and bits flipped: anything but writing outside the frame */
            for (int k = 0; k < 12; k++)
            {
                memcpy(damaged, src, size);
                u32 cut = k == 0 ? size / 2 : k == 1 ? size - 1 : size;
                for (int flips = k < 2 ? 0 : 1 + (int)(rnd() % 4); flips > 0; flips--)
                    damaged[rnd() % size] ^= (u8)(1 << rnd() % 8);
                memcpy(back, g_pic[0][a], pixels * 2);
                frameChecks++;
                flbDecodeFrame(damaged, cut, back, w, h, true, coded, &codes);
                for (u32 i = 0; i < 64; i++)
                    if (guarded[i] != 0xA5A5 || back[pixels + i] != 0xA5A5)
                    { FAIL("frame %d, damaged: written outside the frame\n", f); break; }
            }
        }
        freePics();
    }
    /* SKIPs by hand: one over the whole frame leaves it as it was (the short
       kind and the long); one a pixel too long, or cut off, must fail; and
       after one, the previous pixel is the last one passed over */
    {
        u32 w = 48, h = 40, pixels = w * h;
        g_dim[0][0] = w; g_dim[0][1] = h;
        int a = newPic(0, -1);
        u8 all[4] = { 0xFF, 0xFF, (u8)(pixels - 1), (u8)((pixels - 1) >> 8) };
        u8 over[4] = { 0xFF, 0xFF, (u8)pixels, (u8)(pixels >> 8) };
        u8 two[4] = { 0xFF, 254, 0xFF, 0 };     /* 255 pixels, then 1: 256 in all */
        u8 run[3] = { 0xFF, 0, 0xC0 };          /* the first pixel as it was, then that color once more */
        memcpy(back, g_pic[0][a], pixels * 2);
        if (!flbDecodeFrame(all, 4, back, w, h, true, false, &codes) || memcmp(back, g_pic[0][a], pixels * 2) != 0)
            FAIL("a SKIP over the whole frame didn't leave it as it was\n");
        if (flbDecodeFrame(over, 4, back, w, h, true, false, &codes))
            FAIL("a SKIP a pixel past the frame decoded\n");
        if (flbDecodeFrame(all, 3, back, w, h, true, false, &codes) || flbDecodeFrame(all, 1, back, w, h, true, false, &codes))
            FAIL("a SKIP cut off decoded\n");
        if (flbDecodeFrame(two, 4, back, 16, 16, true, false, &codes) != true)
            FAIL("two SKIPs over a 16 x 16 frame didn't decode\n");
        /* 2 x 1: the left pixel stays, the right one becomes it */
        u16 pair[2] = { 0x1234, 0x4321 };
        if (!flbDecodeFrame(run, 3, pair, 2, 1, true, false, &codes) || pair[0] != 0x1234 || pair[1] != 0x1234)
            FAIL("a run after a SKIP isn't of the pixel passed over\n");
        frameChecks += 6;
        freePics();
    }
    printf("%ld frames of what changed decoded, whole and damaged; %ld damaged clips tried\n", frameChecks, damagedChecks);

    remove(rec);
    remove(cmp);
    remove(trim);
    remove(trim2);
    remove(bad);
    printf("%s (%ld errors)\n", errors ? "FAILED" : "all good", errors);
    return errors != 0;
}
