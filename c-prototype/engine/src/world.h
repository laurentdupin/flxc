/*
    world.h — host-side storage: registers (one per prefab, SoA columns
    plus generational slot tables), singleton storage, and the per-worker
    command/log/draw buffers.
*/
#pragma once

#include "ecs_api.h"

#define MAX_COMPONENTS 64
#define MAX_REGISTERS  16
#define MAX_WORKERS    16

#define DENSE_PENDING 0xffffffffu   /* slot allocated, row materializes at barrier */

typedef struct EcsSlot {
    uint32_t dense;     /* dense row index, or DENSE_PENDING */
    uint32_t gen;       /* bumped on free; 0 = never allocated */
} EcsSlot;

typedef struct Register {
    EcsPrefabDesc *prefab;
    uint32_t       count;
    uint32_t       capacity;
    void          *cols[ECS_MAX_PREFAB_COMPONENTS];  /* parallel to prefab->components */

    /* generational handle tables */
    EcsSlot  *slots;            /* slot -> {dense, gen} */
    uint32_t *slot_of_dense;    /* dense -> slot (capacity entries) */
    uint32_t  slot_cap;
    uint32_t *free_slots;       /* stack of freed slot indices */
    uint32_t  free_count;
} Register;

#define LOG_LINE_MAX  240
#define LOG_LINES_CAP 2048

typedef struct LogBuf {
    char    (*lines)[LOG_LINE_MAX];
    uint32_t count;
} LogBuf;

typedef struct DrawCmd {
    int16_t  x, y;
    uint32_t rgb;
} DrawCmd;

#define DRAW_CMDS_CAP 65536

typedef struct DrawBuf {
    DrawCmd *cmds;
    uint32_t count;
} DrawBuf;

#define CMDBUF_INITIAL (1u << 20)   /* grows on demand; records are offset-based */
#define CMD_NO_LINK 0xffffffffu

typedef struct CmdBuf {
    uint8_t *data;
    uint32_t size;
    uint32_t cap;
    uint32_t last_create;   /* offset of most recent CmdCreate, CMD_NO_LINK if none */
} CmdBuf;

typedef struct World {
    const EcsModuleDesc *module;

    EcsComponentDesc *components[MAX_COMPONENTS];
    uint32_t          component_count;
    void             *singletons[MAX_COMPONENTS];   /* indexed by component id */

    Register registers[MAX_REGISTERS];
    uint32_t register_count;

    CmdBuf  cmd[MAX_WORKERS];
    LogBuf  logs[MAX_WORKERS];
    DrawBuf draws[MAX_WORKERS];
    uint32_t worker_count;      /* including the main thread (worker 0) */

    void *create_lock;          /* SRWLOCK guarding slot allocation across workers */
    void *sched;                /* Scheduler*, set by scheduler_create (parallel_for) */

    float         dt;
    double        time;
    volatile long quit;

    EcsApi api;
} World;

int  world_init(World *w, const EcsModuleDesc *module, uint32_t worker_count);
void world_shutdown(World *w);

/* Apply all per-worker command buffers. Single-threaded; call at barriers only. */
void world_apply_commands(World *w);
