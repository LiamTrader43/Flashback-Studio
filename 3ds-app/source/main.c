// Flashback Studio for 3DS: browse and play the .flb clips Luma3DS-Flashback
// saves in sd:/luma/flashback/.
//
// The bottom screen is a text console (clip list, playback controls); the top
// screen shows the clip, drawn by the CPU into an RGB565 framebuffer. Half
// resolution clips (200x120) are shown at twice their size to fill the screen.

#include <3ds.h>

#include <dirent.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

#include "flb.h"

#define CLIP_DIR    "sdmc:/luma/flashback/"
#define MAX_CLIPS   512

#define COLS        40
#define LIST_TOP    3
#define LIST_ROWS   15

#define TOP_W       400
#define TOP_H       240

// Progress bar, drawn straight into the bottom framebuffer over console rows
// 3-5 (pixels 24-47), which the player leaves empty.
#define BAR_X       10
#define BAR_W       300
#define BAR_Y       27      // the bar; the playhead sticks out 3 pixels above and below
#define BAR_H       10
#define TICKS_Y     42      // red marks under the bar where frames were missed
#define TICKS_H     2
#define BAR_TOUCH_Y0 16
#define BAR_TOUCH_Y1 56

#define COLOR_PLAYED    ((u16)(RGB8_to_565(80, 200, 255)))
#define COLOR_REST      ((u16)(RGB8_to_565(60, 60, 60)))
#define COLOR_HEAD      ((u16)(RGB8_to_565(255, 255, 255)))
#define COLOR_MISSED    ((u16)(RGB8_to_565(255, 60, 60)))

// Console colors
#define C_NORMAL    "\x1b[0m"
#define C_TITLE     "\x1b[30;46m"
#define C_SELECTED  "\x1b[30;47m"
#define C_DIM       "\x1b[0m\x1b[2m"
#define C_KEY       "\x1b[1;33m"
#define C_WARN      "\x1b[1;31m"
#define C_INFO      "\x1b[1;36m"

typedef struct Clip {
    char name[96];
    FlbInfo info;
    const char *error;      // why it can't be played, or NULL
    u64 compactSize;        // its size once compacted, once known (0 until then)
} Clip;

static const u32 kSpeedQuarters[] = { 1, 2, 4, 8 };
static const char *const kSpeedNames[] = { "1/4x", "1/2x", "1x", "2x" };
#define SPEED_COUNT 4
#define SPEED_NORMAL 2

typedef struct Player {
    FlbClip clip;
    char title[48];
    u32 frame;              // clip frame on screen (copies counted)
    u32 shownEntry;         // index entry on screen, or UINT32_MAX
    bool playing, loop;
    u32 speed;              // index into kSpeedQuarters
    u64 baseTick;           // the clock: frame = baseFrame + time since baseTick
    u32 baseFrame;
    u8 missed[BAR_W];       // bar pixels covering missed (copied) frames
    const char *error;
    bool showStats;
    u32 decodeUs, drawUs;   // last frame's read + decode, and drawing, times
    u32 skipped;            // stored frames passed over without being shown
} Player;

static PrintConsole g_con;
static Clip *g_clips;
static int g_clipCount;
static u16 *g_frame;        // one decoded frame
static Player g_player;
static FlbCompactor g_compactor;
static char g_messageText[96];

// ---------------------------------------------------------------------------
// Bottom screen text

// Prints one whole console row: 'text' cut or padded to the screen width.
static void row(int r, const char *color, const char *fmt, ...)
{
    char text[128];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);

    // (ANSI positions are 1-based)
    printf("\x1b[%d;1H%s%-*.*s" C_NORMAL, r + 1, color, COLS, COLS, text);
}

static void clearRows(int first, int last)
{
    for (int r = first; r <= last; r++)
        row(r, C_NORMAL, "");
}

// Word-wraps 'text' over rows first..last.
static void wrapRows(int first, int last, const char *color, const char *text)
{
    int r = first;
    while (r <= last)
    {
        int len = (int)strlen(text);
        int cut = len;
        if (len > COLS)
        {
            cut = COLS;
            while (cut > 0 && text[cut] != ' ')
                cut--;
            if (cut == 0)
                cut = COLS;
        }
        row(r++, color, "%.*s", cut, text);
        text += cut;
        while (*text == ' ')
            text++;
        if (*text == '\0')
            break;
    }
    clearRows(r, last);
}

