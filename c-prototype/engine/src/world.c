#include "world.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <assert.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* ------------------------------------------------------------------ */
/* Command buffer records (8-byte aligned)                             */
/* ------------------------------------------------------------------ */

enum { CMD_CREATE = 1, CMD_DESTROY = 2 };

typedef struct CmdCreate {
    uint64_t       op;
    EcsPrefabDesc *prefab;
    uint32_t       slot;            /* reserved at record time */
    uint32_t       prev_create;     /* offset of previous CmdCreate, CMD_NO_LINK */
    /* component blob follows, laid out by prefab_blob_offset() */
} CmdCreate;

typedef struct CmdDestroy {
    uint64_t op;
    uint32_t reg;
    uint32_t slot;
    uint32_t gen;
    uint32_t pad;
} CmdDestroy;

static uint32_t align8(uint32_t v)
{
    return (v + 7u) & ~7u;
}

/* Offset of component `ci` inside a create blob; returns total size if ci == count. */
static uint32_t prefab_blob_offset(const EcsPrefabDesc *p, uint32_t ci)
{
    uint32_t off = 0;

    for (uint32_t i = 0; i < p->component_count; ++i) {
        if (i == ci) {
            return off;
        }
        off += align8(p->components[i]->size);
    }

    return off;
}

static uint32_t prefab_blob_size(const EcsPrefabDesc *p)
{
    return prefab_blob_offset(p, p->component_count);
}

/* ------------------------------------------------------------------ */
/* Register storage + slots                                            */
/* ------------------------------------------------------------------ */

static void register_reserve(Register *r, uint32_t needed)
{
    if (needed <= r->capacity) {
        return;
    }

    uint32_t cap = r->capacity ? r->capacity : 1024;
    while (cap < needed) {
        cap *= 2;
    }

    for (uint32_t i = 0; i < r->prefab->component_count; ++i) {
        r->cols[i] = realloc(r->cols[i], (size_t)cap * r->prefab->components[i]->size);
    }
    r->slot_of_dense = realloc(r->slot_of_dense, (size_t)cap * sizeof(uint32_t));

    r->capacity = cap;
}

/*
    Slot allocation with an explicit watermark: slots [0, watermark) have
    been handed out at least once; freed ones sit in the free stack and
    keep their bumped generation for reuse.
*/
static uint32_t reg_watermark[MAX_REGISTERS];   /* indexed by prefab id */

static void register_slots_reserve(Register *r, uint32_t needed)
{
    if (needed <= r->slot_cap) {
        return;
    }

    uint32_t cap = r->slot_cap ? r->slot_cap : 1024;
    while (cap < needed) {
        cap *= 2;
    }

    r->slots = realloc(r->slots, (size_t)cap * sizeof(EcsSlot));
    r->free_slots = realloc(r->free_slots, (size_t)cap * sizeof(uint32_t));
    memset(r->slots + r->slot_cap, 0, (size_t)(cap - r->slot_cap) * sizeof(EcsSlot));
    r->slot_cap = cap;
}

static uint32_t slot_alloc(Register *r)
{
    if (r->free_count > 0) {
        return r->free_slots[--r->free_count];
    }

    uint32_t s = reg_watermark[r->prefab->id]++;
    register_slots_reserve(r, s + 1);

    if (r->slots[s].gen == 0) {
        r->slots[s].gen = 1;
    }
    return s;
}

static void register_push_row(Register *r, const uint8_t *blob, uint32_t slot)
{
    register_reserve(r, r->count + 1);

    for (uint32_t i = 0; i < r->prefab->component_count; ++i) {
        uint32_t size = r->prefab->components[i]->size;
        memcpy((uint8_t *)r->cols[i] + (size_t)r->count * size,
               blob + prefab_blob_offset(r->prefab, i),
               size);
    }

    r->slots[slot].dense = r->count;
    r->slot_of_dense[r->count] = slot;
    r->count++;
}

static void register_swap_remove(Register *r, uint32_t index)
{
    assert(index < r->count);

    uint32_t dead_slot = r->slot_of_dense[index];
    r->slots[dead_slot].gen++;                    /* invalidate all handles */
    r->slots[dead_slot].dense = DENSE_PENDING;
    r->free_slots[r->free_count++] = dead_slot;

    r->count--;

    if (index != r->count) {
        for (uint32_t i = 0; i < r->prefab->component_count; ++i) {
            uint32_t size = r->prefab->components[i]->size;
            memcpy((uint8_t *)r->cols[i] + (size_t)index * size,
                   (uint8_t *)r->cols[i] + (size_t)r->count * size,
                   size);
        }

        uint32_t moved_slot = r->slot_of_dense[r->count];
        r->slots[moved_slot].dense = index;
        r->slot_of_dense[index] = moved_slot;
    }
}

