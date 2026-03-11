// ShellThumbnail.cs — Per-channel thumbnail data for a UV shell
// Part of UV0 Atlas Optimizer

using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// 32x32 per-channel thumbnail of a UV shell region, sampled from material textures.
    /// Used for perceptual similarity comparison between shells.
    /// </summary>
    public class ShellThumbnail
    {
        public Color[] albedo;
        public Color[] normal;
        public Color[] gloss;
        public Color[] ao;
        public int width  = 32;
        public int height = 32;

        /// <summary>True if the shell is visually monotone (very low variance).</summary>
        public bool isMonotone;
        /// <summary>Average color if monotone (used for 1px atlas bake).</summary>
        public Color monotoneColor;

        /// <summary>Source shell index in LOD0.</summary>
        public int shellIndex;
        /// <summary>UV bounding box of the source shell.</summary>
        public Rect uvBbox;

        public int PixelCount => width * height;

        public ShellThumbnail(int w = 32, int h = 32)
        {
            width = w;
            height = h;
            int n = w * h;
            albedo = new Color[n];
            normal = new Color[n];
            gloss  = new Color[n];
            ao     = new Color[n];
        }
    }
}
