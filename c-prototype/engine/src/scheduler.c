#include "scheduler.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <process.h>

#define CHUNK_SIZE     512
#define SHARED_Q_CAP   4096
#define MAIN_Q_CAP     256

typedef struct NestedBatch {
    EcsForFn  fn;
    void     *user;
    SysCtx   *parent_ctx;
    uint32_t  remaining;      /* guarded by the scheduler lock */
} NestedBatch;

enum { JOB_CHUNK = 0, JOB_NESTED = 1 };

typedef struct Job {
    int          kind;
    SchedSystem *ss;          /* JOB_CHUNK */
    NestedBatch *batch;       /* JOB_NESTED */
    uint32_t     first;
    uint32_t     last;
} Job;

typedef struct JobQueue {
    Job      jobs[SHARED_Q_CAP];
    uint32_t head;
    uint32_t tail;
    uint32_t cap;
} JobQueue;

struct Scheduler {
    World *world;

    SchedStep steps[MAX_SCHED_STEPS];
    uint32_t  step_count;

    SRWLOCK            lock;
    CONDITION_VARIABLE cv_work;   /* workers wait for shared jobs */
    CONDITION_VARIABLE cv_done;   /* main waits for system completions */

    JobQueue shared_q;
    JobQueue main_q;

    uint64_t   active_read;
    uint64_t   active_write;
    SchedStep *current;

    HANDLE   threads[MAX_WORKERS];
    uint32_t thread_count;        /* worker threads (excludes main) */
    volatile long shutdown;
};

typedef struct WorkerArg {
    Scheduler *sched;
    uint32_t   worker_index;
} WorkerArg;

static WorkerArg g_worker_args[MAX_WORKERS];

/* ------------------------------------------------------------------ */
/* Queue helpers (caller holds the lock)                               */
/* ------------------------------------------------------------------ */

static int q_push(JobQueue *q, Job j)
{
    if (q->tail - q->head >= q->cap) {
        return 0;
    }
    q->jobs[q->tail % q->cap] = j;
    q->tail++;
    return 1;
}

static int q_pop(JobQueue *q, Job *out)
{
    if (q->head == q->tail) {
        return 0;
    }
    *out = q->jobs[q->head % q->cap];
    q->head++;
    return 1;
}

/* ------------------------------------------------------------------ */
/* Job execution                                                       */
/* ------------------------------------------------------------------ */

static void execute_job(Scheduler *s, const Job *j, uint32_t worker_index)
{
    World *w = s->world;

    if (j->kind == JOB_NESTED) {
        SysCtx sub = *j->batch->parent_ctx;
        sub.first = j->first;
        sub.last = j->last;
        sub.worker = worker_index;
        j->batch->fn(&sub, j->batch->user);
        return;
    }

    SysCtx ctx;
    ctx.api = &w->api;
    ctx.world = w;
    ctx.system = j->ss->sys;
    ctx.first = j->first;
    ctx.last = j->last;
    ctx.count = j->ss->reg ? j->ss->reg->count : 0;
    ctx.worker = worker_index;
    ctx.dt = w->dt;
    ctx.time = w->time;
    ctx.self.reg = j->ss->reg ? j->ss->reg->prefab->id : ECS_NO_REG;
    ctx.self.slot = 0;
    ctx.self.gen = 0;

    j->ss->sys->run(&ctx);
}

/* Caller holds the lock. */
static void recompute_active_masks(Scheduler *s)
{
    s->active_read = 0;
    s->active_write = 0;

    if (!s->current) {
        return;
    }

    for (uint32_t i = 0; i < s->current->count; ++i) {
        if (s->current->systems[i].state == SYS_RUNNING) {
            s->active_read |= s->current->systems[i].sys->read_mask;
            s->active_write |= s->current->systems[i].sys->write_mask;
        }
    }
}

/* Caller holds the lock. */
static void complete_job(Scheduler *s, const Job *j)
{
    if (j->kind == JOB_NESTED) {
        assert(j->batch->remaining > 0);
        j->batch->remaining--;
        WakeAllConditionVariable(&s->cv_done);
        return;
    }

    SchedSystem *ss = j->ss;
    assert(ss->remaining > 0);
    ss->remaining--;

    if (ss->remaining == 0) {
        ss->state = SYS_DONE;
        recompute_active_masks(s);
        WakeAllConditionVariable(&s->cv_done);
    }
}