/* ------------------------------------------------------------------ */
/* EcsApi implementation                                               */
/* ------------------------------------------------------------------ */

static void *api_sys_col(SysCtx *ctx, const EcsComponentDesc *comp)
{
    World *w = (World *)ctx->world;

    if (comp->is_singleton) {
        return w->singletons[comp->id];
    }

    const EcsPrefabDesc *p = ctx->system->prefab_desc;
    assert(p && "READ/WRITE of a non-singleton component from a TASK");

    Register *r = &w->registers[p->id];
    for (uint32_t i = 0; i < p->component_count; ++i) {
        if (p->components[i] == comp) {
            return r->cols[i];
        }
    }

    assert(0 && "component not part of the system's prefab");
    return NULL;
}

static const void *api_query_col(SysCtx *ctx, const EcsComponentDesc *comp, uint32_t *count)
{
    World *w = (World *)ctx->world;

    if (comp->is_singleton) {
        *count = 1;
        return w->singletons[comp->id];
    }

    for (uint32_t ri = 0; ri < w->register_count; ++ri) {
        Register *r = &w->registers[ri];
        for (uint32_t i = 0; i < r->prefab->component_count; ++i) {
            if (r->prefab->components[i] == comp) {
                *count = r->count;
                return r->cols[i];
            }
        }
    }

    assert(0 && "QUERY component not found in any register");
    *count = 0;
    return NULL;
}

static void *api_lookup(SysCtx *ctx, const EcsComponentDesc *comp, Entity e)
{
    World *w = (World *)ctx->world;

    if (e.reg >= w->register_count) {
        return NULL;                            /* null or corrupt handle */
    }

    Register *r = &w->registers[e.reg];
    if (e.slot >= r->slot_cap || r->slots[e.slot].gen != e.gen) {
        return NULL;                            /* stale handle */
    }

    uint32_t dense = r->slots[e.slot].dense;
    if (dense == DENSE_PENDING) {
        return NULL;                            /* created this frame */
    }

    const EcsPrefabDesc *p = r->prefab;
    for (uint32_t i = 0; i < p->component_count; ++i) {
        if (p->components[i] == comp) {
            return (uint8_t *)r->cols[i] + (size_t)dense * comp->size;
        }
    }

    return NULL;                                /* entity has no such component */
}

static Entity api_entity_at(SysCtx *ctx, uint32_t reg, uint32_t dense)
{
    World *w = (World *)ctx->world;
    Entity e = ECS_NULL_ENTITY;

    if (reg >= w->register_count || dense >= w->registers[reg].count) {
        return e;
    }

    Register *r = &w->registers[reg];
    e.reg = reg;
    e.slot = r->slot_of_dense[dense];
    e.gen = r->slots[e.slot].gen;
    return e;
}

static int32_t api_entity_index(SysCtx *ctx, Entity e)
{
    World *w = (World *)ctx->world;

    if (e.reg >= w->register_count) {
        return -1;
    }

    Register *r = &w->registers[e.reg];
    if (e.slot >= r->slot_cap || r->slots[e.slot].gen != e.gen) {
        return -1;
    }

    uint32_t dense = r->slots[e.slot].dense;
    return dense == DENSE_PENDING ? -1 : (int32_t)dense;
}

static uint32_t api_register_of(SysCtx *ctx, const EcsComponentDesc *comp)
{
    World *w = (World *)ctx->world;

    for (uint32_t ri = 0; ri < w->register_count; ++ri) {
        Register *r = &w->registers[ri];
        for (uint32_t i = 0; i < r->prefab->component_count; ++i) {
            if (r->prefab->components[i] == comp) {
                return ri;
            }
        }
    }

    return ECS_NO_REG;
}

static uint8_t *cmdbuf_push(CmdBuf *b, uint32_t bytes)
{
    if (b->size + bytes > b->cap) {
        uint32_t cap = b->cap;
        while (b->size + bytes > cap) {
            cap *= 2;
        }
        b->data = realloc(b->data, cap);
        b->cap = cap;
    }

    uint8_t *at = b->data + b->size;
    b->size += bytes;
    return at;
}

