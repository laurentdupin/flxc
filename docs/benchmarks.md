# FLX Benchmarks

## Parallel CPU Demo

`examples/benchmarks/parallel_cpu.flx` is a non-graphical benchmark/demo for the generated scheduler. It creates many `WorkItem` objects, then times a single scheduled prefab-parameter update, `Crunch(WorkItem)`.

The timed workload is intentionally CPU-bound and does not use `printf`. Printing, timing, and object creation happen in separate serial schedule steps. `Crunch(WorkItem)` reads the generated object id, performs deterministic local integer mixing, returns the result, and does not write prefab fields or globals. That keeps it inside the compiler's current automatic parallel scheduling rules.

Validate C generation without invoking the C preprocessor:

```powershell
dotnet run --project src\Flx.Compiler\Flx.Compiler.csproj -- --emit-c --no-preprocess examples\benchmarks\parallel_cpu.flx --obj-dir build\benchmarks\parallel_cpu_emit
```

Build and run with MSVC on Windows:

```powershell
dotnet run --project src\Flx.Compiler\Flx.Compiler.csproj -- examples\benchmarks\parallel_cpu.flx -o build\benchmarks\parallel_cpu.exe --cc-mode msvc

$env:FLX_WORKERS = "1"
build\benchmarks\parallel_cpu.exe

$env:FLX_WORKERS = "8"
build\benchmarks\parallel_cpu.exe
```

Use the same executable for both runs. `FLX_WORKERS` is read by the generated Windows runtime. If it is unset, the runtime requests the logical CPU count. The runtime may clamp the worker count to the number of batches and to its current 64-worker limit. Non-Windows generated runtime currently runs the parallel loop serially, so worker comparisons are only meaningful on Windows.
