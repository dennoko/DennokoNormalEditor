using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 編集結果を適用したメッシュをアセットとして書き出す。
    /// 元メッシュアセットには一切書き込まない。
    /// </summary>
    internal static class DenNormalEditorBaker
    {
        private const string Suffix = "_edited";

        internal static void Bake(DenNormalEditor component)
        {
            if (component == null) return;

            var created = new List<Object>();
            var skipped = new List<string>();

            foreach (var edit in component.edits)
            {
                if (edit?.target == null || !edit.HasEdits) continue;

                var source = NormalDeltaApplier.GetSharedMesh(edit.target);
                if (source == null)
                {
                    skipped.Add($"{edit.target.name}: メッシュが見つかりません");
                    continue;
                }

                if (!NormalDeltaApplier.IsCompatible(source, edit))
                {
                    skipped.Add(
                        $"{edit.target.name}: 頂点数が編集時と異なります（現在 {source.vertexCount} / 編集時 {edit.vertexCount}）");
                    continue;
                }

                var baseName = MakeBaseName(source.name);
                var mesh = NormalDeltaApplier.CreateEdited(source, edit);
                if (mesh == null)
                {
                    skipped.Add($"{edit.target.name}: メッシュの生成に失敗しました");
                    continue;
                }

                var path = MakeUniquePath(source, baseName);
                mesh.name = Path.GetFileNameWithoutExtension(path);

                AssetDatabase.CreateAsset(mesh, path);
                created.Add(mesh);
            }

            if (created.Count > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                foreach (var asset in created)
                {
                    Debug.Log($"[Dennoko Normal Editor] ベイクしました: {AssetDatabase.GetAssetPath(asset)}", asset);
                }

                var assets = created.ToArray();
                EditorApplication.delayCall += () =>
                {
                    if (assets.Length == 0 || assets[0] == null) return;

                    EditorGUIUtility.PingObject(assets[0]);
                    if (EditSession.Active == null) Selection.objects = assets;
                };
            }

            foreach (var message in skipped)
            {
                Debug.LogWarning($"[Dennoko Normal Editor] ベイクをスキップしました — {message}", component);
            }

            if (created.Count == 0 && skipped.Count == 0)
            {
                Debug.LogWarning("[Dennoko Normal Editor] ベイク対象がありません（編集データが空です）。", component);
            }
        }

        private static string MakeBaseName(string sourceName)
        {
            return sourceName.EndsWith(Suffix) ? sourceName : sourceName + Suffix;
        }

        private static string MakeUniquePath(Mesh source, string baseName)
        {
            var sourcePath = AssetDatabase.GetAssetPath(source);
            var folder = string.IsNullOrEmpty(sourcePath) ? "Assets" : Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(folder)) folder = "Assets";
            folder = folder.Replace('\\', '/');

            var candidate = $"{folder}/{baseName}.asset";
            if (!Exists(candidate)) return candidate;

            for (var n = 1; ; n++)
            {
                candidate = $"{folder}/{baseName} {n}.asset";
                if (!Exists(candidate)) return candidate;
            }
        }

        private static bool Exists(string path)
        {
            return AssetDatabase.LoadAssetAtPath<Object>(path) != null || File.Exists(path);
        }
    }
}

