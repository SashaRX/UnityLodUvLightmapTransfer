// PostprocessorDefineManager.cs — Manages the LIGHTMAP_UV_TOOL_POSTPROCESSOR
// scripting define that enables/disables the sidecar UV2 postprocessor.
// Without this define, the package never touches model imports on install.

using System.Collections.Generic;
using UnityEditor;

namespace LightmapUvTool
{
    static class PostprocessorDefineManager
    {
        const string DEFINE = "LIGHTMAP_UV_TOOL_POSTPROCESSOR";

        static BuildTargetGroup ActiveGroup =>
            EditorUserBuildSettings.selectedBuildTargetGroup;

        internal static bool IsEnabled()
        {
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(ActiveGroup);
            return defines.Contains(DEFINE);
        }

        internal static void SetEnabled(bool enabled)
        {
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(ActiveGroup);
            var list = new List<string>(defines.Split(';'));
            list.RemoveAll(d => d.Trim() == DEFINE);
            if (enabled) list.Add(DEFINE);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(ActiveGroup, string.Join(";", list));
        }
    }
}
