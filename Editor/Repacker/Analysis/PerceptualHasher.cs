// PerceptualHasher.cs — Inline DCT-based pHash, SSIM, histogram, variance check
// Part of UV0 Atlas Optimizer — no external dependencies

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Perceptual hashing and similarity metrics for shell thumbnails.
    /// All algorithms implemented inline using standard math — no NuGet dependencies.
    /// </summary>
    public static class PerceptualHasher
    {
        // ════════════════════════════════════════════════════════════
        //  pHash — DCT-based perceptual hash (64-bit)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute a 64-bit perceptual hash from pixel data.
        /// Pipeline: grayscale → resize to 32×32 → DCT → top-left 8×8 → median threshold → 64 bits.
        /// </summary>
        public static ulong ComputePhash(Color[] pixels, int w = 32, int h = 32)
        {
            // Convert to grayscale float array
            float[] gray = new float[w * h];
            for (int i = 0; i < pixels.Length && i < gray.Length; i++)
            {
                Color c = pixels[i];
                gray[i] = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            }

            // Separable 2D DCT: apply 1D DCT on rows, then on columns
            float[] dctRows = new float[w * h];
            float[] dct2d   = new float[w * h];

            // DCT on rows
            for (int y = 0; y < h; y++)
                Dct1D(gray, y * w, w, dctRows, y * w);

            // DCT on columns (transpose, DCT, transpose back)
            float[] colIn  = new float[h];
            float[] colOut = new float[h];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                    colIn[y] = dctRows[y * w + x];
                Dct1D(colIn, 0, h, colOut, 0);
                for (int y = 0; y < h; y++)
                    dct2d[y * w + x] = colOut[y];
            }

            // Extract top-left 8×8 (excluding DC at [0,0])
            const int hashSize = 8;
            float[] lowFreq = new float[hashSize * hashSize];
            for (int y = 0; y < hashSize; y++)
                for (int x = 0; x < hashSize; x++)
                    lowFreq[y * hashSize + x] = dct2d[y * w + x];

            // Compute median of the 64 values (excluding DC)
            float[] sorted = new float[hashSize * hashSize - 1];
            System.Array.Copy(lowFreq, 1, sorted, 0, sorted.Length);
            System.Array.Sort(sorted);
            float median = sorted[sorted.Length / 2];

            // Generate hash: 1 if above median, 0 if below
            ulong hash = 0;
            for (int i = 1; i < hashSize * hashSize; i++)
            {
                if (lowFreq[i] > median)
                    hash |= 1UL << (i - 1);
            }

            return hash;
        }

        /// <summary>
        /// Compute Hamming distance between two pHash values, normalized to [0..1].
        /// 0 = identical, 1 = completely different.
        /// </summary>
        public static float HammingDistance(ulong a, ulong b)
        {
            ulong xor = a ^ b;
            int bits = 0;
            while (xor != 0)
            {
                bits += (int)(xor & 1);
                xor >>= 1;
            }
            return bits / 63f; // 63 bits used (excluding DC)
        }

        /// <summary>
        /// Compute pHash similarity: 1.0 = identical, 0.0 = completely different.
        /// </summary>
        public static float PhashSimilarity(Color[] a, Color[] b, int w = 32, int h = 32)
        {
            ulong hashA = ComputePhash(a, w, h);
            ulong hashB = ComputePhash(b, w, h);
            return 1f - HammingDistance(hashA, hashB);
        }

        // ════════════════════════════════════════════════════════════
        //  SSIM — Structural Similarity Index (simplified)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute SSIM between two pixel arrays. Returns value in [0..1].
        /// Simplified: computes on grayscale, no window — global statistics.
        /// </summary>
        public static float ComputeSsim(Color[] a, Color[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return 0f;

            int n = a.Length;
            double muA = 0, muB = 0;
            for (int i = 0; i < n; i++)
            {
                muA += Luminance(a[i]);
                muB += Luminance(b[i]);
            }
            muA /= n;
            muB /= n;

            double sigmaA2 = 0, sigmaB2 = 0, sigmaAB = 0;
            for (int i = 0; i < n; i++)
            {
                double la = Luminance(a[i]) - muA;
                double lb = Luminance(b[i]) - muB;
                sigmaA2 += la * la;
                sigmaB2 += lb * lb;
                sigmaAB += la * lb;
            }
            sigmaA2 /= n;
            sigmaB2 /= n;
            sigmaAB /= n;

            const double c1 = 0.0001; // (k1*L)^2, L=1, k1=0.01
            const double c2 = 0.0009; // (k2*L)^2, L=1, k2=0.03

            double num   = (2 * muA * muB + c1) * (2 * sigmaAB + c2);
            double denom = (muA * muA + muB * muB + c1) * (sigmaA2 + sigmaB2 + c2);

            return (float)(num / denom);
        }

        // ════════════════════════════════════════════════════════════
        //  Color Histogram — pre-filter
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute a color histogram with specified number of bins per channel.
        /// Returns a normalized histogram array of size bins*3 (R,G,B).
        /// </summary>
        public static float[] ComputeHistogram(Color[] pixels, int bins = 64)
        {
            float[] hist = new float[bins * 3];
            float scale = bins - 0.001f;

            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                int rBin = Mathf.Clamp(Mathf.FloorToInt(c.r * scale), 0, bins - 1);
                int gBin = Mathf.Clamp(Mathf.FloorToInt(c.g * scale), 0, bins - 1);
                int bBin = Mathf.Clamp(Mathf.FloorToInt(c.b * scale), 0, bins - 1);
                hist[rBin]++;
                hist[bins + gBin]++;
                hist[bins * 2 + bBin]++;
            }

            // Normalize
            float invN = 1f / Mathf.Max(pixels.Length, 1);
            for (int i = 0; i < hist.Length; i++)
                hist[i] *= invN;

            return hist;
        }

        /// <summary>
        /// Compute histogram intersection similarity [0..1]. 1 = identical distribution.
        /// </summary>
        public static float HistogramSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return 0f;

            float intersection = 0f;
            for (int i = 0; i < a.Length; i++)
                intersection += Mathf.Min(a[i], b[i]);

            // Normalize by 3 channels (each channel sums to 1)
            return intersection / 3f;
        }

        // ════════════════════════════════════════════════════════════
        //  Monotone / Variance Check
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if pixel array is effectively monotone (very low variance).
        /// </summary>
        public static bool IsMonotone(Color[] pixels, float varianceThreshold = 0.008f)
        {
            if (pixels.Length == 0) return true;

            // Compute mean
            float sumR = 0, sumG = 0, sumB = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                sumR += pixels[i].r;
                sumG += pixels[i].g;
                sumB += pixels[i].b;
            }
            float inv = 1f / pixels.Length;
            float meanR = sumR * inv, meanG = sumG * inv, meanB = sumB * inv;

            // Compute variance
            float varR = 0, varG = 0, varB = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                float dr = pixels[i].r - meanR;
                float dg = pixels[i].g - meanG;
                float db = pixels[i].b - meanB;
                varR += dr * dr;
                varG += dg * dg;
                varB += db * db;
            }
            varR *= inv; varG *= inv; varB *= inv;

            return (varR + varG + varB) / 3f < varianceThreshold;
        }

        /// <summary>
        /// Compute average color of pixel array.
        /// </summary>
        public static Color AverageColor(Color[] pixels)
        {
            if (pixels.Length == 0) return Color.gray;

            float r = 0, g = 0, b = 0, a = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                r += pixels[i].r;
                g += pixels[i].g;
                b += pixels[i].b;
                a += pixels[i].a;
            }
            float inv = 1f / pixels.Length;
            return new Color(r * inv, g * inv, b * inv, a * inv);
        }

        // ════════════════════════════════════════════════════════════
        //  Internal helpers
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 1D DCT-II (Type 2) — standard cosine transform.
        /// Separable: apply on rows then columns for 2D DCT.
        /// </summary>
        static void Dct1D(float[] input, int offset, int N, float[] output, int outOffset)
        {
            float sqrtInv = 1f / Mathf.Sqrt(N);
            float sqrt2Inv = Mathf.Sqrt(2f / N);

            for (int k = 0; k < N; k++)
            {
                float sum = 0f;
                float factor = Mathf.PI * k / N;
                for (int n = 0; n < N; n++)
                    sum += input[offset + n] * Mathf.Cos(factor * (n + 0.5f));

                output[outOffset + k] = sum * (k == 0 ? sqrtInv : sqrt2Inv);
            }
        }

        static double Luminance(Color c)
        {
            return 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
        }
    }
}
