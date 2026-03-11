// ShellSimilarityAnalyzer.cs — Similarity matrix builder (albedo-only for now)
// Part of UV0 Atlas Optimizer

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Builds a pairwise similarity matrix between shell thumbnails.
    /// Currently uses albedo channel only: histogram pre-filter → pHash → SSIM fallback.
    /// Monotone shells require close color match to merge.
    /// </summary>
    public static class ShellSimilarityAnalyzer
    {
        /// <summary>
        /// Max Euclidean color distance (in RGB [0,1]) for two monotone shells to match.
        /// ~0.05 allows slight shade variation; ~0.15 allows noticeable differences.
        /// </summary>
        const float MonotoneColorDistanceThreshold = 0.06f;

        /// <summary>
        /// Compute similarity between two shell thumbnails (albedo-only).
        /// </summary>
        public static float ComputeCompositeScore(ShellThumbnail a, ShellThumbnail b,
            Uv0Optimizer.SimilarityWeights w)
        {
            // Monotone pair: use direct color distance instead of pHash
            // (pHash is meaningless on flat-color images — all produce similar hashes)
            if (a.isMonotone && b.isMonotone)
            {
                float colorDist = ColorDistance(a.monotoneColor, b.monotoneColor);
                // Convert distance to similarity: 0 distance → 1.0, threshold → 0.0
                float sim = 1f - Mathf.Clamp01(colorDist / MonotoneColorDistanceThreshold);
                return sim;
            }

            // One monotone, one not — can't match
            if (a.isMonotone != b.isMonotone)
                return 0f;

            // Both non-monotone: histogram pre-filter → pHash → SSIM
            float histSim = PerceptualHasher.HistogramSimilarity(
                PerceptualHasher.ComputeHistogram(a.albedo),
                PerceptualHasher.ComputeHistogram(b.albedo));
            if (histSim < 0.3f)
                return histSim * 0.3f;

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
        /// </summary>
        public static float[,] BuildSimilarityMatrix(ShellThumbnail[] thumbs,
            Uv0Optimizer.SimilarityWeights w, float[] shellAreas = null, float maxSizeRatio = 3f)
        {
            int n = thumbs.Length;
            float[,] matrix = new float[n, n];

            int pairsComputed = 0;
            int pairsSkippedSize = 0;
            int pairsSkippedMonoMix = 0;
            int pairsMonoColor = 0;

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

                    if (thumbs[i].isMonotone && thumbs[j].isMonotone)
                        pairsMonoColor++;
                    else if (thumbs[i].isMonotone != thumbs[j].isMonotone)
                        pairsSkippedMonoMix++;
                }
            }

            UvtLog.Info($"[Similarity] {n} shells → {pairsComputed} pairs compared, " +
                $"{pairsSkippedSize} skipped (size), {pairsMonoColor} mono↔mono, {pairsSkippedMonoMix} mono↔pattern (=0)");

            // Log top matches
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (matrix[i, j] >= 0.5f)
                    {
                        string tag = (thumbs[i].isMonotone && thumbs[j].isMonotone) ? " MONO" : "";
                        UvtLog.Verbose($"[Similarity] shell {i} ↔ shell {j}: score={matrix[i, j]:F3}{tag}");
                    }
                }
            }

            return matrix;
        }

        /// <summary>
        /// Euclidean RGB distance between two colors (range 0..~1.73).
        /// </summary>
        static float ColorDistance(Color a, Color b)
        {
            float dr = a.r - b.r;
            float dg = a.g - b.g;
            float db = a.b - b.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
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
