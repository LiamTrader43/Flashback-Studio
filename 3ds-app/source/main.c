// Flashback Studio for 3DS: browse and play the .flb clips Luma3DS-Flashback
// saves in sd:/luma/flashback/.
//
// The bottom screen is a text console (clip grid, playback controls), with the
// grid's thumbnails and the player's progress bar drawn straight into its
// framebuffer; the top screen shows the clip, drawn by the CPU into an RGB565
// framebuffer. Half resolution clips (200x120) are shown at twice their size to
// fill the screen.
// Clips recorded with audio play it through NDSP (which needs the DSP firmware
// dump sd:/3ds/dspfirm.cdc, as all homebrew with sound does).

#include <3ds.h>

#include <dirent.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/stat.h>

#include "flb.h"

#define CLIP_DIR    "sdmc:/luma/flashback/"
#define MAX_CLIPS   512
#define NAME_MAX_LEN 64     // a clip's name when renaming it (without ".flb")

#define COLS        40

#define TOP_W       400
#define TOP_H       240

// The clip list is a grid of GRID_ROWS rows of GRID_COLS tiles under the title.
// A tile is TILE_COLS console columns by TILE_ROWS rows: a blank row, then a
// thumbnail of the clip's first frame (drawn straight into the bottom
// framebuffer over THUMB_ROWS console rows the text leaves empty), then two
// rows of text.
#define GRID_TOP    1
#define GRID_COLS   4
#define GRID_ROWS   2
#define TILE_COLS   10
#define TILE_ROWS   9
#define THUMB_ROWS  6
#define THUMB_W     72      // 400x240 (or 200x120) shrunk to fit, inside a frame
#define THUMB_H     43
#define THUMB_X     4       // the thumbnail's place in its THUMB_ROWS * 8 pixel high area
#define THUMB_Y     3

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

#define COLOR_PLAYED    ((u16)(RGB8_to_565(204, 26, 31)))    // the logo's red (assets/)
#define COLOR_REST      ((u16)(RGB8_to_565(60, 60, 60)))
#define COLOR_HEAD      ((u16)(RGB8_to_565(255, 255, 255)))
#define COLOR_MISSED    ((u16)(RGB8_to_565(255, 60, 60)))
#define COLOR_TILE      ((u16)(RGB8_to_565(60, 60, 60)))     // a thumbnail's frame
#define COLOR_LOADING   ((u16)(RGB8_to_565(25, 25, 25)))     // a thumbnail not read yet...
#define COLOR_DAMAGED   ((u16)(RGB8_to_565(110, 20, 20)))    // ...or that can't be
#define COLOR_CUT       ((u16)(RGB8_to_565(20, 20, 20)))     // trimming: the bar outside the selection...
#define COLOR_MARK      ((u16)(RGB8_to_565(255, 200, 0)))    // ...and its start and end

// Console colors (the title bar in the logo's red)
#define C_NORMAL    "\x1b[0m"
#define C_TITLE     "\x1b[1;37m\x1b[48;2;204;26;31m"
#define C_SELECTED  "\x1b[30;47m"
#define C_DIM       "\x1b[0m\x1b[2m"
#define C_KEY       "\x1b[1;33m"
#define C_WARN      "\x1b[1;31m"
#define C_INFO      "\x1b[1;37m"

typedef struct Clip {
    char name[96];
    FlbInfo info;
    const char *error;      // why it can't be played, or NULL
    u64 compactSize;        // its size once compacted, once known (0 until then)
    u16 *thumb;             // THUMB_W x THUMB_H of its first frame, once read...
    bool thumbFailed;       // ...or if that failed
} Clip;

static const u32 kSpeedQuarters[] = { 1, 2, 4, 8 };
static const char *const kSpeedNames[] = { "1/4x", "1/2x", "1x", "2x" };
#define SPEED_COUNT 4
#define SPEED_NORMAL 2

typedef struct Player {
    FlbClip clip;
    char fileName[96];      // the clip's
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
    bool showInputs;        // the input display (for clips with inputs)
    bool inputsShown;       // the input display is on screen...
    u32 inputsFrame;        // ...showing this frame's inputs
    bool settingsOpen;      // the Settings menu is open (X)...
    u32 settingsItem;       // ...and the setting selected in it
    u32 decodeUs, drawUs;   // last frame's read + decode, and drawing, times...
    u32 textUs;             // ...and the bottom screen's
    u32 skipped;            // stored frames passed over without being shown

    // Trimming: frames trimIn .. trimOut are the selection, which plays (and
    // loops) on its own and can be saved as a new clip.
    bool trimMode;
    bool trimConfirm;       // asking whether to save it
    bool trimSave;          // it's to be saved: the clip list does that once the player closes
    u32 trimIn, trimOut;
    const char *trimMessage;

    // Audio: streamed from the clip through AUDIO_BUFS NDSP wave buffers.
    bool scrubbing;         // the bar is being touched (no sound meanwhile)
    bool touchWait;         // ignore the touch screen until it's let go (the touch that opened the clip)
    bool clockMoved;        // the picture's clock was set (a jump, a loop, play): restart the sound
    bool audioQueued;       // wave buffers are queued (the sound is playing)
    const char *audioError;
    u32 audioNext;          // the next sample to queue (counting from the clip's first frame)
    u32 audioStart[4];      // first sample of each wave buffer
    u32 audioStopped;       // ndspGetFrameCount() when the buffers were last cleared
    s32 audioOffsetMs;      // how far the sound is ahead of the picture (last check)
    u64 audioCheckTick;
    ndspWaveBuf wave[4];
} Player;

static PrintConsole g_con;
static Clip *g_clips;
static int g_clipCount;
static u16 *g_frame;        // one decoded frame
static Player g_player;

#define AUDIO_BUFS          4
#define AUDIO_BUF_SAMPLES   4096    // 125 ms each: half a second queued
#define AUDIO_NUDGE_MS      15      // move the picture to the sound if they're further apart than this...
#define AUDIO_RESTART_MS    300     // ...or restart the sound if they're further apart than this
static bool g_ndsp;                 // NDSP started (else: no sound)
static s16 *g_audioMem;             // the wave buffers' samples (linear memory, for the DSP)
static FlbCompactor g_compactor;
static char g_messageText[128];

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

static u16 *g_topShown;     // the top screen's framebuffer on screen (the last one presented)

static void presentTop(void)
{
    g_topShown = (u16 *)gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
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
static void drawTopFrame(u16 *fb, const u16 *src, u32 w, u32 h)
{
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
}

static void drawTop(const u16 *src, u32 w, u32 h)
{
    drawTopFrame((u16 *)gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL), src, w, h);
    presentTop();
}

// The input display: a small picture of the controls in the top screen's
// bottom left corner, over the clip (darkened under it), with what was held
// during the frame on screen in the logo's red. For clips recorded with their
// inputs (see flb.h); Settings turns it on and off.
#define PAD_W       128
#define PAD_H       62
#define PAD_X       6       // where it is on the top screen
#define PAD_Y       (TOP_H - PAD_H - 6)
#define COLOR_PAD_IDLE  ((u16)(RGB8_to_565(85, 85, 85)))
#define COLOR_PAD_LINE  ((u16)(RGB8_to_565(150, 150, 150)))
#define COLOR_PAD_ON    COLOR_PLAYED

static u16 g_padUnder[PAD_W * PAD_H];   // the clip's pixels under it (to draw it again over the same frame)

static inline u16 *padAt(u16 *fb, int x, int y)
{
    return fb + (PAD_X + x) * TOP_H + (TOP_H - 1 - (PAD_Y + y));
}

static void padRect(u16 *fb, int x0, int y0, int x1, int y1, u16 color)
{
    for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
            *padAt(fb, x, y) = color;
}

// A disc of radius r (with 'ring', just its edge).
static void padCircle(u16 *fb, int cx, int cy, int r, bool ring, u16 color)
{
    for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            int d = dx * dx + dy * dy;
            if (d <= r * r + r && (!ring || d > (r - 1) * (r - 1) + r - 1))
                *padAt(fb, cx + dx, cy + dy) = color;
        }
}

// Keeps the clip's pixels where the input display goes (as just drawn), or
// puts them back. A column of it is one run of pixels, from its bottom row up.
static void padSaveUnder(u16 *fb)
{
    for (int x = 0; x < PAD_W; x++)
        memcpy(g_padUnder + x * PAD_H, padAt(fb, x, PAD_H - 1), PAD_H * 2);
}

