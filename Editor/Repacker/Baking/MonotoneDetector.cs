// MonotoneDetector.cs — Detects monotone (solid color) regions in thumbnails
// Part of UV0 Atlas Optimizer

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Detects whether a shell thumbnail is effectively monotone (solid color)
    /// and extracts the dominant color.
    /// </summary>
    public static class MonotoneDetector
    {
        /// <summary>
        /// Check if pixel array is monotone (very low variance).
        /// Delegates to PerceptualHasher.IsMonotone.
        /// </summary>
        public static bool IsMonotone(Color[] pixels, float threshold = 0.008f)
        {
            return PerceptualHasher.IsMonotone(pixels, threshold);
        }

        /// <summary>
        /// Get the dominant (average) color of a pixel array.
        /// </summary>
        public static Color GetDominantColor(Color[] pixels)
        {
            return PerceptualHasher.AverageColor(pixels);
        }

        /// <summary>
        /// Check if a shell thumbnail is monotone across all channels.
        /// Returns true only if ALL non-null channels are monotone.
        /// </summary>
        public static bool IsShellMonotone(ShellThumbnail thumb, float threshold = 0.008f)
        {
            if (thumb == null) return false;
            return IsMonotone(thumb.albedo, threshold)
                && IsMonotone(thumb.normal, threshold)
                && IsMonotone(thumb.gloss, threshold)
                && IsMonotone(thumb.ao, threshold);
        }
    }
}
