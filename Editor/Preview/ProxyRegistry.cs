using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// NDMF プレビューのプロキシ Renderer をシーンビュー編集ツールへ受け渡すためのレジストリ。
    /// </summary>
    internal static class ProxyRegistry
    {
        private static readonly Dictionary<Renderer, Renderer> Map = new Dictionary<Renderer, Renderer>();
        private static int _pruneThreshold = 64;

        internal static void Report(Renderer original, Renderer proxy)
        {
            if (original == null || proxy == null) return;

            var isNew = !Map.ContainsKey(original);
            Map[original] = proxy;

            if (!isNew || Map.Count < _pruneThreshold) return;

            Prune();
            _pruneThreshold = Mathf.Max(64, Map.Count * 2);
        }

        internal static void Remove(Renderer original, Renderer proxy)
        {
            if (original == null) return;
            if (Map.TryGetValue(original, out var current) && current != proxy) return;

            Map.Remove(original);
        }

        internal static bool TryGet(Renderer original, out Renderer proxy)
        {
            proxy = null;
            if (original == null) return false;
            if (!Map.TryGetValue(original, out var found)) return false;
            if (found == null)
            {
                Map.Remove(original);
                return false;
            }

            proxy = found;
            return true;
        }

        internal static Renderer ResolveOrOriginal(Renderer original, out bool isProxy)
        {
            isProxy = TryGet(original, out var proxy);
            return isProxy ? proxy : original;
        }

        internal static void Prune()
        {
            List<Renderer> stale = null;

            foreach (var pair in Map)
            {
                if (pair.Key != null && pair.Value != null) continue;

                stale ??= new List<Renderer>();
                stale.Add(pair.Key);
            }

            if (stale == null) return;

            foreach (var key in stale)
            {
                Map.Remove(key);
            }
        }
    }
}

