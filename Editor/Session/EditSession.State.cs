using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenNormalEditor.Editor
{
    internal partial class EditSession
    {
        private sealed class TargetState
        {
            public int TargetIndex;
            public Renderer Original;
            public Renderer Proxy;
            public SkinnedMeshRenderer Skinned;
            public Mesh Mesh;
            public Mesh OriginalMesh;
            public int VertexCount;

            public Mesh BakeScratch;
            public Vector3[] WorldVertices;

            /// <summary>元メッシュ（編集前）のバインドポーズ空間における単位法線。</summary>
            public Vector3[] BaseLocalNormals;

            /// <summary>現在の編集（Working）を反映したワールド空間の単位法線。</summary>
            public Vector3[] WorldNormals;

            /// <summary>ドラッグ開始時点（Snapshot）のワールド空間の単位法線。</summary>
            public Vector3[] SnapshotWorldNormals;

            public NormalEdit Edit;

            public readonly List<Vector3> LocalVertices = new List<Vector3>();
            public readonly List<Vector3> LocalNormalsScratch = new List<Vector3>();
            public readonly List<int> Triangles = new List<int>();
            public bool HasTriangles;

            // スクリーン空間キャッシュ
            public Vector2[] ScreenPositions;
            public float[] ViewDepths;
            public bool[] InFront;

            // 遮蔽三角形グリッド（CSR）
            public int[] BucketStart;
            public int[] BucketItems;
            public int[] BucketCursor;
            public readonly List<int> Overflow = new List<int>();
            public bool GridValid;
            public int[] Stamp;
            public int StampGeneration;

            public Transform[] Bones;
            public readonly List<Matrix4x4> BindPoses = new List<Matrix4x4>();
            public readonly List<BoneWeight> BoneWeights = new List<BoneWeight>();

            public readonly Dictionary<int, Vector3> Working = new Dictionary<int, Vector3>();
            public readonly Dictionary<int, Vector3> Snapshot = new Dictionary<int, Vector3>();
            public bool Touched;
        }

        private struct Influence
        {
            public TargetState Target;
            public int Index;
            public int GroupId;
            public float Weight;
            public float MirrorWeight;
            public Matrix4x4 Skin;
            public Matrix4x4 InverseSkin;
        }

        private struct Candidate
        {
            public TargetState Target;
            public int Index;
            public float ScreenDistanceSq;
        }

        private struct TriangleRef
        {
            public TargetState Target;
            public int Start;
        }

        private struct ViewKey
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float FieldOfView;
            public bool Orthographic;
            public float OrthographicSize;
            public Rect PixelRect;
            public int GeometryGeneration;

            public bool Matches(ViewKey other)
            {
                return GeometryGeneration == other.GeometryGeneration
                       && Orthographic == other.Orthographic
                       && Position == other.Position
                       && Rotation == other.Rotation
                       && FieldOfView == other.FieldOfView
                       && OrthographicSize == other.OrthographicSize
                       && PixelRect == other.PixelRect;
            }

            public static ViewKey From(Camera camera, int geometryGeneration)
            {
                var t = camera.transform;
                return new ViewKey
                {
                    Position = t.position,
                    Rotation = t.rotation,
                    FieldOfView = camera.fieldOfView,
                    Orthographic = camera.orthographic,
                    OrthographicSize = camera.orthographicSize,
                    PixelRect = camera.pixelRect,
                    GeometryGeneration = geometryGeneration,
                };
            }
        }

        private readonly List<TargetState> _targets = new List<TargetState>();
        private readonly List<NormalEdit> _wantedEdits = new List<NormalEdit>();
        private readonly List<Influence> _influences = new List<Influence>();
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly List<TriangleRef> _nearbyTriangles = new List<TriangleRef>();

        /// <summary>複数メッシュ横断の結合グループ・隣接グラフ・スムーズ形状法線。</summary>
        private SmoothNormalCalculator.CrossMeshTopology _crossTopology;

        private void DisposeTargets()
        {
            foreach (var target in _targets)
            {
                if (target.BakeScratch != null) Object.DestroyImmediate(target.BakeScratch);
            }

            _targets.Clear();
            _crossTopology = null;
            _screenCacheValid = false;
            _gridCacheValid = false;
        }

        private void Refresh(bool force)
        {
            if (!force && EditorApplication.timeSinceStartup - _lastRefresh < RefreshIntervalSeconds) return;
            _lastRefresh = EditorApplication.timeSinceStartup;

            if (_component == null) return;

            var geometryChanged = SyncTargetList();
            var anyFallback = false;
            var anyVertexCountMismatch = false;

            for (var tIdx = 0; tIdx < _targets.Count; tIdx++)
            {
                var target = _targets[tIdx];
                target.TargetIndex = tIdx;

                var origMesh = NormalDeltaApplier.GetSharedMesh(target.Original);
                if (target.OriginalMesh != origMesh)
                {
                    target.OriginalMesh = origMesh;
                    ReadBaseLocalNormals(target, origMesh);
                    geometryChanged = true;
                }

                var proxy = ProxyRegistry.ResolveOrOriginal(target.Original, out var usingProxy);
                if (usingProxy && DownstreamGuard.HasVertexCountMismatch(target.Original))
                {
                    proxy = target.Original;
                    usingProxy = false;
                    anyVertexCountMismatch = true;
                }

                if (!usingProxy) anyFallback = true;

                if (target.Proxy != proxy)
                {
                    target.Proxy = proxy;
                    target.Skinned = proxy as SkinnedMeshRenderer;
                }

                var mesh = NormalDeltaApplier.GetSharedMesh(proxy);
                if (mesh != null) target.VertexCount = mesh.vertexCount;

                if (target.Mesh != mesh)
                {
                    target.Mesh = mesh;
                    geometryChanged = true;

                    if (mesh != null)
                    {
                        mesh.GetBindposes(target.BindPoses);
                        mesh.GetBoneWeights(target.BoneWeights);
                        ReadTriangles(mesh, target.Triangles);
                        target.HasTriangles = target.Triangles.Count >= 3;

                        if (target.BaseLocalNormals == null || target.BaseLocalNormals.Length != mesh.vertexCount)
                        {
                            ReadBaseLocalNormals(target, origMesh != null ? origMesh : mesh);
                        }
                    }
                    else
                    {
                        target.BindPoses.Clear();
                        target.BoneWeights.Clear();
                        target.Triangles.Clear();
                        target.HasTriangles = false;
                    }

                    target.GridValid = false;
                    target.Stamp = null;
                }

                target.Bones = target.Skinned != null ? target.Skinned.bones : null;

                if (UpdateWorldVertices(target)) geometryChanged = true;
            }

            if (geometryChanged || _crossTopology == null)
            {
                RebuildCrossMeshTopology();
                foreach (var target in _targets)
                {
                    UpdateTargetWorldNormals(target);
                }

                if (_hasSelection)
                {
                    BuildInfluences();
                    CommitSnapshot();
                    SyncHandleToSelection();
                }

                unchecked
                {
                    _geometryGeneration++;
                }
            }

            AnyFallback = anyFallback;
            AnyVertexCountMismatch = anyVertexCountMismatch;

            if (!anyFallback)
            {
                _fallbackSince = -1;
            }
            else if (_fallbackSince < 0)
            {
                _fallbackSince = EditorApplication.timeSinceStartup;
            }

            _showFallbackWarning = _fallbackSince >= 0
                                   && EditorApplication.timeSinceStartup - _fallbackSince
                                   > FallbackWarningDelaySeconds;
        }

        private static void ReadBaseLocalNormals(TargetState target, Mesh mesh)
        {
            if (mesh == null)
            {
                target.BaseLocalNormals = null;
                return;
            }

            mesh.GetNormals(target.LocalNormalsScratch);
            var count = target.LocalNormalsScratch.Count;
            if (target.BaseLocalNormals == null || target.BaseLocalNormals.Length != count)
            {
                target.BaseLocalNormals = new Vector3[count];
            }

            for (var i = 0; i < count; i++)
            {
                target.BaseLocalNormals[i] = SmoothNormalCalculator.SafeNormalize(
                    target.LocalNormalsScratch[i], Vector3.up);
            }
        }

        private void RebuildCrossMeshTopology()
        {
            var inputs = new SmoothNormalCalculator.MeshInput[_targets.Count];
            for (var i = 0; i < _targets.Count; i++)
            {
                inputs[i] = new SmoothNormalCalculator.MeshInput
                {
                    WorldVertices = _targets[i].WorldVertices,
                    Triangles = _targets[i].Triangles,
                };
            }

            _crossTopology = SmoothNormalCalculator.BuildTopology(inputs);
        }

        private static int ResolveVertexCount(TargetState target)
        {
            if (target.Mesh != null) return target.Mesh.vertexCount;
            if (target.VertexCount > 0) return target.VertexCount;

            var mesh = NormalDeltaApplier.GetSharedMesh(target.Original);
            return mesh != null ? mesh.vertexCount : 0;
        }

        private bool SyncTargetList()
        {
            var wanted = _wantedEdits;
            wanted.Clear();

            foreach (var edit in _component.edits)
            {
                if (edit?.target == null) continue;
                if (NormalDeltaApplier.GetSharedMesh(edit.target) == null) continue;
                wanted.Add(edit);
            }

            if (_targets.Count == wanted.Count)
            {
                var same = true;
                for (var i = 0; i < wanted.Count; i++)
                {
                    if (_targets[i].Original == wanted[i].target) continue;
                    same = false;
                    break;
                }

                if (same)
                {
                    RebindEdits(wanted);
                    return false;
                }
            }

            DisposeTargets();
            ClearSelection();

            for (var i = 0; i < wanted.Count; i++)
            {
                var edit = wanted[i];
                var state = new TargetState
                {
                    TargetIndex = i,
                    Original = edit.target,
                    Edit = edit,
                };

                edit.CopyTo(state.Working);
                CopyDeltas(state.Working, state.Snapshot);
                _targets.Add(state);
            }

            return true;
        }

        private void RebindEdits(List<NormalEdit> wanted)
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                var target = _targets[i];
                var edit = wanted[i];
                if (ReferenceEquals(target.Edit, edit)) continue;

                target.Edit = edit;
                edit.CopyTo(target.Working);
                CopyDeltas(target.Working, target.Snapshot);
                target.Touched = false;
                UpdateTargetWorldNormals(target);
            }
        }

        private static void ReadTriangles(Mesh mesh, List<int> destination)
        {
            destination.Clear();

            var subMeshCount = mesh.subMeshCount;
            for (var s = 0; s < subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;

                mesh.GetTriangles(IndexScratch, s);
                destination.AddRange(IndexScratch);
            }
        }

        private static Matrix4x4 MeshToWorld(TargetState target)
        {
            if (target.Proxy == null) return Matrix4x4.identity;
            var transform = target.Proxy.transform;

            return target.Skinned != null
                ? Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one)
                : transform.localToWorldMatrix;
        }

        private static bool UpdateWorldVertices(TargetState target)
        {
            if (target.Proxy == null || target.Mesh == null) return false;

            var localToWorld = MeshToWorld(target);
            var source = target.LocalVertices;

            if (target.Skinned != null)
            {
                if (target.BakeScratch == null)
                {
                    target.BakeScratch = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                }

                target.Skinned.BakeMesh(target.BakeScratch);
                target.BakeScratch.GetVertices(source);
            }
            else
            {
                target.Mesh.GetVertices(source);
            }

            var count = source.Count;
            var changed = target.WorldVertices == null || target.WorldVertices.Length != count;
            if (changed) target.WorldVertices = new Vector3[count];

            var world = target.WorldVertices;
            for (var i = 0; i < count; i++)
            {
                var position = localToWorld.MultiplyPoint3x4(source[i]);
                if (!changed && position != world[i]) changed = true;
                world[i] = position;
            }

            return changed;
        }

        /// <summary>
        /// 基準ローカル法線と現在の Working デルタ、およびスキニング行列から各頂点のワールド単位法線を更新する。
        /// </summary>
        private static void UpdateTargetWorldNormals(TargetState target)
        {
            var baseNormals = target.BaseLocalNormals;
            if (baseNormals == null) return;

            var count = baseNormals.Length;
            if (target.WorldNormals == null || target.WorldNormals.Length != count)
            {
                target.WorldNormals = new Vector3[count];
            }

            if (target.SnapshotWorldNormals == null || target.SnapshotWorldNormals.Length != count)
            {
                target.SnapshotWorldNormals = new Vector3[count];
            }

            for (var i = 0; i < count; i++)
            {
                target.Working.TryGetValue(i, out var delta);
                var localNormal = NormalDeltaApplier.EvaluateNormal(baseNormals[i], delta);
                var skin = SkinMatrix(target, i);
                target.WorldNormals[i] = SmoothNormalCalculator.SafeNormalize(
                    skin.MultiplyVector(localNormal), Vector3.up);
            }
        }
    }
}

