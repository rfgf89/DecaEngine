---
name: render-verification
description: How to build DecaEngine correctly and PROVE a rendering change works — Verify.ps1, headless probes, A/B method, baselines. Use whenever changing renderer, shader, probe GI, shadow, upscaler, physics or animation code, or when asked whether a graphics change works.
---

# Verifying a rendering change

A change that compiles proves nothing. Rendering failures here are almost always silent: the frame still renders, the knob is just a no-op. Every graphics change needs a measurement that would have gone red if the change were wrong.

## Build

```
dotnet build DecaEngine.sln -c Debug -p:Platform=x64
```

Diligent refuses AnyCPU — **always pass `-p:Platform=x64`**.

**Path trap:** `-p:Platform=x64` writes to `bin\x64\Debug\net10.0`. A stale full set also sits in `bin\Debug\net10.0`, where the exe updates but `DecaEngine.Graphics.Diligent.dll` does not. Running from there silently executes yesterday's code — the tell is stack line numbers that don't match the source. Run only from `bin\x64\Debug\net10.0`.

Editor entry point: `DecaEngine.Editor.App\bin\x64\Debug\net10.0\DecaEngine.Editor.App.exe`.

## The safety net

```
.\tools\Verify.ps1
```

Build + tests + 8 probe scenarios / ~238 metrics against `tools/baselines/*.metrics`, ~50 s. Run it after **every** step of a refactor. Red means the step was not mechanical.

Rewrite baselines with `-Baseline` **only** on a tree you have independently confirmed correct. Rewriting on a broken tree legalises the breakage.

## Probes

Run from the bin dir — probes need `EditorAssets` next to the exe:

```
cd DecaEngine.Editor.App\bin\x64\Debug\net10.0
.\DecaEngine.Editor.App.exe --preview-probe EditorAssets/models/Sponza.gltf <absOutDir> [subMesh] [zoom] [yaw]
```

`DECA_PROBE_*` env vars drive the A/B knobs (PROBEGI, SSAO, SSGI, SHADOWS, HDR, BACKEND, SSR, TAAU, VOLUMETRIC, ANIMCLIP, PHYSICS, GAMEPLAY…). **Env vars do not persist between PowerShell tool calls** — set them in the *same* command as the exe, or your "baseline" quietly renders a different view.

Interior Sponza framing for lighting A/B (orbit framing only ever shows the outer wall):
`DECA_PROBE_EYE=-0.5,3,0.4 DECA_PROBE_TARGET=30,4,0.4`

Live feature toggling (no environment recreate) has its own harness:
`DECA_LOOP_TOGGLE=1 .\DecaEngine.Editor.App.exe --full-loop EditorAssets/models/Sponza.gltf 1700 d3d12`

## Method

1. **A/B, never single-shot.** One rendered frame proves the pass ran, not that it is correct. Pick a pair where the expected difference is structural: volumetric `VOLSHADOW=0` (uniform haze) vs `1` (haze confined to sun-lit space), not volumetric off vs on.
2. **Hash the PNGs first.** Bit-identical output across a config change means the knob never reached the GPU — that is a wiring bug, not a small effect. Do not start tuning numbers before this check.
3. **Drive the knob to an absurd extreme.** If `MIPBIAS=4` does not visibly blur, the knob is dead.
4. **A green suite means nothing until you have seen it go red.** Verify a new check by mutating the thing it guards (flip a bitangent sign, change a bounce coefficient) and confirming it fails.
5. **Read both cost and convergence.** `ms/dispatch` and `rounds run / skipped by fence` diverge wildly — a 23× faster dispatch once bought only 1.5× convergence.
6. **Intermittent failures need repeats.** `Timeout elapsed while waiting for the frame waitable object` on stderr precedes device removal; one clean run proves nothing.

## Metrics

- `[probe] lighting: ... lum avg=` is the practical regression metric.
- `probe-gi field:` averages the whole atlas — **not** comparable once the atlas has unused padding.
- Sharpness/upscaler quality: mean|grad| is WRONG for TAA (it confounds detail with aliasing energy TAA legitimately removes). Use SAD against a full-res reference over converged frames.

## A/B against uncommitted work

Copy the changed files aside, `git checkout --` them, build, run, copy back — then reset `LastWriteTime`, because `Copy-Item` preserves timestamps and MSBuild will skip the recompile.
