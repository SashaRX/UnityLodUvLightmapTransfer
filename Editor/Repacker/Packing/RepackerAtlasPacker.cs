// RepackerAtlasPacker.cs — xatlas AddUvMesh packing for grouped shells
// Part of UV0 Atlas Optimizer

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Packs shell groups into a new atlas using xatlas in AddUvMesh mode.
    /// Each group contributes one chart (the source shell triangles).
    /// Monotone groups get minimal chart size.
    /// After packing, member shells get their new UV0 via transform from source.
    /// </summary>
    public static class RepackerAtlasPacker
    {
        /// <summary>
        /// Result of the packing operation.
        /// </summary>
        public class PackResult
        {
            /// <summary>New UV0 per LOD level per vertex.</summary>
            public Vector2[][] newUv0PerLod;
            /// <summary>Atlas rect per group in normalized [0,1] space.</summary>
            public Rect[] groupRects;
            /// <summary>Atlas width in pixels.</summary>
            public int atlasWidth;
            /// <summary>Atlas height in pixels.</summary>
            public int atlasHeight;
        }

        public struct PackerSettings
        {
            public int atlasSize;
            public int padding;
            public float texelDensity;

            public static PackerSettings Default => new PackerSettings
            {
                atlasSize = 2048,
                padding = 4,
                texelDensity = 1f
            };
        }

        /// <summary>
        /// Pack shell groups into a new UV0 atlas.
        /// </summary>
        /// <param name="groups">Shell groups from ShellGroupBuilder.</param>
        /// <param name="allUv0">Per-LOD UV0 arrays (original).</param>
        /// <param name="allTris">Per-LOD triangle index arrays.</param>
        /// <param name="allShells">Per-LOD shell lists.</param>
        /// <param name="corr">LOD correspondence set.</param>
        /// <param name="settings">Packer settings.</param>
        public static PackResult Pack(
            List<ShellGroup> groups,
            Vector2[][] allUv0,
            int[][] allTris,
            List<UvShell>[] allShells,
            LodShellCorrespondence.LodCorrespondenceSet corr,
            PackerSettings settings)
        {
            // Step 1: Build per-group UV mesh from LOD0 source shell triangles
            // We collect all source shell triangles, submit them to xatlas as separate meshes,
            // then read back the repacked UV coordinates.

            XatlasNative.xatlasCreate();
            try
            {
                var groupSourceShells = new List<UvShell>();
                var groupIndices = new List<int>(); // maps xatlas mesh index → group index

                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var group = groups[gi];
                    int srcId = group.sourceShellId;
                    UvShell srcShell = null;

                    // Find source shell in LOD0
                    foreach (var s in allShells[0])
                    {
                        if (s.shellId == srcId)
                        {
                            srcShell = s;
                            break;
                        }
                    }

                    if (srcShell == null)
                    {
                        UvtLog.Warning($"[RepackerAtlasPacker] Source shell {srcId} not found in LOD0");
                        continue;
                    }

                    groupSourceShells.Add(srcShell);

                    // Collect UV data for this shell
                    var shellUvs = new List<float>();
                    var shellIndices = new List<uint>();
                    var vertexRemap = new Dictionary<int, int>();

                    foreach (int fi in srcShell.faceIndices)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = allTris[0][fi * 3 + j];
                            if (!vertexRemap.TryGetValue(vi, out int newIdx))
                            {
                                newIdx = vertexRemap.Count;
                                vertexRemap[vi] = newIdx;
                                Vector2 uv = vi < allUv0[0].Length ? allUv0[0][vi] : Vector2.zero;
                                shellUvs.Add(uv.x);
                                shellUvs.Add(uv.y);
                            }
                            shellIndices.Add((uint)newIdx);
                        }
                    }

                    // Submit to xatlas
                    uint vertCount = (uint)vertexRemap.Count;
                    uint faceCount = (uint)srcShell.faceIndices.Count;
                    uint[] faceMaterials = new uint[faceCount]; // all same material

                    XatlasNative.xatlasAddUvMesh(
                        shellUvs.ToArray(), vertCount,
                        shellIndices.ToArray(), (uint)shellIndices.Count,
                        faceMaterials, faceCount);

                    groupIndices.Add(gi);
                }

                // Pack
                XatlasNative.xatlasPackCharts(
                    settings.atlasSize,
                    (uint)settings.padding,
                    settings.texelDensity,
                    (uint)settings.atlasSize,
                    1, // bilinear
                    0, // blockAlign
                    0  // bruteForce
                );

                uint atlasW = XatlasNative.xatlasGetAtlasWidth();
                uint atlasH = XatlasNative.xatlasGetAtlasHeight();

                if (atlasW == 0 || atlasH == 0)
                {
                    UvtLog.Error("[RepackerAtlasPacker] xatlas produced 0-size atlas");
                    return null;
                }

                // Step 2: Read back packed UVs and build group rects
                var result = new PackResult
                {
                    atlasWidth = (int)atlasW,
                    atlasHeight = (int)atlasH,
                    groupRects = new Rect[groups.Count]
                };

                // Map: group → new UV coordinates for source shell vertices
                var groupNewUvs = new Dictionary<int, Dictionary<int, Vector2>>(); // groupIdx → (origVertIdx → newUv)

                for (int mi = 0; mi < groupIndices.Count; mi++)
                {
                    int gi = groupIndices[mi];
                    int outVertCount = XatlasNative.xatlasGetOutputVertexCount(mi);
                    int outIdxCount = XatlasNative.xatlasGetOutputIndexCount(mi);

                    if (outVertCount <= 0) continue;

                    uint[] xref = new uint[outVertCount];
                    float[] outUv = new float[outVertCount * 2];
                    uint[] outChart = new uint[outVertCount];
                    XatlasNative.xatlasGetOutputVertexData(mi, xref, outUv, outChart, outVertCount);

                    // Build vertex remap for this group's source shell
                    UvShell srcShell = groupSourceShells[mi];
                    var vertexRemap = new Dictionary<int, int>();
                    foreach (int fi in srcShell.faceIndices)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = allTris[0][fi * 3 + j];
                            if (!vertexRemap.ContainsKey(vi))
                                vertexRemap[vi] = vertexRemap.Count;
                        }
                    }

                    // Reverse remap: local index → original vertex index
                    var reverseRemap = new int[vertexRemap.Count];
                    foreach (var kv in vertexRemap)
                        reverseRemap[kv.Value] = kv.Key;

                    var newUvMap = new Dictionary<int, Vector2>();
                    float minU = float.MaxValue, minV = float.MaxValue;
                    float maxU = float.MinValue, maxV = float.MinValue;

                    for (int ovi = 0; ovi < outVertCount; ovi++)
                    {
                        int localIdx = (int)xref[ovi];
                        if (localIdx >= 0 && localIdx < reverseRemap.Length)
                        {
                            int origVi = reverseRemap[localIdx];
                            // Normalize to [0,1] from atlas pixel coords
                            float u = outUv[ovi * 2] / atlasW;
                            float v = outUv[ovi * 2 + 1] / atlasH;
                            newUvMap[origVi] = new Vector2(u, v);

                            if (u < minU) minU = u;
                            if (v < minV) minV = v;
                            if (u > maxU) maxU = u;
                            if (v > maxV) maxV = v;
                        }
                    }

                    groupNewUvs[gi] = newUvMap;

                    if (minU <= maxU)
                        result.groupRects[gi] = new Rect(minU, minV, maxU - minU, maxV - minV);
                }

                // Step 3: Build new UV0 per LOD
                int lodCount = allUv0.Length;
                result.newUv0PerLod = new Vector2[lodCount][];

                for (int lod = 0; lod < lodCount; lod++)
                {
                    Vector2[] newUv = new Vector2[allUv0[lod].Length];
                    System.Array.Copy(allUv0[lod], newUv, newUv.Length);

                    if (lod == 0)
                    {
                        // LOD0: apply new UVs directly from xatlas output
                        foreach (var kv in groupNewUvs)
                        {
                            foreach (var vkv in kv.Value)
                                if (vkv.Key < newUv.Length)
                                    newUv[vkv.Key] = vkv.Value;
                        }
                    }
                    else if (corr != null && lod < corr.lodCount && corr.lodToLod0[lod] != null)
                    {
                        // LOD-N: remap via correspondence + transform
                        AssignLodNNewUvs(lod, groups, groupNewUvs, allShells, allTris,
                            allUv0, corr, newUv);
                    }

                    result.newUv0PerLod[lod] = newUv;
                }

                return result;
            }
            finally
            {
                XatlasNative.xatlasDestroy();
            }
        }

        /// <summary>
        /// Assign new UV0 for LOD-N vertices via correspondence and group transforms.
        /// </summary>
        static void AssignLodNNewUvs(int lod, List<ShellGroup> groups,
            Dictionary<int, Dictionary<int, Vector2>> groupNewUvs,
            List<UvShell>[] allShells, int[][] allTris, Vector2[][] allUv0,
            LodShellCorrespondence.LodCorrespondenceSet corr,
            Vector2[] newUv)
        {
            if (allShells[lod] == null) return;

            // Build shell → group mapping for this LOD
            var shellToGroup = new Dictionary<int, int>(); // lodN shellId → group index
            var shellToTransform = new Dictionary<int, ShellUvTransform>();

            for (int gi = 0; gi < groups.Count; gi++)
            {
                foreach (var m in groups[gi].members)
                {
                    if (m.lodLevel == lod)
                    {
                        shellToGroup[m.shellId] = gi;
                        shellToTransform[m.shellId] = m.transform;
                    }
                }
            }

            foreach (var shell in allShells[lod])
            {
                if (!shellToGroup.TryGetValue(shell.shellId, out int gi)) continue;
                if (!groupNewUvs.TryGetValue(gi, out var srcNewUvMap)) continue;

                var group = groups[gi];
                var transform = shellToTransform.ContainsKey(shell.shellId)
                    ? shellToTransform[shell.shellId]
                    : ShellUvTransform.Identity();

                // For LOD-N vertices: find their corresponding LOD0 UV, look up new UV
                // Simplified: use UV0 proximity to map LOD-N verts to source shell verts
                foreach (int vi in shell.vertexIndices)
                {
                    if (vi >= allUv0[lod].Length) continue;
                    Vector2 origUv = allUv0[lod][vi];

                    // Find closest source vertex UV
                    float bestDist = float.MaxValue;
                    Vector2 bestNewUv = origUv;

                    foreach (var kv in srcNewUvMap)
                    {
                        if (kv.Key >= allUv0[0].Length) continue;
                        Vector2 srcOrigUv = allUv0[0][kv.Key];
                        float dist = (srcOrigUv - origUv).sqrMagnitude;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestNewUv = kv.Value;
                        }
                    }

                    if (!transform.IsIdentity)
                        bestNewUv = transform.Apply(bestNewUv);

                    newUv[vi] = bestNewUv;
                }
            }
        }
    }
}
