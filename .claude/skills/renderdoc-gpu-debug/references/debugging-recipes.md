# GPU debugging recipes

Extended workflows with expected output shapes. Adapted from [rudybear/renderdoc-skill](https://github.com/rudybear/renderdoc-skill) (MIT); the author's project-specific examples are replaced with DecaEngine ones.

Before reaching for any of these, check `gpu-binding-traps` — several DecaEngine failures look exactly like the symptoms below but are diagnosable in one run with an env var.

## Recipe 1: object is invisible

```bash
rdc open rdc-captures/frame.rdc
rdc draws --limit 50 --json
rdc draws --pass "Main" --json          # if you know the marker
rdc pipeline EID rs --json              # rasterizer
```

Expected rasterizer shape:
```json
{
  "CullMode": "Back",
  "FrontFace": "CounterClockwise",
  "DepthClipEnable": true,
  "FillMode": "Solid",
  "Viewports": [{"X":0,"Y":0,"Width":512,"Height":512,"MinDepth":0.0,"MaxDepth":1.0}]
}
```

Common causes: `CullMode` opposite to the geometry's winding; zero-sized viewport; scissor clipping; depth test rejecting it; `ColorWriteMask: 0`; vertex transform putting it off-screen.

```bash
rdc pipeline EID ds --json              # depth state
rdc draw EID --json                     # VertexCount / InstanceCount > 0?
rdc debug vertex EID 0 --json           # is SV_Position inside NDC [-1,1]?
rdc close
```

**DecaEngine notes.** Mesh winding is passed through unchanged to Bepu and the renderer, so an inverted mesh shows as one-sided geometry rather than a cull-state bug. `DepthClipDisable` is deliberately set on some paths (it smears objects sitting flush against a lamp — see the punctual-rotation work). And if the draw is empty **on Vulkan only**, stop here and read `gpu-binding-traps` trap 1: a compute stage flag in a graphics layout silently unbinds everything.

## Recipe 2: colors are wrong

```bash
rdc open rdc-captures/frame.rdc
rdc rt EID -o rdc-captures/analysis/wrong_color.png    # then view with the Read tool
rdc pick-pixel 256 256 EID --json
```

```json
{"x":256,"y":256,"r":0.502,"g":0.0,"b":0.0,"a":1.0}
```

```bash
rdc bindings EID --json                 # is the right texture bound?
rdc texture RESID -o rdc-captures/analysis/bound_tex.png
rdc shader EID ps --constants --json    # material factors
rdc pipeline EID om --json              # blend state
```

Expected output-merger shape:
```json
{
  "BlendState": {"Blends":[{"Enabled":false,"Source":"One","Destination":"Zero","Operation":"Add","ColorWriteMask":15}]},
  "DepthStencilState": {"DepthEnable":true,"DepthFunc":"LessEqual"}
}
```

```bash
rdc debug pixel EID 256 256 --trace
rdc close
```

Common causes: wrong texture bound; wrong constant-buffer material color; additive blend where alpha was intended; sRGB vs linear mismatch; hardcoded color in the shader.

**DecaEngine notes.** glTF convention is G=roughness, B=metallic, linear; albedo is decoded with `pow(2.2)` in step with `UnlitInstancedPS.hlsl`. If a sampler-related knob appears to do nothing **and frames are bit-identical**, it is the PSO disk cache serving a stale root signature, not a color bug — `DECA_PSO_CACHE=0` settles it.

## Recipe 3: shadows are broken

```bash
rdc open rdc-captures/frame.rdc
rdc passes --json
SHADOW_EID=$(rdc draws --pass "Shadow*" -q | tail -1)
rdc rt $SHADOW_EID -o rdc-captures/analysis/shadow_map.png
```

View the map with the Read tool and check resolution, coverage (silhouettes present?) and depth range.

```bash
rdc bindings $SHADOW_EID --json         # render target dimensions
rdc pipeline $SHADOW_EID rs --json      # depth bias
```

```json
{"DepthBias": 1.25, "SlopeScaledDepthBias": 1.75, "DepthBiasClamp": 0.0}
```

```bash
LIGHT_EID=$(rdc draws --pass "Raster*" -q | head -1)
rdc shader $LIGHT_EID ps --source            # shadow sampling, PCF kernel
rdc shader $LIGHT_EID ps --constants --json  # light matrices, bias, map size
rdc debug pixel $LIGHT_EID 300 400 --trace
rdc close
```

| Symptom | Likely cause | Check |
|---|---|---|
| Blocky | low shadow-map resolution | `bindings` → RT dimensions |
| Acne | depth bias too low | `pipeline rs` → DepthBias |
| Peter-panning | depth bias too high | `pipeline rs` → DepthBias |
| Missing entirely | map never sampled | `bindings` on the lighting draw |
| Wrong direction | light matrix wrong | constants → lightViewProj |
| Hard edges | no PCF / radius 0 | shader source |

**DecaEngine notes.** The live sun path is in `UnlitInstancedPS.hlsl` (`Shadows.hlsl` is dead). `SpotAngles.w` is the **tangent** of the sun half-angle, not the angle. Punctual shadow matrices ride in a `StructuredBuffer<float4>` as four rows precisely because `float4x4` elements have backend-dependent majorness — if punctual shadows work on Vulkan and vanish on D3D12, that is trap 4, not a bias problem. Cascade `w > 1e-4` guards silently swallow negative `shadowClip.w`, which looks identical to "no light here" in the debug channel.

## Recipe 4: performance is bad

```bash
rdc open rdc-captures/frame.rdc
rdc stats --json                        # totals and per-pass breakdown
rdc passes --json                       # excessive draws in one pass?
rdc counters --list                     # if empty, the GPU/driver has none
rdc counters --name "duration" --json
rdc resources --type Texture --sort size --json
rdc rt EID --overlay wireframe -o rdc-captures/analysis/wireframe.png
rdc close
```

Red flags: >1000 draws in a pass (batch or instance them); textures >32 MB (compress, mip); passes repeating identical draws; many small draws with state changes between each.

**DecaEngine note.** For GI and upscaler cost, the engine's own numbers are better: `ms/dispatch` and `rounds run / skipped by fence` from the probe output. Dispatch cost and convergence rate diverge wildly — a capture shows only the former.

## Recipe 5: what changed between two frames

```bash
cd DecaEngine.Editor.App/bin/x64/Debug/net10.0
rdc capture -o rdc-captures/before.rdc -- ./DecaEngine.Editor.App.exe <args>
# make the change, rebuild
rdc capture -o rdc-captures/after.rdc -- ./DecaEngine.Editor.App.exe <args>

rdc diff rdc-captures/before.rdc rdc-captures/after.rdc --shortstat
rdc diff ... --draws --json
rdc diff ... --passes --json
rdc diff ... --framebuffer --diff-output rdc-captures/analysis/diff.png
rdc diff ... --pipeline EID --json
```

**DecaEngine note.** For an A/B of engine settings this is the slow route — `DECA_PROBE_*` knobs plus `tools/Verify.ps1` cover it with baselines. Use capture diffing when the metric moved but the *reason* is in API state: an unexpected extra pass, a changed binding, a different PSO.

## Recipe 6: debug this pixel

```bash
rdc open rdc-captures/frame.rdc
rdc pixel X Y --json
```

```json
[
  {"eid":42,"name":"DrawIndexed(360)","passed":true,
   "pre":{"r":0.0,"g":0.0,"b":0.0,"a":0.0},
   "post":{"r":0.8,"g":0.2,"b":0.1,"a":1.0},
   "depth_passed":true,"stencil_passed":true},
  {"eid":67,"name":"DrawIndexed(180)","passed":false,"failure_reason":"depth_test"}
]
```

```bash
rdc pick-pixel X Y 42 --json
rdc debug pixel 42 X Y --json
rdc debug pixel 42 X Y --trace
rdc debug pixel 42 X Y --dump-at 15
rdc pipeline 42 --json
rdc rt 42 -o rdc-captures/analysis/at_draw_42.png
rdc close
```

Interpretation: `failure_reason: depth_test` means it is behind something; `stencil_test` means a mask blocks it; a final color differing from the shader output means blending changed it; an empty history means nothing draws there at all — check viewport and scissor.
