/* PC test of Flashback Studio's read-ahead threads (main.c's "Reading ahead"
   and "Reading the bottom screen ahead" sections, extracted to
   ahead_extracted.c by run_tests.py), with pthreads standing in for libctru
   and a stand-in decoder that takes a random while and fills the buffer with
   the entry's number. A simulated player plays, seeks, pauses, scrubs, changes
   the looped part, and checks every frame it gets is the entry it asked for
   and stays put while it "draws" it. With the clip's bottom screen up, it
   checks the same of each bottom screen picture it gets, without waiting for
   it while playing (as the player doesn't). */
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

typedef uint16_t u16;
typedef uint32_t u32;
typedef int32_t s32;
typedef uint64_t u64;
typedef s32 Result;

#define TOP_W 400
#define TOP_H 240
#define BOT_W 320
#define BOT_H 240
#define SYSCLOCK_ARM11 268111856ULL
#define U64_MAX UINT64_MAX
#define CUR_THREAD_HANDLE 0xFFFF8000
#define R_SUCCEEDED(r) ((r) >= 0)

/* (Windows sleeps are 1-15 ms at least: wait microseconds by yielding instead) */
static void preciseSleep(unsigned us)
{
    struct timespec a, b;
    clock_gettime(CLOCK_MONOTONIC, &a);
    do { sched_yield(); clock_gettime(CLOCK_MONOTONIC, &b); }
    while ((b.tv_sec - a.tv_sec) * 1000000L + (b.tv_nsec - a.tv_nsec) / 1000 < (long)us);
}
#define usleep preciseSleep

/* a watchdog: a hang (deadlock) fails the test instead of running forever */
static volatile long g_progress;
static void *watchdog(void *arg)
{
    (void)arg;
    long last = -1;
    for (;;)
    {
        sleep(10);
        if (g_progress == last) { fprintf(stderr, "ERROR: no progress for 10 s (a hang)\n"); exit(2); }
        last = g_progress;
    }
    return NULL;
}

typedef pthread_mutex_t LightLock;
static void LightLock_Init(LightLock *l) { pthread_mutex_init(l, NULL); }
static void LightLock_Lock(LightLock *l) { pthread_mutex_lock(l); }
static void LightLock_Unlock(LightLock *l) { pthread_mutex_unlock(l); }

typedef enum { RESET_ONESHOT, RESET_STICKY } ResetType;
typedef struct { pthread_mutex_t m; pthread_cond_t c; int state; } LightEvent;
static void LightEvent_Init(LightEvent *e, ResetType t) { (void)t; pthread_mutex_init(&e->m, NULL); pthread_cond_init(&e->c, NULL); e->state = 0; }
static void LightEvent_Signal(LightEvent *e) { pthread_mutex_lock(&e->m); e->state = 1; pthread_cond_signal(&e->c); pthread_mutex_unlock(&e->m); }
static void LightEvent_Wait(LightEvent *e) { pthread_mutex_lock(&e->m); while (!e->state) pthread_cond_wait(&e->c, &e->m); e->state = 0; pthread_mutex_unlock(&e->m); }

/* Threads. One on the system core (1) only once the app has some of its time,
   and only one at a time there; in some sessions, none at all there. */
