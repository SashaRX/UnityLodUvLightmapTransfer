// TextureAtlasBaker.cs — Blit shell regions into new atlas textures per channel
// Part of UV0 Atlas Optimizer
//
// Important: albedo in sRGB, normal in linear — use correct RenderTextureReadWrite.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Bakes shell group textures into new atlas textures.
    /// For each texture channel (albedo, normal, gloss, AO), creates a packed atlas
    /// by blitting source shell regions with their transform applied.
    /// </summary>
    public static class TextureAtlasBaker
    {
        public struct BakerSettings
        {
            public int atlasSize;
            public string postfix;

            public static BakerSettings Default => new BakerSettings
            {
                atlasSize = 2048,
                postfix = "_repack"
            };
        }

        /// <summary>
        /// Bake all texture channels into new atlas textures.
        /// </summary>
        /// <param name="packResult">Pack result from RepackerAtlasPacker.</param>
        /// <param name="groups">Shell groups.</param>
        /// <param name="albedoTex">Original albedo texture.</param>
        /// <param name="normalTex">Original normal map.</param>
        /// <param name="glossTex">Original gloss/roughness texture.</param>
        /// <param name="aoTex">Original AO texture.</param>
        /// <param name="allUv0">Original per-LOD UV0 arrays.</param>
        /// <param name="allTris">Per-LOD triangle index arrays.</param>
        /// <param name="allShells">Per-LOD shell lists.</param>
        /// <param name="settings">Baker settings.</param>
        /// <param name="outputDir">Directory to save output textures.</param>
        /// <returns>Dictionary of channel name → baked Texture2D.</returns>
        public static Dictionary<string, Texture2D> Bake(
            RepackerAtlasPacker.PackResult packResult,
            List<ShellGroup> groups,
            Texture2D albedoTex, Texture2D normalTex, Texture2D glossTex, Texture2D aoTex,
            Vector2[] origUv0, int[] origTris, List<UvShell> lod0Shells,
            BakerSettings settings, string outputDir)
        {
            var result = new Dictionary<string, Texture2D>();
            int atlasW = packResult.atlasWidth;
            int atlasH = packResult.atlasHeight;

            // Bake each channel
            if (albedoTex != null)
            {
                var tex = BakeChannel(packResult, groups, albedoTex, origUv0, origTris,
                    lod0Shells, atlasW, atlasH, true, false);
                SaveTexture(tex, outputDir, albedoTex.name + settings.postfix, true);
                result["_MainTex"] = tex;
            }

            if (normalTex != null)
            {
                var tex = BakeChannel(packResult, groups, normalTex, origUv0, origTris,
                    lod0Shells, atlasW, atlasH, false, true);
                SaveTexture(tex, outputDir, normalTex.name + settings.postfix, false);
                result["_BumpMap"] = tex;
            }

            if (glossTex != null)
            {
                var tex = BakeChannel(packResult, groups, glossTex, origUv0, origTris,
                    lod0Shells, atlasW, atlasH, false, false);
                SaveTexture(tex, outputDir, glossTex.name + settings.postfix, false);
                result["_MetallicGlossMap"] = tex;
            }

            if (aoTex != null)
            {
                var tex = BakeChannel(packResult, groups, aoTex, origUv0, origTris,
                    lod0Shells, atlasW, atlasH, false, false);
                SaveTexture(tex, outputDir, aoTex.name + settings.postfix, false);
                result["_OcclusionMap"] = tex;
            }

            return result;
        }

        /// <summary>
        /// Bake a single texture channel into the atlas.
        /// </summary>
        static Texture2D BakeChannel(
            RepackerAtlasPacker.PackResult packResult,
            List<ShellGroup> groups,
            Texture2D sourceTex,
            Vector2[] origUv0, int[] origTris, List<UvShell> lod0Shells,
            int atlasW, int atlasH,
            bool isSrgb, bool isNormal)
        {
            // Ensure source is readable
            bool wasReadable = sourceTex.isReadable;
            string assetPath = AssetDatabase.GetAssetPath(sourceTex);
            if (!wasReadable && !string.IsNullOrEmpty(assetPath))
            {
                var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (imp != null) { imp.isReadable = true; AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate); }
            }

            try
            {
                var atlas = new Texture2D(atlasW, atlasH, TextureFormat.RGBA32, false, !isSrgb);
                var pixels = new Color[atlasW * atlasH];

                // Clear to transparent black
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = isNormal ? new Color(0.5f, 0.5f, 1f, 1f) : Color.clear;

                int srcW = sourceTex.width;
                int srcH = sourceTex.height;

                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var group = groups[gi];
                    Rect groupRect = packResult.groupRects[gi];

                    if (groupRect.width <= 0 || groupRect.height <= 0) continue;

                    // Find source shell
                    UvShell srcShell = null;
                    foreach (var s in lod0Shells)
                        if (s.shellId == group.sourceShellId) { srcShell = s; break; }
                    if (srcShell == null) continue;

                    // Original UV bbox of source shell
                    Rect origBbox = MeshUvUtils.GetShellUvBbox(origUv0, srcShell.faceIndices, origTris);

                    if (group.isMonotone)
                    {
                        // Monotone: fill group rect with solid color
                        Color fillColor = group.monotoneColor;
                        if (isNormal) fillColor = new Color(0.5f, 0.5f, 1f, 1f); // flat normal

                        int px0 = Mathf.FloorToInt(groupRect.xMin * atlasW);
                        int py0 = Mathf.FloorToInt(groupRect.yMin * atlasH);
                        int px1 = Mathf.CeilToInt(groupRect.xMax * atlasW);
                        int py1 = Mathf.CeilToInt(groupRect.yMax * atlasH);
                        px0 = Mathf.Clamp(px0, 0, atlasW - 1);
                        py0 = Mathf.Clamp(py0, 0, atlasH - 1);
                        px1 = Mathf.Clamp(px1, 0, atlasW);
                        py1 = Mathf.Clamp(py1, 0, atlasH);

                        for (int y = py0; y < py1; y++)
                            for (int x = px0; x < px1; x++)
                                pixels[y * atlasW + x] = fillColor;
                    }
                    else
                    {
                        // Non-monotone: blit from source texture using UV mapping
                        BlitShellRegion(pixels, atlasW, atlasH, groupRect,
                            sourceTex, srcW, srcH, origBbox, isNormal);
                    }
                }

                atlas.SetPixels(pixels);
                atlas.Apply();
                return atlas;
            }
            finally
            {
                // Restore readability
                if (!wasReadable && !string.IsNullOrEmpty(assetPath))
                {
                    var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (imp != null) { imp.isReadable = false; AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate); }
                }
            }
        }

        /// <summary>
        /// Blit a shell region from source texture to atlas.
        /// Maps normalized atlas rect to source UV bbox.
        /// </summary>
        static void BlitShellRegion(Color[] atlasPixels, int atlasW, int atlasH, Rect atlasRect,
            Texture2D srcTex, int srcW, int srcH, Rect srcUvBbox, bool isNormal)
        {
            int ax0 = Mathf.FloorToInt(atlasRect.xMin * atlasW);
            int ay0 = Mathf.FloorToInt(atlasRect.yMin * atlasH);
            int ax1 = Mathf.CeilToInt(atlasRect.xMax * atlasW);
            int ay1 = Mathf.CeilToInt(atlasRect.yMax * atlasH);
            ax0 = Mathf.Clamp(ax0, 0, atlasW);
            ay0 = Mathf.Clamp(ay0, 0, atlasH);
            ax1 = Mathf.Clamp(ax1, 0, atlasW);
            ay1 = Mathf.Clamp(ay1, 0, atlasH);

            int dw = ax1 - ax0;
            int dh = ay1 - ay0;
            if (dw <= 0 || dh <= 0) return;

            for (int dy = 0; dy < dh; dy++)
            {
                float t = (dy + 0.5f) / dh;
                float srcV = srcUvBbox.yMin + t * srcUvBbox.height;
                int srcY = Mathf.Clamp(Mathf.FloorToInt(srcV * srcH), 0, srcH - 1);

                for (int dx = 0; dx < dw; dx++)
                {
                    float s = (dx + 0.5f) / dw;
                    float srcU = srcUvBbox.xMin + s * srcUvBbox.width;
                    int srcX = Mathf.Clamp(Mathf.FloorToInt(srcU * srcW), 0, srcW - 1);

                    Color c = srcTex.GetPixel(srcX, srcY);
                    atlasPixels[(ay0 + dy) * atlasW + (ax0 + dx)] = c;
                }
            }
        }

        /// <summary>
        /// Save a Texture2D as PNG to disk and import into AssetDatabase.
        /// </summary>
        static void SaveTexture(Texture2D tex, string outputDir, string name, bool isSrgb)
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            string path = Path.Combine(outputDir, name + ".png");
            byte[] png = tex.EncodeToPNG();
            File.WriteAllBytes(path, png);

            // Make path relative to project
            string projectPath = Application.dataPath;
            if (path.StartsWith(projectPath))
                path = "Assets" + path.Substring(projectPath.Length);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            // Configure import settings
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.sRGBTexture = isSrgb;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.maxTextureSize = tex.width;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
