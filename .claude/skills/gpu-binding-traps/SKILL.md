---
name: gpu-binding-traps
description: Known silent failure modes of the Diligent/D3D12/Vulkan layer in DecaEngine — resource binding, PSO cache, structured buffers, device removal — plus the diagnostic ladders that found them. Use when a draw renders nothing, a knob has no effect, the app AVs in a native call, or when adding GPU buffers, samplers, compute shaders or pipeline states.
---

# GPU binding and PSO traps

Every trap below cost hours because the failure is silent and surfaces far from its cause. Check this list *before* descriptor-level archaeology.

## Fast triage

| Symptom | Suspect first |
|---|---|
| Draw renders nothing on **Vulkan only** | compute stage flag in a graphics layout (Trap 1) |
| Knob compiles, runs, changes nothing; frames bit-identical | PSO disk cache serving a stale root signature (Trap 5) |
| `0xC0000005` in a native call, no diagnostics | null/invalid PSO from the disk cache (Traps 2, 3) |
| Works on Vulkan, absent on **D3D12** | matrix majorness in a structured buffer (Trap 4) |
| Device removed; every later resource creation fails | read-only `StructuredBuffer` in a compute shader (Trap 6) |

**`DECA_PSO_CACHE=0` settles any cache suspicion in one run.** If the behaviour appears, the cache is the culprit.

## The traps

**1 — Compute stage flag in a graphics PSO layout kills ALL Vulkan bindings.** A buffer created `HandleAccess.Compute | HandleAccess.Pixel` drags `ShaderType.Compute` into the graphics resource layout; on Vulkan this silently breaks binding of *other* variables in the set. Validation says `VUID-...-08114: "X" has never been updated`; D3D12 is unaffected. Grep validation output for "has never been updated" first.

**2 — D3D12 disk PSO cache poisons compute PSOs across processes.** A cached compute PSO comes back non-null but internally invalid: no error, no null, and binding it AVs. Compute PSOs build in milliseconds — they are not attached to the cache for this reason.

**3 — …and poisons graphics PSOs when a NAME repeats within one process.** The cache keys on PSO name. Registering a second pipeline under a name already used this run returns a non-null invalid PSO. This surfaced only once features became runtime-toggleable (post-process classes rebuild materials under fixed literals like "SSAO Material"). The cache is routed only for a name's first creation per process.

**4 — `float4x4` inside a `StructuredBuffer` has backend-dependent majorness.** `PackMatrixRowMajor` governs constant buffers only; it does not reach structured-buffer elements. D3D12 delivers them transposed, Vulkan does not. A blind `transpose()` fixes one backend and breaks the other. **Rule: never put a matrix in a structured-buffer element** — declare `StructuredBuffer<float4>`, store 4 rows, rebuild with `float4x4(r0,r1,r2,r3)`, and create the buffer as `CreateBuffer<Vector4>(n * 4)` so the view stride matches.

**5 — The PSO disk cache serves stale immutable samplers.** Immutable samplers live in the root signature inside the cached blob, so a name hit returns the first session's samplers forever — every sampler knob becomes a silent no-op, and even dynamic view-samplers are shadowed. Mitigated by hashing the sampler signature and variable layout into the PSO name (`HashCode` is process-randomized and unusable as a key), plus a null-PSO guard that recreates without the cache.

**6 — Read-only `StructuredBuffer` in a compute shader kills the D3D12 device.** `DiligentComputeMaterial.SetBuffer` binds a UAV view only. Vulkan compiles both `StructuredBuffer` and `RWStructuredBuffer` to one `STORAGE_BUFFER` so the mismatch is invisible; on D3D12 they are different descriptor types and the UAV in an SRV slot reads a garbage address. **Rule: in engine compute shaders declare ALL structured buffers `RWStructuredBuffer`, even read-only ones.**

## Rules when adding GPU resources

- Shared CS-write/PS-read buffer: create with `HandleAccess.Compute | HandleAccess.Pixel`, declare `RWStructuredBuffer` in the CS and `StructuredBuffer` in the PS.
- A texture slot declared unconditionally in a shader must ALWAYS have something bound, or Vulkan fails VUID-08114 — placeholder-bind it.
- After `IGraphicsApi.UpdateTexture2D`, transition the texture back to `ShaderResource` explicitly or Vulkan fails VUID-vkCmdDraw-None-09600.
- `new SamplerDesc{}` zero-inits `MaxLOD`, clamping sampling to mip 0; native Diligent defaults it to `+FLT_MAX`. Set MinLOD/MaxLOD explicitly.
- Hardware ray tracing compiles on D3D12 only; on Vulkan `RaytracingAccelerationStructure` reaches Diligent's own HLSL parser instead of DXC and fails.

## Diagnosing device removal

After the device dies, *nothing* is created — the log fills with `Failed to create D3D12 buffer/texture` for trivial allocations and the process AVs in the next native call, where the culprit is long gone.

- Test whether the device is dead: try creating a 4×4 RGBA8 texture. If that fails too, the problem is not your descriptor.
- Pin the frame: `Flush()` + `WaitForIdle()` plus that check after each stage. **Death lands on the first flush after the guilty command, not on the command itself.**
- Absence of `nvlddmkm` / Xid events in the system log disproves nothing.

## Diagnostic ladder for "the knob does nothing"

1. Hash output PNGs across config A/B — bit-identical means it never reached the GPU.
2. Drive the knob to an absurd extreme; it must be visible.
3. `DECA_TEX_DIAG=1` — real mip counts of created textures.
4. `DECA_MAT_DIAG=1` — SetSampler calls with desc values; `=2` traces every PSO bind with the material name.
5. `DECA_PSO_CACHE=0` — if behaviour appears, it is the cache.

## Discipline

These bugs punish guessing. When a theory and a location disagree, **A/B the suspect forced on and off** — one trap above burned hours on shadow cascade staggering purely because that was where the fault happened to surface in the first stack trace.
