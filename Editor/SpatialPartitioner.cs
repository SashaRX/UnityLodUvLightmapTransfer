// SpatialPartitioner.cs — Spatial partition for overlapping UV0 shells
//
// Approach A: Flood-fill mesh adjacency → connected components = partitions.
//             Works when overlapping UV0 faces belong to different connected
//             components (front/back of belt are separate polygon groups).
//
// Approach B: When flood-fill returns one partition for a shell with overlap,
//             force-split by 3D proximity (K-means k=2 on overlapping face
//             centroids, propagate to all faces via adjacency).

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class SpatialPartitioner
    {
        public class ShellPartitionResult
        {
            public int shellId;
            public bool hasOverlap;
            public int partitionCount; // 1 = no split, >1 = partitions available
            public int[] facePartitionId; // [globalFaceIndex] → partitionId (-1 for faces outside this shell)
            public Vector3[] partitionCentroid; // 3D centroid per partition
        }

        /// <summary>
        /// Partition source shells by detecting UV0 overlap and splitting via
        /// flood-fill (Approach A) then forced 3D split (Approach B) if needed.
        /// </summary>
        public static ShellPartitionResult[] PartitionShells(
            List<UvShell> shells,
            Vector2[] uv0, int[] triangles, Vector3[] vertices,
            int gridResolution = 512)
        {
            int totalFaces = triangles.Length / 3;
            var results = new ShellPartitionResult[shells.Count];

            for (int si = 0; si < shells.Count; si++)
            {
                var shell = shells[si];
                var r = new ShellPartitionResult
                {
                    shellId = si,
                    hasOverlap = false,
                    partitionCount = 1,
                    facePartitionId = new int[totalFaces],
                    partitionCentroid = null
                };
                for (int i = 0; i < totalFaces; i++) r.facePartitionId[i] = -1;

                // Mark all faces in this shell as partition 0 initially
                foreach (int f in shell.faceIndices)
                    r.facePartitionId[f] = 0;

                // Step 1: Overlap detection via UV0 grid rasterization
                var overlappingFaces = DetectOverlap(shell, uv0, triangles, gridResolution);
                r.hasOverlap = overlappingFaces.Count > 0;

                if (!r.hasOverlap)
                {
                    // No overlap — single partition, compute centroid
                    r.partitionCentroid = new Vector3[] { ComputePartitionCentroid(shell.faceIndices, triangles, vertices) };
                    results[si] = r;
                    continue;
                }

                UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: overlap detected ({overlappingFaces.Count} faces in overlap zones)");

                // Step 2: Face adjacency graph within shell
                var adjacency = BuildFaceAdjacency(shell.faceIndices, triangles);

                // Step 3: Flood-fill → connected components = partitions (Approach A)
                var components = FloodFillComponents(shell.faceIndices, adjacency);

                if (components.Count > 1)
                {
                    // Approach A succeeded — multiple connected components
                    r.partitionCount = components.Count;
                    r.partitionCentroid = new Vector3[components.Count];
                    for (int ci = 0; ci < components.Count; ci++)
                    {
                        foreach (int f in components[ci])
                            r.facePartitionId[f] = ci;
                        r.partitionCentroid[ci] = ComputePartitionCentroid(components[ci], triangles, vertices);
                    }

                    UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: flood-fill → {components.Count} partitions " +
                        $"({string.Join(" + ", PartitionSizes(components))} faces)");
                    for (int ci = 0; ci < components.Count; ci++)
                        UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: partition {ci} centroid={r.partitionCentroid[ci]:F2}");
                }
                else
                {
                    // Approach A failed — single connected component
                    UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: flood-fill → 1 partition (connected mesh)");

                    // Step 4: Approach B — forced split by 3D proximity
                    var split = ForceSplitByProximity(shell, overlappingFaces, adjacency, triangles, vertices);
                    if (split != null && split.Count == 2)
                    {
                        r.partitionCount = 2;
                        r.partitionCentroid = new Vector3[2];
                        for (int ci = 0; ci < 2; ci++)
                        {
                            foreach (int f in split[ci])
                                r.facePartitionId[f] = ci;
                            r.partitionCentroid[ci] = ComputePartitionCentroid(split[ci], triangles, vertices);
                        }

                        UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: forced 3D split → 2 partitions " +
                            $"({split[0].Count} + {split[1].Count} faces)");
                        for (int ci = 0; ci < 2; ci++)
                            UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: partition {ci} centroid={r.partitionCentroid[ci]:F2}");
                    }
                    else
                    {
                        // Both A and B failed — single partition
                        r.partitionCentroid = new Vector3[] { ComputePartitionCentroid(shell.faceIndices, triangles, vertices) };
                        UvtLog.Verbose($"[SpatialPartitioner] Shell {si}: forced split failed, staying as 1 partition");
                    }
                }

                results[si] = r;
            }

            return results;
        }

        /// <summary>
        /// Find the best-matching source partition for a target shell by 3D centroid distance.
        /// Returns partition index, or -1 if no partitions available.
        /// </summary>
        public static int MatchPartition(ShellPartitionResult srcPartition, Vector3 targetCentroid3D)
        {
            if (srcPartition == null || srcPartition.partitionCount <= 1)
                return -1; // no partition constraint needed

            int bestPart = 0;
            float bestDistSq = float.MaxValue;
            for (int pi = 0; pi < srcPartition.partitionCount; pi++)
            {
                float dSq = (srcPartition.partitionCentroid[pi] - targetCentroid3D).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestPart = pi;
                }
            }
            return bestPart;
        }

        /// <summary>
        /// Collect global face indices belonging to a specific partition.
        /// </summary>
        public static int[] GetPartitionFaces(UvShell shell, ShellPartitionResult partResult, int partitionId)
        {
            var faces = new List<int>();
            foreach (int f in shell.faceIndices)
            {
                if (partResult.facePartitionId[f] == partitionId)
                    faces.Add(f);
            }
            return faces.ToArray();
        }

        // ════════════════════════════════════════════════════════════
        //  Overlap detection: rasterize UV0 triangles onto 2D grid
        // ════════════════════════════════════════════════════════════

        static HashSet<int> DetectOverlap(UvShell shell, Vector2[] uv0, int[] triangles, int gridRes)
        {
            var overlapping = new HashSet<int>();

            // Compute shell UV bounds
            Vector2 bMin = shell.boundsMin;
            Vector2 bMax = shell.boundsMax;
            float rangeX = bMax.x - bMin.x;
            float rangeY = bMax.y - bMin.y;
            if (rangeX < 1e-8f || rangeY < 1e-8f) return overlapping;

            // Scale grid to shell bounds
            float invX = gridRes / rangeX;
            float invY = gridRes / rangeY;

            // Grid cell → list of faces that cover it
            // For memory efficiency, use a dictionary for sparse grid
            var cellFaces = new Dictionary<long, List<int>>();

            foreach (int f in shell.faceIndices)
            {
                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                if (i0 >= uv0.Length || i1 >= uv0.Length || i2 >= uv0.Length) continue;

                Vector2 a = uv0[i0], b = uv0[i1], c = uv0[i2];

                // AABB of triangle in grid coords
                float minGx = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
                float maxGx = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                float minGy = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                float maxGy = Mathf.Max(a.y, Mathf.Max(b.y, c.y));

                int gxMin = Mathf.Clamp((int)((minGx - bMin.x) * invX), 0, gridRes - 1);
                int gxMax = Mathf.Clamp((int)((maxGx - bMin.x) * invX), 0, gridRes - 1);
                int gyMin = Mathf.Clamp((int)((minGy - bMin.y) * invY), 0, gridRes - 1);
                int gyMax = Mathf.Clamp((int)((maxGy - bMin.y) * invY), 0, gridRes - 1);

                for (int gy = gyMin; gy <= gyMax; gy++)
                {
                    for (int gx = gxMin; gx <= gxMax; gx++)
                    {
                        long key = (long)gy * gridRes + gx;
                        if (!cellFaces.TryGetValue(key, out var list))
                        {
                            list = new List<int>(2);
                            cellFaces[key] = list;
                        }
                        list.Add(f);
                    }
                }
            }

            // Cells with >1 face → those faces are overlapping
            foreach (var kv in cellFaces)
            {
                if (kv.Value.Count > 1)
                {
                    foreach (int f in kv.Value)
                        overlapping.Add(f);
                }
            }

            return overlapping;
        }

        // ════════════════════════════════════════════════════════════
        //  Face adjacency graph: two faces adjacent if they share an edge
        // ════════════════════════════════════════════════════════════

        static Dictionary<int, List<int>> BuildFaceAdjacency(List<int> faceIndices, int[] triangles)
        {
            var adjacency = new Dictionary<int, List<int>>();
            var edgeToFace = new Dictionary<long, int>();

            foreach (int f in faceIndices)
            {
                adjacency[f] = new List<int>();

                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                // 3 edges per face
                TryAddEdge(edgeToFace, adjacency, i0, i1, f);
                TryAddEdge(edgeToFace, adjacency, i1, i2, f);
                TryAddEdge(edgeToFace, adjacency, i2, i0, f);
            }

            return adjacency;
        }

        static void TryAddEdge(Dictionary<long, int> edgeToFace, Dictionary<int, List<int>> adjacency,
            int v0, int v1, int face)
        {
            // Canonical edge key: smaller vertex first
            long key = v0 < v1 ? ((long)v0 << 32) | (uint)v1 : ((long)v1 << 32) | (uint)v0;

            if (edgeToFace.TryGetValue(key, out int otherFace))
            {
                // Edge shared → faces are adjacent
                if (!adjacency[face].Contains(otherFace))
                    adjacency[face].Add(otherFace);
                if (adjacency.ContainsKey(otherFace) && !adjacency[otherFace].Contains(face))
                    adjacency[otherFace].Add(face);
            }
            else
            {
                edgeToFace[key] = face;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Flood-fill → connected components (Approach A)
        // ════════════════════════════════════════════════════════════

        static List<List<int>> FloodFillComponents(List<int> faceIndices, Dictionary<int, List<int>> adjacency)
        {
            var visited = new HashSet<int>();
            var components = new List<List<int>>();

            foreach (int startFace in faceIndices)
            {
                if (visited.Contains(startFace)) continue;

                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(startFace);
                visited.Add(startFace);

                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    component.Add(f);

                    if (adjacency.TryGetValue(f, out var neighbors))
                    {
                        foreach (int n in neighbors)
                        {
                            if (!visited.Contains(n))
                            {
                                visited.Add(n);
                                queue.Enqueue(n);
                            }
                        }
                    }
                }

                components.Add(component);
            }

            return components;
        }

        // ════════════════════════════════════════════════════════════
        //  Forced split by 3D proximity (Approach B)
        //  K-means (k=2) on overlapping face 3D centroids, then
        //  propagate to non-overlapping faces via adjacency.
        // ════════════════════════════════════════════════════════════

        static List<List<int>> ForceSplitByProximity(
            UvShell shell, HashSet<int> overlappingFaces,
            Dictionary<int, List<int>> adjacency,
            int[] triangles, Vector3[] vertices)
        {
            if (overlappingFaces.Count < 2) return null;

            // Compute 3D centroid for each overlapping face
            var faceCentroids = new Dictionary<int, Vector3>();
            foreach (int f in overlappingFaces)
            {
                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) continue;
                faceCentroids[f] = (vertices[i0] + vertices[i1] + vertices[i2]) / 3f;
            }

            if (faceCentroids.Count < 2) return null;

            // K-means k=2 on 3D centroids of overlapping faces
            // Initialize: pick two most distant points
            Vector3 c0 = Vector3.zero, c1 = Vector3.zero;
            float maxDist = -1f;
            var faceList = new List<int>(faceCentroids.Keys);

            // Start with first face centroid
            c0 = faceCentroids[faceList[0]];

            // Find farthest from c0
            foreach (int f in faceList)
            {
                float d = (faceCentroids[f] - c0).sqrMagnitude;
                if (d > maxDist) { maxDist = d; c1 = faceCentroids[f]; }
            }

            if (maxDist < 1e-10f) return null; // all faces at same 3D position

            // Run K-means iterations
            var assignment = new Dictionary<int, int>(); // face → cluster (0 or 1)
            for (int iter = 0; iter < 20; iter++)
            {
                // Assign each overlapping face to nearest centroid
                bool changed = false;
                foreach (int f in faceList)
                {
                    float d0 = (faceCentroids[f] - c0).sqrMagnitude;
                    float d1 = (faceCentroids[f] - c1).sqrMagnitude;
                    int newCluster = d0 <= d1 ? 0 : 1;
                    if (!assignment.TryGetValue(f, out int old) || old != newCluster)
                    {
                        assignment[f] = newCluster;
                        changed = true;
                    }
                }

                if (!changed && iter > 0) break;

                // Recompute centroids
                Vector3 sum0 = Vector3.zero, sum1 = Vector3.zero;
                int n0 = 0, n1 = 0;
                foreach (int f in faceList)
                {
                    if (assignment[f] == 0) { sum0 += faceCentroids[f]; n0++; }
                    else { sum1 += faceCentroids[f]; n1++; }
                }

                if (n0 > 0) c0 = sum0 / n0;
                if (n1 > 0) c1 = sum1 / n1;
            }

            // Check we have faces in both clusters
            int count0 = 0, count1 = 0;
            foreach (var kv in assignment)
            {
                if (kv.Value == 0) count0++;
                else count1++;
            }
            if (count0 == 0 || count1 == 0) return null; // degenerate split

            // Propagate cluster assignment to non-overlapping faces via adjacency BFS
            // Each non-overlapping face gets the cluster of its nearest overlapping neighbor
            var faceCluster = new Dictionary<int, int>(assignment);
            var propagateQueue = new Queue<int>();

            // Seed: overlapping faces already assigned
            foreach (var kv in assignment)
                propagateQueue.Enqueue(kv.Key);

            while (propagateQueue.Count > 0)
            {
                int f = propagateQueue.Dequeue();
                int cluster = faceCluster[f];

                if (adjacency.TryGetValue(f, out var neighbors))
                {
                    foreach (int n in neighbors)
                    {
                        if (!faceCluster.ContainsKey(n))
                        {
                            faceCluster[n] = cluster;
                            propagateQueue.Enqueue(n);
                        }
                    }
                }
            }

            // Build partition lists
            var part0 = new List<int>();
            var part1 = new List<int>();
            foreach (int f in shell.faceIndices)
            {
                if (faceCluster.TryGetValue(f, out int cl))
                {
                    if (cl == 0) part0.Add(f);
                    else part1.Add(f);
                }
                else
                {
                    // Face not reached by BFS — assign to nearest cluster by 3D centroid
                    int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                    if (i0 < vertices.Length && i1 < vertices.Length && i2 < vertices.Length)
                    {
                        Vector3 fc = (vertices[i0] + vertices[i1] + vertices[i2]) / 3f;
                        if ((fc - c0).sqrMagnitude <= (fc - c1).sqrMagnitude)
                            part0.Add(f);
                        else
                            part1.Add(f);
                    }
                    else
                    {
                        part0.Add(f);
                    }
                }
            }

            if (part0.Count == 0 || part1.Count == 0) return null;

            return new List<List<int>> { part0, part1 };
        }

        // ════════════════════════════════════════════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════

        static Vector3 ComputePartitionCentroid(List<int> faceIndices, int[] triangles, Vector3[] vertices)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (int f in faceIndices)
            {
                int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                if (i0 < vertices.Length && i1 < vertices.Length && i2 < vertices.Length)
                {
                    sum += (vertices[i0] + vertices[i1] + vertices[i2]) / 3f;
                    count++;
                }
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        static string PartitionSizes(List<List<int>> components)
        {
            var parts = new string[components.Count];
            for (int i = 0; i < components.Count; i++)
                parts[i] = components[i].Count.ToString();
            return string.Join(" + ", parts);
        }
    }
}
