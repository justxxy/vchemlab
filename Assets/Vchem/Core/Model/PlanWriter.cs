using System;
using System.Collections.Generic;
using Vchem.Core.Contracts;

namespace Vchem.Core.Model
{
    /// <summary>
    /// Plan → plan.json（契约 6.1）。UI 提交时把画布状态序列化为学生方案。
    /// 与 Plan.FromJson 对称，走同一套 Json.Stringify。
    /// </summary>
    public static class PlanWriter
    {
        public static string ToJson(Plan p)
        {
            if (p == null) return "{}";
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["schema"] = string.IsNullOrEmpty(p.Schema) ? "vchem.plan/1" : p.Schema;
            root["experimentId"] = string.IsNullOrEmpty(p.ExperimentId) ? "exp.co2.prepare" : p.ExperimentId;

            Dictionary<string, object> topo = new Dictionary<string, object>(StringComparer.Ordinal);
            List<object> nodes = new List<object>();
            if (p.Topology != null)
            {
                foreach (Node n in p.Topology.Nodes)
                {
                    Dictionary<string, object> no = new Dictionary<string, object>(StringComparer.Ordinal);
                    no["id"] = n.Id;
                    no["type"] = n.Type;
                    if (n.Props != null && n.Props.Count > 0)
                    {
                        Dictionary<string, object> props = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, object> kv in n.Props) props[kv.Key] = kv.Value;
                        no["props"] = props;
                    }
                    nodes.Add(no);
                }
                List<object> edges = new List<object>();
                foreach (Edge e in p.Topology.Edges)
                {
                    Dictionary<string, object> eo = new Dictionary<string, object>(StringComparer.Ordinal);
                    eo["from"] = e.From;
                    eo["to"] = e.To;
                    eo["kind"] = e.Kind;
                    if (e.Props != null && e.Props.Count > 0)
                    {
                        Dictionary<string, object> props = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, object> kv in e.Props) props[kv.Key] = kv.Value;
                        eo["props"] = props;
                    }
                    edges.Add(eo);
                }
                topo["nodes"] = nodes;
                topo["edges"] = edges;
            }
            root["topology"] = topo;

            Dictionary<string, object> ver = new Dictionary<string, object>(StringComparer.Ordinal);
            ver["fullness"] = p.Verification == null ? "none" : (p.Verification.Fullness ?? "none");
            ver["identity"] = p.Verification == null ? "none" : (p.Verification.Identity ?? "none");
            root["verification"] = ver;

            Dictionary<string, object> feeds = new Dictionary<string, object>(StringComparer.Ordinal);
            feeds["solid"] = p.Feeds == null ? "" : (p.Feeds.Solid ?? "");
            feeds["liquid"] = p.Feeds == null ? "" : (p.Feeds.Liquid ?? "");
            root["feeds"] = feeds;

            if (!string.IsNullOrEmpty(p.StudentId)) root["studentId"] = p.StudentId;
            if (!string.IsNullOrEmpty(p.SubmittedAt)) root["submittedAt"] = p.SubmittedAt;
            return Json.Stringify(root);
        }
    }
}