static Entity api_create(SysCtx *ctx, EcsPrefabDesc *prefab)
{
    World  *w = (World *)ctx->world;
    CmdBuf *b = &w->cmd[ctx->worker];

    uint32_t blob_size = prefab_blob_size(prefab);
    uint32_t rec_off = b->size;
    uint8_t *at = cmdbuf_push(b, (uint32_t)sizeof(CmdCreate) + align8(blob_size));

    if (!at) {
        return ECS_NULL_ENTITY;
    }

    /* Reserve a real slot immediately so the returned handle is stable.
       The slot's dense stays DENSE_PENDING until the barrier, so lookups
       this frame return NULL (a created entity is unreadable until then). */
    Register *r = &w->registers[prefab->id];
    AcquireSRWLockExclusive((SRWLOCK *)w->create_lock);
    uint32_t slot = slot_alloc(r);
    r->slots[slot].dense = DENSE_PENDING;
    ReleaseSRWLockExclusive((SRWLOCK *)w->create_lock);

    CmdCreate *c = (CmdCreate *)at;
    c->op = CMD_CREATE;
    c->prefab = prefab;
    c->slot = slot;
    c->prev_create = b->last_create;
    b->last_create = rec_off;
    memset(at + sizeof(CmdCreate), 0, blob_size);

    Entity e;
    e.reg = prefab->id;
    e.slot = slot;
    e.gen = r->slots[slot].gen;
    return e;
}

static void api_destroy(SysCtx *ctx, Entity e)
{
    World *w = (World *)ctx->world;

    if (ecs_entity_is_null(e)) {
        return;
    }

    CmdDestroy *d = (CmdDestroy *)cmdbuf_push(&w->cmd[ctx->worker], sizeof(CmdDestroy));
    if (!d) {
        return;
    }

    d->op = CMD_DESTROY;
    d->reg = e.reg;
    d->slot = e.slot;
    d->gen = e.gen;
    d->pad = 0;
}

static void api_init(SysCtx *ctx, Entity e, const EcsComponentDesc *comp, const void *data)
{
    World  *w = (World *)ctx->world;
    CmdBuf *b = &w->cmd[ctx->worker];

    /* walk this worker's create chain (init follows create, so it's the head) */
    uint32_t off = b->last_create;
    while (off != CMD_NO_LINK) {
        CmdCreate *c = (CmdCreate *)(b->data + off);
        if (c->slot == e.slot && c->prefab->id == e.reg) {
            const EcsPrefabDesc *p = c->prefab;
            for (uint32_t i = 0; i < p->component_count; ++i) {
                if (p->components[i] == comp) {
                    memcpy(b->data + off + sizeof(CmdCreate) + prefab_blob_offset(p, i),
                           data, comp->size);
                    return;
                }
            }
            assert(0 && "ecs_init component not part of the created prefab");
            return;
        }
        off = c->prev_create;
    }

    assert(0 && "ecs_init: entity was not created this frame on this worker");
}

static void api_logf(SysCtx *ctx, const char *fmt, ...)
{
    World  *w = (World *)ctx->world;
    LogBuf *b = &w->logs[ctx->worker];

    if (b->count >= LOG_LINES_CAP) {
        return;
    }

    va_list args;
    va_start(args, fmt);
    vsnprintf(b->lines[b->count], LOG_LINE_MAX, fmt, args);
    va_end(args);
    b->count++;
}

static uint32_t api_log_drain(SysCtx *ctx, EcsLogSinkFn fn, void *user)
{
    World   *w = (World *)ctx->world;
    uint32_t total = 0;

    for (uint32_t wi = 0; wi < w->worker_count; ++wi) {
        LogBuf *b = &w->logs[wi];
        for (uint32_t i = 0; i < b->count; ++i) {
            fn(user, wi, b->lines[i]);
        }
        total += b->count;
        b->count = 0;
    }

    return total;
}

static void api_draw(SysCtx *ctx, int x, int y, uint32_t rgb)
{
    World   *w = (World *)ctx->world;
    DrawBuf *b = &w->draws[ctx->worker];

    if (b->count >= DRAW_CMDS_CAP) {
        return;
    }

    DrawCmd *c = &b->cmds[b->count++];
    c->x = (int16_t)x;
    c->y = (int16_t)y;
    c->rgb = rgb;
}