static bool g_systemCoreRefused;
static int g_onSystemCore, g_systemCoreAsked;
typedef struct { pthread_t t; void (*fn)(void *); void *arg; int core; } ThreadS;
typedef ThreadS *Thread;
static void *threadTrampoline(void *p) { ThreadS *t = p; t->fn(t->arg); return NULL; }
static Thread threadCreate(void (*fn)(void *), void *arg, size_t stack, int prio, int core, bool detached)
{
    (void)stack; (void)prio; (void)detached;
    if (core == 1 && (g_systemCoreAsked == 0 || g_systemCoreRefused || g_onSystemCore > 0)) return NULL;
    ThreadS *t = malloc(sizeof(*t));
    t->fn = fn; t->arg = arg; t->core = core;
    if (pthread_create(&t->t, NULL, threadTrampoline, t) != 0) { free(t); return NULL; }
    if (core == 1) g_onSystemCore++;
    return t;
}
static void threadJoin(Thread t, u64 timeout) { (void)timeout; pthread_join(t->t, NULL); }
static void threadFree(Thread t) { if (t->core == 1) g_onSystemCore--; free(t); }
static int svcGetThreadPriority(s32 *prio, u32 handle) { (void)handle; *prio = 0x30; return 0; }
static Result APT_GetAppCpuTimeLimit(u32 *percent) { *percent = 0; return 0; }
static Result APT_SetAppCpuTimeLimit(u32 percent)
{
    if (percent < 5 || percent > 89) { fprintf(stderr, "ERROR: system core time %u%%\n", percent); exit(1); }
    g_systemCoreAsked++;
    return 0;
}
static u64 svcGetSystemTick(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (u64)ts.tv_sec * SYSCLOCK_ARM11 + (u64)ts.tv_nsec * SYSCLOCK_ARM11 / 1000000000ULL;
}

/* ---- the clip, standing in for flb.c. Its bottom screen has frames at some
   entries (g_bottomAt); before the first whole one (g_firstWhole), it's
   unknown, which is black. */
enum { FLB_TOP, FLB_BOTTOM };
typedef struct { u32 bottomSize, copies; } FlbEntry;
typedef struct { u32 entryCount, bottomWidth, bottomHeight, fps; } FlbInfo;
#define FRAME_US 6000   /* the simulated player's frame time... */
#define FPS 166         /* ...as a frame rate */
typedef struct { FlbInfo info; bool open; FlbEntry *entries; u16 *picture[2]; bool known[2]; } FlbClip;
static u32 g_entries = 50;
static u32 g_failEntry = UINT32_MAX;     /* this entry can't be read */
static bool g_hasBottom = true;
static bool g_bottomAt[1000];
static u32 g_firstWhole;
static unsigned g_bottomReadUs;          /* (if set: how long reading each of its pictures takes) */
static volatile int g_timesRead[1000];   /* how many times each of its pictures was read */
static volatile int g_opens, g_decodes, g_bottomReads;
static pthread_mutex_t g_randLock = PTHREAD_MUTEX_INITIALIZER;
static int lockedRand(void) { pthread_mutex_lock(&g_randLock); int r = rand(); pthread_mutex_unlock(&g_randLock); return r; }

static bool flbOpen(FlbClip *c, const char *path, const char **error)
{
    (void)path; (void)error;
    memset(c, 0, sizeof(*c));
    c->info.entryCount = g_entries;
    c->info.bottomWidth = g_hasBottom ? BOT_W : 0;
    c->info.bottomHeight = g_hasBottom ? BOT_H : 0;
    c->info.fps = FPS;
    c->entries = calloc(g_entries, sizeof(FlbEntry));
    for (u32 e = 0; e < g_entries; e++)
    {
        c->entries[e].bottomSize = g_hasBottom && g_bottomAt[e] ? 1000 : 0;
        c->entries[e].copies = 1;
    }
    c->picture[FLB_BOTTOM] = malloc(BOT_W * BOT_H * 2);
    c->open = true;
    __sync_fetch_and_add(&g_opens, 1);
    return true;
}
static void flbClose(FlbClip *c)
{
    if (c->open) __sync_fetch_and_sub(&g_opens, 1);
    c->open = false;
    free(c->entries);
    free(c->picture[FLB_BOTTOM]);
    c->entries = NULL;
    c->picture[FLB_BOTTOM] = NULL;
}
static u16 tag(u32 e) { return (u16)(e * 7 + 3); }
static u16 bottomTag(u32 e) { return (u16)(e * 13 + 5); }
static void fill(u16 *dst, u32 n, u16 v)
{
    for (u32 i = 0; i < n; i += 997)
        dst[i] = v;
    dst[n - 1] = v;
}
static bool flbDecodeEntry(FlbClip *c, u32 entry, u16 *dst, const char **error)
{
    if (!c->open || entry >= c->info.entryCount) { fprintf(stderr, "ERROR: decode of entry %u (open %d)\n", entry, c->open); exit(1); }
    __sync_fetch_and_add(&g_decodes, 1);
    dst[0] = 0xDEAD;    /* half written while it's being decoded */
    usleep(lockedRand() % 3 == 0 ? lockedRand() % 3000 : lockedRand() % 300);
    if (entry == g_failEntry) { *error = "frame damaged"; return false; }
    fill(dst, TOP_W * TOP_H, tag(entry));
    return true;
}
/* (only the bottom screen's picture, as of an entry with a frame of it:
   black while unknown) */
