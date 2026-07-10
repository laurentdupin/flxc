/*
    scheduler.h — resolves the module's SCHEDULE into steps, and executes
    each step by throwing its systems at a mask-based dispatcher:

      launchable(S) :=  S.write & (active_read | active_write) == 0
                    &&  S.read  &  active_write               == 0

    SYSTEMs are chunked across a worker pool; each RUN step ends with a
    barrier where command buffers are applied. MAIN_THREAD tasks go to a
    queue only the main thread drains.
*/
#pragma once

#include "world.h"

typedef enum SysState { SYS_PENDING, SYS_RUNNING, SYS_DONE } SysState;

typedef struct SchedSystem {
    EcsSystemDesc *sys;
    Register      *reg;         /* NULL for tasks */
    SysState       state;
    uint32_t       remaining;   /* chunk jobs still running */
} SchedSystem;

#define MAX_STEP_SYSTEMS 64
#define MAX_SCHED_STEPS  64

typedef struct SchedStep {
    EcsStepKind kind;
    SchedSystem systems[MAX_STEP_SYSTEMS];
    uint32_t    count;
} SchedStep;

typedef struct Scheduler Scheduler;

Scheduler *scheduler_create(World *w, uint32_t worker_count);
void       scheduler_destroy(Scheduler *s);

/* Resolve schedule names to systems; returns 0 on unknown names. */
int  scheduler_build(Scheduler *s, const EcsModuleDesc *module);

/* Print systems, masks, conflicts, and greedy parallel groups per step. */
void scheduler_explain(Scheduler *s);

/* Run the whole schedule (including the frame LOOP) until quit. */
void scheduler_run(Scheduler *s);
