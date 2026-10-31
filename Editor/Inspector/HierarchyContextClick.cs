using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// ヒエラルキーで直近に右クリックされた行を覚えておく。
    /// </summary>
    [InitializeOnLoad]
    internal static class HierarchyContextClick
    {
        private const double ValidSeconds = 30.0;

        private static int _instanceId;
        private static double _time = double.NegativeInfinity;

        static HierarchyContextClick()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGui;
        }

        internal static bool TryConsumeSelected(out GameObject gameObject)
        {
            gameObject = null;

            if (_instanceId == 0) return false;
            if (EditorApplication.timeSinceStartup - _time > ValidSeconds) return false;

            var go = EditorUtility.InstanceIDToObject(_instanceId) as GameObject;
            if (go == null || !Selection.Contains(go)) return false;

            _instanceId = 0;
            gameObject = go;
            return true;
        }

        private static void OnHierarchyItemGui(int instanceId, Rect selectionRect)
        {
            var e = Event.current;

            var isRightClick = (e.type == EventType.MouseDown && e.button == 1) || e.type == EventType.ContextClick;
            var isLeftClick = e.type == EventType.MouseDown && e.button == 0;
            if (!isRightClick && !isLeftClick) return;

            var row = new Rect(0f, selectionRect.y, EditorGUIUtility.currentViewWidth, selectionRect.height);
            if (!row.Contains(e.mousePosition)) return;

            if (isRightClick)
            {
                _instanceId = instanceId;
                _time = EditorApplication.timeSinceStartup;
            }
            else
            {
                _instanceId = 0;
            }
        }
    }
}