// "clip_2026-10-20_19-13-05.123.flb" -> "2026-10-20 19:13:05"; other names
// lose their extension.
static void prettyName(const char *name, char *out, size_t size)
{
    unsigned y, mo, d, h, mi, s;
    if (sscanf(name, "clip_%4u-%2u-%2u_%2u-%2u-%2u", &y, &mo, &d, &h, &mi, &s) == 6)
        snprintf(out, size, "%04u-%02u-%02u %02u:%02u:%02u", y, mo, d, h, mi, s);
    else
    {
        snprintf(out, size, "%s", name);
        size_t n = strlen(out);
        if (n > 4 && strcasecmp(out + n - 4, ".flb") == 0)
            out[n - 4] = '\0';
    }
}

// Decimal megabytes, like the Flashback menu: one decimal below 10 MB.
static void formatSize(u64 bytes, char *out, size_t size)
{
    u32 tenths = (u32)((bytes + 50000) / 100000);
    if (tenths < 100)
        snprintf(out, size, "%lu.%lu MB", tenths / 10, tenths % 10);
    else
        snprintf(out, size, "%lu MB", (tenths + 5) / 10);
}

static void formatLength(u32 frames, u32 fps, char *out, size_t size)
{
    u32 secs = (frames + fps / 2) / fps;
    snprintf(out, size, "%lu:%02lu", secs / 60, secs % 60);
}

// m:ss.t
static void formatTime(u32 frame, u32 fps, char *out, size_t size)
{
    u32 tenths = (u32)((u64)frame * 10 / fps);
    snprintf(out, size, "%lu:%02lu.%lu", tenths / 600, (tenths / 10) % 60, tenths % 10);
}

static void formatResolution(const FlbInfo *info, char *out, size_t size)
{
    // (With sound there isn't room for the size as well.)
    if (info->hasAudio && info->width == 400 && info->height == 240)
        snprintf(out, size, "Full resolution, %lu fps, with sound", info->fps);
    else if (info->hasAudio && info->width == 200 && info->height == 120)
        snprintf(out, size, "Half resolution, %lu fps, with sound", info->fps);
    else if (info->hasAudio)
        snprintf(out, size, "%lux%lu, %lu fps, with sound", info->width, info->height, info->fps);
    else if (info->width == 400 && info->height == 240)
        snprintf(out, size, "Full resolution (400x240), %lu fps", info->fps);
    else if (info->width == 200 && info->height == 120)
        snprintf(out, size, "Half resolution (200x120), %lu fps", info->fps);
    else
        snprintf(out, size, "%lux%lu, %lu fps", info->width, info->height, info->fps);
}

// ---------------------------------------------------------------------------
// Top screen

static void presentTop(void)
{
    gfxFlushBuffers();
    gfxScreenSwapBuffers(GFX_TOP, false);
}

static void clearTop(void)
{
    u16 *fb = (u16 *)gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
    memset(fb, 0, TOP_W * TOP_H * 2);
    presentTop();
}

// The framebuffer is the screen rotated: pixel (x, y) is fb[x * 240 + 239 - y].
static void drawTop(const u16 *src, u32 w, u32 h)
{
    u16 *fb = (u16 *)gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
    u32 scale = (w * 2 <= TOP_W && h * 2 <= TOP_H) ? 2 : 1;
    u32 ox = (TOP_W - w * scale) / 2, oy = (TOP_H - h * scale) / 2;

    if (w * scale != TOP_W || h * scale != TOP_H)
        memset(fb, 0, TOP_W * TOP_H * 2);

    if (scale == 2)
    {
        for (u32 x = 0; x < w; x++)
        {
            u16 *c0 = fb + (ox + 2 * x) * TOP_H + (TOP_H - 1 - oy);
            u16 *c1 = c0 + TOP_H;
            const u16 *s = src + x;
            for (u32 y = 0; y < h; y++, s += w, c0 -= 2, c1 -= 2)
            {
                u16 p = *s;
                c0[0] = p;
                c0[-1] = p;
                c1[0] = p;
                c1[-1] = p;
            }
        }
    }
    else
    {
        for (u32 x = 0; x < w; x++)
        {
            u16 *c = fb + (ox + x) * TOP_H + (TOP_H - 1 - oy);
            const u16 *s = src + x;
            for (u32 y = 0; y < h; y++, s += w)
                *c-- = *s;
        }
    }
    presentTop();
}

// ---------------------------------------------------------------------------
// Clip list

static bool isDatedName(const char *name)
{
    unsigned y, mo, d, h, mi, s;
    return sscanf(name, "clip_%4u-%2u-%2u_%2u-%2u-%2u", &y, &mo, &d, &h, &mi, &s) == 6;
}

