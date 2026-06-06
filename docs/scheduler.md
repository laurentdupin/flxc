# FLX Scheduler

The scheduler is declared with a single executable `schedule` block:

```flx
schedule {
    run CreateZombies;
    run Greet;
}
```

`run Name;` is strict. A non-wildcard target must resolve to exactly one function group:

- zero matches is an error
- one match runs that function group
- multiple matches is an ambiguity error

Wildcard targets such as `run *.Update;` opt in to aggregation explicitly.

## Parallel External Calls

Imported C calls block automatic parallel scheduling unless the source file marks the specific imported member as safe to call concurrently:

```flx
import c "stdio.h" as stdio

parallel stdio.printf;
```

This annotation is file-local, like `import c`. It only supports imported C alias members in the form `alias.member`.

`parallel stdio.printf;` does not mean the function is pure. It means calls to that C function are allowed from parallel scheduled jobs, may execute concurrently, and have unordered observable effects. The programmer accepts responsibility for those external effects.

## Automatic Parallel Runs

A scheduled prefab-parameter function can run in parallel when the compiler's conservative analysis proves the function is safe enough for the current implementation.

First-pass requirements:

- exactly one parameter
- the parameter type is a known prefab
- the function does not create, destroy, or reparent objects
- the function does not write globals
- every imported C call in the body is marked with `parallel alias.member;`

Prefab field assignment is allowed for the current object parameter in this first runtime because each scheduled iteration receives a distinct object view. This is still conservative: writes to globals, creates/destroys/reparents, and unknown external effects keep the step serial.

If a scheduled function is not parallelizable, code generation keeps the serial per-object loop and records the reason in metadata.

## Worker Count

Generated programs use the logical CPU count by default on Windows. Set `FLX_WORKERS` to a positive integer to override the requested worker count:

```powershell
$env:FLX_WORKERS = "8"
.\game.exe
```

The runtime may clamp the worker count for implementation limits and batch size. Non-Windows runtime generation currently uses a serial fallback.
