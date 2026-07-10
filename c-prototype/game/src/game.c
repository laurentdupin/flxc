/*
    Demo game: zombies seek food patches, and six animated trees exercise
    the hierarchy machinery. Engine mechanisms demonstrated:

      - COMPONENT / SINGLETON / PREFAB declarations
      - SYSTEM with READ/WRITE/QUERY, parallel groups vs conflicts
      - generational Entity handles: Parent refs survive swap-remove,
        stale handles resolve to NULL (see Sway / PruneAndGrow)
      - LOOKUP clause: random access by handle (Sway reads its parent's
        world matrix from last frame)
      - FIRST clause: "any entity with T, or NULL" (VerifyHierarchy)
      - QUERY_MUT in TASKs, rejected in SYSTEMs at load
      - derived children lists + depth buckets rebuilt from Parent refs
        each frame (RebuildHierarchy)
      - ecs_parallel_for: ComputeWorldMatrices propagates transforms one
        depth level at a time, each level fanned across the worker pool
      - deferred create/destroy from parallel systems and tasks
      - buffer-and-flush: ecs_log -> FlushLog, ecs_draw -> EndFrame
      - MAIN_THREAD tasks for the Win32 window and message pump

    Environment:
      FABLEC_MAX_FRAMES   exit after N frames (default: run until window closes)
*/
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <stdio.h>
#include <stdlib.h>
#include <math.h>

#include "ecs_meta.h"

#define WORLD_W 960
#define WORLD_H 540
#define FOOD_COUNT 12
#define START_ZOMBIES 1200
#define MAX_ZOMBIES 2600
#define FOOD_AVAILABLE 60.0f

#define TREE_COUNT 6
#define TREE_DEPTH 7
#define NO_DENSE 0xffffffffu

/* ------------------------------------------------------------------ */
/* Data                                                                */
/* ------------------------------------------------------------------ */

COMPONENT(Position, { float x, y; });
COMPONENT(Velocity, { float dx, dy; });
COMPONENT(Hunger,   { float energy; });
COMPONENT(FoodInfo, { float x, y, amount; });

/* hierarchy */
COMPONENT(LocalTransform, { float x, y, angle, scale; });
COMPONENT(WorldMatrix,    { float a, b, c, d, tx, ty; });   /* 2D affine; a Mat4 is a drop-in swap */
COMPONENT(Parent,         { Entity e; });
COMPONENT(Children,       { uint32_t first, count; });      /* slice into ChildPool */

SINGLETON(FrameStats,  { uint32_t frame, max_frames; double total_ms; uint32_t spawned; });
SINGLETON(WindowState, { HWND hwnd; int quit; });
SINGLETON(CanvasState, { uint32_t *pixels; int w, h; HDC mem_dc; HBITMAP dib; });
SINGLETON(LogSink,     { uint32_t total; });
SINGLETON(ChildPool,   { Entity *items; uint32_t count, cap; });
SINGLETON(HierarchyData, {
    uint32_t *order;          /* dense indices sorted by depth */
    uint32_t *parent_dense;   /* dense index of parent, NO_DENSE for roots/stale */
    uint32_t *depth;
    uint32_t *scratch;
    uint32_t  cap, count, levels;
    uint32_t  level_start[34];
});

PREFAB(Zombie, Position, Velocity, Hunger);
PREFAB(Food, FoodInfo);
PREFAB(Node, LocalTransform, WorldMatrix, Parent, Children);

/* ------------------------------------------------------------------ */
/* Helpers                                                             */
/* ------------------------------------------------------------------ */

