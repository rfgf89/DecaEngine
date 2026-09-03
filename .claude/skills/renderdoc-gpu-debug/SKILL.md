---
name: renderdoc-gpu-debug
description: Capture and inspect GPU frames with RenderDoc via the rdc-cli command line (Vulkan, D3D11, D3D12, OpenGL) — pipeline state, bindings, shader source and constants, pixel history, shader debugging, edit-replay, capture diffing. Use when a draw renders nothing, geometry or colors are wrong, shadows misbehave, or a frame needs inspecting at the API level and the headless probes cannot say why.
---

# RenderDoc GPU debugging

Adapted from [rudybear/renderdoc-skill](https://github.com/rudybear/renderdoc-skill) (MIT) for DecaEngine.

`rdc-cli` is a 66-command CLI over RenderDoc's Python API. See `references/commands-quick-ref.md` for every command and flag, and `references/debugging-recipes.md` for extended recipes with expected output shapes.

## When to reach for this

The headless probes (`render-verification` skill) tell you **that** a frame changed and by how much. RenderDoc tells you **why** — which draw, which binding, which pixel, which shader line. Use probes first: they are faster, scriptable and already have baselines. Escalate here when a probe metric moves and the cause is not obvious from source.

For DecaEngine's own known silent failures — empty draws on Vulkan, dead knobs, PSO cache poisoning, device removal — check the `gpu-binding-traps` skill first. Several of those are diagnosable in one run with an env var and never need a capture.

## Prerequisites — already set up on this machine

```bash
rdc doctor      # all checks pass
```

Working configuration (built 2026-09-04, verified end to end):

- `C:\Users\rfgf89\renderdoc-py\` holds `renderdoc.pyd`, `renderdoc.dll`, `renderdoccmd.exe`, `d3dcompiler_47.dll` — all RenderDoc **1.46**, built from source against **Python 3.12**.
- `RENDERDOC_PYTHON_PATH` points there (user env var); that dir and the pip Scripts dir are on the user PATH.
- `rdc-cli` 0.6.3 installed via pip.

**Why a source build was needed.** No official RenderDoc binary ships `renderdoc.pyd` — it is only produced by a source build. The shipped builds embed Python 3.8 (1.46) or 3.6 (1.44), while `rdc-cli` requires ≥3.10, and the module must be imported by exactly the Python version it was compiled against.

To rebuild after a RenderDoc upgrade: extract the source, build `renderdoc\3rdparty\breakpad\**` (four projects), then
`msbuild qrenderdoc\Code\pyrenderdoc\pyrenderdoc_module.vcxproj -p:Configuration=Release -p:Platform=x64 -p:SolutionDir=<src>\ -p:VSPythonOverridePath=<pydir>`
where `<pydir>` contains `include\Python.h`, `python312.zip` and `python312.lib` (the python.org embeddable zip plus headers and the import lib from a full install). Building the `.vcxproj` directly does **not** pull in solution-level dependencies — that is why breakpad must be built first.

**Local patch.** `rdc-cli` 0.6.3 calls `rd.GetDefaultCaptureOptions()`, which RenderDoc 1.46 removed in favour of the `rd.CaptureOptions()` constructor. Two call sites in `site-packages/rdc/capture_core.py` are patched with a `hasattr` fallback. **Reapply this after any `pip install -U rdc-cli`** or capture will fail with `AttributeError`.

## Session lifecycle

```bash
rdc open <capture>.rdc     # start daemon, load capture
# ... inspection ...
rdc close                  # release resources, stop daemon
```

One capture per session (`--session name` for parallel sessions); `rdc status` shows state. **Always close** — leaked daemons hold GPU memory.

## Capturing DecaEngine

CWD matters: the editor resolves `EditorAssets` relative to the working directory, exactly as the probes do.

```bash
cd DecaEngine.Editor.App/bin/x64/Debug/net10.0
rdc capture -o C:/Users/rfgf89/DecaEngine/rdc-captures/frame.rdc -- \
    ./DecaEngine.Editor.App.exe --preview-probe EditorAssets/models/Sponza.gltf <outDir>
```

Useful flags: `--frame N`, `--timeout S`, `--api-validation`, `--ref-all-resources`, `--wait-for-exit`, `--trigger` (inject only, then `rdc capture-trigger`).

**Vulkan** needs the layer registered: `HKCU\SOFTWARE\Khronos\Vulkan\ImplicitLayers` must contain RenderDoc's `renderdoc.json` (DWORD 0), and `ENABLE_VULKAN_RENDERDOC_CAPTURE=1` must be set. For D3D12 runs pass `DECA_PROBE_BACKEND=d3d12` in the same command.

**Frame boundaries are present calls.** A headless run with no swapchain cannot be captured. Capture the editor with its window, or use `--trigger` mode.

Keep captures out of git — write them to a path already ignored (e.g. `rdc-captures/`), never into the repo tree next to the probe outputs.

## Exploring a frame

```bash
rdc info --json           # API, GPU, driver, resolution, frame number
rdc stats --json          # per-pass breakdown, top draws, largest resources
rdc passes                # render passes (debug markers)
rdc draws --limit 20      # first 20 draws
rdc draws --pass "Shadow" --json
rdc events --limit 50     # all API events, not just draws
rdc ls /textures -l       # VFS browsing
```

## Pipeline, bindings, shaders

```bash
rdc pipeline EID --json              # full state; or a section:
rdc pipeline EID rasterizer --json   # cull, fill, depth bias
rdc pipeline EID viewport --json
rdc pipeline EID blend --json
rdc pipeline EID vs|ps|cs|gs --json  # shader stages
# NOTE: the upstream docs list rs/om/ia/ds — those are rejected as
# "invalid section" by rdc-cli 0.6.3. Verified working names are above.

rdc bindings EID --json          # all bound resources
rdc bindings EID --set 0 --json  # one descriptor set

rdc shader EID ps --source       # debug source, if present
rdc shader EID ps --reflect --json
rdc shader EID ps --constants --json
rdc shader EID ps --target spirv
rdc search "shadow" --stage ps   # regex over shader disassembly
```

## Visual inspection: export → Read → analyze

Export to PNG, then **view it with the Read tool** — it renders images. Never `cat` a PNG.

```bash
rdc rt EID -o C:/Users/rfgf89/DecaEngine/rdc-captures/analysis/rt.png
rdc rt EID --target 1 -o .../rt_mrt1.png
rdc texture RESID -o .../tex.png
rdc rt EID --overlay wireframe -o .../wireframe.png
```

Then correlate the image with `rdc pipeline EID` and `rdc shader EID ps --constants`.

## Pixel debugging

```bash
rdc pixel X Y --json             # history: every draw that touched the pixel
rdc pick-pixel X Y EID --json    # current color
rdc debug pixel EID X Y --json   # shader in/out summary
rdc debug pixel EID X Y --trace  # full execution trace
rdc debug pixel EID X Y --dump-at 42
rdc debug vertex EID VTXID --trace
rdc debug thread EID GX GY GZ TX TY TZ --json
```

Pixel history entries carry `passed` and `failure_reason` (`depth_test`, `stencil_test`) — that alone answers most "why is it invisible" questions.

## Shader edit-replay

Change a shader without rebuilding the engine:

```bash
rdc shader-encodings --json
rdc shader EID ps --source -o .../shader.frag
# edit the file
rdc shader-build .../shader.frag --encoding GLSL --stage ps --json
rdc shader-replace EID ps --with SHADER_ID --json
rdc rt EID -o .../after_edit.png
rdc shader-restore EID ps      # or: rdc shader-restore-all
```

Useful as a faster loop than the engine's own shader A/B (swapping a compiled shader in `bin`), but the engine's route is what proves the fix in a real run.

## Comparing captures

```bash
rdc diff a.rdc b.rdc --shortstat
rdc diff a.rdc b.rdc --draws --json
rdc diff a.rdc b.rdc --passes --json
rdc diff a.rdc b.rdc --framebuffer --diff-output .../diff.png
rdc diff a.rdc b.rdc --pipeline EID --json
```

## Output size discipline

Captures produce enormous output. Always:
- `--limit` when exploring (`rdc draws --limit 20`)
- filter by pass instead of listing everything
- `-q` for bare ID lists
- a pipeline **section** (`rdc pipeline EID rs`) rather than the full state
- TSV (the default) for scanning; `--json` only when you need structure

Rough sizes: `rdc info` ~20 lines; `rdc draws --limit 20` ~25; `rdc pipeline EID --json` 200-500 (full) vs ~50 (section); `rdc debug pixel --trace` 100-1000.

## Troubleshooting

| Problem | Action |
|---|---|
| `rdc` not found | `pip install rdc-cli` |
| `rdc doctor` fails | check `RENDERDOC_PYTHON_PATH` → dir with `renderdoc.pyd` + `renderdoc.dll` |
| capture fails, "no swapchain" | `rdc capture --trigger -- app`, then `rdc capture-trigger` |
| daemon not responding | `rdc status` → `rdc close` → `rdc open` |
| counters/pixel history unsupported | `rdc gpus --json`, `rdc counters --list` (empty = unavailable) |
