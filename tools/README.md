# A safety net for refactoring

One command for the whole cycle:

```powershell
.\tools\Verify.ps1              # build + unit tests + probe regression (~50 s)
.\tools\Verify.ps1 -SkipProbes  # build and tests only (~10 s), for checking as you go
```

A green `Verify.ps1` means: moving code between projects and splitting files did not change
behaviour. Red means the step was not mechanical - and shows exactly where.

## What it is made of

### 1. Unit tests (`DecaEngine.Tests`)

Pure logic: arrays in, arrays out, no graphics API and no files. What is covered is what is about to
be pulled out of the big files - so that the move has something to check it.

```powershell
dotnet test DecaEngine.Tests\DecaEngine.Tests.csproj -c Debug -p:Platform=x64
```

### 2. Probe regression (`Run-ProbeSuite.ps1`)

There are no render tests and there cannot be: asserting on an image costs more than drawing it. But
the `DECA_PROBE_*` harnesses already print numbers that show **what exactly** broke - per-channel
frame luminance, scene bounds, the probe GI field, a clip report, streaming state. The script turns
those numbers into a baseline and checks against it.

```powershell
.\tools\Run-ProbeSuite.ps1                            # check against the baseline
.\tools\Run-ProbeSuite.ps1 -Scenario physics          # a single scenario
.\tools\Run-ProbeSuite.ps1 -Baseline                  # OVERWRITE the baseline
.\tools\Run-ProbeSuite.ps1 -Backend vulkan            # a different backend
```

Eight scenarios, 238 metrics: the Sponza preview, an interior with a point light, the probe GI GPU
path, animation import and avatar mapping on Fox, the Bepu world, the gameplay systems, a full loop
of both render graphs.

**`-Baseline` only on a tree known to be good.** Usually right after a commit that passed the check.
Overwriting the baseline on a broken tree legitimises the breakage - after that the suite lies, and a
suite that lies stops being run.

The baselines live in `tools/baselines/*.metrics` and are versioned: any divergence shows up in the
diff. (The directory is deliberately not called `probe-baseline`: `.gitignore` drops `probe-*/` at
any level, and the baselines would silently never make it into a commit.)

## What is kept out of the baseline, and why

| Dropped | Reason |
|---|---|
| lines containing ` ms` | timings depend on machine load |
| absolute paths | would tie the baseline to one machine |
| compile / pso counters | depend on the state of `DECA_SHADER_CACHE` |
| `[diligent-*]` | driver log: memory page addresses differ between two runs |
| `[...] frame N:` | async streaming milestones drift by a frame or two because of background threads |

Numbers are compared with a relative tolerance (1% by default), text exactly.

Individual **fields** within a line can be excluded by name (`IgnoreFields` on a scenario). That is
what is done for `full-loop`: `finalized`, `texturesReady`, `visible`, `streamingComplete` are the
frame numbers at which the model finished loading, i.e. a measure of machine speed, not of engine
behaviour. Between warm runs they drift by a frame, and on the first run after a rebuild by as much
as five, because a cold JIT and a cold shader cache shift the whole async load.

It is the value that is excluded: the presence of the field is still checked by the shape of the
line, and everything else in it - `300 frames`, `HasModel=True`, an empty `LoadError` - is compared
as usual. Widening the tolerance instead, until it goes green, means checking nothing at all.

## The net has been proven to catch things

Both halves were verified by mutation - a green suite proves nothing until you have seen it go red:

- a flipped bitangent sign and a raised ceiling on fixed rays -> the unit tests go red;
- probe GI bounce feedback 0.5 -> 0.7 -> `probe-gi field` and `debug_probes` diverge.

## Traps

- **Platform.** Diligent does not work on AnyCPU: use `-p:Platform=x64` everywhere. What is built
  with that flag lands in `bin\x64\Debug\net10.0`, while `bin\Debug\net10.0` keeps an OLD kit where
  the exe is updated but `DecaEngine.Graphics.Diligent.dll` is not. Running from there silently
  exercises yesterday's code.
- **BOM in `.ps1`.** Windows PowerShell 5.1 reads a script without a BOM as ANSI, and non-ASCII text
  turns to garbage during parsing. The scripts here do have a BOM - do not lose it when editing.

## Probes while the editor is open

An open editor holds the exe and DLLs in `bin\x64`, so you cannot rebuild into it. To verify an edit
without closing the scene: copy all of `bin\x64\Debug\net10.0` aside, drop in the fresh DLLs from the
`bin\x64` of the projects that were rebuilt (`dotnet build DecaEngine.Probes\...` also builds Editor,
Scene and Physics), and run the suite from there:

```powershell
.\tools\Run-ProbeSuite.ps1 -SkipBuild -BinDir C:\path\to\kit
.\tools\Run-ProbeSuite.ps1 -SkipBuild -BinDir C:\path\to\kit -Baseline -Scenario gameplay
```

That is exactly how the fall-through-the-floor safeguard (see `ProbeFloorRescue` / `ProbeLateFloor` in
GameplayProbe) was verified with the editor running. Once the editor is closed, a normal `Verify.ps1`
is mandatory: it rebuilds the real `bin\x64` that the editor is launched from.