static bool flbDecodeScreens(FlbClip *c, u32 entry, u16 *top, u16 *bottom, const char **error)
{
    if (!c->open || entry >= c->info.entryCount || top != NULL || bottom == NULL || c->info.bottomWidth == 0 ||
        !g_bottomAt[entry])
    { fprintf(stderr, "ERROR: bottom screen picture %u asked for wrongly\n", entry); exit(1); }
    __sync_fetch_and_add(&g_bottomReads, 1);
    __sync_fetch_and_add(&g_timesRead[entry], 1);
    bottom[0] = 0xDEAD;     /* half written while it's being read */
    usleep(g_bottomReadUs ? g_bottomReadUs : (unsigned)(lockedRand() % 3 == 0 ? lockedRand() % 4000 : lockedRand() % 400));
    if (entry == g_failEntry) { *error = "frame damaged"; return false; }
    fill(bottom, BOT_W * BOT_H, entry >= g_firstWhole ? bottomTag(entry) : 0);
    return true;
}

#include "ahead_extracted.c"

static long g_errors;
#define FAIL(...) do { fprintf(stderr, "ERROR: " __VA_ARGS__); g_errors++; } while (0)

static bool filled(const u16 *f, u32 n, u16 v)
{
    for (u32 i = 0; i < n; i += 997)
        if (f[i] != v) return false;
    return f[n - 1] == v;
}
static bool check(const u16 *f, u32 e) { return filled(f, TOP_W * TOP_H, tag(e)); }

/* The bottom screen's picture shown at entry e: its id, and what it looks like
   (black before its first frame, or before its first whole one) */
static u32 bottomIdAt(u32 e)
{
    for (u32 i = e + 1; i-- > 0;)
        if (g_bottomAt[i]) return i;
    return BOTTOM_START;
}
static u16 bottomLook(u32 id) { return id == BOTTOM_START || id < g_firstWhole ? 0 : bottomTag(id); }

/* A new clip's bottom screen: frames at about one entry in 'every' */
static void makeBottom(int every)
{
    memset(g_bottomAt, 0, sizeof(g_bottomAt));
    for (u32 e = 0; e < g_entries; e++)
        g_bottomAt[e] = rand() % every == 0;
    g_firstWhole = rand() % 3 == 0 ? 0 : (u32)(rand() % (g_entries + 1));
}

static int g_bottomWaits, g_bottomGets;

/* With the bottom screen up, entry 'entry''s picture: the right one, staying
   put while drawn, and paused, there within a while. (Playing, the player
   doesn't wait for it, and the thread passes over pictures it can't read in
   time.) Returns false if it came back as an error (the damaged one). */
static bool checkBottom(u32 entry, bool playing, bool *failed)
{
    u32 id = 0;
    const char *error = NULL;
    const u16 *b = bottomGet(entry, &id, &error);
    g_bottomGets++;
    if (id != bottomIdAt(entry)) FAIL("entry %u's bottom screen picture said to be %u, not %u\n", entry, id, bottomIdAt(entry));
    if (b == NULL && error == NULL && playing)
    {
        g_bottomWaits++;
        return true;
    }
    if (b == NULL && error == NULL)
    {
        g_bottomWaits++;
        for (int t = 0; t < 4000 && b == NULL && error == NULL; t++)
        {
            usleep(250);
            b = bottomGet(entry, &id, &error);
        }
    }
    if (b == NULL)
    {
        if (error == NULL) { FAIL("entry %u's bottom screen picture (%u) never came\n", entry, id); return true; }
        if (id != g_failEntry) FAIL("bottom screen picture %u failed, but only %u is damaged\n", id, g_failEntry);
        *failed = true;
        return false;
    }
    if (error != NULL) FAIL("a bottom screen picture and an error\n");
    if (!filled(b, BOT_W * BOT_H, bottomLook(id))) FAIL("entry %u came with the wrong bottom screen (picture %u)\n", entry, id);
    usleep(rand() % 300);                               /* copying it in */
    if (!filled(b, BOT_W * BOT_H, bottomLook(id))) FAIL("bottom screen picture %u overwritten while drawn\n", id);
    return true;
}

