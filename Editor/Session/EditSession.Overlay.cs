using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    internal partial class EditSession
    {
        private Rect _overlayRect;
        private static bool _hasCustomOverlayPosition;
        private static Vector2 _overlayPosition;
        private static bool _overlayDragging;
        private static Vector2 _overlayDragOffset;

        private EffectMode _activeEffectMode = EffectMode.None;
        private float _smoothSlider;
        private float _restoreShapeSlider;
        private float _flattenSlider;
        private float _eraseSlider;

        private void ResetEffectSliders()
        {
            _activeEffectMode = EffectMode.None;
            _smoothSlider = 0f;
            _restoreShapeSlider = 0f;
            _flattenSlider = 0f;
            _eraseSlider = 0f;
        }

        private void DrawOverlay(SceneView sceneView)
        {
            if (Event.current.type == EventType.Layout)
            {
                _overlayShowsWarning = _showFallbackWarning;
            }

            var current = Event.current;
            var overlayHeight = _overlayShowsWarning ? 288f : 248f;
            var overlayWidth = 340f;
            const float margin = 10f;

            var canvasWidth = GetCanvasWidth(sceneView);
            var canvasHeight = GetCanvasHeight(sceneView);

            var maxX = Mathf.Max(margin, canvasWidth - overlayWidth - margin);
            var maxY = Mathf.Max(margin, canvasHeight - overlayHeight - margin);

            if (!_hasCustomOverlayPosition)
            {
                _overlayPosition.x = maxX;
                _overlayPosition.y = maxY;
            }
            else
            {
                _overlayPosition.x = Mathf.Clamp(_overlayPosition.x, margin, maxX);
                _overlayPosition.y = Mathf.Clamp(_overlayPosition.y, margin, maxY);
            }

            _overlayRect = new Rect(_overlayPosition.x, _overlayPosition.y, overlayWidth, overlayHeight);
            var headerRect = new Rect(_overlayRect.x, _overlayRect.y, _overlayRect.width, 24f);

            if (current.type == EventType.MouseDown && current.button == 0 && headerRect.Contains(current.mousePosition))
            {
                _overlayDragging = true;
                _overlayDragOffset = current.mousePosition - _overlayPosition;
                _hasCustomOverlayPosition = true;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && _overlayDragging)
            {
                _overlayPosition = current.mousePosition - _overlayDragOffset;
                _overlayPosition.x = Mathf.Clamp(_overlayPosition.x, margin, maxX);
                _overlayPosition.y = Mathf.Clamp(_overlayPosition.y, margin, maxY);
                current.Use();
                GUI.changed = true;
            }
            else if ((current.type == EventType.MouseUp || current.rawType == EventType.MouseUp) && _overlayDragging)
            {
                _overlayDragging = false;
                current.Use();
            }

            Handles.BeginGUI();

            EditorGUI.DrawRect(_overlayRect, new Color(0.16f, 0.16f, 0.16f, 0.94f));
            DrawOutlineRect(_overlayRect, new Color(0.25f, 0.88f, 0.45f, 0.95f), 2f);
            EditorGUIUtility.AddCursorRect(headerRect, MouseCursor.MoveArrow);

            GUILayout.BeginArea(_overlayRect, GUIStyle.none);
            GUILayout.Space(6);

            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 78f;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            GUILayout.Label(DenNormalEditorLocalization.Tr("overlay.title"), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("⠿", EditorStyles.miniLabel);
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();

            EditorGUI.BeginChangeCheck();
            var radius = EditorGUILayout.Slider(
                DenNormalEditorLocalization.Tr("overlay.radius"), BrushRadius, MinBrushRadius, MaxBrushRadius);
            var falloff = (FalloffType)EditorGUILayout.EnumPopup(
                DenNormalEditorLocalization.Tr("overlay.falloff"), _component.falloff);
            if (EditorGUI.EndChangeCheck())
            {
                if (_settingsUndoGroup < 0)
                {
                    _settingsUndoGroup = DenNormalEditorUndo.BeginGroup(_component, "Dennoko Normal Editor Settings");
                }

                _hasPendingRadius = false;
                _component.brushRadius = radius;
                _component.falloff = falloff;
                DenNormalEditorUndo.Apply(_component);

                if (_hasSelection)
                {
                    RecomputeMirrorCenter();
                    BuildInfluences();
                }
            }

            if (_settingsUndoGroup >= 0 && Event.current.rawType == EventType.MouseUp)
            {
                DenNormalEditorUndo.Collapse(_settingsUndoGroup);
                _settingsUndoGroup = -1;
            }

            GUILayout.Space(3);

            var mirrorActive = _component.mirror;
            var prevColor = GUI.backgroundColor;
            if (mirrorActive)
            {
                GUI.backgroundColor = new Color(0.35f, 0.95f, 0.45f);
            }

            var buttonText = mirrorActive
                ? DenNormalEditorLocalization.Tr("overlay.mirror_on")
                : DenNormalEditorLocalization.Tr("overlay.mirror_off");
            if (GUILayout.Button(buttonText, GUILayout.Height(24)))
            {
                DenNormalEditorUndo.BeginGroup(_component, "Dennoko Normal Editor Mirror Toggle");
                _component.mirror = !mirrorActive;
                DenNormalEditorUndo.Apply(_component);
                DenNormalEditorUndo.EndGroup();

                if (_hasSelection)
                {
                    RecomputeMirrorCenter();
                    BuildInfluences();
                }

                SceneView.RepaintAll();
            }

            GUI.backgroundColor = prevColor;

            GUILayout.Space(4);
            GUILayout.Label(DenNormalEditorLocalization.Tr("overlay.effects_header"), EditorStyles.miniBoldLabel);

            using (new EditorGUI.DisabledScope(!_hasSelection))
            {
                DrawEffectSlider(EffectMode.Smooth, "overlay.effect_smooth", ref _smoothSlider);
                DrawEffectSlider(EffectMode.RestoreShape, "overlay.effect_restore_shape", ref _restoreShapeSlider);
                DrawEffectSlider(EffectMode.Flatten, "overlay.effect_flatten", ref _flattenSlider);
                DrawEffectSlider(EffectMode.Erase, "overlay.effect_erase", ref _eraseSlider);
            }

            if (_activeEffectMode != EffectMode.None &&
                (Event.current.rawType == EventType.MouseUp || GUIUtility.hotControl == 0))
            {
                var finishedMode = _activeEffectMode;
                ResetEffectSliders();
                Commit($"Dennoko Normal Editor {finishedMode}");
                CommitSnapshot();
                SyncHandleToSelection();
                GUI.changed = true;
            }

            GUILayout.Space(2);

            var hintStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                normal = { textColor = new Color(0.3f, 0.95f, 0.45f) }
            };
            GUILayout.Label(DenNormalEditorLocalization.Tr("overlay.hint_wheel"), hintStyle);
            GUILayout.Label(DenNormalEditorLocalization.Tr("overlay.hint_esc"), hintStyle);

            if (_overlayShowsWarning)
            {
                EditorGUILayout.HelpBox(DenNormalEditorLocalization.Tr("overlay.warn_ndmf_fallback"),
                    MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();

            EditorGUIUtility.labelWidth = previousLabelWidth;

            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private void DrawEffectSlider(EffectMode mode, string labelKey, ref float value)
        {
            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.Slider(DenNormalEditorLocalization.Tr(labelKey), value, 0f, 1f);
            if (EditorGUI.EndChangeCheck() && _hasSelection)
            {
                if (_activeEffectMode != mode)
                {
                    ResetEffectSliders();
                    _activeEffectMode = mode;
                }

                value = next;
                ApplyEffect(mode, value);
            }
        }

        private static float GetCanvasWidth(SceneView sceneView)
        {
            if (sceneView != null && sceneView.camera != null)
            {
                var ppp = EditorGUIUtility.pixelsPerPoint;
                if (ppp > 0f && sceneView.camera.pixelWidth > 0)
                {
                    return sceneView.camera.pixelWidth / ppp;
                }
            }
            return sceneView != null ? sceneView.position.width : 800f;
        }

        private static float GetCanvasHeight(SceneView sceneView)
        {
            if (sceneView != null && sceneView.camera != null)
            {
                var ppp = EditorGUIUtility.pixelsPerPoint;
                if (ppp > 0f && sceneView.camera.pixelHeight > 0)
                {
                    return sceneView.camera.pixelHeight / ppp;
                }
            }
            return sceneView != null ? sceneView.position.height : 600f;
        }

        private static void DrawOutlineRect(Rect rect, Color color, float width = 2f)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, width), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + width, width, rect.height - width * 2f), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - width, rect.y + width, width, rect.height - width * 2f), color);
        }
    }
}

