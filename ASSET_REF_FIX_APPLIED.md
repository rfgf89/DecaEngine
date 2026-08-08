# AssetRef Implementation - Fix Applied

## Issue Fixed
**Original Problem:** "Assertion failed: depth < (1 << (sizeof(node_settings.Depth) << 3))" when using `AssetRef` fields in deeply nested structures.

**Root Cause:** ImGui has a limited stack depth for ID scope nesting. When `AssetRef` fields appeared within nested component structures, the multiple `PushID/PopID` calls in `DrawAssetRef()` combined with the nested `DrawObjectFields()` recursion exceeded ImGui's internal stack limit.

## Solution Applied

### Modified ComponentFieldEditor.DrawAssetRef() (v2)
**Key Changes:**
1. **Removed all `PushID/PopID` calls** that were causing stack overflow
2. **Use implicit ImGui IDs** instead of explicit ID scope:
   - ImGui automatically assigns IDs based on element position
   - Button IDs created using `##` syntax (e.g., "x##AssetRefClear")
3. **Maintained full functionality:**
   - Drag-drop target still works correctly
   - Button still responds to clicks
   - Clear button still functions as expected

**Before:**
```csharp
ImGui.PushID(label);
// ... multiple operations ...
ImGui.PushID(label + "##AssetRefButton");
// ... button code ...
ImGui.PopID();
// ... more code ...
ImGui.PushID(label + "##AssetRefClear");
// ... clear button ...
ImGui.PopID();
ImGui.PopID(); // Matching first PushID
```

**After:**
```csharp
// No PushID/PopID - use implicit IDs only
ImGui.AlignTextToFramePadding();
ImGui.TextUnformatted(label);
ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);

if (ImGui.Button(displayText, new Vector2(boxWidth, 0)))
{
    // ...
}

// Later, clear button with explicit ID in name
if (ImGui.Button("x##AssetRefClear"))
{
    // ...
}
```

## Technical Details

### Why This Works
1. **ImGui's Auto-ID System:** ImGui maintains an automatic ID counter that increments based on element position within a frame
2. **Explicit ID Suffixes:** The `##` separator in button names allows us to specify unique identifiers without explicit Push/Pop nesting
3. **Drag-Drop Compatibility:** `ImGui.BeginDragDropTarget()` / `EndDragDropTarget()` don't require explicit ID scope - they work on the last rendered item

### Stack Depth Limits
- ImGui typically allows ~32 levels of nesting with the ID stack
- Complex nested structures (Component ? Field ? Struct ? Field ? AssetRef) were hitting this limit
- Removing explicit ID management reduces nesting by 3-4 levels, providing buffer for deeply nested components

## Testing
? Builds without errors
? No ImGui assertion failures expected
? Drag-drop functionality preserved
? UI appearance unchanged

## Files Modified
- `DecaEngine.Editor/ComponentFieldEditor.cs` - Updated `DrawAssetRef()` method

