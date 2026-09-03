---
name: windows-editing-traps
description: Encoding, path and C# namespace traps specific to editing this repo on Windows/PowerShell 5.1 — UTF-8 mangling, .ps1 BOM, line counting, namespace shadowing, splitting large files. Use before bulk-editing files, writing or rewriting a tools/*.ps1 script, measuring code size, or splitting a large C# file.
---

# Windows editing traps

## Never pipe repo files through PowerShell

`Get-Content -Raw` in Windows PowerShell 5.1 reads in the system ANSI codepage, not UTF-8. The idiom

```powershell
(Get-Content $f -Raw) -replace ... | Set-Content $f -Encoding utf8
```

turns every Cyrillic comment into `Ð¿Ñ€Ð¾Ð²ÐµÑ€ÐºÐ°`. **The code still compiles**, so the damage stays invisible until someone opens the file.

**Rule: edit files only with Edit/Write.** For bulk changes use several Edit calls or a full Write — never a PowerShell pipe. `sed -i` in Git Bash is byte-oriented and therefore safe, but the Bash tool keeps its working directory between calls, so a prior `cd` breaks relative paths.

## `.ps1` scripts need a BOM

PS 5.1 reads the script itself as ANSI, so a file written by Write (UTF-8 without BOM) fails at *parse* time: `Unexpected token 'ÐµÑ€Ð¸Ð°Ð»Ñ‹'`, `The hash literal was incomplete`. Scripts in `tools/` already have a BOM; rewriting them with Write loses it. Restore it byte-wise, without re-encoding the content:

```powershell
$b=[IO.File]::ReadAllBytes($p); [IO.File]::WriteAllBytes($p, ([byte[]](0xEF,0xBB,0xBF))+$b)
```

Inside such a script also set `[Console]::OutputEncoding = [Text.Encoding]::UTF8` and pass `-Encoding utf8` explicitly to `Out-File`/`Get-Content`.

Files in this repo are CRLF. When rewriting a whole file with Write, preserve CRLF and any existing BOM.

## Measuring code size

Count lines only with `wc -l`. PowerShell `Get-Content | Measure-Object -Line` does not count blank lines and understates by roughly 15%. Numbers from the two methods are not comparable.

## C# namespace shadowing

Inside `DecaEngine.Graphics.*` the simple name `Diligent` binds to the sibling `DecaEngine.Graphics.Diligent` before the SDK — and members of a parent namespace beat even file-scoped **using aliases**: `using ResourceState = Diligent.ResourceState;` died silently when a local `ResourceState` moved into the parent chain. **Write SDK types as `global::Diligent.X` in backend code.**

## Splitting a large C# file

Do not cut at the line where a type is declared: its `///` docs and attributes sit *above* it, so each cut strands the next type's documentation at the end of the previous file. C# only warns (CS1587) — silently. **Check for `warning CS1587` after every such split.**

Recipe that works: map methods with awk, take ranges at method boundaries backed off over their leading comments, share a using header, wrap in `public partial class X`, and verify brace balance before invoking the compiler. Strip string literals and comments before counting braces — a JSON glyph rendered as text once broke the count.

PowerShell array trap in split scripts: write a single range as `@(,@(a,b))`. `@(@(a,b))` flattens and turns the ranges into garbage.
