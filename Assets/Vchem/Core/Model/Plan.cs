using System;
using System.Collections.Generic;
using Vchem.Core.Contracts;

namespace Vchem.Core.Model
{
    /// <summary>学生提交的方案（plan.json）。结构 + 检验栏 + 试剂槽。</summary>
    public sealed class Plan
    {
        public string Schema;
        public string ExperimentId;
        public Topology Topology;
        public Verification Verification;
        public Feeds Feeds;
        public string StudentId;
        public string SubmittedAt;

        public static Plan FromJson(string text)
        {
            Dictionary<string, object> root = Json.ParseObject(text);
            Plan p = new Plan();
            p.Schema = Json.GetString(root, "schema", "");
            p.ExperimentId = Json.GetString(root, "experimentId", "");
            p.StudentId = Json.GetString(root, "studentId", "");
            p.SubmittedAt = Json.GetString(root, "submittedAt", "");

            Dictionary<string, object> topo = Json.GetObject(root, "topology");
            p.Topology = topo == null ? new Topology() : Topology.FromJson(topo);

            Dictionary<string, object> ver = Json.GetObject(root, "verification");
            p.Verification = ver == null ? new Verification() : Verification.FromJson(ver);

            Dictionary<string, object> feeds = Json.GetObject(root, "feeds");
            p.Feeds = feeds == null ? new Feeds() : Feeds.FromJson(feeds);
            return p;
        }
    }

    /// <summary>装置图：节点与边。图必须是 DAG，节点 ≤ 8、边 ≤ 12。</summary>
    public sealed class Topology
    {
        public List<Node> Nodes = new List<Node>();
        public List<Edge> Edges = new List<Edge>();

        public static Topology FromJson(Dictionary<string, object> o)
        {
            Topology t = new Topology();
            List<object> nodes = Json.GetArray(o, "nodes");
            if (nodes != null)
                foreach (object n in nodes)
                    t.Nodes.Add(Node.FromJson(Json.AsObject(n)));
            List<object> edges = Json.GetArray(o, "edges");
            if (edges != null)
                foreach (object e in edges)
                    t.Edges.Add(Edge.FromJson(Json.AsObject(e)));
            return t;
        }

        public Node FindNode(string id)
        {
            foreach (Node n in Nodes)
                if (string.Equals(n.Id, id, StringComparison.Ordinal)) return n;
            return null;
        }
    }

    public sealed class Node
    {
        public string Id;
        public string Type;
        public Dictionary<string, object> Props = new Dictionary<string, object>(StringComparer.Ordinal);

        public static Node FromJson(Dictionary<string, object> o)
        {
            Node n = new Node();
            n.Id = Json.GetString(o, "id", "");
            n.Type = Json.GetString(o, "type", "");
            Dictionary<string, object> props = Json.GetObject(o, "props");
            if (props != null) n.Props = props;
            return n;
        }

        /// <summary>读取结构属性（字符串），如 orientation / covered / depth / submerged。</summary>
        public string Prop(string key, string fallback)
        {
            return Json.GetString(Props, key, fallback);
        }

        public bool PropBool(string key, bool fallback)
        {
            return Json.GetBool(Props, key, fallback);
        }
    }

    public sealed class Edge
    {
        /// <summary>起点，形如 "n2.bottom"。</summary>
        public string From;
        /// <summary>终点，形如 "n1.mouth"。</summary>
        public string To;
        /// <summary>边类型：gas / feed / water / support。</summary>
        public string Kind;
        public Dictionary<string, object> Props = new Dictionary<string, object>(StringComparer.Ordinal);

        public static Edge FromJson(Dictionary<string, object> o)
        {
            Edge e = new Edge();
            e.From = Json.GetString(o, "from", "");
            e.To = Json.GetString(o, "to", "");
            e.Kind = Json.GetString(o, "kind", "");
            Dictionary<string, object> props = Json.GetObject(o, "props");
            if (props != null) e.Props = props;
            return e;
        }

        public string Prop(string key, string fallback)
        {
            return Json.GetString(Props, key, fallback);
        }

        public static string NodeOf(string endpoint)
        {
            int dot = endpoint.IndexOf('.');
            return dot < 0 ? endpoint : endpoint.Substring(0, dot);
        }

        public static string PortOf(string endpoint)
        {
            int dot = endpoint.IndexOf('.');
            return dot < 0 ? "" : endpoint.Substring(dot + 1);
        }
    }

    /// <summary>检验栏：验满方式 + 检验项。石灰水固定，不占试剂槽。</summary>
    public sealed class Verification
    {
        /// <summary>burning_splint / bubble / none</summary>
        public string Fullness;
        /// <summary>limewater / none</summary>
        public string Identity;

        public static Verification FromJson(Dictionary<string, object> o)
        {
            Verification v = new Verification();
            v.Fullness = Json.GetString(o, "fullness", "none");
            v.Identity = Json.GetString(o, "identity", "none");
            return v;
        }
    }

    /// <summary>两个可自由的试剂槽：固体位与液体位。</summary>
    public sealed class Feeds
    {
        public string Solid;
        public string Liquid;
        /// <summary>液体入瓶路线：funnel＝经长颈漏斗加入（正确操作）；direct＝直接倒进容器。</summary>
        public string LiquidRoute = "funnel";

        public static Feeds FromJson(Dictionary<string, object> o)
        {
            Feeds f = new Feeds();
            f.Solid = Json.GetString(o, "solid", "");
            f.Liquid = Json.GetString(o, "liquid", "");
            f.LiquidRoute = Json.GetString(o, "liquid_route", "funnel");
            return f;
        }
    }
}
