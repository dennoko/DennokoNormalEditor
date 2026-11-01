using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dennokoworks.DenNormalEditor.Editor
{
    [CustomEditor(typeof(DenNormalEditor))]
    internal class DenNormalEditorInspector : UnityEditor.Editor
    {
        private SerializedProperty _edits;
        private SerializedProperty _brushRadius;
        private SerializedProperty _falloff;
        private SerializedProperty _mirror;
        private SerializedProperty _mirrorAxis;

        private static bool _menuHandled;

        [MenuItem("GameObject/dennokoworks/Dennoko Normal Editor", false, 21)]
        private static void AddDenNormalEditorMenuItem(MenuCommand menuCommand)
        {
            if (_menuHandled) return;
            _menuHandled = true;
            EditorApplication.delayCall += () => _menuHandled = false;

            var renderers = CollectSelectedRenderers();
            var target = ResolveMenuTarget(menuCommand.context != null, renderers);

            if (target == null)
            {
                target = new GameObject("DenNormalEditor");
                Undo.RegisterCreatedObjectUndo(target, "Create DenNormalEditor");
                GameObjectUtility.SetParentAndAlign(target, menuCommand.context as GameObject);
            }

            var component = target.GetComponent<DenNormalEditor>();
            if (component == null)
            {
                component = Undo.AddComponent<DenNormalEditor>(target);
            }

            var ownRenderer = target.GetComponent<Renderer>();
            if (IsEditableRenderer(ownRenderer))
            {
                renderers.Remove(ownRenderer);
                renderers.Insert(0, ownRenderer);
            }

            var recorded = false;
            foreach (var renderer in renderers)
            {
                if (component.FindEdit(renderer) != null) continue;

                if (!recorded)
                {
                    DenNormalEditorUndo.Record(component, "Add Target to DenNormalEditor");
                    recorded = true;
                }

                component.edits.Add(new NormalEdit { target = renderer });
            }

            if (recorded)
            {
                DenNormalEditorUndo.Apply(component);
            }

            Selection.activeGameObject = target;
            HierarchyHighlight.Flash(target);

            if (HasEditableMesh(component))
            {
                EditSession.Begin(component);
            }
        }

        private static List<Renderer> CollectSelectedRenderers()
        {
            var result = new List<Renderer>();

            foreach (var go in CollectSelectedSceneObjects())
            {
                var renderer = go.GetComponent<Renderer>();
                if (!IsEditableRenderer(renderer)) continue;
                if (result.Contains(renderer)) continue;

                result.Add(renderer);
            }

            return result;
        }

        private static List<GameObject> CollectSelectedSceneObjects()
        {
            var result = new List<GameObject>();

            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                if (EditorUtility.IsPersistent(go)) continue;

                result.Add(go);
            }

            return result;
        }

        private static GameObject ResolveMenuTarget(bool fromContextMenu, List<Renderer> renderers)
        {
            if (fromContextMenu && HierarchyContextClick.TryConsumeSelected(out var clicked) &&
                !EditorUtility.IsPersistent(clicked))
            {
                return clicked;
            }

            var candidates = renderers.Count > 0
                ? renderers.ConvertAll(renderer => renderer.gameObject)
                : CollectSelectedSceneObjects();

            var active = Selection.activeGameObject;
            if (active != null && candidates.Contains(active)) return active;

            return FindTopmostInHierarchy(candidates);
        }

        private static GameObject FindTopmostInHierarchy(List<GameObject> objects)
        {
            GameObject best = null;
            List<int> bestKey = null;

            foreach (var go in objects)
            {
                var key = HierarchyOrderKey(go);
                if (bestKey != null && CompareOrderKeys(key, bestKey) >= 0) continue;

                best = go;
                bestKey = key;
            }

            return best;
        }

        private static List<int> HierarchyOrderKey(GameObject go)
        {
            var key = new List<int>();

            for (var t = go.transform; t != null; t = t.parent)
            {
                key.Add(t.GetSiblingIndex());
            }

            key.Add(SceneOrder(go.scene));
            key.Reverse();

            return key;
        }

        private static int SceneOrder(Scene scene)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i) == scene) return i;
            }

            return SceneManager.sceneCount;
        }

        private static int CompareOrderKeys(List<int> a, List<int> b)
        {
            var count = Mathf.Min(a.Count, b.Count);

            for (var i = 0; i < count; i++)
            {
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            }

            return a.Count.CompareTo(b.Count);
        }

        private static bool IsEditableRenderer(Renderer renderer)
        {
            return renderer is SkinnedMeshRenderer || renderer is MeshRenderer;
        }

        private static bool HasEditableMesh(DenNormalEditor component)
        {
            foreach (var edit in component.edits)
            {
                if (edit != null && NormalDeltaApplier.GetSharedMesh(edit.target) != null) return true;
            }

            return false;
        }

        private void OnEnable()
        {
            _edits = serializedObject.FindProperty("edits");
            _brushRadius = serializedObject.FindProperty("brushRadius");
            _falloff = serializedObject.FindProperty("falloff");
            _mirror = serializedObject.FindProperty("mirror");
            _mirrorAxis = serializedObject.FindProperty("mirrorAxis");

            DenNormalEditorLocalization.OnLanguageChanged += OnLanguageChanged;
        }

        private void OnDisable()
        {
            DenNormalEditorLocalization.OnLanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged()
        {
            Repaint();
        }

        public override bool RequiresConstantRepaint()
        {
            return target is DenNormalEditor component && EditSession.IsActive(component);
        }

        public override void OnInspectorGUI()
        {
            var component = (DenNormalEditor)target;
            serializedObject.Update();

            DrawHeaderBar();
            EditorGUILayout.Space();
            DrawTargets();
            EditorGUILayout.Space();
            DrawEditControls(component);
            EditorGUILayout.Space();
            DrawBrushSettings();
            EditorGUILayout.Space();
            DrawBakeSection(component);

            if (serializedObject.ApplyModifiedProperties() && EditSession.IsActive(component))
            {
                EditSession.NotifySettingsChanged();
            }
        }

        private static void DrawHeaderBar()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(
                    new GUIContent(DenNormalEditorLocalization.ButtonText, DenNormalEditorLocalization.ButtonTooltip),
                    EditorStyles.miniButton, GUILayout.Width(28)))
            {
                DenNormalEditorLocalization.ToggleLanguage();
            }

            EditorGUILayout.EndHorizontal();

            var separator = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(separator, new Color(0f, 0f, 0f, 0.2f));
        }

        private void DrawTargets()
        {
            EditorGUILayout.LabelField(DenNormalEditorLocalization.Tr("inspector.targets_header"), EditorStyles.boldLabel);

            var removeAt = -1;

            for (var i = 0; i < _edits.arraySize; i++)
            {
                var element = _edits.GetArrayElementAtIndex(i);
                var targetProp = element.FindPropertyRelative("target");
                var countProp = element.FindPropertyRelative("count");
                var vertexCountProp = element.FindPropertyRelative("vertexCount");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(targetProp, GUIContent.none);

                GUILayout.Label(DenNormalEditorLocalization.Format("inspector.vertex_count", countProp.intValue), GUILayout.Width(64));

                if (GUILayout.Button("×", GUILayout.Width(22)))
                {
                    removeAt = i;
                }

                EditorGUILayout.EndHorizontal();

                var maskProp = element.FindPropertyRelative("strengthMask");
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(maskProp, new GUIContent(
                    DenNormalEditorLocalization.Tr("inspector.mask"),
                    DenNormalEditorLocalization.Tr("inspector.mask_tooltip")));
                EditorGUI.indentLevel--;

                DrawTargetWarning(targetProp, vertexCountProp, maskProp);
            }

            if (removeAt >= 0)
            {
                serializedObject.ApplyModifiedProperties();

                var component = (DenNormalEditor)target;
                if (removeAt < component.edits.Count)
                {
                    DenNormalEditorUndo.BeginGroup(component, "Remove Dennoko Normal Editor Target");
                    component.edits.RemoveAt(removeAt);
                    DenNormalEditorUndo.Apply(component);
                    DenNormalEditorUndo.EndGroup();
                }

                serializedObject.Update();
            }

            DrawAddTargetSlot();

            if (_edits.arraySize == 0)
            {
                EditorGUILayout.HelpBox(DenNormalEditorLocalization.Tr("inspector.add_target_prompt"), MessageType.Info);
            }
        }

        private void DrawAddTargetSlot()
        {
            var rect = EditorGUILayout.GetControlRect();
            HandleMultiDrop(rect);

            var label = new GUIContent(
                DenNormalEditorLocalization.Tr("inspector.add_target"),
                DenNormalEditorLocalization.Tr("inspector.add_target_tooltip"));

            var picked = EditorGUI.ObjectField(rect, label, null, typeof(Renderer), true) as Renderer;
            if (picked == null) return;

            AddTargets(new List<Renderer> { picked });
        }

        private void HandleMultiDrop(Rect rect)
        {
            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (DragAndDrop.objectReferences.Length <= 1) return;
            if (!rect.Contains(evt.mousePosition)) return;

            var renderers = CollectDroppedRenderers(DragAndDrop.objectReferences);
            if (renderers.Count == 0) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Link;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddTargets(renderers);
            }

            evt.Use();
        }

        private static List<Renderer> CollectDroppedRenderers(Object[] dropped)
        {
            var result = new List<Renderer>();

            foreach (var obj in dropped)
            {
                var renderer = obj as Renderer;
                if (renderer == null && obj is GameObject go)
                {
                    renderer = go.GetComponent<Renderer>();
                }

                if (!IsEditableRenderer(renderer)) continue;
                if (result.Contains(renderer)) continue;

                result.Add(renderer);
            }

            return result;
        }

        private void AddTargets(List<Renderer> renderers)
        {
            serializedObject.ApplyModifiedProperties();

            var component = (DenNormalEditor)target;
            var added = false;

            foreach (var renderer in renderers)
            {
                if (!IsEditableRenderer(renderer)) continue;
                if (component.FindEdit(renderer) != null) continue;

                if (!added)
                {
                    DenNormalEditorUndo.BeginGroup(component, "Add Dennoko Normal Editor Target");
                    added = true;
                }

                component.edits.Add(new NormalEdit { target = renderer });
            }

            if (added)
            {
                DenNormalEditorUndo.Apply(component);
                DenNormalEditorUndo.EndGroup();
            }

            serializedObject.Update();
        }

        private static void DrawTargetWarning(SerializedProperty targetProp, SerializedProperty vertexCountProp,
            SerializedProperty maskProp)
        {
            var renderer = targetProp.objectReferenceValue as Renderer;
            if (renderer == null) return;

            var mesh = NormalDeltaApplier.GetSharedMesh(renderer);
            if (mesh == null)
            {
                EditorGUILayout.HelpBox(DenNormalEditorLocalization.Tr("inspector.warn_no_mesh"), MessageType.Warning);
                return;
            }

            var recorded = vertexCountProp.intValue;
            if (recorded != 0 && recorded != mesh.vertexCount)
            {
                EditorGUILayout.HelpBox(
                    DenNormalEditorLocalization.Format("inspector.warn_vertex_count_mismatch", mesh.vertexCount, recorded),
                    MessageType.Error);
            }

            if (maskProp.objectReferenceValue != null && !StrengthMaskUtil.HasUV0(mesh))
            {
                EditorGUILayout.HelpBox(DenNormalEditorLocalization.Tr("inspector.mask_no_uv"), MessageType.Warning);
            }
        }

        private void DrawEditControls(DenNormalEditor component)
        {
            var editing = EditSession.IsActive(component);

            using (new EditorGUI.DisabledScope(!editing && _edits.arraySize == 0))
            {
                var prevColor = GUI.backgroundColor;
                if (editing)
                {
                    GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                    if (GUILayout.Button(DenNormalEditorLocalization.Tr("inspector.btn_edit_end"), GUILayout.Height(28)))
                    {
                        EditSession.End();
                    }
                }
                else
                {
                    GUI.backgroundColor = new Color(0.35f, 0.75f, 1f);
                    if (GUILayout.Button(DenNormalEditorLocalization.Tr("inspector.btn_edit_start"), GUILayout.Height(28)))
                    {
                        serializedObject.ApplyModifiedProperties();
                        EditSession.Begin(component);
                    }
                }
                GUI.backgroundColor = prevColor;
            }

            if (!editing) return;

            EditorGUILayout.HelpBox(
                DenNormalEditorLocalization.Tr("inspector.edit_help"),
                MessageType.Info);

            var session = EditSession.Active;

            if (session != null && session.AnyVertexCountMismatch)
            {
                EditorGUILayout.HelpBox(
                    DenNormalEditorLocalization.Tr("inspector.warn_ndmf_vertex_mismatch"),
                    MessageType.Warning);
            }
            else if (session != null && session.ShowFallbackWarning)
            {
                EditorGUILayout.HelpBox(
                    DenNormalEditorLocalization.Tr("inspector.warn_ndmf_fallback"),
                    MessageType.Warning);
            }
        }

        private void DrawBrushSettings()
        {
            EditorGUILayout.LabelField(DenNormalEditorLocalization.Tr("inspector.brush_header"), EditorStyles.boldLabel);
            EditorGUILayout.Slider(_brushRadius, EditSession.MinBrushRadius, EditSession.MaxBrushRadius,
                new GUIContent(DenNormalEditorLocalization.Tr("inspector.brush_radius"), DenNormalEditorLocalization.Tr("inspector.brush_radius_tooltip")));
            EditorGUILayout.PropertyField(_falloff, new GUIContent(DenNormalEditorLocalization.Tr("inspector.brush_falloff")));

            EditorGUILayout.Space(2);

            var mirrorActive = _mirror.boolValue;
            var prevColor = GUI.backgroundColor;
            if (mirrorActive)
            {
                GUI.backgroundColor = new Color(0.35f, 0.95f, 0.45f);
            }

            var buttonText = mirrorActive
                ? DenNormalEditorLocalization.Tr("inspector.mirror_on")
                : DenNormalEditorLocalization.Tr("inspector.mirror_off");
            if (GUILayout.Button(buttonText, GUILayout.Height(28)))
            {
                _mirror.boolValue = !mirrorActive;
                serializedObject.ApplyModifiedProperties();
                EditSession.NotifySettingsChanged();
            }

            GUI.backgroundColor = prevColor;

            using (new EditorGUI.DisabledScope(!_mirror.boolValue))
            {
                DrawMirrorAxisButtons();
            }
        }

        private void DrawMirrorAxisButtons()
        {
            EditorGUILayout.LabelField(DenNormalEditorLocalization.Tr("inspector.mirror_axis"));
            EditorGUILayout.BeginHorizontal();

            var currentAxis = (MirrorAxis)_mirrorAxis.enumValueIndex;
            var defaultColor = GUI.backgroundColor;
            var selectedColor = new Color(0.35f, 0.7f, 1f);

            var axes = new[]
            {
                (Axis: MirrorAxis.X, Label: DenNormalEditorLocalization.Tr("inspector.axis_x"), Tooltip: DenNormalEditorLocalization.Tr("inspector.axis_x_tooltip")),
                (Axis: MirrorAxis.Y, Label: DenNormalEditorLocalization.Tr("inspector.axis_y"), Tooltip: DenNormalEditorLocalization.Tr("inspector.axis_y_tooltip")),
                (Axis: MirrorAxis.Z, Label: DenNormalEditorLocalization.Tr("inspector.axis_z"), Tooltip: DenNormalEditorLocalization.Tr("inspector.axis_z_tooltip")),
            };

            foreach (var item in axes)
            {
                var isSelected = currentAxis == item.Axis;
                GUI.backgroundColor = isSelected ? selectedColor : defaultColor;

                if (GUILayout.Button(new GUIContent(item.Label, item.Tooltip), GUILayout.Height(26)))
                {
                    if (!isSelected)
                    {
                        _mirrorAxis.enumValueIndex = (int)item.Axis;
                        serializedObject.ApplyModifiedProperties();
                        EditSession.NotifySettingsChanged();
                    }
                }
            }

            GUI.backgroundColor = defaultColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBakeSection(DenNormalEditor component)
        {
            EditorGUILayout.LabelField(DenNormalEditorLocalization.Tr("inspector.bake_header"), EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                DenNormalEditorLocalization.Tr("inspector.bake_help"),
                MessageType.None);

            var hasEdits = false;
            foreach (var edit in component.edits)
            {
                if (edit == null || edit.target == null || !edit.HasEdits) continue;
                hasEdits = true;
                break;
            }

            using (new EditorGUI.DisabledScope(!hasEdits))
            {
                if (GUILayout.Button(DenNormalEditorLocalization.Tr("inspector.btn_bake"), GUILayout.Height(24)))
                {
                    serializedObject.ApplyModifiedProperties();
                    DenNormalEditorBaker.Bake(component);
                }
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(!hasEdits))
            {
                if (GUILayout.Button(DenNormalEditorLocalization.Tr("inspector.btn_clear_all")))
                {
                    if (EditorUtility.DisplayDialog(
                            DenNormalEditorLocalization.Tr("inspector.dialog_clear_title"),
                            DenNormalEditorLocalization.Tr("inspector.dialog_clear_message"),
                            DenNormalEditorLocalization.Tr("inspector.dialog_clear_ok"),
                            DenNormalEditorLocalization.Tr("inspector.dialog_clear_cancel")))
                    {
                        EditSession.End();

                        DenNormalEditorUndo.BeginGroup(component, "Clear Dennoko Normal Editor Edits");
                        foreach (var edit in component.edits)
                        {
                            edit?.Clear();
                        }

                        DenNormalEditorUndo.Apply(component);
                        DenNormalEditorUndo.EndGroup();

                        LiveEdits.Invalidate();
                        serializedObject.Update();
                    }
                }
            }
        }
    }
}
