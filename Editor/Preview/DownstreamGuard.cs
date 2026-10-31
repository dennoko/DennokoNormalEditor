using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 自分より下流の NDMF フィルタがプロキシのメッシュを差し替えているかを検出する。
    /// </summary>
    [InitializeOnLoad]
    internal static class DownstreamGuard
    {
        private sealed class State
        {
            public object Owner;
            public Renderer Proxy;
            public Mesh Expected;
            public int ExpectedVertexCount;
            public bool Overridden;
            public int ObservedVertexCount;
        }

        private static readonly Dictionary<Renderer, State> States = new Dictionary<Renderer, State>();
        private static int _overriddenCount;

        internal static bool AnyOverridden => _overriddenCount > 0;

        internal static readonly PublishedValue<int> OverrideGeneration =
            new PublishedValue<int>(0, "DenNormalEditor.DownstreamOverrideGeneration");

        static DownstreamGuard()
        {
            Camera.onPostRender -= OnPostRender;
            Camera.onPostRender += OnPostRender;
        }

        internal static void Expect(object owner, Renderer original, Renderer proxy, Mesh assigned)
        {
            if (original == null || proxy == null || assigned == null) return;

            if (!States.TryGetValue(original, out var state))
            {
                state = new State();
                States.Add(original, state);
            }

            state.Owner = owner;
            state.Proxy = proxy;
            state.Expected = assigned;
            state.ExpectedVertexCount = assigned.vertexCount;
        }

        internal static bool IsOverridden(Renderer original)
        {
            return original != null && States.TryGetValue(original, out var state) && state.Overridden;
        }

        internal static bool HasVertexCountMismatch(Renderer original)
        {
            if (original == null || !States.TryGetValue(original, out var state)) return false;
            return state.Overridden && state.ObservedVertexCount != state.ExpectedVertexCount;
        }

        internal static void Forget(object owner, Renderer original)
        {
            if (original == null) return;
            if (!States.TryGetValue(original, out var state)) return;
            if (!ReferenceEquals(state.Owner, owner)) return;

            if (state.Overridden) _overriddenCount--;
            States.Remove(original);
        }

        private static void OnPostRender(Camera camera)
        {
            if (States.Count == 0) return;

            List<Renderer> stale = null;
            var newlyLatched = false;

            foreach (var pair in States)
            {
                var state = pair.Value;
                if (state.Expected == null) continue;

                var expected = state.Expected;
                state.Expected = null;

                if (pair.Key == null || state.Proxy == null)
                {
                    if (state.Overridden)
                    {
                        state.Overridden = false;
                        _overriddenCount--;
                    }

                    stale ??= new List<Renderer>();
                    stale.Add(pair.Key);
                    continue;
                }

                var current = NormalDeltaApplier.GetSharedMesh(state.Proxy);
                if (current == expected) continue;

                state.ObservedVertexCount = current != null ? current.vertexCount : 0;

                if (state.Overridden) continue;

                state.Overridden = true;
                _overriddenCount++;
                newlyLatched = true;
            }

            if (stale != null)
            {
                foreach (var key in stale)
                {
                    States.Remove(key);
                }
            }

            if (!newlyLatched) return;

            unchecked
            {
                OverrideGeneration.Value = OverrideGeneration.Value + 1;
            }
        }
    }
}

