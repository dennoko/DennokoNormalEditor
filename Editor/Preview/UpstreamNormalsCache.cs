using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 上流メッシュの基準法線・基準接線の読み取りと変化検出。
    /// 同一メッシュを参照する複数の Renderer 間で共有される。
    /// </summary>
    internal sealed class UpstreamNormals
    {
        private const double FullReadInterval = 2.0;

        internal readonly Mesh Mesh;

        private List<Vector3> _normals = new List<Vector3>();
        private List<Vector4> _tangents = new List<Vector4>();
        private List<Vector3> _scratchNormals = new List<Vector3>();
        private List<Vector4> _scratchTangents = new List<Vector4>();

        private readonly double _phase;
        private bool _hasRead;
        private double _nextRead;
        private bool _warnedNotReadable;

        internal UpstreamNormals(Mesh mesh)
        {
            Mesh = mesh;
            _phase = mesh != null ? (mesh.GetInstanceID() & 0xFF) / 255.0 : 0.0;
        }

        internal int Generation { get; private set; }
        internal List<Vector3> Normals => _normals;
        internal List<Vector4> Tangents => _tangents;
        internal bool HasRead => _hasRead;

        internal void Probe(double now, Object context)
        {
            if (Mesh == null) return;
            if (_hasRead && now < _nextRead) return;

            try
            {
                ReadAll(now, context);
            }
            catch (Exception e)
            {
                if (_warnedNotReadable) return;
                _warnedNotReadable = true;
                Debug.LogWarning(
                    $"[Dennoko Normal Editor] 上流メッシュの法線を読み取れませんでした。 ({e.GetType().Name}: {e.Message})",
                    context);
            }
        }

        private void ReadAll(double now, Object context)
        {
            Mesh.GetNormals(_scratchNormals);
            Mesh.GetTangents(_scratchTangents);

            if (_scratchNormals.Count == 0 && Mesh.vertexCount > 0)
            {
                if (!_warnedNotReadable)
                {
                    _warnedNotReadable = true;
                    Debug.LogWarning(
                        $"[Dennoko Normal Editor] {(context != null ? context.name : "?")} のメッシュ「{Mesh.name}」から"
                        + "法線を読み取れませんでした。インポート設定の Read/Write Enabled を有効にしてください。",
                        context);
                }

                return;
            }

            if (!_hasRead || !SameNormals(_normals, _scratchNormals))
            {
                (_normals, _scratchNormals) = (_scratchNormals, _normals);
                (_tangents, _scratchTangents) = (_scratchTangents, _tangents);

                unchecked
                {
                    Generation++;
                }
            }

            _hasRead = true;
            _nextRead = now + FullReadInterval * (0.75 + 0.5 * _phase);
            _warnedNotReadable = false;
        }

        private static bool SameNormals(List<Vector3> a, List<Vector3> b)
        {
            if (a.Count != b.Count) return false;

            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (!x.x.Equals(y.x) || !x.y.Equals(y.y) || !x.z.Equals(y.z)) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 同じ上流メッシュを参照する Renderer 同士で <see cref="UpstreamNormals"/> を共有する。
    /// </summary>
    internal static class UpstreamNormalsCache
    {
        private sealed class Slot
        {
            public UpstreamNormals Value;
            public int RefCount;
        }

        private sealed class ReferenceComparer : IEqualityComparer<Mesh>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(Mesh x, Mesh y) => ReferenceEquals(x, y);
            public int GetHashCode(Mesh obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly Dictionary<Mesh, Slot> Slots = new Dictionary<Mesh, Slot>(ReferenceComparer.Instance);

        internal static UpstreamNormals Acquire(Mesh mesh)
        {
            if (mesh == null) return null;

            if (!Slots.TryGetValue(mesh, out var slot))
            {
                slot = new Slot { Value = new UpstreamNormals(mesh) };
                Slots.Add(mesh, slot);
            }

            slot.RefCount++;
            return slot.Value;
        }

        internal static void Release(UpstreamNormals upstream)
        {
            if (upstream == null) return;
            if (!Slots.TryGetValue(upstream.Mesh, out var slot)) return;
            if (!ReferenceEquals(slot.Value, upstream)) return;

            if (--slot.RefCount > 0) return;

            Slots.Remove(upstream.Mesh);
        }
    }
}

