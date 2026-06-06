# FLX Demos

## Windowed Parallel Agents

`examples/demos/windows_parallel_agents.flx` is the first windowed demo that combines the current scheduler pieces:

- a Win32 window and message loop
- 50,000 prefab objects
- numeric prefab fields for position and velocity
- automatic parallel execution of the mutating `UpdateAgent(Agent)`
- frame update timing and average timing logs
- background file loading through `flx_task_read_file_start`
- simple GDI point rendering

The parallel workload is the scheduled `UpdateAgent(Agent)` step. It reads each agent id, performs CPU-heavy local integer work, and updates that agent's own `x`, `y`, `vx`, and `vy` fields. The render step is serial GDI drawing over the first visible subset of agents, and the demo does not claim GPU acceleration.

Build and run from the repository root with MSVC:

```powershell
dotnet run --project src\Flx.Compiler\Flx.Compiler.csproj -- examples\demos\windows_parallel_agents.flx -o build\demos\windows_parallel_agents.exe --cc-mode msvc -l user32 -l gdi32

$env:FLX_WORKERS = "1"
build\demos\windows_parallel_agents.exe

$env:FLX_WORKERS = "8"
build\demos\windows_parallel_agents.exe
```

The executable runs a bounded number of frames and exits on its own. You can also close the window. Run from the repository root so the background loader can find `docs/scheduler.md`.

Current limitations:

- The parallel step is CPU simulation work, not rendering.
- Rendering uses Win32 GDI for simplicity.
- The non-Windows generated runtime still serializes `flx_parallel_for`.
