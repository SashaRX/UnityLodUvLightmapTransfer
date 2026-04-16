// PostprocessorDefineManager.cs — Manages the persistent sidecar replay toggle.
// The UV postprocessor is always compiled; this flag only controls whether
// sidecar replay stays enabled for future imports or is used transiently.

using UnityEditor;

namespace LightmapUvTool
{
    static class PostprocessorDefineManager
    {
        const string PrefKey = "LightmapUvTool.SidecarUv2Mode";

        internal static bool IsEnabled()
        {
            return EditorPrefs.GetBool(PrefKey, false);
        }

        internal static void SetEnabled(bool enabled)
        {
            EditorPrefs.SetBool(PrefKey, enabled);
        }
    }
}
