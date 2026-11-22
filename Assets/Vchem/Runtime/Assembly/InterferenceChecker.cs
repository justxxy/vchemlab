using System;
using System.Collections.Generic;
using UnityEngine;
using Vchem.Authoring.Geometry;
using Vchem.Core.Model;

namespace Vchem.Assembly
{
    /// <summary>
    /// 廉价穿插检测：不使用物理引擎。每件器材由 2~N 个球近似，
    /// 已连接端口所在的一对节点登记为允许接触，其余按 1mm 阈值报重叠。
    /// </summary>
    public static class InterferenceChecker
    {
        private struct Sphere
        {
            public Vector3 Center;
            public float Radius;
            public string Part;
        }

        private const float ThresholdM = 0.001f;

        public static void Check(Plan plan, AssemblyResult result)
        {
            result.Interference.Clear();
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            if (plan != null && plan.Topology != null)
            {
                var edges = plan.Topology.Edges;
                foreach (Edge e in edges)
                {
                    allowed.Add(PairKey(Edge.NodeOf(e.From), Edge.NodeOf(e.To)));
                    allowed.Add(PairKey(Edge.NodeOf(e.To), Edge.NodeOf(e.From)));
                }
                // 同一端口上的两条边（漏斗 + 导管都插在瓶口）必然靠近，登记为允许接触。
                for (int i = 0; i < edges.Count; i++)
                    for (int j = i + 1; j < edges.Count; j++)
                    {
                        string shared = SharedEndpoint(edges[i], edges[j]);
                        if (string.IsNullOrEmpty(shared)) continue;
                        string ai = OtherNode(edges[i], shared), aj = OtherNode(edges[j], shared);
                        allowed.Add(PairKey(ai, aj));
                    }
            }

            var spheres = new List<List<Sphere>>();
            foreach (AssemblyNode n in result.Nodes) spheres.Add(BuildSpheres(n));

            for (int i = 0; i < result.Nodes.Count; i++)
            {
                for (int j = i + 1; j < result.Nodes.Count; j++)
                {
                    AssemblyNode a = result.Nodes[i], b = result.Nodes[j];
                    if (allowed.Contains(PairKey(a.Id, b.Id))) continue;
                    foreach (Sphere sa in spheres[i])
                    foreach (Sphere sb in spheres[j])
                    {
                        float overlap = sa.Radius + sb.Radius - Vector3.Distance(sa.Center, sb.Center);
                        if (overlap > ThresholdM)
                        {
                            result.Interference.Add(new InterferenceHit
                            {
                                A = a.Id, B = b.Id, PartA = sa.Part, PartB = sb.Part,
                                OverlapMm = overlap * 1000f
                            });
                        }
                    }
                }
            }
        }

        private static string PairKey(string a, string b)
        {
            return string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
        }

        private static string SharedEndpoint(Edge a, Edge b)
        {
            foreach (string p in new[] { a.From, a.To })
                if (string.Equals(p, b.From, StringComparison.Ordinal)
                    || string.Equals(p, b.To, StringComparison.Ordinal)) return p;
            return "";
        }

        private static string OtherNode(Edge e, string endpoint)
        {
            return string.Equals(e.From, endpoint, StringComparison.Ordinal)
                ? Edge.NodeOf(e.To) : Edge.NodeOf(e.From);
        }

        private static List<Sphere> BuildSpheres(AssemblyNode node)
        {
            var list = new List<Sphere>();
            VesselProfile p = node.Profile;
            if (p == null) return list;
            float s = node.Pose.Scale;

            if (p.kind == "sweep")
            {
                // profile 长度一律是 mm；Pose.Scale 只负责 local→m，半径不能再乘一次。
                float r = Mathf.Max(0.004f, p.outerRadius * 0.001f * 1.25f);
                int step = Mathf.Max(1, node.PathWorld.Count / 10);
                for (int i = 0; i < node.PathWorld.Count; i += step)
                    list.Add(new Sphere { Center = node.PathWorld[i], Radius = r, Part = "tube" });
                if (node.PathWorld.Count > 0)
                {
                    int last = node.PathWorld.Count - 1;
                    list.Add(new Sphere { Center = node.PathWorld[last], Radius = r, Part = "tubeEnd" });
                }
                return list;
            }

            if (p.kind == "lathe")
            {
                float bodyR = Mathf.Max(0.006f, ProfileCatalog.FootprintRadius(p) * 0.001f * 0.78f);
                float unitMm = ProfileCatalog.LocalUnitMmStatic(p);
                Vector3 bodyCenter = node.Pose.Position
                    + node.Pose.Rotation * (new Vector3(0f, p.height * 0.35f / unitMm, 0f) * s);
                list.Add(new Sphere { Center = bodyCenter, Radius = bodyR, Part = "body" });

                foreach (var kv in p.anchors)
                {
                    if (kv.Key == "bottom") continue;
                    float rMm = kv.Value.r > 0f ? kv.Value.r : ProfileCatalog.FootprintRadius(p) * 0.35f;
                    float r = Mathf.Max(0.006f, rMm * 0.001f * 1.15f);
                    list.Add(new Sphere
                    {
                        Center = node.AnchorWorld(kv.Key, Vector3.zero),
                        Radius = r,
                        Part = kv.Key
                    });
                }
                return list;
            }

            // 水槽 / 铁架台：用一个包围球近似，配置里 footprint 已保守放大。
            float radius = Mathf.Max(0.03f, ProfileCatalog.FootprintRadius(p) * 0.001f * 0.62f);
            Vector3 c = node.Pose.Position + node.Pose.Rotation * (new Vector3(0f, 0.4f, 0f) * s);
            list.Add(new Sphere { Center = c, Radius = radius, Part = "solid" });
            return list;
        }
    }
}