/* One player session: 'steps' frames of random use. */
static void session(int steps, bool withFailure)
{
    g_entries = 1 + rand() % 120;
    g_failEntry = withFailure ? (u32)(rand() % g_entries) : UINT32_MAX;
    g_hasBottom = rand() % 4 != 0;
    g_systemCoreRefused = rand() % 3 == 0;
    makeBottom(1 + rand() % 4);
    if (g_failEntry != UINT32_MAX)
        g_bottomAt[g_failEntry] = true;     /* (the damaged entry's bottom screen picture is damaged too) */
    aheadStart("clip");
    if (!g_ahead.running) { FAIL("didn't start\n"); return; }
    bottomStart("clip");
    if (g_bottom.running != g_hasBottom) FAIL("bottom screen reading %s\n", g_hasBottom ? "didn't start" : "started without one");
    if (g_bottom.running && (g_bottom.reader[0].systemCore == g_systemCoreRefused ||
                             g_bottom.readers != (g_systemCoreRefused ? 1u : 2u) || g_bottom.reader[g_bottom.readers - 1].systemCore))
        FAIL("readers: %u, the first %son the system core (refused: %d)\n", g_bottom.readers,
             g_bottom.reader[0].systemCore ? "" : "not ", g_systemCoreRefused);

    u32 entry = 0, first = 0, last = g_entries - 1;
    bool playing = rand() % 2, loop = rand() % 2, failed = false, bottom = rand() % 2;
    int waitsWhilePlaying = 0, gets = 0;
    for (int s = 0; s < steps && !failed; s++)
    {
        int r = rand() % 100;
        if (r < 70 && playing)
        {
            /* plays on: next entry (sometimes 2, as at 2x or behind), going round the part */
            int adv = rand() % 10 == 0 ? 2 : 1;
            for (int k = 0; k < adv; k++)
            {
                u32 n = aheadNext(&g_ahead, entry);     /* (the same rule the player's clock follows) */
                if (n == UINT32_MAX) { playing = false; break; }
                entry = n;
            }
        }
        else if (r < 80)
            entry = rand() % g_entries;                 /* a jump, or scrubbing */
        else if (r < 88)
            playing = !playing;
        else if (r < 94)
        {
            first = rand() % g_entries;                 /* trimming: a new part to play */
            last = first + rand() % (g_entries - first);
            if (entry < first || entry > last) entry = first;
        }
        else if (r < 96)
            loop = !loop;
        else if (r < 99)
            bottom = !bottom;                           /* the clip's bottom screen up or down (Y) */
        else
            usleep(rand() % 5000);                      /* the player waits a while (the HOME menu) */

        u32 count = playing ? AHEAD_BUFS : 1;
        aheadWant(entry, count, first, last, loop);
        if (g_bottom.running)
            bottomWant(entry, bottom ? (playing ? BOTTOM_LOOK : 1) : 0, first, last, loop);
        if (rand() % 4 == 0) usleep(rand() % 1500);     /* (time to decode ahead before it's needed) */
        const char *error = NULL;
        u32 us = 0;
        u64 t0 = svcGetSystemTick();
        const u16 *f = aheadGet(entry, &error, &us);
        u64 waited = svcGetSystemTick() - t0;
        gets++;
        g_progress++;
        if (playing && waited > SYSCLOCK_ARM11 / 2000) waitsWhilePlaying++;
        if (f == NULL)
        {
            if (error == NULL) FAIL("no frame and no error\n");
            if (entry != g_failEntry) FAIL("entry %u failed, but only %u is damaged\n", entry, g_failEntry);
            failed = true;
            break;
        }
        if (error != NULL) FAIL("a frame and an error\n");
        if (!check(f, entry)) FAIL("got the wrong frame for entry %u\n", entry);
        if (bottom && g_bottom.running && !checkBottom(entry, playing, &failed))
            break;
        usleep(rand() % 400);                           /* drawing it */
        if (!check(f, entry)) FAIL("entry %u's buffer was overwritten while drawn\n", entry);
    }
    if (withFailure && !failed && g_failEntry != UINT32_MAX)
    {
        /* ask for the damaged one: it must come back as an error, not hang */
        aheadWant(g_failEntry, 1, first, last, loop);
        const char *error = NULL;
        u32 us;
        if (aheadGet(g_failEntry, &error, &us) != NULL || error == NULL) FAIL("the damaged entry didn't fail\n");
        /* and paused there with the bottom screen up, its picture too (which
           must say so, not just never come) */
        if (g_bottom.running)
        {
            bottomWant(g_failEntry, 1, first, last, loop);
            u32 id;
            const u16 *b = NULL;
            error = NULL;
            for (int t = 0; t < 4000 && b == NULL && error == NULL; t++)
            {
                b = bottomGet(g_failEntry, &id, &error);
                if (b == NULL && error == NULL) usleep(250);
            }
            if (b != NULL || error == NULL) FAIL("the damaged bottom screen picture didn't fail\n");
        }
    }
    aheadStop();
    bottomStop();
    if (g_ahead.running || g_bottom.running || g_opens != 0) FAIL("not stopped (%d clips open)\n", g_opens);
    if (g_onSystemCore != 0) FAIL("a thread left on the system core\n");
    (void)waitsWhilePlaying;
}

