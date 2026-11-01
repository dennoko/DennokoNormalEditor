using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    internal partial class EditSession
    {
        internal enum EffectMode
        {
            None,
            Smooth,
            RestoreShape,
            Flatten,
            Spherize,
            Erase,
        }

        private Vector3 _selectedSnapshotNormal = Vector3.up;
        private Vector3 _sphereAutoCenterWorld;
        private readonly List<Vector3[]> _snapshotNormalsList = new List<Vector3[]>();

        private Transform MirrorRoot
        {
            get
            {
                var avatarRoot = nadena.dev.ndmf.runtime.RuntimeUtil.FindAvatarInParents(_component.transform);
                return avatarRoot != null ? avatarRoot : _component.transform;
            }
        }

        private static Vector3 Reflect(Vector3 v, MirrorAxis axis)
        {
            switch (axis)
            {
                case MirrorAxis.Y: return new Vector3(v.x, -v.y, v.z);
                case MirrorAxis.Z: return new Vector3(v.x, v.y, -v.z);
                default: return new Vector3(-v.x, v.y, v.z);
            }
        }

        private static float AxisComponent(Vector3 v, MirrorAxis axis)
        {
            switch (axis)
            {
                case MirrorAxis.Y: return v.y;
                case MirrorAxis.Z: return v.z;
                default: return v.x;
            }
        }

        private Vector3 MirrorDirection(Vector3 worldDir)
        {
            var root = MirrorRoot;
            var inRoot = root.worldToLocalMatrix.MultiplyVector(worldDir);
            var reflected = Reflect(inRoot, _component.mirrorAxis);
            return SmoothNormalCalculator.SafeNormalize(
                root.localToWorldMatrix.MultiplyVector(reflected), worldDir);
        }

        private static Matrix4x4 SkinMatrix(TargetState target, int index)
        {
            if (target.Proxy == null) return Matrix4x4.identity;

            var fallback = MeshToWorld(target);

            if (target.Skinned == null || target.Bones == null || target.BindPoses.Count == 0 ||
                index >= target.BoneWeights.Count)
            {
                return fallback;
            }

            var bw = target.BoneWeights[index];
            var accumulated = new Matrix4x4();
            var total = 0f;

            total += Accumulate(ref accumulated, target, bw.boneIndex0, bw.weight0);
            total += Accumulate(ref accumulated, target, bw.boneIndex1, bw.weight1);
            total += Accumulate(ref accumulated, target, bw.boneIndex2, bw.weight2);
            total += Accumulate(ref accumulated, target, bw.boneIndex3, bw.weight3);

            if (total <= 1e-6f) return fallback;

            if (!Mathf.Approximately(total, 1f))
            {
                var scale = 1f / total;
                for (var i = 0; i < 16; i++) accumulated[i] *= scale;
            }

            return accumulated;
        }

        private static float Accumulate(ref Matrix4x4 accumulated, TargetState target, int boneIndex, float weight)
        {
            if (weight <= 0f) return 0f;
            if (boneIndex < 0 || boneIndex >= target.Bones.Length || boneIndex >= target.BindPoses.Count) return 0f;

            var bone = target.Bones[boneIndex];
            if (bone == null) return 0f;

            var m = bone.localToWorldMatrix * target.BindPoses[boneIndex];
            for (var i = 0; i < 16; i++) accumulated[i] += m[i] * weight;

            return weight;
        }

        private static Matrix4x4 SafeInverse(Matrix4x4 m)
        {
            return Mathf.Abs(m.determinant) < 1e-12f ? Matrix4x4.identity : m.inverse;
        }

        private void RecomputeMirrorCenter()
        {
            _mirrorActive = false;
            if (!_component.mirror) return;

            var root = MirrorRoot;
            var centerInRoot = root.worldToLocalMatrix.MultiplyPoint3x4(_centerWorld);

            var epsilon = Mathf.Max(1e-4f, BrushRadius * 0.02f);
            if (Mathf.Abs(AxisComponent(centerInRoot, _component.mirrorAxis)) < epsilon) return;

            _mirrorActive = true;
            _mirrorCenterWorld = root.localToWorldMatrix.MultiplyPoint3x4(
                Reflect(centerInRoot, _component.mirrorAxis));
        }

        private void BuildInfluences()
        {
            _influences.Clear();
            RebuildMaskIfNeeded();

            var radius = Mathf.Max(1e-5f, BrushRadius);
            var radiusSq = radius * radius;
            var inverseRadius = 1f / radius;
            var falloff = _component.falloff;
            var hasTopology = _crossTopology != null && _crossTopology.VertexToGroup != null;

            for (var tIdx = 0; tIdx < _targets.Count; tIdx++)
            {
                var target = _targets[tIdx];
                var vertices = target.WorldVertices;
                if (vertices == null) continue;

                var groupMap = hasTopology && tIdx < _crossTopology.VertexToGroup.Length
                    ? _crossTopology.VertexToGroup[tIdx]
                    : null;

                for (var i = 0; i < vertices.Length; i++)
                {
                    var groupId = groupMap != null && i < groupMap.Length ? groupMap[i] : 0;

                    // 同一結合グループ（首の継ぎ目の別メッシュ頂点など）は代表座標で距離を測ることで
                    // 完全に同じブラシ重みを受け取るようにする
                    var world = hasTopology && groupId < _crossTopology.GroupPositions.Length
                        ? _crossTopology.GroupPositions[groupId]
                        : vertices[i];

                    var weight = 0f;
                    var distanceSq = (world - _centerWorld).sqrMagnitude;
                    if (distanceSq < radiusSq)
                    {
                        weight = FalloffUtil.Weight(Mathf.Sqrt(distanceSq) * inverseRadius, falloff);
                    }

                    var mirrorWeight = 0f;
                    if (_mirrorActive)
                    {
                        var mirrorDistanceSq = (world - _mirrorCenterWorld).sqrMagnitude;
                        if (mirrorDistanceSq < radiusSq)
                        {
                            mirrorWeight = FalloffUtil.Weight(Mathf.Sqrt(mirrorDistanceSq) * inverseRadius, falloff);
                        }
                    }

                    if (weight <= 0f && mirrorWeight <= 0f) continue;

                    // 強度マスク。Tilt・全エフェクトはこの重みだけを読むので、ここで掛ければ全操作に効く。
                    // 鏡像側もその頂点自身の UV のマスクを使う
                    var mask = MaskAt(target, i);
                    if (mask <= 0f) continue;
                    weight *= mask;
                    mirrorWeight *= mask;

                    var skin = SkinMatrix(target, i);
                    _influences.Add(new Influence
                    {
                        Target = target,
                        Index = i,
                        GroupId = groupId,
                        Weight = weight,
                        MirrorWeight = mirrorWeight,
                        Skin = skin,
                        InverseSkin = SafeInverse(skin),
                    });
                }
            }
        }

        /// <summary>
        /// 現在の編集状態を次のドラッグ（3Dハンドルまたはスライダー）の基準スナップショットとして確定する。
        /// </summary>
        private void CommitSnapshot()
        {
            _snapshotNormalsList.Clear();

            foreach (var target in _targets)
            {
                CopyDeltas(target.Working, target.Snapshot);
                target.Touched = false;
                UpdateTargetWorldNormals(target);

                if (target.WorldNormals != null && target.SnapshotWorldNormals != null)
                {
                    System.Array.Copy(target.WorldNormals, target.SnapshotWorldNormals, target.WorldNormals.Length);
                }

                _snapshotNormalsList.Add(target.SnapshotWorldNormals);
            }

            if (_hasSelection && _selectedTarget?.SnapshotWorldNormals != null &&
                _selectedIndex >= 0 && _selectedIndex < _selectedTarget.SnapshotWorldNormals.Length)
            {
                _selectedSnapshotNormal = _selectedTarget.SnapshotWorldNormals[_selectedIndex];
            }
            else
            {
                _selectedSnapshotNormal = Vector3.up;
            }

            // なじませる（Smooth）用の目標ワールド法線を複数メッシュ横断で事前計算しておく
            if (_crossTopology != null && _crossTopology.GroupCount > 0)
            {
                SmoothNormalCalculator.ComputeSmoothedGroupNormals(
                    _crossTopology,
                    _snapshotNormalsList,
                    _crossTopology.SmoothedBufferA);
            }

            // 球面化の自動中心（中心頂点から基準法線の逆方向へ半径分奥まった点）
            _sphereAutoCenterWorld = _centerWorld - _selectedSnapshotNormal * Mathf.Max(BrushRadius, 0.05f);
        }

        /// <summary>
        /// 3D ハンドルの移動から、選択範囲内の法線を傾ける。
        /// </summary>
        private void ApplyTilt()
        {
            var draggedVector = _handlePosition - _centerWorld;
            var targetNormal = SmoothNormalCalculator.SafeNormalize(draggedVector, _selectedSnapshotNormal);

            var mirrorFrom = _mirrorActive ? MirrorDirection(_selectedSnapshotNormal) : Vector3.up;
            var mirrorTo = _mirrorActive ? MirrorDirection(targetNormal) : Vector3.up;

            ResetTouchedTargetsToSnapshot();

            foreach (var influence in _influences)
            {
                var target = influence.Target;
                EnsureTargetTouched(target);

                var idx = influence.Index;
                var snapNormal = target.SnapshotWorldNormals[idx];
                var currentWorld = snapNormal;

                if (influence.Weight > 0f)
                {
                    var tilted = SmoothNormalCalculator.RotateVector(_selectedSnapshotNormal, targetNormal, currentWorld);
                    currentWorld = SmoothNormalCalculator.SlerpNormal(currentWorld, tilted, influence.Weight);
                }

                if (_mirrorActive && influence.MirrorWeight > 0f)
                {
                    var mirrorTilted = SmoothNormalCalculator.RotateVector(mirrorFrom, mirrorTo, currentWorld);
                    currentWorld = SmoothNormalCalculator.SlerpNormal(currentWorld, mirrorTilted, influence.MirrorWeight);
                }

                WriteWorldNormalToWorking(target, influence, currentWorld);
            }

            PublishTouchedTargets();
        }

        /// <summary>
        /// オーバーレイのスライダー操作によるエフェクト（なじませる・形状を戻す・揃える・球面化・元に戻す）を適用する。
        /// </summary>
        private void ApplyEffect(EffectMode mode, float strength)
        {
            strength = Mathf.Clamp01(strength);
            ResetTouchedTargetsToSnapshot();

            if (mode == EffectMode.None || strength <= 0f || _influences.Count == 0)
            {
                PublishTouchedTargets();
                return;
            }

            var hasTopology = _crossTopology != null && _crossTopology.GroupCount > 0;
            var mirrorFlattenDir = _mirrorActive ? MirrorDirection(_selectedSnapshotNormal) : _selectedSnapshotNormal;
            var sphereCenter = _component.spherizeCenter != null
                ? _component.spherizeCenter.position
                : _sphereAutoCenterWorld;
            var mirrorSphereCenter = _mirrorActive
                ? MirrorRoot.localToWorldMatrix.MultiplyPoint3x4(
                    Reflect(MirrorRoot.worldToLocalMatrix.MultiplyPoint3x4(sphereCenter), _component.mirrorAxis))
                : sphereCenter;

            foreach (var influence in _influences)
            {
                var target = influence.Target;
                EnsureTargetTouched(target);

                var idx = influence.Index;
                var combinedWeight = Mathf.Clamp01(influence.Weight + influence.MirrorWeight) * strength;
                if (combinedWeight <= 0f) continue;

                if (mode == EffectMode.Erase)
                {
                    target.Snapshot.TryGetValue(idx, out var baseDelta);
                    var remaining = baseDelta * (1f - combinedWeight);
                    target.Working[idx] = remaining;

                    if (target.BaseLocalNormals != null && idx < target.BaseLocalNormals.Length)
                    {
                        var localN = NormalDeltaApplier.EvaluateNormal(target.BaseLocalNormals[idx], remaining);
                        target.WorldNormals[idx] = SmoothNormalCalculator.SafeNormalize(
                            influence.Skin.MultiplyVector(localN), Vector3.up);
                    }
                    continue;
                }

                var snapNormal = target.SnapshotWorldNormals[idx];
                var newWorldNormal = snapNormal;

                switch (mode)
                {
                    case EffectMode.Smooth:
                    {
                        if (!hasTopology || influence.GroupId >= _crossTopology.SmoothedBufferA.Length) break;
                        var goal = _crossTopology.SmoothedBufferA[influence.GroupId];
                        newWorldNormal = SmoothNormalCalculator.SlerpNormal(snapNormal, goal, combinedWeight);
                        break;
                    }

                    case EffectMode.RestoreShape:
                    {
                        if (!hasTopology || influence.GroupId >= _crossTopology.SmoothGeometricNormals.Length) break;
                        var goal = _crossTopology.SmoothGeometricNormals[influence.GroupId];
                        newWorldNormal = SmoothNormalCalculator.SlerpNormal(snapNormal, goal, combinedWeight);
                        break;
                    }

                    case EffectMode.Flatten:
                    {
                        if (influence.Weight > 0f)
                        {
                            newWorldNormal = SmoothNormalCalculator.SlerpNormal(
                                newWorldNormal, _selectedSnapshotNormal, influence.Weight * strength);
                        }

                        if (_mirrorActive && influence.MirrorWeight > 0f)
                        {
                            newWorldNormal = SmoothNormalCalculator.SlerpNormal(
                                newWorldNormal, mirrorFlattenDir, influence.MirrorWeight * strength);
                        }
                        break;
                    }

                    case EffectMode.Spherize:
                    {
                        var vertexWorld = hasTopology && influence.GroupId < _crossTopology.GroupPositions.Length
                            ? _crossTopology.GroupPositions[influence.GroupId]
                            : target.WorldVertices[idx];

                        if (influence.Weight > 0f)
                        {
                            newWorldNormal = NormalTransferUtil.ComputeSpherizedNormal(
                                vertexWorld, sphereCenter, newWorldNormal, influence.Weight * strength);
                        }

                        if (_mirrorActive && influence.MirrorWeight > 0f)
                        {
                            newWorldNormal = NormalTransferUtil.ComputeSpherizedNormal(
                                vertexWorld, mirrorSphereCenter, newWorldNormal, influence.MirrorWeight * strength);
                        }
                        break;
                    }
                }

                WriteWorldNormalToWorking(target, influence, newWorldNormal);
            }

            PublishTouchedTargets();
        }

        private static void WriteWorldNormalToWorking(TargetState target, Influence influence, Vector3 worldNormal)
        {
            var idx = influence.Index;
            target.WorldNormals[idx] = worldNormal;

            if (target.BaseLocalNormals == null || idx >= target.BaseLocalNormals.Length) return;

            var baseLocal = target.BaseLocalNormals[idx];
            var newLocal = SmoothNormalCalculator.SafeNormalize(
                influence.InverseSkin.MultiplyVector(worldNormal), baseLocal);
            target.Working[idx] = newLocal - baseLocal;
        }

        private void ResetTouchedTargetsToSnapshot()
        {
            foreach (var target in _targets)
            {
                if (!target.Touched) continue;
                CopyDeltas(target.Snapshot, target.Working);
                if (target.SnapshotWorldNormals != null && target.WorldNormals != null)
                {
                    System.Array.Copy(target.SnapshotWorldNormals, target.WorldNormals, target.WorldNormals.Length);
                }
            }
        }

        private static void EnsureTargetTouched(TargetState target)
        {
            if (target.Touched) return;
            CopyDeltas(target.Snapshot, target.Working);
            target.Touched = true;
        }

        private void PublishTouchedTargets()
        {
            foreach (var target in _targets)
            {
                if (!target.Touched) continue;
                LiveEdits.Publish(target.Edit, target.Working);
            }

            UnityEditor.SceneView.RepaintAll();
        }

        private static void CopyDeltas(Dictionary<int, Vector3> source, Dictionary<int, Vector3> destination)
        {
            destination.Clear();
            foreach (var pair in source)
            {
                destination.Add(pair.Key, pair.Value);
            }
        }
    }
}
