# FableC — parallel-first ECS engine prototype

A host engine (`Engine.exe`) that loads game code from a DLL (`Game.dll`),
discovers its components/prefabs/systems/schedule through a single exported
table, and executes each schedule step with **mask-based parallel dispatch**:
two systems run concurrently exactly when their declared read/write sets
don't collide.

Open `FableC.slnx` in Visual Studio (v145 toolset), set **Engine** as the
startup project, F5. A window opens with ~1200 zombies chasing food patches
plus six animated 2D trees; the console prints the schedule explanation and
periodic stats.

```
build\Debug\Engine.exe             # 2D demo (Game.dll, default)
build\Debug\Engine.exe Tree3D.dll  # 3D wind tree (raylib)
FABLEC_MAX_FRAMES=600              # exit after N frames (default: until window closes)
FABLEC_WORKERS=4                   # total workers incl. main thread (default: CPU count)
```

**Tree3D** (`game3d/`) renders a 3D tree with ~1100 branches and ~3600
independently fluttering leaves under a gusty wind, using raylib. One
`TreeNode` prefab covers branches and leaves; the `Animate` system flutters
every node in parallel chunks reading a `Wind` singleton, and the transform
hierarchy is propagated per depth level with `ecs_parallel_for`. Because
raylib/OpenGL is thread-affine, *all* GL calls live in two `TASK_MAIN`
tasks (`Setup3D`, `Render3D`) — the external-call rule with a real API.
Requires `external\raylib\` (raylib 5.5 win64 MSVC release unzipped there;
`include\` + `lib\raylib.lib`).

## Layout

| Path | What |
|---|---|
| `ecs/ecs_api.h` | The host↔DLL ABI: descriptors, `SysCtx`, the `EcsApi` function table |
| `ecs/ecs_meta.h` | The six declaration macros + FOR_EACH machinery + linker-section registration |
| `engine/` | Host: register storage, command buffers, mask scheduler, worker pool, DLL loader |
| `game/` | Demo module: all data + systems + schedule, compiled to `Game.dll` |

## The declaration surface

Game code declares everything; the host infers nothing:

```c
COMPONENT(Position, { float x, y; });
SINGLETON(FrameStats, { uint32_t frame; });
PREFAB(Zombie, Position, Velocity, Hunger);

SYSTEM(Zombie, Seek, READ(Position, p), WRITE(Velocity, v), QUERY(FoodInfo, foods))
{
    /* runs once per zombie; the scheduler chunks the register across workers */
}

TASK(FlushLog, WRITE(LogSink, l))       { /* runs once per scheduled step */ }
TASK_MAIN(PumpMessages, WRITE(WindowState, w)) { /* pinned to the main thread */ }

SCHEDULE(
    RUN(SpawnWorld),
    LOOP(
        RUN(Seek, Move, Digest, Regrow),   /* one step; masks decide overlap */
        RUN(Render),
        RUN(FinishFrame)
    )
);
ECS_MODULE()
```

Each macro emits a descriptor and drops a pointer to it into the `ecs$m`
linker section; `ECS_MODULE()` defines the DLL's one export, `EcsModule()`,
which walks the section and returns the module table. Adding a system is
just writing the `SYSTEM` block — no registration lists to maintain.

## How scheduling works

- Each component type gets a bit index at load. Each system gets two
  constant masks built from its declared clauses (QUERY counts as read).
- Within a `RUN` step, a system launches when
  `write & (active_read|active_write) == 0 && read & active_write == 0`
  against everything currently executing. Conflicting systems serialize
  automatically, in declaration order.
- SYSTEMs are split into 512-entity chunks over a shared work queue;
  `TASK_MAIN` jobs go to a queue only the main thread drains.
- Every `RUN` boundary is a barrier: jobs finish, then per-worker command
  buffers apply (creates first, then destroys sorted descending so
  swap-remove indices stay valid).
- `ecs_create`/`ecs_init`/`ecs_destroy` are always deferred, so structural
  changes never appear in the masks — a spawner system runs fully parallel.
  A created entity is provisional until the next barrier: `ecs_init` it in
  the same system, read it next frame.
- External-call policy: exclusive resources are singletons guarded by
  `WRITE` masks; hot paths use buffer-and-flush (`ecs_log` → `FlushLog`,
  `ecs_draw` → `EndFrame` drain per-worker buffers in stable worker order);
  thread-affine APIs (the Win32 message pump) use `TASK_MAIN`.

Startup prints the resolved schedule, per-system masks, every conflict
pair, and the greedy parallel groups — the demo's Update step yields
`{Seek, Digest}` then `{Move, Regrow}`.

## Entities, handles, and cross-entity clauses

Entity handles are generational: `{register, slot, generation}`. They stay
valid across swap-removes; after destruction, lookups return NULL / -1.
`ecs_create` returns a real handle immediately (readable next frame).

Cross-entity clauses and their capability rules (validated at load):

| Clause | Binds | Mask | SYSTEM | TASK |
|---|---|---|---|---|
| `QUERY(T,n)` | whole column + count, const | read | yes | yes |
| `FIRST(T,n)` | one row, const, NULL if none | read | yes | yes |
| `LOOKUP(T,n)` | `ecs_lookup(n, entity)` by handle | read | yes | yes |
| `QUERY_MUT` / `FIRST_MUT` / `LOOKUP_MUT` | same, mutable | write | **load error** | yes |

The mutable forms are TASK-only because masks protect systems from each
other, not a chunked system from itself.

## Hierarchy

`Parent {Entity}` is the only authoritative link. `RebuildHierarchy` (a
TASK) derives everything else each frame: depth buckets for propagation,
plus `Children {first,count}` slices into a `ChildPool` singleton.
`ComputeWorldMatrices` holds the `WorldMatrix` write bit and walks depth
levels, fanning each level across the pool with `ecs_parallel_for` — the
one algorithm whose ordering invariant lives inside a single column, so it
encapsulates it behind a task boundary. Pruned parents make orphans
re-root automatically (stale handle → NULL → treated as root).
`VerifyHierarchy` re-derives every parented node's matrix and reports
mismatches.

## Known simplifications (deliberate, this is a prototype)

- Declared masks are trusted, not enforced; the planned debug-build checked
  accessors are not implemented yet.
- Component/prefab/system caps are fixed constants; masks cap at 64
  component types (one `uint64_t`).
- `QUERY`/`FIRST`/`register_of` resolve to the *first* register owning `T` —
  fine while a component type lives in one prefab. `LOOKUP` works across
  all registers (the handle names its register).
- Every SYSTEM/TASK needs at least one clause; clause count caps at 8.
- Game module must be a single translation unit (descriptors are `static`).
- Single module DLL; multi-module loading (renderer/platform backends as
  separate DLLs) is designed but not implemented.
- No hot reload yet — but all state already lives in the host, so it's
  unload/reload/re-resolve one symbol away.
- MSVC-only: linker-section registration uses `#pragma section` +
  `__declspec(allocate)` + `/include:` (the `/include` pragma is what keeps
  LTCG from stripping the entries). GCC/Clang would use
  `__attribute__((section))` + `used`.

## Where Vulkan would slot in

Vulkan's "externally synchronized" rule maps 1:1 onto the masks: command
*recording* is per-worker (one `VkCommandPool` per worker, handed out via
`SysCtx`) in parallel `Render` systems, and queue submit / acquire / present
live in a single `TASK(SubmitFrame, WRITE(Renderer, r))` — same
buffer-then-flush shape as `ecs_draw`/`EndFrame` in this demo, no
`TASK_MAIN` needed since Vulkan has no thread affinity.
