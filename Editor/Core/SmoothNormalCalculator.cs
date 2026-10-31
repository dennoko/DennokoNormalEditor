using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    /// <summary>
    /// 複数メッシュ（頭と体など）を横断した頂点結合（Cross-Mesh Seam Weld）、
    /// トポロジー隣接グラフ構築、スムーズ形状法線（SmoothGeometricNormals）算出、
    /// および周囲との法線スムーズ化（なじませ）を行う計算クラス。
    /// </summary>
    internal static class SmoothNormalCalculator
    {
        /// <summary>別メッシュ間の境界（首の継ぎ目等）やUVシームを同一頂点とみなす既定距離（1mm）。</summary>
        internal const float DefaultSeamWeldTolerance = 0.001f;

        /// <summary>スムーズ形状法線を算出する際の既定ラプラシアンスムーズ反復回数。</summary>
        internal const int DefaultGeoSmoothIterations = 2;

        /// <summary>「なじませる（Smooth）」操作時の既定ラプラシアンスムーズ反復回数。</summary>
        internal const int DefaultSmoothIterations = 2;

        internal struct MeshInput
        {
            public Vector3[] WorldVertices;
            public IReadOnlyList<int> Triangles;
        }

        /// <summary>
        /// 登録された全 Renderer を統合した結合グループと隣接グラフ、および事前計算されたスムーズ形状法線。
        /// </summary>
        internal sealed class CrossMeshTopology
        {
            public int GroupCount;

            /// <summary>[meshIndex][vertexIndex] → 結合グループID。</summary>
            public int[][] VertexToGroup;

            /// <summary>グループごとの代表ワールド座標。</summary>
            public Vector3[] GroupPositions;

            /// <summary>
            /// グループごとのスムーズ形状法線（ワールド空間）。
            /// 同一・近接座標の頂点を結合した上で全メッシュの三角形面法線を合算し、
            /// 軽いラプラシアンスムージングをかけたもの。
            /// </summary>
            public Vector3[] SmoothGeometricNormals;

            /// <summary>CSR形式の隣接グループオフセット（長さ GroupCount + 1）。</summary>
            public int[] NeighborOffsets;

            /// <summary>CSR形式の隣接グループID配列。</summary>
            public int[] NeighborIndices;

            /// <summary>なじませ計算用の作業バッファ（長さ GroupCount）。</summary>
            public Vector3[] SmoothedBufferA;
            public Vector3[] SmoothedBufferB;
        }

        private readonly struct CellKey : IEquatable<CellKey>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public CellKey(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(CellKey other) => X == other.X && Y == other.Y && Z == other.Z;

            public override bool Equals(object obj) => obj is CellKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791);
                }
            }
        }

        /// <summary>
        /// 複数メッシュのワールド頂点と三角形から、クロス・メッシュ結合トポロジーとスムーズ形状法線を構築する。
        /// </summary>
        internal static CrossMeshTopology BuildTopology(
            IReadOnlyList<MeshInput> meshes,
            float weldTolerance = DefaultSeamWeldTolerance,
            int geoSmoothIterations = DefaultGeoSmoothIterations)
        {
            if (meshes == null || meshes.Count == 0)
            {
                return new CrossMeshTopology
                {
                    GroupCount = 0,
                    VertexToGroup = Array.Empty<int[]>(),
                    GroupPositions = Array.Empty<Vector3>(),
                    SmoothGeometricNormals = Array.Empty<Vector3>(),
                    NeighborOffsets = new[] { 0 },
                    NeighborIndices = Array.Empty<int>(),
                    SmoothedBufferA = Array.Empty<Vector3>(),
                    SmoothedBufferB = Array.Empty<Vector3>(),
                };
            }

            var vertexToGroup = BuildCrossMeshWeldGroups(meshes, weldTolerance, out var groupPositions);
            var groupCount = groupPositions.Length;

            BuildAdjacency(meshes, vertexToGroup, groupCount, out var neighborOffsets, out var neighborIndices);

            var smoothGeo = new Vector3[groupCount];
            var scratch = new Vector3[groupCount];
            ComputeSmoothGeometricNormals(
                meshes, vertexToGroup, groupCount, neighborOffsets, neighborIndices,
                geoSmoothIterations, smoothGeo, scratch);

            return new CrossMeshTopology
            {
                GroupCount = groupCount,
                VertexToGroup = vertexToGroup,
                GroupPositions = groupPositions,
                SmoothGeometricNormals = smoothGeo,
                NeighborOffsets = neighborOffsets,
                NeighborIndices = neighborIndices,
                SmoothedBufferA = new Vector3[groupCount],
                SmoothedBufferB = scratch,
            };
        }

        /// <summary>
        /// 空間ハッシュを用いて、同一メッシュ内および別メッシュ間で距離が weldTolerance 以内の頂点を
        /// 同一の結合グループにまとめる。O(全頂点数)。
        /// </summary>
        internal static int[][] BuildCrossMeshWeldGroups(
            IReadOnlyList<MeshInput> meshes,
            float weldTolerance,
            out Vector3[] groupPositions)
        {
            var meshCount = meshes.Count;
            var vertexToGroup = new int[meshCount][];
            var representatives = new List<Vector3>(1024);

            var tol = Mathf.Max(0f, weldTolerance);
            var tolSq = tol * tol;

            // セルサイズはしきい値の2倍以上にすることで、近傍27セル探索で確実に全ペアを拾える
            var cellSize = Mathf.Max(tol * 2f, 0.002f);
            var invCellSize = 1f / cellSize;
            var grid = new Dictionary<CellKey, List<int>>(1024);

            for (var m = 0; m < meshCount; m++)
            {
                var vertices = meshes[m].WorldVertices;
                if (vertices == null || vertices.Length == 0)
                {
                    vertexToGroup[m] = Array.Empty<int>();
                    continue;
                }

                var map = new int[vertices.Length];
                vertexToGroup[m] = map;

                for (var i = 0; i < vertices.Length; i++)
                {
                    var pos = vertices[i];
                    var cx = Mathf.FloorToInt(pos.x * invCellSize);
                    var cy = Mathf.FloorToInt(pos.y * invCellSize);
                    var cz = Mathf.FloorToInt(pos.z * invCellSize);

                    var foundGroup = -1;
                    var bestDistSq = tolSq;

                    for (var dz = -1; dz <= 1; dz++)
                    {
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var key = new CellKey(cx + dx, cy + dy, cz + dz);
                                if (!grid.TryGetValue(key, out var bucket)) continue;

                                for (var b = 0; b < bucket.Count; b++)
                                {
                                    var candidateGroup = bucket[b];
                                    var distSq = (representatives[candidateGroup] - pos).sqrMagnitude;
                                    if (distSq <= bestDistSq)
                                    {
                                        bestDistSq = distSq;
                                        foundGroup = candidateGroup;
                                    }
                                }
                            }
                        }
                    }

                    if (foundGroup < 0)
                    {
                        foundGroup = representatives.Count;
                        representatives.Add(pos);

                        var ownKey = new CellKey(cx, cy, cz);
                        if (!grid.TryGetValue(ownKey, out var ownBucket))
                        {
                            ownBucket = new List<int>(2);
                            grid.Add(ownKey, ownBucket);
                        }

                        ownBucket.Add(foundGroup);
                    }

                    map[i] = foundGroup;
                }
            }

            groupPositions = representatives.ToArray();
            return vertexToGroup;
        }

        /// <summary>
        /// 全メッシュの三角形から結合グループ間の隣接グラフ（CSR形式）を構築する。
        /// </summary>
        private static void BuildAdjacency(
            IReadOnlyList<MeshInput> meshes,
            int[][] vertexToGroup,
            int groupCount,
            out int[] offsets,
            out int[] indices)
        {
            if (groupCount == 0)
            {
                offsets = new[] { 0 };
                indices = Array.Empty<int>();
                return;
            }

            var adjacencyLists = new List<int>[groupCount];
            for (var g = 0; g < groupCount; g++)
            {
                adjacencyLists[g] = new List<int>(6);
            }

            for (var m = 0; m < meshes.Count; m++)
            {
                var tris = meshes[m].Triangles;
                var map = vertexToGroup[m];
                if (tris == null || map == null || map.Length == 0) continue;

                var vCount = map.Length;
                var triLen = tris.Count;
                for (var t = 0; t + 2 < triLen; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vCount || i1 >= vCount || i2 >= vCount) continue;

                    int g0 = map[i0], g1 = map[i1], g2 = map[i2];
                    AddUniqueEdge(adjacencyLists, g0, g1);
                    AddUniqueEdge(adjacencyLists, g1, g2);
                    AddUniqueEdge(adjacencyLists, g2, g0);
                }
            }

            offsets = new int[groupCount + 1];
            var totalEdges = 0;
            for (var g = 0; g < groupCount; g++)
            {
                offsets[g] = totalEdges;
                totalEdges += adjacencyLists[g].Count;
            }

            offsets[groupCount] = totalEdges;
            indices = new int[totalEdges];

            var cursor = 0;
            for (var g = 0; g < groupCount; g++)
            {
                var list = adjacencyLists[g];
                for (var k = 0; k < list.Count; k++)
                {
                    indices[cursor++] = list[k];
                }
            }
        }

        private static void AddUniqueEdge(List<int>[] adjacency, int a, int b)
        {
            if (a == b) return;

            var listA = adjacency[a];
            if (!listA.Contains(b)) listA.Add(b);

            var listB = adjacency[b];
            if (!listB.Contains(a)) listB.Add(a);
        }

        /// <summary>
        /// 結合グループ単位で面積重み付き面法線を合算し、隣接グループ間でラプラシアンスムージングを行う。
        /// </summary>
        internal static void ComputeSmoothGeometricNormals(
            IReadOnlyList<MeshInput> meshes,
            int[][] vertexToGroup,
            int groupCount,
            int[] neighborOffsets,
            int[] neighborIndices,
            int smoothIterations,
            Vector3[] result,
            Vector3[] scratch)
        {
            Array.Clear(result, 0, groupCount);

            for (var m = 0; m < meshes.Count; m++)
            {
                var vertices = meshes[m].WorldVertices;
                var tris = meshes[m].Triangles;
                var map = vertexToGroup[m];
                if (vertices == null || tris == null || map == null || map.Length == 0) continue;

                var vCount = vertices.Length;
                var triLen = tris.Count;
                for (var t = 0; t + 2 < triLen; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vCount || i1 >= vCount || i2 >= vCount) continue;

                    var p0 = vertices[i0];
                    var faceNormal = Vector3.Cross(vertices[i1] - p0, vertices[i2] - p0);

                    result[map[i0]] += faceNormal;
                    result[map[i1]] += faceNormal;
                    result[map[i2]] += faceNormal;
                }
            }

            for (var g = 0; g < groupCount; g++)
            {
                result[g] = SafeNormalize(result[g], Vector3.up);
            }

            LaplacianSmoothGroups(groupCount, neighborOffsets, neighborIndices, smoothIterations, result, scratch);
        }

        /// <summary>
        /// 現在の各頂点のワールド法線から、結合グループ内の一致化＋隣接グループとの平均化を行い、
        /// なじませ後のワールド法線をグループ単位で result へ書き出す。
        /// </summary>
        internal static void ComputeSmoothedGroupNormals(
            CrossMeshTopology topology,
            IReadOnlyList<Vector3[]> currentWorldNormals,
            Vector3[] result,
            int iterations = DefaultSmoothIterations)
        {
            if (topology == null || topology.GroupCount == 0 || result == null) return;

            var groupCount = topology.GroupCount;
            Array.Clear(result, 0, groupCount);

            // 1. 同一結合グループ（UVシーム・頭と体の首の境界頂点）の法線を合算して単一化する
            for (var m = 0; m < currentWorldNormals.Count && m < topology.VertexToGroup.Length; m++)
            {
                var normals = currentWorldNormals[m];
                var map = topology.VertexToGroup[m];
                if (normals == null || map == null) continue;

                var count = Mathf.Min(normals.Length, map.Length);
                for (var i = 0; i < count; i++)
                {
                    result[map[i]] += normals[i];
                }
            }

            for (var g = 0; g < groupCount; g++)
            {
                result[g] = SafeNormalize(result[g], topology.SmoothGeometricNormals[g]);
            }

            // 2. 隣接グループ間でラプラシアン平均化する
            LaplacianSmoothGroups(
                groupCount,
                topology.NeighborOffsets,
                topology.NeighborIndices,
                iterations,
                result,
                topology.SmoothedBufferB);
        }

        private static void LaplacianSmoothGroups(
            int groupCount,
            int[] neighborOffsets,
            int[] neighborIndices,
            int iterations,
            Vector3[] buffer,
            Vector3[] scratch)
        {
            if (iterations <= 0 || neighborOffsets == null || neighborIndices == null) return;

            for (var iter = 0; iter < iterations; iter++)
            {
                for (var g = 0; g < groupCount; g++)
                {
                    var current = buffer[g];
                    var start = neighborOffsets[g];
                    var end = neighborOffsets[g + 1];
                    if (start >= end)
                    {
                        scratch[g] = current;
                        continue;
                    }

                    var neighborSum = Vector3.zero;
                    for (var k = start; k < end; k++)
                    {
                        neighborSum += buffer[neighborIndices[k]];
                    }

                    // 自身の向きと隣接平均を 1:1 でブレンドして正規化する（縮小や振動を防ぐ）
                    var neighborAvg = neighborSum * (1f / (end - start));
                    scratch[g] = SafeNormalize(current + neighborAvg, current);
                }

                Array.Copy(scratch, buffer, groupCount);
            }
        }

        /// <summary>
        /// 単位ベクトル from から to へ重み t (0..1) で球面線形補間する。
        /// </summary>
        internal static Vector3 SlerpNormal(Vector3 from, Vector3 to, float t)
        {
            t = Mathf.Clamp01(t);
            if (t <= 0f) return from;
            if (t >= 1f) return to;

            from = SafeNormalize(from, Vector3.up);
            to = SafeNormalize(to, from);

            var dot = Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f);

            // 角度がごく小さい場合は正規化線形補間で十分かつ数値的に安定
            if (dot > 0.9995f)
            {
                return SafeNormalize(Vector3.Lerp(from, to, t), to);
            }

            // ほぼ正反対（180度）の場合は直交軸経由で回転する
            if (dot < -0.9995f)
            {
                var ortho = Vector3.Cross(from, Mathf.Abs(from.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                var angle = Mathf.PI * t;
                return from * Mathf.Cos(angle) + ortho * Mathf.Sin(angle);
            }

            var theta = Mathf.Acos(dot);
            var sinTheta = Mathf.Sin(theta);
            if (sinTheta <= 1e-6f)
            {
                return SafeNormalize(Vector3.Lerp(from, to, t), to);
            }

            var w0 = Mathf.Sin((1f - t) * theta) / sinTheta;
            var w1 = Mathf.Sin(t * theta) / sinTheta;
            return SafeNormalize(from * w0 + to * w1, to);
        }

        /// <summary>
        /// 単位ベクトル from を to へ重ねる最小回転を v に適用する（Rodrigues の回転公式）。
        /// </summary>
        internal static Vector3 RotateVector(Vector3 from, Vector3 to, Vector3 v)
        {
            from = SafeNormalize(from, Vector3.up);
            to = SafeNormalize(to, from);

            var cos = Vector3.Dot(from, to);
            var axis = Vector3.Cross(from, to);
            var sin = axis.magnitude;

            if (sin <= 1e-7f)
            {
                if (cos >= 0f) return v;
                var perpendicular = Vector3.Cross(from, Mathf.Abs(from.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                return 2f * Vector3.Dot(perpendicular, v) * perpendicular - v;
            }

            var k = axis / sin;
            return v * cos + Vector3.Cross(k, v) * sin + k * (Vector3.Dot(k, v) * (1f - cos));
        }

        internal static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            var sqr = v.sqrMagnitude;
            if (sqr > 1e-12f) return v * (1f / Mathf.Sqrt(sqr));

            var fbSqr = fallback.sqrMagnitude;
            return fbSqr > 1e-12f ? fallback * (1f / Mathf.Sqrt(fbSqr)) : Vector3.up;
        }
    }
}

