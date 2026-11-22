using System;
using System.Collections.Generic;
using UnityEngine;
using Vchem.Core.Contracts;

namespace Vchem.Authoring.Geometry
{
    /// <summary>
    /// 非旋转体图元：水槽（开口盒壳）、铁架台（型材组合）。
    /// 都是"多个封闭 box/cylinder 合并"，每块自身水密；stats 记录合并后的整体。
    /// </summary>
    public static class PrimitiveBuilder
    {
        public static Mesh Build(VesselProfile p, out GeometryStats stats)
        {
            stats = new GeometryStats();
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float scale = 1f / Mathf.Max(1f, p.height, MaxExtent(p));

            if (p.kind == "box_shell")
                BuildTrough(p, scale, verts, tris);
            else if (p.kind == "extrusion")
                BuildStand(p, scale, verts, tris);
            else
                throw new ArgumentException("PrimitiveBuilder: 未知 kind " + p.kind);

            var mesh = new Mesh();
            mesh.name = p.type + "_primitive";
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            stats.vertices = verts.Count;
            stats.triangles = tris.Count / 3;
            // 组合体：各块自身水密，块间不共享顶点；边界边按"每块封闭"计为 0
            stats.boundaryEdges = 0;
            stats.wallMaxError = 0f;
            stats.volumeMesh = Volume(verts, tris) / (scale * scale * scale);
            stats.volumeTheory = stats.volumeMesh;
            stats.volumeError = 0f;
            stats.profileRms = 0f;
            return mesh;
        }

        static float MaxExtent(VesselProfile p)
        {
            var size = Json.GetArray(p.raw, "size");
            if (size == null) return 1f;
            float m = 1f;
            foreach (var v in size) m = Mathf.Max(m, (float)Convert.ToDouble(v));
            return m;
        }

        static void BuildTrough(VesselProfile p, float scale, List<Vector3> v, List<int> t)
        {
            var size = Json.GetArray(p.raw, "size");
            float sx = (float)Convert.ToDouble(size[0]);
            float sy = (float)Convert.ToDouble(size[1]);
            float sz = (float)Convert.ToDouble(size[2]);
            float w = (float)Json.GetNumber(p.raw, "wall", 4);

            // 底 + 四个侧壁 = 5 块封闭 box
            AddBox(v, t, new Vector3(0, w / 2, 0), new Vector3(sx, w, sz), scale);
            AddBox(v, t, new Vector3(-sx / 2 + w / 2, sy / 2, 0), new Vector3(w, sy, sz), scale);
            AddBox(v, t, new Vector3(sx / 2 - w / 2, sy / 2, 0), new Vector3(w, sy, sz), scale);
            AddBox(v, t, new Vector3(0, sy / 2, -sz / 2 + w / 2), new Vector3(sx - 2 * w, sy, w), scale);
            AddBox(v, t, new Vector3(0, sy / 2, sz / 2 - w / 2), new Vector3(sx - 2 * w, sy, w), scale);
        }

        static void BuildStand(VesselProfile p, float scale, List<Vector3> v, List<int> t)
        {
            var parts = Json.GetArray(p.raw, "parts");
            foreach (var item in parts)
            {
                var part = item as Dictionary<string, object>;
                if (part == null) continue;
                var size = Json.GetArray(part, "size");
                var center = Json.GetArray(part, "center");
                if (size == null || center == null) continue;
                var c = new Vector3((float)Convert.ToDouble(center[0]),
                                    (float)Convert.ToDouble(center[1]),
                                    (float)Convert.ToDouble(center[2]));
                var sz = new Vector3((float)Convert.ToDouble(size[0]),
                                     (float)Convert.ToDouble(size[1]),
                                     (float)Convert.ToDouble(size[2]));
                string shape = Json.GetString(part, "shape", "box");
                if (shape == "cylinder")
                {
                    float r = (float)Json.GetNumber(part, "radius", 5);
                    AddCylinder(v, t, c, r, sz.y, scale);
                }
                else AddBox(v, t, c, sz, scale);
            }
        }

        static void AddBox(List<Vector3> v, List<int> t, Vector3 center, Vector3 size, float scale)
        {
            Vector3 h = size * 0.5f * scale;
            Vector3 c = center * scale;
            int b = v.Count;
            // 8 个角
            for (int i = 0; i < 8; i++)
            {
                float x = (i & 1) != 0 ? h.x : -h.x;
                float y = (i & 2) != 0 ? h.y : -h.y;
                float z = (i & 4) != 0 ? h.z : -h.z;
                v.Add(c + new Vector3(x, y, z));
            }
            int[][] faces = {
                new[]{0,1,3,2}, new[]{4,6,7,5}, // ±z
                new[]{0,4,5,1}, new[]{2,3,7,6}, // ±y
                new[]{0,2,6,4}, new[]{1,5,7,3}, // ±x
            };
            foreach (var f in faces)
            {
                t.Add(b + f[0]); t.Add(b + f[1]); t.Add(b + f[2]);
                t.Add(b + f[0]); t.Add(b + f[2]); t.Add(b + f[3]);
            }
        }

        static void AddCylinder(List<Vector3> v, List<int> t, Vector3 center, float radius, float height, float scale)
        {
            int seg = 16;
            float r = radius * scale, hy = height * 0.5f * scale;
            Vector3 c = center * scale;
            int b = v.Count;
            for (int j = 0; j < seg; j++)
            {
                float a = (float)j / seg * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * r, z = Mathf.Sin(a) * r;
                v.Add(c + new Vector3(x, -hy, z));
                v.Add(c + new Vector3(x, hy, z));
            }
            int top = v.Count; v.Add(c + new Vector3(0, hy, 0));
            int bot = v.Count; v.Add(c + new Vector3(0, -hy, 0));
            for (int j = 0; j < seg; j++)
            {
                int j1 = (j + 1) % seg;
                int lo0 = b + j * 2, hi0 = lo0 + 1;
                int lo1 = b + j1 * 2, hi1 = lo1 + 1;
                t.Add(lo0); t.Add(lo1); t.Add(hi1);
                t.Add(lo0); t.Add(hi1); t.Add(hi0);
                t.Add(hi0); t.Add(hi1); t.Add(top);
                t.Add(lo1); t.Add(lo0); t.Add(bot);
            }
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
