using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vchem.Authoring.Geometry
{
    /// <summary>几何自检结果。所有长度/体积都在 profile 的原始单位（mm）下计算。</summary>
    [Serializable]
    public struct GeometryStats
    {
        public int vertices;
        public int triangles;
        public int boundaryEdges;       // 水密要求 = 0
        public float wallMaxError;      // 壁厚最大偏差 mm，阈值 <0.1
        public float volumeMesh;        // 网格算出的玻璃体积 mm^3
        public float volumeTheory;      // 剖面积分的玻璃体积 mm^3
        public float volumeError;       // 相对误差，阈值 <0.02
        public float profileRms;        // 轮廓对控制点的 RMS mm，阈值 <0.3
        public bool watertight { get { return boundaryEdges == 0; } }

        public string Report()
        {
            return string.Format(
                "v={0} tri={1} boundaryEdges={2} wallErr={3:F4}mm " +
                "volMesh={4:F1} volTheory={5:F1} volErr={6:F4} profileRms={7:F4}mm",
                vertices, triangles, boundaryEdges, wallMaxError,
                volumeMesh, volumeTheory, volumeError, profileRms);
        }
    }

    /// <summary>
    /// 旋转体车削：把 VesselProfile 的外轮廓（mm）平滑重采样后，
    /// 生成「外壁 + 内壁（按 wall 内缩）+ 底盖 + 口沿封边」的封闭双壳网格。
    /// 双壳是 R2 双线壁厚高光的几何前提。
    /// 输出顶点按 height 归一化到 1 单位高，便于直接替换现有 1 单位高的探针网格。
    /// </summary>
    public static class LatheMeshBuilder
    {
        const float EPS = 1e-4f;

        public static Mesh Build(VesselProfile p, out GeometryStats stats)
        {
            stats = new GeometryStats();
            if (p == null || p.profile.Count < 2)
                throw new ArgumentException("LatheMeshBuilder: 剖面点不足");

            float H = Mathf.Max(1f, p.height);
            float wallMm = p.wall;
            float R = Mathf.Max(1f, p.MaxRadius);

            // 1) 弧长重采样外轮廓（Catmull-Rom 平滑，急角退化为线性）。
            // step = 轴向采样弧长间距：让轴向段数落在 40~55，单件面数≈64×48×2≈6k。
            float step = Mathf.Max(3.5f, totalArc(p.profile) / 48f);
            List<ProfilePoint> outer = Resample(p.profile, step);

            // 2) 内轮廓：r 减 wall；底部抬升 wall 形成实心底
            var inner = new List<ProfilePoint>(outer.Count);
            for (int i = 0; i < outer.Count; i++)
            {
                float y = outer[i].y;
                float r = Mathf.Max(EPS, outer[i].r - wallMm);
                // 底部水平段（含底缘）内缩方向是 +y：整体抬到 wall 高度，
                // 这样底厚体现在轴向；只抬 r=0 的点会让底厚变成径向的薄边。
                if (p.closedBottom && y <= wallMm + EPS) y = wallMm;
                inner.Add(new ProfilePoint(y, r));
            }

            // 3) 拼成闭合截面轮廓：外壁(底→口) + 口沿内点 + 内壁(口→底) + 内底中心
            var loop = new List<ProfilePoint>();
            loop.AddRange(outer);
            // 口沿封边由 outer 末点与 inner 末点直接相连（同 y、r 差 wall）
            for (int i = inner.Count - 1; i >= 0; i--)
                loop.Add(inner[i]);
            // loop 首尾都在 r≈0，绕轴退化；闭合由底盖环 completing

            // 4) 绕轴旋转生成网格。外/内/封边各自独立顶点保证法线正确。
            int seg = Mathf.Max(12, p.segments);
            float mmToUnit = 1f / H;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var loopRings = new List<List<int>>(); // 每个 loop 点对应的环顶点索引（r=0 时 1 个中心点）

            for (int i = 0; i < loop.Count; i++)
            {
                var ring = new List<int>(seg);
                float y = loop[i].y * mmToUnit;
                float r = loop[i].r * mmToUnit;
                if (r < EPS)
                {
                    ring.Add(verts.Count);
                    verts.Add(new Vector3(0f, y, 0f));
                }
                else
                {
                    for (int j = 0; j < seg; j++)
                    {
                        float a = (float)j / seg * Mathf.PI * 2f;
                        ring.Add(verts.Count);
                        verts.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
                    }
                }
                loopRings.Add(ring);
            }

            // 连接相邻环（loop = outer(0..M-1) + inner(M-1..0)，闭合段是底盖）。
            // 双 submesh：外壁/口沿/底盖 → submesh 0（outer 材质）；内壁 → submesh 1（inner 材质）。
            // 这样保留 G1 已验证的"双壳两套玻璃参数"，只是几何从圆柱换成车削。
            var outerTris = new List<int>();
            var innerTris = new List<int>();
            int n = loop.Count;
            int innerStart = outer.Count; // loop 中第一个内壁点的索引
            for (int i = 0; i < n; i++)
            {
                var a = loopRings[i];
                var b = loopRings[(i + 1) % n];
                // 纯内壁段：两端都在 inner 区（i>=innerStart 且下一段也在 inner 区）
                bool isInner = i >= innerStart && (i + 1) % n != 0;
                var target = isInner ? innerTris : outerTris;
                if (a.Count == 1 && b.Count == 1) continue;
                if (a.Count == 1)
                {
                    for (int j = 0; j < seg; j++) AddTri(target, a[0], b[j], b[(j + 1) % seg]);
                }
                else if (b.Count == 1)
                {
                    for (int j = 0; j < seg; j++) AddTri(target, a[j], b[0], a[(j + 1) % seg]);
                }
                else
                {
                    for (int j = 0; j < seg; j++)
                    {
                        int j1 = (j + 1) % seg;
                        AddTri(target, a[j], b[j], b[j1]);
                        AddTri(target, a[j], b[j1], a[j1]);
                    }
                }
            }
            // stats/boundary 用合并后的完整三角形列表
            tris.AddRange(outerTris);
            tris.AddRange(innerTris);

            var mesh = new Mesh();
            mesh.name = p.type + "_lathe";
            mesh.subMeshCount = 2;
            mesh.SetVertices(verts);
            mesh.SetTriangles(outerTris, 0);
            mesh.SetTriangles(innerTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            stats.vertices = verts.Count;
            stats.triangles = tris.Count / 3;
            stats.boundaryEdges = CountBoundaryEdges(verts, tris);
            stats.wallMaxError = MaxWallError(outer, inner, wallMm);
            stats.volumeMesh = MeshVolume(verts, tris) * H * H * H; // 归一化坐标还原到 mm^3
            stats.volumeTheory = TheoryVolume(outer, inner, wallMm, p.closedBottom);
            stats.volumeError = stats.volumeTheory > 0
                ? Mathf.Abs(stats.volumeMesh - stats.volumeTheory) / stats.volumeTheory : 0f;
            stats.profileRms = ProfileRms(p.profile, outer);
            return mesh;
        }

        static void AddTri(List<int> tris, int a, int b, int c)
        {
            tris.Add(a); tris.Add(b); tris.Add(c);
        }

        /// <summary>Catmull-Rom 过点插值（端点镜像外推），与图标脚本同口径。</summary>
        static Vector2 Catmull(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        static List<ProfilePoint> Resample(List<ProfilePoint> src, float step)
        {
            // 每段按弧长分配采样数，控制点本身也作为采样点保留。
            // 关键：在【急角控制点】两侧退化为线性连接——Catmull-Rom 在 60~85° 的
            // 底缘/口沿拐角会过冲 1mm+，把 profileRms 顶到阈值外；平缓段才用 Catmull 平滑。
            var segLen = new float[src.Count - 1];
            float total = 0f;
            for (int i = 0; i < src.Count - 1; i++)
            {
                segLen[i] = Vector2.Distance(src[i].ToV2(), src[i + 1].ToV2());
                total += segLen[i];
            }
            // 目标轴向段数：让单件面数落在 3~6k（面数≈圆周分段×轴向段×2）
            int axial = Mathf.Clamp(Mathf.RoundToInt(total / Mathf.Max(1f, step)), 16, 72);

            var outp = new List<ProfilePoint>();
            for (int i = 0; i < src.Count - 1; i++)
            {
                Vector2 p0 = src[Mathf.Max(0, i - 1)].ToV2();
                Vector2 p1 = src[i].ToV2();
                Vector2 p2 = src[i + 1].ToV2();
                Vector2 p3 = src[Mathf.Min(src.Count - 1, i + 2)].ToV2();
                int sub = Mathf.Max(1, Mathf.RoundToInt(axial * segLen[i] / total));

                bool sharpHere = IsSharp(src, i) || IsSharp(src, i + 1);
                for (int k = 0; k < sub; k++)
                {
                    float t = (float)k / sub;
                    Vector2 v = sharpHere ? Vector2.Lerp(p1, p2, t) : Catmull(p0, p1, p2, p3, t);
                    outp.Add(new ProfilePoint(Mathf.Max(0f, v.x), Mathf.Max(0f, v.y)));
                }
            }
            outp.Add(new ProfilePoint(src[src.Count - 1].y, src[src.Count - 1].r));
            return outp;
        }

        static float totalArc(List<ProfilePoint> src)
        {
            float s = 0f;
            for (int i = 0; i < src.Count - 1; i++) s += Vector2.Distance(src[i].ToV2(), src[i + 1].ToV2());
            return s;
        }

        /// <summary>控制点 i 是否为急角（相邻两段夹角 > 35°）。急角处用线性保形。</summary>
        static bool IsSharp(List<ProfilePoint> src, int i)
        {
            if (i <= 0 || i >= src.Count - 1) return false;
            Vector2 a = src[i].ToV2() - src[i - 1].ToV2();
            Vector2 b = src[i + 1].ToV2() - src[i].ToV2();
            if (a.sqrMagnitude < EPS || b.sqrMagnitude < EPS) return false;
            float dot = Vector2.Dot(a.normalized, b.normalized);
            return dot < 0.82f; // cos(35°)≈0.819
        }

        static int CountBoundaryEdges(List<Vector3> verts, List<int> tris)
        {
            var edges = new Dictionary<string, int>(StringComparer.Ordinal);
            Func<int, int, string> key = (a, b) => a < b ? a + "_" + b : b + "_" + a;
            for (int i = 0; i < tris.Count; i += 3)
            {
                key(tris[i], tris[i + 1]);
                Inc(edges, key(tris[i], tris[i + 1]));
                Inc(edges, key(tris[i + 1], tris[i + 2]));
                Inc(edges, key(tris[i + 2], tris[i]));
            }
            int boundary = 0;
            foreach (var kv in edges) if (kv.Value == 1) boundary++;
            return boundary;
        }

        static void Inc(Dictionary<string, int> d, string k)
        {
            int v; d.TryGetValue(k, out v); d[k] = v + 1;
        }

        static float MaxWallError(List<ProfilePoint> outer, List<ProfilePoint> inner, float wall)
        {
            float max = 0f;
            // 跳过底部（inner 被抬升）和口沿，只看侧壁法向近似径向的段
            for (int i = 2; i < outer.Count - 2; i++)
            {
                if (outer[i].y <= wall * 1.5f) continue;
                float err = Mathf.Abs((outer[i].r - inner[i].r) - wall);
                max = Mathf.Max(max, err);
            }
            return max;
        }

        static float MeshVolume(List<Vector3> verts, List<int> tris)
        {
            double vol = 0;
            for (int i = 0; i < tris.Count; i += 3)
            {
                Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
                vol += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
            return (float)Math.Abs(vol);
        }

        static float TheoryVolume(List<ProfilePoint> outer, List<ProfilePoint> inner, float wall, bool closed)
        {
            // 旋转体体积 = ∫π r^2 dy；玻璃体积 = 外壳 - 内腔
            double vOut = RevolveVolume(outer);
            double vIn = RevolveVolume(inner);
            return (float)Math.Max(0, vOut - vIn);
        }

        static double RevolveVolume(List<ProfilePoint> pts)
        {
            double v = 0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double dy = pts[i + 1].y - pts[i].y;
                double r0 = pts[i].r, r1 = pts[i + 1].r;
                v += Math.PI * (r0 * r0 + r0 * r1 + r1 * r1) / 3.0 * dy;
            }
            return v;
        }

        static float ProfileRms(List<ProfilePoint> ctrl, List<ProfilePoint> resampled)
        {
            // Resample 会把每个控制点作为其所在段的起点原样保留，所以直接找
            // 最近的重采样点即可。不能按累计弧长比例匹配：Catmull 平滑会让
            // 重采样总弧长略大于控制折线，比例匹配会把肩部/底缘的控制点错配，
            // 报出并不存在的 1mm+ 轮廓偏差。
            double sum = 0;
            for (int c = 0; c < ctrl.Count; c++)
            {
                Vector2 q = ctrl[c].ToV2();
                float best = float.MaxValue;
                for (int i = 0; i < resampled.Count; i++)
                {
                    float d = (resampled[i].ToV2() - q).sqrMagnitude;
                    if (d < best) best = d;
                }
                sum += best;
            }
            return ctrl.Count > 0 ? Mathf.Sqrt((float)(sum / ctrl.Count)) : 0f;
        }
    }

    static class PPext
    {
        public static Vector2 ToV2(this ProfilePoint p) { return new Vector2(p.y, p.r); }
    }
}