static unsigned __stdcall worker_main(void *arg)
{
    WorkerArg *wa = (WorkerArg *)arg;
    Scheduler *s = wa->sched;

    AcquireSRWLockExclusive(&s->lock);
    for (;;) {
        Job j;
        while (!q_pop(&s->shared_q, &j)) {
            if (s->shutdown) {
                ReleaseSRWLockExclusive(&s->lock);
                return 0;
            }
            SleepConditionVariableSRW(&s->cv_work, &s->lock, INFINITE, 0);
        }

        ReleaseSRWLockExclusive(&s->lock);
        execute_job(s, &j, wa->worker_index);
        AcquireSRWLockExclusive(&s->lock);

        complete_job(s, &j);
    }
}

/* ------------------------------------------------------------------ */
/* Nested parallel-for (EcsApi.parallel_for)                           */
/* ------------------------------------------------------------------ */

#define NESTED_CHUNK 256

static void api_parallel_for(SysCtx *ctx, uint32_t count, EcsForFn fn, void *user)
{
    World     *w = (World *)ctx->world;
    Scheduler *s = (Scheduler *)w->sched;

    if (count == 0) {
        return;
    }

    NestedBatch batch = { fn, user, ctx, 0 };

    AcquireSRWLockExclusive(&s->lock);

    for (uint32_t f = 0; f < count; f += NESTED_CHUNK) {
        uint32_t l = f + NESTED_CHUNK < count ? f + NESTED_CHUNK : count;
        Job j = { JOB_NESTED, NULL, &batch, f, l };
        q_push(&s->shared_q, j);
        batch.remaining++;
    }
    WakeAllConditionVariable(&s->cv_work);

    /* Help until our batch drains — popping any shared job keeps the
       pool deadlock-free even when several tasks nest concurrently. */
    while (batch.remaining > 0) {
        Job j;
        if (q_pop(&s->shared_q, &j)) {
            ReleaseSRWLockExclusive(&s->lock);
            execute_job(s, &j, ctx->worker);
            AcquireSRWLockExclusive(&s->lock);
            complete_job(s, &j);
        }
        else {
            SleepConditionVariableSRW(&s->cv_done, &s->lock, INFINITE, 0);
        }
    }

    ReleaseSRWLockExclusive(&s->lock);
}

/* ------------------------------------------------------------------ */
/* Launching (caller holds the lock)                                   */
/* ------------------------------------------------------------------ */

static int can_launch(const Scheduler *s, const EcsSystemDesc *sys)
{
    if (sys->write_mask & (s->active_read | s->active_write)) {
        return 0;
    }
    if (sys->read_mask & s->active_write) {
        return 0;
    }
    return 1;
}

static void launch_system(Scheduler *s, SchedSystem *ss)
{
    if (ss->reg && ss->reg->count == 0) {
        ss->state = SYS_DONE;   /* empty register: nothing to do, no masks held */
        return;
    }

    ss->state = SYS_RUNNING;
    ss->remaining = 0;

    if (!ss->reg) {
        /* TASK: one job, pinned or shared */
        Job j = { JOB_CHUNK, ss, NULL, 0, 0 };
        q_push(ss->sys->main_thread ? &s->main_q : &s->shared_q, j);
        ss->remaining = 1;
    }
    else {
        uint32_t count = ss->reg->count;
        for (uint32_t f = 0; f < count; f += CHUNK_SIZE) {
            uint32_t l = f + CHUNK_SIZE < count ? f + CHUNK_SIZE : count;
            Job j = { JOB_CHUNK, ss, NULL, f, l };
            q_push(&s->shared_q, j);
            ss->remaining++;
        }
    }

    s->active_read |= ss->sys->read_mask;
    s->active_write |= ss->sys->write_mask;
    WakeAllConditionVariable(&s->cv_work);
}

/* ------------------------------------------------------------------ */
/* Step execution                                                      */
/* ------------------------------------------------------------------ */

