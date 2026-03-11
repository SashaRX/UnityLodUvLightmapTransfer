// MeshUvUtils.cs — UV bbox, area, shell transform matrix utilities
// Part of UV0 Atlas Optimizer

using UnityEngine;
using System.Collections.Generic;

namespace LightmapUvTool
{
    public static class MeshUvUtils
    {
        /// <summary>
        /// Get axis-aligned bounding box of a UV shell in UV space.
        /// </summary>
        public static Rect GetShellUvBbox(Vector2[] uvs, List<int> faceIndices, int[] tris)
        {
            float minU = float.MaxValue, minV = float.MaxValue;
            float maxU = float.MinValue, maxV = float.MinValue;

            foreach (int fi in faceIndices)
            {
                for (int j = 0; j < 3; j++)
                {
                    int vi = tris[fi * 3 + j];
                    if (vi >= uvs.Length) continue;
                    Vector2 uv = uvs[vi];
                    if (uv.x < minU) minU = uv.x;
                    if (uv.y < minV) minV = uv.y;
                    if (uv.x > maxU) maxU = uv.x;
                    if (uv.y > maxV) maxV = uv.y;
                }
            }

            if (minU > maxU) return new Rect(0, 0, 0, 0);
            return new Rect(minU, minV, maxU - minU, maxV - minV);
        }

        /// <summary>
        /// Compute signed UV area of a shell (sum of triangle cross products).
        /// </summary>
        public static float GetShellUvArea(Vector2[] uvs, List<int> faceIndices, int[] tris)
        {
            double area = 0;
            foreach (int fi in faceIndices)
            {
                int i0 = tris[fi * 3], i1 = tris[fi * 3 + 1], i2 = tris[fi * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                Vector2 a = uvs[i0], b = uvs[i1], c = uvs[i2];
                area += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return (float)(area * 0.5);
        }

        /// <summary>
        /// Compute absolute UV area of a shell.
        /// </summary>
        public static float GetShellUvAreaAbs(Vector2[] uvs, List<int> faceIndices, int[] tris)
        {
            return Mathf.Abs(GetShellUvArea(uvs, faceIndices, tris));
        }

        /// <summary>
        /// Transform a UV coordinate: rotate around pivot, then optionally flip U/V.
        /// </summary>
        public static Vector2 TransformUv(Vector2 uv, float rotDeg, bool flipU, bool flipV, Vector2 pivot)
        {
            // Center on pivot
            Vector2 p = uv - pivot;

            // Rotate
            if (rotDeg != 0f)
            {
                float rad = rotDeg * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);
                p = new Vector2(p.x * cos - p.y * sin, p.x * sin + p.y * cos);
            }

            // Flip
            if (flipU) p.x = -p.x;
            if (flipV) p.y = -p.y;

            return p + pivot;
        }

        /// <summary>
        /// Compute centroid of a shell in 3D world space.
        /// </summary>
        public static Vector3 GetShell3DCentroid(Vector3[] vertices, HashSet<int> vertexIndices)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (int vi in vertexIndices)
            {
                if (vi < vertices.Length)
                {
                    sum += vertices[vi];
                    count++;
                }
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        /// <summary>
        /// Compute centroid of a shell in UV space.
        /// </summary>
        public static Vector2 GetShellUvCentroid(Vector2[] uvs, HashSet<int> vertexIndices)
        {
            Vector2 sum = Vector2.zero;
            int count = 0;
            foreach (int vi in vertexIndices)
            {
                if (vi < uvs.Length)
                {
                    sum += uvs[vi];
                    count++;
                }
            }
            return count > 0 ? sum / count : Vector2.zero;
        }
    }
}