static void padRestoreUnder(u16 *fb)
{
    for (int x = 0; x < PAD_W; x++)
        memcpy(padAt(fb, x, PAD_H - 1), g_padUnder + x * PAD_H, PAD_H * 2);
}

static void drawInputs(u16 *fb, const FlbInput *in)
{
    // The clip under it at half brightness (each channel halved).
    for (int x = 0; x < PAD_W; x++)
    {
        u16 *c = padAt(fb, x, PAD_H - 1);
        for (int i = 0; i < PAD_H; i++)
            c[i] = (c[i] >> 1) & 0x7BEF;
    }

    u32 b = in->buttons;
#define PAD_KEY(keys) ((b & (keys)) ? COLOR_PAD_ON : COLOR_PAD_IDLE)
    padRect(fb, 4, 2, 34, 7, PAD_KEY(KEY_L));
    padRect(fb, 94, 2, 124, 7, PAD_KEY(KEY_R));

    // The Circle Pad: its range, and where it was (red once HID counts it as a direction).
    int kx = in->circleX * 8 / 156, ky = in->circleY * 8 / 156;
    kx = kx < -8 ? -8 : kx > 8 ? 8 : kx;
    ky = ky < -8 ? -8 : ky > 8 ? 8 : ky;
    padCircle(fb, 20, 23, 11, true, COLOR_PAD_LINE);
    padCircle(fb, 20 + kx, 23 - ky, 3, false,
              (b & (KEY_CPAD_UP | KEY_CPAD_DOWN | KEY_CPAD_LEFT | KEY_CPAD_RIGHT)) ? COLOR_PAD_ON : COLOR_PAD_LINE);

    // The D-Pad.
    padRect(fb, 17, 38, 24, 45, PAD_KEY(KEY_DUP));
    padRect(fb, 17, 51, 24, 58, PAD_KEY(KEY_DDOWN));
    padRect(fb, 10, 45, 17, 51, PAD_KEY(KEY_DLEFT));
    padRect(fb, 24, 45, 31, 51, PAD_KEY(KEY_DRIGHT));
    padRect(fb, 17, 45, 24, 51, COLOR_PAD_IDLE);

    // The touch screen (320 x 240, at a tenth), and where it was touched.
    padRect(fb, 47, 15, 81, 16, COLOR_PAD_LINE);
    padRect(fb, 47, 40, 81, 41, COLOR_PAD_LINE);
    padRect(fb, 47, 15, 48, 41, COLOR_PAD_LINE);
    padRect(fb, 80, 15, 81, 41, COLOR_PAD_LINE);
    if (b & KEY_TOUCH)
    {
        int tx = 48 + in->touchX * 32 / 320, ty = 16 + in->touchY * 24 / 240;
        padCircle(fb, tx < 50 ? 50 : tx > 78 ? 78 : tx, ty < 18 ? 18 : ty > 38 ? 38 : ty, 2, false, COLOR_PAD_ON);
    }

    padRect(fb, 48, 48, 62, 52, PAD_KEY(KEY_SELECT));
    padRect(fb, 66, 48, 80, 52, PAD_KEY(KEY_START));

    padCircle(fb, 108, 23, 5, false, PAD_KEY(KEY_X));
    padCircle(fb, 97, 34, 5, false, PAD_KEY(KEY_Y));
    padCircle(fb, 119, 34, 5, false, PAD_KEY(KEY_A));
    padCircle(fb, 108, 45, 5, false, PAD_KEY(KEY_B));
#undef PAD_KEY
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

// Reads the clip folder. Clips that are still there keep their thumbnails,
// unless 'keepThumbs' is false.
static void scanClips(bool keepThumbs)
{
    // The old list, for its thumbnails (it's in the same order as the new one).
    int oldCount = g_clipCount;
    Clip *old = keepThumbs && oldCount > 0 ? malloc(oldCount * sizeof(Clip)) : NULL;
    if (old != NULL)
        memcpy(old, g_clips, oldCount * sizeof(Clip));
    else
    {
        for (int i = 0; i < oldCount; i++)
            free(g_clips[i].thumb);
        oldCount = 0;
    }

    g_clipCount = 0;
    DIR *dir = opendir(CLIP_DIR);
    if (dir != NULL)
    {
        struct dirent *ent;
        while ((ent = readdir(dir)) != NULL && g_clipCount < MAX_CLIPS)
        {
            size_t len = strlen(ent->d_name);
            if (len < 5 || len >= sizeof(g_clips[0].name) || strcasecmp(ent->d_name + len - 4, ".flb") != 0)
                continue;
            strcpy(g_clips[g_clipCount++].name, ent->d_name);
        }
        closedir(dir);
    }
    qsort(g_clips, g_clipCount, sizeof(Clip), compareClips);

    int j = 0;
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

        c->thumb = NULL;
        c->thumbFailed = false;
        while (j < oldCount && compareClips(&old[j], c) < 0)
            j++;
        if (j < oldCount && strcmp(old[j].name, c->name) == 0)
        {
            c->thumb = old[j].thumb;
            old[j++].thumb = NULL;
        }
    }

    for (int i = 0; i < oldCount; i++)
        free(old[i].thumb);
    free(old);
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

// Shrinks a w x h frame to THUMB_W x THUMB_H, each pixel the average of the
// ones it covers.
static void shrinkFrame(const u16 *src, u32 w, u32 h, u16 *dst)
{
    for (u32 ty = 0; ty < THUMB_H; ty++)
    {
        u32 y0 = ty * h / THUMB_H, y1 = (ty + 1) * h / THUMB_H;
        if (y1 == y0)
            y1 = y0 + 1;
        for (u32 tx = 0; tx < THUMB_W; tx++)
        {
            u32 x0 = tx * w / THUMB_W, x1 = (tx + 1) * w / THUMB_W;
            if (x1 == x0)
                x1 = x0 + 1;
            u32 r = 0, g = 0, b = 0, n = (y1 - y0) * (x1 - x0);
            for (u32 y = y0; y < y1; y++)
                for (const u16 *s = src + y * w + x0, *end = src + y * w + x1; s < end; s++)
                {
                    r += *s >> 11;
                    g += (*s >> 5) & 63;
                    b += *s & 31;
                }
            dst[ty * THUMB_W + tx] = (u16)((r / n) << 11 | (g / n) << 5 | (b / n));
        }
    }
}

// Makes the clip's thumbnail from its first frame, decoded in g_frame.
static void makeThumb(Clip *c, u32 w, u32 h)
{
    c->thumb = malloc(THUMB_W * THUMB_H * 2);
    c->thumbFailed = c->thumb == NULL;
    if (c->thumb != NULL)
        shrinkFrame(g_frame, w, h, c->thumb);
}

// Reads the clip's first frame for its thumbnail (and its compacted size,
// while the clip is open).
static void loadThumb(Clip *c)
{
    char path[160];
    const char *error;
    FlbClip clip;
    snprintf(path, sizeof(path), CLIP_DIR "%s", c->name);
    c->thumbFailed = true;
    if (!flbOpen(&clip, path, &error))
        return;
    if (c->compactSize == 0)
        c->compactSize = flbCompactSize(&clip);
    if (flbDecodeEntry(&clip, 0, g_frame, &error))
        makeThumb(c, clip.info.width, clip.info.height);
    flbClose(&clip);
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
    bool compacting;        // g_compactor is at work: compacting the selected clip, or trimming it...
    bool trimming;          // ...into a new clip
    char jobClip[96];       // the clip being written (the selected one, or the trimmed copy)
    u64 compactFrom;        // the clip's size before...
    u64 compactStart;       // ...and when the work started (system ticks)
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

// The compaction's (or trim's) progress, in the details area.
static void drawCompacting(const ListState *ls)
{
    char copied[16], total[16];
    u32 pct = g_compactor.total ? (u32)(g_compactor.copied * 100 / g_compactor.total) : 100;
    row(20, C_INFO, ls->trimming ? "Trimming..." : "Compacting...");
    row(21, C_NORMAL, "%s", ls->jobClip);
    row(22, C_NORMAL, "[%-30.*s] %3lu%%", (int)(pct * 30 / 100), "##############################", pct);
    formatSize(g_compactor.copied, copied, sizeof(copied));
    formatSize(g_compactor.total, total, sizeof(total));
    row(23, C_NORMAL, "%s of %s copied", copied, total);
    clearRows(24, 26);
}

// A tile's thumbnail area (THUMB_ROWS console rows down from pixel row y0),
// straight into the bottom framebuffer: the thumbnail in a thin frame, or on a
// white card (which its text continues) if the clip is selected. 'c' is NULL
// for an empty place in the grid.
static void drawTile(u16 *fb, u32 x0, u32 y0, const Clip *c, bool selected)
{
    u16 fill = c == NULL ? 0 : c->error != NULL || c->thumbFailed ? COLOR_DAMAGED : COLOR_LOADING;
    for (u32 x = 0; x < TILE_COLS * 8; x++)
    {
        u16 *col = fb + (x0 + x) * 240 + 239 - y0;  // pixel (x0 + x, y0 + y) is col[-y]
        bool inX = x >= THUMB_X && x < THUMB_X + THUMB_W;
        bool frameX = x + 2 >= THUMB_X && x < THUMB_X + THUMB_W + 2;
        for (u32 y = 0; y < THUMB_ROWS * 8; y++)
        {
            u16 p = 0;
            if (inX && y >= THUMB_Y && y < THUMB_Y + THUMB_H)
                p = c != NULL && c->thumb != NULL ? c->thumb[(y - THUMB_Y) * THUMB_W + x - THUMB_X] : fill;
            else if (c != NULL && selected)
                p = COLOR_HEAD;
            else if (c != NULL && frameX && y + 2 >= THUMB_Y && y < THUMB_Y + THUMB_H + 2)
                p = COLOR_TILE;
            col[-(s32)y] = p;
        }
    }
}

// A tile's two lines of text: when the clip was saved, and how long it is.
static void tileText(const Clip *c, char *saved, char *length, size_t size)
{
    unsigned y, mo, d, h, mi, s;
    saved[0] = length[0] = '\0';
    if (c == NULL)
        return;
    if (sscanf(c->name, "clip_%4u-%2u-%2u_%2u-%2u-%2u", &y, &mo, &d, &h, &mi, &s) == 6)
        snprintf(saved, size, "%02u:%02u:%02u", h, mi, s);
    else
        prettyName(c->name, saved, size);
    if (c->error != NULL)
        snprintf(length, size, "damaged");
    else
        formatLength(c->info.frameCount, c->info.fps, length, size);
}

// One console row of tile text, each tile's centered in its columns.
static void tileTextRow(int r, char text[GRID_COLS][16], const char *const color[GRID_COLS])
{
    printf("\x1b[%d;1H", r + 1);
    for (int i = 0; i < GRID_COLS; i++)
    {
        int len = (int)strlen(text[i]);
        if (len > TILE_COLS)
            len = TILE_COLS;
        int left = (TILE_COLS - len) / 2;
        printf("%s%*s%.*s%*s", color[i], left, "", len, text[i], TILE_COLS - left - len, "");
    }
    printf("%s", C_NORMAL);
}

// The title and the clip grid (see GRID_TOP).
static void drawGrid(const ListState *ls)
{
    int first = ls->scroll * GRID_COLS, last = first + GRID_COLS * GRID_ROWS;
    char counts[48];
    if (last > g_clipCount)
        last = g_clipCount;
    if (g_clipCount > GRID_COLS * GRID_ROWS)
        snprintf(counts, sizeof(counts), "%d-%d of %d ", first + 1, last, g_clipCount);
    else
        snprintf(counts, sizeof(counts), "%d clip%s ", g_clipCount, g_clipCount == 1 ? "" : "s");
    row(0, C_TITLE, " Flashback Studio%*s", COLS - 17, counts);

    u16 *fb = (u16 *)gfxGetFramebuffer(GFX_BOTTOM, GFX_LEFT, NULL, NULL);
    for (int r = 0; r < GRID_ROWS; r++)
    {
        int top = GRID_TOP + r * TILE_ROWS;     // a blank row, the thumbnails, two rows of text
        char saved[GRID_COLS][16], length[GRID_COLS][16];
        const char *color[GRID_COLS];
        row(top, C_NORMAL, "");
        for (int i = 0; i < GRID_COLS; i++)
        {
            int idx = first + r * GRID_COLS + i;
            const Clip *c = idx < g_clipCount ? &g_clips[idx] : NULL;
            drawTile(fb, i * TILE_COLS * 8, (top + 1) * 8, c, idx == ls->selected);
            tileText(c, saved[i], length[i], sizeof(saved[i]));
            color[i] = idx == ls->selected ? C_SELECTED : c != NULL && c->error != NULL ? C_WARN : C_NORMAL;
        }
        tileTextRow(top + 1 + THUMB_ROWS, saved, color);
        tileTextRow(top + 2 + THUMB_ROWS, length, color);
    }
}

// The clip shown at touch point (px, py), or -1.
static int tileAt(const ListState *ls, u32 px, u32 py)
{
    int r = (int)(py / 8) - GRID_TOP;
    if (r < 0 || r >= GRID_ROWS * TILE_ROWS || px >= GRID_COLS * TILE_COLS * 8)
        return -1;
    int idx = (ls->scroll + r / TILE_ROWS) * GRID_COLS + (int)(px / (TILE_COLS * 8));
    return idx < g_clipCount ? idx : -1;
}

static void drawList(const ListState *ls)
{
    if (g_clipCount == 0)
    {
        row(0, C_TITLE, " Flashback Studio");
        clearRows(1, 18);
        wrapRows(3, 12, C_NORMAL,
                 "No clips found in sd:/luma/flashback/. Save one with Luma3DS-Flashback "
                 "(Rosalina menu > Flashback, Format: Clip file), then come back.");
    }
    else
        drawGrid(ls);

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
        wrapRows(23, 26, C_NORMAL, "Remove empty space between frames. The same video but smaller.");
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
        row(28, C_NORMAL, g_clipCount ? " D-Pad/touch Select   Touch again Play" : "");
        row(29, C_NORMAL, g_clipCount ? " L Rename  SELECT Trim  START Exit" : " START Exit");
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
        Clip *sel = &g_clips[ls->selected];
        sel->compactSize = flbCompactSize(&clip);
        if (flbDecodeEntry(&clip, 0, g_frame, &error))
        {
            drawTop(g_frame, clip.info.width, clip.info.height);
            if (sel->thumb == NULL)
                makeThumb(sel, clip.info.width, clip.info.height);
        }
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
    // (scroll counts grid rows)
    int sel = ls->selected / GRID_COLS, rows = (g_clipCount + GRID_COLS - 1) / GRID_COLS;
    if (ls->scroll > sel)
        ls->scroll = sel;
    if (ls->scroll < sel - (GRID_ROWS - 1))
        ls->scroll = sel - (GRID_ROWS - 1);
    if (ls->scroll > rows - GRID_ROWS)
        ls->scroll = rows - GRID_ROWS;
    if (ls->scroll < 0)
        ls->scroll = 0;
}

// The file name for a name typed in the keyboard, 'len' bytes of 'text': each
// space made a dash, and ".flb" added (unless it was typed too). Returns the
// length of the name itself.
static size_t clipFileName(const char *text, size_t len, char *out, size_t size)
{
    if (len >= 4 && strncasecmp(text + len - 4, ".flb", 4) == 0)
        len -= 4;
    snprintf(out, size, "%.*s.flb", (int)len, text);
    for (char *p = out; *p != '\0'; p++)
        if (*p == ' ')
            *p = '-';
    return len;
}

// Windows reserves these names (before the first dot, whatever the case) for
// devices: the SD card takes them, but a clip named one couldn't be copied to
// a PC.
static bool isReservedName(const char *name)
{
    size_t stem = strcspn(name, ".");
    if (stem == 3)
        return strncasecmp(name, "CON", 3) == 0 || strncasecmp(name, "PRN", 3) == 0 ||
               strncasecmp(name, "AUX", 3) == 0 || strncasecmp(name, "NUL", 3) == 0;
    if (stem == 4 && (strncasecmp(name, "COM", 3) == 0 || strncasecmp(name, "LPT", 3) == 0))
        return name[3] >= '0' && name[3] <= '9';
    return false;
}

// Checks a new clip name as it's confirmed in the keyboard: if it can't be
// used, the keyboard says why and stays open. 'user' is the clip's file name.
// (The keyboard can't stop characters from being typed, only refuse them here.)
static SwkbdCallbackResult checkClipName(void *user, const char **message, const char *text, size_t len)
{
    char name[sizeof(g_clips[0].name)], path[160];
    struct stat st;
    size_t base = clipFileName(text, len, name, sizeof(name));

    for (size_t i = 0; i < base; i++)
    {
        unsigned char ch = (unsigned char)text[i];
        if (ch < 0x20 || ch > 0x7E || strchr("\\/:*?\"<>|", ch) != NULL)
        {
            *message = "Use letters, numbers and punctuation other than \\ / : * ? \" < > |. (Spaces become dashes.)";
            return SWKBD_CALLBACK_CONTINUE;
        }
    }
    if (base == 0 || text[base - 1] == '.')
    {
        *message = "The name can't be empty or end with a dot.";
        return SWKBD_CALLBACK_CONTINUE;
    }
    if (isReservedName(name))
    {
        *message = "Windows can't use that name (CON, PRN, AUX, NUL, COM0-9 and LPT0-9 are taken), "
                   "so the clip couldn't be copied to a PC. Try another.";
        return SWKBD_CALLBACK_CONTINUE;
    }
    snprintf(path, sizeof(path), CLIP_DIR "%s", name);
    if (strcasecmp(name, (const char *)user) != 0 && stat(path, &st) == 0)
    {
        *message = "There's already a clip with that name.";
        return SWKBD_CALLBACK_CONTINUE;
    }
    return SWKBD_CALLBACK_OK;
}

// Asks for a new name for the selected clip with the system keyboard, and
// renames it.
static void renameClip(ListState *ls)
{
    char current[sizeof(g_clips[0].name)], initial[sizeof(current)], text[NAME_MAX_LEN * 4 + 1];
    snprintf(current, sizeof(current), "%s", g_clips[ls->selected].name);
    snprintf(initial, sizeof(initial), "%.*s", (int)strlen(current) - 4, current);  // without ".flb"

    SwkbdState kb;
    swkbdInit(&kb, SWKBD_TYPE_NORMAL, 2, NAME_MAX_LEN);
    swkbdSetHintText(&kb, "Clip name (spaces become dashes)");
    swkbdSetInitialText(&kb, initial);
    swkbdSetButton(&kb, SWKBD_BUTTON_LEFT, "Cancel", false);
    swkbdSetButton(&kb, SWKBD_BUTTON_RIGHT, "Rename", true);
    swkbdSetValidation(&kb, SWKBD_NOTEMPTY_NOTBLANK, SWKBD_FILTER_CALLBACK, 0);
    swkbdSetFilterCallback(&kb, checkClipName, current);
    SwkbdButton button = swkbdInputText(&kb, text, sizeof(text));

    // The keyboard was drawn over both screens.
    consoleClear();
    ls->previewFor = -1;
    ls->settle = 0;

    char name[sizeof(current)], from[160], to[160];
    clipFileName(text, strlen(text), name, sizeof(name));
    if (button != SWKBD_BUTTON_RIGHT || strcmp(name, current) == 0)
        return;
    snprintf(from, sizeof(from), CLIP_DIR "%s", current);
    snprintf(to, sizeof(to), CLIP_DIR "%s", name);

    bool ok;
    if (strcasecmp(name, current) == 0)
    {
        // Only capitals change, which the SD card's file system sees as the
        // same name: go through "<clip>.old", which the next start puts back
        // if this is cut off (see flbCleanUpLeftover).
        char old[sizeof(from) + 4];
        snprintf(old, sizeof(old), "%s.old", from);
        ok = rename(from, old) == 0;
        if (ok && rename(old, to) != 0)
        {
            rename(old, from);
            ok = false;
        }
    }
    else
        ok = rename(from, to) == 0;

    if (!ok)
    {
        setMessage(ls, "The clip couldn't be renamed.", false);
        return;
    }
    scanClips(true);
    selectClip(ls, name);
    clampList(ls);
    snprintf(g_messageText, sizeof(g_messageText), "Renamed to %.80s.", name);   // (names are at most NAME_MAX_LEN + 4)
    setMessage(ls, g_messageText, true);
}

// A free name for a trimmed copy of clip 'name': "<name>-trim.flb", or
// "<name>-trim2.flb" and so on if that's taken. False if none is free.
static bool trimmedName(const char *name, char *out, size_t size)
{
    int base = (int)strlen(name) - 4;   // without ".flb"
    if (base > 80)
        base = 80;
    for (int n = 1; n < 100; n++)
    {
        char path[160];
        struct stat st;
        if (n == 1)
            snprintf(out, size, "%.*s-trim.flb", base, name);
        else
            snprintf(out, size, "%.*s-trim%d.flb", base, name, n);
        snprintf(path, sizeof(path), CLIP_DIR "%s", out);
        if (stat(path, &st) != 0)
            return true;
    }
    return false;
}

// Starts saving frames first .. first + frames - 1 of clip 'source' (taking
// 'bytes') as a new clip next to it; the clip list shows the progress.
static void startTrim(ListState *ls, const char *source, u32 first, u32 frames, u64 bytes)
{
    char name[sizeof(g_clips[0].name)], from[160], to[160];
    const char *error;
    if (!trimmedName(source, name, sizeof(name)))
    {
        setMessage(ls, "There's no free name for the trimmed clip.", false);
        return;
    }
    u64 needed = bytes + 1000000;
    if (sdFreeBytes() < needed)
    {
        char size[16];
        formatSize(needed, size, sizeof(size));
        snprintf(g_messageText, sizeof(g_messageText), "Not enough free space: trimming needs %s free.", size);
        setMessage(ls, g_messageText, false);
        return;
    }
    snprintf(from, sizeof(from), CLIP_DIR "%s", source);
    snprintf(to, sizeof(to), CLIP_DIR "%s", name);
    if (!flbTrimBegin(&g_compactor, from, to, first, frames, &error))
    {
        setMessage(ls, error, false);
        return;
    }
    ls->compacting = true;
    ls->trimming = true;
    snprintf(ls->jobClip, sizeof(ls->jobClip), "%s", name);
    ls->compactStart = svcGetSystemTick();
    setMessage(ls, NULL, false);
}

// ---------------------------------------------------------------------------
// Reading ahead
//
// While a clip plays, a thread reads and decodes the next few frames into
// buffers of their own, so the player only has to draw them. Reading the SD
// card then overlaps the drawing and the waiting for the screen instead of
// adding to them (an Old 3DS couldn't always read, decode and draw a frame in a
// frame's time, and skipped some), and a slow read is covered by the frames
// already decoded. The thread has the clip open for itself (the player reads
// the sound through its own handle), and runs below the player's priority:
// it uses the time the player spends waiting.

#define AHEAD_BUFS  6       // the frame on screen and the ones after it (0.2 s at 30 fps)

typedef struct Ahead {
    bool running;
    FlbClip clip;
    Thread thread;
    LightLock lock;             // for everything below
    LightEvent wake;            // something else is wanted
    LightEvent done;            // a frame was decoded (or couldn't be read)
    u32 held[AHEAD_BUFS];       // the entry in each buffer, or UINT32_MAX
    // Wanted: entry 'want', then the ones after it ('count' in all), going
    // round from 'last' to 'first' if 'loop' (the part that plays).
    u32 want, count, first, last;
    bool loop, quit;
    u32 failed;                 // an entry that couldn't be read (UINT32_MAX: none)...
    const char *error;          // ...and why
    u32 decodeUs;               // the last frame's read + decode time
} Ahead;

static Ahead g_ahead;
static u16 *g_aheadBuf[AHEAD_BUFS];     // TOP_W x TOP_H each

// The wanted entry after 'e', or UINT32_MAX if there's none.
static u32 aheadNext(const Ahead *a, u32 e)
{
    if (e == a->last)
        return a->loop ? a->first : UINT32_MAX;
    return e + 1 < a->clip.info.entryCount ? e + 1 : UINT32_MAX;
}

static int aheadFind(const Ahead *a, u32 e)
{
    for (int i = 0; i < AHEAD_BUFS; i++)
        if (a->held[i] == e)
            return i;
    return -1;
}

static bool aheadIsWanted(const Ahead *a, u32 e)
{
    u32 w = a->want;
    for (u32 k = 0; k < a->count && w != UINT32_MAX; k++, w = aheadNext(a, w))
        if (w == e)
            return true;
    return false;
}

static void aheadThread(void *arg)
{
    Ahead *a = (Ahead *)arg;
    LightLock_Lock(&a->lock);
    while (!a->quit)
    {
        // The first wanted frame that isn't decoded yet (or found damaged),
        // and a buffer to decode it into: one holding a frame no longer wanted
        // (there always is one, with at most AHEAD_BUFS wanted).
        u32 entry = UINT32_MAX, w = a->want;
        for (u32 k = 0; k < a->count && w != UINT32_MAX; k++, w = aheadNext(a, w))
            if (w != a->failed && aheadFind(a, w) < 0)
            {
                entry = w;
                break;
            }
        int b = -1;
        for (int i = 0; entry != UINT32_MAX && b < 0 && i < AHEAD_BUFS; i++)
            if (a->held[i] == UINT32_MAX || !aheadIsWanted(a, a->held[i]))
                b = i;
        if (b < 0)
        {
            LightLock_Unlock(&a->lock);
            LightEvent_Wait(&a->wake);
            LightLock_Lock(&a->lock);
            continue;
        }

        a->held[b] = UINT32_MAX;
        LightLock_Unlock(&a->lock);
        const char *error = NULL;
        u64 t0 = svcGetSystemTick();
        bool ok = flbDecodeEntry(&a->clip, entry, g_aheadBuf[b], &error);
        u32 us = (u32)((svcGetSystemTick() - t0) * 1000000 / SYSCLOCK_ARM11);
        LightLock_Lock(&a->lock);
        if (ok)
        {
            a->held[b] = entry;
            a->decodeUs = us;
        }
        else
        {
            a->failed = entry;
            a->error = error;
        }
        LightEvent_Signal(&a->done);
    }
    LightLock_Unlock(&a->lock);
}

// Starts reading ahead in the clip at 'path'. If it can't, the player reads
// each frame itself when it's needed.
static void aheadStart(const char *path)
{
    Ahead *a = &g_ahead;
    const char *error;
    memset(a, 0, sizeof(*a));
    for (int i = 0; i < AHEAD_BUFS; i++)
        if (g_aheadBuf[i] == NULL)
            return;
    if (!flbOpen(&a->clip, path, &error))
        return;
    LightLock_Init(&a->lock);
    LightEvent_Init(&a->wake, RESET_ONESHOT);
    LightEvent_Init(&a->done, RESET_ONESHOT);
    for (int i = 0; i < AHEAD_BUFS; i++)
        a->held[i] = UINT32_MAX;
    a->failed = UINT32_MAX;
    s32 prio = 0x30;
    svcGetThreadPriority(&prio, CUR_THREAD_HANDLE);
    a->thread = threadCreate(aheadThread, a, 32 * 1024, prio < 0x3F ? prio + 1 : prio, -2, false);
    if (a->thread == NULL)
    {
        flbClose(&a->clip);
        return;
    }
    a->running = true;
}

static void aheadStop(void)
{
    Ahead *a = &g_ahead;
    if (!a->running)
        return;
    LightLock_Lock(&a->lock);
    a->quit = true;
    LightLock_Unlock(&a->lock);
    LightEvent_Signal(&a->wake);
    threadJoin(a->thread, U64_MAX);
    threadFree(a->thread);
    flbClose(&a->clip);
    a->running = false;
}

// Asks for entry 'entry' and the ones after it, 'count' in all (the part that
// plays is entries first .. last, going round if 'loop').
static void aheadWant(u32 entry, u32 count, u32 first, u32 last, bool loop)
{
    Ahead *a = &g_ahead;
    LightLock_Lock(&a->lock);
    bool changed = a->want != entry || a->count != count || a->first != first || a->last != last || a->loop != loop;
    a->want = entry;
    a->count = count;
    a->first = first;
    a->last = last;
    a->loop = loop;
    LightLock_Unlock(&a->lock);
    if (changed)
        LightEvent_Signal(&a->wake);
}

// Entry 'entry' decoded (the one last asked for with aheadWant), waiting for it
// if need be; or NULL if it couldn't be read ('error' says why). Its buffer
// stays as it is until something else is asked for.
static const u16 *aheadGet(u32 entry, const char **error, u32 *decodeUs)
{
    Ahead *a = &g_ahead;
    LightLock_Lock(&a->lock);
    for (;;)
    {
        int b = aheadFind(a, entry);
        if (b >= 0 || entry == a->failed)
        {
            if (b < 0)
                *error = a->error;
            *decodeUs = a->decodeUs;
            LightLock_Unlock(&a->lock);
            return b >= 0 ? g_aheadBuf[b] : NULL;
        }
        LightLock_Unlock(&a->lock);
        LightEvent_Wait(&a->done);
        LightLock_Lock(&a->lock);
    }
}

// ---------------------------------------------------------------------------
// Player

static void playerRebase(Player *p)
{
    p->baseTick = svcGetSystemTick();
    p->baseFrame = p->frame;
    p->clockMoved = true;
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

// The audio sample (counting from the clip's first frame) at which clip frame
// 'frame' starts, the same way the converter places the audio.
static u32 audioSampleForFrame(const Player *p, u32 frame)
{
    const FlbInfo *info = &p->clip.info;
    return (u32)((u64)frame * info->audioRateMhz / ((u64)info->fps * 1000));
}

// Where the picture is, in audio samples (at 1x speed, the only one with sound).
static u32 audioClockSample(const Player *p)
{
    u64 elapsed = svcGetSystemTick() - p->baseTick;
    return audioSampleForFrame(p, p->baseFrame) +
           (u32)(elapsed * p->clip.info.audioRateMhz / ((u64)SYSCLOCK_ARM11 * 1000));
}

static void audioStop(Player *p)
{
    if (!p->audioQueued)
        return;
    ndspChnWaveBufClear(0);
    p->audioQueued = false;
    p->audioStopped = ndspGetFrameCount();
}

static void audioFill(Player *p, int i)
{
    const FlbInfo *info = &p->clip.info;
    u32 total = flbAudioSamples(info);
    u32 n = total - p->audioNext < AUDIO_BUF_SAMPLES ? total - p->audioNext : AUDIO_BUF_SAMPLES;
    s16 *data = g_audioMem + i * AUDIO_BUF_SAMPLES * 2;

    if (!flbReadAudio(&p->clip, p->clip.entries[0].audioPos + p->audioNext, n, data, &p->audioError))
    {
        audioStop(p);
        return;
    }
    DSP_FlushDataCache(data, n * 4);

    ndspWaveBuf *w = &p->wave[i];
    memset(w, 0, sizeof(*w));
    w->data_pcm16 = data;
    w->nsamples = n;
    ndspChnWaveBufAdd(0, w);
    p->audioStart[i] = p->audioNext;
    p->audioNext += n;
}

// Starts the sound at sample 'sample'.
static void audioStartAt(Player *p, u32 sample)
{
    // Buffers cleared just before are only let go once NDSP has passed the
    // clear on to the DSP, a frame or two later (~5 ms each).
    while (ndspGetFrameCount() - p->audioStopped < 3)
        svcSleepThread(1000 * 1000LL);

    memset(p->wave, 0, sizeof(p->wave));
    p->audioNext = sample;
    p->audioQueued = true;
    for (int i = 0; i < AUDIO_BUFS && p->audioQueued && p->audioNext < flbAudioSamples(&p->clip.info); i++)
        audioFill(p, i);
    // First check 0.1 s in, once it's surely playing (see playerAudio).
    p->audioCheckTick = svcGetSystemTick() - SYSCLOCK_ARM11 / 2 + SYSCLOCK_ARM11 / 10;
}

// The sample being played right now, or UINT32_MAX if none is.
static u32 audioPlayingSample(const Player *p)
{
    for (int i = 0; i < AUDIO_BUFS; i++)
        if (p->wave[i].status == NDSP_WBUF_PLAYING)
            return p->audioStart[i] + ndspChnGetSamplePos(0);
    return UINT32_MAX;
}

// Once per frame: plays the sound when the picture plays at 1x (not paused,
// stepping or scrubbing), and refills played buffers. The sound starts a little
// after it's asked to (old buffers to let go of, new ones to read), so shortly
// after it starts, and twice a second after that, the picture's clock is moved
// to match it. If they're far apart (the HOME menu, the SD card falling behind),
// the sound starts over at the right place instead.
static void playerAudio(Player *p)
{
    const FlbInfo *info = &p->clip.info;
    bool want = g_ndsp && info->hasAudio && p->audioError == NULL && p->error == NULL &&
                p->playing && p->speed == SPEED_NORMAL && !p->scrubbing;

    if (!want)
    {
        p->clockMoved = false;
        audioStop(p);
        return;
    }
    if (!p->audioQueued || p->clockMoved)
    {
        p->clockMoved = false;
        audioStop(p);
        audioStartAt(p, audioClockSample(p));
        return;
    }

    for (int i = 0; i < AUDIO_BUFS && p->audioQueued; i++)
        if (p->wave[i].status == NDSP_WBUF_DONE && p->audioNext < flbAudioSamples(info))
            audioFill(p, i);

    u64 now = svcGetSystemTick();
    if (now - p->audioCheckTick >= SYSCLOCK_ARM11 / 2)
    {
        p->audioCheckTick = now;
        u32 playing = audioPlayingSample(p), clock = audioClockSample(p);
        if (playing != UINT32_MAX)
        {
            s64 ahead = (s64)playing - (s64)clock;      // samples the sound is ahead of the picture
            p->audioOffsetMs = (s32)(ahead * 1000000 / info->audioRateMhz);
            if (p->audioOffsetMs > AUDIO_RESTART_MS || p->audioOffsetMs < -AUDIO_RESTART_MS)
            {
                audioStop(p);
                audioStartAt(p, audioClockSample(p));
            }
            else if (p->audioOffsetMs > AUDIO_NUDGE_MS || p->audioOffsetMs < -AUDIO_NUDGE_MS)
                p->baseTick -= ahead * (s64)SYSCLOCK_ARM11 * 1000 / info->audioRateMhz;    // (not playerRebase: the sound carries on)
        }
        else
        {
            // Nothing queued with sound still to come: it ran dry. Start over.
            bool active = false;
            for (int i = 0; i < AUDIO_BUFS; i++)
                active |= p->wave[i].status == NDSP_WBUF_QUEUED || p->wave[i].status == NDSP_WBUF_PLAYING;
            if (!active && p->audioNext < flbAudioSamples(info))
            {
                audioStop(p);
                audioStartAt(p, clock);
            }
        }
    }
}

static bool playerOpen(Player *p, const Clip *c, const char **error)
{
    char path[160];
    snprintf(path, sizeof(path), CLIP_DIR "%s", c->name);
    bool loop = p->loop, showStats = p->showStats, showInputs = p->showInputs;
    memset(p, 0, sizeof(*p));
    p->loop = loop;
    p->showStats = showStats;
    p->showInputs = showInputs;
    if (!flbOpen(&p->clip, path, error))
        return false;

    snprintf(p->fileName, sizeof(p->fileName), "%s", c->name);
    prettyName(c->name, p->title, sizeof(p->title));
    p->shownEntry = UINT32_MAX;
    p->speed = SPEED_NORMAL;
    p->playing = true;
    p->touchWait = true;
    p->trimOut = p->clip.info.frameCount - 1;

    // The recorder keeps the DSP's own sample rate, so the sound plays as is.
    if (g_ndsp && p->clip.info.hasAudio)
    {
        float rate = p->clip.info.audioRateMhz / 1000.0f;
        ndspChnReset(0);
        ndspChnSetInterp(0, rate > 32700.0f && rate < 32760.0f ? NDSP_INTERP_NONE : NDSP_INTERP_POLYPHASE);
        ndspChnSetRate(0, rate);
        ndspChnSetFormat(0, NDSP_FORMAT_STEREO_PCM16);
        float mix[12] = { 1.0f, 1.0f };
        ndspChnSetMix(0, mix);
    }

    // Mark where frames were missed (the copies of each stored frame).
    u32 count = p->clip.info.frameCount;
    for (u32 e = 0; e < p->clip.info.entryCount; e++)
    {
        const FlbEntry *entry = &p->clip.entries[e];
        for (u32 k = 1; k < entry->copies; k++)
            p->missed[(u64)(entry->firstFrame + k) * BAR_W / count] = 1;
    }
    aheadStart(path);
    playerRebase(p);
    return true;
}

// The input display over the top screen 'fb', for the frame on screen (if
// it's on and the clip has inputs).
static void playerDrawInputs(Player *p, u16 *fb)
{
    p->inputsShown = p->showInputs && p->clip.inputs != NULL;
    p->inputsFrame = p->frame;
    if (p->inputsShown)
        drawInputs(fb, &p->clip.inputs[p->frame]);
}

// Shows a decoded frame of the clip, with the input display over it.
static void playerDrawTop(Player *p, const u16 *frame)
{
    u16 *fb = (u16 *)gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
    drawTopFrame(fb, frame, p->clip.info.width, p->clip.info.height);
    padSaveUnder(fb);
    playerDrawInputs(p, fb);
    presentTop();
}

// Whether the input display on screen is out of date while the same stored
// frame stays up: a copy of it with other inputs, or the display turned on or off.
static bool playerInputsStale(const Player *p)
{
    bool want = p->showInputs && p->clip.inputs != NULL;
    if (want != p->inputsShown)
        return true;
    return want && p->frame != p->inputsFrame &&
           memcmp(&p->clip.inputs[p->frame], &p->clip.inputs[p->inputsFrame], sizeof(FlbInput)) != 0;
}

// Draws the input display again over the frame on screen: the screen copied
// to the other framebuffer, the clip's pixels put back under the display, and
// the display drawn for the frame now on screen.
static void playerRedrawInputs(Player *p)
{
    u16 *fb = (u16 *)gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
    memcpy(fb, g_topShown, TOP_W * TOP_H * 2);
    padRestoreUnder(fb);
    playerDrawInputs(p, fb);
    presentTop();
}

// The Settings menu (X while a clip plays).
enum { SETTING_LOOP, SETTING_INPUTS, SETTING_TIMING, SETTING_COUNT };

static void playerToggleSetting(Player *p, u32 item)
{
    if (item == SETTING_LOOP)
        p->loop = !p->loop;
    else if (item == SETTING_INPUTS)
        p->showInputs = !p->showInputs;
    else
        p->showStats = !p->showStats;
}

// The bar pixel showing clip frame 'frame'.
static u32 barPixel(const Player *p, u32 frame)
{
    u32 count = p->clip.info.frameCount;
    return count > 1 ? (u32)((u64)frame * (BAR_W - 1) / (count - 1)) : 0;
}

// The player's bottom screen is redrawn every frame, but only what changed is
// drawn again: the console draws text pixel by pixel, which is slow enough to
// make playback stutter if every row is reprinted each time. What's shown on
// each row (its color and text, or "\x01" if unknown), and the bar's state:
static char g_playerRows[30][160];
static u32 g_barShown;

// Forgets what the player's rows and bar show (after the screen was cleared).
static void playerScreenForget(void)
{
    for (int r = 0; r < 30; r++)
        strcpy(g_playerRows[r], "\x01");
    g_barShown = UINT32_MAX;
}

// Like row(), but only prints the row if it changed.
static void playerRow(int r, const char *color, const char *fmt, ...)
{
    char text[128], shown[sizeof(g_playerRows[0])];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);

    snprintf(shown, sizeof(shown), "%s%s", color, text);
    if (strcmp(shown, g_playerRows[r]) == 0)
        return;
    strcpy(g_playerRows[r], shown);
    row(r, color, "%s", text);
}

// The progress bar. While trimming, the parts outside the selection are dark
// and its start and end are marked.
static void drawBar(const Player *p)
{
    u16 *fb = (u16 *)gfxGetFramebuffer(GFX_BOTTOM, GFX_LEFT, NULL, NULL);
    u32 head = barPixel(p, p->frame), in = barPixel(p, p->trimIn), out = barPixel(p, p->trimOut);
    u32 shown = head | in << 9 | out << 18 | (u32)p->trimMode << 27;   // (each under BAR_W, 300)
    if (shown == g_barShown)
        return;
    g_barShown = shown;

    for (u32 i = 0; i < BAR_W; i++)
    {
        u16 *col = fb + (BAR_X + i) * 240;
        bool isHead = i + 1 >= head && i <= head + 1;
        bool isMark = p->trimMode && (i == in || i == in + 1 || i + 1 == out || i == out);
        u16 color = isHead ? COLOR_HEAD : isMark ? COLOR_MARK : p->trimMode && (i < in || i > out) ? COLOR_CUT :
                    i <= head ? COLOR_PLAYED : COLOR_REST;
        bool tall = isHead || isMark;
        u32 y0 = tall ? BAR_Y - 3 : BAR_Y, y1 = tall ? BAR_Y + BAR_H + 3 : BAR_Y + BAR_H;
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
    playerScreenForget();
    row(0, C_TITLE, " %s", p->title);
    formatResolution(&p->clip.info, text, sizeof(text));
    row(1, C_DIM, "%s", text);

    row(19, C_DIM, "----------------------------------------");
    if (p->trimMode)
    {
        row(20, C_NORMAL, " A          Play / pause the selection");
        row(21, C_NORMAL, " Left/Right Previous / next frame");
        row(22, C_NORMAL, " Up/Down    Forward / back 1 second");
        row(23, C_NORMAL, " L/R        Start / end the selection here");
        row(24, C_NORMAL, " X          Save it as a new clip");
        row(25, C_NORMAL, " Y          Loop on/off");
        row(26, C_NORMAL, " Touch the bar to jump");
        row(27, C_NORMAL, " B/SELECT   Stop trimming");
        row(28, C_NORMAL, "");
    }
    else
    {
        row(20, C_NORMAL, " A          Play / pause");
        row(21, C_NORMAL, " Left/Right Previous / next frame");
        row(22, C_NORMAL, " L/R        Back / forward 1 second");
        row(23, C_NORMAL, " Up/Down    Speed");
        row(24, C_NORMAL, " X          Settings (loop, inputs, ...)");
        row(25, C_NORMAL, " Touch the bar to jump");
        row(26, C_NORMAL, " B          Back to the clip list");
        row(27, C_NORMAL, " SELECT     Trim");
        row(28, C_NORMAL, "");
    }
}

// The parts that change, redrawn every frame (only where they did change).
static void drawPlayer(const Player *p)
{
    const FlbInfo *info = &p->clip.info;
    char text[96], now[16], total[16];

    drawBar(p);

    formatTime(p->frame, info->fps, now, sizeof(now));
    formatTime(info->frameCount, info->fps, total, sizeof(total));
    snprintf(text, sizeof(text), "%s / %s", now, total);
    playerRow(6, C_NORMAL, " %-20s Frame %lu/%lu", text, p->frame + 1, info->frameCount);
    playerRow(7, C_NORMAL, " %-8s %-6s Loop %s", p->playing ? "Playing" : "Paused", kSpeedNames[p->speed],
              p->loop ? "on" : "off");

    const FlbEntry *entry = &p->clip.entries[p->shownEntry == UINT32_MAX ? 0 : p->shownEntry];
    if (p->shownEntry != UINT32_MAX && p->frame > entry->firstFrame)
        playerRow(8, C_WARN, " Missed frame (copy of the one before)");
    else
        playerRow(8, C_NORMAL, "");

    if (!info->hasAudio)
        playerRow(9, C_DIM, " No sound (recorded without audio)");
    else if (!g_ndsp)
        playerRow(9, C_WARN, " No sound: needs sd:/3ds/dspfirm.cdc");
    else if (p->audioError != NULL)
        playerRow(9, C_WARN, " No sound: %s", p->audioError);
    else if (p->audioQueued)
        playerRow(9, C_NORMAL, " Sound on");
    else if (!p->playing)
        playerRow(9, C_DIM, " Sound: muted while paused");
    else if (p->speed != SPEED_NORMAL)
        playerRow(9, C_DIM, " Sound: muted at %s speed", kSpeedNames[p->speed]);
    else
        playerRow(9, C_DIM, " Sound: muted");

    if (p->error != NULL)
    {
        wrapRows(10, 12, C_WARN, p->error);
        for (int r = 10; r <= 12; r++)
            strcpy(g_playerRows[r], "\x01");    // (printed without playerRow)
    }
    else if (p->showStats)
    {
        playerRow(10, C_DIM, " Read+decode %lu.%lu  draw %lu.%lu  text %lu.%lu ms", p->decodeUs / 1000,
                  p->decodeUs / 100 % 10, p->drawUs / 1000, p->drawUs / 100 % 10, p->textUs / 1000, p->textUs / 100 % 10);
        playerRow(11, C_DIM, " Frames skipped to keep up: %lu", p->skipped);
        if (p->audioQueued)
            playerRow(12, C_DIM, " Sound vs picture: %+ld ms", p->audioOffsetMs);
        else
            playerRow(12, C_NORMAL, "");
    }
    else
        for (int r = 10; r <= 12; r++)
            playerRow(r, C_NORMAL, "");

    if (p->trimMode)
    {
        char from[16], to[16], length[16];
        formatTime(p->trimIn, info->fps, from, sizeof(from));
        formatTime(p->trimOut + 1, info->fps, to, sizeof(to));
        formatTime(p->trimOut + 1 - p->trimIn, info->fps, length, sizeof(length));
        playerRow(14, C_INFO, " Keep %s to %s (%s)", from, to, length);
        if (p->trimConfirm)
            playerRow(15, C_KEY, " Save it as a new clip?  A: save  B: no");
        else if (p->trimMessage != NULL)
            playerRow(15, C_WARN, " %s", p->trimMessage);
        else
            playerRow(15, C_NORMAL, "");
    }
    else
    {
        playerRow(14, C_NORMAL, "");
        playerRow(15, C_NORMAL, "");
    }

    // The Settings menu, over the controls while it's open.
    if (p->settingsOpen)
    {
        static const char *const names[SETTING_COUNT] = { "Loop", "Input display", "Timing details" };
        bool on[SETTING_COUNT] = { p->loop, p->showInputs, p->showStats };
        playerRow(20, C_INFO, " Settings");
        for (u32 i = 0; i < SETTING_COUNT; i++)
            playerRow(21 + i, i == p->settingsItem ? C_SELECTED : C_NORMAL, " %-14s %-3s%s", names[i],
                      on[i] ? "on" : "off", i == SETTING_INPUTS && p->clip.inputs == NULL ? " (none in this clip)" : "");
        playerRow(24, C_NORMAL, "");
        playerRow(25, C_DIM, " Up/Down: choose  A: change  X/B: close");
        for (int r = 26; r <= 28; r++)
            playerRow(r, C_NORMAL, "");
    }
}

// One frame of the player. Returns false when it should close (with trimSave
// set if the selection is to be saved).
static bool playerUpdate(Player *p, u32 down, u32 repeat, u32 held)
{
    u32 count = p->clip.info.frameCount, fps = p->clip.info.fps;

    // Asking whether to save the selection: nothing else until it's answered.
    if (p->trimConfirm)
    {
        if (down & KEY_A)
        {
            p->trimSave = true;
            return false;
        }
        if (down & (KEY_B | KEY_X))
            p->trimConfirm = false;
        return true;
    }

    // The Settings menu takes the buttons while it's open; the clip plays on.
    if (p->settingsOpen)
    {
        if (repeat & KEY_DUP)
            p->settingsItem = (p->settingsItem + SETTING_COUNT - 1) % SETTING_COUNT;
        if (repeat & KEY_DDOWN)
            p->settingsItem = (p->settingsItem + 1) % SETTING_COUNT;
        if (down & (KEY_A | KEY_DLEFT | KEY_DRIGHT))
            playerToggleSetting(p, p->settingsItem);
        if (down & (KEY_B | KEY_X))
        {
            p->settingsOpen = false;
            drawPlayerStatic(p);
        }
        down = repeat = 0;
    }

    // SELECT starts and stops trimming (B stops it too, rather than leaving).
    if ((down & KEY_SELECT) || (p->trimMode && (down & KEY_B)))
    {
        p->trimMode = !p->trimMode;
        p->trimMessage = NULL;
        drawPlayerStatic(p);
    }
    else if (down & KEY_B)
        return false;

    // What plays: the selection while trimming, otherwise the whole clip.
    u32 lo = p->trimMode ? p->trimIn : 0, hi = p->trimMode ? p->trimOut + 1 : count;

    if (down & KEY_A)
    {
        if (p->playing)
            p->playing = false;
        else
        {
            if (p->frame + 1 >= hi || p->frame < lo)
                p->frame = lo;
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
    if (p->trimMode)
    {
        if (repeat & KEY_DUP)
            playerSeek(p, (s32)p->frame + (s32)fps);
        if (repeat & KEY_DDOWN)
            playerSeek(p, (s32)p->frame - (s32)fps);
        if (down & KEY_L)
        {
            p->trimIn = p->frame;
            if (p->trimOut < p->trimIn)
                p->trimOut = p->trimIn;
            p->trimMessage = NULL;
        }
        if (down & KEY_R)
        {
            p->trimOut = p->frame;
            if (p->trimIn > p->trimOut)
                p->trimIn = p->trimOut;
            p->trimMessage = NULL;
        }
        if (down & KEY_X)
        {
            if (p->trimIn == 0 && p->trimOut + 1 == count)
                p->trimMessage = "Set a start (L) or an end (R) first.";
            else
            {
                p->trimConfirm = true;
                p->playing = false;
            }
        }
        if (down & KEY_Y)
            p->loop = !p->loop;
        lo = p->trimIn;
        hi = p->trimOut + 1;
    }
    else
    {
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
        if (down & KEY_X)
            p->settingsOpen = true;
    }
    p->scrubbing = false;
    if (!(held & KEY_TOUCH))
        p->touchWait = false;
    if ((held & KEY_TOUCH) && !p->touchWait)
    {
        touchPosition touch;
        hidTouchRead(&touch);
        if (touch.py >= BAR_TOUCH_Y0 && touch.py < BAR_TOUCH_Y1)
        {
            p->scrubbing = true;
            s32 x = (s32)touch.px - BAR_X;
            x = x < 0 ? 0 : x >= BAR_W ? BAR_W - 1 : x;
            playerSeek(p, count > 1 ? (s32)((u64)x * (count - 1) / (BAR_W - 1)) : 0);
        }
    }

    if (p->playing && p->error == NULL)
    {
        u32 frame = playerClockFrame(p);
        if (frame >= hi)
        {
            if (p->loop)
            {
                p->frame = lo + (frame - hi) % (hi - lo);
                playerRebase(p);
            }
            else
            {
                p->frame = hi - 1;
                p->playing = false;
            }
        }
        else
            p->frame = frame;
    }

    // While it plays, the frames after this one are read ahead too (paused,
    // stepping or scrubbing, just this one).
    u32 entry = flbEntryForFrame(&p->clip, p->frame);
    if (g_ahead.running && p->error == NULL)
        aheadWant(entry, p->playing && !p->scrubbing ? AHEAD_BUFS : 1, flbEntryForFrame(&p->clip, lo),
                  flbEntryForFrame(&p->clip, hi - 1), p->loop);
    if (entry != p->shownEntry && p->error == NULL)
    {
        const u16 *frame = NULL;
        if (g_ahead.running)
            frame = aheadGet(entry, &p->error, &p->decodeUs);
        else
        {
            u64 t0 = svcGetSystemTick();
            if (flbDecodeEntry(&p->clip, entry, g_frame, &p->error))
                frame = g_frame;
            p->decodeUs = (u32)((svcGetSystemTick() - t0) * 1000000 / SYSCLOCK_ARM11);
        }
        if (frame != NULL)
        {
            u64 t1 = svcGetSystemTick();
            playerDrawTop(p, frame);
            u64 t2 = svcGetSystemTick();
            p->drawUs = (u32)((t2 - t1) * 1000000 / SYSCLOCK_ARM11);
            if (p->playing && p->shownEntry != UINT32_MAX && entry > p->shownEntry + 1)
                p->skipped += entry - p->shownEntry - 1;
            p->shownEntry = entry;
        }
        else
            p->playing = false;
    }
    else if (p->error == NULL && p->shownEntry != UINT32_MAX && playerInputsStale(p))
        playerRedrawInputs(p);
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
    for (int i = 0; i < AHEAD_BUFS; i++)
        g_aheadBuf[i] = malloc(TOP_W * TOP_H * 2);
    g_player.loop = true;
    g_player.showInputs = true;

    g_audioMem = linearAlloc(AUDIO_BUFS * AUDIO_BUF_SAMPLES * 4);
    g_ndsp = g_audioMem != NULL && R_SUCCEEDED(ndspInit());
    if (g_ndsp)
        ndspSetOutputMode(NDSP_OUTPUT_STEREO);

    ListState ls = { .previewFor = -1 };
    bool playing = false, dirty = true;
    u64 lastTick = svcGetSystemTick();

    clearTop();
    cleanUpLeftovers();
    scanClips(true);

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
                // The selection to save, if any (its size while the clip is still open).
                Player *p = &g_player;
                u32 trimFrames = p->trimOut + 1 - p->trimIn;
                u64 trimBytes = p->trimSave ? flbTrimSize(&p->clip, p->trimIn, trimFrames) : 0;
                audioStop(p);
                aheadStop();
                flbClose(&p->clip);
                playing = false;
                consoleClear();
                scanClips(true);
                clampList(&ls);
                ls.previewFor = -1;
                ls.settle = 0;
                dirty = true;
                if (p->trimSave)
                    startTrim(&ls, p->fileName, p->trimIn, trimFrames, trimBytes);
            }
            else
            {
                playerAudio(&g_player);
                u64 t0 = svcGetSystemTick();
                drawPlayer(&g_player);
                g_player.textUs = (u32)((svcGetSystemTick() - t0) * 1000000 / SYSCLOCK_ARM11);
            }
        }
        else if (ls.compacting)
        {
            if (down & KEY_B)
            {
                flbCompactCancel(&g_compactor);
                ls.compacting = false;
                setMessage(&ls, ls.trimming ? "Trimming cancelled; nothing was saved." :
                                              "Compacting cancelled; the clip wasn't changed.", true);
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
                    // (Selects the trimmed copy, once it's saved.)
                    ls.compacting = false;
                    scanClips(true);
                    selectClip(&ls, ls.jobClip);
                    clampList(&ls);
                    unsigned long secs = (unsigned long)((svcGetSystemTick() - ls.compactStart) / SYSCLOCK_ARM11);
                    const Clip *c = &g_clips[ls.selected];
                    if (result == 1 && ls.trimming)
                    {
                        char length[12], size[16];
                        formatLength(c->info.frameCount, c->info.fps, length, sizeof(length));
                        formatSize(c->info.fileSize, size, sizeof(size));
                        snprintf(g_messageText, sizeof(g_messageText), "Trimmed: saved %.50s (%s, %s) in %lu s.",
                                 ls.jobClip, length, size, secs);
                        setMessage(&ls, g_messageText, true);
                    }
                    else if (result == 1)
                    {
                        char from[16], to[16];
                        formatSize(ls.compactFrom, from, sizeof(from));
                        formatSize(c->info.fileSize, to, sizeof(to));
                        snprintf(g_messageText, sizeof(g_messageText), "Compacted: %s -> %s in %lu s.", from, to, secs);
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
                    ls.trimming = false;
                    snprintf(ls.jobClip, sizeof(ls.jobClip), "%s", c->name);
                    ls.compactFrom = c->info.fileSize;
                    ls.compactStart = svcGetSystemTick();
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
                scanClips(true);
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
            // Left/Right move along the grid, Up/Down a row (Down from a row
            // above a shorter last one goes to the last clip). Touching a clip
            // selects it, and touching the selected one plays it.
            int before = ls.selected;
            bool play = (down & KEY_A) != 0;
            if (repeat & KEY_DLEFT)
                ls.selected--;
            if (repeat & KEY_DRIGHT)
                ls.selected++;
            if ((repeat & KEY_DUP) && ls.selected >= GRID_COLS)
                ls.selected -= GRID_COLS;
            if ((repeat & KEY_DDOWN) && ls.selected / GRID_COLS < (g_clipCount - 1) / GRID_COLS)
                ls.selected += GRID_COLS;
            if (down & KEY_TOUCH)
            {
                touchPosition touch;
                hidTouchRead(&touch);
                int idx = tileAt(&ls, touch.px, touch.py);
                if (idx >= 0 && idx == before)
                    play = true;
                else if (idx >= 0)
                    ls.selected = idx;
            }
            clampList(&ls);
            if (ls.selected != before)
            {
                ls.message = NULL;
                ls.settle = 0;
                dirty = true;
            }

            if (down & KEY_Y)
            {
                scanClips(false);
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
            else if ((down & KEY_L) && g_clipCount > 0)
            {
                renameClip(&ls);
                dirty = true;
            }
            else if ((play || (down & KEY_SELECT)) && g_clipCount > 0)
            {
                // (A damaged clip's details already say why it can't be played.)
                // SELECT opens it for trimming, paused at the start.
                const Clip *c = &g_clips[ls.selected];
                const char *error;
                if (c->error == NULL)
                {
                    if (playerOpen(&g_player, c, &error))
                    {
                        playing = true;
                        if (down & KEY_SELECT)
                        {
                            g_player.trimMode = true;
                            g_player.playing = false;
                        }
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
            // Otherwise read the thumbnail of a clip on screen that hasn't got one
            // yet: one per pass, so the controls stay responsive.
            else if (!playing)
            {
                for (int i = ls.scroll * GRID_COLS; i < g_clipCount && i < (ls.scroll + GRID_ROWS) * GRID_COLS; i++)
                {
                    Clip *c = &g_clips[i];
                    if (c->thumb == NULL && !c->thumbFailed && c->error == NULL)
                    {
                        loadThumb(c);
                        dirty = true;
                        break;
                    }
                }
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
    {
        audioStop(&g_player);
        aheadStop();
        flbClose(&g_player.clip);
    }
    if (g_ndsp)
        ndspExit();
    if (g_audioMem != NULL)
        linearFree(g_audioMem);
    if (ls.compacting)
        flbCompactCancel(&g_compactor);
    for (int i = 0; i < g_clipCount; i++)
        free(g_clips[i].thumb);
    for (int i = 0; i < AHEAD_BUFS; i++)
        free(g_aheadBuf[i]);
    free(g_frame);
    free(g_clips);
    gfxExit();
    return 0;
}
