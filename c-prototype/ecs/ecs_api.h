/*
    ecs_api.h — the ABI shared between the host engine and game DLLs.

    Everything the host and a game module exchange is described here:
    descriptors (components, prefabs, systems, schedule), the per-job
    SysCtx, and the EcsApi function table through which game code talks
    back to the host (deferred create/destroy, lookups, log/draw buffers,
    nested parallel-for, quit).

    Game code should include ecs_meta.h instead, which includes this and
    adds the declaration macros.
*/
#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct EcsComponentDesc EcsComponentDesc;
typedef struct EcsPrefabDesc    EcsPrefabDesc;
typedef struct EcsSystemDesc    EcsSystemDesc;
typedef struct EcsScheduleDesc  EcsScheduleDesc;
typedef struct EcsModuleDesc    EcsModuleDesc;
typedef struct EcsApi           EcsApi;
typedef struct SysCtx           SysCtx;

typedef enum EcsDescKind {
    ECS_DESC_COMPONENT = 1,
    ECS_DESC_PREFAB    = 2,
    ECS_DESC_SYSTEM    = 3,
    ECS_DESC_SCHEDULE  = 4
} EcsDescKind;

/* ------------------------------------------------------------------ */
/* Components and prefabs                                              */
/* ------------------------------------------------------------------ */

struct EcsComponentDesc {
    EcsDescKind kind;
    const char *name;
    uint32_t    size;
    int         is_singleton;
    uint32_t    id;             /* bit index in access masks; host-assigned at load */
};

#define ECS_MAX_PREFAB_COMPONENTS 8

struct EcsPrefabDesc {
    EcsDescKind       kind;
    const char       *name;
    EcsComponentDesc *components[ECS_MAX_PREFAB_COMPONENTS];
    uint32_t          component_count;
    uint32_t          id;       /* register index; host-assigned at load */
};

/* ------------------------------------------------------------------ */
/* Systems                                                             */
/* ------------------------------------------------------------------ */

/*
    Cross-entity capability rules (validated by the host at load):
    the masks protect systems from each other, but nothing protects a
    chunked SYSTEM from itself — so the mutable cross-entity modes
    (QUERY_MUT / FIRST_MUT / LOOKUP_MUT) are only legal in TASKs,
    which run as a single job.
*/
typedef enum EcsAccessMode {
    ECS_ACCESS_READ       = 0,  /* own entity's row / singleton            */
    ECS_ACCESS_WRITE      = 1,
    ECS_ACCESS_QUERY      = 2,  /* whole register column, const            */
    ECS_ACCESS_QUERY_MUT  = 3,  /* whole register column, mutable (TASK)   */
    ECS_ACCESS_FIRST      = 4,  /* one row, const, NULL when none          */
    ECS_ACCESS_FIRST_MUT  = 5,  /* TASK only                               */
    ECS_ACCESS_LOOKUP     = 6,  /* random access by Entity handle, const   */
    ECS_ACCESS_LOOKUP_MUT = 7   /* TASK only                               */
} EcsAccessMode;

typedef struct EcsAccess {
    EcsComponentDesc *component;
    EcsAccessMode     mode;
} EcsAccess;

typedef void (*EcsRunFn)(SysCtx *ctx);

#define ECS_MAX_SYSTEM_ACCESSES 8

struct EcsSystemDesc {
    EcsDescKind kind;
    const char *prefab;         /* NULL for a TASK */
    const char *phase;          /* phase name for SYSTEM, task name for TASK */
    EcsRunFn    run;
    EcsAccess   accesses[ECS_MAX_SYSTEM_ACCESSES];
    uint32_t    access_count;
    int         main_thread;    /* pin to the main thread (TASK_MAIN) */

    /* host-filled at load: */
    uint64_t             read_mask;
    uint64_t             write_mask;
    const EcsPrefabDesc *prefab_desc;   /* resolved prefab, NULL for tasks */
};

/* ------------------------------------------------------------------ */
/* Schedule                                                            */
/* ------------------------------------------------------------------ */

typedef enum EcsStepKind {
    ECS_STEP_RUN        = 1,
    ECS_STEP_LOOP_BEGIN = 2,
    ECS_STEP_LOOP_END   = 3
} EcsStepKind;

#define ECS_MAX_STEP_NAMES 8

typedef struct EcsScheduleStep {
    EcsStepKind kind;
    uint32_t    name_count;
    const char *names[ECS_MAX_STEP_NAMES];
} EcsScheduleStep;

struct EcsScheduleDesc {
    EcsDescKind            kind;
    const EcsScheduleStep *steps;
    uint32_t               step_count;
};

/* ------------------------------------------------------------------ */
/* Module                                                              */
/* ------------------------------------------------------------------ */

struct EcsModuleDesc {
    EcsComponentDesc     **components;
    uint32_t               component_count;
    EcsPrefabDesc        **prefabs;
    uint32_t               prefab_count;
    EcsSystemDesc        **systems;
    uint32_t               system_count;
    const EcsScheduleDesc *schedule;
};

/* The single symbol every game DLL exports. */
typedef const EcsModuleDesc *(*EcsModuleFn)(void);
#define ECS_MODULE_ENTRY_NAME "EcsModule"

/* ------------------------------------------------------------------ */
/* Entities — generational handles                                     */
/* ------------------------------------------------------------------ */

