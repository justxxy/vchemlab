using System;
using System.Collections.Generic;
using Vchem.Core.Model;
using Vchem.Core.Reagents;

namespace Vchem.Core.Judge
{
    /// <summary>
    /// 第一层：合法性校验。产出 E-STR-* / E-RGT-01。
    /// 任一结构错误 → 不进 3D；试剂越界 → 显式提示，禁止静默。
    /// </summary>
    public static class Validator
    {
        /// <summary>规模上限由判定层定义，UI / DesignState 共用同一份，避免"界面放得下、提交才拒"。</summary>
        public const int MaxNodes = 8;
        public const int MaxEdges = 12;

        private static readonly HashSet<string> AllowedNodeTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "reactor.flask_conical", "reactor.test_tube",
            "feed.thistle_funnel", "conduit.bent",
            "collect.gas_jar", "collect.water_trough", "support.stand"
        };

        private static readonly HashSet<string> AllowedEdgeKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "gas", "feed", "water", "support"
        };

        public static List<string> Validate(Plan plan)
        {
            List<string> errors = new List<string>();
            if (plan == null || plan.Topology == null)
            {
                errors.Add(Codes.E_STR_01);
                errors.Add(Codes.E_STR_02);
                errors.Add(Codes.E_STR_07);
                return errors;
            }

            Topology topo = plan.Topology;

            // E-STR-08 规模上限
            if (topo.Nodes.Count > MaxNodes || topo.Edges.Count > MaxEdges)
                errors.Add(Codes.E_STR_08);

            // E-STR-09 元件白名单
            foreach (Node n in topo.Nodes)
                if (!AllowedNodeTypes.Contains(n.Type))
                {
                    if (!errors.Contains(Codes.E_STR_09)) errors.Add(Codes.E_STR_09);
                    break;
                }

            // E-STR-03 边类型白名单 + 端点格式 + 端口类型兼容（与 UI 共用 ConnectionMatrix）
            foreach (Edge e in topo.Edges)
            {
                if (!AllowedEdgeKinds.Contains(e.Kind) ||
                    string.IsNullOrEmpty(Edge.NodeOf(e.From)) ||
                    string.IsNullOrEmpty(Edge.PortOf(e.From)) ||
                    string.IsNullOrEmpty(Edge.NodeOf(e.To)) ||
                    string.IsNullOrEmpty(Edge.PortOf(e.To)))
                {
                    errors.Add(Codes.E_STR_03);
                    break;
                }
                // 端口类型不匹配（如把导管接到 body）——UI 连线时用同一矩阵拦截
                if (!ConnectionMatrix.CanConnect(plan, e.From, e.To))
                {
                    errors.Add(Codes.E_STR_03);
                    break;
                }
            }

            // E-STR-11 关键元件唯一性：本实验是"一套装置"，不允许两个反应容器/漏斗/集气瓶/水槽
            int reactors = 0, funnels = 0, jars = 0, troughs = 0;
            foreach (Node n in topo.Nodes)
            {
                if (n.Type == "reactor.flask_conical" || n.Type == "reactor.test_tube") reactors++;
                else if (n.Type == "feed.thistle_funnel") funnels++;
                else if (n.Type == "collect.gas_jar") jars++;
                else if (n.Type == "collect.water_trough") troughs++;
            }
            if (reactors > 1 || funnels > 1 || jars > 1 || troughs > 1)
                errors.Add(Codes.E_STR_11);

            // E-STR-01 缺少出气口：气路未接到发生容器瓶口。
            // 按契约，gas 边方向是"导管 → 瓶口"（如 n3.in → n1.mouth）；
            // 也可能反向书写（n1.mouth → n3.in），故两端都检查。
            bool hasOutlet = false;
            foreach (Edge e in topo.Edges)
            {
                if (e.Kind != "gas") continue;
                Node from = topo.FindNode(Edge.NodeOf(e.From));
                Node to = topo.FindNode(Edge.NodeOf(e.To));
                if (IsReactorPort(from, Edge.PortOf(e.From)) ||
                    IsReactorPort(to, Edge.PortOf(e.To)))
                {
                    hasOutlet = true;
                    break;
                }
            }
            if (!hasOutlet) errors.Add(Codes.E_STR_01);

            // E-STR-02 没有收集装置
            bool hasCollector = false;
            foreach (Node n in topo.Nodes)
                if (n.Type == "collect.gas_jar") { hasCollector = true; break; }
            if (!hasCollector) errors.Add(Codes.E_STR_02);

            // E-STR-10 加料路径：漏斗必须真的接到发生容器；
            // 锥形瓶必须有加料路径；大试管可按教材"预装后塞单孔塞"，允许没有漏斗。
            bool hasFlask = false, hasFunnel = false;
            foreach (Node n in topo.Nodes)
            {
                if (n.Type == "reactor.flask_conical") hasFlask = true;
                else if (n.Type == "feed.thistle_funnel") hasFunnel = true;
            }
            bool feedConnected = false;
            foreach (Edge e in topo.Edges)
            {
                if (e.Kind != "feed") continue;
                Node a = topo.FindNode(Edge.NodeOf(e.From));
                Node b = topo.FindNode(Edge.NodeOf(e.To));
                if (a == null || b == null) continue;
                bool funnelEnd = (a.Type == "feed.thistle_funnel" && Edge.PortOf(e.From) == "bottom")
                              || (b.Type == "feed.thistle_funnel" && Edge.PortOf(e.To) == "bottom");
                bool reactorEnd = IsReactorPort(a, Edge.PortOf(e.From)) || IsReactorPort(b, Edge.PortOf(e.To));
                if (funnelEnd && reactorEnd) { feedConnected = true; break; }
            }
            if (!feedConnected && (hasFlask || hasFunnel)) errors.Add(Codes.E_STR_10);

            // E-STR-06 导管必须伸进集气瓶，并声明插入深度（deep / shallow）。
            // 只有"到瓶口"没有深度属性的方案，等同没插进瓶里。
            if (hasCollector)
            {
                bool gasIntoJar = false, depthDeclared = false;
                foreach (Edge e in topo.Edges)
                {
                    if (e.Kind != "gas") continue;
                    Node from = topo.FindNode(Edge.NodeOf(e.From));
                    Node to = topo.FindNode(Edge.NodeOf(e.To));
                    bool touchesJar =
                        (to != null && to.Type == "collect.gas_jar" && Edge.PortOf(e.To) == "mouth") ||
                        (from != null && from.Type == "collect.gas_jar" && Edge.PortOf(e.From) == "mouth");
                    if (!touchesJar) continue;
                    gasIntoJar = true;
                    string depth = e.Prop("depth", "none");
                    if (depth == "deep" || depth == "shallow") depthDeclared = true;
                }
                if (!gasIntoJar || !depthDeclared) errors.Add(Codes.E_STR_06);
            }

            // E-STR-04 气路未封闭：导管存在但出气端未接到任何收集端
            // （在 plan 层面，gas 边的 to 必须是有效的集气瓶或石灰水检验端；这里只检查是否有悬空 gas 边）
            foreach (Edge e in topo.Edges)
            {
                if (e.Kind != "gas") continue;
                Node to = topo.FindNode(Edge.NodeOf(e.To));
                if (to == null)
                {
                    errors.Add(Codes.E_STR_04);
                    break;
                }
            }

            // E-STR-07 试剂槽未填
            if (plan.Feeds == null ||
                string.IsNullOrEmpty(plan.Feeds.Solid) ||
                string.IsNullOrEmpty(plan.Feeds.Liquid))
                errors.Add(Codes.E_STR_07);

            // E-RGT-01 试剂不在白名单
            if (plan.Feeds != null)
            {
                if (!string.IsNullOrEmpty(plan.Feeds.Solid) && !ReagentDb.Contains(plan.Feeds.Solid))
                    errors.Add(Codes.E_RGT_01);
                if (!string.IsNullOrEmpty(plan.Feeds.Liquid) && !ReagentDb.Contains(plan.Feeds.Liquid))
                {
                    if (!errors.Contains(Codes.E_RGT_01)) errors.Add(Codes.E_RGT_01);
                }
            }
            // 检验项不做白名单强制：none 是合法取值（缺检验 → R-OK-02）

            return errors;
        }

        private static bool IsReactorPort(Node n, string port)
        {
            if (n == null || port != "mouth") return false;
            return n.Type == "reactor.flask_conical" || n.Type == "reactor.test_tube";
        }
    }
}
