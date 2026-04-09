// PostprocessorDefineManager.cs — Manages the LIGHTMAP_UV_TOOL_POSTPROCESSOR
// scripting define that enables/disables the sidecar UV2 postprocessor.
// Without this define, the package never touches model imports on install.

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;

namespace LightmapUvTool
{
    static class PostprocessorDefineManager
    {
        const string DEFINE = "LIGHTMAP_UV_TOOL_POSTPROCESSOR";

        internal static bool IsEnabled()
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            var defines = PlayerSettings.GetScriptingDefineSymbols(target);
            return defines.Contains(DEFINE);
        }

        internal static void SetEnabled(bool enabled)
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            var defines = PlayerSettings.GetScriptingDefineSymbols(target);
            var list = new List<string>(defines.Split(';'));
            list.RemoveAll(d => d.Trim() == DEFINE);
            if (enabled) list.Add(DEFINE);
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", list));
        }
    }
}