static uint32_t hash_u32(uint32_t x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

static float frand(uint32_t seed)   /* [0, 1) */
{
    return (float)(hash_u32(seed) & 0xffffffu) / 16777216.0f;
}

static WorldMatrix xf_compose(const LocalTransform *l)
{
    float cs = cosf(l->angle) * l->scale;
    float sn = sinf(l->angle) * l->scale;
    WorldMatrix m = { cs, -sn, sn, cs, l->x, l->y };
    return m;
}

static WorldMatrix xf_mul(const WorldMatrix *p, const WorldMatrix *c)
{
    WorldMatrix r;
    r.a = p->a * c->a + p->b * c->c;
    r.b = p->a * c->b + p->b * c->d;
    r.c = p->c * c->a + p->d * c->c;
    r.d = p->c * c->b + p->d * c->d;
    r.tx = p->a * c->tx + p->b * c->ty + p->tx;
    r.ty = p->c * c->tx + p->d * c->ty + p->ty;
    return r;
}

/* ------------------------------------------------------------------ */
/* Zombie systems                                                      */
/* ------------------------------------------------------------------ */

SYSTEM(Zombie, Seek, READ(Position, p), WRITE(Velocity, v), QUERY(FoodInfo, foods))
{
    float bdx = 0, bdy = 0, best = 1e30f;
    int found = 0;

    for (uint32_t i = 0; i < foods_count; ++i) {
        if (foods[i].amount < FOOD_AVAILABLE) {
            continue;
        }
        float dx = foods[i].x - p->x;
        float dy = foods[i].y - p->y;
        float d2 = dx * dx + dy * dy;
        if (d2 < best) {
            best = d2; bdx = dx; bdy = dy; found = 1;
        }
    }

    if (found) {
        float d = sqrtf(best) + 0.001f;
        v->dx += (bdx / d) * 90.0f * ctx->dt;
        v->dy += (bdy / d) * 90.0f * ctx->dt;
    }
    else {
        uint32_t s = hash_u32(ctx->self.slot * 31u + (uint32_t)(ctx->time * 3.0));
        v->dx += (frand(s) - 0.5f) * 60.0f * ctx->dt;
        v->dy += (frand(s ^ 0x9e3779b9u) - 0.5f) * 60.0f * ctx->dt;
    }

    float speed2 = v->dx * v->dx + v->dy * v->dy;
    if (speed2 > 80.0f * 80.0f) {
        float k = 80.0f / sqrtf(speed2);
        v->dx *= k;
        v->dy *= k;
    }
}

SYSTEM(Zombie, Move, READ(Velocity, v), WRITE(Position, p))
{
    p->x += v->dx * ctx->dt;
    p->y += v->dy * ctx->dt;

    if (p->x < 0) p->x += WORLD_W;
    if (p->x >= WORLD_W) p->x -= WORLD_W;
    if (p->y < 0) p->y += WORLD_H;
    if (p->y >= WORLD_H) p->y -= WORLD_H;
}

SYSTEM(Zombie, Digest, READ(Position, p), WRITE(Hunger, h), QUERY(FoodInfo, foods))
{
    int eating = 0;

    for (uint32_t i = 0; i < foods_count; ++i) {
        if (foods[i].amount < FOOD_AVAILABLE) {
            continue;
        }
        float dx = foods[i].x - p->x;
        float dy = foods[i].y - p->y;
        if (dx * dx + dy * dy < 28.0f * 28.0f) {
            eating = 1;
            break;
        }
    }

    h->energy += eating ? 35.0f * ctx->dt : -7.0f * ctx->dt;

    if (h->energy <= 0.0f) {
        ecs_destroy(ctx, ctx->self);
        if ((ctx->self.slot & 255u) == 0) {
            ecs_log(ctx, "zombie slot %u starved (population ~%u)", ctx->self.slot, ctx->count);
        }
    }
    else if (h->energy > 160.0f && ctx->count < MAX_ZOMBIES) {
        h->energy = 70.0f;

        uint32_t s = hash_u32(ctx->self.slot ^ (uint32_t)(ctx->time * 977.0));
        Entity child = ecs_create(ctx, Zombie);
        ecs_init(ctx, child, Position, { p->x + (frand(s) - 0.5f) * 12.0f,
                                         p->y + (frand(s ^ 7u) - 0.5f) * 12.0f });
        ecs_init(ctx, child, Velocity, { (frand(s ^ 13u) - 0.5f) * 60.0f,
                                         (frand(s ^ 29u) - 0.5f) * 60.0f });
        ecs_init(ctx, child, Hunger, { .energy = 70.0f });
    }
}

SYSTEM(Food, Regrow, WRITE(FoodInfo, f))
{
    f->amount = 60.0f + 55.0f * sinf((float)ctx->time * 0.6f + (float)ctx->self.slot * 1.7f);
}

SYSTEM(Zombie, Render, READ(Position, p), READ(Hunger, h))
{
    float e = h->energy;
    uint32_t r = e < 80.0f ? 255u : (uint32_t)(255.0f * (1.0f - (e - 80.0f) / 80.0f));
    uint32_t g = e > 80.0f ? 255u : (uint32_t)(255.0f * e / 80.0f);

    ecs_draw(ctx, (int)p->x, (int)p->y, (r << 16) | (g << 8) | 0x30u);
}

SYSTEM(Food, Render, READ(FoodInfo, f))
{
    uint32_t c = f->amount >= FOOD_AVAILABLE ? 0x30ff60u : 0x104020u;

    for (int dy = -2; dy <= 2; ++dy) {
        for (int dx = -2; dx <= 2; ++dx) {
            ecs_draw(ctx, (int)f->x + dx * 2, (int)f->y + dy * 2, c);
        }
    }
}

/* ------------------------------------------------------------------ */
/* Hierarchy systems                                                   */
/* ------------------------------------------------------------------ */

SYSTEM(Node, Spin, WRITE(LocalTransform, lt))
{
    float dir = (ctx->self.slot & 1u) ? 1.0f : -1.0f;
    lt->angle += ctx->dt * dir * (0.15f + 0.35f * frand(ctx->self.slot));
}

/* LOOKUP demo: read the parent's world matrix (from last frame's
   propagation). A pruned parent yields NULL and the node just skips. */
SYSTEM(Node, Sway, READ(Parent, par), LOOKUP(WorldMatrix, worlds), WRITE(LocalTransform, lt))
{
    const WorldMatrix *pw = ecs_lookup(worlds, par->e);
    if (!pw) {
        return;   /* root, or parent destroyed (stale handle) */
    }

    lt->scale = 0.86f + 0.04f * sinf((float)ctx->time * 2.0f + pw->tx * 0.02f);
}

SYSTEM(Node, Render, READ(WorldMatrix, wm))
{
    ecs_draw(ctx, (int)wm->tx, (int)wm->ty, 0x50c8ffu);
}

/* Rebuild the derived structures from authoritative Parent refs:
   depth buckets for propagation, and Children slices + the pool. */
static void hier_reserve(HierarchyData *h, ChildPool *pool, uint32_t n)
{
    if (n > h->cap) {
        uint32_t cap = h->cap ? h->cap : 2048;
        while (cap < n) cap *= 2;
        h->order = realloc(h->order, cap * sizeof(uint32_t));
        h->parent_dense = realloc(h->parent_dense, cap * sizeof(uint32_t));
        h->depth = realloc(h->depth, cap * sizeof(uint32_t));
        h->scratch = realloc(h->scratch, cap * sizeof(uint32_t));
        h->cap = cap;
    }
    if (n > pool->cap) {
        uint32_t cap = pool->cap ? pool->cap : 2048;
        while (cap < n) cap *= 2;
        pool->items = realloc(pool->items, cap * sizeof(Entity));
        pool->cap = cap;
    }
}

TASK(RebuildHierarchy, QUERY(Parent, par), QUERY_MUT(Children, ch),
     WRITE(ChildPool, pool), WRITE(HierarchyData, h))
{
    uint32_t n = par_count;
    uint32_t reg = ecs_register_of(ctx, Parent);

    hier_reserve(h, pool, n);
    h->count = n;
    if (n == 0) {
        h->levels = 0;
        pool->count = 0;
        return;
    }

    /* resolve handles once; stale parents (pruned) make orphans roots */
    for (uint32_t i = 0; i < n; ++i) {
        int32_t pd = ecs_entity_index(ctx, par[i].e);
        h->parent_dense[i] = pd < 0 ? NO_DENSE : (uint32_t)pd;
    }

    uint32_t maxd = 0;
    for (uint32_t i = 0; i < n; ++i) {
        uint32_t d = 0, j = i;
        while (h->parent_dense[j] != NO_DENSE && d < 32) {
            j = h->parent_dense[j];
            d++;
        }
        h->depth[i] = d;
        if (d > maxd) maxd = d;
    }
    h->levels = maxd + 1;

    for (uint32_t L = 0; L <= h->levels; ++L) h->level_start[L] = 0;
    for (uint32_t i = 0; i < n; ++i) h->level_start[h->depth[i] + 1]++;
    for (uint32_t L = 1; L <= h->levels; ++L) h->level_start[L] += h->level_start[L - 1];

    for (uint32_t L = 0; L < h->levels; ++L) h->scratch[L] = h->level_start[L];
    for (uint32_t i = 0; i < n; ++i) h->order[h->scratch[h->depth[i]]++] = i;

    /* derived children slices (counting sort by parent) */
    for (uint32_t i = 0; i < n; ++i) ch[i].count = 0;
    for (uint32_t i = 0; i < n; ++i) {
        if (h->parent_dense[i] != NO_DENSE) ch[h->parent_dense[i]].count++;
    }

    uint32_t run = 0;
    for (uint32_t i = 0; i < n; ++i) {
        ch[i].first = run;
        run += ch[i].count;
        h->scratch[i] = ch[i].first;
    }
    pool->count = run;

    for (uint32_t i = 0; i < n; ++i) {
        uint32_t pd = h->parent_dense[i];
        if (pd != NO_DENSE) {
            pool->items[h->scratch[pd]++] = ecs_entity_at(ctx, reg, i);
        }
    }
}

/* Propagation: the one algorithm whose ordering invariant lives inside a
   single column, so it runs as a TASK holding the write bit and fans each
   depth level across the pool with ecs_parallel_for. */
typedef struct LvlArgs {
    WorldMatrix          *wm;
    const LocalTransform *lt;
    const HierarchyData  *h;
    uint32_t              base;
} LvlArgs;

static void compute_level(SysCtx *sub, void *user)
{
    LvlArgs *a = (LvlArgs *)user;

    for (uint32_t k = sub->first; k < sub->last; ++k) {
        uint32_t i = a->h->order[a->base + k];
        WorldMatrix local = xf_compose(&a->lt[i]);
        uint32_t pd = a->h->parent_dense[i];
        a->wm[i] = (pd == NO_DENSE) ? local : xf_mul(&a->wm[pd], &local);
    }
}

TASK(ComputeWorldMatrices, QUERY_MUT(WorldMatrix, wm), QUERY(LocalTransform, lt),
     READ(HierarchyData, h))
{
    LvlArgs a = { wm, lt, h, 0 };

    for (uint32_t L = 0; L < h->levels; ++L) {
        a.base = h->level_start[L];
        ecs_parallel_for(ctx, h->level_start[L + 1] - a.base, compute_level, &a);
    }
}

/* Structural churn: destroy a random node, grow a fresh one 窶・exercises
   swap-remove under live Parent handles and stale-handle adoption. */
TASK(PruneAndGrow, QUERY(Parent, par), READ(FrameStats, fs))
{
    if (fs->frame % 90 != 30 || par_count == 0) {
        return;
    }

    uint32_t reg = ecs_register_of(ctx, Parent);
    uint32_t seed = hash_u32(fs->frame);

    if (par_count > 600) {
        Entity victim = ecs_entity_at(ctx, reg, seed % par_count);
        ecs_destroy(ctx, victim);
        ecs_log(ctx, "pruned node (slot %u gen %u); orphans re-root next rebuild",
                victim.slot, victim.gen);
    }

    Entity parent = ecs_entity_at(ctx, reg, hash_u32(seed) % par_count);
    Entity e = ecs_create(ctx, Node);
    ecs_init(ctx, e, Parent, { parent });
    ecs_init(ctx, e, LocalTransform, { (frand(seed) - 0.5f) * 30.0f, -30.0f,
                                       (frand(seed ^ 5u) - 0.5f), 0.8f });
}

/* Full re-derivation check of every parented node, plus a FIRST demo. */
TASK(VerifyHierarchy, QUERY(WorldMatrix, wm), QUERY(LocalTransform, lt),
     READ(HierarchyData, h), READ(FrameStats, fs), FIRST(FoodInfo, anyfood))
{
    if (fs->frame % 180 != 60 || h->count != wm_count) {
        return;
    }

    uint32_t ok = 0, bad = 0;

    for (uint32_t i = 0; i < h->count; ++i) {
        uint32_t pd = h->parent_dense[i];
        if (pd == NO_DENSE) {
            continue;
        }
        WorldMatrix local = xf_compose(&lt[i]);
        WorldMatrix expect = xf_mul(&wm[pd], &local);
        float err = fabsf(expect.tx - wm[i].tx) + fabsf(expect.ty - wm[i].ty) +
                    fabsf(expect.a - wm[i].a);
        if (err < 0.01f) ok++; else bad++;
    }

    ecs_log(ctx, "hierarchy verify: %u ok, %u bad; FIRST(FoodInfo) amount %.1f",
            ok, bad, anyfood ? anyfood->amount : -1.0f);
}

/* ------------------------------------------------------------------ */
/* Setup tasks                                                         */
/* ------------------------------------------------------------------ */

TASK(PrintStartup, WRITE(FrameStats, s))
{
    const char *max = getenv("FABLEC_MAX_FRAMES");
    s->max_frames = max ? (uint32_t)atoi(max) : 0;

    printf("[game] zombies & food + hierarchy demo 窶・%d zombies, %d food, %d trees depth %d\n",
           START_ZOMBIES, FOOD_COUNT, TREE_COUNT, TREE_DEPTH);
    printf("[game] max frames: %u%s\n",
           s->max_frames, s->max_frames ? "" : " (run until window closes)");
}

TASK(SpawnWorld, WRITE(FrameStats, s))
{
    for (uint32_t i = 0; i < START_ZOMBIES; ++i) {
        Entity e = ecs_create(ctx, Zombie);
        ecs_init(ctx, e, Position, { frand(i * 3u + 1u) * WORLD_W,
                                     frand(i * 3u + 2u) * WORLD_H });
        ecs_init(ctx, e, Velocity, { (frand(i * 3u + 3u) - 0.5f) * 80.0f,
                                     (frand(i * 3u + 4u) - 0.5f) * 80.0f });
        ecs_init(ctx, e, Hunger, { .energy = 90.0f + frand(i * 3u + 5u) * 40.0f });
    }

    for (uint32_t i = 0; i < FOOD_COUNT; ++i) {
        Entity e = ecs_create(ctx, Food);
        ecs_init(ctx, e, FoodInfo, {
            60.0f + (float)(i % 4) * 280.0f + frand(i * 7u + 1u) * 60.0f,
            70.0f + (float)(i / 4) * 190.0f + frand(i * 7u + 2u) * 50.0f,
            0.0f
        });
    }

    s->spawned = START_ZOMBIES + FOOD_COUNT;
    ecs_log(ctx, "spawned %u entities", s->spawned);
}

static void build_tree(SysCtx *ctx, Entity parent, int depth, uint32_t seed)
{
    if (depth <= 0) {
        return;
    }

    for (int k = 0; k < 2; ++k) {
        Entity e = ecs_create(ctx, Node);
        ecs_init(ctx, e, Parent, { parent });
        ecs_init(ctx, e, LocalTransform, { k ? 12.0f : -12.0f, -34.0f,
                                           k ? 0.35f : -0.35f, 0.86f });
        build_tree(ctx, e, depth - 1, hash_u32(seed * 2u + (uint32_t)k));
    }
}

TASK(SpawnHierarchy, WRITE(FrameStats, s))
{
    for (uint32_t i = 0; i < TREE_COUNT; ++i) {
        Entity root = ecs_create(ctx, Node);
        ecs_init(ctx, root, Parent, { ECS_NULL_ENTITY });
        ecs_init(ctx, root, LocalTransform, { 90.0f + (float)i * 155.0f, 480.0f, 0.0f, 1.0f });
        build_tree(ctx, root, TREE_DEPTH, i * 101u + 7u);
    }

    uint32_t nodes = TREE_COUNT * ((1u << (TREE_DEPTH + 1)) - 1u);
    s->spawned += nodes;
    ecs_log(ctx, "spawned %u hierarchy nodes in %d trees", nodes, TREE_COUNT);
}

/* ------------------------------------------------------------------ */
/* Window / frame tasks                                                */
/* ------------------------------------------------------------------ */

static LRESULT CALLBACK window_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg) {
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hwnd, msg, wp, lp);
}

