# rdc-cli command reference

All 66 commands. Args in `<>` are required, `[]` optional. Shared output flags on list commands: `--json`, `--jsonl`, `--no-header`, `-q/--quiet` (primary key column only).

## Session

| Command | Args | Options |
|---|---|---|
| `rdc open` | `[capture]` | `--preload` (preload shader cache), `--proxy HOST[:PORT]`, `--remote`, `--listen [ADDR]:PORT`, `--connect` (existing daemon), `--token` (required with `--connect`) |
| `rdc close` | | `--shutdown` (send shutdown RPC) |
| `rdc status` | | |
| `rdc goto` | `<eid>` | |
| `rdc doctor` | | environment checks |
| `rdc attach` | `<ident>` | `--host` (default localhost) |

## Capture

| Command | Args | Options |
|---|---|---|
| `rdc capture` | `-- app [args]` | `-o/--output`, `--api`, `--list-apis`, `--frame N`, `--trigger` (inject only), `--timeout` (60.0), `--wait-for-exit`, `--keep-alive`, `--auto-open`, `--api-validation`, `--callstacks`, `--hook-children`, `--ref-all-resources`, `--soft-memory-limit MB`, `--delay-for-debugger S`, `--json` |
| `rdc capture-trigger` | | `--ident`, `--host`, `--num-frames` (1) |
| `rdc capture-list` | | `--ident`, `--host`, `--timeout` (5.0), `--json` |
| `rdc capture-copy` | `<capture_id> <dest>` | `--ident`, `--host`, `--timeout` (30.0) |

## Frame overview

| Command | Args | Options |
|---|---|---|
| `rdc info` | | `--json` |
| `rdc stats` | | per-pass breakdown, top draws, largest resources |
| `rdc gpus` | | `--json` |
| `rdc sections` | | embedded sections; `--json` |
| `rdc log` | | `--level` (severity), `--eid` |
| `rdc count` | `<what>` | `--pass` |

## Draws, events, passes

| Command | Args | Options |
|---|---|---|
| `rdc draws` | | `--pass`, `--sort`, `--limit` |
| `rdc draw` | `[eid]` | `--json` |
| `rdc events` | | `--type`, `--filter` (name glob), `--limit`, `--range N:M` |
| `rdc event` | `<eid>` | `--json` |
| `rdc passes` | | |
| `rdc pass` | `<identifier>` | index or name; `--json` |

## Pipeline and resources

| Command | Args | Options |
|---|---|---|
| `rdc pipeline` | `[eid] [section]` | verified sections: `rasterizer`, `viewport`, `blend`, `vs`, `ps`, `cs`, `gs`. The upstream doc's `ia`/`rs`/`om`/`ds` are rejected as "invalid section" by 0.6.3. `--json` |
| `rdc bindings` | `[eid]` | `--binding N`, `--set N` |
| `rdc resources` | | `--type` (exact), `--name` (substring), `--sort` (default `id`) |
| `rdc resource` | `<resid>` | `--json` |
| `rdc usage` | `[resource_id]` | `--all` (usage matrix), `--type`, `--usage` |
| `rdc counters` | | `--list`, `--eid`, `--name` (substring) |

## Shaders

| Command | Args | Options |
|---|---|---|
| `rdc shader` | `[first] [second]` (eid, stage) | `--reflect`, `--constants`, `--source`, `--target FMT` (`dxil`/`spirv`/`glsl`), `--targets`, `-o/--output`, `--all`, `--json` |
| `rdc shaders` | | `--stage`, `--sort` (default `name`) |
| `rdc shader-map` | | EID→shader mapping |
| `rdc search` | `<pattern>` (regex) | `--stage`, `--limit` (200), `-C/--context`, `--case-sensitive`, `--json` |
| `rdc shader-encodings` | | `--json` |
| `rdc shader-build` | `<source_file>` | `--stage`, `--entry` (main), `--encoding` (GLSL), `-q` (print shader_id only) |
| `rdc shader-replace` | `<eid> <stage>` | `--with SHADER_ID` |
| `rdc shader-restore` | `<eid> <stage>` | `--json` |
| `rdc shader-restore-all` | | `--json` |

## Pixel and shader debugging

| Command | Args | Options |
|---|---|---|
| `rdc pixel` | `<x> <y> [eid]` | history; `--target` (0), `--sample` (0) |
| `rdc pick-pixel` | `<x> <y> [eid]` | `--target` (0), `--json` |
| `rdc debug pixel` | `<eid> <x> <y>` | `--trace`, `--dump-at LINE`, `--sample`, `--primitive`, `--json`, `--no-header` |
| `rdc debug vertex` | `<eid> <vtx_id>` | `--trace`, `--dump-at`, `--instance` (0) |
| `rdc debug thread` | `<eid> <gx> <gy> <gz> <tx> <ty> <tz>` | `--trace`, `--dump-at` |

## Export

| Command | Args | Options |
|---|---|---|
| `rdc rt` | `[eid]` | `-o/--output`, `--target` (0), `--raw`, `--overlay` (e.g. wireframe), `--width` (256), `--height` (256) |
| `rdc texture` | `<id>` | `-o`, `--mip` (0), `--raw` |
| `rdc tex-stats` | `<resource_id> [eid]` | `--mip` (0), `--slice` (0), `--histogram` (256 buckets) |
| `rdc buffer` | `<id>` | `-o`, `--raw` |
| `rdc mesh` | `[eid]` | post-transform OBJ; `--stage` (default `vs-out`), `-o`, `--no-header` |
| `rdc thumbnail` | | `--maxsize` (0), `-o`, `--json` |
| `rdc snapshot` | `<eid>` | full state snapshot; `-o` (directory) |

## VFS

| Command | Args | Options |
|---|---|---|
| `rdc ls` | `[path]` | `-F/--classify`, `-l/--long` |
| `rdc tree` | `[path]` | `--depth` (2) |
| `rdc cat` | `<path>` | `--json`, `--raw`, `-o` |

## Diff and assertions

| Command | Args | Options |
|---|---|---|
| `rdc diff` | `<capture_a> <capture_b>` | `--draws`, `--resources`, `--passes`, `--stats`, `--framebuffer`, `--pipeline EID`, `--shortstat`, `--format` (tsv), `--verbose`, `--timeout` (60.0), `--target` (0), `--threshold` (0.0), `--eid`, `--diff-output PNG` |
| `rdc assert-clean` | | `--min-severity` (HIGH) |
| `rdc assert-count` | `<what>` | `--expect N`, `--op` (eq), `--pass` |
| `rdc assert-image` | `<expected> <actual>` | `--threshold` % (0.0), `--diff-output` |
| `rdc assert-pixel` | `<eid> <x> <y>` | `--expect "R G B A"`, `--tolerance` (0.01), `--target` (0) |
| `rdc assert-state` | `<eid> <key_path>` | `--expect` |

## Scripting

| Command | Args | Options |
|---|---|---|
| `rdc script` | `<script_file>` | runs Python inside the daemon; `--arg`, `--json` |

The `assert-*` family is what makes captures usable in a regression harness — the same role `tools/baselines/*.metrics` plays for the headless probes.
