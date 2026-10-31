using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// ドラッグ中の未確定な法線編集内容を、シーンビュー編集セッションから
    /// NDMF プレビューノードへ受け渡すための一時領域。
    /// </summary>
    internal static class LiveEdits
    {
        private static readonly Dictionary<NormalEdit, Dictionary<int, Vector3>> Map =
            new Dictionary<NormalEdit, Dictionary<int, Vector3>>();

        private static readonly Dictionary<NormalEdit, int> Stamps = new Dictionary<NormalEdit, int>();

        private static int _stampCounter;
        private static int _version;

        internal static readonly PublishedValue<int> SyncedVersion =
            new PublishedValue<int>(0, "DenNormalEditor.LiveEditsSyncedVersion");

        private const double SyncIntervalSeconds = 0.05;
        private static double _nextSync;
        private static bool _syncPending;

        internal static void Invalidate()
        {
            unchecked
            {
                _version++;
            }

            RequestSync();
        }

        private static void RequestSync()
        {
            if (!DownstreamGuard.AnyOverridden) return;
            if (_syncPending) return;

            _syncPending = true;
            EditorApplication.update += FlushPending;
        }

        private static void FlushPending()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < _nextSync) return;

            EditorApplication.update -= FlushPending;
            _syncPending = false;
            _nextSync = now + SyncIntervalSeconds;
            SyncedVersion.Value = _version;
        }

        internal static void Publish(NormalEdit edit, Dictionary<int, Vector3> deltas)
        {
            if (edit == null || deltas == null) return;

            Map[edit] = deltas;

            unchecked
            {
                if (++_stampCounter == 0) ++_stampCounter;
            }

            Stamps[edit] = _stampCounter;
            Invalidate();
        }

        internal static int GetStamp(NormalEdit edit)
        {
            return edit != null && Stamps.TryGetValue(edit, out var stamp) ? stamp : 0;
        }

        internal static bool TryGet(NormalEdit edit, out Dictionary<int, Vector3> deltas)
        {
            deltas = null;
            return edit != null && Map.TryGetValue(edit, out deltas);
        }

        internal static bool Clear()
        {
            if (Map.Count == 0) return false;

            Map.Clear();
            Stamps.Clear();
            Invalidate();
            return true;
        }
    }
}