TASK_MAIN(CreateWindowTask, WRITE(WindowState, w), WRITE(CanvasState, c))
{
    WNDCLASSW wc = { 0 };
    wc.lpfnWndProc = window_proc;
    wc.hInstance = GetModuleHandleW(NULL);
    wc.lpszClassName = L"FableCDemo";
    wc.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
    RegisterClassW(&wc);

    RECT r = { 0, 0, WORLD_W, WORLD_H };
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW & ~(WS_THICKFRAME | WS_MAXIMIZEBOX), FALSE);

    w->hwnd = CreateWindowExW(
        0, L"FableCDemo", L"FableC 窶・zombies, food & hierarchies",
        WS_OVERLAPPEDWINDOW & ~(WS_THICKFRAME | WS_MAXIMIZEBOX),
        CW_USEDEFAULT, CW_USEDEFAULT,
        r.right - r.left, r.bottom - r.top,
        NULL, NULL, wc.hInstance, NULL);

    ShowWindow(w->hwnd, SW_SHOW);

    BITMAPINFO bi = { 0 };
    bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
    bi.bmiHeader.biWidth = WORLD_W;
    bi.bmiHeader.biHeight = -WORLD_H;   /* top-down */
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    HDC screen = GetDC(w->hwnd);
    c->mem_dc = CreateCompatibleDC(screen);
    c->dib = CreateDIBSection(c->mem_dc, &bi, DIB_RGB_COLORS, (void **)&c->pixels, NULL, 0);
    SelectObject(c->mem_dc, c->dib);
    ReleaseDC(w->hwnd, screen);

    c->w = WORLD_W;
    c->h = WORLD_H;
}

