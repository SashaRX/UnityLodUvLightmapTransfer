// ShellGroupBuilder.cs — Union-Find clustering of similar shells into groups
// Part of UV0 Atlas Optimizer

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Builds shell groups using Union-Find (DSU) with path compression.
    /// LOD correspondence forces LOD-N shells into the same group as their LOD0 source.
    /// </summary>
    public static class ShellGroupBuilder
    {
        /// <summary>
        /// Build shell groups from the similarity matrix and LOD correspondence.
        /// </summary>
        /// <param name="simMatrix">NxN similarity matrix for LOD0 shells.</param>
        /// <param name="lod0Shells">LOD0 shell list.</param>
        /// <param name="allShells">Per-LOD shell lists.</param>
        /// <param name="corr">LOD shell correspondence set.</param>
        /// <param name="threshold">Similarity threshold for grouping (0..1).</param>
        /// <param name="thumbnails">Shell thumbnails (for monotone detection).</param>
        /// <param name="shellAreas">LOD0 shell areas (for occupancy estimation).</param>
        public static List<ShellGroup> Build(
            float[,] simMatrix,
            List<UvShell> lod0Shells,
            List<UvShell>[] allShells,
            LodShellCorrespondence.LodCorrespondenceSet corr,
            float threshold,
            ShellThumbnail[] thumbnails,
            float[] shellAreas)
        {
            int n = lod0Shells.Count;

            // Union-Find on LOD0 shells
            int[] parent = new int[n];
            int[] rank = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            // Merge similar LOD0 shells
            int mergeCount = 0;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (simMatrix[i, j] >= threshold)
                    {
                        UvtLog.Verbose($"[GroupBuilder] Merge shell {i} ↔ {j} (score={simMatrix[i, j]:F3} >= {threshold:F3})");
                        Union(parent, rank, i, j);
                        mergeCount++;
                    }
                }
            }

            UvtLog.Info($"[GroupBuilder] {n} LOD0 shells, {mergeCount} merges at threshold {threshold:F3}");

            // Build groups from Union-Find roots
            var rootToGroup = new Dictionary<int, ShellGroup>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                if (!rootToGroup.TryGetValue(root, out var group))
                {
                    group = new ShellGroup { sourceShellId = root };
                    rootToGroup[root] = group;
                }

                // Add LOD0 member
                group.members.Add(new LodShellRef
                {
                    lodLevel = 0,
                    shellId = i,
                    transform = ShellUvTransform.Identity()
                });
            }

            // Add LOD-N members via correspondence
            if (corr != null)
            {
                for (int lod = 1; lod < corr.lodCount; lod++)
                {
                    if (corr.lodToLod0[lod] == null || allShells == null || lod >= allShells.Length || allShells[lod] == null)
                        continue;

                    foreach (var shell in allShells[lod])
                    {
                        if (corr.lodToLod0[lod].TryGetValue(shell.shellId, out int lod0ShellId))
                        {
                            if (lod0ShellId >= 0 && lod0ShellId < n)
                            {
                                int root = Find(parent, lod0ShellId);
                                if (rootToGroup.TryGetValue(root, out var group))
                                {
                                    group.members.Add(new LodShellRef
                                    {
                                        lodLevel = lod,
                                        shellId = shell.shellId,
                                        transform = ShellUvTransform.Identity()
                                    });
                                }
                            }
                        }
                        else
                        {
                            // No correspondence — shell gets its own group
                            // (will be handled as unique in packing)
                            UvtLog.Warning($"[ShellGroupBuilder] LOD{lod} shell {shell.shellId} has no LOD0 correspondence");
                        }
                    }
                }
            }

            // Set monotone flag and occupancy saving
            var groups = new List<ShellGroup>(rootToGroup.Values);
            foreach (var group in groups)
            {
                // Check monotone from thumbnail
                if (thumbnails != null && group.sourceShellId < thumbnails.Length)
                {
                    var thumb = thumbnails[group.sourceShellId];
                    if (thumb != null && thumb.isMonotone)
                    {
                        group.isMonotone = true;
                        group.monotoneColor = thumb.monotoneColor;
                    }
                }

                // Estimate occupancy saving
                int lod0Count = group.Lod0MemberCount;
                if (lod0Count > 1)
                    group.occupancySaving = 1f - 1f / lod0Count;
            }

            return groups;
        }

        /// <summary>
        /// Estimate total occupancy saving across all groups.
        /// </summary>
        public static float EstimateOccupancySaving(List<ShellGroup> groups, float[] shellAreas)
        {
            if (shellAreas == null || groups == null) return 0f;

            float totalOrigArea = 0f;
            float totalNewArea = 0f;

            foreach (var group in groups)
            {
                float groupArea = 0f;
                float maxMemberArea = 0f;

                foreach (var m in group.members)
                {
                    if (m.lodLevel == 0 && m.shellId < shellAreas.Length)
                    {
                        float area = shellAreas[m.shellId];
                        groupArea += area;
                        if (area > maxMemberArea) maxMemberArea = area;
                    }
                }

                totalOrigArea += groupArea;
                totalNewArea += maxMemberArea; // All members overlap onto the largest
            }

            return totalOrigArea > 0f ? 1f - totalNewArea / totalOrigArea : 0f;
        }

        // ── Union-Find with path compression and union by rank ──

        static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path halving
                x = parent[x];
            }
            return x;
        }

        static void Union(int[] parent, int[] rank, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (rank[ra] < rank[rb]) { int t = ra; ra = rb; rb = t; }
            parent[rb] = ra;
            if (rank[ra] == rank[rb]) rank[ra]++;
        }
    }
}
