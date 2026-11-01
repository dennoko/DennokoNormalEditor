using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dennokoworks.DenNormalEditor.Editor
{
    internal partial class EditSession
    {
        private const float VertexDotReferenceHandleSize = 0.05f;
        private const float NormalPinLengthRatio = 0.35f;

        private static void BeginOccludedVertexCulling(out CompareFunction previous)
        {
            previous = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;
        }

        private void DrawGizmos()
        {
            if (Event.current.type != EventType.Repaint) return;

            var camera = Camera.current;
            if (camera == null) return;

            var cameraPosition = camera.transform.position;

            if (!_hasSelection)
            {
                DrawHoverGizmos(cameraPosition);
                return;
            }

            // ドラッグ操作中はピンの不透明度を下げて lilToon 等のシェーディング境界を見やすくする
            var activelyAdjusting = _dragging || _activeEffectMode != EffectMode.None;
            var alphaMultiplier = activelyAdjusting ? 0.25f : 1f;

            DrawBrushCircle(_centerWorld, new Color(0.3f, 0.8f, 1f, 0.8f * alphaMultiplier));
            if (_mirrorActive)
            {
                DrawBrushCircle(_mirrorCenterWorld, new Color(1f, 0.4f, 0.6f, 0.8f * alphaMultiplier));
            }

            // 選択頂点から 3D ハンドルへのガイドライン
            Handles.color = new Color(1f, 0.9f, 0.2f, 0.9f);
            Handles.DrawLine(_centerWorld, _handlePosition);

            BeginOccludedVertexCulling(out var previousZTest);

            var pinLen = Mathf.Clamp(BrushRadius * NormalPinLengthRatio, 0.003f, 0.04f);
            var step = Mathf.Max(1, _influences.Count / MaxDrawnVertices);

            for (var i = 0; i < _influences.Count; i += step)
            {
                var influence = _influences[i];
                var target = influence.Target;
                var vertices = target.WorldVertices;
                var normals = target.WorldNormals;
                if (vertices == null || normals == null || influence.Index >= vertices.Length ||
                    influence.Index >= normals.Length)
                {
                    continue;
                }

                var strength = Mathf.Clamp01(influence.Weight + influence.MirrorWeight);
                var baseColor = Color.Lerp(
                    new Color(0.2f, 0.65f, 1f, 0.35f),
                    new Color(1f, 0.35f, 0.3f, 0.9f),
                    strength);
                baseColor.a *= alphaMultiplier;

                Handles.color = baseColor;

                var worldPos = vertices[influence.Index];
                var worldNormal = normals[influence.Index];
                var tipPos = worldPos + worldNormal * pinLen;

                Handles.DrawLine(worldPos, tipPos);
                DrawVertexDot(worldPos, cameraPosition, 0.022f, 0.14f);
            }

            Handles.zTest = previousZTest;
        }

        /// <summary>
        /// ホバー中は、カーソル下の頂点を中心とするブラシ半径内の頂点のみ法線ピンをプレビュー表示する。
        /// メッシュ全体をピンで埋め尽くさずに法線の向きを確認できる。
        /// </summary>
        private void DrawHoverGizmos(Vector3 cameraPosition)
        {
            if (_hoverTarget?.WorldVertices == null || _hoverIndex < 0 ||
                _hoverIndex >= _hoverTarget.WorldVertices.Length)
            {
                return;
            }

            var hovered = _hoverTarget.WorldVertices[_hoverIndex];
            DrawBrushCircle(hovered, new Color(1f, 0.8f, 0.2f, 0.65f));

            var radius = Mathf.Max(1e-5f, BrushRadius);
            var radiusSq = radius * radius;
            var inverseRadius = 1f / radius;
            var pinLen = Mathf.Clamp(radius * NormalPinLengthRatio, 0.003f, 0.04f);

            BeginOccludedVertexCulling(out var previousHoverZTest);

            var drawn = 0;
            foreach (var target in _targets)
            {
                var vertices = target.WorldVertices;
                var normals = target.WorldNormals;
                if (vertices == null || normals == null) continue;

                for (var i = 0; i < vertices.Length; i++)
                {
                    var dSq = (vertices[i] - hovered).sqrMagnitude;
                    if (dSq >= radiusSq) continue;

                    // マスクで効かない頂点はピンを出さない（クリック前に効く範囲が分かるようにする）
                    var mask = MaskAt(target, i);
                    if (mask <= 0f) continue;

                    if (++drawn > MaxDrawnVertices) break;

                    var t = Mathf.Sqrt(dSq) * inverseRadius;
                    var w = FalloffUtil.Weight(t, _component.falloff) * mask;
                    Handles.color = new Color(0.3f, 0.85f, 1f, Mathf.Lerp(0.15f, 0.65f, w));

                    var pos = vertices[i];
                    Handles.DrawLine(pos, pos + normals[i] * pinLen);
                }
            }

            Handles.color = new Color(1f, 0.8f, 0.2f, 0.95f);
            DrawVertexDot(hovered, cameraPosition, 0.045f, 0.35f);
            Handles.zTest = previousHoverZTest;
        }

        private void DrawVertexDot(Vector3 world, Vector3 cameraPosition, float screenScale, float maxRadiusRatio)
        {
            var size = GetVertexDotSize(world, screenScale, maxRadiusRatio);

            var toCamera = cameraPosition - world;
            var distance = toCamera.magnitude;
            if (distance > 1e-6f) world += toCamera * (size * VertexDotDepthBias / distance);

            Handles.DotHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
        }

        private static float VertexDotDistanceScale(float handleSize)
        {
            return handleSize <= VertexDotReferenceHandleSize
                ? 1f
                : Mathf.Sqrt(VertexDotReferenceHandleSize / handleSize);
        }

        private float GetVertexDotSize(Vector3 worldPosition, float screenScale, float maxRadiusRatio = 0.25f)
        {
            var handleSize = HandleUtility.GetHandleSize(worldPosition);
            var size = handleSize * screenScale * VertexDotDistanceScale(handleSize);
            var maxByRadius = BrushRadius * maxRadiusRatio;
            return Mathf.Min(size, maxByRadius);
        }

        private void DrawBrushCircle(Vector3 center, Color color)
        {
            var camera = Camera.current;
            if (camera == null) return;

            Handles.color = color;
            Handles.DrawWireDisc(center, camera.transform.forward, BrushRadius);
        }
    }
}

