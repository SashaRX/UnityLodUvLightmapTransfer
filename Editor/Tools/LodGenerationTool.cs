// LodGenerationTool.cs — Stub: LOD generation via mesh simplification.
// Wraps existing MeshSimplifier. Placeholder for full implementation.

using System;
using System.Collections.Generic;
using UnityEditor;

namespace LightmapUvTool
{
    public class LodGenerationTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "LOD Gen";
        public string ToolId    => "lod_generation";
        public int    ToolOrder => 30;

        public Action RequestRepaint { set => requestRepaint = value; }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            this.ctx = ctx;
            this.canvas = canvas;
        }

        public void OnDeactivate() { }
        public void OnRefresh() { }

        public void OnDrawSidebar()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("LOD Generation", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "LOD mesh generation using meshoptimizer simplification.\n" +
                "Preserves UV2 lightmap coordinates with configurable weights.\n\n" +
                "Uses the existing MeshSimplifier infrastructure.\n" +
                "Not yet implemented as a standalone tool.",
                MessageType.Info);
        }

        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }

        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield return new UvCanvasView.FillModeEntry { name = "Shells" };
        }

        public void OnSceneGUI(SceneView sv) { }
    }
}