int main(int argc, char **argv)
{
    srand(argc > 1 ? atoi(argv[1]) : 1);
    for (int i = 0; i < AHEAD_BUFS; i++)
        g_aheadBuf[i] = malloc(TOP_W * TOP_H * 2);
    for (int i = 0; i < BOTTOM_BUFS; i++)
        g_bottomBuf[i] = malloc(BOT_W * BOT_H * 2);
    pthread_t wd;
    pthread_create(&wd, NULL, watchdog, NULL);

    int sessions = argc > 2 ? atoi(argv[2]) : 400;
    for (int n = 0; n < sessions; n++)
        session(150 + rand() % 300, n % 5 == 4);
    printf("%d sessions, %ld frames asked for; bottom screen pictures asked for %d times, %d of them not there yet\n",
           sessions, g_progress, g_bottomGets, g_bottomWaits);
    if (g_systemCoreAsked != 1) FAIL("the system core's time asked for %d times, not once\n", g_systemCoreAsked);

    /* Reading ahead keeps up: a steady sequential play with time between frames
       never waits after the first few; nor, with it up, is the bottom screen's
       picture late (a frame of it about every other entry, as in a busy game). */
    for (int withBottom = 0; withBottom < 2; withBottom++)
    {
        g_entries = 300;
        g_failEntry = UINT32_MAX;
        g_hasBottom = true;
        g_systemCoreRefused = false;
        makeBottom(2);
        memset((void *)g_timesRead, 0, sizeof(g_timesRead));
        aheadStart("clip");
        bottomStart("clip");
        int waits = 0, late = 0;
        for (u32 e = 0; e < 300; e++)
        {
            aheadWant(e, AHEAD_BUFS, 0, 299, false);
            bottomWant(e, withBottom ? BOTTOM_LOOK : 0, 0, 299, false);
            u64 t0 = svcGetSystemTick();
            const char *error = NULL;
            u32 us, id;
            const u16 *f = aheadGet(e, &error, &us);
            if (f == NULL || !check(f, e)) FAIL("steady play: wrong frame %u\n", e);
            if (e >= AHEAD_BUFS && svcGetSystemTick() - t0 > SYSCLOCK_ARM11 / 1000) waits++;
            if (withBottom)
            {
                const u16 *b = bottomGet(e, &id, &error);
                if (b == NULL && e >= BOTTOM_BUFS) late++;
                if (b != NULL && !filled(b, BOT_W * BOT_H, bottomLook(id))) FAIL("steady play: wrong bottom screen at %u\n", e);
            }
            g_progress++;
            usleep(FRAME_US);   /* (a frame's time, longer than the slowest decode) */
        }
        aheadStop();
        bottomStop();
        printf("steady play%s: waited for %d of 294 frames read ahead%s", withBottom ? " with the bottom screen" : "", waits,
               withBottom ? "" : "\n");
        if (withBottom) printf(", %d bottom screen pictures late\n", late);
        if (waits > 3) FAIL("reading ahead didn't keep up\n");
        if (late > 3) FAIL("reading the bottom screen ahead didn't keep up\n");
        for (u32 e = 0; e < g_entries; e++)
            if (g_timesRead[e] > 1) { FAIL("steady play: bottom screen picture %u read %d times\n", e, g_timesRead[e]); break; }
    }

    /* Bottom screen readers slower than its pictures come (as on an Old 3DS):
       a picture every other frame, each taking 2.5 frames' time to read. One
       can't show them all, but it shows the ones it has time for (at most 80%
       of them) rather than reading each one too late to be shown; two (on both
       cores) show nearly all, rather than getting in each other's way. */
    for (int readers = 1; readers <= 2; readers++)
    {
        g_systemCoreRefused = readers == 1;
        g_entries = 300;
        g_failEntry = UINT32_MAX;
        g_hasBottom = true;
        memset(g_bottomAt, 0, sizeof(g_bottomAt));
        for (u32 e = 0; e < g_entries; e += 2)
            g_bottomAt[e] = true;
        g_firstWhole = 0;
        g_bottomReadUs = 15000;
        memset((void *)g_timesRead, 0, sizeof(g_timesRead));
        aheadStart("clip");
        bottomStart("clip");
        u32 shownId = UINT32_MAX, pictures = 0, shown = 0;
        for (u32 e = 0; e < 300; e++)
        {
            aheadWant(e, AHEAD_BUFS, 0, 299, false);
            bottomWant(e, BOTTOM_LOOK, 0, 299, false);
            const char *error = NULL;
            u32 us, id;
            if (aheadGet(e, &error, &us) == NULL) FAIL("slow bottom screen: no frame %u\n", e);
            const u16 *b = bottomGet(e, &id, &error);
            pictures += e == 0 || id != bottomIdAt(e - 1);
            if (b != NULL && id != shownId)
            {
                if (!filled(b, BOT_W * BOT_H, bottomLook(id))) FAIL("slow bottom screen: wrong picture at %u\n", e);
                shownId = id;
                shown++;
            }
            g_progress++;
            usleep(FRAME_US);
        }
        aheadStop();
        bottomStop();
        g_bottomReadUs = 0;
        printf("%s slow bottom screen reader%s (a picture every 12 ms, 15 ms to read one): %u of %u pictures shown\n",
               readers == 1 ? "a" : "two", readers == 1 ? "" : "s", shown, pictures);
        if (readers == 1 && shown * 2 < pictures) FAIL("the slow bottom screen reader showed under half of them\n");
        if (readers == 2 && shown * 10 < pictures * 9) FAIL("two slow bottom screen readers showed under 90%% of them\n");
        /* (playing straight through, no picture is wanted again once it's
           passed: none is read twice, by one reader or both) */
        for (u32 e = 0; e < g_entries; e++)
            if (g_timesRead[e] > 1) { FAIL("bottom screen picture %u read %d times\n", e, g_timesRead[e]); break; }
    }

    printf("decodes %d, bottom screen pictures read %d\n%s (%ld errors)\n", g_decodes, g_bottomReads,
           g_errors ? "FAILED" : "all good", g_errors);
    return g_errors != 0;
}
