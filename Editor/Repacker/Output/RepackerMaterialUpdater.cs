// RepackerMaterialUpdater.cs — Update material texture slots with _repack versions
// Part of UV0 Atlas Optimizer

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// Updates a material's texture slots to point to the repacked atlas textures.
    /// Supports standard shader properties: _MainTex, _BumpMap, _MetallicGlossMap, _OcclusionMap.
    /// </summary>
    public static class RepackerMaterialUpdater
    {
        /// <summary>
        /// Property name mappings for standard Unity shaders.
        /// </summary>
        static readonly string[] TextureProperties = new[]
        {
            "_MainTex",
            "_BumpMap",
            "_MetallicGlossMap",
            "_SpecGlossMap",
            "_GlossMap",
            "_OcclusionMap"
        };

        /// <summary>
        /// Apply repacked textures to a material.
        /// </summary>
        /// <param name="mat">Target material.</param>
        /// <param name="repackedTextures">Dictionary of property name → repacked Texture2D.</param>
        public static void Apply(Material mat, Dictionary<string, Texture2D> repackedTextures)
        {
            if (mat == null || repackedTextures == null) return;

            foreach (var kv in repackedTextures)
            {
                string propName = kv.Key;
                Texture2D tex = kv.Value;

                if (mat.HasProperty(propName))
                {
                    mat.SetTexture(propName, tex);
                    UvtLog.Info($"[RepackerMaterialUpdater] Set {propName} → {tex.name} on material '{mat.name}'");
                }

                // Handle alternative property name for gloss
                if (propName == "_MetallicGlossMap")
                {
                    if (mat.HasProperty("_GlossMap"))
                        mat.SetTexture("_GlossMap", tex);
                    if (mat.HasProperty("_SpecGlossMap"))
                        mat.SetTexture("_SpecGlossMap", tex);
                }
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Apply repacked meshes to renderers in a LODGroup.
        /// </summary>
        /// <param name="lodGroup">Target LOD group.</param>
        /// <param name="repackedMeshes">Per-LOD repacked meshes.</param>
        public static void ApplyMeshes(LODGroup lodGroup, Mesh[] repackedMeshes)
        {
            if (lodGroup == null || repackedMeshes == null) return;

            var lods = lodGroup.GetLODs();
            for (int li = 0; li < lods.Length && li < repackedMeshes.Length; li++)
            {
                if (repackedMeshes[li] == null) continue;

                foreach (var renderer in lods[li].renderers)
                {
                    if (renderer == null) continue;
                    var mf = renderer.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        mf.sharedMesh = repackedMeshes[li];
                        EditorUtility.SetDirty(mf);
                    }
                }
            }
        }
    }
}
