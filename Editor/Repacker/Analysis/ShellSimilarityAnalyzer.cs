// ShellSimilarityAnalyzer.cs — Similarity matrix builder with weighted composite scoring
// Part of UV0 Atlas Optimizer

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Builds a pairwise similarity matrix between shell thumbnails using
    /// weighted composite of pHash, histogram, and optional SSIM fallback.
    /// </summary>
    public static class ShellSimilarityAnalyzer
    {
        /// <summary>
        /// Compute weighted composite similarity between two shell thumbnails.
        /// Uses histogram pre-filter → pHash per channel → optional SSIM fallback.
        /// </summary>
        public static float ComputeCompositeScore(ShellThumbnail a, ShellThumbnail b,
            Uv0Optimizer.SimilarityWeights w)
        {
            // Quick reject: histogram pre-filter on albedo
            float histSim = PerceptualHasher.HistogramSimilarity(
                PerceptualHasher.ComputeHistogram(a.albedo),
                PerceptualHasher.ComputeHistogram(b.albedo));
            if (histSim < 0.3f)
                return histSim * 0.3f; // Clearly different — don't waste time on pHash

            // Per-channel pHash similarity
            float simAlbedo = PerceptualHasher.PhashSimilarity(a.albedo, b.albedo, a.width, a.height);
            float simNormal = PerceptualHasher.PhashSimilarity(a.normal, b.normal, a.width, a.height);
            float simGloss  = PerceptualHasher.PhashSimilarity(a.gloss,  b.gloss,  a.width, a.height);
            float simAo     = PerceptualHasher.PhashSimilarity(a.ao,     b.ao,     a.width, a.height);

            // Weighted composite
            float totalWeight = w.albedo + w.normal + w.gloss + w.ao;
            if (totalWeight < 0.001f) totalWeight = 1f;

            float score = (simAlbedo * w.albedo + simNormal * w.normal +
                           simGloss * w.gloss + simAo * w.ao) / totalWeight;

            // SSIM fallback for borderline cases (within ±0.1 of typical threshold)
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
                        continue;
                    }

                    float score = ComputeCompositeScore(thumbs[i], thumbs[j], w);
                    matrix[i, j] = score;
                    matrix[j, i] = score;
                }
            }

            return matrix;
        }

        /// <summary>
        /// Check if two shells pass the size ratio gate.
        /// Returns false if the ratio of their areas exceeds maxRatio.
        /// </summary>
        public static bool PassesSizeRatioGate(float areaA, float areaB, float maxRatio)
        {
            if (areaA <= 0f || areaB <= 0f) return false;
            float ratio = areaA > areaB ? areaA / areaB : areaB / areaA;
            return ratio <= maxRatio;
        }
    }
}
