// NormalAwareTransformer.cs — 8-variant rotation/flip search minimizing normal MSE
// Part of UV0 Atlas Optimizer
//
// Critical: when rotating/flipping normal maps, the tangent-space XY must be
// transformed accordingly:
//   rot 90°:  N.xy = (-N.y, N.x)
//   rot 180°: N.xy = (-N.x, -N.y)
//   rot 270°: N.xy = (N.y, -N.x)
//   flipU:    N.x = -N.x
//   flipV:    N.y = -N.y

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Finds the best UV transform (from 8 variants: 4 rotations × 2 flip states)
    /// that minimizes the normal map MSE between a source and target shell thumbnail.
    /// </summary>
    public static class NormalAwareTransformer
    {
        /// <summary>
        /// All 8 candidate transforms: {0°,90°,180°,270°} × {flipU: false, true}.
        /// flipV is redundant given these combinations (flipU+rot180 = flipV).
        /// </summary>
        static readonly (float rot, bool flipU)[] Candidates = new[]
        {
            (0f,   false),
            (90f,  false),
            (180f, false),
            (270f, false),
            (0f,   true),
            (90f,  true),
            (180f, true),
            (270f, true),
        };

        /// <summary>
        /// Find the best transform that aligns target shell normals to source shell normals.
        /// Returns the ShellUvTransform with minimum normal MSE.
        /// </summary>
        public static ShellUvTransform FindBestTransform(ShellThumbnail source, ShellThumbnail target)
        {
            float bestMse = float.MaxValue;
            int bestIdx = 0;

            for (int i = 0; i < Candidates.Length; i++)
            {
                var (rot, flip) = Candidates[i];
                float mse = ComputeTransformedNormalMSE(source.normal, target.normal,
                    source.width, source.height, rot, flip);

                if (mse < bestMse)
                {
                    bestMse = mse;
                    bestIdx = i;
                }
            }

            var best = Candidates[bestIdx];
            Vector2 pivot = new Vector2(
                (source.uvBbox.xMin + source.uvBbox.xMax) * 0.5f,
                (source.uvBbox.yMin + source.uvBbox.yMax) * 0.5f);

            return new ShellUvTransform
            {
                rotationDeg = best.rot,
                flipU = best.flipU,
                flipV = false,
                pivotUv = pivot
            };
        }

        /// <summary>
        /// Compute MSE between source normals and rotated/flipped target normals.
        /// The target pixels are spatially transformed AND the normal vectors themselves
        /// are rotated in tangent space.
        /// </summary>
        static float ComputeTransformedNormalMSE(Color[] sourceNormals, Color[] targetNormals,
            int w, int h, float rotDeg, bool flipU)
        {
            if (sourceNormals.Length != targetNormals.Length) return float.MaxValue;

            double mse = 0;
            int count = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Transform pixel coordinates: where does target[tx,ty] map to?
                    int tx, ty;
                    TransformPixelCoord(x, y, w, h, rotDeg, flipU, out tx, out ty);

                    if (tx < 0 || tx >= w || ty < 0 || ty >= h) continue;

                    Color srcN = sourceNormals[y * w + x];
                    Color tgtN = targetNormals[ty * w + tx];

                    // Transform the normal vector in tangent space
                    Vector3 tn = NormalFromColor(tgtN);
                    tn = TransformNormalVector(tn, rotDeg, flipU);
                    Vector3 sn = NormalFromColor(srcN);

                    float dx = sn.x - tn.x;
                    float dy = sn.y - tn.y;
                    float dz = sn.z - tn.z;
                    mse += dx * dx + dy * dy + dz * dz;
                    count++;
                }
            }

            return count > 0 ? (float)(mse / count) : float.MaxValue;
        }

        /// <summary>
        /// Transform a pixel coordinate by rotation + flip.
        /// </summary>
        static void TransformPixelCoord(int x, int y, int w, int h,
            float rotDeg, bool flipU, out int tx, out int ty)
        {
            // Apply flip first
            int fx = flipU ? (w - 1 - x) : x;
            int fy = y;

            // Apply rotation (clockwise in image space)
            int rotSteps = Mathf.RoundToInt(rotDeg / 90f) & 3;
            switch (rotSteps)
            {
                case 0: tx = fx; ty = fy; break;
                case 1: tx = h - 1 - fy; ty = fx; break;      // 90° CW
                case 2: tx = w - 1 - fx; ty = h - 1 - fy; break; // 180°
                case 3: tx = fy; ty = w - 1 - fx; break;       // 270° CW
                default: tx = fx; ty = fy; break;
            }
        }

        /// <summary>
        /// Transform a tangent-space normal vector according to UV rotation/flip.
        /// </summary>
        public static Vector3 TransformNormalVector(Vector3 n, float rotDeg, bool flipU)
        {
            // Rotation in tangent space
            int rotSteps = Mathf.RoundToInt(rotDeg / 90f) & 3;
            float nx = n.x, ny = n.y;
            switch (rotSteps)
            {
                case 1: // 90°
                    n.x = -ny;
                    n.y = nx;
                    break;
                case 2: // 180°
                    n.x = -nx;
                    n.y = -ny;
                    break;
                case 3: // 270°
                    n.x = ny;
                    n.y = -nx;
                    break;
            }

            // Flip
            if (flipU) n.x = -n.x;

            return n;
        }

        /// <summary>
        /// Transform an array of normal pixels by the given ShellUvTransform.
        /// </summary>
        public static void TransformNormalPixels(Color[] normals, int w, int h,
            ShellUvTransform t, out Color[] result)
        {
            result = new Color[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int tx, ty;
                    TransformPixelCoord(x, y, w, h, t.rotationDeg, t.flipU, out tx, out ty);

                    if (tx >= 0 && tx < w && ty >= 0 && ty < h)
                    {
                        Color c = normals[ty * w + tx];
                        Vector3 n = NormalFromColor(c);
                        n = TransformNormalVector(n, t.rotationDeg, t.flipU);
                        if (t.flipV) n.y = -n.y;
                        result[y * w + x] = NormalToColor(n);
                    }
                }
            }
        }

        /// <summary>
        /// Compute MSE between two normal pixel arrays.
        /// </summary>
        public static float NormalMSE(Color[] a, Color[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return float.MaxValue;

            double mse = 0;
            for (int i = 0; i < a.Length; i++)
            {
                Vector3 na = NormalFromColor(a[i]);
                Vector3 nb = NormalFromColor(b[i]);
                float dx = na.x - nb.x;
                float dy = na.y - nb.y;
                float dz = na.z - nb.z;
                mse += dx * dx + dy * dy + dz * dz;
            }
            return (float)(mse / a.Length);
        }

        // Decode normal from color: [0,1] → [-1,1]
        static Vector3 NormalFromColor(Color c)
        {
            return new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
        }

        // Encode normal to color: [-1,1] → [0,1]
        static Color NormalToColor(Vector3 n)
        {
            return new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
        }
    }
}
