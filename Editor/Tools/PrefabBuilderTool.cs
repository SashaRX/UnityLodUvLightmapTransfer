// PrefabBuilderTool.cs — Prefab Builder tool (IUvTool tab).
// Sidebar layout (PR-1):
//   • Scene preview toolbar (Off / Vert Colors / Normals / Tangents / UV0-3 / Edges / Problems)
//   • Hierarchy: Root + Apply Names + per-Dummy blocks (LOD rows + COL rows + channel badges)
//   • Legacy Collision / Split / Merge / Mesh Info / Edge / Problem sections
//
// PR-2 will pull tool settings (LOD generation, transfer, collider, VC bake) into a
// right-side stack and migrate the legacy sections out of the sidebar. PR-3 adds the
// Build & Save bottom bar with pre-flight validation. The Build Pipeline / LOD Levels
// foldouts that lived here previously have been folded into the new Hierarchy view.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public class PrefabBuilderTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        System.Action requestRepaint;

        // ── Identity ──
        public string ToolName  => "Prefab Builder";
        public string ToolId    => "prefab_builder";
        public int    ToolOrder => 44;
        public System.Action RequestRepaint { set => requestRepaint = value; }

        // ── Preview ──
        enum PreviewMode
        {
            None,
            VertexColors,
            Normals,
            Tangents,
            UV0, UV1, UV2, UV3, UV4, UV5, UV6, UV7,
            EdgeWireframe,
            ProblemAreas
        }

        PreviewMode previewMode = PreviewMode.None;
        PrefabBuilderPreview preview;

        // ── Hierarchy view state ──
        // pendingNames: instanceID → edited name (uncommitted; committed by Apply Names).
        // lodQualitySliders: renderer instanceID → simplification target ratio.
        // freshRendererIds: renderer instanceID set freshly created or regenerated
        // since the last Apply Names. Drives the bright-orange "this is new"
        // highlight so the user can spot just-inserted LODs in a busy hierarchy.
        // hierarchyDummies: cached view model rebuilt from ctx; null = needs rebuild.
        Dictionary<int, string> pendingNames;
        Dictionary<int, float> lodQualitySliders;
        HashSet<int> freshRendererIds;
        bool hierarchyFoldout = true;
        List<HierarchyDummy> hierarchyDummies;

        sealed class HierarchyDummy
        {
            public Transform dummy;          // container transform; equals root when flat
            public bool isRoot;              // true when the prefab has no separate dummy container
            public List<HierarchyLodRow> lods = new List<HierarchyLodRow>();
            public List<HierarchyColRow> cols = new List<HierarchyColRow>();
            public bool foldout = true;
        }

        sealed class HierarchyLodRow
        {
            public int lodIndex;
            public Renderer renderer;
            public Mesh mesh;
        }

        sealed class HierarchyColRow
        {
            public Transform colTransform;     // GameObject the collider is on (dummy itself or a _COL child).
            public Collider collider;          // Component reference (Mesh / Box / Capsule / Sphere…).
            public Mesh mesh;                  // sharedMesh for MeshCollider, or sharedMesh of a _COL GO MeshFilter.
            public string typeLabel;           // "Mesh" / "Box" / "Capsule" / "Sphere" / fallback type name.
            // True when this row represents a Collider component attached to the
            // Dummy/Root GameObject itself (not a separate _COL child). Component
            // rows can't be renamed by Rebuild Names and ✕ removes only the
            // component instead of destroying the whole GameObject.
            public bool isComponentOnly;
        }

        // Accumulates which channels mutating operations touched since the last
        // refresh. Drives the export pipeline (PR-3) so isolated re-save can be
        // used when only data channels changed.
        FbxExportIntent buildIntent = FbxExportIntent.None;

        // ── Split/Merge state (legacy, migrates to right panel in PR-2) ──
        struct SplitCandidate
        {
            public MeshEntry entry;
            public bool include;
        }
        struct MergeGroup
        {
            public int lodIndex;
            public Material material;
            public List<MeshEntry> entries;
            public bool include;
        }
        List<SplitCandidate> splitCandidates;
        List<MergeGroup> mergeCandidates;
        bool splitMergeFoldout;

        // ── Collision state (legacy) ──
        bool collisionFoldout;

        // ── Edge / problem report cache (legacy) ──
        struct MeshEdgeReport
        {
            public string meshName;
            public EdgeAnalyzer.EdgeReport report;
        }
        List<MeshEdgeReport> edgeReports;

        struct ProblemSummary
        {
            public string meshName;
            public int degenerateTris;
            public int unusedVerts;
            public int falseSeamVerts;
        }
        List<ProblemSummary> problemSummaries;

        // ── Lifecycle ──

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
            if (preview == null) preview = new PrefabBuilderPreview();
            pendingNames = new Dictionary<int, string>();
            lodQualitySliders = new Dictionary<int, float>();
            freshRendererIds = new HashSet<int>();
        }

        public void OnDeactivate()
        {
            preview?.Restore();
            previewMode = PreviewMode.None;
        }

        public void OnRefresh()
        {
            preview?.Restore();
            previewMode = PreviewMode.None;
            edgeReports = null;
            problemSummaries = null;
            pendingNames?.Clear();
            lodQualitySliders?.Clear();
            freshRendererIds?.Clear();
            hierarchyDummies = null;
            splitCandidates = null;
            mergeCandidates = null;
            buildIntent = FbxExportIntent.None;
        }

        // ── UI: Sidebar ──

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Prefab Builder", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (ctx == null || (ctx.LodGroup == null && !ctx.StandaloneMesh))
            {
                EditorGUILayout.HelpBox(
                    "Select a GameObject with LODGroup or MeshRenderer.",
                    MessageType.Info);
                return;
            }

            DrawPreviewModeToolbar();
            DrawHierarchySection();
            DrawCollisionSection();
            DrawSplitMergeSection();
            DrawMeshInfo();

            if (edgeReports != null)
                DrawEdgeReportSection();

            if (problemSummaries != null)
                DrawProblemSummarySection();

            DrawEdgeLegend();
        }

        // ═══════════════════════════════════════════════════════════
        // Preview mode toolbar (PR-4 will move this to viewport top bar)
        // ═══════════════════════════════════════════════════════════

        void DrawPreviewModeToolbar()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Off", PreviewMode.None);
            DrawModeButton("Vert Colors", PreviewMode.VertexColors);
            DrawModeButton("Normals", PreviewMode.Normals);
            DrawModeButton("Tangents", PreviewMode.Tangents);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawModeButton("UV0", PreviewMode.UV0);
            DrawModeButton("UV1", PreviewMode.UV1);
            DrawModeButton("UV2", PreviewMode.UV2);
            DrawModeButton("UV3", PreviewMode.UV3);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawModeButton("Edges", PreviewMode.EdgeWireframe);
            DrawModeButton("Problems", PreviewMode.ProblemAreas);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
        }

        void DrawModeButton(string label, PreviewMode mode)
        {
            var bgc = GUI.backgroundColor;
            if (previewMode == mode)
                GUI.backgroundColor = new Color(0.3f, 0.7f, 1f);

            if (GUILayout.Button(label, GUILayout.Height(20)))
            {
                if (previewMode == mode)
                {
                    preview.Restore();
                    previewMode = PreviewMode.None;
                    edgeReports = null;
                    problemSummaries = null;
                }
                else
                {
                    preview.Restore();
                    previewMode = mode;
                    edgeReports = null;
                    problemSummaries = null;
                    ActivateCurrentPreview();
                }
                SceneView.RepaintAll();
                requestRepaint?.Invoke();
            }

            GUI.backgroundColor = bgc;
        }

        void ActivateCurrentPreview()
        {
            switch (previewMode)
            {
                case PreviewMode.VertexColors:
                    preview.ActivateVertexColorPreview(ctx);
                    break;
                case PreviewMode.Normals:
                    preview.ActivateNormalsPreview(ctx);
                    break;
                case PreviewMode.Tangents:
                    preview.ActivateTangentsPreview(ctx);
                    break;
                case PreviewMode.UV0: case PreviewMode.UV1:
                case PreviewMode.UV2: case PreviewMode.UV3:
                case PreviewMode.UV4: case PreviewMode.UV5:
                case PreviewMode.UV6: case PreviewMode.UV7:
                    int channel = previewMode - PreviewMode.UV0;
                    preview.ActivateUvPreview(ctx, channel);
                    break;
                case PreviewMode.EdgeWireframe:
                    preview.BuildEdgeOverlays(ctx);
                    BuildEdgeReports();
                    break;
                case PreviewMode.ProblemAreas:
                    preview.ActivateProblemPreview(ctx);
                    BuildProblemSummaries();
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Hierarchy section (NEW, PR-1)
        //
        // Shape:
        //   Root row: [name field] "Root"  [Apply Names (green)]
        //   For each Dummy block:
        //     Foldout header  [name field if non-root]
        //     Per LOD row:
        //       Row A: [name display]   [+ Add LOD]  [↻]  [✕]
        //       Row B: LOD{N}  Vv / Tt   [channel badges]
        //       Row C: LOD{N}  [— quality slider —]  value
        //     "+ Add LOD" insert-row between LODs and at the bottom
        //     COL rows (different colour) with [name] [✕]
        //
        // Per user spec: only Root + Dummy names are user-editable; child names
        // (LOD / COL) are auto-derived on Apply Names. ↑↓ reorder removed.
        // ═══════════════════════════════════════════════════════════

        void DrawHierarchySection()
        {
            EditorGUILayout.Space(8);
            hierarchyFoldout = EditorGUILayout.Foldout(hierarchyFoldout, "Hierarchy", true);
            if (!hierarchyFoldout) return;

            if (ctx.LodGroup == null && !ctx.StandaloneMesh)
            {
                EditorGUILayout.HelpBox("No LODGroup selected.", MessageType.Info);
                return;
            }

            if (ctx.LodGroup == null)
            {
                EditorGUILayout.HelpBox("Standalone mesh: hierarchy editing requires LODGroup.", MessageType.Info);
                return;
            }

            if (hierarchyDummies == null) RebuildHierarchyView();
            if (hierarchyDummies == null) return;

            DrawRootRow();

            // Re-check: DrawRootRow's Rebuild Names button can mutate state
            // and null hierarchyDummies mid-frame.
            if (hierarchyDummies == null) return;

            // Indent each Dummy block so the hierarchy reads as a tree
            // (Root at the left edge → Dummy children visually nested under
            // it). The 20-px gutter on the left of each block holds an
            // L-shaped connector that explicitly anchors the dummy back to
            // Root, so the parent/child relationship is unmistakable even
            // when the helpBox borders blend in with the inspector.
            foreach (var dummy in hierarchyDummies)
            {
                if (dummy == null || dummy.dummy == null) continue;

                EditorGUILayout.BeginHorizontal();
                var gutter = GUILayoutUtility.GetRect(HierarchyDummyIndent, 22,
                    GUILayout.Width(HierarchyDummyIndent), GUILayout.Height(22));
                if (Event.current.type == EventType.Repaint)
                {
                    var line = new Color(0.55f, 0.55f, 0.55f);
                    float xMid = gutter.x + HierarchyDummyIndent * 0.5f;
                    // Vertical drop from top of gutter to the elbow.
                    EditorGUI.DrawRect(new Rect(xMid, gutter.y, 1, 14), line);
                    // Horizontal stub from the elbow into the helpBox.
                    EditorGUI.DrawRect(new Rect(xMid, gutter.y + 14,
                        HierarchyDummyIndent * 0.5f + 2, 1), line);
                }

                EditorGUILayout.BeginVertical();
                DrawDummyBlock(dummy);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();

                if (hierarchyDummies == null) break;
            }
        }

        void DrawRootRow()
        {
            var rootGo = ctx.LodGroup.gameObject;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Root", EditorStyles.miniLabel, GUILayout.Width(34));
            DrawEditableNameField(rootGo, GUILayout.MinWidth(120));
            GUILayout.FlexibleSpace();

            // Rebuild Names commits pending edits AND rebuilds child LOD/COL
            // names from the canonical "<prefix>_LOD{N}" / "<prefix>_COL[_Hull{N}]"
            // pattern. Resolves duplicate names left over from Insert / Delete.
            // Stays enabled even when no pending edits so users can resync.
            bool hasPending = pendingNames != null && pendingNames.Count > 0;
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.40f, 0.80f, 0.40f);
            string label = hasPending ? $"Rebuild Names ({pendingNames.Count})" : "Rebuild Names";
            var tooltip = new GUIContent(label,
                "Commit pending Root/Dummy renames AND fix the trailing _LOD{N} / _COL "
                + "suffix on every child so the index matches its slot. Each child keeps "
                + "its existing base — \"Stove_Base_LOD0\" stays \"Stove_Base_LOD0\". "
                + "Use this to clean up duplicate suffixes after Insert / Delete.");
            if (GUILayout.Button(tooltip, GUILayout.Height(20), GUILayout.Width(140)))
                ApplyPendingNames();
            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();

            // Sanity warnings on root
            if (MeshHygieneUtility.HasLodOrColSuffix(rootGo.name))
                EditorGUILayout.HelpBox("Root name has LOD/COL suffix.", MessageType.Warning);
            var rootMf = rootGo.GetComponent<MeshFilter>();
            if (rootMf != null && rootMf.sharedMesh != null)
                EditorGUILayout.HelpBox("Root has mesh — should be empty pivot.", MessageType.Warning);
        }

        void DrawDummyBlock(HierarchyDummy dummy)
        {
            if (dummy == null || dummy.dummy == null) return;

            EditorGUILayout.Space(4);

            // Tint the helpBox per Dummy via GUI.backgroundColor — green for the
            // implicit root group, blue for explicit Dummy containers. Visually
            // separates multiple Dummy groups in a busy hierarchy.
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = dummy.isRoot
                ? new Color(0.75f, 1.05f, 0.75f)
                : new Color(0.78f, 0.92f, 1.10f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            // Header: small foldout arrow + dummy name (editable when not root) + summary.
            EditorGUILayout.BeginHorizontal();
            var foldoutRect = GUILayoutUtility.GetRect(14, 16, GUILayout.Width(14), GUILayout.Height(16));
            dummy.foldout = EditorGUI.Foldout(foldoutRect, dummy.foldout, GUIContent.none, true);
            if (dummy.isRoot)
                EditorGUILayout.LabelField("Root group", EditorStyles.boldLabel, GUILayout.MinWidth(100));
            else
                DrawEditableNameField(dummy.dummy.gameObject, GUILayout.MinWidth(100));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{dummy.lods.Count} LOD · {dummy.cols.Count} COL",
                EditorStyles.miniLabel, GUILayout.Width(110));
            EditorGUILayout.EndHorizontal();

            if (!dummy.foldout)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space(2);

            // LOD rows interleaved with insert buttons
            for (int i = 0; i < dummy.lods.Count; i++)
            {
                if (DrawLodRow(dummy, dummy.lods[i]))
                {
                    EditorGUILayout.EndVertical();
                    return;
                }
                int afterLodIndex = dummy.lods[i].lodIndex;
                if (DrawAddLodButton(dummy, afterLodIndex))
                {
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            // No LODs yet — single Add at top
            if (dummy.lods.Count == 0)
            {
                if (DrawAddLodButton(dummy, -1))
                {
                    EditorGUILayout.EndVertical();
                    return;
                }
            }

            // COL rows (different color tint)
            if (dummy.cols.Count > 0)
            {
                EditorGUILayout.Space(2);
                var colSep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(colSep, new Color(0.35f, 0.55f, 0.45f, 0.5f));
                for (int i = 0; i < dummy.cols.Count; i++)
                {
                    if (DrawColRow(dummy, dummy.cols[i], i, dummy.cols.Count))
                    {
                        EditorGUILayout.EndVertical();
                        return;
                    }
                }
            }

            // Hint when any child's _LOD/_COL suffix doesn't match its slot.
            if (DummyHasStale(dummy))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(
                    "Trailing _LOD/_COL suffix doesn't match the slot. Click Rebuild Names to renumber.",
                    MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        const float HierarchyRowIndent = 10f;
        const float HierarchyDummyIndent = 18f;

        // Returns true when the operation invalidates the UI for this frame.
        bool DrawLodRow(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (lod == null || lod.renderer == null) return false;

            int rid = lod.renderer.GetInstanceID();
            bool fresh = freshRendererIds != null && freshRendererIds.Contains(rid);
            bool stale = !fresh && IsLodRowStale(dummy, lod);

            // Row A: status marker + name + actions
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            DrawStatusMarker(fresh, stale);

            var prevBg = GUI.backgroundColor;
            if (fresh)
                GUI.backgroundColor = new Color(1.0f, 0.55f, 0.10f);    // bright orange — just inserted/regen
            else if (stale)
                GUI.backgroundColor = new Color(0.95f, 0.78f, 0.30f);   // amber — name out of sync
            EditorGUILayout.LabelField(lod.renderer.gameObject.name,
                EditorStyles.textField, GUILayout.MinWidth(120));
            GUI.backgroundColor = prevBg;
            GUILayout.FlexibleSpace();

            GUI.backgroundColor = new Color(0.60f, 0.75f, 0.90f);
            if (GUILayout.Button(new GUIContent("↻", "Regenerate this LOD from LOD0 source with the current quality."),
                    GUILayout.Width(22), GUILayout.Height(18)))
            {
                RegenerateLodWithQuality(dummy, lod);
                GUI.backgroundColor = prevBg;
                EditorGUILayout.EndHorizontal();
                return true;
            }
            GUI.backgroundColor = new Color(0.90f, 0.30f, 0.30f);
            if (GUILayout.Button(new GUIContent("✕", "Remove this LOD level."),
                    GUILayout.Width(22), GUILayout.Height(18)))
            {
                DeleteLodRow(dummy, lod);
                GUI.backgroundColor = prevBg;
                EditorGUILayout.EndHorizontal();
                return true;
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            // Row B: stat + channel badges
            int verts = lod.mesh != null ? lod.mesh.vertexCount : 0;
            int tris = lod.mesh != null ? MeshHygieneUtility.GetTriangleCount(lod.mesh) : 0;
            string stat = $"LOD{lod.lodIndex}  {verts:N0}v / {tris:N0}t";
            string badges = ChannelBadges(lod.mesh);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            EditorGUILayout.LabelField(stat, EditorStyles.miniLabel, GUILayout.Width(160));
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(badges))
                EditorGUILayout.LabelField(badges, EditorStyles.miniLabel, GUILayout.Width(180));
            EditorGUILayout.EndHorizontal();

            // Row C: LOD label + quality slider
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            EditorGUILayout.LabelField($"LOD{lod.lodIndex}", EditorStyles.miniLabel, GUILayout.Width(40));
            if (!lodQualitySliders.TryGetValue(rid, out var quality))
                quality = lod.lodIndex == 0 ? 1f : Mathf.Pow(0.5f, lod.lodIndex);
            float newQuality = EditorGUILayout.Slider(quality, 0.001f, 1f);
            if (Mathf.Abs(newQuality - quality) > 0.0001f)
                lodQualitySliders[rid] = newQuality;
            EditorGUILayout.EndHorizontal();

            return false;
        }

        // Returns true on click (UI invalidated by insertion).
        bool DrawAddLodButton(HierarchyDummy dummy, int afterLodIndex)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(40);
            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.75f, 0.95f);
            string label = afterLodIndex < 0 ? "+ Add LOD" : $"+ Add LOD after LOD{afterLodIndex}";
            bool clicked = GUILayout.Button(label, GUILayout.Height(16));
            GUI.backgroundColor = bgc;
            GUILayout.Space(40);
            EditorGUILayout.EndHorizontal();

            if (clicked)
            {
                int insertAt = afterLodIndex + 1;
                InsertLodAt(dummy, insertAt);
                return true;
            }
            return false;
        }

        bool DrawColRow(HierarchyDummy dummy, HierarchyColRow col, int index, int totalCols)
        {
            if (col == null || col.colTransform == null) return false;

            // Component-only rows (collider on the dummy itself) are never
            // renamed — the host GameObject is the dummy, not a leaf.
            bool stale = !col.isComponentOnly && IsColRowStale(dummy, col, index, totalCols);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HierarchyRowIndent);
            DrawStatusMarker(false, stale);

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = stale
                ? new Color(0.95f, 0.78f, 0.30f)
                : new Color(0.55f, 0.95f, 0.70f);
            string display = col.isComponentOnly
                ? $"{col.colTransform.gameObject.name}  [{col.typeLabel}]"
                : col.colTransform.gameObject.name;
            EditorGUILayout.LabelField(display, EditorStyles.textField, GUILayout.MinWidth(120));
            GUI.backgroundColor = prevBg;

            EditorGUILayout.LabelField(BuildColStatLabel(col),
                EditorStyles.miniLabel, GUILayout.Width(180));

            GUILayout.FlexibleSpace();
            string removeTooltip = col.isComponentOnly
                ? $"Remove the {col.typeLabel}Collider component from '{col.colTransform.name}'."
                : "Remove this collision GameObject.";
            GUI.backgroundColor = new Color(0.90f, 0.30f, 0.30f);
            if (GUILayout.Button(new GUIContent("✕", removeTooltip),
                    GUILayout.Width(22), GUILayout.Height(18)))
            {
                DeleteCol(dummy, col);
                GUI.backgroundColor = prevBg;
                EditorGUILayout.EndHorizontal();
                return true;
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            return false;
        }

        static string BuildColStatLabel(HierarchyColRow col)
        {
            if (col.collider is MeshCollider)
            {
                int verts = col.mesh != null ? col.mesh.vertexCount : 0;
                int tris = col.mesh != null ? MeshHygieneUtility.GetTriangleCount(col.mesh) : 0;
                string meshName = col.mesh != null ? col.mesh.name : "(none)";
                return $"Mesh  {verts:N0}v / {tris:N0}t  · {meshName}";
            }
            if (col.collider is BoxCollider bc)
                return $"Box  {bc.size.x:F2}×{bc.size.y:F2}×{bc.size.z:F2}";
            if (col.collider is CapsuleCollider cc)
                return $"Capsule  r={cc.radius:F2}  h={cc.height:F2}";
            if (col.collider is SphereCollider sc)
                return $"Sphere  r={sc.radius:F2}";
            if (col.collider != null)
                return col.typeLabel ?? col.collider.GetType().Name;
            int v = col.mesh != null ? col.mesh.vertexCount : 0;
            int t = col.mesh != null ? MeshHygieneUtility.GetTriangleCount(col.mesh) : 0;
            return $"COL  {v:N0}v / {t:N0}t";
        }

        // Editable text field bound to pendingNames keyed by GameObject instanceID.
        // Callers receive a draggable change indicator on the right of the input.
        void DrawEditableNameField(GameObject go, params GUILayoutOption[] options)
        {
            if (go == null) return;
            int id = go.GetInstanceID();
            if (!pendingNames.TryGetValue(id, out string editName))
                editName = go.name;

            string newName = EditorGUILayout.TextField(editName, options);
            if (newName != editName)
            {
                if (newName != go.name) pendingNames[id] = newName;
                else pendingNames.Remove(id);
            }

            if (pendingNames.ContainsKey(id))
            {
                var dot = EditorGUILayout.GetControlRect(false, 14, GUILayout.Width(14));
                EditorGUI.DrawRect(new Rect(dot.x + 2, dot.y + 2, 10, 10),
                    new Color(1f, 0.7f, 0.2f));
            }
        }

        // ── Hierarchy view rebuild ──

        void RebuildHierarchyView()
        {
            hierarchyDummies = new List<HierarchyDummy>();
            if (ctx == null || ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;
            var lookup = new Dictionary<Transform, HierarchyDummy>();

            // Group LOD renderers by their parent transform. A parent that equals
            // root means the prefab is flat (no separate Dummy container) — in
            // that case the root acts as the implicit single Dummy.
            var lods = ctx.LodGroup.GetLODs();
            for (int li = 0; li < lods.Length; li++)
            {
                if (lods[li].renderers == null) continue;
                foreach (var r in lods[li].renderers)
                {
                    if (r == null) continue;
                    var parent = r.transform.parent;
                    if (parent == null) continue;
                    Transform key = parent == root ? root : parent;
                    var dummy = GetOrCreateDummy(lookup, key, root);
                    var mf = r.GetComponent<MeshFilter>();
                    dummy.lods.Add(new HierarchyLodRow
                    {
                        lodIndex = li,
                        renderer = r,
                        mesh = mf != null ? mf.sharedMesh : null
                    });
                }
            }

            // Two collision sources flow into each dummy block:
            //  1) Collider components attached to the dummy/root GameObject itself
            //     (e.g. a MeshCollider on the prefab root pointing at a *_COL mesh
            //     asset). These are "component-only" rows.
            //  2) Standalone _COL named child GameObjects (legacy convention).
            // The first is the case the user just flagged — meshes referenced by a
            // root-level MeshCollider were invisible in the tree.
            foreach (var dummy in lookup.Values)
            {
                if (dummy.dummy == null) continue;
                foreach (var c in dummy.dummy.GetComponents<Collider>())
                {
                    if (c == null) continue;
                    Mesh m = null;
                    if (c is MeshCollider mc) m = mc.sharedMesh;
                    dummy.cols.Add(new HierarchyColRow
                    {
                        colTransform   = dummy.dummy,
                        collider       = c,
                        mesh           = m,
                        typeLabel      = ColliderTypeLabel(c),
                        isComponentOnly = true,
                    });
                }
            }

            foreach (var colGo in MeshHygieneUtility.FindCollisionObjects(root))
            {
                if (colGo == null) continue;
                var colT = colGo.transform;
                var parent = colT.parent;
                if (parent == null) continue;
                Transform key = parent == root ? root : parent;
                var dummy = GetOrCreateDummy(lookup, key, root);
                var mf = colGo.GetComponent<MeshFilter>();
                var c = colGo.GetComponent<Collider>();
                dummy.cols.Add(new HierarchyColRow
                {
                    colTransform   = colT,
                    collider       = c,
                    mesh           = (c is MeshCollider mc2) ? mc2.sharedMesh
                                       : (mf != null ? mf.sharedMesh : null),
                    typeLabel      = c != null ? ColliderTypeLabel(c) : "Mesh",
                    isComponentOnly = false,
                });
            }

            hierarchyDummies = lookup.Values.ToList();
            hierarchyDummies.Sort(CompareDummies);
            foreach (var d in hierarchyDummies)
                d.lods.Sort((a, b) => a.lodIndex.CompareTo(b.lodIndex));
        }

        static HierarchyDummy GetOrCreateDummy(Dictionary<Transform, HierarchyDummy> lookup,
            Transform key, Transform root)
        {
            if (!lookup.TryGetValue(key, out var dummy))
            {
                dummy = new HierarchyDummy
                {
                    dummy = key,
                    isRoot = key == root
                };
                lookup[key] = dummy;
            }
            return dummy;
        }

        static string ColliderTypeLabel(Collider c)
        {
            switch (c)
            {
                case MeshCollider _:    return "Mesh";
                case BoxCollider _:     return "Box";
                case CapsuleCollider _: return "Capsule";
                case SphereCollider _:  return "Sphere";
                case WheelCollider _:   return "Wheel";
                case TerrainCollider _: return "Terrain";
                default:                return c.GetType().Name.Replace("Collider", "");
            }
        }

        static int CompareDummies(HierarchyDummy a, HierarchyDummy b)
        {
            if (a.isRoot && !b.isRoot) return -1;
            if (!a.isRoot && b.isRoot) return 1;
            return string.Compare(a.dummy.name, b.dummy.name,
                System.StringComparison.OrdinalIgnoreCase);
        }

        // ── Apply Names: two-step commit. ──
        // Step 1 commits pending edits on Root / Dummy nodes; Step 2 walks each
        // dummy and rebuilds child LOD/COL names from the (possibly just-renamed)
        // root or dummy prefix using the canonical `<prefix>_LOD{N}` and
        // `<prefix>_COL[_Hull{N}]` patterns. Per spec the user only edits Root
        // and Dummy names directly — leaf names are derived deterministically.

        void ApplyPendingNames()
        {
            if (ctx.LodGroup == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Apply Names");
            int undoGroup = Undo.GetCurrentGroup();

            // Step 1: commit pending edits.
            if (pendingNames != null)
            {
                foreach (var kvp in pendingNames)
                {
                    var go = EditorUtility.InstanceIDToObject(kvp.Key) as GameObject;
                    if (go == null || go.name == kvp.Value) continue;

                    Undo.RecordObject(go, "Rename");
                    UvtLog.Info($"[LightmapUV] Renamed: {go.name} → {kvp.Value}");
                    go.name = kvp.Value;
                }
                pendingNames.Clear();
            }

            // Step 2: rebuild leaf names from the current Root + Dummy names.
            RebuildLeafNamesNoUndoGroup();

            Undo.CollapseUndoOperations(undoGroup);
            buildIntent |= FbxExportIntent.Hierarchy;
            freshRendererIds?.Clear();

            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        // ── LOD row operations ──

        void RegenerateLodWithQuality(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (ctx.LodGroup == null || dummy == null || lod == null || lod.renderer == null)
                return;

            int rid = lod.renderer.GetInstanceID();
            float quality = lodQualitySliders.TryGetValue(rid, out var q) ? q
                : Mathf.Pow(0.5f, lod.lodIndex);

            // LOD0 source = matching renderer in this dummy's LOD0 row, or fall back
            // to the first LOD0 renderer in the LODGroup. We match by stripped group
            // key so renames mid-pipeline don't break the link.
            var sourceMesh = ResolveLodSourceMesh(dummy, lod);
            if (sourceMesh == null)
            {
                UvtLog.Warn($"[LightmapUV] Regenerate LOD{lod.lodIndex}: no source mesh.");
                return;
            }

            var settings = new MeshSimplifier.SimplifySettings
            {
                targetRatio  = quality,
                targetError  = 0.1f,
                uv2Weight    = 0.5f,
                normalWeight = 0.5f,
                lockBorder   = true,
                uvChannel    = 1
            };

            var res = MeshSimplifier.Simplify(sourceMesh, settings);
            if (!res.ok)
            {
                UvtLog.Warn($"[LightmapUV] Simplify failed for '{sourceMesh.name}': {res.error}");
                return;
            }
            res.simplifiedMesh.name = sourceMesh.name + "_LOD" + lod.lodIndex;

            var mf = lod.renderer.GetComponent<MeshFilter>();
            if (mf == null) return;
            Undo.RecordObject(mf, "Regenerate LOD");
            mf.sharedMesh = res.simplifiedMesh;

            freshRendererIds?.Add(rid);

            UvtLog.Info($"[LightmapUV] Regenerated LOD{lod.lodIndex} on '{lod.renderer.name}': "
                + $"{res.originalTriCount} → {res.simplifiedTriCount} tris (target {quality:P0})");

            buildIntent |= FbxExportIntent.AnyUv | FbxExportIntent.Normals
                | FbxExportIntent.Tangents | FbxExportIntent.VertexColors;

            ctx.LodGroup.RecalculateBounds();
            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        Mesh ResolveLodSourceMesh(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            // Always prefer the import-time fbxMesh from MeshEntries over the
            // currently-bound sharedMesh. Otherwise re-running simplify on
            // LOD0 would replace its mesh with a degraded copy and every
            // downstream regenerate (LOD1 → LODN) would start from that
            // already-simplified geometry instead of the original FBX.
            string key = lod != null && lod.renderer != null
                ? UvToolContext.ExtractGroupKey(lod.renderer.name)
                : null;
            if (key != null)
            {
                foreach (var candidate in dummy.lods)
                {
                    if (candidate == lod) continue;
                    if (candidate.lodIndex != 0) continue;
                    if (candidate.mesh == null) continue;
                    string ck = UvToolContext.ExtractGroupKey(candidate.renderer != null ? candidate.renderer.name : "");
                    if (string.Equals(ck, key, System.StringComparison.OrdinalIgnoreCase))
                        return PreferOriginalMesh(candidate.renderer, candidate.mesh);
                }
            }
            // For LOD0 → reuse the renderer's own mesh, but always go through
            // the original-mesh lookup so we don't simplify a simplified mesh.
            if (lod != null && lod.lodIndex == 0)
                return PreferOriginalMesh(lod.renderer, lod.mesh);
            // Fallback: first LOD0 mesh in the dummy.
            foreach (var candidate in dummy.lods)
                if (candidate.lodIndex == 0 && candidate.mesh != null)
                    return PreferOriginalMesh(candidate.renderer, candidate.mesh);
            return null;
        }

        // Look up the import-time mesh (fbxMesh) for a renderer via the
        // shared MeshEntries cache. Falls back to the supplied current
        // sharedMesh when the entry doesn't carry an FBX-source reference.
        Mesh PreferOriginalMesh(Renderer r, Mesh fallback)
        {
            if (r == null || ctx?.MeshEntries == null) return fallback;
            foreach (var e in ctx.MeshEntries)
            {
                if (e.renderer != r) continue;
                return e.fbxMesh ?? e.originalMesh ?? fallback;
            }
            return fallback;
        }

        void InsertLodAt(HierarchyDummy dummy, int insertIndex)
        {
            if (ctx.LodGroup == null || dummy == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Insert LOD");
            int undoGroup = Undo.GetCurrentGroup();

            var lods = ctx.LodGroup.GetLODs();
            int targetIdx = Mathf.Clamp(insertIndex, 0, lods.Length);

            // Compute transition height: midpoint between neighbours.
            float prevTrans = targetIdx > 0
                ? lods[targetIdx - 1].screenRelativeTransitionHeight : 1f;
            float nextTrans = targetIdx < lods.Length
                ? lods[targetIdx].screenRelativeTransitionHeight : 0.01f;
            float newTrans = Mathf.Max(0.01f, (prevTrans + nextTrans) * 0.5f);

            // Default quality: half the previous LOD slider (or 0.5 for the first
            // inserted slot). The user can refine via the row slider after.
            float quality = 0.5f;
            if (targetIdx > 0)
            {
                foreach (var existing in dummy.lods)
                {
                    if (existing.lodIndex != targetIdx - 1) continue;
                    if (existing.renderer == null) break;
                    int rid = existing.renderer.GetInstanceID();
                    if (lodQualitySliders.TryGetValue(rid, out var prevQ))
                        quality = Mathf.Max(0.01f, prevQ * 0.5f);
                    break;
                }
            }

            // Generate a new mesh and renderer GameObject from the dummy's LOD0.
            var sourceMesh = ResolveLodSourceMesh(dummy,
                dummy.lods.Count > 0 ? dummy.lods[0] : new HierarchyLodRow { lodIndex = 0 });
            if (sourceMesh == null)
            {
                UvtLog.Warn("[LightmapUV] Insert LOD: no source mesh in this dummy group.");
                return;
            }

            var settings = new MeshSimplifier.SimplifySettings
            {
                targetRatio  = quality,
                targetError  = 0.1f,
                uv2Weight    = 0.5f,
                normalWeight = 0.5f,
                lockBorder   = true,
                uvChannel    = 1
            };

            var res = MeshSimplifier.Simplify(sourceMesh, settings);
            if (!res.ok)
            {
                UvtLog.Warn($"[LightmapUV] Insert LOD simplify failed: {res.error}");
                return;
            }

            string baseName = UvToolContext.ExtractGroupKey(sourceMesh.name);
            if (string.IsNullOrEmpty(baseName)) baseName = sourceMesh.name;
            res.simplifiedMesh.name = baseName + "_LOD" + targetIdx;

            // Pick a parent: prefer the existing dummy container, else the LODGroup transform.
            Transform parent = dummy.dummy != null ? dummy.dummy : ctx.LodGroup.transform;

            var go = new GameObject(baseName + "_LOD" + targetIdx);
            Undo.RegisterCreatedObjectUndo(go, "Insert LOD");
            go.transform.SetParent(parent, false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = res.simplifiedMesh;
            var mr = go.AddComponent<MeshRenderer>();

            // Copy renderer settings from the LOD0 source where possible.
            Renderer sourceRenderer = null;
            foreach (var candidate in dummy.lods)
                if (candidate.lodIndex == 0 && candidate.renderer != null)
                { sourceRenderer = candidate.renderer; break; }
            if (sourceRenderer != null)
                LightmapTransferTool.CopyRendererSettings(sourceRenderer, mr);

            // Splice into LODs[]: shift renderers from targetIdx onward down a slot.
            var newLods = new LOD[lods.Length + 1];
            for (int i = 0; i < targetIdx; i++) newLods[i] = lods[i];
            newLods[targetIdx] = new LOD(newTrans, new Renderer[] { mr });
            for (int i = targetIdx; i < lods.Length; i++) newLods[i + 1] = lods[i];

            LodGroupUtility.ApplyLods(ctx.LodGroup, newLods);

            // Save the chosen quality on the new renderer slot so the slider
            // reflects the value that was just used. Mark the renderer as
            // "fresh" so the row picks up the bright-orange highlight until
            // Apply Names is clicked.
            int newRid = mr.GetInstanceID();
            lodQualitySliders[newRid] = quality;
            freshRendererIds?.Add(newRid);

            buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup
                | FbxExportIntent.AnyUv | FbxExportIntent.Normals
                | FbxExportIntent.Tangents | FbxExportIntent.VertexColors;

            // Refresh + rebuild leaf names so the new LOD lands at its slot's
            // canonical name and any LODs that shifted down get renumbered
            // automatically — no manual Rebuild Names step required.
            ctx.LodGroup.RecalculateBounds();
            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            RebuildLeafNamesNoUndoGroup();

            Undo.CollapseUndoOperations(undoGroup);

            UvtLog.Info($"[LightmapUV] Inserted LOD{targetIdx} '{go.name}' (quality {quality:P0}, "
                + $"{res.originalTriCount} → {res.simplifiedTriCount} tris)");

            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        void DeleteLodRow(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (ctx.LodGroup == null || dummy == null || lod == null) return;
            var lods = ctx.LodGroup.GetLODs();
            if (lod.lodIndex < 0 || lod.lodIndex >= lods.Length) return;

            // Guard: refuse to delete if it would leave the LODGroup with zero
            // slots. Without this, RebuildHierarchyView produces no Dummy at all
            // and the Hierarchy UI loses every "+ Add LOD" entry point — the
            // user has to undo or switch tools to recover.
            var slot = lods[lod.lodIndex];
            int slotRendererCount = 0;
            if (slot.renderers != null)
                foreach (var r in slot.renderers) if (r != null) slotRendererCount++;
            bool isLastSlot = lods.Length == 1;
            bool wouldEmptySlot = slotRendererCount <= 1;
            if (isLastSlot && wouldEmptySlot)
            {
                UvtLog.Warn("[LightmapUV] Can't delete the last LOD level. Add another LOD first.");
                return;
            }

            Undo.SetCurrentGroupName("Prefab Builder: Delete LOD");
            int undoGroup = Undo.GetCurrentGroup();

            // Remove the renderer GameObject and its slot.
            // If the slot still has other renderers (multi-mesh per LOD), only
            // strip this renderer; otherwise drop the slot entirely.
            var renderers = slot.renderers != null
                ? new List<Renderer>(slot.renderers) : new List<Renderer>();
            renderers.Remove(lod.renderer);

            if (renderers.Count > 0)
            {
                lods[lod.lodIndex] = new LOD(slot.screenRelativeTransitionHeight, renderers.ToArray());
                LodGroupUtility.ApplyLods(ctx.LodGroup, lods);
            }
            else
            {
                var newLods = new LOD[lods.Length - 1];
                for (int i = 0, j = 0; i < lods.Length; i++)
                {
                    if (i == lod.lodIndex) continue;
                    newLods[j++] = lods[i];
                }
                LodGroupUtility.ApplyLods(ctx.LodGroup, newLods);
            }

            if (lod.renderer != null && lod.renderer.gameObject != null)
                Undo.DestroyObjectImmediate(lod.renderer.gameObject);

            // Refresh + rebuild leaf names so the slot indices and GameObject
            // names stay in sync without a manual Rebuild Names click.
            ctx.LodGroup.RecalculateBounds();
            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            RebuildLeafNamesNoUndoGroup();

            Undo.CollapseUndoOperations(undoGroup);

            buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup;

            UvtLog.Info($"[LightmapUV] Removed LOD{lod.lodIndex} from '{dummy.dummy.name}'.");

            ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        void DeleteCol(HierarchyDummy dummy, HierarchyColRow col)
        {
            if (col == null) return;

            if (col.isComponentOnly)
            {
                if (col.collider == null) return;
                UvtLog.Info($"[LightmapUV] Removed {col.typeLabel}Collider from '{col.colTransform.name}'.");
                Undo.DestroyObjectImmediate(col.collider);
            }
            else
            {
                if (col.colTransform == null) return;
                UvtLog.Info($"[LightmapUV] Removed COL '{col.colTransform.name}' from '{dummy.dummy.name}'.");
                Undo.DestroyObjectImmediate(col.colTransform.gameObject);
            }

            buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.Collision;

            if (ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        // ── Leaf rename: only fix the trailing _LOD{N} / _COL[_Hull{N}] suffix.
        // Each leaf keeps its existing base name (e.g. "Stove_Base_LOD0" stays
        // "Stove_Base_LOD0", not "Stove_LOD0") — the user controls the base by
        // editing GameObject names directly; we just renumber slots so that
        // Insert / Delete don't leave duplicates like two "*_LOD2"s.
        // Caller owns the surrounding Undo group; rename ops are recorded with
        // Undo.RecordObject so they collapse with the triggering mutation.
        void RebuildLeafNamesNoUndoGroup()
        {
            if (ctx?.LodGroup == null) return;
            if (hierarchyDummies == null) RebuildHierarchyView();
            if (hierarchyDummies == null) return;

            string rootName = ctx.LodGroup.gameObject.name;
            foreach (var dummy in hierarchyDummies)
            {
                if (dummy.dummy == null) continue;
                string fallbackBase = dummy.isRoot ? rootName : dummy.dummy.name;
                if (string.IsNullOrEmpty(fallbackBase)) fallbackBase = "Group";

                for (int i = 0; i < dummy.lods.Count; i++)
                {
                    var lod = dummy.lods[i];
                    if (lod.renderer == null) continue;
                    string current = lod.renderer.gameObject.name;
                    string baseName = UvToolContext.ExtractGroupKey(current);
                    if (string.IsNullOrEmpty(baseName)) baseName = fallbackBase;
                    string desired = $"{baseName}_LOD{lod.lodIndex}";
                    if (current == desired) continue;
                    Undo.RecordObject(lod.renderer.gameObject, "Rename LOD");
                    lod.renderer.gameObject.name = desired;
                }

                // Count standalone _COL GameObjects to pick single vs Hull{N}.
                int standaloneCount = 0;
                foreach (var c in dummy.cols)
                    if (!c.isComponentOnly) standaloneCount++;
                int hullIndex = 0;
                for (int i = 0; i < dummy.cols.Count; i++)
                {
                    var col = dummy.cols[i];
                    if (col.colTransform == null) continue;
                    if (col.isComponentOnly) continue;
                    string current = col.colTransform.gameObject.name;
                    string baseName = UvToolContext.ExtractGroupKey(current);
                    if (string.IsNullOrEmpty(baseName)) baseName = fallbackBase;
                    string desired = standaloneCount <= 1
                        ? $"{baseName}_COL"
                        : $"{baseName}_COL_Hull{hullIndex}";
                    hullIndex++;
                    if (current == desired) continue;
                    Undo.RecordObject(col.colTransform.gameObject, "Rename COL");
                    col.colTransform.gameObject.name = desired;
                }
            }
        }

        // ── Status marker: small coloured square painted at the start of a row. ──
        // Drawn via GetRect+DrawRect (a guaranteed-rendered rect from the layout)
        // so the indicator survives nested helpBox layouts that swallow post-paint
        // strokes elsewhere.
        static void DrawStatusMarker(bool fresh, bool stale)
        {
            const float w = 6f;
            const float h = 18f;
            var rect = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            if (Event.current.type != EventType.Repaint) return;
            Color color;
            if (fresh) color = new Color(1f, 0.50f, 0.05f);      // bright orange — just inserted / regenerated
            else if (stale) color = new Color(0.95f, 0.75f, 0.20f); // amber — name out of sync
            else color = new Color(0.30f, 0.30f, 0.30f, 0.35f);  // subtle gutter
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, w - 1, h - 4), color);
        }

        // ── Pre-Apply staleness detection ──
        // After Insert / Delete / Regenerate the LOD slot indices and the
        // renderer GameObject names drift apart (e.g. inserting a slot at
        // index 1 leaves the old `_LOD1` GameObject sitting in slot 2). Apply
        // Names rebuilds the canonical names; until then the row is "stale".

        bool IsLodRowStale(HierarchyDummy dummy, HierarchyLodRow lod)
        {
            if (dummy == null || lod == null || lod.renderer == null || ctx.LodGroup == null)
                return false;
            string current = lod.renderer.gameObject.name;
            string baseName = UvToolContext.ExtractGroupKey(current);
            if (string.IsNullOrEmpty(baseName))
                baseName = dummy.isRoot ? ctx.LodGroup.gameObject.name : dummy.dummy.name;
            return current != $"{baseName}_LOD{lod.lodIndex}";
        }

        bool IsColRowStale(HierarchyDummy dummy, HierarchyColRow col, int index, int totalCols)
        {
            if (dummy == null || col == null || col.colTransform == null || ctx.LodGroup == null)
                return false;
            // Component-only rows live ON the dummy/root and can't be renamed
            // independently — never report them as stale.
            if (col.isComponentOnly) return false;
            // Compute base from the existing name so custom prefixes
            // (e.g. "Stove_Base_COL") are preserved through Rebuild.
            string current = col.colTransform.gameObject.name;
            string baseName = UvToolContext.ExtractGroupKey(current);
            if (string.IsNullOrEmpty(baseName))
                baseName = dummy.isRoot ? ctx.LodGroup.gameObject.name : dummy.dummy.name;
            // Count standalone _COL siblings to choose single vs Hull{N} convention.
            int standaloneCount = 0;
            int hullIdx = -1;
            int hullPos = 0;
            foreach (var c in dummy.cols)
            {
                if (c.isComponentOnly) continue;
                if (ReferenceEquals(c, col)) hullIdx = hullPos;
                hullPos++;
                standaloneCount++;
            }
            string expected = standaloneCount <= 1
                ? $"{baseName}_COL"
                : $"{baseName}_COL_Hull{hullIdx}";
            return current != expected;
        }

        bool DummyHasStale(HierarchyDummy dummy)
        {
            if (dummy == null) return false;
            foreach (var lod in dummy.lods)
                if (IsLodRowStale(dummy, lod)) return true;
            for (int i = 0; i < dummy.cols.Count; i++)
                if (IsColRowStale(dummy, dummy.cols[i], i, dummy.cols.Count)) return true;
            return false;
        }

        // ── Channel badges: compact summary of which mesh streams have data. ──

        static string ChannelBadges(Mesh mesh)
        {
            if (mesh == null) return "";
            var parts = new List<string>();
            if (mesh.isReadable)
            {
                var tmp = new List<Vector2>();
                for (int ch = 0; ch <= 3; ch++)
                {
                    mesh.GetUVs(ch, tmp);
                    if (tmp.Count > 0) parts.Add("UV" + ch);
                }
                if (mesh.colors32 != null && mesh.colors32.Length > 0) parts.Add("VC");
                if (mesh.normals != null && mesh.normals.Length > 0) parts.Add("N");
                if (mesh.tangents != null && mesh.tangents.Length > 0) parts.Add("T");
            }
            return parts.Count == 0 ? "—" : string.Join("·", parts);
        }

        // Helper kept for use by FixSplitByMaterial, which still needs to rebuild
        // the LODGroup after restructuring renderer GameObjects.
        void RebuildLodGroupFromNames()
        {
            if (ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;
            var colSet = new HashSet<GameObject>(MeshHygieneUtility.FindCollisionObjects(root));
            var lodChildren = new SortedDictionary<int, List<Renderer>>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.transform == root) continue;
                if (colSet.Contains(r.gameObject)) continue;

                var match = System.Text.RegularExpressions.Regex.Match(
                    r.gameObject.name, @"_LOD(\d+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success) continue;

                int lodIdx = int.Parse(match.Groups[1].Value);
                if (!lodChildren.ContainsKey(lodIdx))
                    lodChildren[lodIdx] = new List<Renderer>();
                lodChildren[lodIdx].Add(r);
            }

            if (lodChildren.Count == 0) return;

            Undo.RecordObject(ctx.LodGroup, "Rebuild LODGroup");

            int lodCount = lodChildren.Count;
            var newLods = new LOD[lodCount];
            int idx = 0;
            foreach (var kvp in lodChildren)
            {
                float screenHeight = lodCount == 1
                    ? 0.01f
                    : 1f - ((float)idx / (lodCount - 1)) * 0.99f;
                newLods[idx] = new LOD(screenHeight, kvp.Value.ToArray());
                idx++;
            }
            ctx.LodGroup.SetLODs(newLods);
            ctx.LodGroup.RecalculateBounds();
        }

        // ═══════════════════════════════════════════════════════════
        // Collision section (legacy — migrates to right-panel Collider Settings in PR-2)
        // ═══════════════════════════════════════════════════════════

        void DrawCollisionSection()
        {
            EditorGUILayout.Space(8);
            collisionFoldout = EditorGUILayout.Foldout(collisionFoldout, "Collision", true);
            if (!collisionFoldout) return;

            if (ctx.LodGroup == null) return;

            var root = ctx.LodGroup.transform;
            var colObjects = MeshHygieneUtility.FindCollisionObjects(root);
            var rootCollider = root.GetComponent<MeshCollider>();

            if (rootCollider != null)
            {
                Mesh colMesh = rootCollider.sharedMesh;
                string meshInfo = colMesh != null
                    ? $"{colMesh.name} ({colMesh.vertexCount:N0}v, {MeshHygieneUtility.GetTriangleCount(colMesh):N0}t)"
                    : "(none)";
                EditorGUILayout.LabelField($"Root MeshCollider: {meshInfo}", EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                bool convex = rootCollider.convex;
                bool newConvex = EditorGUILayout.Toggle("Convex", convex);
                if (newConvex != convex)
                {
                    Undo.RecordObject(rootCollider, "Toggle Convex");
                    rootCollider.convex = newConvex;
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.LabelField("Root: no MeshCollider", EditorStyles.miniLabel);
            }

            if (colObjects.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Collision Objects", EditorStyles.boldLabel);

                foreach (var colGo in colObjects)
                {
                    if (colGo == null) continue;
                    var mf = colGo.GetComponent<MeshFilter>();
                    var mr = colGo.GetComponent<MeshRenderer>();
                    Mesh mesh = mf != null ? mf.sharedMesh : null;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"  {colGo.name}", EditorStyles.miniLabel, GUILayout.MinWidth(100));

                    if (mesh != null)
                        EditorGUILayout.LabelField($"{mesh.vertexCount:N0}v",
                            EditorStyles.miniLabel, GUILayout.Width(60));

                    if (mr != null)
                    {
                        bool vis = mr.enabled;
                        bool newVis = EditorGUILayout.Toggle(vis, GUILayout.Width(16));
                        if (newVis != vis)
                        {
                            Undo.RecordObject(mr, "Toggle COL Visibility");
                            mr.enabled = newVis;
                        }
                    }

                    if (mesh != null && (rootCollider == null || rootCollider.sharedMesh != mesh))
                    {
                        if (GUILayout.Button("Assign", GUILayout.Width(50), GUILayout.Height(16)))
                            AssignCollisionToRoot(mesh);
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Space(4);
            var bgc = GUI.backgroundColor;
            EditorGUILayout.BeginHorizontal();

            if (rootCollider == null && colObjects.Count > 0)
            {
                GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
                if (GUILayout.Button("Add MeshCollider to Root", GUILayout.Height(22)))
                {
                    var firstCol = colObjects[0];
                    var colMf = firstCol.GetComponent<MeshFilter>();
                    if (colMf != null && colMf.sharedMesh != null)
                        AssignCollisionToRoot(colMf.sharedMesh);
                }
            }

            if (rootCollider == null && colObjects.Count == 0)
            {
                GUI.backgroundColor = new Color(0.6f, 0.75f, 0.9f);
                if (GUILayout.Button("Use LOD0 as Collider", GUILayout.Height(22)))
                {
                    var lod0Entries = ctx.ForLod(0);
                    if (lod0Entries.Count > 0)
                    {
                        Mesh lod0Mesh = lod0Entries[0].originalMesh ?? lod0Entries[0].fbxMesh;
                        if (lod0Mesh != null)
                            AssignCollisionToRoot(lod0Mesh);
                    }
                }
            }

            if (colObjects.Count > 0)
            {
                GUI.backgroundColor = new Color(0.7f, 0.4f, 0.95f);
                bool anyEnabled = false;
                foreach (var go in colObjects)
                {
                    var mr = go != null ? go.GetComponent<MeshRenderer>() : null;
                    if (mr != null && mr.enabled) { anyEnabled = true; break; }
                }
                if (anyEnabled)
                {
                    if (GUILayout.Button("Hide COL Renderers", GUILayout.Height(22)))
                    {
                        foreach (var go in colObjects)
                        {
                            var mr = go != null ? go.GetComponent<MeshRenderer>() : null;
                            if (mr != null && mr.enabled)
                            {
                                Undo.RecordObject(mr, "Hide COL");
                                mr.enabled = false;
                            }
                        }
                    }
                }
            }

            GUI.backgroundColor = bgc;
            EditorGUILayout.EndHorizontal();
        }

        void AssignCollisionToRoot(Mesh mesh)
        {
            if (ctx.LodGroup == null || mesh == null) return;

            var root = ctx.LodGroup.gameObject;
            var mc = root.GetComponent<MeshCollider>();
            if (mc == null)
            {
                mc = Undo.AddComponent<MeshCollider>(root);
                UvtLog.Info($"[LightmapUV] Added MeshCollider to {root.name}");
            }
            else
            {
                Undo.RecordObject(mc, "Assign Collision Mesh");
            }

            mc.sharedMesh = mesh;
            buildIntent |= FbxExportIntent.Collision;
            UvtLog.Info($"[LightmapUV] Assigned collision mesh: {mesh.name}");
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════
        // Split / Merge section (legacy — migrates in PR-2)
        // ═══════════════════════════════════════════════════════════

        void DrawSplitMergeSection()
        {
            EditorGUILayout.Space(8);
            splitMergeFoldout = EditorGUILayout.Foldout(splitMergeFoldout, "Split / Merge", true);
            if (!splitMergeFoldout) return;

            var bgc = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.6f, 0.75f, 0.9f);
            if (GUILayout.Button("Scan Split/Merge", GUILayout.Height(22)))
                ScanSplitMerge();
            GUI.backgroundColor = bgc;

            if (splitCandidates != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Split Multi-Material", EditorStyles.boldLabel);

                if (splitCandidates.Count == 0)
                {
                    EditorGUILayout.LabelField("  No multi-material meshes.", EditorStyles.miniLabel);
                }
                else
                {
                    for (int i = 0; i < splitCandidates.Count; i++)
                    {
                        var sc = splitCandidates[i];
                        if (sc.entry.renderer == null) continue;
                        var mesh = sc.entry.originalMesh ?? sc.entry.fbxMesh;
                        if (mesh == null) continue;
                        var mats = sc.entry.renderer.sharedMaterials;

                        EditorGUILayout.BeginHorizontal();
                        sc.include = EditorGUILayout.Toggle(sc.include, GUILayout.Width(16));
                        splitCandidates[i] = sc;

                        string info = $"LOD{sc.entry.lodIndex} {sc.entry.renderer.name}: {mesh.subMeshCount} submeshes";
                        EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
                        EditorGUILayout.EndHorizontal();

                        if (sc.include)
                        {
                            string srcName = sc.entry.renderer.name;
                            string lodSuffix = "";
                            var lodMatch = System.Text.RegularExpressions.Regex.Match(
                                srcName, @"([_\-\s]+LOD\d+)$",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (lodMatch.Success)
                            {
                                lodSuffix = lodMatch.Value;
                                srcName = srcName.Substring(0, srcName.Length - lodSuffix.Length);
                            }

                            EditorGUILayout.LabelField("      Create:", EditorStyles.miniLabel);
                            for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                            {
                                string matName = mats[s] != null ? mats[s].name : $"mat{s}";
                                string newName = $"{srcName}_{matName}{lodSuffix}";
                                EditorGUILayout.LabelField($"        {newName}", EditorStyles.miniLabel);
                            }
                        }
                    }

                    EditorGUILayout.BeginHorizontal();
                    int splitSel = 0;
                    foreach (var s in splitCandidates) if (s.include) splitSel++;

                    GUI.backgroundColor = new Color(0.3f, 0.7f, 1f);
                    GUI.enabled = splitSel > 0;
                    if (GUILayout.Button("Preview Split", GUILayout.Height(24)))
                    {
                        preview.Restore();
                        previewMode = PreviewMode.None;
                        preview.ActivateSplitPreview(ctx, splitCandidates.FindAll(s => s.include)
                            .ConvertAll(s => (s.entry.renderer, s.entry.originalMesh ?? s.entry.fbxMesh)));
                    }

                    GUI.backgroundColor = new Color(0.7f, 0.4f, 0.95f);
                    if (GUILayout.Button($"Split ({splitSel})", GUILayout.Height(24)))
                        FixSplitByMaterial();
                    GUI.enabled = true;
                    GUI.backgroundColor = bgc;
                    EditorGUILayout.EndHorizontal();
                }
            }

            if (mergeCandidates != null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Merge Same-Material", EditorStyles.boldLabel);

                if (mergeCandidates.Count == 0)
                {
                    EditorGUILayout.LabelField("  No merge candidates.", EditorStyles.miniLabel);
                }
                else
                {
                    for (int i = 0; i < mergeCandidates.Count; i++)
                    {
                        var g = mergeCandidates[i];
                        EditorGUILayout.BeginHorizontal();
                        g.include = EditorGUILayout.Toggle(g.include, GUILayout.Width(16));
                        mergeCandidates[i] = g;

                        string info = $"LOD{g.lodIndex} \"{g.material.name}\": {g.entries.Count} objects";
                        EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
                        EditorGUILayout.EndHorizontal();

                        if (g.include)
                        {
                            int totalVerts = 0;
                            foreach (var e in g.entries)
                            {
                                var mesh = e.originalMesh ?? e.fbxMesh;
                                int verts = mesh != null ? mesh.vertexCount : 0;
                                totalVerts += verts;
                                EditorGUILayout.LabelField(
                                    $"        {e.renderer.name} ({verts:N0} v)", EditorStyles.miniLabel);
                            }
                            EditorGUILayout.LabelField(
                                $"      -> merged ({totalVerts:N0} v)", EditorStyles.miniLabel);
                        }
                    }

                    int mergeSel = 0;
                    foreach (var g in mergeCandidates) if (g.include) mergeSel++;

                    GUI.backgroundColor = new Color(0.7f, 0.4f, 0.95f);
                    GUI.enabled = mergeSel > 0;
                    if (GUILayout.Button($"Merge ({mergeSel})", GUILayout.Height(24)))
                        FixMerge();
                    GUI.enabled = true;
                    GUI.backgroundColor = bgc;
                }
            }
        }

        void ScanSplitMerge()
        {
            splitCandidates = new List<SplitCandidate>();
            mergeCandidates = new List<MergeGroup>();

            var mergeMap = new Dictionary<string, MergeGroup>();

            for (int li = 0; li < ctx.LodCount; li++)
            {
                var entries = ctx.ForLod(li);
                foreach (var e in entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null || e.renderer == null) continue;

                    if (mesh.subMeshCount > 1)
                    {
                        splitCandidates.Add(new SplitCandidate
                        {
                            entry = e,
                            include = true
                        });
                    }

                    var mats = e.renderer.sharedMaterials;
                    if (mesh.subMeshCount == 1 && mats.Length == 1 && mats[0] != null)
                    {
                        string key = $"{li}_{mats[0].GetInstanceID()}";
                        if (!mergeMap.ContainsKey(key))
                            mergeMap[key] = new MergeGroup
                            {
                                lodIndex = li,
                                material = mats[0],
                                entries = new List<MeshEntry>(),
                                include = true
                            };
                        mergeMap[key].entries.Add(e);
                    }
                }
            }

            foreach (var kvp in mergeMap)
                if (kvp.Value.entries.Count > 1)
                    mergeCandidates.Add(kvp.Value);

            UvtLog.Info($"[LightmapUV] Split/Merge scan: {splitCandidates.Count} split, {mergeCandidates.Count} merge.");
        }

        void FixSplitByMaterial()
        {
            if (splitCandidates == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Split by Material");
            int undoGroup = Undo.GetCurrentGroup();
            int split = 0;

            foreach (var sc in splitCandidates)
            {
                if (!sc.include) continue;
                if (sc.entry.renderer == null || sc.entry.meshFilter == null) continue;
                var mesh = sc.entry.originalMesh ?? sc.entry.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                var mats = sc.entry.renderer.sharedMaterials;
                string srcName = sc.entry.renderer.name;
                string lodSuffix = "";
                var lodMatch = System.Text.RegularExpressions.Regex.Match(
                    srcName, @"([_\-\s]+LOD\d+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (lodMatch.Success)
                {
                    lodSuffix = lodMatch.Value;
                    srcName = srcName.Substring(0, srcName.Length - lodSuffix.Length);
                }

                var parent = sc.entry.renderer.transform.parent;

                for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                {
                    string matName = mats[s] != null ? mats[s].name : $"mat{s}";
                    string childName = $"{srcName}_{matName}{lodSuffix}";

                    var subTris = mesh.GetTriangles(s);
                    var subMesh = MeshHygieneUtility.ExtractSubmesh(mesh, subTris);
                    if (subMesh == null) continue;
                    subMesh.name = childName;

                    var childGo = new GameObject(childName);
                    Undo.RegisterCreatedObjectUndo(childGo, "Split");
                    childGo.transform.SetParent(parent, false);
                    childGo.transform.localPosition = sc.entry.renderer.transform.localPosition;
                    childGo.transform.localRotation = sc.entry.renderer.transform.localRotation;
                    childGo.transform.localScale = sc.entry.renderer.transform.localScale;

                    var newMf = childGo.AddComponent<MeshFilter>();
                    newMf.sharedMesh = subMesh;
                    var newMr = childGo.AddComponent<MeshRenderer>();
                    newMr.sharedMaterials = new[] { mats[s] };

                    GameObjectUtility.SetStaticEditorFlags(childGo,
                        GameObjectUtility.GetStaticEditorFlags(sc.entry.renderer.gameObject));
                }

                UvtLog.Info($"[LightmapUV] Split: {sc.entry.renderer.name} → {mesh.subMeshCount} children");
                Undo.DestroyObjectImmediate(sc.entry.renderer.gameObject);
                split++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (split > 0)
            {
                ctx.Refresh(ctx.LodGroup);
                RebuildLodGroupFromNames();
                ctx.LodGroup.RecalculateBounds();
                buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup
                    | FbxExportIntent.Materials;
            }

            splitCandidates = null;
            mergeCandidates = null;
            preview?.Restore();
            previewMode = PreviewMode.None;
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        void FixMerge()
        {
            if (mergeCandidates == null) return;

            Undo.SetCurrentGroupName("Prefab Builder: Merge");
            int undoGroup = Undo.GetCurrentGroup();
            int merged = 0;

            foreach (var g in mergeCandidates)
            {
                if (!g.include || g.entries.Count < 2) continue;

                var firstEntry = g.entries[0];
                if (firstEntry.renderer == null) continue;

                var baseMatrix = firstEntry.renderer.transform.worldToLocalMatrix;

                var allPos = new List<Vector3>();
                var allNormals = new List<Vector3>();
                var allUvs = new List<Vector2>();
                var allTris = new List<int>();
                var destroyList = new List<GameObject>();

                foreach (var e in g.entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null || e.renderer == null) continue;

                    var verts = mesh.vertices;
                    var normals = mesh.normals;
                    var uvs = mesh.uv;
                    var tris = mesh.triangles;

                    Matrix4x4 toFirst = baseMatrix * e.renderer.transform.localToWorldMatrix;
                    int vertBase = allPos.Count;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        allPos.Add(toFirst.MultiplyPoint3x4(verts[v]));
                        if (normals != null && v < normals.Length)
                            allNormals.Add(toFirst.MultiplyVector(normals[v]).normalized);
                        if (uvs != null && v < uvs.Length)
                            allUvs.Add(uvs[v]);
                    }

                    for (int t = 0; t < tris.Length; t++)
                        allTris.Add(tris[t] + vertBase);

                    if (e != firstEntry)
                        destroyList.Add(e.renderer.gameObject);
                }

                var mergedMesh = new Mesh();
                mergedMesh.name = firstEntry.renderer.name;
                mergedMesh.SetVertices(allPos);
                if (allNormals.Count == allPos.Count) mergedMesh.SetNormals(allNormals);
                if (allUvs.Count == allPos.Count) mergedMesh.SetUVs(0, allUvs);
                mergedMesh.SetTriangles(allTris, 0);
                mergedMesh.RecalculateBounds();

                Undo.RecordObject(firstEntry.meshFilter, "Merge");
                firstEntry.meshFilter.sharedMesh = mergedMesh;

                var lods = ctx.LodGroup.GetLODs();
                for (int li = 0; li < lods.Length; li++)
                {
                    var renderers = new List<Renderer>(lods[li].renderers);
                    bool replaced = false;
                    for (int ri = renderers.Count - 1; ri >= 0; ri--)
                    {
                        if (renderers[ri] == null) continue;
                        if (destroyList.Contains(renderers[ri].gameObject))
                        {
                            if (!replaced)
                            {
                                renderers[ri] = firstEntry.renderer;
                                replaced = true;
                            }
                            else
                                renderers.RemoveAt(ri);
                        }
                    }
                    lods[li].renderers = renderers.ToArray();
                }
                ctx.LodGroup.SetLODs(lods);

                foreach (var go in destroyList)
                {
                    if (go == null) continue;
                    Undo.DestroyObjectImmediate(go);
                }

                merged++;
                UvtLog.Info($"[LightmapUV] Merged: {g.entries.Count} objects → {firstEntry.renderer.name}");
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (merged > 0)
            {
                ctx.Refresh(ctx.LodGroup);
                ctx.LodGroup.RecalculateBounds();
                buildIntent |= FbxExportIntent.Hierarchy | FbxExportIntent.LodGroup;
            }

            splitCandidates = null;
            mergeCandidates = null;
            preview?.Restore();
            previewMode = PreviewMode.None;
            hierarchyDummies = null;
            requestRepaint?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════
        // Mesh info / edge / problem report sections
        // ═══════════════════════════════════════════════════════════

        void DrawMeshInfo()
        {
            if (ctx.MeshEntries == null || ctx.MeshEntries.Count == 0) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Mesh Entries", EditorStyles.boldLabel);

            int lodCount = ctx.LodCount;
            for (int li = 0; li < lodCount; li++)
            {
                var entries = ctx.ForLod(li);
                if (entries.Count == 0) continue;

                int totalVerts = 0, totalTris = 0;
                foreach (var e in entries)
                {
                    Mesh m = e.originalMesh ?? e.fbxMesh;
                    if (m == null) continue;
                    totalVerts += m.vertexCount;
                    totalTris += MeshHygieneUtility.GetTriangleCount(m);
                }

                EditorGUILayout.LabelField(
                    $"  LOD{li}: {entries.Count} mesh(es), {totalVerts:N0}v / {totalTris:N0}t",
                    EditorStyles.miniLabel);
            }
        }

        void BuildEdgeReports()
        {
            edgeReports = new List<MeshEdgeReport>();
            if (ctx?.MeshEntries == null) return;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                var report = EdgeAnalyzer.Analyze(mesh, out _);
                edgeReports.Add(new MeshEdgeReport
                {
                    meshName = mesh.name,
                    report = report
                });
            }
        }

        void DrawEdgeReportSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Edge Analysis", EditorStyles.boldLabel);

            foreach (var er in edgeReports)
            {
                string name = er.meshName;
                if (name.Length > 25) name = name.Substring(0, 22) + "...";

                var r = er.report;
                var parts = new List<string>();
                if (r.borderEdges > 0) parts.Add($"border:{r.borderEdges}");
                if (r.uvSeamEdges > 0) parts.Add($"seam:{r.uvSeamEdges}");
                if (r.hardEdges > 0) parts.Add($"hard:{r.hardEdges}");
                if (r.nonManifoldEdges > 0) parts.Add($"non-manifold:{r.nonManifoldEdges}");
                if (r.uvFoldoverEdges > 0) parts.Add($"foldover:{r.uvFoldoverEdges}");

                string info = parts.Count > 0 ? string.Join(", ", parts) : "clean";
                EditorGUILayout.LabelField($"  {name}", $"{r.totalEdges} edges: {info}",
                    EditorStyles.miniLabel);
            }
        }

        void BuildProblemSummaries()
        {
            problemSummaries = new List<ProblemSummary>();
            if (ctx?.MeshEntries == null) return;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                int degCount = MeshHygieneUtility.CountDegenerateTriangles(mesh);
                int unusedCount = MeshHygieneUtility.CountUnusedVertices(mesh);
                var seamVerts = Uv0Analyzer.GetFalseSeamVertices(mesh);
                int seamCount = seamVerts?.Count ?? 0;

                if (degCount > 0 || unusedCount > 0 || seamCount > 0)
                {
                    problemSummaries.Add(new ProblemSummary
                    {
                        meshName = mesh.name,
                        degenerateTris = degCount,
                        unusedVerts = unusedCount,
                        falseSeamVerts = seamCount
                    });
                }
            }
        }

        void DrawProblemSummarySection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Problem Areas", EditorStyles.boldLabel);

            if (problemSummaries.Count == 0)
            {
                EditorGUILayout.LabelField("  No problems detected.", EditorStyles.miniLabel);
                return;
            }

            foreach (var ps in problemSummaries)
            {
                string name = ps.meshName;
                if (name.Length > 25) name = name.Substring(0, 22) + "...";

                var parts = new List<string>();
                if (ps.degenerateTris > 0) parts.Add($"degen:{ps.degenerateTris}");
                if (ps.unusedVerts > 0) parts.Add($"unused:{ps.unusedVerts}");
                if (ps.falseSeamVerts > 0) parts.Add($"weld:{ps.falseSeamVerts}");

                EditorGUILayout.LabelField($"  {name}", string.Join(", ", parts),
                    EditorStyles.miniLabel);
            }
        }

        void DrawEdgeLegend()
        {
            if (previewMode != PreviewMode.EdgeWireframe && previewMode != PreviewMode.ProblemAreas)
                return;

            EditorGUILayout.Space(8);

            if (previewMode == PreviewMode.EdgeWireframe)
            {
                EditorGUILayout.LabelField("Legend", EditorStyles.boldLabel);
                DrawLegendItem(new Color(1f, 1f, 1f), "Border");
                DrawLegendItem(new Color(1f, 0.9f, 0.2f), "UV Seam");
                DrawLegendItem(new Color(0.3f, 0.5f, 1f), "Hard Edge");
                DrawLegendItem(new Color(1f, 0.2f, 0.2f), "Non-Manifold");
                DrawLegendItem(new Color(1f, 0.3f, 0.9f), "UV Foldover");
                DrawLegendItem(new Color(0.4f, 0.4f, 0.4f), "Interior");
            }
            else if (previewMode == PreviewMode.ProblemAreas)
            {
                EditorGUILayout.LabelField("Legend", EditorStyles.boldLabel);
                DrawLegendItem(new Color(1f, 0.16f, 0.16f), "Degenerate Tri");
                DrawLegendItem(new Color(0.16f, 0.86f, 0.86f), "Weld Candidate");
                DrawLegendItem(new Color(1f, 0.86f, 0.16f), "Unused Vertex (dot)");
                DrawLegendItem(new Color(0.24f, 0.71f, 0.31f), "Healthy");
            }
        }

        void DrawLegendItem(Color color, string label)
        {
            var rect = EditorGUILayout.GetControlRect(false, 16);
            rect.x += 8;
            var colorRect = new Rect(rect.x, rect.y + 3, 10, 10);
            EditorGUI.DrawRect(colorRect, color);
            var labelRect = new Rect(rect.x + 16, rect.y, rect.width - 24, rect.height);
            EditorGUI.LabelField(labelRect, label, EditorStyles.miniLabel);
        }

        // ═══════════════════════════════════════════════════════════
        // Scene integration
        // ═══════════════════════════════════════════════════════════

        public void OnSceneGUI(SceneView sv)
        {
            if (previewMode == PreviewMode.EdgeWireframe && preview != null && preview.HasEdgeOverlays)
            {
                preview.DrawEdgeWireframe(sv);
            }

            if (previewMode == PreviewMode.ProblemAreas && preview != null && preview.HasUnusedVertOverlays)
            {
                preview.DrawUnusedVertexDots();
            }
        }

        // ── Unused interface methods ──

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield break;
        }
    }
}
