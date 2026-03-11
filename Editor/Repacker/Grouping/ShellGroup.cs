// ShellGroup.cs — Data structures for shell grouping
// Part of UV0 Atlas Optimizer

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Reference to a shell in a specific LOD level.
    /// </summary>
    public struct LodShellRef
    {
        /// <summary>LOD level index (0=LOD0, 1=LOD1, etc.).</summary>
        public int lodLevel;
        /// <summary>Shell ID within that LOD level.</summary>
        public int shellId;
        /// <summary>UV transform to align this member with the group source shell.</summary>
        public ShellUvTransform transform;
    }

    /// <summary>
    /// A group of visually identical UV0 shells that will share the same atlas space.
    /// The sourceShellId is the "representative" shell; all members overlay onto it.
    /// </summary>
    public class ShellGroup
    {
        /// <summary>LOD0 shell ID of the representative (source) shell.</summary>
        public int sourceShellId;

        /// <summary>
        /// All shells that belong to this group (across all LOD levels).
        /// Includes the source shell itself as the first entry.
        /// </summary>
        public List<LodShellRef> members = new List<LodShellRef>();

        /// <summary>True if this group is effectively monotone (solid color).</summary>
        public bool isMonotone;

        /// <summary>Average color if monotone — used for 1px bake.</summary>
        public Color monotoneColor;

        /// <summary>
        /// Estimated UV occupancy saving from overlapping members.
        /// Range [0..1]: 0 = no saving (unique shell), ~0.875 = 8 shells overlapped.
        /// </summary>
        public float occupancySaving;

        /// <summary>Number of LOD0 shells merged into this group.</summary>
        public int Lod0MemberCount
        {
            get
            {
                int count = 0;
                foreach (var m in members)
                    if (m.lodLevel == 0) count++;
                return count;
            }
        }
    }
}
