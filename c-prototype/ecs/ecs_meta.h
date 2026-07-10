/*
    ecs_meta.h — declaration macros for game modules.

    COMPONENT / SINGLETON / PREFAB / SYSTEM / TASK / TASK_MAIN / SCHEDULE /
    ECS_MODULE. Each declaration emits its descriptor and drops a pointer to
    it into the "ecs$m" linker section; ECS_MODULE() defines the exported
    EcsModule() that walks the section and hands the host one table.

    Requires MSVC with the conforming preprocessor (/Zc:preprocessor).
    Clause lists (READ/WRITE/QUERY) support 1..8 clauses.
*/
#pragma once

#include "ecs_api.h"

/* ------------------------------------------------------------------ */
/* FOR_EACH machinery                                                  */
/* ------------------------------------------------------------------ */

#define ECS_CAT(a, b)  ECS_CAT_(a, b)
#define ECS_CAT_(a, b) a##b

#define ECS_NARG(...)  ECS_NARG_(__VA_ARGS__, 8, 7, 6, 5, 4, 3, 2, 1)
#define ECS_NARG_(_1, _2, _3, _4, _5, _6, _7, _8, N, ...) N

#define ECS_FE_1(F, a)      F(a)
#define ECS_FE_2(F, a, ...) F(a) ECS_FE_1(F, __VA_ARGS__)
#define ECS_FE_3(F, a, ...) F(a) ECS_FE_2(F, __VA_ARGS__)
#define ECS_FE_4(F, a, ...) F(a) ECS_FE_3(F, __VA_ARGS__)
#define ECS_FE_5(F, a, ...) F(a) ECS_FE_4(F, __VA_ARGS__)
#define ECS_FE_6(F, a, ...) F(a) ECS_FE_5(F, __VA_ARGS__)
#define ECS_FE_7(F, a, ...) F(a) ECS_FE_6(F, __VA_ARGS__)
#define ECS_FE_8(F, a, ...) F(a) ECS_FE_7(F, __VA_ARGS__)
#define ECS_FOR_EACH(F, ...) ECS_CAT(ECS_FE_, ECS_NARG(__VA_ARGS__))(F, __VA_ARGS__)

/* ------------------------------------------------------------------ */
/* Access clauses                                                      */
/* ------------------------------------------------------------------ */

/*
    A clause expands to a tagged tuple; each expansion context below
    dispatches on the tag.

      READ/WRITE  — one component of the current entity (or a singleton in TASKs)
      QUERY       — const view of the whole register owning T (+ <n>_count)
      QUERY_MUT   — mutable variant (TASK only, enforced at load)
      FIRST       — const T* to entity 0 of that register, NULL when empty
      FIRST_MUT   — mutable variant (TASK only)
      LOOKUP      — EcsLookup view; random access by handle via ecs_lookup()
      LOOKUP_MUT  — mutable variant (TASK only)
*/
#define READ(T, n)       (R, T, n)
#define WRITE(T, n)      (W, T, n)
#define QUERY(T, n)      (Q, T, n)
#define QUERY_MUT(T, n)  (QM, T, n)
#define FIRST(T, n)      (FR, T, n)
#define FIRST_MUT(T, n)  (FM, T, n)
#define LOOKUP(T, n)     (LR, T, n)
#define LOOKUP_MUT(T, n) (LM, T, n)

/* parameter list */
#define ECS_P(t)         ECS_P_ t
#define ECS_P_(tag, T, n) ECS_CAT(ECS_P__, tag)(T, n)
#define ECS_P__R(T, n)   , const T *n
#define ECS_P__W(T, n)   , T *n
#define ECS_P__Q(T, n)   , const T *n, uint32_t n##_count
#define ECS_P__QM(T, n)  , T *n, uint32_t n##_count
#define ECS_P__FR(T, n)  , const T *n
#define ECS_P__FM(T, n)  , T *n
#define ECS_P__LR(T, n)  , EcsLookup n
#define ECS_P__LM(T, n)  , EcsLookup n