TASK_MAIN(PumpMessages, WRITE(WindowState, w))
{
    MSG msg;

    while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE)) {
        if (msg.message == WM_QUIT) {
            w->quit = 1;
            return;
        }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

TASK(BeginFrame, WRITE(CanvasState, c))
{
    if (!c->pixels) {
        return;
    }

    uint32_t n = (uint32_t)(c->w * c->h);
    for (uint32_t i = 0; i < n; ++i) {
        c->pixels[i] = 0x101018u;
    }
}

static void plot_draw(void *user, int x, int y, uint32_t rgb)
{
    CanvasState *c = (CanvasState *)user;

    for (int dy = 0; dy < 2; ++dy) {
        for (int dx = 0; dx < 2; ++dx) {
            int px = x + dx, py = y + dy;
            if (px >= 0 && px < c->w && py >= 0 && py < c->h) {
                c->pixels[py * c->w + px] = rgb;
            }
        }
    }
}

TASK(EndFrame, WRITE(CanvasState, c), READ(WindowState, w))
{
    if (!c->pixels || !w->hwnd) {
        return;
    }

    ecs_draw_drain(ctx, plot_draw, c);

    HDC dc = GetDC(w->hwnd);
    BitBlt(dc, 0, 0, c->w, c->h, c->mem_dc, 0, 0, SRCCOPY);
    ReleaseDC(w->hwnd, dc);
}

static void print_log_line(void *user, uint32_t worker, const char *line)
{
    LogSink *sink = (LogSink *)user;
    sink->total++;
    printf("[log w%u] %s\n", worker, line);
}

TASK(FlushLog, WRITE(LogSink, l))
{
    ecs_log_drain(ctx, print_log_line, l);
}

TASK(FinishFrame, WRITE(FrameStats, s), READ(WindowState, w),
     QUERY(Hunger, zombies), QUERY(WorldMatrix, nodes))
{
    s->frame++;
    s->total_ms += ctx->dt * 1000.0;

    if (s->frame % 120 == 0) {
        printf("[game] frame %u, zombies %u, nodes %u, avg frame %.2f ms\n",
               s->frame, zombies_count, nodes_count, s->total_ms / s->frame);
    }

    if (w->quit || (s->max_frames && s->frame >= s->max_frames)) {
        printf("[game] done after %u frames, avg %.2f ms\n",
               s->frame, s->total_ms / s->frame);
        ecs_quit(ctx);
        return;
    }

    if (!s->max_frames) {
        Sleep(4);   /* crude pacing when running interactively */
    }

    (void)zombies;
    (void)nodes;
}

/* ------------------------------------------------------------------ */
/* Schedule                                                            */
/* ------------------------------------------------------------------ */

SCHEDULE(
    RUN(PrintStartup),
    RUN(CreateWindowTask),
    RUN(SpawnWorld),
    RUN(SpawnHierarchy),
    LOOP(
        RUN(PumpMessages),
        RUN(Seek, Move, Digest, Regrow, Spin, Sway),  /* masks decide the overlap */
        RUN(PruneAndGrow),
        RUN(RebuildHierarchy),
        RUN(ComputeWorldMatrices),
        RUN(BeginFrame),
        RUN(Render),          /* Zombie.Render + Food.Render + Node.Render in parallel */
        RUN(EndFrame),
        RUN(VerifyHierarchy),
        RUN(FlushLog),
        RUN(FinishFrame)
    )
);

ECS_MODULE()