// Clips named with their date (as Flashback saves them) newest first, then
// any renamed ones in name order.
static int compareClips(const void *a, const void *b)
{
    const char *na = ((const Clip *)a)->name, *nb = ((const Clip *)b)->name;
    bool da = isDatedName(na), db = isDatedName(nb);
    if (da != db)
        return da ? -1 : 1;
    return da ? strcmp(nb, na) : strcasecmp(na, nb);
}

static void scanClips(void)
{
    g_clipCount = 0;
    DIR *dir = opendir(CLIP_DIR);
    if (dir == NULL)
        return;

    struct dirent *ent;
    while ((ent = readdir(dir)) != NULL && g_clipCount < MAX_CLIPS)
    {
        size_t len = strlen(ent->d_name);
        if (len < 5 || len >= sizeof(g_clips[0].name) || strcasecmp(ent->d_name + len - 4, ".flb") != 0)
            continue;
        strcpy(g_clips[g_clipCount++].name, ent->d_name);
    }
    closedir(dir);
    qsort(g_clips, g_clipCount, sizeof(Clip), compareClips);

    for (int i = 0; i < g_clipCount; i++)
    {
        Clip *c = &g_clips[i];
        char path[160];
        snprintf(path, sizeof(path), CLIP_DIR "%s", c->name);
        c->compactSize = 0;
        FILE *f = fopen(path, "rb");
        if (f == NULL)
            c->error = "the file couldn't be opened";
        else
        {
            if (flbReadInfo(f, &c->info, &c->error))
                c->error = NULL;
            fclose(f);
        }
    }
}

// Cleans up after a compaction that was cut off (see flbCleanUpLeftover).
static void cleanUpLeftovers(void)
{
    char paths[16][160];
    int count = 0;
    DIR *dir = opendir(CLIP_DIR);
    if (dir == NULL)
        return;
    struct dirent *ent;
    while ((ent = readdir(dir)) != NULL && count < 16)
    {
        size_t len = strlen(ent->d_name);
        if (len > 8 && len < sizeof(paths[0]) - sizeof(CLIP_DIR) &&
            (strcmp(ent->d_name + len - 8, ".flb.tmp") == 0 || strcmp(ent->d_name + len - 8, ".flb.old") == 0))
        {
            memcpy(paths[count], CLIP_DIR, sizeof(CLIP_DIR) - 1);
            memcpy(paths[count] + sizeof(CLIP_DIR) - 1, ent->d_name, len + 1);
            count++;
        }
    }
    closedir(dir);

    for (int i = 0; i < count; i++)
        flbCleanUpLeftover(paths[i]);
}

// Reads the clip's index for its compacted size, if it isn't known yet.
static void findCompactSize(Clip *c)
{
    if (c->compactSize != 0 || c->error != NULL)
        return;
    char path[160];
    const char *error;
    FlbClip clip;
    snprintf(path, sizeof(path), CLIP_DIR "%s", c->name);
    if (flbOpen(&clip, path, &error))
    {
        c->compactSize = flbCompactSize(&clip);
        flbClose(&clip);
    }
}

// Free space on the SD card in bytes, or UINT64_MAX if it can't be read.
static u64 sdFreeBytes(void)
{
    FS_ArchiveResource res;
    if (R_FAILED(FSUSER_GetSdmcArchiveResource(&res)))
        return UINT64_MAX;
    return (u64)res.freeClusters * res.clusterSize;
}

typedef struct ListState {
    int selected, scroll;
    bool confirmDelete;
    bool confirmCompact;
    bool compacting;        // g_compactor is at work on the selected clip
    u64 compactFrom;        // the clip's size before
    const char *message;    // shown in the details area until the selection changes
    bool messageOk;         // (rather than an error)
    int previewFor;         // clip shown on the top screen, or -1
    int settle;             // frames the selection has been still
} ListState;

static void setMessage(ListState *ls, const char *message, bool ok)
{
    ls->message = message;
    ls->messageOk = ok;
}

// The compaction's progress, in the details area.
static void drawCompacting(const ListState *ls)
{
    char copied[16], total[16];
    u32 pct = g_compactor.total ? (u32)(g_compactor.copied * 100 / g_compactor.total) : 100;
    row(20, C_INFO, "Compacting...");
    row(21, C_NORMAL, "%s", g_clips[ls->selected].name);
    row(22, C_NORMAL, "[%-30.*s] %3lu%%", (int)(pct * 30 / 100), "##############################", pct);
    formatSize(g_compactor.copied, copied, sizeof(copied));
    formatSize(g_compactor.total, total, sizeof(total));
    row(23, C_NORMAL, "%s of %s copied", copied, total);
    clearRows(24, 26);
}

