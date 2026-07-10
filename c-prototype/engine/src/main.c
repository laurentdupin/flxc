/*
    Engine host: loads Game.dll, resolves its single EcsModule export,
    builds the world and schedule, prints the schedule explanation, and
    runs. All game state lives here; the DLL holds only code.

    Environment:
      FABLEC_WORKERS      total workers including main (default: CPU count)
*/
#include "world.h"
#include "scheduler.h"

#include <stdio.h>
#include <stdlib.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static uint32_t pick_worker_count(void)
{
    const char *env = getenv("FABLEC_WORKERS");
    if (env) {
        int v = atoi(env);
        if (v >= 1 && v <= MAX_WORKERS) {
            return (uint32_t)v;
        }
    }

    SYSTEM_INFO si;
    GetSystemInfo(&si);

    uint32_t n = si.dwNumberOfProcessors;
    if (n < 1) n = 1;
    if (n > MAX_WORKERS) n = MAX_WORKERS;
    return n;
}

int main(int argc, char **argv)
{
    const char *module_name = argc > 1 ? argv[1] : "Game.dll";

    HMODULE dll = LoadLibraryA(module_name);
    if (!dll) {
        fprintf(stderr, "[engine] failed to load %s (error %lu)\n", module_name, GetLastError());
        return 1;
    }

    EcsModuleFn get_module = (EcsModuleFn)GetProcAddress(dll, ECS_MODULE_ENTRY_NAME);
    if (!get_module) {
        fprintf(stderr, "[engine] Game.dll does not export %s\n", ECS_MODULE_ENTRY_NAME);
        return 1;
    }

    const EcsModuleDesc *module = get_module();
    uint32_t workers = pick_worker_count();

    printf("[engine] module: %u components, %u prefabs, %u systems, %u workers\n",
           module->component_count, module->prefab_count, module->system_count, workers);

    static World world;
    if (!world_init(&world, module, workers)) {
        return 1;
    }

    Scheduler *sched = scheduler_create(&world, workers);
    if (!scheduler_build(sched, module)) {
        return 1;
    }

    scheduler_explain(sched);
    scheduler_run(sched);

    printf("[engine] clean exit\n");

    scheduler_destroy(sched);
    world_shutdown(&world);
    FreeLibrary(dll);
    return 0;
}
