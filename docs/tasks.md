# FLX Background Tasks

FLX has a first-pass runtime API for background file loading. This is intentionally small: it proves that work can continue outside the schedule step that started it, without allowing background code to mutate live world storage.

## Current API

The generated runtime header exposes an opaque file task:

```c
typedef struct flx_file_task {
    flx_file_task_state *state;
} flx_file_task;

flx_file_task flx_task_read_file_start(const char *path);
int flx_task_poll_file(flx_file_task *task);
int flx_task_file_status(const flx_file_task *task);
usize flx_task_file_length(const flx_file_task *task);
const char *flx_task_file_data(const flx_file_task *task);
void flx_task_destroy_file(flx_file_task *task);
```

FLX source can use these generated runtime calls directly inside ordinary functions:

```flx
void TickFileLoad() {
    static flx_file_task task = { 0 };
    static i32 started = 0;

    if (started == 0) {
        task = flx_task_read_file_start("data/level.bin");
        started = 1;
    }

    if (flx_task_poll_file(&task)) {
        // Apply the loaded result at this schedule step boundary.
        flx_task_destroy_file(&task);
        breakloop;
    }
}
```

On Windows, file loading runs on a background thread. On non-Windows targets, the same API currently uses a synchronous fallback so generated code remains portable while the runtime grows.

## Rules

- Background task workers must not mutate live world or prefab storage.
- Results are observed by polling from scheduled functions.
- Applying task results happens inside normal scheduled code, not from the worker thread.
- The task owner must call `flx_task_destroy_file` after completion or cancellation.
- `flx_task_file_data` is owned by the task and becomes invalid after destroy.

## Next Syntax Direction

The runtime API is a stepping stone toward language-level task declarations:

```flx
task Buffer LoadFile(string path)
effects(blocking_io)
{
    ...
}

void RequestAssets() {
    start LoadFile("data/level.bin");
}
```

The language form should keep the same rules: tasks may block, tasks return results or completion events, and tasks do not directly mutate live world storage.

## Open Work

- Persistent task executor instead of per-task thread creation.
- Cancellation and timeout support.
- Completion events consumed by schedule steps.
- Asset handles with non-blocking `try_get` behavior.
- Explicit diagnostics for illegal world mutation from task bodies once task syntax exists.