/* column fetches at the top of the run thunk */
#define ECS_F(t)         ECS_F_ t
#define ECS_F_(tag, T, n) ECS_CAT(ECS_F__, tag)(T, n)
#define ECS_F__R(T, n)   const T *n##__col = (const T *)ctx->api->sys_col(ctx, &T##__cdesc);
#define ECS_F__W(T, n)   T *n##__col = (T *)ctx->api->sys_col(ctx, &T##__cdesc);
#define ECS_F__Q(T, n)   uint32_t n##__count; \
                         const T *n##__col = (const T *)ctx->api->query_col(ctx, &T##__cdesc, &n##__count);
#define ECS_F__QM(T, n)  uint32_t n##__count; \
                         T *n##__col = (T *)ctx->api->query_col(ctx, &T##__cdesc, &n##__count);
#define ECS_F__FR(T, n)  uint32_t n##__fc; \
                         const T *n##__first = (const T *)ctx->api->query_col(ctx, &T##__cdesc, &n##__fc); \
                         if (!n##__fc) n##__first = NULL;
#define ECS_F__FM(T, n)  uint32_t n##__fc; \
                         T *n##__first = (T *)ctx->api->query_col(ctx, &T##__cdesc, &n##__fc); \
                         if (!n##__fc) n##__first = NULL;
#define ECS_F__LR(T, n)  EcsLookup n##__view = { ctx, &T##__cdesc };
#define ECS_F__LM(T, n)  EcsLookup n##__view = { ctx, &T##__cdesc };

/* call arguments, per entity (SYSTEM); a singleton binds its one instance
   instead of being indexed (the check is loop-invariant and hoisted) */
#define ECS_A(t)         ECS_A_ t
#define ECS_A_(tag, T, n) ECS_CAT(ECS_A__, tag)(T, n)
#define ECS_A__R(T, n)   , n##__col + (T##__cdesc.is_singleton ? 0u : ecs_i__)
#define ECS_A__W(T, n)   , n##__col + (T##__cdesc.is_singleton ? 0u : ecs_i__)
#define ECS_A__Q(T, n)   , n##__col, n##__count
#define ECS_A__QM(T, n)  , n##__col, n##__count
#define ECS_A__FR(T, n)  , n##__first
#define ECS_A__FM(T, n)  , n##__first
#define ECS_A__LR(T, n)  , n##__view
#define ECS_A__LM(T, n)  , n##__view

/* call arguments, once (TASK — READ/WRITE bind singletons directly) */
#define ECS_AT(t)         ECS_AT_ t
#define ECS_AT_(tag, T, n) ECS_CAT(ECS_AT__, tag)(T, n)
#define ECS_AT__R(T, n)  , n##__col
#define ECS_AT__W(T, n)  , n##__col
#define ECS_AT__Q(T, n)  , n##__col, n##__count
#define ECS_AT__QM(T, n) , n##__col, n##__count
#define ECS_AT__FR(T, n) , n##__first
#define ECS_AT__FM(T, n) , n##__first
#define ECS_AT__LR(T, n) , n##__view
#define ECS_AT__LM(T, n) , n##__view

/* descriptor access entries */
#define ECS_X(t)         ECS_X_ t
#define ECS_X_(tag, T, n) ECS_CAT(ECS_X__, tag)(T, n)
#define ECS_X__R(T, n)   { &T##__cdesc, ECS_ACCESS_READ },
#define ECS_X__W(T, n)   { &T##__cdesc, ECS_ACCESS_WRITE },
#define ECS_X__Q(T, n)   { &T##__cdesc, ECS_ACCESS_QUERY },
#define ECS_X__QM(T, n)  { &T##__cdesc, ECS_ACCESS_QUERY_MUT },
#define ECS_X__FR(T, n)  { &T##__cdesc, ECS_ACCESS_FIRST },
#define ECS_X__FM(T, n)  { &T##__cdesc, ECS_ACCESS_FIRST_MUT },
#define ECS_X__LR(T, n)  { &T##__cdesc, ECS_ACCESS_LOOKUP },
#define ECS_X__LM(T, n)  { &T##__cdesc, ECS_ACCESS_LOOKUP_MUT },

/* ------------------------------------------------------------------ */
/* Linker-section registration                                         */
/* ------------------------------------------------------------------ */

#pragma section("ecs$a", read)
#pragma section("ecs$m", read)
#pragma section("ecs$z", read)

/*
    Entries get external linkage plus a /include directive so that
    LTCG + /OPT:REF cannot dead-strip them (nothing references them).
*/
#define ECS_REG(desc) \
    __pragma(comment(linker, "/include:" #desc "__reg")) \
    __declspec(allocate("ecs$m")) void *const ECS_CAT(desc, __reg) = (void *)&desc

/* ------------------------------------------------------------------ */
/* COMPONENT / SINGLETON / PREFAB                                      */
/* ------------------------------------------------------------------ */

/* usage: COMPONENT(Position, { float x, y; }); */
#define COMPONENT(T, ...)  ECS_COMPONENT_IMPL(T, 0, __VA_ARGS__)
#define SINGLETON(T, ...)  ECS_COMPONENT_IMPL(T, 1, __VA_ARGS__)

#define ECS_COMPONENT_IMPL(T, singleton, ...) \
    typedef struct T __VA_ARGS__ T; \
    static EcsComponentDesc T##__cdesc = { ECS_DESC_COMPONENT, #T, sizeof(T), singleton, 0 }; \
    ECS_REG(T##__cdesc)

/* usage: PREFAB(Zombie, Position, Velocity, Hunger); */
#define ECS_PC(T) &T##__cdesc,
#define PREFAB(P, ...) \
    static EcsPrefabDesc P##__pdesc = { \
        ECS_DESC_PREFAB, #P, { ECS_FOR_EACH(ECS_PC, __VA_ARGS__) }, ECS_NARG(__VA_ARGS__), 0 }; \
    ECS_REG(P##__pdesc)

/* ------------------------------------------------------------------ */
/* SYSTEM / TASK / TASK_MAIN                                           */
/* ------------------------------------------------------------------ */

/*
    SYSTEM(Prefab, Phase, clauses...) { per-entity body }
      - body runs once per entity; the scheduler chunks the register
        across workers. `ctx` is available; ctx->self is the entity.

    TASK(Name, clauses...) { body }
      - runs once per step it is scheduled in; READ/WRITE clauses bind
        singletons, QUERY binds whole registers.

    TASK_MAIN(...) additionally pins the task to the main thread.
*/
#define SYSTEM(P, Phase, ...) \
    static void P##__##Phase(SysCtx *ctx ECS_FOR_EACH(ECS_P, __VA_ARGS__)); \
    static void P##__##Phase##__run(SysCtx *ctx) { \
        ECS_FOR_EACH(ECS_F, __VA_ARGS__) \
        for (uint32_t ecs_i__ = ctx->first; ecs_i__ < ctx->last; ++ecs_i__) { \
            ctx->self = ctx->api->entity_at(ctx, ctx->self.reg, ecs_i__); \
            P##__##Phase(ctx ECS_FOR_EACH(ECS_A, __VA_ARGS__)); \
        } \
    } \
    static EcsSystemDesc P##__##Phase##__sdesc = { \
        ECS_DESC_SYSTEM, #P, #Phase, P##__##Phase##__run, \
        { ECS_FOR_EACH(ECS_X, __VA_ARGS__) }, ECS_NARG(__VA_ARGS__), 0, 0, 0, 0 }; \
    ECS_REG(P##__##Phase##__sdesc); \
    static void P##__##Phase(SysCtx *ctx ECS_FOR_EACH(ECS_P, __VA_ARGS__))

#define ECS_TASK_IMPL(Name, mainflag, ...) \
    static void Name(SysCtx *ctx ECS_FOR_EACH(ECS_P, __VA_ARGS__)); \
    static void Name##__run(SysCtx *ctx) { \
        ECS_FOR_EACH(ECS_F, __VA_ARGS__) \
        Name(ctx ECS_FOR_EACH(ECS_AT, __VA_ARGS__)); \
    } \
    static EcsSystemDesc Name##__sdesc = { \
        ECS_DESC_SYSTEM, 0, #Name, Name##__run, \
        { ECS_FOR_EACH(ECS_X, __VA_ARGS__) }, ECS_NARG(__VA_ARGS__), mainflag, 0, 0, 0 }; \
    ECS_REG(Name##__sdesc); \
    static void Name(SysCtx *ctx ECS_FOR_EACH(ECS_P, __VA_ARGS__))

#define TASK(Name, ...)      ECS_TASK_IMPL(Name, 0, __VA_ARGS__)
#define TASK_MAIN(Name, ...) ECS_TASK_IMPL(Name, 1, __VA_ARGS__)

/* ------------------------------------------------------------------ */
/* SCHEDULE                                                            */
/* ------------------------------------------------------------------ */

/*
    SCHEDULE(
        RUN(PrintStartup),
        RUN(SpawnWorld),
        LOOP(
            RUN(PumpMessages),
            RUN(Seek, Move, Digest, Regrow),   // one step, mask-scheduled together
            RUN(Render),
            RUN(FinishFrame)
        )
    );

    Each RUN is a barrier: the host waits for the step's jobs, applies
    command buffers, then dispatches the next step. A name matches a
    task, or aggregates every <Prefab>__<name> system.
*/
#define ECS_STRC(x) #x,
#define RUN(...)  { ECS_STEP_RUN, ECS_NARG(__VA_ARGS__), { ECS_FOR_EACH(ECS_STRC, __VA_ARGS__) } }
#define LOOP(...) { ECS_STEP_LOOP_BEGIN, 0, { 0 } }, __VA_ARGS__, { ECS_STEP_LOOP_END, 0, { 0 } }

#define SCHEDULE(...) \
    static const EcsScheduleStep ecs__steps[] = { __VA_ARGS__ }; \
    static EcsScheduleDesc ecs__schedule = { \
        ECS_DESC_SCHEDULE, ecs__steps, (uint32_t)(sizeof(ecs__steps) / sizeof(ecs__steps[0])) }; \
    ECS_REG(ecs__schedule)

/* ------------------------------------------------------------------ */
/* ECS_MODULE                                                          */
/* ------------------------------------------------------------------ */

#define ECS_MAX_MODULE_COMPONENTS 64
#define ECS_MAX_MODULE_PREFABS    16
#define ECS_MAX_MODULE_SYSTEMS    64

static const EcsModuleDesc *ecs__build_module(void *const *sec_a, void *const *sec_z)
{
    static EcsComponentDesc *comps[ECS_MAX_MODULE_COMPONENTS];
    static EcsPrefabDesc    *prefs[ECS_MAX_MODULE_PREFABS];
    static EcsSystemDesc    *syss[ECS_MAX_MODULE_SYSTEMS];
    static EcsModuleDesc     m;

    uint32_t nc = 0, np = 0, ns = 0;
    const EcsScheduleDesc *sched = 0;

    for (void *const *p = sec_a + 1; p < sec_z; ++p) {
        if (!*p) {
            continue;   /* linker padding */
        }

        switch (*(const EcsDescKind *)*p) {
        case ECS_DESC_COMPONENT:
            if (nc < ECS_MAX_MODULE_COMPONENTS) comps[nc++] = (EcsComponentDesc *)*p;
            break;
        case ECS_DESC_PREFAB:
            if (np < ECS_MAX_MODULE_PREFABS) prefs[np++] = (EcsPrefabDesc *)*p;
            break;
        case ECS_DESC_SYSTEM:
            if (ns < ECS_MAX_MODULE_SYSTEMS) syss[ns++] = (EcsSystemDesc *)*p;
            break;
        case ECS_DESC_SCHEDULE:
            sched = (const EcsScheduleDesc *)*p;
            break;
        }
    }

    m.components = comps;      m.component_count = nc;
    m.prefabs = prefs;         m.prefab_count = np;
    m.systems = syss;          m.system_count = ns;
    m.schedule = sched;
    return &m;
}

#define ECS_MODULE() \
    __declspec(allocate("ecs$a")) static void *const ecs__sec_a = 0; \
    __declspec(allocate("ecs$z")) static void *const ecs__sec_z = 0; \
    __declspec(dllexport) const EcsModuleDesc *EcsModule(void) { \
        return ecs__build_module(&ecs__sec_a, &ecs__sec_z); \
    }