static void drawList(const ListState *ls)
{
    row(0, C_TITLE, " Flashback Studio");
    if (g_clipCount == 0)
    {
        clearRows(1, 18);
        wrapRows(3, 12, C_NORMAL,
                 "No clips found in sd:/luma/flashback/. Save one with Luma3DS-Flashback "
                 "(Rosalina menu > Flashback, Format: Clip file), then come back.");
    }
    else
    {
        row(1, C_DIM, "%d clip%s in sd:/luma/flashback/", g_clipCount, g_clipCount == 1 ? "" : "s");
        row(2, C_NORMAL, "");
        for (int i = 0; i < LIST_ROWS; i++)
        {
            int idx = ls->scroll + i;
            if (idx >= g_clipCount)
            {
                row(LIST_TOP + i, C_NORMAL, "");
                continue;
            }
            const Clip *c = &g_clips[idx];
            char name[40], len[12], size[16];
            prettyName(c->name, name, sizeof(name));
            if (c->error != NULL)
                row(LIST_TOP + i, idx == ls->selected ? C_SELECTED : C_WARN, " %-19.19s  damaged", name);
            else
            {
                formatLength(c->info.frameCount, c->info.fps, len, sizeof(len));
                formatSize(c->info.fileSize, size, sizeof(size));
                row(LIST_TOP + i, idx == ls->selected ? C_SELECTED : C_NORMAL, " %-19.19s %5s %9s", name, len, size);
            }
        }
        int below = g_clipCount - (ls->scroll + LIST_ROWS);
        if (below > 0)
            row(LIST_TOP + LIST_ROWS, C_DIM, "   ... %d more", below);
        else
            row(LIST_TOP + LIST_ROWS, C_NORMAL, "");
    }

    row(19, C_DIM, "----------------------------------------");
    if (g_clipCount == 0)
        clearRows(20, 26);
    else if (ls->compacting)
        drawCompacting(ls);
    else if (ls->confirmDelete)
    {
        const Clip *c = &g_clips[ls->selected];
        row(20, C_WARN, "Delete this clip?");
        row(21, C_NORMAL, "%s", c->name);
        row(22, C_NORMAL, "It can't be undone.");
        clearRows(23, 26);
    }
    else if (ls->confirmCompact)
    {
        const Clip *c = &g_clips[ls->selected];
        char from[16], to[16];
        formatSize(c->info.fileSize, from, sizeof(from));
        formatSize(c->compactSize, to, sizeof(to));
        row(20, C_INFO, "Compact this clip?");
        row(21, C_NORMAL, "%s", c->name);
        row(22, C_NORMAL, "%s -> %s", from, to);
        wrapRows(23, 26, C_NORMAL, "It's rewritten with just its frame data: the same video, but smaller, "
                                   "so it copies to a PC faster. B cancels it at any time.");
    }
    else
    {
        const Clip *c = &g_clips[ls->selected];
        char text[96], len[12], size[16];
        row(20, C_INFO, "%s", c->name);
        if (c->error != NULL)
        {
            row(21, C_WARN, "Can't play this clip:");
            wrapRows(22, 24, C_NORMAL, c->error);
        }
        else
        {
            formatResolution(&c->info, text, sizeof(text));
            row(21, C_NORMAL, "%s", text);
            formatLength(c->info.frameCount, c->info.fps, len, sizeof(len));
            formatSize(c->info.fileSize, size, sizeof(size));
            row(22, C_NORMAL, "Length %s (%lu frames), %s", len, c->info.frameCount, size);
            u32 missed = c->info.frameCount - c->info.entryCount;
            if (missed == 0)
                row(23, C_NORMAL, "No missed frames");
            else
                row(23, C_NORMAL, "Missed frames: %lu (shown as copies)", missed);
            if (flbIsCompacted(&c->info))
                row(24, C_DIM, "Compacted");
            else if (c->compactSize != 0)
            {
                formatSize(c->compactSize, size, sizeof(size));
                row(24, C_DIM, "Not compacted (%s once compacted)", size);
            }
            else
                row(24, C_DIM, "Not compacted");
        }
        if (ls->message != NULL)
            wrapRows(25, 26, ls->messageOk ? C_INFO : C_WARN, ls->message);
        else
            clearRows(25, 26);
    }

    if (ls->compacting)
    {
        row(27, C_NORMAL, " B Cancel");
        clearRows(28, 29);
    }
    else if (ls->confirmDelete || ls->confirmCompact)
    {
        row(27, C_NORMAL, ls->confirmDelete ? " A Delete    B Cancel" : " A Compact    B Cancel");
        clearRows(28, 29);
    }
    else
    {
        row(27, C_NORMAL, g_clipCount ? " A Play  X Delete  R Compact  Y Refresh" : " Y Refresh");
        row(28, C_NORMAL, g_clipCount ? " Up/Down Select    Left/Right Page" : "");
        row(29, C_NORMAL, " START Exit");
    }
}

