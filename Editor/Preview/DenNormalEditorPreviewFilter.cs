using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 法線編集結果を NDMF プレビューへ反映するフィルタ。
    /// </summary>
    internal class DenNormalEditorPreviewFilter : IRenderFilter
    {
        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var editing = context.Observe(EditSession.ActiveComponent, c => c, (a, b) => a == b);

            var byRenderer = new Dictionary<Renderer, List<DenNormalEditor>>();
            var order = new List<Renderer>();

            foreach (var component in context.GetComponentsByType<DenNormalEditor>())
            {
                if (component == null) continue;
                if (!context.ActiveInHierarchy(component.gameObject)) continue;

                var isEditing = ReferenceEquals(component, editing);
                ObserveShape(context, component, isEditing);

                if (!component.enabled && !isEditing) continue;

                foreach (var edit in component.edits)
                {
                    if (edit?.target == null) continue;
                    if (!isEditing && !edit.HasEdits) continue;

                    if (!byRenderer.TryGetValue(edit.target, out var owners))
                    {
                        owners = new List<DenNormalEditor>();
                        byRenderer.Add(edit.target, owners);
                        order.Add(edit.target);
                    }

                    if (!owners.Contains(component)) owners.Add(component);
                }
            }

            var builder = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var target in order)
            {
                builder.Add(RenderGroup.For(target).WithData(byRenderer[target], Enumerable.SequenceEqual));
            }

            return builder.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var components = group.GetData<List<DenNormalEditor>>() ?? new List<DenNormalEditor>();
            ObserveNodeInputs(context, components, group.Renderers);

            var node = new DenNormalEditorPreviewNode(proxyPairs, components);
            return Task.FromResult<IRenderFilterNode>(node);
        }

        internal static void ObserveNodeInputs(
            ComputeContext context,
            IReadOnlyList<DenNormalEditor> components,
            IEnumerable<Renderer> originals)
        {
            var targets = new HashSet<Renderer>();
            foreach (var original in originals)
            {
                if (original != null) targets.Add(original);
            }

            foreach (var component in components)
            {
                if (component == null) continue;
                ObserveEdits(context, component, targets);
            }

            ObserveDownstreamSync(context, components, targets);
        }

        private static void ObserveDownstreamSync(
            ComputeContext context,
            IReadOnlyList<DenNormalEditor> components,
            HashSet<Renderer> targets)
        {
            context.Observe(DownstreamGuard.OverrideGeneration, v => v, (a, b) => a == b);

            var overridden = false;
            foreach (var original in targets)
            {
                if (!DownstreamGuard.IsOverridden(original)) continue;
                overridden = true;
                break;
            }

            if (!overridden) return;

            var scratch = new List<EditState>();
            context.Observe(
                LiveEdits.SyncedVersion,
                _ =>
                {
                    scratch.Clear();
                    foreach (var component in components)
                    {
                        EditState.AppendFrom(component, targets, scratch, true);
                    }

                    return EditSnapshot.From(scratch);
                },
                EditSnapshot.AreEqual);
        }

        private static void ObserveShape(ComputeContext context, DenNormalEditor component, bool isEditing)
        {
            if (isEditing)
            {
                context.Observe(component, EditingShapeFingerprint, (a, b) => a == b);
                return;
            }

            context.Observe(component, ShapeFingerprint, (a, b) => a == b);
        }

        private static void ObserveEdits(ComputeContext context, DenNormalEditor component, HashSet<Renderer> targets)
        {
            var editing = context.Observe(EditSession.ActiveComponent, c => c, (a, b) => a == b);
            if (ReferenceEquals(component, editing)) return;

            var tracker = new EditsTracker(targets);
            context.Observe(component, tracker.Extract, EditSnapshot.AreEqual);
        }

        private sealed class EditsTracker
        {
            private readonly HashSet<Renderer> _targets;
            private readonly List<EditState> _scratch = new List<EditState>();
            private EditSnapshot _last;

            internal EditsTracker(HashSet<Renderer> targets)
            {
                _targets = targets;
            }

            internal EditSnapshot Extract(DenNormalEditor component)
            {
                _scratch.Clear();
                EditState.AppendFrom(component, _targets, _scratch, false);

                if (_last != null && _last.Matches(_scratch)) return _last;

                _last = EditSnapshot.From(_scratch);
                return _last;
            }
        }

        private static int ShapeFingerprint(DenNormalEditor component)
        {
            return ComputeShapeFingerprint(component, true);
        }

        private static int EditingShapeFingerprint(DenNormalEditor component)
        {
            return ComputeShapeFingerprint(component, false);
        }

        private static int ComputeShapeFingerprint(DenNormalEditor component, bool includeHasEdits)
        {
            if (component == null) return 0;

            unchecked
            {
                var hash = 17;
                hash = hash * 31 + (component.enabled ? 1 : 0);
                hash = hash * 31 + component.edits.Count;

                foreach (var edit in component.edits)
                {
                    if (edit == null)
                    {
                        hash = hash * 31 + 1;
                        continue;
                    }

                    hash = hash * 31 + (edit.target != null ? edit.target.GetInstanceID() : 0);
                    if (includeHasEdits) hash = hash * 31 + (edit.HasEdits ? 1 : 0);
                }

                return hash;
            }
        }

        internal static NormalEdit GatherEdits(IReadOnlyList<DenNormalEditor> components, Renderer target, int vertexCount,
            List<string> skipped = null)
        {
            var merged = new Dictionary<int, Vector3>();
            var kind = GatherEditsInto(components, target, vertexCount, merged, false, out _, skipped);
            if (kind == GatherResult.Empty) return null;

            var result = new NormalEdit { target = target };
            result.SetFrom(merged, vertexCount);
            return result.HasEdits ? result : null;
        }

        internal enum GatherResult
        {
            Empty,
            Single,
            Merged,
        }

        internal static GatherResult GatherEditsInto(
            IReadOnlyList<DenNormalEditor> components,
            Renderer target,
            int vertexCount,
            Dictionary<int, Vector3> destination,
            bool allowSingle,
            out NormalEdit single,
            List<string> skipped = null)
        {
            destination.Clear();
            single = null;

            NormalEdit sole = null;
            var contributions = 0;
            var anyLive = false;

            for (var c = 0; c < components.Count; c++)
            {
                var component = components[c];
                if (component == null || !component.enabled) continue;

                foreach (var edit in component.edits)
                {
                    if (!IsApplicable(edit, target, vertexCount, skipped)) continue;

                    if (LiveEdits.TryGet(edit, out _))
                    {
                        anyLive = true;
                        contributions++;
                        continue;
                    }

                    if (edit.Count == 0) continue;

                    contributions++;
                    sole = edit;
                }
            }

            if (contributions == 0) return GatherResult.Empty;

            if (allowSingle && contributions == 1 && !anyLive)
            {
                single = sole;
                return GatherResult.Single;
            }

            for (var c = 0; c < components.Count; c++)
            {
                var component = components[c];
                if (component == null || !component.enabled) continue;

                foreach (var edit in component.edits)
                {
                    if (!IsApplicable(edit, target, vertexCount, null)) continue;

                    if (LiveEdits.TryGet(edit, out var live))
                    {
                        foreach (var pair in live)
                        {
                            if (pair.Value.sqrMagnitude <= NormalEdit.ZeroDeltaSqrEpsilon) continue;
                            destination.TryGetValue(pair.Key, out var accumulated);
                            destination[pair.Key] = accumulated + pair.Value;
                        }

                        continue;
                    }

                    var count = edit.Count;
                    for (var i = 0; i < count; i++)
                    {
                        var index = edit.GetIndex(i);
                        destination.TryGetValue(index, out var accumulated);
                        destination[index] = accumulated + edit.GetDelta(i);
                    }
                }
            }

            foreach (var pair in destination)
            {
                if (pair.Value.sqrMagnitude > NormalEdit.ZeroDeltaSqrEpsilon) return GatherResult.Merged;
            }

            return GatherResult.Empty;
        }

        private static bool IsApplicable(NormalEdit edit, Renderer target, int vertexCount, List<string> skipped)
        {
            if (edit == null || edit.target != target) return false;
            if (edit.vertexCount == 0 || edit.vertexCount == vertexCount) return true;

            if (edit.HasEdits)
            {
                skipped?.Add(
                    $"頂点数が編集時と異なるため {edit.Count} 頂点分の法線編集を適用できませんでした"
                    + $"（現在 {vertexCount} / 編集時 {edit.vertexCount}）。");
            }

            return false;
        }
    }
}
