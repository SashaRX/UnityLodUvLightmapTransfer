# Changelog

## [0.4.1] - 2026-02-28

### Changed
- BVH vertex projection is now primary UV transfer method (was fallback)
- Face-level bindings demoted to fallback when BVH finds no shell match
- Raised BVH projection weights (0.9 direct hit, 0.6 shell scan) to reflect higher reliability

## [0.4.0] - 2026-02-28

### Fixed
- Critical: vertex UV averaging across different shells producing stretched triangles spanning entire atlas
- Per-shell vertex accumulator now isolates UV contributions by shell ID — no cross-shell blending
- UV0 proximity used as priority signal for shell conflict resolution at shared vertices
- Post-validation pass detects and re-projects anomalous triangles exceeding shell UV bounds

### Added
- Target UV0 loaded in PrepareTarget for shell priority matching
- Source UV0 interpolation in InterpolateVertexUv and FallbackVertexProject
- Debug logging for vertex conflict resolution statistics

## [0.3.4] - 2026-02-28

### Fixed
- Repack now packs all selected meshes into a single shared UV atlas instead of repacking each mesh independently, preventing UV2 overlap when meshes share the same lightmap

## [0.1.0] - 2026-02-28

### Added
- xatlas native bridge (C++ DLL) with repack-only UV packing
- UV shell extraction via Union-Find connectivity
- UV overlap classifier with chart instance generation
- Full 7-stage UV transfer pipeline (shell assignment → initial transfer → border repair → validation)
- Triangle BVH for fast surface projection
- Border primitive detection and UV perimeter metrics
- Border repair solver with quality gate and conditional fuse
- Transfer quality evaluator with triangle status classification
- Editor Window with 8-stage pipeline control and per-stage re-run
- UV preview canvas with 7 visualization modes
- Multi-mesh atlas support (multiple renderers per LOD)
- Per-mesh quality reports
- CMake build system for native DLL (auto-fetches xatlas)
