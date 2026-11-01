using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Dennokoworks.DenNormalEditor.Editor.DenNormalEditorPlugin))]

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// ビルド時に法線デルタを適用する非破壊プラグイン。
    /// Transforming フェーズで Modular Avatar の後、Avatar Optimizer の前に動作する。
    /// </summary>
    internal class DenNormalEditorPlugin : Plugin<DenNormalEditorPlugin>
    {
        public override string QualifiedName => "dennokoworks.den-normal-editor";
        public override string DisplayName => "Dennoko Normal Editor";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("Apply Normal Edits", Execute)
                .PreviewingWith(new DenNormalEditorPreviewFilter());
        }

        private static void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<DenNormalEditor>(true);
            if (components.Length == 0) return;

            var componentList = new List<DenNormalEditor>(components.Length);
            foreach (var c in components)
            {
                if (c != null && c.enabled) componentList.Add(c);
            }

            var processed = new HashSet<Renderer>();

            foreach (var component in componentList)
            {
                foreach (var edit in component.edits)
                {
                    if (edit?.target == null) continue;

                    if (!edit.target.transform.IsChildOf(context.AvatarRootTransform))
                    {
                        Debug.LogWarning(
                            $"[Dennoko Normal Editor] {edit.target.name} はアバター配下にないため適用をスキップしました。",
                            component);
                        continue;
                    }

                    if (!processed.Add(edit.target)) continue;

                    ApplyToRenderer(context, edit.target, componentList);
                }
            }

            // VRChat のアップロード制約回避のため、ビルド成果物からコンポーネント自身を削除
            foreach (var component in components)
            {
                if (component != null) Object.DestroyImmediate(component);
            }
        }

        private static void ApplyToRenderer(BuildContext context, Renderer target, List<DenNormalEditor> components)
        {
            var sourceMesh = NormalDeltaApplier.GetSharedMesh(target);
            if (sourceMesh == null) return;

            var skipped = new List<string>();
            var merged = DenNormalEditorPreviewFilter.GatherEdits(
                components, target, sourceMesh.vertexCount, skipped);

            foreach (var message in skipped)
            {
                Debug.LogWarning($"[Dennoko Normal Editor] {target.name}: {message}", target);
            }

            if (merged == null) return;

            var edited = NormalDeltaApplier.CreateEdited(sourceMesh, merged);
            if (edited == null) return;

            ObjectRegistry.RegisterReplacedObject(sourceMesh, edited);
            context.AssetSaver.SaveAsset(edited);

            NormalDeltaApplier.SetSharedMesh(target, edited);

            LogAppendedVertices(target, components, sourceMesh.vertexCount);
        }

        /// <summary>
        /// 上流のツールがメッシュ末尾へ頂点を追加していた場合に、その旨を記録する（追加分の頂点には編集を適用しない）。
        /// </summary>
        private static void LogAppendedVertices(Renderer target, List<DenNormalEditor> components, int vertexCount)
        {
            var recorded = 0;
            foreach (var component in components)
            {
                if (component == null || !component.enabled) continue;

                foreach (var edit in component.edits)
                {
                    if (edit == null || edit.target != target || !edit.HasEdits) continue;
                    if (edit.vertexCount > 0 && edit.vertexCount < vertexCount) recorded = Mathf.Max(recorded, edit.vertexCount);
                }
            }

            if (recorded == 0) return;

            Debug.Log(
                $"[Dennoko Normal Editor] {target.name}: 他のツールによりメッシュ末尾へ頂点が追加されています"
                + $"（現在 {vertexCount} / 編集時 {recorded}）。既存の頂点にのみ法線編集を適用しました。",
                target);
        }
    }
}

