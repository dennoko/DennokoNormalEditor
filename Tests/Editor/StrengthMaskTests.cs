using System.Collections.Generic;
using Dennokoworks.DenNormalEditor.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Tests
{
    public class StrengthMaskTests
    {
        /// <summary>左が黒・右が白の 2×1 画像（Clamp）。</summary>
        private static StrengthMaskUtil.MaskImage BlackWhite(TextureWrapMode wrap = TextureWrapMode.Clamp)
        {
            return new StrengthMaskUtil.MaskImage
            {
                Pixels = new[] { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255) },
                Width = 2,
                Height = 1,
                WrapU = wrap,
                WrapV = wrap,
            };
        }

        [Test]
        public void Sample_BilinearBetweenTexelCenters()
        {
            var image = BlackWhite();

            Assert.AreEqual(0f, StrengthMaskUtil.Sample(image, new Vector2(0.25f, 0.5f)), 1e-4f);
            Assert.AreEqual(1f, StrengthMaskUtil.Sample(image, new Vector2(0.75f, 0.5f)), 1e-4f);
            Assert.AreEqual(0.5f, StrengthMaskUtil.Sample(image, new Vector2(0.5f, 0.5f)), 1e-4f);
        }

        [Test]
        public void Sample_ClampAndRepeatWrap()
        {
            // Clamp：範囲外は端のテクセル
            Assert.AreEqual(1f, StrengthMaskUtil.Sample(BlackWhite(), new Vector2(1.5f, 0.5f)), 1e-4f);

            // Repeat：1.25 は 0.25 と同じ
            var repeat = BlackWhite(TextureWrapMode.Repeat);
            Assert.AreEqual(0f, StrengthMaskUtil.Sample(repeat, new Vector2(1.25f, 0.5f)), 1e-4f);
        }

        [Test]
        public void ComputeVertexMask_ReadsUV0_AndNullWithoutUV()
        {
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 },
            };

            try
            {
                var scratch = new List<Vector2>();
                Assert.IsNull(StrengthMaskUtil.ComputeVertexMask(mesh, BlackWhite(), scratch));

                mesh.uv = new[] { new Vector2(0.25f, 0.5f), new Vector2(0.75f, 0.5f), new Vector2(0.5f, 0.5f) };
                var mask = StrengthMaskUtil.ComputeVertexMask(mesh, BlackWhite(), scratch);

                Assert.IsNotNull(mask);
                Assert.AreEqual(0f, mask[0], 1e-4f);
                Assert.AreEqual(1f, mask[1], 1e-4f);
                Assert.AreEqual(0.5f, mask[2], 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void CombineByGroup_TakesDarkestAcrossSeamAndMeshes()
        {
            // 対象 0：頂点 0 と 1 が UV シームで同じグループ 0。頂点 2 はグループ 1
            // 対象 1（マスク未指定）：頂点 0 が首の継ぎ目で対象 0 のグループ 1 と結合、頂点 1 はグループ 2
            var masks = new List<float[]> { new[] { 1f, 0.2f, 0.6f }, null };
            var groups = new[] { new[] { 0, 0, 1 }, new[] { 1, 2 } };

            var result = StrengthMaskUtil.CombineByGroup(masks, groups, 3);

            Assert.AreEqual(0.2f, result[0][0], 1e-6f); // シームの反対側の黒い方を採用
            Assert.AreEqual(0.2f, result[0][1], 1e-6f);
            Assert.AreEqual(0.6f, result[0][2], 1e-6f);

            Assert.IsNotNull(result[1]);                 // 未指定でも継ぎ目の頂点は相手のマスクに揃う
            Assert.AreEqual(0.6f, result[1][0], 1e-6f);
            Assert.AreEqual(1f, result[1][1], 1e-6f);
        }

        [Test]
        public void CombineByGroup_NoMasks_ReturnsNull()
        {
            var result = StrengthMaskUtil.CombineByGroup(
                new List<float[]> { null, null }, new[] { new[] { 0 }, new[] { 0 } }, 1);

            Assert.IsNull(result[0]);
            Assert.IsNull(result[1]);
        }

        [Test]
        public void Read_NonReadableTexture_PreservesStoredValues()
        {
            // sRGB / Linear のどちらでも、保存された値（128 ≒ 0.5）がそのまま読めること
            foreach (var linear in new[] { false, true })
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
                try
                {
                    var gray = new Color32(128, 128, 128, 255);
                    texture.SetPixels32(new[] { gray, gray, gray, gray });
                    texture.Apply(false, true); // makeNoLongerReadable

                    var image = StrengthMaskUtil.Read(texture);
                    Assert.IsNotNull(image, $"linear={linear}");
                    Assert.AreEqual(128f / 255f, StrengthMaskUtil.Sample(image, new Vector2(0.5f, 0.5f)), 0.02f,
                        $"linear={linear}");
                }
                finally
                {
                    Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