static void showPreview(ListState *ls)
{
    ls->previewFor = ls->selected;
    const Clip *c = &g_clips[ls->selected];
    if (c->error != NULL)
    {
        clearTop();
        return;
    }

    char path[160];
    const char *error;
    FlbClip clip;
    snprintf(path, sizeof(path), CLIP_DIR "%s", c->name);
    if (flbOpen(&clip, path, &error))
    {
        g_clips[ls->selected].compactSize = flbCompactSize(&clip);
        if (flbDecodeEntry(&clip, 0, g_frame, &error))
            drawTop(g_frame, clip.info.width, clip.info.height);
        else
            clearTop();
        flbClose(&clip);
    }
    else
        clearTop();
}

static void selectClip(ListState *ls, const char *name)
{
    for (int i = 0; i < g_clipCount; i++)
        if (strcmp(g_clips[i].name, name) == 0)
            ls->selected = i;
}

static void clampList(ListState *ls)
{
    if (ls->selected >= g_clipCount)
        ls->selected = g_clipCount - 1;
    if (ls->selected < 0)
        ls->selected = 0;
    if (ls->scroll > ls->selected)
        ls->scroll = ls->selected;
    if (ls->scroll < ls->selected - (LIST_ROWS - 1))
        ls->scroll = ls->selected - (LIST_ROWS - 1);
    if (ls->scroll > g_clipCount - LIST_ROWS)
        ls->scroll = g_clipCount - LIST_ROWS;
    if (ls->scroll < 0)
        ls->scroll = 0;
}

// ---------------------------------------------------------------------------
// Player

static void playerRebase(Player *p)
{
    p->baseTick = svcGetSystemTick();
    p->baseFrame = p->frame;
}

static u32 playerClockFrame(const Player *p)
{
    u64 elapsed = svcGetSystemTick() - p->baseTick;
    return p->baseFrame + (u32)(elapsed * p->clip.info.fps * kSpeedQuarters[p->speed] / (SYSCLOCK_ARM11 * 4));
}

static void playerSeek(Player *p, s32 frame)
{
    s32 last = (s32)p->clip.info.frameCount - 1;
    p->frame = frame < 0 ? 0 : frame > last ? (u32)last : (u32)frame;
    playerRebase(p);
}

static bool playerOpen(Player *p, const Clip *c, const char **error)
{
    char path[160];
    snprintf(path, sizeof(path), CLIP_DIR "%s", c->name);
    bool loop = p->loop, showStats = p->showStats;
    memset(p, 0, sizeof(*p));
    p->loop = loop;
    p->showStats = showStats;
    if (!flbOpen(&p->clip, path, error))
        return false;

    prettyName(c->name, p->title, sizeof(p->title));
    p->shownEntry = UINT32_MAX;
    p->speed = SPEED_NORMAL;
    p->playing = true;

    // Mark where frames were missed (the copies of each stored frame).
    u32 count = p->clip.info.frameCount;
    for (u32 e = 0; e < p->clip.info.entryCount; e++)
    {
        const FlbEntry *entry = &p->clip.entries[e];
        for (u32 k = 1; k < entry->copies; k++)
            p->missed[(u64)(entry->firstFrame + k) * BAR_W / count] = 1;
    }
    playerRebase(p);
    return true;
}

static void drawBar(const Player *p)
{
    u16 *fb = (u16 *)gfxGetFramebuffer(GFX_BOTTOM, GFX_LEFT, NULL, NULL);
    u32 count = p->clip.info.frameCount;
    u32 head = count > 1 ? (u32)((u64)p->frame * (BAR_W - 1) / (count - 1)) : 0;

    for (u32 i = 0; i < BAR_W; i++)
    {
        u16 *col = fb + (BAR_X + i) * 240;
        bool isHead = i + 1 >= head && i <= head + 1;
        u16 color = isHead ? COLOR_HEAD : i <= head ? COLOR_PLAYED : COLOR_REST;
        u32 y0 = isHead ? BAR_Y - 3 : BAR_Y, y1 = isHead ? BAR_Y + BAR_H + 3 : BAR_Y + BAR_H;
        for (u32 y = BAR_Y - 3; y < BAR_Y + BAR_H + 3; y++)
            col[239 - y] = (y >= y0 && y < y1) ? color : 0;
        u16 tick = p->missed[i] ? COLOR_MISSED : 0;
        for (u32 y = TICKS_Y; y < TICKS_Y + TICKS_H; y++)
            col[239 - y] = tick;
    }
}

