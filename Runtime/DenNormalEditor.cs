using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor
{
    /// <summary>
    /// VRChat アバター改変向けの非破壊カスタム法線編集コンポーネント。
    ///
    /// 編集結果は頂点ごとの法線デルタとして本コンポーネントに保持され、
    /// NDMF プレビューへの反映とビルド時の適用が行われる。元メッシュアセットは書き換えない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("dennokoworks/Dennoko Normal Editor")]
    public class DenNormalEditor : MonoBehaviour
#if DEN_NORMAL_EDITOR_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [Tooltip("編集対象の Renderer。頭と体など複数指定して境界をなじませることができます。")]
        public List<NormalEdit> edits = new List<NormalEdit>();

        [Header("ブラシ設定")]
        [Tooltip("プロポーショナル編集の影響半径（ワールド単位）。")]
        [Range(0.001f, 0.5f)]
        public float brushRadius = 0.03f;

        public FalloffType falloff = FalloffType.Smooth;

        [Header("ミラー")]
        [Tooltip("有効な間に行った操作のみがミラーされます。")]
        public bool mirror;

        public MirrorAxis mirrorAxis = MirrorAxis.X;

        // Inspector ヘッダーに有効/無効チェックボックスを表示し、ワンクリックで Before/After 比較できるようにする
        private void OnEnable()
        {
        }

        /// <summary>
        /// コンポーネント追加時に呼ばれる。Renderer を持つオブジェクトに付与された場合、
        /// その Renderer を最初の編集対象として自動登録する。
        /// </summary>
        private void Reset()
        {
            if (edits.Count > 0) return;

            var renderer = GetComponent<Renderer>();
            if (renderer is SkinnedMeshRenderer || renderer is MeshRenderer)
            {
                edits.Add(new NormalEdit { target = renderer });
            }
        }

        public NormalEdit FindEdit(Renderer target)
        {
            foreach (var edit in edits)
            {
                if (edit != null && edit.target == target) return edit;
            }

            return null;
        }
    }
}

