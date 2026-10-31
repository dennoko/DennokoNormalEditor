using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    internal partial class EditSession
    {
        private ViewKey _screenCacheKey;
        private bool _screenCacheValid;
        private int _geometryGeneration;

        private ViewKey _gridCacheKey;
        private bool _gridCacheValid;

        private bool _fastProjection;
        private Matrix4x4 _viewProjection;
        private float _fastPixelWidth;
        private float _fastPixelHeight;
        private float _fastRectX;
        private float _fastRectY;
        private float _fastPixelsPerPoint;

        private bool TryPick(Vector2 mousePosition, out TargetState picked, out int pickedIndex)
        {
            picked = null;
            pickedIndex = -1;

            var camera = Camera.current;
            if (camera == null) return false;

            var cameraPosition = camera.transform.position;

            UpdateTriangleGrid(camera);
            CollectCandidates(mousePosition);
            if (_candidates.Count == 0) return false;

            CollectNearbyTriangles(mousePosition);

            foreach (var candidate in _candidates)
            {
                if (IsOccluded(candidate.Target, candidate.Index, cameraPosition)) continue;

                picked = candidate.Target;
                pickedIndex = candidate.Index;
                return true;
            }

            return false;
        }

        private void UpdateScreenPositions(Camera camera)
        {
            var key = ViewKey.From(camera, _geometryGeneration);
            if (_screenCacheValid && _screenCacheKey.Matches(key)) return;

            _screenCacheKey = key;
            _screenCacheValid = true;
            _gridCacheValid = false;

            var cameraTransform = camera.transform;
            var cameraPosition = cameraTransform.position;
            var cameraForward = cameraTransform.forward;

            _fastProjection = TryCalibrateFastProjection(camera);

            foreach (var target in _targets)
            {
                var vertices = target.WorldVertices;
                if (vertices == null)
                {
                    target.ScreenPositions = null;
                    target.GridValid = false;
                    continue;
                }

                if (target.ScreenPositions == null || target.ScreenPositions.Length != vertices.Length)
                {
                    target.ScreenPositions = new Vector2[vertices.Length];
                    target.ViewDepths = new float[vertices.Length];
                    target.InFront = new bool[vertices.Length];
                }

                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = vertices[i];
                    var depth = Vector3.Dot(world - cameraPosition, cameraForward);
                    target.ViewDepths[i] = depth;

                    var inFront = depth > 0f;
                    target.InFront[i] = inFront;

                    if (!inFront)
                    {
                        target.ScreenPositions[i] = Vector2.zero;
                        continue;
                    }

                    target.ScreenPositions[i] = _fastProjection && TryProjectFast(world, out var fast)
                        ? fast
                        : HandleUtility.WorldToGUIPoint(world);
                }
            }
        }

        private void UpdateTriangleGrid(Camera camera)
        {
            UpdateScreenPositions(camera);

            var key = ViewKey.From(camera, _geometryGeneration);
            if (_gridCacheValid && _gridCacheKey.Matches(key)) return;

            _gridCacheKey = key;
            _gridCacheValid = true;

            foreach (var target in _targets)
            {
                BuildTriangleGrid(target);
            }
        }

        private bool TryCalibrateFastProjection(Camera camera)
        {
            _fastPixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            if (_fastPixelsPerPoint <= 0f) return false;

            _viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;

            var rect = camera.pixelRect;
            _fastPixelWidth = camera.pixelWidth;
            _fastPixelHeight = camera.pixelHeight;
            _fastRectX = rect.x;
            _fastRectY = rect.y;

            if (_fastPixelWidth <= 0f || _fastPixelHeight <= 0f) return false;

            var t = camera.transform;
            for (var i = 0; i < 2; i++)
            {
                var probe = t.position
                            + t.forward * (i == 0 ? 1.5f : 4.0f)
                            + t.right * (i == 0 ? 0.31f : -0.77f)
                            + t.up * (i == 0 ? -0.19f : 0.53f);

                if (!TryProjectFast(probe, out var fast)) return false;

                var reference = HandleUtility.WorldToGUIPoint(probe);
                if ((fast - reference).sqrMagnitude > 0.0625f) return false;
            }

            return true;
        }

        private bool TryProjectFast(Vector3 world, out Vector2 gui)
        {
            var clip = _viewProjection * new Vector4(world.x, world.y, world.z, 1f);
            if (clip.w <= 1e-6f)
            {
                gui = Vector2.zero;
                return false;
            }

            var inverse = 1f / clip.w;
            var px = (clip.x * inverse * 0.5f + 0.5f) * _fastPixelWidth + _fastRectX;
            var py = (clip.y * inverse * 0.5f + 0.5f) * _fastPixelHeight + _fastRectY;

            gui = new Vector2(px / _fastPixelsPerPoint, (_fastPixelHeight - py) / _fastPixelsPerPoint);
            return true;
        }

        private void CollectCandidates(Vector2 mousePosition)
        {
            _candidates.Clear();

            var threshold = PickThresholdPixels * PickThresholdPixels;

            foreach (var target in _targets)
            {
                var positions = target.ScreenPositions;
                if (positions == null) continue;

                for (var i = 0; i < positions.Length; i++)
                {
                    if (!target.InFront[i]) continue;

                    var distance = (positions[i] - mousePosition).sqrMagnitude;
                    if (distance >= threshold) continue;

                    InsertCandidate(target, i, distance);
                }
            }
        }

        private void InsertCandidate(TargetState target, int index, float distance)
        {
            var insertAt = _candidates.Count;
            for (var i = 0; i < _candidates.Count; i++)
            {
                if (distance >= _candidates[i].ScreenDistanceSq) continue;
                insertAt = i;
                break;
            }

            if (insertAt >= MaxPickCandidates) return;

            _candidates.Insert(insertAt, new Candidate
            {
                Target = target,
                Index = index,
                ScreenDistanceSq = distance,
            });

            if (_candidates.Count > MaxPickCandidates) _candidates.RemoveAt(_candidates.Count - 1);
        }
    }
}