// The parts of the player screen that don't change while it's open.
static void drawPlayerStatic(const Player *p)
{
    char text[96];

    consoleClear();
    row(0, C_TITLE, " %s", p->title);
    formatResolution(&p->clip.info, text, sizeof(text));
    row(1, C_DIM, "%s", text);

    row(19, C_DIM, "----------------------------------------");
    row(20, C_NORMAL, " A          Play / pause");
    row(21, C_NORMAL, " Left/Right Previous / next frame");
    row(22, C_NORMAL, " L/R        Back / forward 1 second");
    row(23, C_NORMAL, " Up/Down    Speed");
    row(24, C_NORMAL, " Y          Loop on/off");
    row(25, C_NORMAL, " X          Show/hide timing");
    row(26, C_NORMAL, " Touch the bar to jump");
    row(27, C_NORMAL, " B          Back to the clip list");
}

// The parts that change, redrawn every frame.
static void drawPlayer(const Player *p)
{
    const FlbInfo *info = &p->clip.info;
    char text[96], now[16], total[16];

    drawBar(p);

    formatTime(p->frame, info->fps, now, sizeof(now));
    formatTime(info->frameCount, info->fps, total, sizeof(total));
    snprintf(text, sizeof(text), "%s / %s", now, total);
    row(6, C_NORMAL, " %-20s Frame %lu/%lu", text, p->frame + 1, info->frameCount);
    row(7, C_NORMAL, " %-8s %-6s Loop %s", p->playing ? "Playing" : "Paused", kSpeedNames[p->speed],
        p->loop ? "on" : "off");

    const FlbEntry *entry = &p->clip.entries[p->shownEntry == UINT32_MAX ? 0 : p->shownEntry];
    if (p->shownEntry != UINT32_MAX && p->frame > entry->firstFrame)
        row(8, C_WARN, " Missed frame (copy of the one before)");
    else
        row(8, C_NORMAL, "");

    if (p->error != NULL)
        wrapRows(10, 12, C_WARN, p->error);
    else if (p->showStats)
    {
        row(10, C_DIM, " Read + decode %lu.%lu ms, draw %lu.%lu ms", p->decodeUs / 1000, p->decodeUs / 100 % 10,
            p->drawUs / 1000, p->drawUs / 100 % 10);
        row(11, C_DIM, " Frames skipped to keep up: %lu", p->skipped);
        row(12, C_NORMAL, "");
    }
    else
        clearRows(10, 12);
}

