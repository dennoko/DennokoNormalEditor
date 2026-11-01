using System.Collections.Generic;
using Dennokoworks.DenNormalEditor.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Tests
{
    public class NormalEditorTests
    {
        [Test]
        public void NormalEdit_SetFromAndCopyTo_PreservesSparseDeltasAndSkipsZeros()
        {
            var edit = new NormalEdit();
            var initialRevision = edit.Revision;

            var input = new Dictionary<int, Vector3>
            {
                [3] = new Vector3(0.1f, -0.2f, 0.3f),
                [10] = Vector3.zero, // ゼロは保存されない
                [25] = new Vector3(-0.5f, 0.25f, 0.125f),
            };

            edit.SetFrom(input, 100);

            Assert.AreEqual(2, edit.Count);
            Assert.AreEqual(100, edit.vertexCount);
            Assert.AreEqual(initialRevision + 1, edit.Revision);
            Assert.IsTrue(edit.HasEdits);

            var output = edit.ToDictionary();
            Assert.AreEqual(2, output.Count);
            Assert.IsTrue(output.ContainsKey(3));
            Assert.IsTrue(output.ContainsKey(25));
            Assert.IsFalse(output.ContainsKey(10));
            Assert.Less((output[3] - input[3]).magnitude, 1e-6f);
            Assert.Less((output[25] - input[25]).magnitude, 1e-6f);

            edit.Clear();
            Assert.AreEqual(0, edit.Count);
            Assert.IsFalse(edit.HasEdits);
        }

        [Test]
        public void NormalDeltaApplier_EvaluateAndRestoreBuffer_OrthogonalizesTangentsAndRestoresValues()
        {
            var baseNormals = new List<Vector3>
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
            };
            var baseTangents = new List<Vector4>
            {
                new Vector4(1f, 0f, 0f, -1f),
                new Vector4(1f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
            };
            var restore = new NormalRestoreBuffer();

            var targetN = new Vector3(1f, 0f, 1f).normalized;
            var delta = targetN - Vector3.forward;

            restore.Begin();
            restore.Apply(baseNormals, baseTangents, 0, delta);

            // 適用中は法線が targetN になり、接線が直交化され w = -1 が維持されていること
            Assert.Less((baseNormals[0] - targetN).magnitude, 1e-5f);
            var t0 = new Vector3(baseTangents[0].x, baseTangents[0].y, baseTangents[0].z);
            Assert.Less(Mathf.Abs(Vector3.Dot(baseNormals[0], t0)), 1e-5f);
            Assert.Less(Mathf.Abs(t0.magnitude - 1f), 1e-5f);
            Assert.AreEqual(-1f, baseTangents[0].w);

            // RestoreTo 後は元の基準法線・基準接線に完全に戻ること
            restore.RestoreTo(baseNormals, baseTangents);
            Assert.AreEqual(Vector3.forward, baseNormals[0]);
            Assert.AreEqual(new Vector4(1f, 0f, 0f, -1f), baseTangents[0]);
        }

        [Test]
        public void NormalDeltaApplier_ApplyAndRestore_OrthogonalizesTangentsAndRestoresBuffer()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f),
                };
                mesh.normals = new[]
                {
                    Vector3.forward,
                    Vector3.forward,
                    Vector3.forward,
                };
                mesh.tangents = new[]
                {
                    new Vector4(1f, 0f, 0f, -1f),
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(1f, 0f, 0f, 1f),
                };
                mesh.triangles = new[] { 0, 1, 2 };

                var baseNormals = new List<Vector3>(mesh.normals);
                var baseTangents = new List<Vector4>(mesh.tangents);
                var restore = new NormalRestoreBuffer();

                // 頂点 0 の法線を (1, 0, 1).normalized 方向へ傾ける
                var targetN = new Vector3(1f, 0f, 1f).normalized;
                var edit = new NormalEdit();
                edit.SetFrom(new Dictionary<int, Vector3>
                {
                    [0] = targetN - Vector3.forward
                }, 3);

                NormalDeltaApplier.UpdateNormals(mesh, baseNormals, baseTangents, edit, restore);

                // 基準リストは UpdateNormals 完了後に元の値へ復元されていること
                Assert.AreEqual(Vector3.forward, baseNormals[0]);
                Assert.AreEqual(new Vector4(1f, 0f, 0f, -1f), baseTangents[0]);

                // メッシュ側の法線は正規化された目標法線になり、接線は直交化され w 符号が維持されていること
                var updatedNormals = mesh.normals;
                var updatedTangents = mesh.tangents;
                Assert.Less((updatedNormals[0] - targetN).magnitude, 1e-5f);

                var t0 = new Vector3(updatedTangents[0].x, updatedTangents[0].y, updatedTangents[0].z);
                Assert.Less(Mathf.Abs(Vector3.Dot(updatedNormals[0], t0)), 1e-5f);
                Assert.Less(Mathf.Abs(t0.magnitude - 1f), 1e-5f);
                Assert.AreEqual(-1f, updatedTangents[0].w);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void SmoothNormalCalculator_CrossMeshSeamWeld_UnifiesNeckBoundaryNormalsBetweenHeadAndBody()
        {
            // Head メッシュ（y = 0..1）と Body メッシュ（y = -1..0）が y = 0 の境界（首の継ぎ目）で接しているケース。
            // 境界頂点間に 0.4mm の微小なズレがあっても同一結合グループにリンクされ、
            // なじませ処理（ComputeSmoothedGroupNormals）で境界の法線不一致が解消されることを検証する。
            var headVertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            var headTriangles = new[] { 0, 2, 1 };

            var bodyVertices = new[]
            {
                new Vector3(0f, -0.0004f, 0f), // Head[0] から 0.4mm（<= 1mm）
                new Vector3(1f, -0.0004f, 0f), // Head[1] から 0.4mm（<= 1mm）
                new Vector3(0f, -1f, 0f),
            };
            var bodyTriangles = new[] { 0, 1, 2 };

            var inputs = new[]
            {
                new SmoothNormalCalculator.MeshInput { WorldVertices = headVertices, Triangles = headTriangles },
                new SmoothNormalCalculator.MeshInput { WorldVertices = bodyVertices, Triangles = bodyTriangles },
            };

            var topology = SmoothNormalCalculator.BuildTopology(inputs, SmoothNormalCalculator.DefaultSeamWeldTolerance);

            // 境界の 2 頂点は Head と Body で同じ結合グループ ID を共有していること
            Assert.AreEqual(topology.VertexToGroup[0][0], topology.VertexToGroup[1][0]);
            Assert.AreEqual(topology.VertexToGroup[0][1], topology.VertexToGroup[1][1]);
            Assert.AreEqual(4, topology.GroupCount);

            // Head 側と Body 側で境界法線が別方向を向いて割れている状態を作る
            var headNormals = new[]
            {
                new Vector3(0f, 0.6f, 0.8f).normalized,
                new Vector3(0f, 0.6f, 0.8f).normalized,
                Vector3.forward,
            };
            var bodyNormals = new[]
            {
                new Vector3(0f, -0.6f, 0.8f).normalized,
                new Vector3(0f, -0.6f, 0.8f).normalized,
                Vector3.forward,
            };

            var smoothedGroups = new Vector3[topology.GroupCount];
            SmoothNormalCalculator.ComputeSmoothedGroupNormals(
                topology,
                new[] { headNormals, bodyNormals },
                smoothedGroups,
                iterations: 1);

            var seamGroup0 = topology.VertexToGroup[0][0];
            var seamNormal = smoothedGroups[seamGroup0];

            // 上下の傾きが相殺され、正面（+Z）方向の連続した単位法線に収束すること
            Assert.Less(Mathf.Abs(seamNormal.y), 1e-4f);
            Assert.Greater(seamNormal.z, 0.99f);
            Assert.Less(Mathf.Abs(seamNormal.magnitude - 1f), 1e-5f);
        }

        [Test]
        public void SmoothNormalCalculator_RestoreShape_RecoversSmoothContourOnSphereNormalMesh()
        {
            // 鼻筋のような山型の屋根形状（中央 x=0 が z=-0.5 に盛り上がっている）だが、
            // カスタム法線として完全にフラットな法線（(0, 0, -1)）が転写されているメッシュ。
            // UV シームで中央頂点が分割されていても、SmoothGeometricNormals は割れないスムーズな立体法線を持ち、
            // SlerpNormal で元の形状の法線情報を任意の割合だけ滑らかに戻せることを検証する。
            var vertices = new[]
            {
                // 左斜面三角形 (x: -1 -> 0)
                new Vector3(-1f, 0f, 0f),
                new Vector3(0f, 0f, -0.5f), // 鼻筋頂点 A
                new Vector3(0f, 1f, -0.5f), // 鼻筋頂点 B
                // 右斜面三角形 (UVシームで頂点が複製されている)
                new Vector3(0f, 0f, -0.5f), // 鼻筋頂点 A' (A と同座標)
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, -0.5f), // 鼻筋頂点 B' (B と同座標)
            };
            var triangles = new[]
            {
                0, 2, 1,
                3, 5, 4,
            };

            var topology = SmoothNormalCalculator.BuildTopology(new[]
            {
                new SmoothNormalCalculator.MeshInput { WorldVertices = vertices, Triangles = triangles }
            }, weldTolerance: 0.001f, geoSmoothIterations: 1);

            // 分割された鼻筋頂点 A (index 1) と A' (index 3) が同一グループに結合されていること
            var gA = topology.VertexToGroup[0][1];
            var gA2 = topology.VertexToGroup[0][3];
            Assert.AreEqual(gA, gA2);

            // 左斜面の頂点 (index 0) のスムーズ形状法線は左前方向（x < 0, z < 0）を向いていること
            var gLeft = topology.VertexToGroup[0][0];
            var leftGeoNormal = topology.SmoothGeometricNormals[gLeft];
            Assert.Less(leftGeoNormal.x, -0.15f);
            Assert.Less(leftGeoNormal.z, -0.8f);

            // フラット法線 (0, 0, -1) から 40% だけ形状法線を戻したとき、中間の角度に滑らかに補間されること
            var flatNormal = Vector3.back;
            var restored40 = SmoothNormalCalculator.SlerpNormal(flatNormal, leftGeoNormal, 0.4f);
            Assert.Less(restored40.x, 0f);
            Assert.Greater(restored40.x, leftGeoNormal.x);
            Assert.Less(Mathf.Abs(restored40.magnitude - 1f), 1e-5f);
        }

        [Test]
        public void VersionChecker_IsUpdateAvailable_DetectsNewerVersionsCorrectly()
        {
            Assert.IsTrue(DennokoVersionChecker.IsUpdateAvailable("1.0.1", "1.0.0"));
            Assert.IsTrue(DennokoVersionChecker.IsUpdateAvailable("v1.1.0", "1.0.0"));
            Assert.IsTrue(DennokoVersionChecker.IsUpdateAvailable("2.0.0", "1.9.9"));
            Assert.IsFalse(DennokoVersionChecker.IsUpdateAvailable("1.0.0", "1.0.0"));
            Assert.IsFalse(DennokoVersionChecker.IsUpdateAvailable("v1.0.0", "1.0.0"));
            Assert.IsFalse(DennokoVersionChecker.IsUpdateAvailable("0.9.9", "1.0.0"));
        }

        [Test]
        public void DenNormalEditorVersion_Current_ReadsVersionJson()
        {
            Assert.AreEqual("1.0.0", DenNormalEditorVersion.Current);
        }
    }
}
