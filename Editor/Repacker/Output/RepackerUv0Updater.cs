// RepackerUv0Updater.cs — Apply new UV0 to meshes, save as _repack.asset
// Part of UV0 Atlas Optimizer
// CRITICAL: UV2 is NEVER modified. Hash UV2 before/after to verify.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Applies the new UV0 coordinates from the packing result to all LOD meshes.
    /// Saves new meshes as {original}_repack.asset alongside originals.
    /// UV2 (lightmap) is preserved exactly — verified by hash.
    /// </summary>
    public static class RepackerUv0Updater
    {
        /// <summary>
        /// Apply new UV0 to all LOD meshes and save as _repack assets.
        /// </summary>
        /// <param name="packResult">Pack result with new UV0 per LOD.</param>
        /// <param name="meshes">Per-LOD original meshes.</param>
        /// <param name="postfix">Output postfix (e.g. "_repack").</param>
        /// <returns>Array of saved mesh assets per LOD.</returns>
        public static Mesh[] Apply(
            RepackerAtlasPacker.PackResult packResult,
            Mesh[] meshes,
            string postfix = "_repack")
        {
            int lodCount = Mathf.Min(meshes.Length, packResult.newUv0PerLod.Length);
            var result = new Mesh[lodCount];

            for (int lod = 0; lod < lodCount; lod++)
            {
                Mesh original = meshes[lod];
                if (original == null) continue;

                // Hash UV2 before modification
                List<Vector2> uv2Before = new List<Vector2>();
                original.GetUVs(1, uv2Before);
                int uv2HashBefore = ComputeUv2Hash(uv2Before);

                // Create copy
                Mesh copy = Object.Instantiate(original);
                copy.name = original.name + postfix;

                // Apply new UV0
                Vector2[] newUv0 = packResult.newUv0PerLod[lod];
                if (newUv0 != null && newUv0.Length > 0)
                {
                    copy.SetUVs(0, new List<Vector2>(newUv0));
                }

                // Verify UV2 unchanged
                List<Vector2> uv2After = new List<Vector2>();
                copy.GetUVs(1, uv2After);
                int uv2HashAfter = ComputeUv2Hash(uv2After);

                if (uv2HashBefore != uv2HashAfter)
                {
                    UvtLog.Error($"[RepackerUv0Updater] UV2 CHANGED on LOD{lod} mesh '{original.name}'! " +
                                 $"Hash before={uv2HashBefore}, after={uv2HashAfter}. This should not happen!");
                    // Restore UV2 from original
                    copy.SetUVs(1, uv2Before);
                }

                // Save as asset
                string outputPath = GetOutputPath(original, postfix);
                if (!string.IsNullOrEmpty(outputPath))
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    AssetDatabase.CreateAsset(copy, outputPath);
                    UvtLog.Info($"[RepackerUv0Updater] Saved LOD{lod}: {outputPath}");
                }

                result[lod] = copy;
            }

            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>
        /// Compute a hash of UV2 data for integrity verification.
        /// </summary>
        static int ComputeUv2Hash(List<Vector2> uvs)
        {
            if (uvs == null || uvs.Count == 0) return 0;

            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < uvs.Count; i++)
                {
                    int qx = Mathf.RoundToInt(uvs[i].x * 100000f);
                    int qy = Mathf.RoundToInt(uvs[i].y * 100000f);
                    h = (h ^ (uint)qx) * 16777619u;
                    h = (h ^ (uint)qy) * 16777619u;
                }
                return (int)h;
            }
        }

        /// <summary>
        /// Determine output asset path for a mesh.
        /// </summary>
        static string GetOutputPath(Mesh original, string postfix)
        {
            string origPath = AssetDatabase.GetAssetPath(original);
            if (string.IsNullOrEmpty(origPath))
            {
                // Fallback: save in Assets/
                return "Assets/" + original.name + postfix + ".asset";
            }

            string dir = Path.GetDirectoryName(origPath);
            string name = Path.GetFileNameWithoutExtension(origPath);
            return Path.Combine(dir, name + postfix + ".asset").Replace('\\', '/');
        }
    }
}
