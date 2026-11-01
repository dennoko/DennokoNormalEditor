using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 法線デルタ適用の共通ロジック。プレビュー・ビルド・ベイクのすべてがこれを共有する。
    ///
    /// 基準法線へデルタを加算して正規化し、接線（Tangent）が存在する場合は新しい法線に対して
    /// グラム・シュミット直交化を行う。
    /// </summary>
    internal static class NormalDeltaApplier
    {
        internal static Mesh GetSharedMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case null:
                    return null;
                case SkinnedMeshRenderer smr:
                    return smr.sharedMesh;
                default:
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
            }
        }

        internal static void SetSharedMesh(Renderer renderer, Mesh mesh)
        {
            switch (renderer)
            {
                case null:
                    return;
                case SkinnedMeshRenderer smr:
                    smr.sharedMesh = mesh;
                    return;
                default:
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = mesh;
                    return;
            }
        }

        /// <summary>
        /// 編集時の頂点数 <paramref name="recorded"/> の編集データを、頂点数 <paramref name="current"/> のメッシュへ適用できるか。
        ///
        /// 頂点が末尾に追加されただけなら既存の頂点インデックスは変わらないため、適用を許可する
        /// （例: zatools の Convex Depth Wrapper はビルド時に凸包の頂点をメッシュ末尾へ追加する）。
        /// 頂点数が減った場合はインデックスがずれている可能性があるため適用しない。
        /// </summary>
        internal static bool IsVertexCountCompatible(int recorded, int current)
        {
            return recorded == 0 || recorded <= current;
        }

        internal static bool IsCompatible(Mesh mesh, NormalEdit edit)
        {
            if (mesh == null || edit == null || !edit.HasEdits) return false;

            if (!IsVertexCountCompatible(edit.vertexCount, mesh.vertexCount)) return false;

            var count = edit.Count;
            for (var i = 0; i < count; i++)
            {
                var index = edit.GetIndex(i);
                if (index < 0 || index >= mesh.vertexCount) return false;
            }

            return true;
        }

        /// <summary>
        /// 基準法線とデルタから適用後の単位法線を計算する。潰れた場合は基準法線をそのまま返す。
        /// </summary>
        internal static Vector3 EvaluateNormal(Vector3 baseNormal, Vector3 delta)
        {
            var sum = baseNormal + delta;
            var sqr = sum.sqrMagnitude;
            if (sqr > 1e-12f)
            {
                return sum * (1f / Mathf.Sqrt(sqr));
            }

            var baseSqr = baseNormal.sqrMagnitude;
            return baseSqr > 1e-12f ? baseNormal * (1f / Mathf.Sqrt(baseSqr)) : Vector3.up;
        }

        /// <summary>
        /// 接線ベクトルを新しい法線に対してグラム・シュミット直交化する（w 符号は維持）。
        /// </summary>
        internal static Vector4 OrthogonalizeTangent(Vector4 baseTangent, Vector3 newNormal)
        {
            var t = new Vector3(baseTangent.x, baseTangent.y, baseTangent.z);
            var ortho = t - newNormal * Vector3.Dot(newNormal, t);
            var sqr = ortho.sqrMagnitude;
            if (sqr <= 1e-12f) return baseTangent;

            var inv = 1f / Mathf.Sqrt(sqr);
            ortho *= inv;
            return new Vector4(ortho.x, ortho.y, ortho.z, baseTangent.w);
        }

        internal static void ApplyInPlace(Vector3[] normals, Vector4[] tangents, NormalEdit edit)
        {
            if (normals == null || edit == null) return;

            var hasTangents = tangents != null && tangents.Length == normals.Length;
            var count = edit.Count;

            for (var i = 0; i < count; i++)
            {
                var index = edit.GetIndex(i);
                if (index < 0 || index >= normals.Length) continue;

                var newNormal = EvaluateNormal(normals[index], edit.GetDelta(i));
                normals[index] = newNormal;

                if (hasTangents)
                {
                    tangents[index] = OrthogonalizeTangent(tangents[index], newNormal);
                }
            }
        }

        internal static Mesh CreateEdited(Mesh source, NormalEdit edit)
        {
            if (!IsCompatible(source, edit)) return null;

            var normals = source.normals;
            if (normals == null || normals.Length != source.vertexCount) return null;

            var mesh = Object.Instantiate(source);
            mesh.name = source.name + "_edited";

            var tangents = mesh.tangents;
            ApplyInPlace(normals, tangents, edit);
            mesh.normals = normals;

            if (tangents != null && tangents.Length == normals.Length)
            {
                mesh.tangents = tangents;
            }

            return mesh;
        }

        /// <summary>
        /// 生成済みメッシュの法線・接線をインプレース更新する（ドラッグ中のライブプレビュー用）。
        /// 基準配列へ一時書き込みして SetNormals / SetTangents した直後に RestoreBuffer で元の値へ戻す。
        /// </summary>
        internal static void UpdateNormals(
            Mesh mesh,
            List<Vector3> baseNormals,
            List<Vector4> baseTangents,
            NormalEdit edit,
            NormalRestoreBuffer restore)
        {
            if (mesh == null || baseNormals == null || edit == null || restore == null) return;

            var vertexCount = baseNormals.Count;
            if (vertexCount == 0) return;

            var hasTangents = baseTangents != null && baseTangents.Count == vertexCount;
            restore.Begin();

            try
            {
                var count = edit.Count;
                for (var i = 0; i < count; i++)
                {
                    var index = edit.GetIndex(i);
                    if (index < 0 || index >= vertexCount) continue;

                    restore.Apply(baseNormals, hasTangents ? baseTangents : null, index, edit.GetDelta(i));
                }

                mesh.SetNormals(baseNormals);
                if (hasTangents) mesh.SetTangents(baseTangents);
            }
            finally
            {
                restore.RestoreTo(baseNormals, hasTangents ? baseTangents : null);
            }
        }

        internal static void UpdateNormals(
            Mesh mesh,
            List<Vector3> baseNormals,
            List<Vector4> baseTangents,
            Dictionary<int, Vector3> deltas,
            NormalRestoreBuffer restore)
        {
            if (mesh == null || baseNormals == null || deltas == null || restore == null) return;

            var vertexCount = baseNormals.Count;
            if (vertexCount == 0) return;

            var hasTangents = baseTangents != null && baseTangents.Count == vertexCount;
            restore.Begin();

            try
            {
                foreach (var pair in deltas)
                {
                    var index = pair.Key;
                    if (index < 0 || index >= vertexCount) continue;

                    restore.Apply(baseNormals, hasTangents ? baseTangents : null, index, pair.Value);
                }

                mesh.SetNormals(baseNormals);
                if (hasTangents) mesh.SetTangents(baseTangents);
            }
            finally
            {
                restore.RestoreTo(baseNormals, hasTangents ? baseTangents : null);
            }
        }
    }

    /// <summary>
    /// UpdateNormals が基準リストへ一時的に書き込んだ法線・接線を元へ戻すための退避バッファ。
    /// </summary>
    internal sealed class NormalRestoreBuffer
    {
        private readonly struct RestoredEntry
        {
            public readonly int Index;
            public readonly Vector3 Normal;
            public readonly Vector4 Tangent;

            public RestoredEntry(int index, Vector3 normal, Vector4 tangent)
            {
                Index = index;
                Normal = normal;
                Tangent = tangent;
            }
        }

        private readonly List<RestoredEntry> _entries = new List<RestoredEntry>();

        internal void Begin()
        {
            _entries.Clear();
        }

        internal void Apply(List<Vector3> normals, List<Vector4> tangents, int index, Vector3 delta)
        {
            var originalNormal = normals[index];
            var originalTangent = tangents != null ? tangents[index] : Vector4.zero;
            _entries.Add(new RestoredEntry(index, originalNormal, originalTangent));

            var newNormal = NormalDeltaApplier.EvaluateNormal(originalNormal, delta);
            normals[index] = newNormal;

            if (tangents != null)
            {
                tangents[index] = NormalDeltaApplier.OrthogonalizeTangent(originalTangent, newNormal);
            }
        }

        internal void RestoreTo(List<Vector3> normals, List<Vector4> tangents)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                normals[entry.Index] = entry.Normal;
                if (tangents != null)
                {
                    tangents[entry.Index] = entry.Tangent;
                }
            }

            _entries.Clear();
        }
    }

    /// <summary>
    /// プレビュー用に生成した一時メッシュ（HideAndDontSave）のドメインリロード前破棄追跡。
    /// </summary>
    [InitializeOnLoad]
    internal static class GeneratedMeshTracker
    {
        private static readonly HashSet<Mesh> Tracked = new HashSet<Mesh>();

        static GeneratedMeshTracker()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
        }

        internal static void Track(Mesh mesh)
        {
            if (mesh != null) Tracked.Add(mesh);
        }

        internal static void Forget(Mesh mesh)
        {
            if (mesh != null) Tracked.Remove(mesh);
        }

        private static void DestroyAll()
        {
            foreach (var mesh in Tracked)
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }

            Tracked.Clear();
        }
    }
}

