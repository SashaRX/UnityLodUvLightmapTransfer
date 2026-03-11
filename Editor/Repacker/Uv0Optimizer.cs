// Uv0Optimizer.cs — Main facade for the UV0 Atlas Optimizer pipeline
// Orchestrates: Shell extraction → Thumbnailing → Similarity → Grouping → Packing → Baking
// Part of UV0 Atlas Optimizer

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Main entry point for the UV0 Atlas Optimizer.
    /// Provides Analyze (steps 1-5) and PackAndBake (steps 6-10) methods.
    /// </summary>
    public static class Uv0Optimizer
    {
        // ════════════════════════════════════════════════════════════
        //  Data structures
        // ════════════════════════════════════════════════════════════

        public struct SimilarityWeights
        {
            public float albedo;
            public float normal;
            public float gloss;
            public float ao;

            public static SimilarityWeights Default => new SimilarityWeights
            {
                albedo = 0.50f,
                normal = 0.30f,
                gloss  = 0.12f,
                ao     = 0.08f
            };
        }

        public struct PackBakeSettings
        {
            public int atlasSize;
            public int padding;
            public string postfix;

            public static PackBakeSettings Default => new PackBakeSettings
            {
                atlasSize = 2048,
                padding = 4,
                postfix = "_repack"
            };
        }

        /// <summary>
        /// Result of the analysis phase. Cached for user review before packing.
        /// </summary>
        public class AnalysisResult
        {
            public int totalShells;
            public int groupCount;
            public int monotoneCount;
            public float estimatedSaving;

            // Internal data for Pack & Bake phase
            internal List<ShellGroup> groups;
            internal ShellThumbnail[] thumbnails;
            internal float[,] similarityMatrix;
            internal LodShellCorrespondence.LodCorrespondenceSet correspondence;
            internal Vector3[][] allVerts;
            internal int[][] allTris;
            internal Vector2[][] allUv0;
            internal List<UvShell>[] allShells;
            internal float[] shellAreas;
            internal Material material;
            internal Mesh[] meshes;
        }

        // ════════════════════════════════════════════════════════════
        //  Analyze — Steps 1-5 (shell extraction → grouping)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Analyze a LODGroup for shell similarity and build groups.
        /// Does NOT modify any assets.
        /// </summary>
        public static AnalysisResult Analyze(LODGroup lodGroup, float threshold,
            float maxSizeRatio, SimilarityWeights weights)
        {
            EditorUtility.DisplayProgressBar("UV0 Optimizer", "Collecting meshes...", 0f);
            try
            {
                var lods = lodGroup.GetLODs();
                int lodCount = lods.Length;

                // Collect mesh data per LOD
                var allVerts = new Vector3[lodCount][];
                var allTris = new int[lodCount][];
                var allUv0 = new Vector2[lodCount][];
                var allShells = new List<UvShell>[lodCount];
                var meshes = new Mesh[lodCount];
                Material material = null;

                UvtLog.Info($"[UV0 Optimizer] Analyze: LODGroup '{lodGroup.name}', {lodCount} LODs, threshold={threshold:F2}, maxSizeRatio={maxSizeRatio:F1}");

                for (int li = 0; li < lodCount; li++)
                {
                    var renderers = lods[li].renderers;
                    if (renderers == null || renderers.Length == 0) continue;

                    // Take first valid renderer
                    foreach (var r in renderers)
                    {
                        if (r == null) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null) continue;

                        Mesh m = mf.sharedMesh;
                        meshes[li] = m;
                        allVerts[li] = m.vertices;
                        allTris[li] = m.triangles;

                        var uvList = new List<Vector2>();
                        m.GetUVs(0, uvList);
                        allUv0[li] = uvList.ToArray();

                        // Extract shells
                        allShells[li] = UvShellExtractor.Extract(allUv0[li], allTris[li]);

                        UvtLog.Info($"[UV0 Optimizer]   LOD{li}: '{m.name}' verts={m.vertexCount} tris={allTris[li].Length / 3} uv0={allUv0[li].Length} shells={allShells[li].Count}");

                        // Get material from first LOD0 renderer
                        if (li == 0 && material == null)
                        {
                            var mr = r.GetComponent<Renderer>();
                            if (mr != null && mr.sharedMaterial != null)
                                material = mr.sharedMaterial;
                        }

                        break; // Take first mesh per LOD
                    }
                }

                if (allShells[0] == null || allShells[0].Count == 0)
                    throw new System.Exception("No shells found in LOD0");

                // ── Step 2: Sample thumbnails (LOD0 only) ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Sampling textures...", 0.2f);

                Texture2D albedo, normal, gloss, ao;
                TextureChannelSampler.GetMaterialTextures(material, out albedo, out normal, out gloss, out ao);

                UvtLog.Info($"[UV0 Optimizer] Material: '{(material != null ? material.name : "null")}', " +
                    $"albedo={(albedo != null ? $"{albedo.name} {albedo.width}x{albedo.height}" : "none")}");

                var thumbnails = new ShellThumbnail[allShells[0].Count];
                var shellAreas = new float[allShells[0].Count];

                for (int si = 0; si < allShells[0].Count; si++)
                {
                    var shell = allShells[0][si];
                    Rect bbox = MeshUvUtils.GetShellUvBbox(allUv0[0], shell.faceIndices, allTris[0]);
                    shellAreas[si] = MeshUvUtils.GetShellUvAreaAbs(allUv0[0], shell.faceIndices, allTris[0]);

                    var thumb = TextureChannelSampler.Sample(albedo, normal, gloss, ao, bbox);
                    thumb.shellIndex = si;
                    thumb.uvBbox = bbox;

                    // Check monotone
                    thumb.isMonotone = MonotoneDetector.IsShellMonotone(thumb);
                    if (thumb.isMonotone)
                        thumb.monotoneColor = PerceptualHasher.AverageColor(thumb.albedo);

                    thumbnails[si] = thumb;

                    if (si % 10 == 0)
                        EditorUtility.DisplayProgressBar("UV0 Optimizer",
                            $"Sampling shell {si + 1}/{allShells[0].Count}...",
                            0.2f + 0.2f * si / allShells[0].Count);
                }

                // Log thumbnail results
                {
                    int monoCount = 0;
                    float totalArea = 0f;
                    for (int si = 0; si < thumbnails.Length; si++)
                    {
                        if (thumbnails[si].isMonotone) monoCount++;
                        totalArea += shellAreas[si];
                        string monoTag = "";
                        if (thumbnails[si].isMonotone)
                        {
                            var mc = thumbnails[si].monotoneColor;
                            monoTag = $" MONO(#{ColorUtility.ToHtmlStringRGB(mc)})";
                        }
                        UvtLog.Verbose($"[UV0 Optimizer]   shell {si}: area={shellAreas[si]:F4} bbox=({thumbnails[si].uvBbox.x:F3},{thumbnails[si].uvBbox.y:F3},{thumbnails[si].uvBbox.width:F3},{thumbnails[si].uvBbox.height:F3}){monoTag}");
                    }
                    UvtLog.Info($"[UV0 Optimizer] Thumbnails: {thumbnails.Length} shells sampled, {monoCount} monotone, totalArea={totalArea:F3}");
                }

                // ── Step 3: Build similarity matrix (albedo-only) ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Computing similarity (albedo)...", 0.4f);

                float[,] simMatrix = ShellSimilarityAnalyzer.BuildSimilarityMatrix(
                    thumbnails, weights, shellAreas, maxSizeRatio);

                // ── Step 4: LOD correspondence ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Building LOD correspondence...", 0.6f);

                var correspondence = LodShellCorrespondence.Build(allVerts, allTris, allUv0, allShells);

                // ── Step 5: Build groups ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Building groups...", 0.7f);

                var groups = ShellGroupBuilder.Build(simMatrix, allShells[0], allShells,
                    correspondence, threshold, thumbnails, shellAreas);

                // Log group results
                {
                    int multiGroups = 0;
                    foreach (var g in groups)
                    {
                        int lod0 = g.Lod0MemberCount;
                        if (lod0 > 1)
                        {
                            multiGroups++;
                            var memberIds = new List<string>();
                            foreach (var m in g.members)
                                if (m.lodLevel == 0)
                                    memberIds.Add(m.shellId.ToString());
                            string monoTag = g.isMonotone ? $" MONO(#{ColorUtility.ToHtmlStringRGB(g.monotoneColor)})" : "";
                            UvtLog.Info($"[UV0 Optimizer] Group (src={g.sourceShellId}): {lod0} LOD0 shells [{string.Join(",", memberIds)}]{monoTag}");
                        }
                    }
                    UvtLog.Info($"[UV0 Optimizer] Grouping: {groups.Count} groups total, {multiGroups} with matches");
                }

                // ── Step 6: Find best transforms for group members ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Computing transforms...", 0.8f);

                foreach (var group in groups)
                {
                    if (group.Lod0MemberCount <= 1) continue;

                    int srcId = group.sourceShellId;
                    if (srcId >= thumbnails.Length) continue;
                    var srcThumb = thumbnails[srcId];

                    for (int mi = 0; mi < group.members.Count; mi++)
                    {
                        var member = group.members[mi];
                        if (member.lodLevel != 0 || member.shellId == srcId) continue;
                        if (member.shellId >= thumbnails.Length) continue;

                        var memberThumb = thumbnails[member.shellId];
                        member.transform = NormalAwareTransformer.FindBestTransform(srcThumb, memberThumb);
                        group.members[mi] = member; // struct copy-back
                    }
                }

                // ── Compute estimated saving ──
                float saving = ShellGroupBuilder.EstimateOccupancySaving(groups, shellAreas);
                int monotoneCount = 0;
                foreach (var g in groups) if (g.isMonotone) monotoneCount++;

                return new AnalysisResult
                {
                    totalShells = allShells[0].Count,
                    groupCount = groups.Count,
                    monotoneCount = monotoneCount,
                    estimatedSaving = saving,

                    groups = groups,
                    thumbnails = thumbnails,
                    similarityMatrix = simMatrix,
                    correspondence = correspondence,
                    allVerts = allVerts,
                    allTris = allTris,
                    allUv0 = allUv0,
                    allShells = allShells,
                    shellAreas = shellAreas,
                    material = material,
                    meshes = meshes
                };
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Pack & Bake — Steps 6-10
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Pack shell groups and bake atlas textures. Saves new meshes and textures.
        /// </summary>
        public static void PackAndBake(LODGroup lodGroup, AnalysisResult analysis, PackBakeSettings settings)
        {
            UvtLog.Info($"[UV0 Optimizer] PackAndBake: atlasSize={settings.atlasSize}, padding={settings.padding}, postfix='{settings.postfix}'");
            EditorUtility.DisplayProgressBar("UV0 Optimizer", "Packing atlas...", 0f);
            try
            {
                // ── Pack ──
                var packerSettings = new RepackerAtlasPacker.PackerSettings
                {
                    atlasSize = settings.atlasSize,
                    padding = settings.padding,
                    texelDensity = 1f
                };

                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Running xatlas packer...", 0.1f);

                var packResult = RepackerAtlasPacker.Pack(
                    analysis.groups, analysis.allUv0, analysis.allTris,
                    analysis.allShells, analysis.correspondence, packerSettings);

                if (packResult == null)
                    throw new System.Exception("Packing failed — xatlas returned null");

                // ── Bake textures ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Baking textures...", 0.4f);

                Texture2D albedo, normal, gloss, ao;
                TextureChannelSampler.GetMaterialTextures(analysis.material,
                    out albedo, out normal, out gloss, out ao);

                // Determine output directory (next to original mesh)
                string outputDir = "Assets";
                if (analysis.meshes[0] != null)
                {
                    string meshPath = AssetDatabase.GetAssetPath(analysis.meshes[0]);
                    if (!string.IsNullOrEmpty(meshPath))
                        outputDir = System.IO.Path.GetDirectoryName(meshPath);
                }

                var bakerSettings = new TextureAtlasBaker.BakerSettings
                {
                    atlasSize = settings.atlasSize,
                    postfix = settings.postfix
                };

                var bakedTextures = TextureAtlasBaker.Bake(
                    packResult, analysis.groups,
                    albedo, normal, gloss, ao,
                    analysis.allUv0[0], analysis.allTris[0], analysis.allShells[0],
                    bakerSettings, outputDir);

                // ── Apply new UV0 to meshes ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Applying UV0...", 0.7f);

                var repackedMeshes = RepackerUv0Updater.Apply(
                    packResult, analysis.meshes, settings.postfix);

                // ── Update material ──
                EditorUtility.DisplayProgressBar("UV0 Optimizer", "Updating material...", 0.9f);

                if (analysis.material != null && bakedTextures.Count > 0)
                    RepackerMaterialUpdater.Apply(analysis.material, bakedTextures);

                // ── Apply meshes to renderers ──
                RepackerMaterialUpdater.ApplyMeshes(lodGroup, repackedMeshes);

                UvtLog.Info($"[UV0 Optimizer] Complete. Atlas {packResult.atlasWidth}x{packResult.atlasHeight}, " +
                            $"{analysis.groupCount} groups, {bakedTextures.Count} textures baked.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
