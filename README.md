# DecaEngine

A game engine and editor in C# / .NET 10: rendering on top of [Diligent Engine](https://github.com/DiligentGraphics/DiligentEngine)
(D3D12 and Vulkan), Friflo ECS, Bepu 2 physics, ozz skeletal animation, window and input through SDL3,
editor UI on Dear ImGui (Hexa.NET). glTF 2.0 import (SharpGLTF) baked into a custom cooked format
(`.dmdl`, BC7 via BCnEncoder.Net), PBR materials with KHR extensions (transmission, volume, sheen,
specular, emissive_strength, texture_transform), cascaded and punctual shadows, SSAO/GTAO, SSGI, SSR
(including hardware ray tracing), probe GI, an HDR pipeline with auto exposure, bloom and color
grading, DLSS / FSR upscalers.

The code comments explain not "what" but "why": every non-obvious choice carries a measured reason
with it.

## Requirements

- Windows, .NET SDK 10 (see `global.json`), a GPU with D3D12 (editor) or Vulkan (probes).
- Build for `x64` **only**: Diligent does not work on AnyCPU, and a build without `-p:Platform=x64`
  quietly drops a stale kit into `bin\Debug` (see `tools/README.md`, "Traps").

## Build and run

```powershell
dotnet build DecaEngine.sln -c Debug -p:Platform=x64
.\DecaEngine.Editor.App\bin\x64\Debug\net10.0\DecaEngine.Editor.App.exe
```

The entry point is `DecaEngine.Editor.App`; the editor itself (`DecaEngine.Editor`) is a library so
that the probes can reference it.

## Verification

```powershell
.\tools\Verify.ps1              # build + unit tests + probe regression + editor startup (~1 min)
.\tools\Verify.ps1 -SkipProbes  # build and tests only
.\tools\Run-ProbeSuite.ps1 -Backend vulkan
```

There are no image-based tests: the probes (`DECA_PROBE_*`) print numbers - per-channel frame
luminance, the probe GI field, an animation report, physics state - and `Run-ProbeSuite.ps1` checks
them against the baselines in `tools/baselines/`. Details and the rules for updating baselines are in
`tools/README.md`.

## Probes from the command line

They run from the same exe, out of its own directory (`bin\x64\...`): the probes look for
`EditorAssets` next to themselves.

```powershell
.\DecaEngine.Editor.App.exe --preview-probe EditorAssets/models/Sponza.gltf <output directory>
.\DecaEngine.Editor.App.exe --preview-loop ...     # preview loop (streaming, switching)
.\DecaEngine.Editor.App.exe --full-loop            # both render graphs in EditorManager order
.\DecaEngine.Editor.App.exe --make-sample-prefab / --make-sample-project
```

Useful environment variables:

| Variable | What it does |
|---|---|
| `DECA_PROBE_BACKEND=d3d12\|vulkan` | probe backend (Vulkan by default for `--preview-probe`) |
| `DECA_PROBE_EYE=x,y,z`, `DECA_PROBE_TARGET=x,y,z` | probe camera |
| `DECA_PROBE_POINT=1`, `DECA_PROBE_GIGPU=1`, `DECA_PROBE_BLOOM=1` | point light / probe GI GPU path / bloom |
| `DECA_AUTOLOAD_MODEL=<path>`, `DECA_AUTOLOAD_PREFAB=<path>` | open a model/prefab in the editor right at startup |
| `DECA_LOOP_INSPECTOR=N` | every N frames switch the Inspector between prefab and model and print the next frame (repro for switching bugs) |
| `DECA_SHADER_CACHE` | shader bytecode cache (affects the compile/pso counters in probe output) |

## Projects

| Project | Contents |
|---|---|
| `DecaEngine.Core` | application loops, transforms, prefabs (`PrefabAsset`), `AssetRef`/`EntityRef`, unsafe collections, `EngineLog` |
| `DecaEngine.Graphics.Core` | graphics abstraction (`IGraphicsApi`, render graph, PSOs/materials), model import and baking, passes (Forward, Shadow, SSAO, SSGI, SSR, Bloom, Tonemap, Fog, Volumetric, Upscale) |
| `DecaEngine.Graphics.Diligent` | the Diligent backend: device, batch renderer with GPU culling and indirect draws, shadows, RT scene, DLSS/FSR |
| `DecaEngine.Graphics.ProbeGi` | probe GI: BVH, CPU/GPU trace rounds, SH atlases |
| `DecaEngine.Scene` | scene ECS components and systems (rendering, lights, shadows, animation, physics, gameplay) |
| `DecaEngine.Animation` | ozz runtime, humanoid avatar, foot IK |
| `DecaEngine.Physics.Bepu` | Bepu world, ragdoll |
| `DecaEngine.Editor` / `DecaEngine.Editor.App` | the editor (ImGui windows, preview and scene viewports, icon baking, model streaming, settings) and its exe |
| `DecaEngine.Probes` | the `DECA_PROBE_*` CLI harnesses |
| `DecaEngine.Tests` | xunit: pure mesh and probe ray math |
| `DecaEngine.ImGui.*`, `DecaEngine.Input.*`, `DecaEngine.Sdl3` | ImGui rendering on Diligent, input, SDL3 window |
| `DecaEngine.Core.Build`, `DecaEngine.Generator`, `DecaEngine.SourceGenerator` | building user projects (MSBuild/Roslyn), generators |
| `DecaEngine` | inherited DiligentNET samples (triangle, cube, instancing); unrelated to the editor |
| `Samples/AnimationSample`, `ExampleProject` | examples of a user project and a plugin |

The properties shared by all projects live in `Directory.Build.props`. The `probe*/`, `_*/` and
`cache.vulkan.shaders/` directories in the root are local probe and diagnostic output, ignored by git.

## License

MIT, see `LICENSE`.
