/* PC test of Flashback Studio's read-ahead thread (main.c's "Reading ahead"
   section, extracted to ahead_extracted.c by run_tests.py), with pthreads
   standing in for libctru and a stand-in decoder that takes a random while
   and fills the buffer with the entry's number. A simulated player plays,
   seeks, pauses, scrubs, changes the looped part, and checks every frame it
   gets is the entry it asked for and stays put while it "draws" it. */
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

#define TOP_W 400
#define TOP_H 240
#define SYSCLOCK_ARM11 268111856ULL
#define U64_MAX UINT64_MAX
#define CUR_THREAD_HANDLE 0xFFFF8000

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

typedef struct { pthread_t t; void (*fn)(void *); void *arg; } ThreadS;
typedef ThreadS *Thread;
static void *threadTrampoline(void *p) { ThreadS *t = p; t->fn(t->arg); return NULL; }
static Thread threadCreate(void (*fn)(void *), void *arg, size_t stack, int prio, int core, bool detached)
{
    (void)stack; (void)prio; (void)core; (void)detached;
    ThreadS *t = malloc(sizeof(*t));
    t->fn = fn; t->arg = arg;
    if (pthread_create(&t->t, NULL, threadTrampoline, t) != 0) { free(t); return NULL; }
    return t;
}
static void threadJoin(Thread t, u64 timeout) { (void)timeout; pthread_join(t->t, NULL); }
static void threadFree(Thread t) { free(t); }
static int svcGetThreadPriority(s32 *prio, u32 handle) { (void)handle; *prio = 0x30; return 0; }
static u64 svcGetSystemTick(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (u64)ts.tv_sec * SYSCLOCK_ARM11 + (u64)ts.tv_nsec * SYSCLOCK_ARM11 / 1000000000ULL;
}

/* ---- the clip, standing in for flb.c */
typedef struct { u32 entryCount; } FlbInfo;
typedef struct { FlbInfo info; bool open; } FlbClip;
static u32 g_entries = 50;
static u32 g_failEntry = UINT32_MAX;     /* this entry can't be read */
static volatile int g_opens, g_decodes;
static pthread_mutex_t g_randLock = PTHREAD_MUTEX_INITIALIZER;
static int lockedRand(void) { pthread_mutex_lock(&g_randLock); int r = rand(); pthread_mutex_unlock(&g_randLock); return r; }

static bool flbOpen(FlbClip *c, const char *path, const char **error)
{
    (void)path; (void)error;
    c->info.entryCount = g_entries;
    c->open = true;
    __sync_fetch_and_add(&g_opens, 1);
    return true;
}
static void flbClose(FlbClip *c) { if (c->open) __sync_fetch_and_sub(&g_opens, 1); c->open = false; }
static u16 tag(u32 e) { return (u16)(e * 7 + 3); }
static bool flbDecodeEntry(FlbClip *c, u32 entry, u16 *dst, const char **error)
{
    if (!c->open || entry >= c->info.entryCount) { fprintf(stderr, "ERROR: decode of entry %u (open %d)\n", entry, c->open); exit(1); }
    __sync_fetch_and_add(&g_decodes, 1);
    dst[0] = 0xDEAD;    /* half written while it's being decoded */
    usleep(lockedRand() % 3 == 0 ? lockedRand() % 3000 : lockedRand() % 300);
    if (entry == g_failEntry) { *error = "frame damaged"; return false; }
    for (u32 i = 0; i < TOP_W * TOP_H; i += 997)
        dst[i] = tag(entry);
    dst[TOP_W * TOP_H - 1] = tag(entry);
    return true;
}

#include "ahead_extracted.c"

static long g_errors;
#define FAIL(...) do { fprintf(stderr, "ERROR: " __VA_ARGS__); g_errors++; } while (0)

static bool check(const u16 *f, u32 e)
{
    for (u32 i = 0; i < TOP_W * TOP_H; i += 997)
        if (f[i] != tag(e)) return false;
    return f[TOP_W * TOP_H - 1] == tag(e);
}

/* One player session: 'steps' frames of random use. */
static void session(int steps, bool withFailure)
{
    g_entries = 1 + rand() % 120;
    g_failEntry = withFailure ? (u32)(rand() % g_entries) : UINT32_MAX;
    aheadStart("clip");
    if (!g_ahead.running) { FAIL("didn't start\n"); return; }

    u32 entry = 0, first = 0, last = g_entries - 1;
    bool playing = rand() % 2, loop = rand() % 2, failed = false;
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
        else if (r < 97)
            loop = !loop;
        else
            usleep(rand() % 5000);                      /* the player waits a while (the HOME menu) */

        u32 count = playing ? AHEAD_BUFS : 1;
        aheadWant(entry, count, first, last, loop);
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
    }
    aheadStop();
    if (g_ahead.running || g_opens != 0) FAIL("not stopped (%d clips open)\n", g_opens);
    (void)waitsWhilePlaying;
}

int main(int argc, char **argv)
{
    srand(argc > 1 ? atoi(argv[1]) : 1);
    for (int i = 0; i < AHEAD_BUFS; i++)
        g_aheadBuf[i] = malloc(TOP_W * TOP_H * 2);
    pthread_t wd;
    pthread_create(&wd, NULL, watchdog, NULL);

    int sessions = argc > 2 ? atoi(argv[2]) : 400;
    for (int n = 0; n < sessions; n++)
        session(150 + rand() % 300, n % 5 == 4);
    printf("%d sessions, %ld frames asked for\n", sessions, g_progress);

    /* Reading ahead keeps up: a steady sequential play with time between frames
       never waits after the first few. */
    g_entries = 300;
    g_failEntry = UINT32_MAX;
    aheadStart("clip");
    int waits = 0;
    for (u32 e = 0; e < 300; e++)
    {
        aheadWant(e, AHEAD_BUFS, 0, 299, false);
        u64 t0 = svcGetSystemTick();
        const char *error = NULL;
        u32 us;
        const u16 *f = aheadGet(e, &error, &us);
        if (f == NULL || !check(f, e)) FAIL("steady play: wrong frame %u\n", e);
        if (e >= AHEAD_BUFS && svcGetSystemTick() - t0 > SYSCLOCK_ARM11 / 1000) waits++;
        g_progress++;
        usleep(6000);   /* (a frame's time, longer than the slowest decode) */
    }
    aheadStop();
    printf("steady play: waited for %d of 294 frames read ahead\n", waits);
    if (waits > 3) FAIL("reading ahead didn't keep up\n");

    printf("decodes %d\n%s (%ld errors)\n", g_decodes, g_errors ? "FAILED" : "all good", g_errors);
    return g_errors != 0;
}