/*
    A handle stays valid across frames and structural changes; after the
    entity is destroyed (or its slot reused), lookups return NULL / -1.
    ecs_create returns a real handle immediately, but the entity's data
    only materializes at the next barrier: same-frame lookups return
    NULL, ecs_init patches the staged values.
*/
#define ECS_NO_REG 0xffffffffu

typedef struct Entity {
    uint32_t reg;       /* register index, or ECS_NO_REG for the null entity */
    uint32_t slot;      /* stable slot within the register */
    uint32_t gen;       /* generation; mismatch = stale handle */
} Entity;

#define ECS_NULL_ENTITY      ((Entity){ ECS_NO_REG, 0, 0 })
#define ecs_entity_is_null(e) ((e).reg == ECS_NO_REG)

/* Bound by a LOOKUP/LOOKUP_MUT clause; use through ecs_lookup(). */
typedef struct EcsLookup {
    SysCtx                 *ctx;
    const EcsComponentDesc *comp;
} EcsLookup;

/* ------------------------------------------------------------------ */
/* SysCtx — handed to every system/task job                            */
/* ------------------------------------------------------------------ */

struct SysCtx {
    const EcsApi        *api;
    void                *world;     /* opaque host World* */
    const EcsSystemDesc *system;
    uint32_t             first;     /* chunk range [first, last) */
    uint32_t             last;
    uint32_t             count;     /* total entities in the system's register */
    uint32_t             worker;    /* executing worker index (0 = main thread) */
    float                dt;
    double               time;
    Entity               self;      /* current entity inside a SYSTEM body */
};

/* Nested parallel-for callback: sub is a copy of the caller's ctx with
   first/last/worker rebound to the executing chunk. */
typedef void (*EcsForFn)(SysCtx *sub, void *user);

/* ------------------------------------------------------------------ */
/* Host API function table                                             */
/* ------------------------------------------------------------------ */

typedef void (*EcsLogSinkFn)(void *user, uint32_t worker, const char *line);
typedef void (*EcsDrawSinkFn)(void *user, int x, int y, uint32_t rgb);

struct EcsApi {
    /* column of one component for the system's own register (or a singleton) */
    void       *(*sys_col)(SysCtx *ctx, const EcsComponentDesc *comp);
    /* read-only column of the whole register that owns this component */
    const void *(*query_col)(SysCtx *ctx, const EcsComponentDesc *comp, uint32_t *count);

    /* handle plumbing */
    void       *(*lookup)(SysCtx *ctx, const EcsComponentDesc *comp, Entity e);
    Entity      (*entity_at)(SysCtx *ctx, uint32_t reg, uint32_t dense);
    int32_t     (*entity_index)(SysCtx *ctx, Entity e);   /* dense index or -1 */
    uint32_t    (*register_of)(SysCtx *ctx, const EcsComponentDesc *comp);

    /* deferred structural changes — recorded per worker, applied at barriers */
    Entity      (*create)(SysCtx *ctx, EcsPrefabDesc *prefab);
    void        (*destroy)(SysCtx *ctx, Entity e);
    void        (*init)(SysCtx *ctx, Entity e, const EcsComponentDesc *comp, const void *data);

    /* buffer-and-flush services (per-worker buffers, race-free from parallel jobs) */
    void        (*logf)(SysCtx *ctx, const char *fmt, ...);
    uint32_t    (*log_drain)(SysCtx *ctx, EcsLogSinkFn fn, void *user);
    void        (*draw)(SysCtx *ctx, int x, int y, uint32_t rgb);
    uint32_t    (*draw_drain)(SysCtx *ctx, EcsDrawSinkFn fn, void *user);

    /* nested parallelism for TASKs that own an internal ordering invariant */
    void        (*parallel_for)(SysCtx *ctx, uint32_t count, EcsForFn fn, void *user);

    void        (*quit)(SysCtx *ctx);
};

/* Convenience wrappers used by game code. */
#define ecs_create(ctx, P)        ((ctx)->api->create((ctx), &P##__pdesc))
#define ecs_destroy(ctx, e)       ((ctx)->api->destroy((ctx), (e)))
#define ecs_init(ctx, e, T, ...) \
    do { T ecs_tmp__ = __VA_ARGS__; (ctx)->api->init((ctx), (e), &T##__cdesc, &ecs_tmp__); } while (0)
#define ecs_lookup(v, e)          ((v).ctx->api->lookup((v).ctx, (v).comp, (e)))
#define ecs_entity_at(ctx, r, i)  ((ctx)->api->entity_at((ctx), (r), (i)))
#define ecs_entity_index(ctx, e)  ((ctx)->api->entity_index((ctx), (e)))
#define ecs_register_of(ctx, T)   ((ctx)->api->register_of((ctx), &T##__cdesc))
#define ecs_log(ctx, ...)         ((ctx)->api->logf((ctx), __VA_ARGS__))
#define ecs_log_drain(ctx, fn, u) ((ctx)->api->log_drain((ctx), (fn), (u)))
#define ecs_draw(ctx, x, y, c)    ((ctx)->api->draw((ctx), (x), (y), (c)))
#define ecs_draw_drain(ctx, fn, u)((ctx)->api->draw_drain((ctx), (fn), (u)))
#define ecs_parallel_for(ctx, n, fn, u) ((ctx)->api->parallel_for((ctx), (n), (fn), (u)))
#define ecs_quit(ctx)             ((ctx)->api->quit((ctx)))

#ifdef __cplusplus
}
#endif
