using UnityEditor;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// コンポーネントの書き換えを Undo へ積むための共通処理。
    ///
    /// byte[] blob を持つコンポーネントで Undo.RecordObject を使うと Prefab Reconciliation で
    /// 重くなるため、Undo.RegisterCompleteObjectUndo によるスナップショット方式を採用する。
    /// </summary>
    internal static class DenNormalEditorUndo
    {
        internal static int BeginGroup(Object target, string name)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();

            Record(target, name);
            Undo.SetCurrentGroupName(name);

            return group;
        }

        internal static void Record(Object target, string name)
        {
            if (target == null) return;

            Undo.RegisterCompleteObjectUndo(target, name);
        }

        internal static void Collapse(int group)
        {
            if (group < 0) return;

            Undo.CollapseUndoOperations(group);
        }

        internal static void Apply(Object target)
        {
            if (target == null) return;

            EditorUtility.SetDirty(target);

            if (PrefabUtility.IsPartOfPrefabInstance(target))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
        }

        internal static void EndGroup()
        {
            Undo.IncrementCurrentGroup();
        }
    }
}

