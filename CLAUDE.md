# Mesh Lab — Unity Package (com.sasharx.lightmap-uv-tool)

## Project Overview
Unity Editor tool suite: UV2 lightmap transfer, LOD generation, UV0 optimization, collision mesh generation, and FBX export. Distributed as a UPM package.

## Architecture
- **Assembly:** `LightmapUvTool.Editor` (Editor-only, namespace `LightmapUvTool`)
- **Entry point:** `Editor/Framework/UvToolHub.cs` — main EditorWindow, manages tool tabs
- **Context:** `Editor/Framework/UvToolContext.cs` — shared state (LODGroup, MeshEntries, caches)
- **Tools:** Each tab implements `IUvTool` interface in `Editor/Tools/`
  - `LightmapTransferTool` — Setup, Repack, Transfer tabs + FBX export
  - `LodGenerationTool` — LOD Gen tab
  - `CollisionMeshTool` — Collision tab
- **Native plugins:** `Plugins/` — xatlas (UV packing) + V-HACD (convex decomposition)
- **Sidecar data:** `Uv2DataAsset` (ScriptableObject) persists UV2/collision data alongside FBX

## Unity Package Rules

### Meta files
- Every `.cs`, `.asset`, `.dll`, `.so`, `.dylib` file MUST have a `.meta` file
- Every directory MUST have a `.meta` file
- Never delete or regenerate `.meta` files — GUIDs are permanent references
- When creating new files, Unity generates `.meta` automatically; do NOT create them manually

### Assembly definition
- All Editor code is in `Editor/` under `LightmapUvTool.Editor.asmdef`
- `includePlatforms: ["Editor"]` — this code never ships in builds
- `allowUnsafeCode: true` — native interop uses unsafe
- FBX exporter gated by `LIGHTMAP_UV_TOOL_FBX_EXPORTER` define (auto-set when `com.unity.formats.fbx` installed)

### Code conventions
- Namespace: `LightmapUvTool`
- No `using System.Text.RegularExpressions` in `LightmapTransferTool.cs` — use fully qualified `System.Text.RegularExpressions.Regex`
- `internal` visibility for cross-tool helpers (same assembly)
- `Undo.RecordObject` / `Undo.AddComponent` / `Undo.DestroyObjectImmediate` for all scene modifications
- Logging via `UvtLog.Info()` / `UvtLog.Warn()` / `UvtLog.Error()` (prefixed `[LightmapUV]`)

### Key patterns
- LOD siblings detected by name pattern: `baseName[_-\s]LOD{N}` (regex, case-insensitive)
- Mesh group key: `UvToolContext.ExtractGroupKey()` strips LOD/COL suffixes for grouping
- FBX export: clone prefab → replace meshes → add LOD/COL children → `ModelExporter.ExportObjects`
- Sidecar workflow: generate → save to `_uv2data.asset` → export to FBX (non-destructive)

## Review Checklist
When reviewing changes to this package:

1. **Meta files** — new files must have `.meta`, removed files must remove `.meta`
2. **Editor-only** — no runtime code, all under `Editor/` with correct asmdef
3. **Undo support** — scene modifications must be undoable
4. **Null safety** — `ctx.LodGroup`, `ctx.MeshEntries`, renderers, meshes can all be null
5. **LODGroup lifecycle** — `RestoreWorkingMeshes()` before clearing/switching context
6. **FBX export guards** — `#if LIGHTMAP_UV_TOOL_FBX_EXPORTER` around FBX-dependent code
7. **No secrets/credentials** — never commit `.env`, API keys, or user-specific paths
8. **Native plugins** — binary changes to `Plugins/` must match `Native/` source; don't modify binaries directly
9. **Naming** — LOD objects follow `Name_LOD{N}` convention; collision objects use `Name_COL`
10. **Mesh cleanup** — temporary meshes (repacked, transferred, welded) must be destroyed when no longer needed
