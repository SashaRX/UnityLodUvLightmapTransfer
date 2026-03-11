// ShellSimilarityAnalyzer.cs — Similarity matrix builder (albedo-only for now)
// Part of UV0 Atlas Optimizer

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Builds a pairwise similarity matrix between shell thumbnails.
    /// Currently uses albedo channel only: histogram pre-filter → pHash → SSIM fallback.
    /// </summary>
    public static class ShellSimilarityAnalyzer
    {
        /// <summary>
        /// Compute similarity between two shell thumbnails (albedo-only).
        /// </summary>
        public static float ComputeCompositeScore(ShellThumbnail a, ShellThumbnail b,
            Uv0Optimizer.SimilarityWeights w)
        {
            // Histogram pre-filter on albedo
            float histSim = PerceptualHasher.HistogramSimilarity(
                PerceptualHasher.ComputeHistogram(a.albedo),
                PerceptualHasher.ComputeHistogram(b.albedo));
            if (histSim < 0.3f)
                return histSim * 0.3f;

            // pHash on albedo only
            float score = PerceptualHasher.PhashSimilarity(a.albedo, b.albedo, a.width, a.height);

            // SSIM fallback for borderline cases
            if (score > 0.65f && score < 0.95f)
            {
                float ssim = PerceptualHasher.ComputeSsim(a.albedo, b.albedo);
                score = score * 0.7f + ssim * 0.3f;
            }

            return score;
        }

        /// <summary>
        /// Build NxN similarity matrix for all shell thumbnails.
        /// Matrix is symmetric; diagonal is 1.0.
        /// </summary>
        public static float[,] BuildSimilarityMatrix(ShellThumbnail[] thumbs,
            Uv0Optimizer.SimilarityWeights w, float[] shellAreas = null, float maxSizeRatio = 3f)
        {
            int n = thumbs.Length;
            float[,] matrix = new float[n, n];

            int pairsComputed = 0;
            int pairsSkippedSize = 0;

            for (int i = 0; i < n; i++)
            {
                matrix[i, i] = 1f;
                for (int j = i + 1; j < n; j++)
                {
                    // Size ratio gate
                    if (shellAreas != null && !PassesSizeRatioGate(shellAreas[i], shellAreas[j], maxSizeRatio))
                    {
                        matrix[i, j] = 0f;
                        matrix[j, i] = 0f;
                        pairsSkippedSize++;
                        continue;
                    }

                    float score = ComputeCompositeScore(thumbs[i], thumbs[j], w);
                    matrix[i, j] = score;
                    matrix[j, i] = score;
                    pairsComputed++;
                }
            }

            UvtLog.Info($"[Similarity] {n} shells → {pairsComputed} pairs compared, {pairsSkippedSize} skipped (size ratio)");

            // Log top matches
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (matrix[i, j] >= 0.5f)
                        UvtLog.Verbose($"[Similarity] shell {i} ↔ shell {j}: score={matrix[i, j]:F3}");
                }
            }

            return matrix;
        }

        /// <summary>
        /// Check if two shells pass the size ratio gate.
        /// </summary>
        public static bool PassesSizeRatioGate(float areaA, float areaB, float maxRatio)
        {
            if (areaA <= 0f || areaB <= 0f) return false;
            float ratio = areaA > areaB ? areaA / areaB : areaB / areaA;
            return ratio <= maxRatio;
        }
    }
}
