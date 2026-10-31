using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenNormalEditor.Editor
{
    internal class DenNormalEditorPreviewNode : IRenderFilterNode
    {
        private sealed class Entry
        {
            public Renderer Original;
            public Renderer Proxy;
            public Mesh Source;
            public UpstreamNormals Upstream;
            public Mesh Generated;
            public readonly NormalRestoreBuffer Restore = new NormalRestoreBuffer();
            public readonly Dictionary<int, Vector3> Merged = new Dictionary<int, Vector3>();
            public List<EditState> Current = new List<EditState>();
            public List<EditState> Applied = new List<EditState>();
            public bool HasApplied;
            public int AppliedUpstreamGeneration;
            public bool AppliedWithMesh;
            public bool LoggedFailure;
        }

        private readonly Dictionary<Renderer, Entry> _entries = new Dictionary<Renderer, Entry>();
        private readonly List<DenNormalEditor> _components;

        public RenderAspects WhatChanged => RenderAspects.Mesh;

        internal DenNormalEditorPreviewNode(IEnumerable<(Renderer, Renderer)> proxyPairs, List<DenNormalEditor> components)
        {
            _components = components;

            foreach (var (original, proxy) in proxyPairs)
            {
                if (original == null) continue;

                _entries[original] = new Entry
                {
                    Original = original,
                    Proxy = proxy,
                };
            }
        }

        public void OnFrame(Renderer original, Renderer proxy)
        {
            ProxyRegistry.Report(original, proxy);

            if (!_entries.TryGetValue(original, out var entry)) return;
            entry.Proxy = proxy;

            var upstream = NormalDeltaApplier.GetSharedMesh(proxy);
            if (upstream == null)
            {
                entry.Source = null;
                UpstreamNormalsCache.Release(entry.Upstream);
                entry.Upstream = null;
                entry.HasApplied = false;
                DestroyGenerated(entry);
                DownstreamGuard.Forget(this, original);
                return;
            }

            if (upstream != entry.Source && upstream != entry.Generated)
            {
                entry.Source = upstream;
                UpstreamNormalsCache.Release(entry.Upstream);
                entry.Upstream = UpstreamNormalsCache.Acquire(upstream);
                entry.HasApplied = false;
                DestroyGenerated(entry);
            }

            if (entry.Source == null) return;

            entry.Upstream.Probe(EditorApplication.timeSinceStartup, original);
            EditState.Collect(_components, original, entry.Current, true);

            var hasMesh = entry.Generated != null;
            var dirty = !entry.HasApplied
                        || entry.AppliedUpstreamGeneration != entry.Upstream.Generation
                        || entry.AppliedWithMesh != hasMesh
                        || !EditState.SequenceEqual(entry.Applied, entry.Current);

            if (dirty && TryRebuild(entry, original)) hasMesh = entry.Generated != null;

            if (hasMesh) NormalDeltaApplier.SetSharedMesh(proxy, entry.Generated);

            DownstreamGuard.Expect(this, original, proxy, hasMesh ? entry.Generated : entry.Source);
        }

        private bool TryRebuild(Entry entry, Renderer original)
        {
            var upstreamGeneration = entry.Upstream.Generation;

            bool applied;
            try
            {
                applied = Rebuild(entry);
            }
            catch (Exception e)
            {
                if (!entry.LoggedFailure)
                {
                    entry.LoggedFailure = true;
                    Debug.LogError(
                        $"[Dennoko Normal Editor] {original.name} のプレビューを更新できませんでした。\n{e}",
                        original);
                }

                return false;
            }

            if (!applied) return false;

            entry.LoggedFailure = false;
            entry.HasApplied = true;
            entry.AppliedUpstreamGeneration = upstreamGeneration;
            entry.AppliedWithMesh = entry.Generated != null;

            (entry.Applied, entry.Current) = (entry.Current, entry.Applied);
            return true;
        }

        private bool Rebuild(Entry entry)
        {
            if (entry.Source == null || entry.Upstream == null || !entry.Upstream.HasRead) return false;

            var normals = entry.Upstream.Normals;
            if (normals.Count == 0)
            {
                DestroyGenerated(entry);
                return true;
            }

            var kind = DenNormalEditorPreviewFilter.GatherEditsInto(
                _components, entry.Original, normals.Count, entry.Merged, true, out var single);

            if (kind == DenNormalEditorPreviewFilter.GatherResult.Empty)
            {
                DestroyGenerated(entry);
                return true;
            }

            var created = false;
            if (entry.Generated == null)
            {
                entry.Generated = Object.Instantiate(entry.Source);
                entry.Generated.name = entry.Source.name + " (Dennoko Normal Editor)";
                entry.Generated.hideFlags = HideFlags.HideAndDontSave;
                entry.Generated.MarkDynamic();
                GeneratedMeshTracker.Track(entry.Generated);
                created = true;
            }

            try
            {
                if (kind == DenNormalEditorPreviewFilter.GatherResult.Single)
                {
                    NormalDeltaApplier.UpdateNormals(
                        entry.Generated, normals, entry.Upstream.Tangents, single, entry.Restore);
                }
                else
                {
                    NormalDeltaApplier.UpdateNormals(
                        entry.Generated, normals, entry.Upstream.Tangents, entry.Merged, entry.Restore);
                }
            }
            catch
            {
                if (created) DestroyGenerated(entry);
                throw;
            }

            return true;
        }

        private static void DestroyGenerated(Entry entry)
        {
            if (entry.Generated == null) return;

            GeneratedMeshTracker.Forget(entry.Generated);
            Object.DestroyImmediate(entry.Generated);
            entry.Generated = null;
        }

        public Task<IRenderFilterNode> Refresh(
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context,
            RenderAspects updatedAspects)
        {
            var reused = TryReuse(proxyPairs, context, updatedAspects);
            return Task.FromResult<IRenderFilterNode>(reused ? this : null);
        }

        private bool TryReuse(
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context,
            RenderAspects updatedAspects)
        {
            if ((updatedAspects & RenderAspects.Mesh) != 0) return false;

            var matched = 0;
            foreach (var (original, proxy) in proxyPairs)
            {
                if (original == null) continue;
                if (!_entries.TryGetValue(original, out var entry)) return false;
                if (entry.Proxy != proxy) return false;

                matched++;
            }

            if (matched != _entries.Count) return false;

            DenNormalEditorPreviewFilter.ObserveNodeInputs(context, _components, _entries.Keys);
            return true;
        }

        public void Dispose()
        {
            foreach (var entry in _entries.Values)
            {
                ProxyRegistry.Remove(entry.Original, entry.Proxy);
                DownstreamGuard.Forget(this, entry.Original);
                DestroyGenerated(entry);

                UpstreamNormalsCache.Release(entry.Upstream);
                entry.Upstream = null;
            }

            _entries.Clear();
        }
    }
}

