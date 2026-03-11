// ShellUvTransform.cs — UV space rotation/flip transform for shell overlapping
// Part of UV0 Atlas Optimizer

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Describes a UV-space transform: rotation + flip around a pivot point.
    /// Used to align a shell member with its group source for overlapping.
    /// </summary>
    public struct ShellUvTransform
    {
        /// <summary>Rotation in degrees (0, 90, 180, 270).</summary>
        public float rotationDeg;
        /// <summary>Flip U axis after rotation.</summary>
        public bool flipU;
        /// <summary>Flip V axis after rotation.</summary>
        public bool flipV;
        /// <summary>Pivot point in UV space (typically shell center).</summary>
        public Vector2 pivotUv;

        /// <summary>Identity transform (no rotation, no flip).</summary>
        public static ShellUvTransform Identity()
        {
            return new ShellUvTransform
            {
                rotationDeg = 0f,
                flipU = false,
                flipV = false,
                pivotUv = new Vector2(0.5f, 0.5f)
            };
        }

        /// <summary>
        /// Apply this transform to a UV coordinate.
        /// </summary>
        public Vector2 Apply(Vector2 uv)
        {
            return MeshUvUtils.TransformUv(uv, rotationDeg, flipU, flipV, pivotUv);
        }

        /// <summary>
        /// Check if this is an identity transform (no-op).
        /// </summary>
        public bool IsIdentity => rotationDeg == 0f && !flipU && !flipV;

        public override string ToString()
        {
            return $"rot={rotationDeg}° flipU={flipU} flipV={flipV} pivot={pivotUv}";
        }
    }
}
