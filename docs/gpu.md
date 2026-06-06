# FLX GPU and Render Roadmap

This document sketches a pragmatic GPU/render direction for FLX without committing
the compiler to GPU syntax before the runtime shape is proven.

Current state:

- FLX lowers to generated C.
- The executable flow is a single `schedule` block with `run`, labels, and
  `loopto`.
- Prefab-parameter schedule steps may become CPU `flx_parallel_for` jobs when
  the compiler proves they are safe enough.
- The generated runtime owns CPU-side `flx_world` storage, strings, arrays,
  asynchronous file tasks, and a Windows thread implementation with a serial
  fallback outside Windows.
- There is no GPU syntax, shader pipeline, resource state tracking, swapchain,
  barrier planner, or GPU readback model yet.

The first GPU work should therefore be backend-neutral runtime design and small
demos. Compiler-owned GPU scheduling should come after the runtime API has
survived real examples.

## Goals

- Keep FLX source independent of D3D12, Vulkan, WebGPU, and shader language
  details.
- Model buffers, images, passes, barriers, queue submission, and latency
  explicitly enough that the compiler can reason about them later.
- Make asynchronous GPU execution the default mental model.
- Treat CPU readback as explicit, delayed, and fenced. Same-frame readback must
  not be promised by default.
- Prefer external shader files first, with a simple manifest/reflection step,
  before considering inline shader syntax.

## Backend-neutral Objects

Use backend-neutral names at the FLX/runtime design level. Backend-specific
types stay behind the runtime.

- `gpu_device`: selected adapter/device plus backend capabilities.
- `gpu_queue`: queue used for transfer, compute, graphics, or a combined first
  implementation.
- `gpu_buffer`: linear GPU resource with size, stride/alignment metadata, memory
  class, and usage flags.
- `gpu_image`: dimensional image resource with width, height, depth/layers,
  format, mip count, sample count, usage flags, and current presentation role.
- `gpu_sampler`: filtering and addressing state.
- `gpu_shader`: compiled shader module plus entry point metadata.
- `gpu_pipeline`: compute or graphics pipeline created from shaders, layouts,
  formats, and fixed-function state.
- `gpu_bindings`: concrete buffer/image/sampler bindings for one dispatch or
  draw.
- `gpu_command_list`: recorded passes and copies for one submission.
- `gpu_fence`: completion token for submitted GPU work.

Suggested `gpu_buffer` usage flags:

- `copy_src`
- `copy_dst`
- `uniform`
- `storage_read`
- `storage_write`
- `vertex`
- `index`
- `indirect`
- `readback`

Suggested `gpu_image` usage flags:

- `copy_src`
- `copy_dst`
- `sampled`
- `storage_read`
- `storage_write`
- `render_target`
- `depth_stencil`
- `present`

Memory classes should start small:

- `device`: GPU-local, fastest for GPU use, not directly CPU-visible.
- `upload`: CPU-written staging memory for transfers into device resources.
- `readback`: CPU-readable staging memory populated by explicit GPU copies.

## Pass Model

The runtime should expose explicit pass kinds even if the first backend records
all work into one backend command list.

### Transfer Pass

A `transfer_pass` copies data and changes ownership between CPU-visible staging
resources and device resources.

Examples:

- upload bytes into a `gpu_buffer`
- upload pixels into a `gpu_image`
- copy buffer-to-buffer
- copy image-to-image
- copy image-to-buffer for readback

Transfer passes are the only default path for CPU/GPU data movement. CPU writes
to `upload` memory do not make device resources visible until a transfer pass
has been submitted and completed far enough for dependent GPU work.

### Compute Pass

A `compute_pass` binds a compute `gpu_pipeline`, bindings, and dispatch shape.
It declares which `gpu_buffer` and `gpu_image` resources are read, written, or
read-written.

The important first use case is data-parallel work over buffers that resembles
the existing CPU prefab schedule path but does not share its safety rules. CPU
`parallel` annotations for imported C functions are not GPU effect annotations.

### Graphics Pass

A `graphics_pass` binds render targets, optional depth/stencil, viewport/scissor,
graphics pipeline, bindings, vertex/index buffers, and draw commands.

Render target load/store behavior must be explicit:

- `clear`
- `load`
- `discard`
- `store`
- `present`

The first render path can be intentionally narrow: one color render target, no
MSAA, no complex render graph, and one swapchain image transition per frame.

## Latency Annotations

GPU work is asynchronous relative to the CPU. The API and future compiler
surface should make latency visible instead of hiding synchronization in ordinary
reads.

Start with runtime-level annotations and metadata:

- `cpu_to_gpu`: data is visible to GPU work after the upload transfer and the
  dependent GPU pass ordering.
- `gpu_to_gpu`: writes are visible to later GPU passes after barriers inside the
  same submission or across ordered submissions.
- `gpu_to_cpu`: data becomes visible to CPU code only after an explicit readback
  copy and fence completion.
- `present`: image ownership moves to presentation; CPU access is not implied.

Default policy:

