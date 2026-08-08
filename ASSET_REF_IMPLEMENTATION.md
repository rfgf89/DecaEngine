# AssetRef Implementation - Summary

## Overview
Implemented a special serializable type `AssetRef` for storing asset references in DecaEngine, along with integrated drag-and-drop support in the editor. This allows users to easily assign and manage asset references in component fields through a dedicated UI widget.

## Files Created/Modified

### 1. **DecaEngine.Core/Assets/AssetRef.cs** (NEW)
A serializable struct that represents a reference to a project asset:
- Stores path relative to the project's "Assets" directory
- Paths are always forward-slash separated for portability
- Serializes cleanly with Friflo's JSON serializer (no custom converter needed)
- Public constants for ImGui drag-drop payload type identification
- Implicit operators for convenient string conversion

**Key Features:**
- `Path` property: stores asset path (e.g., "Models/cube.gltf")
- `IsEmpty` property: quick check if reference is unassigned
- `DragDropPayloadType` constant: "DECA_ASSET_PATH" for ImGui payload identification

### 2. **DecaEngine.Editor/ComponentFieldEditor.cs** (MODIFIED)
Added full drag-drop target support for `AssetRef` fields:

**New Method:**
- `DrawAssetRef(string label, ref object? value)`: Renders a specialized UI widget that:
  - Displays current asset filename (or "(None)" if unassigned)
  - Shows full path as tooltip on hover
  - Acts as ImGui drag-drop target for asset browser files
  - Includes a clear button (?) to unassign the reference
  - Handles UTF-8 encoding/decoding of dropped asset paths

**Integration:**
- Added check in `DrawField()` to recognize `AssetRef` type and route to `DrawAssetRef()`
- Imports `DecaEngine.Core.Assets` namespace

### 3. **DecaEngine.Editor/AssetBrowserWindow.cs** (MODIFIED)
Added drag source in the asset grid to enable dragging files onto `AssetRef` fields:

**New Drag Source:**
- In `RenderEntry()` method: wraps file entries in `ImGui.BeginDragDropSource/EndDragDropSource`
- Encodes relative asset path as UTF-8 bytes
- Sets ImGui payload with type `AssetRef.DragDropPayloadType`
- Only applies to files (not directories)

**New Helper Method:**
- `GetAssetRelativePath(string fullPath)`: Converts absolute paths to Assets-relative paths with forward slashes
- Maintains cross-platform portability

## How It Works

### User Workflow:
1. Open Inspector window and select a component/script with an `AssetRef` field
2. The field renders as a button showing the current asset name
3. Open Asset Browser window and drag a file into the field
4. The asset path is automatically assigned (relative to Assets directory)
5. Click the ? button to clear the reference

### Technical Flow:
1. **Asset Browser** (drag source):
   - User drags a file entry
   - `RenderEntry()` encodes the relative path as UTF-8 bytes
   - Sets ImGui payload with type "DECA_ASSET_PATH"

2. **Inspector** (drop target):
   - `ComponentFieldEditor.DrawAssetRef()` creates ImGui drag-drop target
   - Receives payload bytes and decodes UTF-8 back to string
   - Updates the `AssetRef.Path` field
   - Component/prefab is marked dirty for saving

3. **Serialization**:
   - Friflo's JSON serializer handles `AssetRef` like any struct
   - Path is stored in JSON alongside other component data
   - No custom converters or special handling needed

## Example Usage

### In a Component/Script:
```csharp
public struct MyAsset
{
    public AssetRef ModelPath;
    public AssetRef TexturePath;
}
```

### In Editor:
- User selects entity with MyAsset component
- Inspector shows two `AssetRef` fields with drag-drop targets
- User drags "Models/character.gltf" onto ModelPath field
- User drags "Textures/skin.png" onto TexturePath field
- Component is serialized with these paths

### At Runtime:
- Game code reads the paths from the component
- Resolves them against the project's Assets directory
- Loads the actual asset files using its own asset-loading system

## Benefits
- ? Type-safe asset references (compile-time checking)
- ? Portable paths (always relative, forward-slash format)
- ? No custom serialization code needed
- ? Intuitive drag-drop editor workflow
- ? Serializes cleanly to JSON for prefabs and scenes
- ? Works with Friflo ECS's JSON round-tripping

## Build Status
? Successfully builds with no errors (0 Error(s), 214 Warning(s) - pre-existing)

