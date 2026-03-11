// TextureChannelSampler.cs — Sample material textures into ShellThumbnail per UV shell region
// Part of UV0 Atlas Optimizer

using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Samples texture channels (albedo, normal, gloss, AO) for a UV shell region
    /// into a fixed-size thumbnail for perceptual comparison.
    /// </summary>
    public static class TextureChannelSampler
    {
        /// <summary>
        /// Sample a shell region from material textures into a ShellThumbnail.
        /// </summary>
        /// <param name="albedo">Albedo/diffuse texture (can be null).</param>
        /// <param name="normal">Normal map (can be null).</param>
        /// <param name="gloss">Gloss/roughness/metallic texture (can be null).</param>
        /// <param name="ao">Ambient occlusion texture (can be null).</param>
        /// <param name="uvBbox">UV bounding box of the shell.</param>
        /// <param name="thumbSize">Thumbnail width/height in pixels.</param>
        public static ShellThumbnail Sample(Texture2D albedo, Texture2D normal,
            Texture2D gloss, Texture2D ao, Rect uvBbox, int thumbSize = 32)
        {
            var thumb = new ShellThumbnail(thumbSize, thumbSize);
            thumb.uvBbox = uvBbox;

            // Currently only albedo is used for similarity matching
            SampleChannel(albedo, uvBbox, thumb.albedo, thumbSize);
            // normal/gloss/ao left as neutral gray (default from constructor)

            return thumb;
        }

        static void SampleChannel(Texture2D tex, Rect uvBbox, Color[] output, int size)
        {
            if (tex == null)
            {
                // Fill with neutral gray
                for (int i = 0; i < output.Length; i++)
                    output[i] = new Color(0.5f, 0.5f, 0.5f, 1f);
                return;
            }

            bool wasReadable = tex.isReadable;
            string assetPath = null;

            if (!wasReadable)
            {
                assetPath = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer != null)
                    {
                        importer.isReadable = true;
                        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                    }
                }
            }

            try
            {
                int tw = tex.width;
                int th = tex.height;

                for (int y = 0; y < size; y++)
                {
                    float v = uvBbox.yMin + (y + 0.5f) / size * uvBbox.height;
                    for (int x = 0; x < size; x++)
                    {
                        float u = uvBbox.xMin + (x + 0.5f) / size * uvBbox.width;
                        // Bilinear sample using UV coordinates
                        int px = Mathf.Clamp(Mathf.FloorToInt(u * tw), 0, tw - 1);
                        int py = Mathf.Clamp(Mathf.FloorToInt(v * th), 0, th - 1);
                        output[y * size + x] = tex.GetPixel(px, py);
                    }
                }
            }
            finally
            {
                // Restore readability
                if (!wasReadable && !string.IsNullOrEmpty(assetPath))
                {
                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer != null)
                    {
                        importer.isReadable = false;
                        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                    }
                }
            }
        }

        /// <summary>
        /// Extract material textures from a Unity material.
        /// Returns textures for standard shader properties.
        /// </summary>
        public static void GetMaterialTextures(Material mat,
            out Texture2D albedo, out Texture2D normal, out Texture2D gloss, out Texture2D ao)
        {
            albedo = null; normal = null; gloss = null; ao = null;
            if (mat == null) return;

            if (mat.HasProperty("_MainTex"))
                albedo = mat.GetTexture("_MainTex") as Texture2D;
            if (mat.HasProperty("_BumpMap"))
                normal = mat.GetTexture("_BumpMap") as Texture2D;

            // Try multiple gloss/roughness property names
            if (mat.HasProperty("_GlossMap"))
                gloss = mat.GetTexture("_GlossMap") as Texture2D;
            else if (mat.HasProperty("_MetallicGlossMap"))
                gloss = mat.GetTexture("_MetallicGlossMap") as Texture2D;
            else if (mat.HasProperty("_SpecGlossMap"))
                gloss = mat.GetTexture("_SpecGlossMap") as Texture2D;

            if (mat.HasProperty("_OcclusionMap"))
                ao = mat.GetTexture("_OcclusionMap") as Texture2D;
        }
    }
}