- GPU submissions return a `gpu_fence` or frame token.
- CPU code can poll, wait, or attach continuation-style runtime work.
- Readback APIs return a delayed result tied to a fence.
- Same-frame readback is not a normal guarantee. A debug-only forced wait can
  exist, but it must be named like a stall, for example `gpu_wait_for_readback`,
  so the cost is obvious.

Future FLX syntax can build on this with annotations such as "available next
frame" or "requires fence", but the first runtime should not need new syntax.

## Resource Barriers

Resource state must be tracked explicitly enough that D3D12 and Vulkan are
correct and WebGPU does not hide hazards from the FLX model.

Minimum states:

- `undefined`
- `copy_src`
- `copy_dst`
- `shader_read`
- `shader_write`
- `render_target`
- `depth_read`
- `depth_write`
- `present`
- `cpu_upload`
- `cpu_readback`

Track state per resource first, then per image subresource when mips/layers are
added. The first implementation can be conservative and insert barriers at pass
boundaries. Later implementations can merge passes and remove redundant
transitions.

Pass declarations should include:

- resources read
- resources written
- resources read-written
- required queue type
- expected beginning and ending resource states

The compiler should eventually validate obvious hazards, but the first runtime
can own barrier planning while the public API forces callers to declare usage.

## Shader Source Strategy

Keep shader source outside `.flx` files first.

Initial strategy:

- Use external `.hlsl` files for the D3D12 path.
- Use external `.glsl` files for the Vulkan path when that backend starts.
- Treat WebGPU shader source as external too; decide later whether that means
  WGSL directly or generated WGSL from another source.
- Add a small shader manifest that names source file, entry point, stage,
  expected bindings, and target backend/profile.
- Compile shaders as part of the build/demo step and pass bytecode or validated
  source to the runtime.
- Do not embed shader bodies in FLX function raw blocks.

This keeps shader tooling close to existing platform compilers and avoids
inventing a shader language before there is a renderer to test against.

## Backend Tradeoffs

### D3D12

Best first fit for the current repo because the examples and build support are
already Windows/MSVC-oriented.

Pros:

- Natural Visual Studio demo path.
- Good HLSL/DXC support.
- Explicit barriers and queues match the desired FLX model.
- Debug layer and PIX give strong diagnostics.

Cons:

- Windows-only.
- Verbose setup for devices, swapchains, descriptors, and synchronization.
- Easy to overfit the abstraction to descriptor heaps if the first API is too
  detailed.

### Vulkan

Best second backend if the goal is portable native rendering.

Pros:

- Cross-platform native API.
- Explicit synchronization and layouts match the barrier model.
- SPIR-V tooling and validation are mature.

Cons:

- Loader, instance/device setup, surface integration, and swapchain management
  add more platform work.
- Shader source and reflection decisions need care.
- Validation is excellent but the initial amount of code is high.

### WebGPU

Best portability target after the core model is stable.

Pros:

- Strong browser story and a safer API shape.
- More constrained resource model can validate the abstraction.
- Good fit for demos that need to run without native toolchains.

Cons:

- Native C integration is less direct than D3D12 in the current generated-C
  pipeline.
- Browser execution has presentation, file access, and threading constraints.
- Some synchronization is intentionally abstracted, so FLX should keep its own
  latency/resource model clear instead of relying on backend behavior.

## Recommended First Backend and Demo Path

Start with a D3D12 backend and a narrow demo because that matches the current
Windows/MSVC generated-C path.

Phase 1: runtime skeleton

- Add a C-facing GPU runtime layer outside the compiler surface.
- Create `gpu_device`, one graphics-capable `gpu_queue`, `gpu_buffer`,
  `gpu_image`, `gpu_command_list`, and `gpu_fence`.
- Support transfer passes, compute passes, and a minimal graphics pass.
- Keep all shader source external.
- Make readback explicit and fence-driven.

Phase 2: compute-first proof

- Load an external HLSL compute shader.
- Create a device-local `gpu_buffer`.
- Upload initial data through an `upload` buffer and transfer pass.
- Dispatch compute.
- Optionally copy to a `readback` buffer and poll/wait on a fence for debug
  output.
- Do not wire this to ordinary FLX field reads.

Phase 3: minimal render proof

- Create a window-backed swapchain.
- Upload a static vertex buffer.
- Draw a triangle or points into the swapchain image.
- Add a compute pass that updates a buffer consumed by the graphics pass.
- Present the image without CPU readback.

Phase 4: compiler integration candidate

- Generate C calls into the GPU runtime from a narrow, documented FLX surface.
- Let schedule steps enqueue GPU passes, then submit at explicit boundaries.
- Add metadata diagnostics for resource usage, barriers, and readback latency.
- Keep CPU prefab scheduling and GPU dispatch scheduling separate until the
  compiler has enough effect information to safely combine them.

## Non-goals for the First Slice

- No same-frame readback promise by default.
- No inline shader language in FLX.
- No backend-specific FLX types such as `d3d12_buffer` or `vk_image`.
- No automatic migration of CPU prefab functions to GPU kernels.
- No render graph optimizer before simple pass ordering and barriers work.
- No attempt to make `parallel alias.member;` describe GPU safety.