// One frame of the player. Returns false when it should close.
static bool playerUpdate(Player *p, u32 down, u32 repeat, u32 held)
{
    u32 count = p->clip.info.frameCount, fps = p->clip.info.fps;

    if (down & KEY_B)
        return false;

    if (down & KEY_A)
    {
        if (p->playing)
            p->playing = false;
        else
        {
            if (p->frame + 1 >= count)
                p->frame = 0;
            p->playing = true;
            playerRebase(p);
        }
    }
    if (repeat & KEY_DLEFT)
    {
        p->playing = false;
        playerSeek(p, (s32)p->frame - 1);
    }
    if (repeat & KEY_DRIGHT)
    {
        p->playing = false;
        playerSeek(p, (s32)p->frame + 1);
    }
    if (repeat & KEY_L)
        playerSeek(p, (s32)p->frame - (s32)fps);
    if (repeat & KEY_R)
        playerSeek(p, (s32)p->frame + (s32)fps);
    if ((down & KEY_DUP) && p->speed + 1 < SPEED_COUNT)
    {
        p->speed++;
        playerRebase(p);
    }
    if ((down & KEY_DDOWN) && p->speed > 0)
    {
        p->speed--;
        playerRebase(p);
    }
    if (down & KEY_Y)
        p->loop = !p->loop;
    if (down & KEY_X)
        p->showStats = !p->showStats;
    if (held & KEY_TOUCH)
    {
        touchPosition touch;
        hidTouchRead(&touch);
        if (touch.py >= BAR_TOUCH_Y0 && touch.py < BAR_TOUCH_Y1)
        {
            s32 x = (s32)touch.px - BAR_X;
            x = x < 0 ? 0 : x >= BAR_W ? BAR_W - 1 : x;
            playerSeek(p, count > 1 ? (s32)((u64)x * (count - 1) / (BAR_W - 1)) : 0);
        }
    }

    if (p->playing && p->error == NULL)
    {
        u32 frame = playerClockFrame(p);
        if (frame >= count)
        {
            if (p->loop)
            {
                p->frame = frame % count;
                playerRebase(p);
            }
            else
            {
                p->frame = count - 1;
                p->playing = false;
            }
        }
        else
            p->frame = frame;
    }

    u32 entry = flbEntryForFrame(&p->clip, p->frame);
    if (entry != p->shownEntry && p->error == NULL)
    {
        u64 t0 = svcGetSystemTick();
        if (flbDecodeEntry(&p->clip, entry, g_frame, &p->error))
        {
            u64 t1 = svcGetSystemTick();
            drawTop(g_frame, p->clip.info.width, p->clip.info.height);
            u64 t2 = svcGetSystemTick();
            p->decodeUs = (u32)((t1 - t0) * 1000000 / SYSCLOCK_ARM11);
            p->drawUs = (u32)((t2 - t1) * 1000000 / SYSCLOCK_ARM11);
            if (p->playing && p->shownEntry != UINT32_MAX && entry > p->shownEntry + 1)
                p->skipped += entry - p->shownEntry - 1;
            p->shownEntry = entry;
        }
        else
            p->playing = false;
    }
    return true;
}

// ---------------------------------------------------------------------------

