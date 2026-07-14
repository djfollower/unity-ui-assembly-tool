using System;
using UnityEngine;
using UnityEngine.UI;

namespace UiAssemblerSlice.Editor.Assembler
{
    /// T3.1: creates the root Canvas with the project's real, existing
    /// CanvasScaler settings - read, never modified. "Read" here means
    /// element-tree.json's canvas_reference/canvas_match_mode/
    /// canvas_match_value, which normalize.ts (T1.10) already wrote from a
    /// hardcoded config confirmed to match the project's real CanvasScaler
    /// setting - not a second, independent live query into an existing
    /// Melon Canvas asset (confirmed with the user rather than assumed).
    public static class CanvasScaffold
    {
        public static Canvas CreateRootCanvas(ElementTreeData elementTree)
        {
            if (elementTree.CanvasMatchMode != "match_width_or_height")
            {
                // Mirrors normalize.ts's own restriction (computeScaleFactor) -
                // the fixture's CanvasScaler never uses "expand"/"shrink", so
                // there's no real data to implement/verify those against yet.
                throw new NotImplementedException(
                    $"CanvasScaffold: canvas_match_mode \"{elementTree.CanvasMatchMode}\" is not implemented for this slice " +
                    "(only match_width_or_height, matching the fixture's actual CanvasScaler setting)");
            }

            var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(elementTree.CanvasReference.W, elementTree.CanvasReference.H);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = elementTree.CanvasMatchValue;

            return canvas;
        }
    }
}