static uint32_t api_draw_drain(SysCtx *ctx, EcsDrawSinkFn fn, void *user)
{
    World   *w = (World *)ctx->world;
    uint32_t total = 0;

    for (uint32_t wi = 0; wi < w->worker_count; ++wi) {
        DrawBuf *b = &w->draws[wi];
        for (uint32_t i = 0; i < b->count; ++i) {
            fn(user, b->cmds[i].x, b->cmds[i].y, b->cmds[i].rgb);
        }
        total += b->count;
        b->count = 0;
    }

    return total;
}

static void api_quit(SysCtx *ctx)
{
    World *w = (World *)ctx->world;
    InterlockedExchange(&w->quit, 1);
}

/* ------------------------------------------------------------------ */
/* Command buffer application (at barriers)                            */
/* ------------------------------------------------------------------ */

void world_apply_commands(World *w)
{
    /* Pass 1: creates (they append rows and bind reserved slots). */
    for (uint32_t wi = 0; wi < w->worker_count; ++wi) {
        CmdBuf  *b = &w->cmd[wi];
        uint32_t at = 0;

        while (at < b->size) {
            uint64_t op = *(uint64_t *)(b->data + at);

            if (op == CMD_CREATE) {
                CmdCreate *c = (CmdCreate *)(b->data + at);
                register_push_row(&w->registers[c->prefab->id],
                                  b->data + at + sizeof(CmdCreate), c->slot);
                at += (uint32_t)sizeof(CmdCreate) + align8(prefab_blob_size(c->prefab));
            }
            else if (op == CMD_DESTROY) {
                at += sizeof(CmdDestroy);
            }
            else {
                fprintf(stderr, "[ecs] corrupt command buffer (op %llu)\n",
                        (unsigned long long)op);
                break;
            }
        }
    }

    /* Pass 2: destroys. Handles resolve through the slot table, so any
       order works and duplicates fail the generation check harmlessly. */
    for (uint32_t wi = 0; wi < w->worker_count; ++wi) {
        CmdBuf  *b = &w->cmd[wi];
        uint32_t at = 0;

        while (at < b->size) {
            uint64_t op = *(uint64_t *)(b->data + at);

            if (op == CMD_CREATE) {
                CmdCreate *c = (CmdCreate *)(b->data + at);
                at += (uint32_t)sizeof(CmdCreate) + align8(prefab_blob_size(c->prefab));
            }
            else if (op == CMD_DESTROY) {
                CmdDestroy *d = (CmdDestroy *)(b->data + at);
                Register   *r = &w->registers[d->reg];

                if (d->reg < w->register_count &&
                    d->slot < r->slot_cap &&
                    r->slots[d->slot].gen == d->gen &&
                    r->slots[d->slot].dense != DENSE_PENDING) {
                    register_swap_remove(r, r->slots[d->slot].dense);
                }
                at += sizeof(CmdDestroy);
            }
            else {
                break;
            }
        }

        b->size = 0;
        b->last_create = CMD_NO_LINK;
    }
}

/* ------------------------------------------------------------------ */
/* World init / shutdown                                               */
/* ------------------------------------------------------------------ */

static const char *access_mode_name(EcsAccessMode m)
{
    switch (m) {
    case ECS_ACCESS_QUERY_MUT:  return "QUERY_MUT";
    case ECS_ACCESS_FIRST_MUT:  return "FIRST_MUT";
    case ECS_ACCESS_LOOKUP_MUT: return "LOOKUP_MUT";
    default:                    return "?";
    }
}

static int access_is_write(EcsAccessMode m)
{
    return m == ECS_ACCESS_WRITE || m == ECS_ACCESS_QUERY_MUT ||
           m == ECS_ACCESS_FIRST_MUT || m == ECS_ACCESS_LOOKUP_MUT;
}

static int access_is_cross_entity_mut(EcsAccessMode m)
{
    return m == ECS_ACCESS_QUERY_MUT || m == ECS_ACCESS_FIRST_MUT ||
           m == ECS_ACCESS_LOOKUP_MUT;
}

