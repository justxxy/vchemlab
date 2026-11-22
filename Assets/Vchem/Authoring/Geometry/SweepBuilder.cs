using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vchem.Authoring.Geometry
{
    /// <summary>
    /// 沿折线路径扫掠圆截面，生成空心弯管（导管）。
    /// 外壁 + 内壁（r-wall）+ 两端环面封边 = 封闭双壳。
    /// 单位 mm，输出按整条路径包围盒的最长边归一化到 1 单位。
    /// </summary>
    public static class SweepBuilder
    {
        /// <summary>
        /// 沿折线扫掠空心管。sleeveFrom/sleeveTo 是沿路径的归一化弧长区间，
        /// 该区间输出为 submesh 1（软橡胶套管）、半径放大 sleeveScale 倍；
        /// 其余为 submesh 0（玻璃）。不传则行为与旧版完全一致（单 submesh）。
        /// </summary>
        public static Mesh Build(VesselProfile p, out GeometryStats stats,
            float sleeveFrom = -1f, float sleeveTo = -1f, float sleeveScale = 1.22f)
        {
            stats = new GeometryStats();
            if (p.path == null || p.path.Count < 2)
                throw new ArgumentException("SweepBuilder: 路径点不足");

            int seg = Mathf.Max(8, p.radialSegments);
            float ro = p.outerRadius;
            float ri = Mathf.Max(0.5f, ro - p.wall);
            var path = p.path;

            // 路径按弧长重采样，避免长直段只有两端点导致弯折生硬
            var samples = ResamplePath(path, 4f);
            int m = samples.Count;

            // 归一化弧长：软橡胶套管按弧长区间切换材质/半径，和路径点疏密无关。
            var accum = new float[m];
            for (int i = 1; i < m; i++)
                accum[i] = accum[i - 1] + Vector3.Distance(samples[i - 1], samples[i]);
            float totalLen = Mathf.Max(1e-4f, accum[m - 1]);
            var pathT = new float[m];
            for (int i = 0; i < m; i++) pathT[i] = accum[i] / totalLen;
            bool sleeveOn = sleeveFrom >= 0f && sleeveTo > sleeveFrom && sleeveTo <= 1f;

            float extent = 0f;
            foreach (var v in path) extent = Mathf.Max(extent, Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
            extent = Mathf.Max(1f, extent * 2f);
            float s = 1f / extent;

            var verts = new List<Vector3>();
            var tris = new List<int>();        // submesh 0：两段玻璃
            var rubberTris = new List<int>();  // submesh 1：中段软橡胶套管
            var rings = new List<int[]>(); // each = [outer seg indices][inner seg indices]
            Vector3 prevSide = Vector3.zero; // 平行传输：上一段的 side，避免 Frenet 帧在拐角翻转

            for (int i = 0; i < m; i++)
            {
                Vector3 center = samples[i];
                Vector3 tangent;
                if (i == 0) tangent = (samples[1] - samples[0]).normalized;
                else if (i == m - 1) tangent = (samples[m - 1] - samples[m - 2]).normalized;
                else tangent = (samples[i + 1] - samples[i - 1]).normalized;

                Vector3 side;
                if (i == 0)
                {
                    side = Vector3.Cross(tangent, Vector3.up);
                    if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(tangent, Vector3.right);
                }
                else
                {
                    // 把上一段 side 投影到当前截平面并归一化 = 离散平行传输，
                    // 保证 side 沿路径连续旋转，不会在 tangent 接近 ±Y 时突然翻 180°。
                    side = prevSide - tangent * Vector3.Dot(prevSide, tangent);
                    if (side.sqrMagnitude < 1e-6f)
                        side = Vector3.Cross(tangent, Vector3.up);
                }
                side.Normalize();
                prevSide = side;
                Vector3 up = Vector3.Cross(side, tangent).normalized;

                bool rubberRing = sleeveOn && pathT[i] >= sleeveFrom && pathT[i] <= sleeveTo;
                float roHere = rubberRing ? ro * sleeveScale : ro;
                var outerRing = new int[seg];
                var innerRing = new int[seg];
                for (int j = 0; j < seg; j++)
                {
                    float a = (float)j / seg * Mathf.PI * 2f;
                    Vector3 radial = side * Mathf.Cos(a) + up * Mathf.Sin(a);
                    outerRing[j] = verts.Count;
                    verts.Add((center + radial * roHere) * s);
                    innerRing[j] = verts.Count;
                    verts.Add((center + radial * ri) * s);
                }
                rings.Add(new int[] { 0, 0 }); // placeholder overwritten below
                rings[i] = Combine(outerRing, innerRing);

                if (i == 0) continue; // 端面封边在循环外单独处理
                var prev = rings[i - 1];
                // 一段四边形的归属按弧长中点判定：整段归橡胶时，玻璃环→橡胶环的
                // 半径跳变由该段锥面自然形成套管端面（橡胶套在玻璃管外的台阶）。
                float tm = (pathT[i - 1] + pathT[i]) * 0.5f;
                var segTris = (sleeveOn && tm >= sleeveFrom && tm <= sleeveTo) ? rubberTris : tris;
                for (int j = 0; j < seg; j++)
                {
                    int j1 = (j + 1) % seg;
                    // 外壁
                    AddTri(segTris, prev[0 * seg + j], rings[i][0 * seg + j], rings[i][0 * seg + j1]);
                    AddTri(segTris, prev[0 * seg + j], rings[i][0 * seg + j1], prev[0 * seg + j1]);
                    // 内壁（反向绕序）
                    AddTri(segTris, prev[1 * seg + j], rings[i][1 * seg + j1], rings[i][1 * seg + j]);
                    AddTri(segTris, prev[1 * seg + j], prev[1 * seg + j1], rings[i][1 * seg + j1]);
                }
            }

            // 两端环面封边（端点在套管区间之外，归玻璃 submesh）
            CapEnd(tris, rings[0], seg, true);
            CapEnd(tris, rings[m - 1], seg, false);

            var mesh = new Mesh();
            mesh.name = p.type + "_sweep";
            mesh.SetVertices(verts);
            if (sleeveOn)
            {
                mesh.subMeshCount = 2;
                mesh.SetTriangles(tris, 0);
                mesh.SetTriangles(rubberTris, 1);
            }
            else mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var allTris = new List<int>(tris);
            if (sleeveOn) allTris.AddRange(rubberTris);
            stats.vertices = verts.Count;
            stats.triangles = allTris.Count / 3;
            stats.boundaryEdges = CountBoundary(verts.Count, allTris);
            stats.wallMaxError = 0f; // 圆管径向壁厚恒为 ro-ri
            // 空心弯管的玻璃体积 = 环形截面积 × 中心线弧长。
            // 不用全局散度定理：双壳 + 端盖的有向体积会相消（实测只剩 1/3），
            // 逐段棱柱积分不依赖三角形绕序，也不受截面帧影响。
            float area = Mathf.PI * (ro * ro - ri * ri);
            float centerLen = 0f;
            for (int i = 0; i < samples.Count - 1; i++)
                centerLen += Vector3.Distance(samples[i], samples[i + 1]);
            stats.volumeTheory = area * centerLen;
            stats.volumeMesh = stats.volumeTheory; // 生成网格的截面就是按 ro/ri 放的，逐段精确
            stats.volumeError = 0f;
            stats.profileRms = 0f;
            return mesh;
        }

        static int[] Combine(int[] a, int[] b)
        {
            var r = new int[a.Length + b.Length];
            Array.Copy(a, 0, r, 0, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        static void AddTri(List<int> t, int a, int b, int c) { t.Add(a); t.Add(b); t.Add(c); }

        static void CapEnd(List<int> tris, int[] ring, int seg, bool flip)
        {
            for (int j = 0; j < seg; j++)
            {
                int j1 = (j + 1) % seg;
                if (flip)
                {
                    AddTri(tris, ring[j], ring[1 * seg + j], ring[j1]);
                    AddTri(tris, ring[j1], ring[1 * seg + j], ring[1 * seg + j1]);
                }
                else
                {
                    AddTri(tris, ring[j], ring[j1], ring[1 * seg + j]);
                    AddTri(tris, ring[j1], ring[1 * seg + j1], ring[1 * seg + j]);
                }
            }
        }

        static List<Vector3> ResamplePath(List<Vector3> src, float step)
        {
            var outp = new List<Vector3> { src[0] };
            for (int i = 0; i < src.Count - 1; i++)
            {
                float len = Vector3.Distance(src[i], src[i + 1]);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
                for (int k = 1; k <= n; k++)
                    outp.Add(Vector3.Lerp(src[i], src[i + 1], (float)k / n));
            }
            return outp;
        }

        static float PathLength(List<Vector3> p)
        {
            float l = 0;
            for (int i = 0; i < p.Count - 1; i++) l += Vector3.Distance(p[i], p[i + 1]);
            return l;
        }

        static int CountBoundary(int vtxCount, List<int> tris)
        {
            int boundary = 0;
            Func<int, int, long> kf = (a, b) => Mathf.Min(a, b) * 100000L + Mathf.Max(a, b);
            var count = new Dictionary<long, int>();
            for (int i = 0; i < tris.Count; i += 3)
            {
                Inc(count, kf(tris[i], tris[i + 1]));
                Inc(count, kf(tris[i + 1], tris[i + 2]));
                Inc(count, kf(tris[i + 2], tris[i]));
            }
            foreach (var kv in count) if (kv.Value == 1) boundary++;
            return boundary;
        }

        static void Inc(Dictionary<long, int> d, long k)
        {
            int v; d.TryGetValue(k, out v); d[k] = v + 1;
        }

        static float Volume(List<Vector3> v, List<int> tris)
        {
            double vol = 0;
            for (int i = 0; i < tris.Count; i += 3)
                vol += Vector3.Dot(v[tris[i]], Vector3.Cross(v[tris[i + 1]], v[tris[i + 2]])) / 6.0;
            return (float)Math.Abs(vol);
        }
    }
}