int main(void)
{
    gfxInit(GSP_RGB565_OES, GSP_RGB565_OES, false);
    gfxSetDoubleBuffering(GFX_TOP, true);
    consoleInit(GFX_BOTTOM, &g_con);
    osSetSpeedupEnable(true);
    hidSetRepeatParameters(18, 4);

    g_clips = malloc(MAX_CLIPS * sizeof(Clip));
    g_frame = malloc(TOP_W * TOP_H * 2);
    g_player.loop = true;

    ListState ls = { .previewFor = -1 };
    bool playing = false, dirty = true;
    u64 lastTick = svcGetSystemTick();

    clearTop();
    cleanUpLeftovers();
    scanClips();

    while (aptMainLoop())
    {
        // Time spent suspended (HOME menu, sleep) doesn't count as playback.
        u64 now = svcGetSystemTick();
        if (playing && now - lastTick > SYSCLOCK_ARM11 / 4)
            g_player.baseTick += now - lastTick;
        lastTick = now;

        // (hidKeysDownRepeat clears what it returns: read it once per frame)
        hidScanInput();
        u32 down = hidKeysDown(), repeat = hidKeysDownRepeat(), held = hidKeysHeld();

        if (down & KEY_START)
            break;

        if (playing)
        {
            if (!playerUpdate(&g_player, down, repeat, held))
            {
                flbClose(&g_player.clip);
                playing = false;
                consoleClear();
                scanClips();
                clampList(&ls);
                ls.previewFor = -1;
                ls.settle = 0;
                dirty = true;
            }
            else
                drawPlayer(&g_player);
        }
        else if (ls.compacting)
        {
            if (down & KEY_B)
            {
                flbCompactCancel(&g_compactor);
                ls.compacting = false;
                setMessage(&ls, "Compacting cancelled; the clip wasn't changed.", true);
                dirty = true;
            }
            else
            {
                // A slice of the work per pass, so B and the HOME button still respond.
                u64 start = svcGetSystemTick();
                const char *error = NULL;
                int result = 0;
                while (result == 0 && svcGetSystemTick() - start < SYSCLOCK_ARM11 / 30)
                    result = flbCompactStep(&g_compactor, &error);

                if (result == 0)
                    drawCompacting(&ls);
                else
                {
                    char name[sizeof(g_clips[0].name)];
                    strcpy(name, g_clips[ls.selected].name);
                    ls.compacting = false;
                    scanClips();
                    selectClip(&ls, name);
                    clampList(&ls);
                    if (result == 1)
                    {
                        char from[16], to[16];
                        formatSize(ls.compactFrom, from, sizeof(from));
                        formatSize(g_clips[ls.selected].info.fileSize, to, sizeof(to));
                        snprintf(g_messageText, sizeof(g_messageText), "Compacted: %s -> %s.", from, to);
                        setMessage(&ls, g_messageText, true);
                    }
                    else
                        setMessage(&ls, error, false);
                    dirty = true;
                }
            }
        }
        else if (ls.confirmCompact)
        {
            if (down & KEY_A)
            {
                Clip *c = &g_clips[ls.selected];
                char path[160];
                const char *error;
                snprintf(path, sizeof(path), CLIP_DIR "%s", c->name);
                ls.confirmCompact = false;
                // The copy is made next to the clip, which is only deleted at the end.
                u64 needed = c->compactSize + 1000000;
                if (sdFreeBytes() < needed)
                {
                    char size[16];
                    formatSize(needed, size, sizeof(size));
                    snprintf(g_messageText, sizeof(g_messageText), "Not enough free space: compacting needs %s free.", size);
                    setMessage(&ls, g_messageText, false);
                }
                else if (flbCompactBegin(&g_compactor, path, &error))
                {
                    ls.compacting = true;
                    ls.compactFrom = c->info.fileSize;
                    setMessage(&ls, NULL, false);
                }
                else
                    setMessage(&ls, error, false);
                dirty = true;
            }
            else if (down & KEY_B)
            {
                ls.confirmCompact = false;
                dirty = true;
            }
        }
        else if (ls.confirmDelete)
        {
            if (down & KEY_A)
            {
                char path[160];
                snprintf(path, sizeof(path), CLIP_DIR "%s", g_clips[ls.selected].name);
                ls.message = remove(path) == 0 ? NULL : "The clip couldn't be deleted.";
                ls.messageOk = false;
                ls.confirmDelete = false;
                scanClips();
                clampList(&ls);
                ls.previewFor = -1;
                ls.settle = 0;
                if (g_clipCount == 0)
                    clearTop();
                dirty = true;
            }
            else if (down & KEY_B)
            {
                ls.confirmDelete = false;
                dirty = true;
            }
        }
        else
        {
            int before = ls.selected;
            if (repeat & KEY_DUP)
                ls.selected--;
            if (repeat & KEY_DDOWN)
                ls.selected++;
            if (repeat & KEY_DLEFT)
                ls.selected -= LIST_ROWS;
            if (repeat & KEY_DRIGHT)
                ls.selected += LIST_ROWS;
            clampList(&ls);
            if (ls.selected != before)
            {
                ls.message = NULL;
                ls.settle = 0;
                dirty = true;
            }

            if (down & KEY_Y)
            {
                scanClips();
                clampList(&ls);
                ls.message = NULL;
                ls.previewFor = -1;
                ls.settle = 0;
                if (g_clipCount == 0)
                    clearTop();
                dirty = true;
            }
            else if ((down & KEY_X) && g_clipCount > 0)
            {
                ls.confirmDelete = true;
                dirty = true;
            }
            else if ((down & KEY_R) && g_clipCount > 0)
            {
                // (A damaged clip's details already say what's wrong with it.)
                Clip *c = &g_clips[ls.selected];
                if (c->error == NULL)
                {
                    if (flbIsCompacted(&c->info))
                        setMessage(&ls, "This clip is already compacted.", true);
                    else
                    {
                        findCompactSize(c);
                        if (c->compactSize != 0)
                            ls.confirmCompact = true;
                        else
                            setMessage(&ls, "The clip couldn't be read.", false);
                    }
                    dirty = true;
                }
            }
            else if ((down & KEY_A) && g_clipCount > 0)
            {
                // (A damaged clip's details already say why it can't be played.)
                const Clip *c = &g_clips[ls.selected];
                const char *error;
                if (c->error == NULL)
                {
                    if (playerOpen(&g_player, c, &error))
                    {
                        playing = true;
                        drawPlayerStatic(&g_player);
                    }
                    else
                    {
                        setMessage(&ls, error, false);
                        dirty = true;
                    }
                }
            }

            // Show the selected clip's first frame once the selection stops moving
            // (which also tells its compacted size, for the details).
            if (!playing && g_clipCount > 0 && ls.previewFor != ls.selected && ++ls.settle >= 8)
            {
                showPreview(&ls);
                dirty = true;
            }
        }

        if (!playing && dirty)
        {
            drawList(&ls);
            dirty = false;
        }

        gfxFlushBuffers();
        if (!ls.compacting)     // (compacting paces itself)
            gspWaitForVBlank();
    }

    if (playing)
        flbClose(&g_player.clip);
    if (ls.compacting)
        flbCompactCancel(&g_compactor);
    free(g_frame);
    free(g_clips);
    gfxExit();
    return 0;
}
