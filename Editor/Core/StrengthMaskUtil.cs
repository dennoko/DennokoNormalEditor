using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 強度マスク（UV0 上の明るさ。白 = 1、黒 = 0）の読み出しと頂点ごとの値の計算。
    /// </summary>
    internal static class StrengthMaskUtil
    {
        /// <summary>読み出し時の長辺の上限。頂点ごとのサンプリングにはこれで十分な解像度がある。</summary>
        internal const int MaxReadSize = 1024;

        /// <summary>CPU 側に読み出したマスク画像。</summary>
        internal sealed class MaskImage
        {
            public Color32[] Pixels;
            public int Width;
            public int Height;
            public TextureWrapMode WrapU;
            public TextureWrapMode WrapV;
        }

        /// <summary>
        /// テクスチャを CPU 側へ読み出す。失敗時は null。
        ///
        /// Read/Write 無効や圧縮形式でも読めるよう、RenderTexture へ Blit して ReadPixels する。
        /// RenderTexture の色空間をテクスチャのインポート設定（sRGB の有無）に合わせることで、
        /// 線形色空間のプロジェクトでも「画像に保存されている値」（ペイントツールで見た値）がそのまま得られる。
        /// </summary>
        internal static MaskImage Read(Texture2D texture)
        {
            if (texture == null) return null;

            var scale = Mathf.Min(1f, (float)MaxReadSize / Mathf.Max(1, Mathf.Max(texture.width, texture.height)));
            var width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));

            var srgb = texture.isDataSRGB;
            var readWrite = srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;

            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWrite);
            Texture2D readback = null;

            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;

                readback = new Texture2D(width, height, TextureFormat.RGBA32, false, !srgb)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);

                return new MaskImage
                {
                    Pixels = readback.GetPixels32(),
                    Width = width,
                    Height = height,
                    WrapU = texture.wrapModeU,
                    WrapV = texture.wrapModeV,
                };
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Dennoko Normal Editor] マスク {texture.name} を読み込めませんでした。\n{e}", texture);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (readback != null) Object.DestroyImmediate(readback);
            }
        }

        /// <summary>
        /// UV 座標でのマスク値（0..1）をバイリニア補間で求める。
        /// wrapMode が Clamp なら端で止め、それ以外は繰り返す。
        /// </summary>
        internal static float Sample(MaskImage image, Vector2 uv)
        {
            var x = uv.x * image.Width - 0.5f;
            var y = uv.y * image.Height - 0.5f;

            var x0 = Mathf.FloorToInt(x);
            var y0 = Mathf.FloorToInt(y);
            var fx = x - x0;
            var fy = y - y0;

            var xa = Wrap(x0, image.Width, image.WrapU);
            var xb = Wrap(x0 + 1, image.Width, image.WrapU);
            var ya = Wrap(y0, image.Height, image.WrapV);
            var yb = Wrap(y0 + 1, image.Height, image.WrapV);

            var c00 = Luminance(image.Pixels[ya * image.Width + xa]);
            var c10 = Luminance(image.Pixels[ya * image.Width + xb]);
            var c01 = Luminance(image.Pixels[yb * image.Width + xa]);
            var c11 = Luminance(image.Pixels[yb * image.Width + xb]);

            var bottom = Mathf.Lerp(c00, c10, fx);
            var top = Mathf.Lerp(c01, c11, fx);
            return Mathf.Clamp01(Mathf.Lerp(bottom, top, fy));
        }

        /// <summary>
        /// メッシュの全頂点について UV0 のマスク値を求める。UV0 を持たないメッシュは null（= 100% 扱い）。
        /// </summary>
        internal static float[] ComputeVertexMask(Mesh mesh, MaskImage image, List<Vector2> uvScratch)
        {
            if (mesh == null || image == null || !HasUV0(mesh)) return null;

            mesh.GetUVs(0, uvScratch);
            if (uvScratch.Count != mesh.vertexCount) return null;

            var result = new float[uvScratch.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = Sample(image, uvScratch[i]);
            }

            return result;
        }

        internal static bool HasUV0(Mesh mesh)
        {
            return mesh != null && mesh.HasVertexAttribute(VertexAttribute.TexCoord0);
        }

        /// <summary>
        /// 結合グループ（UV シーム・首の継ぎ目などで同じ位置にある頂点の集まり）ごとに最小値を取り、
        /// グループ内の全頂点へ配る。これにより「黒に近い方を優先」し、同じグループの頂点が
        /// 必ず同じ強度を受け取る（境界に法線の段差が生まれない）。
        /// </summary>
        /// <param name="vertexMasks">対象ごとの頂点マスク。null はマスク未指定（= すべて 1）。</param>
        /// <param name="vertexToGroup">[対象][頂点] → 結合グループ ID。null ならグループ化しない。</param>
        /// <returns>対象ごとの最終値。全頂点が 1 の対象は null。</returns>
        internal static float[][] CombineByGroup(IReadOnlyList<float[]> vertexMasks, int[][] vertexToGroup, int groupCount)
        {
            var count = vertexMasks.Count;
            var result = new float[count][];

            if (vertexToGroup == null || groupCount <= 0)
            {
                for (var t = 0; t < count; t++) result[t] = vertexMasks[t];
                return result;
            }

            var groupMin = new float[groupCount];
            for (var g = 0; g < groupCount; g++) groupMin[g] = 1f;

            var any = false;
            for (var t = 0; t < count; t++)
            {
                var own = vertexMasks[t];
                var map = GroupMapFor(vertexToGroup, t, own);
                if (own == null || map == null) continue;

                for (var i = 0; i < own.Length; i++)
                {
                    var g = map[i];
                    if (g < 0 || g >= groupCount || own[i] >= groupMin[g]) continue;
                    groupMin[g] = own[i];
                    any = true;
                }
            }

            for (var t = 0; t < count; t++)
            {
                var own = vertexMasks[t];
                var map = GroupMapFor(vertexToGroup, t, own);

                // グループ情報と頂点数が噛み合わない対象は、自分のマスクだけを使う
                if (map == null || !any)
                {
                    result[t] = own;
                    continue;
                }

                float[] combined = null;
                for (var i = 0; i < map.Length; i++)
                {
                    var g = map[i];
                    var value = g >= 0 && g < groupCount ? groupMin[g] : 1f;
                    if (own != null && own[i] < value) value = own[i];
                    if (value >= 1f) continue;

                    if (combined == null)
                    {
                        combined = new float[map.Length];
                        for (var k = 0; k < combined.Length; k++) combined[k] = 1f;
                    }

                    combined[i] = value;
                }

                result[t] = combined;
            }

            return result;
        }

        private static int[] GroupMapFor(int[][] vertexToGroup, int target, float[] own)
        {
            if (target >= vertexToGroup.Length) return null;

            var map = vertexToGroup[target];
            if (map == null) return null;
            if (own != null && own.Length != map.Length) return null;

            return map;
        }

        private static int Wrap(int i, int size, TextureWrapMode mode)
        {
            if (mode == TextureWrapMode.Clamp) return Mathf.Clamp(i, 0, size - 1);

            var m = i % size;
            return m < 0 ? m + size : m;
        }

        /// <summary>Color.grayscale と同じ係数の明るさ。</summary>
        private static float Luminance(Color32 c)
        {
            return (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
        }
    }
}
