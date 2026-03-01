// GroupedShellTransfer.cs — UV2 transfer via shell assignment + UV0-space projection
// Two-phase approach:
// Phase 1: Shell assignment — for each target vertex, determine source shell
//          via 3D nearest vertex with normal filter (separates thin wall sides)
// Phase 2: UV0 transfer — within assigned shell, find nearest source triangle
//          in UV0 SPACE (not 3D), compute UV0 barycentric, interpolate UV2.
//          This is equivalent to 3ds Max "UV Weld Selected" approach.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class GroupedShellTransfer
    {
        // ─── Shell info for cross-LOD analysis ───
        public class SourceShellInfo
        {
            public int shellId;
            public Vector2 uv0BoundsMin, uv0BoundsMax;
            public Vector2 uv0Centroid;
            public Vector3 worldCentroid;
            public float signedAreaUv0;
            public int vertexCount;

            public int[] vertexIndices;
            public Vector3[] worldPositions;
            public Vector3[] normals;
            public Vector2[] shellUv0;
            public Vector2[] shellUv2;

            // Triangle data for UV0-space projection
            public List<int> faceIndices;
        }

        // ─── Result of transfer for one target mesh ───
        public class TransferResult
        {
            public Vector2[] uv2;
            public int shellsMatched;
            public int shellsUnmatched;
            public int shellsMirrored;
            public int verticesTransferred;
            public int verticesTotal;
        }


        // ═══════════════════════════════════════════════════════════
        //  Step 1: Analyze source mesh — extract UV0 shells
        // ═══════════════════════════════════════════════════════════

        public static SourceShellInfo[] AnalyzeSource(Mesh sourceMesh)
        {
            var uv0List = new List<Vector2>();
            var uv2List = new List<Vector2>();
            sourceMesh.GetUVs(0, uv0List);
            sourceMesh.GetUVs(2, uv2List);

            if (uv0List.Count == 0 || uv2List.Count == 0)
            {
                Debug.LogError("[GroupedTransfer] Source mesh missing UV0 or UV2");
                return null;
            }

            var uv0 = uv0List.ToArray();
            var uv2 = uv2List.ToArray();
            var tris = sourceMesh.triangles;
            var verts = sourceMesh.vertices;
            var norms = sourceMesh.normals;
            bool hasNormals = norms != null && norms.Length == verts.Length;

            var shells = UvShellExtractor.Extract(uv0, tris);
            var infos = new SourceShellInfo[shells.Count];

            for (int si = 0; si < shells.Count; si++)
            {
                var shell = shells[si];
                var idxList = new List<int>();
                var posList = new List<Vector3>();
                var nrmList = new List<Vector3>();
                var uv0sList = new List<Vector2>();
                var uv2sList = new List<Vector2>();
                Vector2 uv0Sum = Vector2.zero;
                Vector3 worldSum = Vector3.zero;
                int n = 0;

                foreach (int vi in shell.vertexIndices)
                {
                    if (vi >= uv0.Length || vi >= uv2.Length || vi >= verts.Length) continue;
                    idxList.Add(vi);
                    posList.Add(verts[vi]);
                    nrmList.Add(hasNormals ? norms[vi] : Vector3.up);
                    uv0sList.Add(uv0[vi]);
                    uv2sList.Add(uv2[vi]);
                    uv0Sum += uv0[vi];
                    worldSum += verts[vi];
                    n++;
                }

                float signedArea = ComputeSignedArea(tris, uv0, shell.faceIndices);

                infos[si] = new SourceShellInfo
                {
                    shellId = shell.shellId,
                    uv0BoundsMin = shell.boundsMin,
                    uv0BoundsMax = shell.boundsMax,
                    uv0Centroid = n > 0 ? uv0Sum / n : Vector2.zero,
                    worldCentroid = n > 0 ? worldSum / n : Vector3.zero,
                    signedAreaUv0 = signedArea,
                    vertexCount = n,
                    vertexIndices = idxList.ToArray(),
                    worldPositions = posList.ToArray(),
                    normals = nrmList.ToArray(),
                    shellUv0 = uv0sList.ToArray(),
                    shellUv2 = uv2sList.ToArray(),
                    faceIndices = shell.faceIndices
                };
            }

            Debug.Log($"[GroupedTransfer] Source '{sourceMesh.name}': {infos.Length} shells");
            return infos;
        }

        // ═══════════════════════════════════════════════════════════
        //  Step 2: Two-phase transfer
        //  Phase 1: Shell assignment via 3D nearest + normal filter
        //  Phase 2: UV0-space triangle projection within assigned shell
        //
        //  This matches 3ds Max "UV Weld Selected" logic:
        //  shell identity comes from 3D geometry,
        //  UV2 transfer happens entirely in UV0 space.
        // ═══════════════════════════════════════════════════════════

        const float NORMAL_DOT_THRESHOLD = 0.3f;

        public static TransferResult Transfer(Mesh targetMesh, Mesh sourceMesh)
        {
            var result = new TransferResult();

            // Source data
            var srcVerts = sourceMesh.vertices;
            var srcNormals = sourceMesh.normals;
            var srcTris = sourceMesh.triangles;
            var srcUv0List = new List<Vector2>();
            var srcUv2List = new List<Vector2>();
            sourceMesh.GetUVs(0, srcUv0List);
            sourceMesh.GetUVs(2, srcUv2List);
            var srcUv0 = srcUv0List.ToArray();
            var srcUv2 = srcUv2List.ToArray();
            bool srcHasNormals = srcNormals != null && srcNormals.Length == srcVerts.Length;

            if (srcUv0.Length == 0 || srcUv2.Length == 0)
            {
                Debug.LogError("[GroupedTransfer] Source mesh missing UV0 or UV2");
                return result;
            }

            // Target data
            var tVerts = targetMesh.vertices;
            var tNormals = targetMesh.normals;
            var tUv0List = new List<Vector2>();
            targetMesh.GetUVs(0, tUv0List);
            var tUv0 = tUv0List.ToArray();
            int vertCount = targetMesh.vertexCount;
            bool tHasNormals = tNormals != null && tNormals.Length == vertCount;

            if (tUv0.Length == 0)
            {
                Debug.LogError("[GroupedTransfer] Target mesh missing UV0");
                return result;
            }

            result.uv2 = new Vector2[vertCount];
            result.verticesTotal = vertCount;

            // ── Extract source UV0 shells ──
            var shells = UvShellExtractor.Extract(srcUv0, srcTris);

            // Build vertex→shell mapping for source
            int[] srcVertShell = new int[srcVerts.Length];
            for (int i = 0; i < srcVertShell.Length; i++) srcVertShell[i] = -1;
            for (int si = 0; si < shells.Count; si++)
                foreach (int vi in shells[si].vertexIndices)
                    srcVertShell[vi] = si;

            // ── Build per-shell triangle lists ──
            // Each shell has triangles; we store their UV0 coords and UV2 coords
            int srcTriCount = srcTris.Length / 3;
            int[] triShell = new int[srcTriCount];
            for (int f = 0; f < srcTriCount; f++)
            {
                int i0 = srcTris[f * 3];
                triShell[f] = srcVertShell[i0]; // all 3 verts of a face are in same shell
            }

            // Per-shell: list of face indices
            var shellFaces = new List<int>[shells.Count];
            for (int si = 0; si < shells.Count; si++)
                shellFaces[si] = new List<int>();
            for (int f = 0; f < srcTriCount; f++)
            {
                int si = triShell[f];
                if (si >= 0 && si < shells.Count)
                    shellFaces[si].Add(f);
            }

            // ══════════════════════════════════════════════
            //  PHASE 1: Shell assignment via 3D nearest + normal
            // ══════════════════════════════════════════════
            int[] targetShell = new int[vertCount];
            int shellAssignFail = 0;

            for (int vi = 0; vi < vertCount; vi++)
            {
                Vector3 tPos = tVerts[vi];
                Vector3 tN = tHasNormals ? tNormals[vi] : Vector3.up;

                float bestDistSq = float.MaxValue;
                int bestShell = -1;

                // Find nearest source vertex with normal compatibility
                for (int sv = 0; sv < srcVerts.Length; sv++)
                {
                    if (srcVertShell[sv] < 0) continue;

                    // Normal filter: skip source vertices facing wrong way
                    if (srcHasNormals)
                    {
                        float dot = Vector3.Dot(tN, srcNormals[sv]);
                        if (dot < NORMAL_DOT_THRESHOLD) continue;
                    }

                    float dSq = (tPos - srcVerts[sv]).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestShell = srcVertShell[sv];
                    }
                }

                // Fallback: no normal-compatible source found
                if (bestShell < 0)
                {
                    shellAssignFail++;
                    for (int sv = 0; sv < srcVerts.Length; sv++)
                    {
                        if (srcVertShell[sv] < 0) continue;
                        float dSq = (tPos - srcVerts[sv]).sqrMagnitude;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestShell = srcVertShell[sv];
                        }
                    }
                }

                targetShell[vi] = bestShell;
            }

            // ══════════════════════════════════════════════
            //  PHASE 2: UV0-space triangle projection within shell
            //  For each target vertex:
            //  - take its UV0 coordinate as a 2D point
            //  - within the assigned source shell, find nearest triangle in UV0 space
            //  - compute UV0 barycentric coordinates
            //  - interpolate UV2 using those barycentrics
            // ══════════════════════════════════════════════
            int transferred = 0;
            int shellsUsed = 0;
            var usedShells = new HashSet<int>();

            for (int vi = 0; vi < vertCount; vi++)
            {
                int si = targetShell[vi];
                if (si < 0 || si >= shells.Count) continue;

                Vector2 tUv = tUv0[vi];  // target vertex UV0 coordinate
                var faces = shellFaces[si];

                float bestDistSq = float.MaxValue;
                int bestFace = -1;
                float bestU = 0, bestV = 0, bestW = 0;

                // Find nearest source triangle in UV0 space
                foreach (int f in faces)
                {
                    int i0 = srcTris[f * 3], i1 = srcTris[f * 3 + 1], i2 = srcTris[f * 3 + 2];
                    Vector2 a = srcUv0[i0], b = srcUv0[i1], c = srcUv0[i2];

                    float dSq = PointToTriangle2DDistSq(tUv, a, b, c,
                        out float u, out float v, out float w);

                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestFace = f;
                        bestU = u; bestV = v; bestW = w;
                    }
                }

                // Interpolate UV2 using UV0 barycentrics
                if (bestFace >= 0)
                {
                    int i0 = srcTris[bestFace * 3];
                    int i1 = srcTris[bestFace * 3 + 1];
                    int i2 = srcTris[bestFace * 3 + 2];

                    result.uv2[vi] = srcUv2[i0] * bestU
                                   + srcUv2[i1] * bestV
                                   + srcUv2[i2] * bestW;
                    transferred++;
                    usedShells.Add(si);
                }
            }

            result.verticesTransferred = transferred;
            result.shellsMatched = usedShells.Count;

            Debug.Log($"[GroupedTransfer] '{targetMesh.name}': " +
                      $"shell-assign + UV0-space projection, " +
                      $"{transferred}/{vertCount} verts, " +
                      $"{usedShells.Count} shells used" +
                      (shellAssignFail > 0 ? $", {shellAssignFail} normal-fallback" : ""));

            // UV2 bounds check
            int outOfBounds = 0;
            Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < result.uv2.Length; i++)
            {
                var uv = result.uv2[i];
                if (uv.x < uvMin.x) uvMin.x = uv.x;
                if (uv.y < uvMin.y) uvMin.y = uv.y;
                if (uv.x > uvMax.x) uvMax.x = uv.x;
                if (uv.y > uvMax.y) uvMax.y = uv.y;
                if (uv.x < -0.01f || uv.x > 1.01f || uv.y < -0.01f || uv.y > 1.01f)
                    outOfBounds++;
            }
            if (outOfBounds > 0)
                Debug.LogWarning($"[GroupedTransfer] '{targetMesh.name}': {outOfBounds} verts " +
                    $"outside 0-1! UV2 bounds=[{uvMin.x:F3},{uvMin.y:F3}]-[{uvMax.x:F3},{uvMax.y:F3}]");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  2D Point-to-triangle distance with barycentric coordinates
        //  Works in UV0 space. Returns squared distance.
        //  Outputs clamped barycentric (u,v,w) where point ≈ a*u + b*v + c*w
        // ═══════════════════════════════════════════════════════════

        static float PointToTriangle2DDistSq(
            Vector2 p, Vector2 a, Vector2 b, Vector2 c,
            out float u, out float v, out float w)
        {
            Vector2 ab = b - a;
            Vector2 ac = c - a;
            Vector2 ap = p - a;

            float d00 = Vector2.Dot(ab, ab);
            float d01 = Vector2.Dot(ab, ac);
            float d11 = Vector2.Dot(ac, ac);
            float d20 = Vector2.Dot(ap, ab);
            float d21 = Vector2.Dot(ap, ac);

            float denom = d00 * d11 - d01 * d01;

            // Degenerate triangle
            if (Mathf.Abs(denom) < 1e-12f)
            {
                u = 1f; v = 0f; w = 0f;
                return (p - a).sqrMagnitude;
            }

            float baryV = (d11 * d20 - d01 * d21) / denom;
            float baryW = (d00 * d21 - d01 * d20) / denom;
            float baryU = 1f - baryV - baryW;

            // Inside triangle
            if (baryU >= 0f && baryV >= 0f && baryW >= 0f)
            {
                u = baryU; v = baryV; w = baryW;
                Vector2 proj = a * u + b * v + c * w;
                return (p - proj).sqrMagnitude;
            }

            // Outside triangle — clamp to nearest edge/vertex
            float bestDist = float.MaxValue;
            u = 1; v = 0; w = 0;

            // Edge AB (w=0)
            {
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(d00, 1e-12f));
                Vector2 cp = a + ab * t;
                float d = (p - cp).sqrMagnitude;
                if (d < bestDist) { bestDist = d; u = 1f - t; v = t; w = 0f; }
            }
            // Edge AC (v=0)
            {
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ac) / Mathf.Max(d11, 1e-12f));
                Vector2 cp = a + ac * t;
                float d = (p - cp).sqrMagnitude;
                if (d < bestDist) { bestDist = d; u = 1f - t; v = 0f; w = t; }
            }
            // Edge BC (u=0)
            {
                Vector2 bc = c - b;
                float bcLen = Vector2.Dot(bc, bc);
                float t = Mathf.Clamp01(Vector2.Dot(p - b, bc) / Mathf.Max(bcLen, 1e-12f));
                Vector2 cp = b + bc * t;
                float d = (p - cp).sqrMagnitude;
                if (d < bestDist) { bestDist = d; u = 0f; v = 1f - t; w = t; }
            }

            return bestDist;
        }

        // ═══════════════════════════════════════════════════════════
        //  Signed area of shell in UV space
        // ═══════════════════════════════════════════════════════════

        static float ComputeSignedArea(int[] tris, Vector2[] uvs, List<int> faceIndices)
        {
            double area = 0;
            foreach (int f in faceIndices)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                var a = uvs[i0]; var b = uvs[i1]; var c = uvs[i2];
                area += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return (float)(area * 0.5);
        }

        // ═══════════════════════════════════════════════════════════
        //  Legacy overload — kept for backward compatibility
        // ═══════════════════════════════════════════════════════════

        public static TransferResult Transfer(
            Mesh targetMesh, SourceShellInfo[] sourceInfos)
        {
            Debug.LogWarning("[GroupedTransfer] Legacy vertex-based Transfer called. " +
                             "Use Transfer(targetMesh, sourceMesh) for UV0-space projection.");
            return new TransferResult
            {
                uv2 = new Vector2[targetMesh.vertexCount],
                verticesTotal = targetMesh.vertexCount
            };
        }
    }
}