int world_init(World *w, const EcsModuleDesc *module, uint32_t worker_count)
{
    memset(w, 0, sizeof(*w));
    memset(reg_watermark, 0, sizeof(reg_watermark));
    w->module = module;
    w->worker_count = worker_count;

    static SRWLOCK create_lock;
    InitializeSRWLock(&create_lock);
    w->create_lock = &create_lock;

    if (module->component_count > MAX_COMPONENTS ||
        module->prefab_count > MAX_REGISTERS) {
        fprintf(stderr, "[ecs] module exceeds host limits\n");
        return 0;
    }

    /* Assign component ids (= bit index in access masks). */
    for (uint32_t i = 0; i < module->component_count; ++i) {
        EcsComponentDesc *c = module->components[i];
        c->id = i;
        w->components[i] = c;

        if (c->is_singleton) {
            w->singletons[i] = calloc(1, c->size);
        }
    }
    w->component_count = module->component_count;

    /* One register per prefab. */
    for (uint32_t i = 0; i < module->prefab_count; ++i) {
        EcsPrefabDesc *p = module->prefabs[i];
        p->id = i;
        w->registers[i].prefab = p;
        register_reserve(&w->registers[i], 4096);
        register_slots_reserve(&w->registers[i], 4096);
    }
    w->register_count = module->prefab_count;

    /* Resolve system prefabs, build masks, validate capabilities. */
    for (uint32_t i = 0; i < module->system_count; ++i) {
        EcsSystemDesc *s = module->systems[i];

        s->prefab_desc = NULL;
        if (s->prefab) {
            for (uint32_t pi = 0; pi < module->prefab_count; ++pi) {
                if (strcmp(module->prefabs[pi]->name, s->prefab) == 0) {
                    s->prefab_desc = module->prefabs[pi];
                    break;
                }
            }
            if (!s->prefab_desc) {
                fprintf(stderr, "[ecs] system %s.%s: unknown prefab\n", s->prefab, s->phase);
                return 0;
            }
        }

        s->read_mask = 0;
        s->write_mask = 0;
        for (uint32_t a = 0; a < s->access_count; ++a) {
            EcsAccessMode mode = s->accesses[a].mode;
            uint64_t bit = 1ull << s->accesses[a].component->id;

            /* chunked SYSTEMs may not mutate outside their own row */
            if (s->prefab && access_is_cross_entity_mut(mode)) {
                fprintf(stderr,
                        "[ecs] %s.%s: %s(%s) is only legal in a TASK — "
                        "chunks of one system would race with each other\n",
                        s->prefab, s->phase, access_mode_name(mode),
                        s->accesses[a].component->name);
                return 0;
            }

            /* a singleton written from parallel chunks is the same race */
            if (s->prefab && mode == ECS_ACCESS_WRITE &&
                s->accesses[a].component->is_singleton) {
                fprintf(stderr,
                        "[ecs] %s.%s: WRITE(%s) on a singleton is only legal "
                        "in a TASK — all chunks would write one instance\n",
                        s->prefab, s->phase, s->accesses[a].component->name);
                return 0;
            }

            if (access_is_write(mode)) {
                s->write_mask |= bit;
            }
            else {
                s->read_mask |= bit;
            }
        }
    }

    /* Per-worker buffers. */
    for (uint32_t i = 0; i < worker_count; ++i) {
        w->cmd[i].data = malloc(CMDBUF_INITIAL);
        w->cmd[i].cap = CMDBUF_INITIAL;
        w->cmd[i].last_create = CMD_NO_LINK;
        w->logs[i].lines = malloc(sizeof(char[LOG_LINES_CAP][LOG_LINE_MAX]));
        w->draws[i].cmds = malloc(sizeof(DrawCmd) * DRAW_CMDS_CAP);
    }

    w->api.sys_col = api_sys_col;
    w->api.query_col = api_query_col;
    w->api.lookup = api_lookup;
    w->api.entity_at = api_entity_at;
    w->api.entity_index = api_entity_index;
    w->api.register_of = api_register_of;
    w->api.create = api_create;
    w->api.destroy = api_destroy;
    w->api.init = api_init;
    w->api.logf = api_logf;
    w->api.log_drain = api_log_drain;
    w->api.draw = api_draw;
    w->api.draw_drain = api_draw_drain;
    w->api.quit = api_quit;
    /* w->api.parallel_for is wired by scheduler_create */
    return 1;
}

void world_shutdown(World *w)
{
    for (uint32_t i = 0; i < w->register_count; ++i) {
        Register *r = &w->registers[i];
        for (uint32_t c = 0; c < r->prefab->component_count; ++c) {
            free(r->cols[c]);
        }
        free(r->slots);
        free(r->slot_of_dense);
        free(r->free_slots);
    }
    for (uint32_t i = 0; i < w->component_count; ++i) {
        free(w->singletons[i]);
    }
    for (uint32_t i = 0; i < w->worker_count; ++i) {
        free(w->cmd[i].data);
        free(w->logs[i].lines);
        free(w->draws[i].cmds);
    }
}
