using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor
{
    /// <summary>
    /// 1 つの Renderer に対する法線編集データ。
    ///
    /// 編集された頂点だけを疎に保持する。保存する値はメッシュローカル（バインドポーズ）空間での
    /// 法線デルタ（<c>editedNormal - baseNormal</c>）であり、適用時は基準法線に加算して正規化する。
    ///
    /// 保持形式は <c>byte[]</c> 1 本（int32 インデックス + float32 dx/dy/dz = 16 バイト/頂点）。
    /// テキスト YAML の肥大化と Prefab オーバーライドの負荷を防ぐ。
    /// </summary>
    [Serializable]
    public class NormalEdit
    {
        /// <summary>1 頂点分のバイト数。int32 インデックス + float32 x/y/z。</summary>
        private const int Stride = 16;

        /// <summary>ゼロとみなすデルタ二乗長のしきい値。</summary>
        public const float ZeroDeltaSqrEpsilon = 1e-10f;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Bits;
        }

        public Renderer target;

        [Tooltip("編集時点の頂点数。適用前の整合性チェックに使用します。")]
        public int vertexCount;

        [SerializeField, HideInInspector] private int count;
        [SerializeField, HideInInspector] private byte[] blob;

        /// <summary>
        /// 編集データの世代番号。書き込みのたびに増える。
        /// NDMF プレビューの変更検出（ComputeContext.Observe）が O(1) で判定するために使う。
        /// </summary>
        [SerializeField, HideInInspector] private int revision;

        /// <summary>編集された頂点数。</summary>
        public int Count => count;

        /// <summary>編集データの世代番号。</summary>
        public int Revision => revision;

        public bool HasEdits => count > 0 && blob != null && blob.Length >= count * Stride;

        /// <summary><paramref name="i"/> 番目の編集頂点のインデックス。</summary>
        public int GetIndex(int i)
        {
            return ReadInt(blob, i * Stride);
        }

        /// <summary><paramref name="i"/> 番目の編集頂点の法線デルタ。</summary>
        public Vector3 GetDelta(int i)
        {
            var offset = i * Stride + 4;
            return new Vector3(
                ReadFloat(blob, offset),
                ReadFloat(blob, offset + 4),
                ReadFloat(blob, offset + 8));
        }

        public Dictionary<int, Vector3> ToDictionary()
        {
            var dict = new Dictionary<int, Vector3>(count);
            CopyTo(dict);
            return dict;
        }

        /// <summary>
        /// 内容を既存の辞書へ書き出す（辞書インスタンスを再利用して GC 確保を避ける）。
        /// </summary>
        public void CopyTo(Dictionary<int, Vector3> destination)
        {
            if (destination == null) return;

            destination.Clear();

            if (!HasEdits) return;

            var n = count;
            for (var i = 0; i < n; i++)
            {
                destination[GetIndex(i)] = GetDelta(i);
            }
        }

        /// <summary>
        /// 辞書の内容で上書きする。ゼロに戻った頂点は保存しない。
        /// </summary>
        public void SetFrom(Dictionary<int, Vector3> dict, int newVertexCount)
        {
            if (dict == null || dict.Count == 0)
            {
                Clear();
                vertexCount = newVertexCount;
                return;
            }

            var buffer = new byte[dict.Count * Stride];
            var written = 0;

            foreach (var kv in dict)
            {
                if (kv.Value.sqrMagnitude <= ZeroDeltaSqrEpsilon) continue;

                var offset = written * Stride;
                WriteInt(buffer, offset, kv.Key);
                WriteFloat(buffer, offset + 4, kv.Value.x);
                WriteFloat(buffer, offset + 8, kv.Value.y);
                WriteFloat(buffer, offset + 12, kv.Value.z);
                written++;
            }

            blob = Trim(buffer, written * Stride);
            count = written;
            vertexCount = newVertexCount;

            unchecked
            {
                revision++;
            }
        }

        public void Clear()
        {
            if (count == 0 && (blob == null || blob.Length == 0)) return;

            count = 0;
            blob = null;

            unchecked
            {
                revision++;
            }
        }

        private static byte[] Trim(byte[] buffer, int used)
        {
            if (used == 0) return null;
            if (buffer.Length == used) return buffer;

            var trimmed = new byte[used];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, used);
            return trimmed;
        }

        private static int ReadInt(byte[] buffer, int offset)
        {
            return buffer[offset]
                   | (buffer[offset + 1] << 8)
                   | (buffer[offset + 2] << 16)
                   | (buffer[offset + 3] << 24);
        }

        private static float ReadFloat(byte[] buffer, int offset)
        {
            return new FloatBits { Bits = ReadInt(buffer, offset) }.Value;
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteFloat(byte[] buffer, int offset, float value)
        {
            WriteInt(buffer, offset, new FloatBits { Value = value }.Bits);
        }
    }
}