static void run_step(Scheduler *s, SchedStep *st)
{
    AcquireSRWLockExclusive(&s->lock);

    s->current = st;
    s->active_read = 0;
    s->active_write = 0;
    for (uint32_t i = 0; i < st->count; ++i) {
        st->systems[i].state = SYS_PENDING;
        st->systems[i].remaining = 0;
    }

    for (;;) {
        int all_done = 1;

        for (uint32_t i = 0; i < st->count; ++i) {
            SchedSystem *ss = &st->systems[i];
            if (ss->state == SYS_PENDING) {
                if (can_launch(s, ss->sys)) {
                    launch_system(s, ss);
                }
                if (ss->state != SYS_DONE) {
                    all_done = 0;
                }
            }
            else if (ss->state == SYS_RUNNING) {
                all_done = 0;
            }
        }

        if (all_done) {
            break;
        }

        /* The main thread helps: pinned jobs first, then shared work. */
        Job j;
        if (q_pop(&s->main_q, &j) || q_pop(&s->shared_q, &j)) {
            ReleaseSRWLockExclusive(&s->lock);
            execute_job(s, &j, 0);
            AcquireSRWLockExclusive(&s->lock);
            complete_job(s, &j);
            continue;
        }

        SleepConditionVariableSRW(&s->cv_done, &s->lock, INFINITE, 0);
    }

    s->current = NULL;
    ReleaseSRWLockExclusive(&s->lock);

    /* Barrier: structural changes become visible here. */
    world_apply_commands(s->world);
}

/* ------------------------------------------------------------------ */
/* Schedule resolution                                                 */
/* ------------------------------------------------------------------ */

static int step_add_matches(SchedStep *st, World *w, const EcsModuleDesc *m, const char *name)
{
    int matched = 0;

    for (uint32_t i = 0; i < m->system_count; ++i) {
        EcsSystemDesc *sys = m->systems[i];

        if (strcmp(sys->phase, name) != 0) {
            continue;
        }
        if (st->count >= MAX_STEP_SYSTEMS) {
            fprintf(stderr, "[ecs] too many systems in one step\n");
            return 0;
        }

        SchedSystem *ss = &st->systems[st->count++];
        ss->sys = sys;
        ss->reg = sys->prefab_desc ? &w->registers[sys->prefab_desc->id] : NULL;
        matched = 1;
    }

    if (!matched) {
        fprintf(stderr, "[ecs] schedule step '%s' matches no task or phase\n", name);
    }
    return matched;
}

int scheduler_build(Scheduler *s, const EcsModuleDesc *module)
{
    if (!module->schedule) {
        fprintf(stderr, "[ecs] module has no SCHEDULE\n");
        return 0;
    }

    const EcsScheduleDesc *sched = module->schedule;
    if (sched->step_count > MAX_SCHED_STEPS) {
        fprintf(stderr, "[ecs] schedule too long\n");
        return 0;
    }

    for (uint32_t i = 0; i < sched->step_count; ++i) {
        const EcsScheduleStep *decl = &sched->steps[i];
        SchedStep *st = &s->steps[s->step_count++];

        memset(st, 0, sizeof(*st));
        st->kind = decl->kind;

        if (decl->kind == ECS_STEP_RUN) {
            for (uint32_t n = 0; n < decl->name_count; ++n) {
                if (!step_add_matches(st, s->world, module, decl->names[n])) {
                    return 0;
                }
            }
        }
    }

    return 1;
}

/* ------------------------------------------------------------------ */
/* Explain                                                             */
/* ------------------------------------------------------------------ */

static void print_mask(World *w, uint64_t mask)
{
    int first = 1;
    printf("{");
    for (uint32_t i = 0; i < w->component_count; ++i) {
        if (mask & (1ull << i)) {
            printf("%s%s", first ? "" : ",", w->components[i]->name);
            first = 0;
        }
    }
    printf("}");
}

static const char *sys_display_name(const EcsSystemDesc *sys, char *buf, size_t cap)
{
    if (sys->prefab) {
        snprintf(buf, cap, "%s.%s", sys->prefab, sys->phase);
        return buf;
    }
    return sys->phase;
}

static int systems_conflict(const EcsSystemDesc *a, const EcsSystemDesc *b)
{
    return (a->write_mask & (b->read_mask | b->write_mask)) != 0 ||
           (b->write_mask & a->read_mask) != 0;
}

