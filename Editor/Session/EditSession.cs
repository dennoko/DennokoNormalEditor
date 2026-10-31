using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// シーンビューでのカスタム法線編集セッション。
    ///
    /// 頂点をクリックして影響範囲（全登録 Renderer 対象）を選択し、
    /// 選択頂点上の常時 3D ハンドルで法線を傾けるか、オーバーレイのスライダーで
    /// 「なじませる」「形状を戻す」「揃える」「球面化」「元に戻す」をリアルタイム適用する。
    /// </summary>
    internal partial class EditSession
    {
        private const float PickThresholdPixels = 24f;
        private const float RefreshIntervalSeconds = 0.1f;
        private const int MaxDrawnVertices = 1500;
        private const float VertexDotDepthBias = 2f;
        private const int MaxPickCandidates = 8;

        internal const float MinBrushRadius = 0.001f;
        internal const float MaxBrushRadius = 0.5f;
        private const float RadiusScrollStep = 1.05f;
        private const double FallbackWarningDelaySeconds = 1.0;

        /// <summary>3Dハンドルの長さを決めるスクリーンハンドルサイズ倍率。</summary>
        private const float NormalHandleLengthScale = 0.85f;

        private static EditSession _active;
        private static bool _toolsHiddenBefore;
        private static readonly List<int> IndexScratch = new List<int>();

        internal static readonly PublishedValue<DenNormalEditor> ActiveComponent =
            new PublishedValue<DenNormalEditor>(null, "DenNormalEditor.ActiveComponent");

        internal static EditSession Active => _active;

        internal static bool IsActive(DenNormalEditor component)
        {
            return _active != null && _active._component == component;
        }

        private static bool CanContinueEditing(EditSession session)
        {
            if (session == null) return false;
            if (session._component == null) return false;

            var go = session._component.gameObject;
            if (go == null) return false;

            var scene = go.scene;
            if (!scene.IsValid() || !scene.isLoaded) return false;

            if (!Selection.Contains(go)) return false;

            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (EditorApplication.isCompiling) return false;

            return true;
        }

        [InitializeOnLoadMethod]
        private static void InstallLifecycleHooks()
        {
            AssemblyReloadEvents.beforeAssemblyReload += End;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            EditorApplication.quitting += End;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            End();
        }

        private static void OnSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            End();
        }

        private static void OnUndoRedoPerformed()
        {
            if (_active == null) return;
            _active._resyncPending = true;
        }

        internal static void Begin(DenNormalEditor component)
        {
            End();
            if (component == null) return;

            if (component.gameObject != null)
            {
                Selection.activeGameObject = component.gameObject;
            }

            var session = new EditSession(component);
            _active = session;
            _toolsHiddenBefore = Tools.hidden;

            try
            {
                SceneView.duringSceneGui += session.OnSceneGui;
                EditorApplication.update += session.OnEditorUpdate;
                Tools.hidden = true;
                ActiveComponent.Value = component;
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
            catch
            {
                try { End(); }
                catch (System.Exception ex) { Debug.LogException(ex); }
                throw;
            }
        }

        internal static void End()
        {
            if (_active == null) return;

            var session = _active;
            _active = null;

            SceneView.duringSceneGui -= session.OnSceneGui;
            EditorApplication.update -= session.OnEditorUpdate;

            try
            {
                session.Cleanup();
            }
            finally
            {
                try { Tools.hidden = _toolsHiddenBefore; }
                finally
                {
                    try { ActiveComponent.Value = null; }
                    finally { UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); }
                }
            }
        }

        internal static void NotifySettingsChanged()
        {
            if (_active == null) return;

            if (_active._hasSelection)
            {
                _active.RecomputeMirrorCenter();
                _active.BuildInfluences();
            }

            SceneView.RepaintAll();
        }

        private readonly DenNormalEditor _component;
        private double _lastRefresh;
        private bool _resyncPending;

        private bool _hasSelection;
        private TargetState _selectedTarget;
        private int _selectedIndex = -1;
        private Vector3 _centerWorld;
        private Vector3 _mirrorCenterWorld;
        private bool _mirrorActive;

        /// <summary>選択頂点の法線先端に置く 3D ハンドルのワールド座標。</summary>
        private Vector3 _handlePosition;
        private Vector3 _handleRestPosition;
        private float _handleRestLength = 0.05f;
        private bool _dragging;
        private bool _handleGrabbed;

        private float _pendingRadius;
        private bool _hasPendingRadius;

        private TargetState _hoverTarget;
        private int _hoverIndex = -1;

        private double _lastRepaint;
        private double _fallbackSince = -1;
        private bool _showFallbackWarning;
        private bool _overlayShowsWarning;
        private int _settingsUndoGroup = -1;

        internal bool AnyFallback { get; private set; }
        internal bool AnyVertexCountMismatch { get; private set; }
        internal bool ShowFallbackWarning => _showFallbackWarning;

        private float BrushRadius => _hasPendingRadius ? _pendingRadius : _component.brushRadius;

        private EditSession(DenNormalEditor component)
        {
            _component = component;
            Refresh(true);
        }

        private void Cleanup()
        {
            LiveEdits.Clear();

            DisposeTargets();
            _influences.Clear();
            _candidates.Clear();
            _nearbyTriangles.Clear();

            ProxyRegistry.Prune();
        }

        internal void ResyncFromComponent()
        {
            if (_component == null)
            {
                End();
                return;
            }

            ClearLiveEdits();

            _dragging = false;
            _handleGrabbed = false;
            _hasPendingRadius = false;
            _settingsUndoGroup = -1;
            ResetEffectSliders();

            var hadSelection = _hasSelection;
            var selectedRenderer = _selectedTarget?.Original;
            var selectedIndex = _selectedIndex;

            Refresh(true);

            foreach (var target in _targets)
            {
                if (target.Edit != null) target.Edit.CopyTo(target.Working);
                else target.Working.Clear();

                CopyDeltas(target.Working, target.Snapshot);
                target.Touched = false;
                UpdateTargetWorldNormals(target);
            }

            if (hadSelection && !_hasSelection)
            {
                RestoreSelection(selectedRenderer, selectedIndex);
            }

            if (_hasSelection)
            {
                RecomputeMirrorCenter();
                BuildInfluences();
                CommitSnapshot();
                SyncHandleToSelection();
            }

            SceneView.RepaintAll();
        }

        private void OnEditorUpdate()
        {
            if (_active != this) return;

            if (!CanContinueEditing(this))
            {
                End();
                return;
            }

            if (_resyncPending)
            {
                _resyncPending = false;
                ResyncFromComponent();
            }

            if (_dragging || _activeEffectMode != EffectMode.None) return;

            if (EditorApplication.timeSinceStartup - _lastRepaint < RefreshIntervalSeconds) return;
            _lastRepaint = EditorApplication.timeSinceStartup;

            SceneView.RepaintAll();
        }

        private void OnSceneGui(SceneView sceneView)
        {
            if (_active != this) return;

            if (!CanContinueEditing(this))
            {
                End();
                return;
            }

            var current = Event.current;

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                if (_hasSelection)
                {
                    ClearSelection();
                    sceneView.Repaint();
                }
                else
                {
                    End();
                }
                current.Use();
                return;
            }

            if (!_dragging && _activeEffectMode == EffectMode.None) Refresh(false);

            var defaultControl = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(defaultControl);

            DrawOverlay(sceneView);
            HandleSelection(current, defaultControl);
            HandleRadiusScroll(current);
            HandleDrag(current);
            DrawGizmos();

            if (current.type == EventType.MouseMove || current.type == EventType.MouseDrag)
            {
                sceneView.Repaint();
            }
        }

        private void HandleSelection(Event current, int defaultControl)
        {
            if (_overlayRect.Contains(current.mousePosition)) return;

            if (current.type == EventType.MouseMove && !_dragging && _activeEffectMode == EffectMode.None)
            {
                TryPick(current.mousePosition, out _hoverTarget, out _hoverIndex);
            }

            if (current.type != EventType.MouseDown || current.button != 0 || current.alt) return;
            if (HandleUtility.nearestControl != defaultControl) return;

            if (TryPick(current.mousePosition, out var target, out var index))
            {
                BeginSelection(target, index);
                current.Use();
            }
            else
            {
                ClearSelection();
            }
        }

        private void BeginSelection(TargetState target, int index)
        {
            _hasSelection = true;
            _selectedTarget = target;
            _selectedIndex = index;
            _centerWorld = target.WorldVertices[index];
            ResetEffectSliders();

            RecomputeMirrorCenter();
            BuildInfluences();
            CommitSnapshot();
            SyncHandleToSelection();
        }

        private static float GetNormalHandleLength(Vector3 worldPos)
        {
            var handleSize = HandleUtility.GetHandleSize(worldPos);
            return Mathf.Clamp(handleSize * NormalHandleLengthScale, 0.01f, 0.25f);
        }

        private void SyncHandleToSelection()
        {
            if (!_hasSelection || _selectedTarget == null) return;
            if (_selectedTarget.WorldVertices == null || _selectedIndex < 0 ||
                _selectedIndex >= _selectedTarget.WorldVertices.Length)
            {
                return;
            }

            _centerWorld = _selectedTarget.WorldVertices[_selectedIndex];
            var worldNormal = _selectedTarget.WorldNormals != null && _selectedIndex < _selectedTarget.WorldNormals.Length
                ? _selectedTarget.WorldNormals[_selectedIndex]
                : Vector3.up;

            _handleRestLength = GetNormalHandleLength(_centerWorld);
            _handleRestPosition = _centerWorld + worldNormal * _handleRestLength;
            _handlePosition = _handleRestPosition;
        }

        private void RestoreSelection(Renderer original, int index)
        {
            if (original == null || index < 0) return;

            foreach (var target in _targets)
            {
                if (target.Original != original) continue;
                if (target.WorldVertices == null || index >= target.WorldVertices.Length) return;

                _hasSelection = true;
                _selectedTarget = target;
                _selectedIndex = index;
                return;
            }
        }

        private void ClearSelection()
        {
            _dragging = false;
            _handleGrabbed = false;
            _hasPendingRadius = false;
            ResetEffectSliders();

            _hasSelection = false;
            _mirrorActive = false;
            _selectedTarget = null;
            _selectedIndex = -1;
            _influences.Clear();
            _hoverTarget = null;
            _hoverIndex = -1;
        }

        private void HandleRadiusScroll(Event current)
        {
            if (!_hasSelection) return;
            if (!_dragging && !_handleGrabbed) return;
            if (current.type != EventType.ScrollWheel) return;

            var notches = Mathf.Clamp(current.delta.y, -10f, 10f);
            _pendingRadius = Mathf.Clamp(
                BrushRadius * Mathf.Pow(RadiusScrollStep, notches),
                MinBrushRadius, MaxBrushRadius);
            _hasPendingRadius = true;

            RecomputeMirrorCenter();
            BuildInfluences();
            if (_dragging) ApplyTilt();

            current.Use();
        }

        private void HandleDrag(Event current)
        {
            if (!_hasSelection || _activeEffectMode != EffectMode.None) return;

            if (!_dragging && !_handleGrabbed)
            {
                // カメラのズームに合わせて休止時のハンドル長を更新する
                SyncHandleToSelection();
            }

            EditorGUI.BeginChangeCheck();
            var hotBefore = GUIUtility.hotControl;

            var moved = Handles.PositionHandle(_handlePosition, Tools.handleRotation);
            if (EditorGUI.EndChangeCheck())
            {
                _handlePosition = moved;
                _dragging = true;
                ApplyTilt();
            }

            if (hotBefore == 0 && GUIUtility.hotControl != 0) _handleGrabbed = true;

            if (!_dragging && !_handleGrabbed) return;
            if (GUIUtility.hotControl != 0 && current.rawType != EventType.MouseUp) return;

            _handleGrabbed = false;

            if (!_dragging && !_hasPendingRadius) return;

            _dragging = false;
            Commit("Dennoko Normal Editor Tilt");
            CommitSnapshot();
            SyncHandleToSelection();
        }

        private void Commit(string undoName)
        {
            if (_component == null) return;

            var hasChanges = _hasPendingRadius;
            foreach (var target in _targets)
            {
                if (!target.Touched || ResolveVertexCount(target) <= 0) continue;
                hasChanges = true;
                break;
            }

            if (!hasChanges)
            {
                ClearLiveEdits();
                return;
            }

            DenNormalEditorUndo.BeginGroup(_component, undoName);

            if (_hasPendingRadius)
            {
                _component.brushRadius = _pendingRadius;
                _hasPendingRadius = false;
            }

            foreach (var target in _targets)
            {
                if (!target.Touched) continue;

                var vertexCount = ResolveVertexCount(target);
                if (vertexCount <= 0) continue;

                target.Edit.SetFrom(target.Working, vertexCount);
            }

            DenNormalEditorUndo.Apply(_component);
            DenNormalEditorUndo.EndGroup();

            ClearLiveEdits();
        }

        private static void ClearLiveEdits()
        {
            if (!LiveEdits.Clear()) LiveEdits.Invalidate();
        }
    }
}

