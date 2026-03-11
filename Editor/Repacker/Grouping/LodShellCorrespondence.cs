// LodShellCorrespondence.cs — BVH projection to map LOD-N shells to LOD0 shells
// Part of UV0 Atlas Optimizer

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Maps each shell in LOD-N to the corresponding shell in LOD0 by projecting
    /// the LOD-N shell centroid onto LOD0 surface via BVH, then finding which
    /// LOD0 shell owns the hit triangle.
    /// </summary>
    public static class LodShellCorrespondence
    {
        /// <summary>
        /// Correspondence set: for each LOD level, maps lodN_shellId → lod0_shellId.
        /// Index 0 is LOD0→LOD0 (identity). Index 1 is LOD1→LOD0, etc.
        /// </summary>
        public class LodCorrespondenceSet
        {
            /// <summary>
            /// Per LOD level: lodN_shellId → lod0_shellId mapping.
            /// </summary>
            public Dictionary<int, int>[] lodToLod0;

            /// <summary>Number of LOD levels (including LOD0).</summary>
            public int lodCount;
        }

        /// <summary>
        /// Build correspondence for a single LOD level against LOD0.
        /// </summary>
        /// <param name="lodNVerts">Vertices of LOD-N mesh.</param>
        /// <param name="lodNTris">Triangle indices of LOD-N mesh.</param>
        /// <param name="lodNUv0">UV0 of LOD-N mesh.</param>
        /// <param name="lodNShells">Extracted shells of LOD-N.</param>
        /// <param name="lod0Shells">Extracted shells of LOD0.</param>
        /// <param name="lod0Tris">Triangle indices of LOD0 mesh.</param>
        /// <param name="bvh">BVH built from LOD0 mesh vertices/triangles.</param>
        /// <param name="lod0FaceToShell">Mapping: LOD0 face index → shell index.</param>
        public static Dictionary<int, int> BuildCorrespondence(
            Vector3[] lodNVerts, int[] lodNTris, Vector2[] lodNUv0,
            List<UvShell> lodNShells, List<UvShell> lod0Shells,
            int[] lod0Tris, TriangleBvh bvh, int[] lod0FaceToShell)
        {
            var map = new Dictionary<int, int>();

            foreach (var shell in lodNShells)
            {
                // Compute 3D centroid of this LOD-N shell
                Vector3 centroid = MeshUvUtils.GetShell3DCentroid(lodNVerts, shell.vertexIndices);

                // Project onto LOD0 surface via BVH
                var hit = bvh.FindNearest(centroid);

                if (hit.triangleIndex >= 0 && hit.triangleIndex < lod0FaceToShell.Length)
                {
                    int lod0ShellId = lod0FaceToShell[hit.triangleIndex];
                    map[shell.shellId] = lod0ShellId;
                }
                else
                {
                    // Fallback: UV0 bbox overlap
                    int bestShell = FindByUvBboxOverlap(shell, lod0Shells);
                    if (bestShell >= 0)
                    {
                        map[shell.shellId] = bestShell;
                        UvtLog.Warning($"[LodCorrespondence] Shell {shell.shellId}: BVH miss, fallback to UV bbox → LOD0 shell {bestShell}");
                    }
                    else
                    {
                        UvtLog.Warning($"[LodCorrespondence] Shell {shell.shellId}: no correspondence found");
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// Build correspondence for all LOD levels.
        /// </summary>
        /// <param name="allVerts">Per-LOD vertex arrays.</param>
        /// <param name="allTris">Per-LOD triangle index arrays.</param>
        /// <param name="allUv0">Per-LOD UV0 arrays.</param>
        /// <param name="allShells">Per-LOD shell lists.</param>
        public static LodCorrespondenceSet Build(
            Vector3[][] allVerts, int[][] allTris, Vector2[][] allUv0,
            List<UvShell>[] allShells)
        {
            int lodCount = allVerts.Length;
            var result = new LodCorrespondenceSet
            {
                lodCount = lodCount,
                lodToLod0 = new Dictionary<int, int>[lodCount]
            };

            // LOD0 → LOD0 is identity
            result.lodToLod0[0] = new Dictionary<int, int>();
            if (allShells[0] != null)
            {
                foreach (var s in allShells[0])
                    result.lodToLod0[0][s.shellId] = s.shellId;
            }

            if (lodCount <= 1 || allShells[0] == null) return result;

            // Build BVH from LOD0
            var bvh = new TriangleBvh(allVerts[0], allTris[0]);

            // Build LOD0 face-to-shell lookup
            int lod0FaceCount = allTris[0].Length / 3;
            int[] lod0FaceToShell = new int[lod0FaceCount];
            for (int i = 0; i < lod0FaceToShell.Length; i++)
                lod0FaceToShell[i] = -1;
            foreach (var shell in allShells[0])
                foreach (int fi in shell.faceIndices)
                    if (fi < lod0FaceToShell.Length)
                        lod0FaceToShell[fi] = shell.shellId;

            // Build correspondence for LOD1, LOD2, etc.
            for (int lod = 1; lod < lodCount; lod++)
            {
                if (allShells[lod] == null || allVerts[lod] == null)
                {
                    result.lodToLod0[lod] = new Dictionary<int, int>();
                    UvtLog.Verbose($"[LodCorrespondence] LOD{lod}: skipped (no data)");
                    continue;
                }

                result.lodToLod0[lod] = BuildCorrespondence(
                    allVerts[lod], allTris[lod], allUv0[lod],
                    allShells[lod], allShells[0], allTris[0],
                    bvh, lod0FaceToShell);

                UvtLog.Info($"[LodCorrespondence] LOD{lod}: {allShells[lod].Count} shells → {result.lodToLod0[lod].Count} mapped to LOD0");
            }

            return result;
        }

        /// <summary>
        /// Fallback: find LOD0 shell with the most UV bbox overlap with given shell.
        /// </summary>
        static int FindByUvBboxOverlap(UvShell lodNShell, List<UvShell> lod0Shells)
        {
            float bestOverlap = 0f;
            int bestId = -1;

            Rect nRect = new Rect(lodNShell.boundsMin, lodNShell.boundsMax - lodNShell.boundsMin);

            foreach (var s0 in lod0Shells)
            {
                Rect r0 = new Rect(s0.boundsMin, s0.boundsMax - s0.boundsMin);

                // Compute intersection area
                float xMin = Mathf.Max(nRect.xMin, r0.xMin);
                float xMax = Mathf.Min(nRect.xMax, r0.xMax);
                float yMin = Mathf.Max(nRect.yMin, r0.yMin);
                float yMax = Mathf.Min(nRect.yMax, r0.yMax);

                if (xMin < xMax && yMin < yMax)
                {
                    float overlap = (xMax - xMin) * (yMax - yMin);
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        bestId = s0.shellId;
                    }
                }
            }

            return bestId;
        }
    }
}