void scheduler_explain(Scheduler *s)
{
    World *w = s->world;
    char na[96], nb[96];

    printf("=== schedule explanation ===\n");

    for (uint32_t si = 0; si < s->step_count; ++si) {
        SchedStep *st = &s->steps[si];

        if (st->kind == ECS_STEP_LOOP_BEGIN) { printf("loop {\n"); continue; }
        if (st->kind == ECS_STEP_LOOP_END)   { printf("}\n");      continue; }

        printf("step:\n");
        for (uint32_t i = 0; i < st->count; ++i) {
            EcsSystemDesc *sys = st->systems[i].sys;
            printf("  %-22s reads", sys_display_name(sys, na, sizeof(na)));
            print_mask(w, sys->read_mask);
            printf(" writes");
            print_mask(w, sys->write_mask);
            if (sys->main_thread) {
                printf(" [main thread]");
            }
            printf("\n");
        }

        for (uint32_t i = 0; i < st->count; ++i) {
            for (uint32_t j = i + 1; j < st->count; ++j) {
                if (systems_conflict(st->systems[i].sys, st->systems[j].sys)) {
                    printf("  conflict: %s x %s\n",
                           sys_display_name(st->systems[i].sys, na, sizeof(na)),
                           sys_display_name(st->systems[j].sys, nb, sizeof(nb)));
                }
            }
        }

        /* Greedy grouping preview: which systems could start together. */
        if (st->count > 1) {
            int grouped[MAX_STEP_SYSTEMS] = { 0 };
            int group = 0;

            for (uint32_t i = 0; i < st->count; ++i) {
                if (grouped[i]) {
                    continue;
                }
                group++;
                printf("  parallel group %d: %s",
                       group, sys_display_name(st->systems[i].sys, na, sizeof(na)));
                grouped[i] = group;

                for (uint32_t j = i + 1; j < st->count; ++j) {
                    if (grouped[j] != group && grouped[j] == 0) {
                        int ok = 1;
                        for (uint32_t k = 0; k < st->count; ++k) {
                            if (grouped[k] == group &&
                                systems_conflict(st->systems[k].sys, st->systems[j].sys)) {
                                ok = 0;
                                break;
                            }
                        }
                        if (ok) {
                            printf(", %s", sys_display_name(st->systems[j].sys, nb, sizeof(nb)));
                            grouped[j] = group;
                        }
                    }
                }
                printf("\n");
            }
        }
    }

    printf("============================\n");
}

/* ------------------------------------------------------------------ */
/* Run                                                                 */
/* ------------------------------------------------------------------ */

void scheduler_run(Scheduler *s)
{
    World *w = s->world;

    LARGE_INTEGER freq, prev, now;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&prev);

    uint32_t loop_begin = 0;
    uint32_t ip = 0;

    while (ip < s->step_count) {
        SchedStep *st = &s->steps[ip];

        switch (st->kind) {
        case ECS_STEP_LOOP_BEGIN:
            loop_begin = ip + 1;

            QueryPerformanceCounter(&now);
            w->dt = (float)((double)(now.QuadPart - prev.QuadPart) / (double)freq.QuadPart);
            if (w->dt > 0.1f) {
                w->dt = 0.1f;
            }
            w->time += w->dt;
            prev = now;
            ip++;
            break;

        case ECS_STEP_LOOP_END:
            if (!w->quit) {
                ip = loop_begin - 1;   /* back to LOOP_BEGIN for dt update */
            }
            else {
                ip++;
            }
            break;

        case ECS_STEP_RUN:
        default:
            run_step(s, st);
            ip++;
            break;
        }
    }
}

/* ------------------------------------------------------------------ */
/* Create / destroy                                                    */
/* ------------------------------------------------------------------ */

Scheduler *scheduler_create(World *w, uint32_t worker_count)
{
    Scheduler *s = calloc(1, sizeof(Scheduler));

    s->world = w;
    w->sched = s;
    w->api.parallel_for = api_parallel_for;
    s->shared_q.cap = SHARED_Q_CAP;
    s->main_q.cap = MAIN_Q_CAP;
    InitializeSRWLock(&s->lock);
    InitializeConditionVariable(&s->cv_work);
    InitializeConditionVariable(&s->cv_done);

    /* worker 0 is the main thread; spawn worker_count-1 threads */
    s->thread_count = worker_count > 1 ? worker_count - 1 : 0;
    for (uint32_t i = 0; i < s->thread_count; ++i) {
        g_worker_args[i].sched = s;
        g_worker_args[i].worker_index = i + 1;
        s->threads[i] = (HANDLE)_beginthreadex(NULL, 0, worker_main, &g_worker_args[i], 0, NULL);
    }

    return s;
}

void scheduler_destroy(Scheduler *s)
{
    AcquireSRWLockExclusive(&s->lock);
    InterlockedExchange(&s->shutdown, 1);
    WakeAllConditionVariable(&s->cv_work);
    ReleaseSRWLockExclusive(&s->lock);

    for (uint32_t i = 0; i < s->thread_count; ++i) {
        WaitForSingleObject(s->threads[i], INFINITE);
        CloseHandle(s->threads[i]);
    }

    free(s);
}
