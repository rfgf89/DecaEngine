---
name: terse-comments
description: Commenting policy for DecaEngine - write short English-only comments, and only where the code cannot speak for itself. Use whenever writing or editing comments in .cs or .hlsl files.
---

# Terse comments

All comments in this repo are English, short, and rare. The code is public; comments are part of the API surface.

## Rules

1. **English only.** Never write Russian (or any non-English) comments, even in WIP code.
2. **One line, ideally under 80 chars.** If a comment needs a second line, the code probably needs a rename or a smaller function instead.
3. **Only non-obvious constraints.** A comment must say something the code cannot: a driver quirk, an ordering requirement, a unit convention, a deliberate deviation. Never narrate what the next line does.
4. **No history, no dialogue.** Never write "fixed", "was broken before", "TODO: ask X", "this used to be", or explain why a change is correct - that belongs in the commit message.
5. **XML docs (`///`)**: only on public API that a user of the engine would call; a single `<summary>` line. No `<param>`/`<returns>` boilerplate that restates the signature.
6. **Delete on touch.** When editing code near a comment that violates these rules, delete or shrink the comment as part of the edit.

## Examples

Bad — narrates the loop, spans two lines, and is written in Russian:
```csharp
// Here we walk over every mesh and group them into batches by material
// (otherwise there would be far too many draw calls)
```

Good:
```csharp
// Batched by material: per-mesh draws blow past the D3D12 command budget.
```

Bad:
```csharp
// increment the counter
count++;
```

Good: (no comment)

## Encoding trap

Never edit comments via PowerShell `Get-Content`/`Set-Content` (PS 5.1 mangles UTF-8). Use the Read/Edit/Write tools only.
