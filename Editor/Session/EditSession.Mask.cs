using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 対象ごとの強度マスク（<see cref="NormalEdit.strengthMask"/>）のキャッシュ。
    ///
    /// マスク値は「頂点ごとの最終値」として、マスクの参照・内容（再インポート）・形状が
    /// 変わったときだけ作り直す。ドラッグ中にテクスチャは読まない。
    /// </summary>
    internal partial class EditSession
    {
        /// <summary>トポロジーの作り直しなどで、全対象のマスクを計算し直す必要があるか。</summary>
        private bool _maskDirty = true;

        private readonly List<Vector2> _maskUvScratch = new List<Vector2>();
        private readonly List<float[]> _maskInputs = new List<float[]>();

        private static float MaskAt(TargetState target, int index)
        {
            var mask = target.Mask;
            return mask == null || index >= mask.Length ? 1f : mask[index];
        }

        /// <summary>
        /// 必要ならマスクを作り直す。作り直した場合は true。
        ///
        /// 変化がなければ対象ごとの参照比較と <c>imageContentsHash</c> の比較だけで返るので、
        /// <see cref="BuildInfluences"/> の先頭から毎回呼んでよい。
        /// </summary>
        private bool RebuildMaskIfNeeded()
        {
            var rebuildAll = _maskDirty;
            _maskDirty = false;

            var changed = rebuildAll;
            Dictionary<Texture2D, StrengthMaskUtil.MaskImage> images = null;

            foreach (var target in _targets)
            {
                var texture = target.Edit != null ? target.Edit.strengthMask : null;
                if (texture == null) texture = null; // 破棄済みの参照を正規化する

                var hash = texture != null ? texture.imageContentsHash : default;
                if (!rebuildAll && target.MaskSource == texture && target.MaskHash == hash) continue;

                target.MaskSource = texture;
                target.MaskHash = hash;
                target.VertexMask = null;
                changed = true;

                if (texture == null) continue;

                images ??= new Dictionary<Texture2D, StrengthMaskUtil.MaskImage>();
                if (!images.TryGetValue(texture, out var image))
                {
                    image = StrengthMaskUtil.Read(texture);
                    images.Add(texture, image);
                }

                target.VertexMask = StrengthMaskUtil.ComputeVertexMask(ResolveMaskMesh(target), image, _maskUvScratch);
            }

            if (!changed) return false;

            _maskInputs.Clear();
            foreach (var target in _targets) _maskInputs.Add(target.VertexMask);

            var combined = StrengthMaskUtil.CombineByGroup(
                _maskInputs,
                _crossTopology?.VertexToGroup,
                _crossTopology?.GroupCount ?? 0);

            for (var t = 0; t < _targets.Count; t++)
            {
                _targets[t].Mask = combined[t];
            }

            return true;
        }

        /// <summary>
        /// マスクの UV を読むメッシュ。マスクは元テクスチャの UV に合わせて描かれるため、
        /// 上流ツール（アトラス化など）が UV を書き換えうるプロキシ側ではなく元メッシュを優先する。
        /// </summary>
        private static Mesh ResolveMaskMesh(TargetState target)
        {
            var original = target.OriginalMesh;
            var vertexCount = target.WorldVertices?.Length ?? target.VertexCount;

            if (original != null && original.vertexCount == vertexCount) return original;
            return target.Mesh != null ? target.Mesh : original;
        }
    }
}
