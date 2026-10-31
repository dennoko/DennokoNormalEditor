using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 1 つの Renderer に寄与する <see cref="NormalEdit"/> 1 件分の変更比較値。
    /// </summary>
    internal readonly struct EditState : IEquatable<EditState>
    {
        public readonly NormalEdit Edit;
        public readonly int Revision;
        public readonly int Count;
        public readonly int VertexCount;
        public readonly int LiveStamp;

        private EditState(NormalEdit edit, int liveStamp)
        {
            Edit = edit;
            Revision = edit.Revision;
            Count = edit.Count;
            VertexCount = edit.vertexCount;
            LiveStamp = liveStamp;
        }

        public bool Equals(EditState other)
        {
            return ReferenceEquals(Edit, other.Edit)
                   && Revision == other.Revision
                   && Count == other.Count
                   && VertexCount == other.VertexCount
                   && LiveStamp == other.LiveStamp;
        }

        public override bool Equals(object obj) => obj is EditState other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Edit != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Edit) : 0;
                hash = hash * 31 + Revision;
                hash = hash * 31 + Count;
                hash = hash * 31 + VertexCount;
                return hash * 31 + LiveStamp;
            }
        }

        internal static void Collect(IReadOnlyList<DenNormalEditor> components, Renderer target,
            List<EditState> destination, bool includeLive)
        {
            destination.Clear();

            for (var c = 0; c < components.Count; c++)
            {
                AppendFrom(components[c], target, destination, includeLive);
            }
        }

        internal static void AppendFrom(DenNormalEditor component, Renderer target,
            List<EditState> destination, bool includeLive)
        {
            if (component == null || !component.enabled) return;

            var edits = component.edits;
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (edit == null || edit.target != target) continue;

                destination.Add(new EditState(edit, includeLive ? LiveEdits.GetStamp(edit) : 0));
            }
        }

        internal static void AppendFrom(DenNormalEditor component, HashSet<Renderer> targets,
            List<EditState> destination, bool includeLive)
        {
            if (component == null || !component.enabled) return;

            var edits = component.edits;
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (edit == null) continue;

                var target = edit.target;
                if (target == null || !targets.Contains(target)) continue;

                destination.Add(new EditState(edit, includeLive ? LiveEdits.GetStamp(edit) : 0));
            }
        }

        internal static bool SequenceEqual(List<EditState> a, List<EditState> b)
        {
            if (a.Count != b.Count) return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i])) return false;
            }

            return true;
        }
    }

    internal sealed class EditSnapshot
    {
        private readonly EditState[] _states;

        private EditSnapshot(List<EditState> states)
        {
            _states = states.ToArray();
        }

        internal static EditSnapshot From(List<EditState> states) => new EditSnapshot(states);

        internal bool Matches(List<EditState> states)
        {
            if (_states.Length != states.Count) return false;

            for (var i = 0; i < _states.Length; i++)
            {
                if (!_states[i].Equals(states[i])) return false;
            }

            return true;
        }

        internal static bool AreEqual(EditSnapshot a, EditSnapshot b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a._states.Length != b._states.Length) return false;

            for (var i = 0; i < a._states.Length; i++)
            {
                if (!a._states[i].Equals(b._states[i])) return false;
            }

            return true;
        }
    }
}

