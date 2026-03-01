// GroupedShellTransfer.cs — UV2 transfer via triangle surface projection
// For each target vertex:
// 1. Find nearest source TRIANGLE (not vertex) filtered by face normal
// 2. Compute barycentric coordinates on that triangle
// 3. Interpolate UV2 from the 3 triangle vertices
// Triangle is atomic: all 3 vertices in same UV2 shell → no seam ambiguity.
// Normal filter separates front/back of thin walls.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class GroupedShellTransfer
    {
        // ─── Shell info for cross-LOD analysis (kept for UI stats) ───
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
        //  Step 1: Analyze source mesh — extract UV0 shells (for UI)
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
                    shellUv2 = uv2sList.ToArray()
                };
            }

            Debug.Log($"[GroupedTransfer] Source '{sourceMesh.name}': {infos.Length} shells");
            return infos;
        }

        // ═══════════════════════════════════════════════════════════
        //  Step 2: Transfer UV2 via triangle surface projection
        //  For each target vertex:
        //  1. Find nearest source TRIANGLE by point-to-triangle distance
        //     filtered by face normal (dot > threshold)
        //  2. Compute barycentric coordinates on that triangle
        //  3. Interpolate UV2 from the 3 triangle vertices
        //
        //  Triangle is atomic — all 3 vertices belong to the same
        //  UV2 shell. No seam ambiguity possible.
        //  Normal filter separates front/back of thin walls.
        // ═══════════════════════════════════════════════════════════

        const float NORMAL_DOT_THRESHOLD = 0.3f;

        public static TransferResult Transfer(Mesh targetMesh, Mesh sourceMesh)
        {
            var result = new TransferResult();

            // Source data
            var srcVerts = sourceMesh.vertices;
            var srcNormals = sourceMesh.normals;
            var srcTris = sourceMesh.triangles;
            var srcUv2List = new List<Vector2>();
            sourceMesh.GetUVs(2, srcUv2List);
            var srcUv2 = srcUv2List.ToArray();
            bool srcHasNormals = srcNormals != null && srcNormals.Length == srcVerts.Length;

            if (srcUv2.Length == 0)
            {
                Debug.LogError("[GroupedTransfer] Source mesh has no UV2");
                return result;
            }

            // Target data
            var tVerts = targetMesh.vertices;
            var tNormals = targetMesh.normals;
            int vertCount = targetMesh.vertexCount;
            bool tHasNormals = tNormals != null && tNormals.Length == vertCount;

            result.uv2 = new Vector2[vertCount];
            result.verticesTotal = vertCount;

            // Pre-compute source triangle data
            int triCount = srcTris.Length / 3;
            var triA = new Vector3[triCount];
            var triB = new Vector3[triCount];
            var triC = new Vector3[triCount];
            var triFaceNormal = new Vector3[triCount];
            var triUv2A = new Vector2[triCount];
            var triUv2B = new Vector2[triCount];
            var triUv2C = new Vector2[triCount];

            for (int t = 0; t < triCount; t++)
            {
                int i0 = srcTris[t * 3], i1 = srcTris[t * 3 + 1], i2 = srcTris[t * 3 + 2];
                triA[t] = srcVerts[i0];
                triB[t] = srcVerts[i1];
                triC[t] = srcVerts[i2];
                triUv2A[t] = srcUv2[i0];
                triUv2B[t] = srcUv2[i1];
                triUv2C[t] = srcUv2[i2];

                // Face normal from cross product
                Vector3 edge1 = triB[t] - triA[t];
                Vector3 edge2 = triC[t] - triA[t];
                triFaceNormal[t] = Vector3.Cross(edge1, edge2).normalized;

                // Fallback: use averaged vertex normals if cross product is degenerate
                if (triFaceNormal[t].sqrMagnitude < 0.5f && srcHasNormals)
                {
                    triFaceNormal[t] = ((srcNormals[i0] + srcNormals[i1] + srcNormals[i2]) / 3f).normalized;
                }
            }

            // For each target vertex: find nearest source triangle, interpolate UV2
            int normalFallback = 0;

            for (int vi = 0; vi < vertCount; vi++)
            {
                Vector3 tPos = tVerts[vi];
                Vector3 tN = tHasNormals ? tNormals[vi] : Vector3.up;

                float bestDistSq = float.MaxValue;
                int bestTri = -1;
                float bestU = 0, bestV = 0, bestW = 0;

                // Pass 1: find nearest triangle among normal-compatible
                for (int t = 0; t < triCount; t++)
                {
                    float dot = Vector3.Dot(tN, triFaceNormal[t]);
                    if (dot < NORMAL_DOT_THRESHOLD) continue;

                    float distSq = PointToTriangleDistSq(
                        tPos, triA[t], triB[t], triC[t],
                        out float u, out float v, out float w);

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestTri = t;
                        bestU = u; bestV = v; bestW = w;
                    }
                }

                // Fallback: no normal-compatible triangle found
                if (bestTri < 0)
                {
                    normalFallback++;
                    for (int t = 0; t < triCount; t++)
                    {
                        float distSq = PointToTriangleDistSq(
                            tPos, triA[t], triB[t], triC[t],
                            out float u, out float v, out float w);

                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestTri = t;
                            bestU = u; bestV = v; bestW = w;
                        }
                    }
                }

                // Interpolate UV2 using barycentric coordinates
                if (bestTri >= 0)
                {
                    result.uv2[vi] = triUv2A[bestTri] * bestU
                                   + triUv2B[bestTri] * bestV
                                   + triUv2C[bestTri] * bestW;
                    result.verticesTransferred++;
                }
            }

            result.shellsMatched = 0; // triangle-based, no shell tracking needed

            Debug.Log($"[GroupedTransfer] '{targetMesh.name}': " +
                      $"triangle projection, " +
                      $"{result.verticesTransferred}/{result.verticesTotal} verts" +
                      (normalFallback > 0 ? $", {normalFallback} normal-fallback" : ""));

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
        //  Point-to-triangle distance with barycentric coordinates
        //  Returns squared distance; outputs clamped barycentric (u,v,w)
        //  where point ≈ a*u + b*v + c*w
        // ═══════════════════════════════════════════════════════════

        static float PointToTriangleDistSq(
            Vector3 p, Vector3 a, Vector3 b, Vector3 c,
            out float u, out float v, out float w)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = p - a;

            float d00 = Vector3.Dot(ab, ab);
            float d01 = Vector3.Dot(ab, ac);
            float d11 = Vector3.Dot(ac, ac);
            float d20 = Vector3.Dot(ap, ab);
            float d21 = Vector3.Dot(ap, ac);

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

            // Inside triangle — project directly
            if (baryU >= 0f && baryV >= 0f && baryW >= 0f)
            {
                u = baryU; v = baryV; w = baryW;
                Vector3 proj = a * u + b * v + c * w;
                return (p - proj).sqrMagnitude;
            }

            // Outside triangle — clamp to nearest edge/vertex
            // Test all 3 edges, pick closest point
            float bestDist = float.MaxValue;
            u = 1; v = 0; w = 0;

            // Edge AB (w=0): point = a*(1-t) + b*t
            {
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(d00, 1e-12f));
                Vector3 cp = a + ab * t;
                float d = (p - cp).sqrMagnitude;
                if (d < bestDist) { bestDist = d; u = 1f - t; v = t; w = 0f; }
            }
            // Edge AC (v=0): point = a*(1-t) + c*t
            {
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ac) / Mathf.Max(d11, 1e-12f));
                Vector3 cp = a + ac * t;
                float d = (p - cp).sqrMagnitude;
                if (d < bestDist) { bestDist = d; u = 1f - t; v = 0f; w = t; }
            }
            // Edge BC (u=0): point = b*(1-t) + c*t
            {
                Vector3 bc = c - b;
                float bcLen = Vector3.Dot(bc, bc);
                float t = Mathf.Clamp01(Vector3.Dot(p - b, bc) / Mathf.Max(bcLen, 1e-12f));
                Vector3 cp = b + bc * t;
                float d = (p - cp).sqrMagnitude;
                if (d < bestDist) { bestDist = d; u = 0f; v = 1f - t; w = t; }
            }

            return bestDist;
        }

        // ═══════════════════════════════════════════════════════════
        //  Signed area of shell in UV space (used by AnalyzeSource)
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
                             "Use Transfer(targetMesh, sourceMesh) for triangle projection.");
            // Fallback: cannot do triangle projection without source mesh
            return new TransferResult
            {
                uv2 = new Vector2[targetMesh.vertexCount],
                verticesTotal = targetMesh.vertexCount
            };
        }
    }
}
