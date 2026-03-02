// UvShellExtractor.cs — Shell extraction + per-face material ID for overlap separation
// Place in Assets/Editor/

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public class UvShell
    {
        public int shellId;
        public List<int> faceIndices = new List<int>();
        public HashSet<int> vertexIndices = new HashSet<int>();
        public Vector2 boundsMin;
        public Vector2 boundsMax;
        public float bboxArea;
    }

    public static class UvShellExtractor
    {
        /// <summary>
        /// Extract UV shells via Union-Find on faces by shared vertex index.
        /// In Unity, UV seams = duplicated vertices, so connected components
        /// by shared vertex index = UV shells.
        /// </summary>
        public static List<UvShell> Extract(Vector2[] uvs, int[] triangles)
        {
            int faceCount = triangles.Length / 3;
            int[] parent = new int[faceCount];
            int[] rank   = new int[faceCount];
            for (int i = 0; i < faceCount; i++) parent[i] = i;

            // vertex → faces
            var vertToFaces = new Dictionary<int, List<int>>();
            for (int f = 0; f < faceCount; f++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int v = triangles[f * 3 + j];
                    if (!vertToFaces.TryGetValue(v, out var list))
                    {
                        list = new List<int>();
                        vertToFaces[v] = list;
                    }
                    list.Add(f);
                }
            }

            // Union faces sharing a vertex
            foreach (var kv in vertToFaces)
            {
                var list = kv.Value;
                for (int i = 1; i < list.Count; i++)
                    Union(parent, rank, list[0], list[i]);
            }

            // Group faces by root
            var groups = new Dictionary<int, List<int>>();
            for (int f = 0; f < faceCount; f++)
            {
                int root = Find(parent, f);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }
                list.Add(f);
            }

            // Build shells
            var shells = new List<UvShell>();
            int id = 0;
            foreach (var kv in groups)
            {
                var shell = new UvShell { shellId = id++ };
                shell.faceIndices = kv.Value;
                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 mx = new Vector2(float.MinValue, float.MinValue);
                foreach (int f in kv.Value)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int v = triangles[f * 3 + j];
                        shell.vertexIndices.Add(v);
                        mn = Vector2.Min(mn, uvs[v]);
                        mx = Vector2.Max(mx, uvs[v]);
                    }
                }
                shell.boundsMin = mn;
                shell.boundsMax = mx;
                shell.bboxArea = Mathf.Max(0f, (mx.x - mn.x) * (mx.y - mn.y));
                shells.Add(shell);
            }

            return shells;
        }

        /// <summary>
        /// Build per-face shell ID array (uint[faceCount]).
        /// Each shell gets a unique ID → pass as faceMaterialData to xatlas.
        /// xatlas never merges faces from different material IDs into one chart.
        /// This is the clean way to separate overlapping shells — no UV modification.
        /// </summary>
        public static uint[] BuildPerFaceShellIds(Vector2[] uvs, int[] triangles,
            out List<UvShell> outShells, out List<List<int>> outOverlapGroups)
        {
            outShells = Extract(uvs, triangles);

            // Split shells with internal UV folds (mixed winding)
            int foldSplits;
            outShells = SplitFoldedShells(outShells, uvs, triangles, out foldSplits);
            if (foldSplits > 0)
                Debug.Log($"[UvShellExtractor] Fold split: {foldSplits} sub-shell(s) extracted");

            int faceCount = triangles.Length / 3;
            uint[] ids = new uint[faceCount];

            foreach (var shell in outShells)
                foreach (int f in shell.faceIndices)
                    ids[f] = (uint)shell.shellId;

            outOverlapGroups = FindOverlapGroups(outShells);
            return ids;
        }

        /// <summary>
        /// Detect overlap groups by bounding box intersection.
        /// Used for diagnostics/reporting — the actual separation is done by faceMaterialId.
        /// </summary>
        public static List<List<int>> FindOverlapGroups(List<UvShell> shells, float threshold = 0.25f)
        {
            int n = shells.Count;
            int[] parent = new int[n];
            int[] rank   = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (BboxOverlapRatio(shells[i], shells[j]) > threshold)
                        Union(parent, rank, i, j);

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }
                list.Add(i);
            }

            var result = new List<List<int>>();
            foreach (var kv in groups)
                if (kv.Value.Count > 1)
                    result.Add(kv.Value);
            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  SplitFoldedShells — detect UV folds within a shell and
        //  split minority-winding triangles into separate sub-shells.
        //
        //  A "fold" occurs when part of a connected UV shell is flipped
        //  over (negative signed area while majority is positive, or
        //  vice versa). This creates self-overlapping UV regions that
        //  xatlas cannot repack correctly as a single chart.
        //
        //  Algorithm:
        //  1. For each shell, compute signed area per triangle.
        //  2. Determine majority winding by absolute area sum.
        //  3. If any triangles have opposite winding → they are "folded".
        //  4. Group folded triangles by UV-edge adjacency (Union-Find)
        //     to form connected sub-shells.
        //  5. Each connected group becomes a new UvShell.
        //
        //  After splitting, NormalizeShellWinding in XatlasRepack will
        //  flip each sub-shell independently, resolving the fold.
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Split shells that contain folded (mixed-winding) triangles.
        /// Returns a new list with folded parts extracted into separate shells.
        /// Original shell IDs may change — always use the returned list.
        /// </summary>
        public static List<UvShell> SplitFoldedShells(
            List<UvShell> shells, Vector2[] uvs, int[] triangles,
            out int totalSplits, float degenEps = 1e-10f)
        {
            totalSplits = 0;
            var result = new List<UvShell>();
            int nextId = 0;

            foreach (var shell in shells)
            {
                // ── 1. Signed area per triangle ──
                var faces = shell.faceIndices;
                float posArea = 0f, negArea = 0f;
                var faceAreas = new float[faces.Count];

                for (int i = 0; i < faces.Count; i++)
                {
                    int f = faces[i];
                    int i0 = triangles[f * 3], i1 = triangles[f * 3 + 1], i2 = triangles[f * 3 + 2];
                    if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length)
                    {
                        faceAreas[i] = 0f;
                        continue;
                    }
                    float sa = (uvs[i1].x - uvs[i0].x) * (uvs[i2].y - uvs[i0].y)
                             - (uvs[i2].x - uvs[i0].x) * (uvs[i1].y - uvs[i0].y);
                    faceAreas[i] = sa;
                    if (sa > 0f) posArea += sa;
                    else negArea += -sa;
                }

                // ── 2. Majority winding ──
                bool majorityPositive = posArea >= negArea;
                float minorityArea = majorityPositive ? negArea : posArea;

                // No fold if minority is negligible (all degenerate)
                if (minorityArea < degenEps)
                {
                    shell.shellId = nextId++;
                    result.Add(shell);
                    continue;
                }

                // ── 3. Separate majority / minority faces ──
                var majorFaces = new List<int>();
                var minorFaces = new List<int>();

                for (int i = 0; i < faces.Count; i++)
                {
                    float sa = faceAreas[i];
                    bool isDegenerate = Mathf.Abs(sa) < degenEps;

                    if (isDegenerate)
                    {
                        // Degenerate triangles stay with majority
                        majorFaces.Add(faces[i]);
                    }
                    else if ((sa > 0f) == majorityPositive)
                    {
                        majorFaces.Add(faces[i]);
                    }
                    else
                    {
                        minorFaces.Add(faces[i]);
                    }
                }

                if (minorFaces.Count == 0)
                {
                    // False alarm — all minority was degenerate
                    shell.shellId = nextId++;
                    result.Add(shell);
                    continue;
                }

                // ── 4. Group minority faces by UV-edge adjacency ──
                // Build edge → face map for minority faces only
                var minorSet = new HashSet<int>(minorFaces);
                var edgeToFace = new Dictionary<long, List<int>>();

                foreach (int f in minorFaces)
                {
                    int v0 = triangles[f * 3], v1 = triangles[f * 3 + 1], v2 = triangles[f * 3 + 2];
                    AddEdge(edgeToFace, v0, v1, f);
                    AddEdge(edgeToFace, v1, v2, f);
                    AddEdge(edgeToFace, v2, v0, f);
                }

                // Union-Find on minority faces sharing an edge
                int mCount = minorFaces.Count;
                var mParent = new int[mCount];
                var mRank = new int[mCount];
                for (int i = 0; i < mCount; i++) mParent[i] = i;

                // Map faceIndex → local index for Union-Find
                var faceToLocal = new Dictionary<int, int>();
                for (int i = 0; i < mCount; i++)
                    faceToLocal[minorFaces[i]] = i;

                foreach (var kv in edgeToFace)
                {
                    var fList = kv.Value;
                    for (int i = 1; i < fList.Count; i++)
                    {
                        if (faceToLocal.TryGetValue(fList[0], out int la) &&
                            faceToLocal.TryGetValue(fList[i], out int lb))
                        {
                            Union(mParent, mRank, la, lb);
                        }
                    }
                }

                // Group minority faces by connected component
                var minorGroups = new Dictionary<int, List<int>>();
                for (int i = 0; i < mCount; i++)
                {
                    int root = Find(mParent, i);
                    if (!minorGroups.TryGetValue(root, out var list))
                    {
                        list = new List<int>();
                        minorGroups[root] = list;
                    }
                    list.Add(minorFaces[i]);
                }

                // ── 5. Build shells: majority + each minority group ──
                // Majority shell (keeps original shell concept)
                var majorShell = BuildShellFromFaces(majorFaces, uvs, triangles, nextId++);
                result.Add(majorShell);

                // Each connected minority group → separate shell
                foreach (var kv in minorGroups)
                {
                    var subShell = BuildShellFromFaces(kv.Value, uvs, triangles, nextId++);
                    result.Add(subShell);
                    totalSplits++;
                }

                Debug.Log($"[UvShellExtractor] Shell fold split: " +
                    $"{faces.Count} tris → majority {majorFaces.Count} + " +
                    $"{minorGroups.Count} minority group(s) ({minorFaces.Count} tris)");
            }

            return result;
        }

        static UvShell BuildShellFromFaces(List<int> faceIndices, Vector2[] uvs, int[] triangles, int id)
        {
            var shell = new UvShell { shellId = id, faceIndices = faceIndices };
            Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 mx = new Vector2(float.MinValue, float.MinValue);
            foreach (int f in faceIndices)
            {
                for (int j = 0; j < 3; j++)
                {
                    int v = triangles[f * 3 + j];
                    shell.vertexIndices.Add(v);
                    if (v < uvs.Length)
                    {
                        mn = Vector2.Min(mn, uvs[v]);
                        mx = Vector2.Max(mx, uvs[v]);
                    }
                }
            }
            shell.boundsMin = mn;
            shell.boundsMax = mx;
            shell.bboxArea = Mathf.Max(0f, (mx.x - mn.x) * (mx.y - mn.y));
            return shell;
        }

        static void AddEdge(Dictionary<long, List<int>> map, int v0, int v1, int face)
        {
            long key = v0 < v1 ? ((long)v0 << 32) | (uint)v1 : ((long)v1 << 32) | (uint)v0;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                map[key] = list;
            }
            list.Add(face);
        }

        // ── Internals ──

        static float BboxOverlapRatio(UvShell a, UvShell b)
        {
            float oMinX = Mathf.Max(a.boundsMin.x, b.boundsMin.x);
            float oMinY = Mathf.Max(a.boundsMin.y, b.boundsMin.y);
            float oMaxX = Mathf.Min(a.boundsMax.x, b.boundsMax.x);
            float oMaxY = Mathf.Min(a.boundsMax.y, b.boundsMax.y);
            if (oMaxX <= oMinX || oMaxY <= oMinY) return 0f;
            float overlapArea = (oMaxX - oMinX) * (oMaxY - oMinY);
            float smaller = Mathf.Min(a.bboxArea, b.bboxArea);
            return smaller > 0f ? overlapArea / smaller : 0f;
        }

        static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }

        static void Union(int[] p, int[] r, int a, int b)
        {
            a = Find(p, a); b = Find(p, b);
            if (a == b) return;
            if (r[a] < r[b]) { int t = a; a = b; b = t; }
            p[b] = a;
            if (r[a] == r[b]) r[a]++;
        }
    }
}
