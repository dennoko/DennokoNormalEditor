using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 球面法線化および別メッシュ（球体プロキシ等）からの最近傍法線転写を行うユーティリティ。
    /// </summary>
    internal static class NormalTransferUtil
    {
        /// <summary>
        /// 頂点のワールド座標と球中心ワールド座標から、放射状の球面法線を求めて現在法線と補間する。
        /// </summary>
        internal static Vector3 ComputeSpherizedNormal(
            Vector3 vertexWorld,
            Vector3 sphereCenterWorld,
            Vector3 currentNormalWorld,
            float weight)
        {
            var radial = vertexWorld - sphereCenterWorld;
            var radialNormal = SmoothNormalCalculator.SafeNormalize(radial, currentNormalWorld);
            return SmoothNormalCalculator.SlerpNormal(currentNormalWorld, radialNormal, weight);
        }

        /// <summary>
        /// 転写元メッシュのワールド頂点・ワールド法線から、対象頂点座標に最も近い表面法線を取得して補間する。
        /// </summary>
        internal static void TransferClosestNormals(
            Vector3[] sourceWorldVertices,
            Vector3[] sourceWorldNormals,
            Vector3[] targetWorldVertices,
            Vector3[] targetCurrentWorldNormals,
            float[] targetWeights,
            Vector3[] outTargetWorldNormals)
        {
            if (sourceWorldVertices == null || sourceWorldNormals == null ||
                targetWorldVertices == null || outTargetWorldNormals == null)
            {
                return;
            }

            var srcCount = Mathf.Min(sourceWorldVertices.Length, sourceWorldNormals.Length);
            var dstCount = Mathf.Min(targetWorldVertices.Length, outTargetWorldNormals.Length);
            if (srcCount == 0 || dstCount == 0) return;

            for (var i = 0; i < dstCount; i++)
            {
                var current = targetCurrentWorldNormals != null && i < targetCurrentWorldNormals.Length
                    ? targetCurrentWorldNormals[i]
                    : Vector3.up;

                var w = targetWeights != null && i < targetWeights.Length
                    ? Mathf.Clamp01(targetWeights[i])
                    : 1f;

                if (w <= 0f)
                {
                    outTargetWorldNormals[i] = current;
                    continue;
                }

                var pos = targetWorldVertices[i];
                var bestIdx = 0;
                var bestDistSq = float.MaxValue;

                for (var s = 0; s < srcCount; s++)
                {
                    var dSq = (sourceWorldVertices[s] - pos).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestIdx = s;
                    }
                }

                var transferred = SmoothNormalCalculator.SafeNormalize(sourceWorldNormals[bestIdx], current);
                outTargetWorldNormals[i] = SmoothNormalCalculator.SlerpNormal(current, transferred, w);
            }
        }

        /// <summary>
        /// Renderer からワールド空間の頂点座標と単位法線配列を取得する。
        /// </summary>
        internal static bool ExtractWorldVerticesAndNormals(
            Renderer renderer,
            out Vector3[] worldVertices,
            out Vector3[] worldNormals)
        {
            worldVertices = null;
            worldNormals = null;

            var mesh = NormalDeltaApplier.GetSharedMesh(renderer);
            if (mesh == null) return false;

            var t = renderer.transform;
            if (renderer is SkinnedMeshRenderer smr)
            {
                var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    smr.BakeMesh(baked);
                    var localV = baked.vertices;
                    var localN = baked.normals;
                    if (localN == null || localN.Length != localV.Length) return false;

                    var trs = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                    worldVertices = new Vector3[localV.Length];
                    worldNormals = new Vector3[localN.Length];

                    for (var i = 0; i < localV.Length; i++)
                    {
                        worldVertices[i] = trs.MultiplyPoint3x4(localV[i]);
                        worldNormals[i] = SmoothNormalCalculator.SafeNormalize(
                            trs.MultiplyVector(localN[i]), Vector3.up);
                    }

                    return true;
                }
                finally
                {
                    Object.DestroyImmediate(baked);
                }
            }
            else
            {
                var localV = mesh.vertices;
                var localN = mesh.normals;
                if (localN == null || localN.Length != localV.Length) return false;

                var l2w = t.localToWorldMatrix;
                var normalMatrix = l2w.inverse.transpose;
                worldVertices = new Vector3[localV.Length];
                worldNormals = new Vector3[localN.Length];

                for (var i = 0; i < localV.Length; i++)
                {
                    worldVertices[i] = l2w.MultiplyPoint3x4(localV[i]);
                    worldNormals[i] = SmoothNormalCalculator.SafeNormalize(
                        normalMatrix.MultiplyVector(localN[i]), Vector3.up);
                }

                return true;
            }
        }

        /// <summary>
        /// ワールド空間の目標法線配列を、対象 Renderer のバインドポーズ（メッシュローカル）空間の
        /// 基準法線に対するデルタへ変換して辞書へ格納する。
        /// </summary>
        internal static void ConvertWorldNormalsToLocalDeltas(
            Renderer renderer,
            Mesh mesh,
            Vector3[] worldNormals,
            Dictionary<int, Vector3> destination)
        {
            if (renderer == null || mesh == null || worldNormals == null || destination == null) return;

            destination.Clear();
            var baseNormals = mesh.normals;
            if (baseNormals == null || baseNormals.Length != worldNormals.Length) return;

            var smr = renderer as SkinnedMeshRenderer;
            var bones = smr != null ? smr.bones : null;
            var bindPoses = mesh.bindposes;
            var boneWeights = mesh.boneWeights;
            var hasSkinning = smr != null && bones != null && bindPoses != null && bindPoses.Length > 0 &&
                              boneWeights != null && boneWeights.Length == worldNormals.Length;

            var fallbackL2W = smr != null
                ? Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one)
                : renderer.transform.localToWorldMatrix;
            var fallbackW2L = Mathf.Abs(fallbackL2W.determinant) < 1e-12f ? Matrix4x4.identity : fallbackL2W.inverse;

            for (var i = 0; i < worldNormals.Length; i++)
            {
                var invSkin = fallbackW2L;
                if (hasSkinning)
                {
                    var skin = ComputeVertexSkinMatrix(bones, bindPoses, boneWeights[i], fallbackL2W);
                    if (Mathf.Abs(skin.determinant) >= 1e-12f) invSkin = skin.inverse;
                }

                var baseNormal = SmoothNormalCalculator.SafeNormalize(baseNormals[i], Vector3.up);
                var localNormal = SmoothNormalCalculator.SafeNormalize(
                    invSkin.MultiplyVector(worldNormals[i]), baseNormal);
                var delta = localNormal - baseNormal;

                if (delta.sqrMagnitude > NormalEdit.ZeroDeltaSqrEpsilon)
                {
                    destination[i] = delta;
                }
            }
        }

        private static Matrix4x4 ComputeVertexSkinMatrix(
            Transform[] bones,
            Matrix4x4[] bindPoses,
            BoneWeight bw,
            Matrix4x4 fallback)
        {
            var accumulated = new Matrix4x4();
            var total = 0f;

            total += AccumulateBone(ref accumulated, bones, bindPoses, bw.boneIndex0, bw.weight0);
            total += AccumulateBone(ref accumulated, bones, bindPoses, bw.boneIndex1, bw.weight1);
            total += AccumulateBone(ref accumulated, bones, bindPoses, bw.boneIndex2, bw.weight2);
            total += AccumulateBone(ref accumulated, bones, bindPoses, bw.boneIndex3, bw.weight3);

            if (total <= 1e-6f) return fallback;

            if (!Mathf.Approximately(total, 1f))
            {
                var scale = 1f / total;
                for (var k = 0; k < 16; k++) accumulated[k] *= scale;
            }

            return accumulated;
        }

        private static float AccumulateBone(
            ref Matrix4x4 accumulated,
            Transform[] bones,
            Matrix4x4[] bindPoses,
            int boneIndex,
            float weight)
        {
            if (weight <= 0f) return 0f;
            if (boneIndex < 0 || boneIndex >= bones.Length || boneIndex >= bindPoses.Length) return 0f;

            var bone = bones[boneIndex];
            if (bone == null) return 0f;

            var m = bone.localToWorldMatrix * bindPoses[boneIndex];
            for (var k = 0; k < 16; k++) accumulated[k] += m[k] * weight;

            return weight;
        }
    }
}
