using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// ヒエラルキー上の 1 オブジェクトを一時的に色付けし、フェードで消す。
    /// </summary>
    internal static class HierarchyHighlight
    {
        private const double DurationSeconds = 1.8;
        private const double HoldRatio = 0.25;
        private const float MaxAlpha = 0.45f;
        private static readonly Color HighlightColor = new Color(0.298f, 0.686f, 0.314f);

        private static int _instanceId;
        private static double _startTime;
        private static bool _hooked;

        internal static void Flash(GameObject target)
        {
            if (target == null) return;

            _instanceId = target.GetInstanceID();
            _startTime = EditorApplication.timeSinceStartup;

            if (!_hooked)
            {
                EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGui;
                EditorApplication.update += OnUpdate;
                _hooked = true;
            }

            EditorApplication.RepaintHierarchyWindow();
        }

        private static void OnUpdate()
        {
            if (Progress() >= 1.0)
            {
                Stop();
            }

            EditorApplication.RepaintHierarchyWindow();
        }

        private static void OnHierarchyItemGui(int instanceId, Rect selectionRect)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (instanceId != _instanceId) return;

            var alpha = AlphaAt(Progress());
            if (alpha <= 0f) return;

            var rect = new Rect(0f, selectionRect.y, EditorGUIUtility.currentViewWidth, selectionRect.height);
            EditorGUI.DrawRect(rect, new Color(HighlightColor.r, HighlightColor.g, HighlightColor.b, alpha));
        }

        private static double Progress()
        {
            if (!_hooked) return 1.0;
            return (EditorApplication.timeSinceStartup - _startTime) / DurationSeconds;
        }

        private static float AlphaAt(double progress)
        {
            if (progress >= 1.0) return 0f;
            if (progress <= HoldRatio) return MaxAlpha;

            var t = (float)((progress - HoldRatio) / (1.0 - HoldRatio));
            return MaxAlpha * (1f - Mathf.SmoothStep(0f, 1f, t));
        }

        private static void Stop()
        {
            if (!_hooked) return;

            EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyItemGui;
            EditorApplication.update -= OnUpdate;

            _hooked = false;
            _instanceId = 0;
        }
    }
}

