using System;
using System.Collections.Generic;
using Vchem.Core.Model;
using Vchem.Core.Reagents;

namespace Vchem.Core.Judge
{
    /// <summary>
    /// 把 plan 编译成特征向量（文档 6.3 / 7.5）。
    /// 特征只描述"装置长什么样 + 选了什么试剂 + 做了哪些检验"，不设实验条件开关。
    /// </summary>
    public sealed class Features
    {
        // 结构特征
        public bool Sealed;             // 发生装置有出气导管
        public bool LiquidSeal;         // 长颈漏斗下端在液面下
        public string TubeDepth = "none"; // deep / shallow / none（导管出气端插入收集装置）
        public string CollectorMode = "none"; // upward_displacement / downward_displacement / water_displacement / none
        public bool CollectorCovered;   // 集气瓶盖玻璃片
        public bool HasCollector;       // 有集气瓶
        public bool HasWaterTrough;     // 有水槽（排水法）
        public bool HasGasConduit;      // 有导管
        public bool HasReactor;         // 有发生容器（锥形瓶/大试管）
        public bool HasFeedFunnel;      // 有长颈漏斗
        public string ReactorType = "none"; // flask / tube / none
        public bool HasFeedPath;        // 漏斗下端真的接到了发生容器瓶口
        public bool AdditionOk;         // 加料成立（漏斗接通并液封，或大试管预装）
        public bool HasWaterEdge;       // 集气瓶 mouth ↔ 水槽 basin 的 water 边

        // 检验特征
        public bool HasVerification;    // 做了验满
        public bool HasLimewater;       // 做了检验
        public string Fullness = "none"; // burning_splint / bubble / none

        // 试剂特征（归一标签）
        public string ReagentSolid = "none";
        public string ReagentLiquid = "none";
        /// <summary>液体入瓶路线：funnel / direct（固液同瓶直投判定用）。</summary>
        public string FeedRoute = "funnel";

        /// <summary>供规则/诊断读取的字典视图。</summary>
        public Dictionary<string, string> ToMap()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.Ordinal);
            m["sealed"] = Sealed ? "1" : "0";
            m["liquid_seal"] = LiquidSeal ? "1" : "0";
            m["tube_depth"] = TubeDepth;
            m["collector_mode"] = CollectorMode;
            m["collector_cover"] = CollectorCovered ? "1" : "0";
            m["has_collector"] = HasCollector ? "1" : "0";
            m["has_water_trough"] = HasWaterTrough ? "1" : "0";
            m["has_conduit"] = HasGasConduit ? "1" : "0";
            m["has_reactor"] = HasReactor ? "1" : "0";
            m["has_funnel"] = HasFeedFunnel ? "1" : "0";
            m["reactor_type"] = ReactorType;
            m["feed_path"] = HasFeedPath ? "1" : "0";
            m["addition_ok"] = AdditionOk ? "1" : "0";
            m["has_water_edge"] = HasWaterEdge ? "1" : "0";
            m["has_verification"] = HasVerification ? "1" : "0";
            m["has_limewater"] = HasLimewater ? "1" : "0";
            m["fullness"] = Fullness;
            m["reagent_solid"] = ReagentSolid;
            m["reagent_liquid"] = ReagentLiquid;
            m["feed_route"] = FeedRoute;
            return m;
        }
    }

    public static class FeatureExtractor
    {
        public static Features Extract(Plan plan)
        {
            Features f = new Features();
            if (plan == null || plan.Topology == null) return f;

            Topology topo = plan.Topology;

            // --- 节点类型扫描 ---
            Node collectorNode = null;
            foreach (Node n in topo.Nodes)
            {
                switch (n.Type)
                {
                    case "reactor.flask_conical":
                        f.HasReactor = true;
                        if (f.ReactorType == "none") f.ReactorType = "flask";
                        break;
                    case "reactor.test_tube":
                        f.HasReactor = true;
                        if (f.ReactorType == "none") f.ReactorType = "tube";
                        break;
                    case "feed.thistle_funnel":
                        f.HasFeedFunnel = true;
                        f.LiquidSeal = n.PropBool("submerged", false);
                        break;
                    case "conduit.bent":
                        f.HasGasConduit = true;
                        break;
                    case "collect.gas_jar":
                        f.HasCollector = true;
                        collectorNode = n;
                        break;
                    case "collect.water_trough":
                        f.HasWaterTrough = true;
                        break;
                }
            }

            // --- 边扫描：气路 / 加料路径 / 排水法水边 / 插入深度 ---
            foreach (Edge e in topo.Edges)
            {
                Node from = topo.FindNode(Edge.NodeOf(e.From));
                Node to = topo.FindNode(Edge.NodeOf(e.To));
                if (from == null || to == null) continue;

                if (e.Kind == "feed")
                {
                    bool funnelEnd = (from.Type == "feed.thistle_funnel" && Edge.PortOf(e.From) == "bottom")
                                  || (to.Type == "feed.thistle_funnel" && Edge.PortOf(e.To) == "bottom");
                    bool reactorEnd = IsReactor(from, Edge.PortOf(e.From)) || IsReactor(to, Edge.PortOf(e.To));
                    if (funnelEnd && reactorEnd) f.HasFeedPath = true;
                }
                else if (e.Kind == "water")
                {
                    bool jarEnd = (from.Type == "collect.gas_jar" && Edge.PortOf(e.From) == "mouth")
                               || (to.Type == "collect.gas_jar" && Edge.PortOf(e.To) == "mouth");
                    bool troughEnd = (from.Type == "collect.water_trough" && Edge.PortOf(e.From) == "basin")
                                  || (to.Type == "collect.water_trough" && Edge.PortOf(e.To) == "basin");
                    if (jarEnd && troughEnd) f.HasWaterEdge = true;
                }
                else if (e.Kind == "gas")
                {
                    // 气路接到发生容器瓶口 → 装置封闭（方向可能是 导管→瓶口 或 瓶口→导管）
                    if (IsReactor(from, Edge.PortOf(e.From)) || IsReactor(to, Edge.PortOf(e.To)))
                        f.Sealed = true;

                    // 导管插入收集装置的深度（双向判断）
                    if (to.Type == "collect.gas_jar" || from.Type == "collect.gas_jar")
                    {
                        string depth = e.Prop("depth", "none");
                        if (depth == "deep" || depth == "shallow") f.TubeDepth = depth;
                    }
                }
            }

            // 集气瓶结构属性：排水法只认状态层真实存在的 water 边（由切换收集方式时建立），不是"场景里有水槽"
            if (collectorNode != null)
            {
                f.CollectorCovered = collectorNode.PropBool("covered", false);
                string orientation = collectorNode.Prop("orientation", "upright");
                if (f.HasWaterTrough && f.HasWaterEdge)
                    f.CollectorMode = "water_displacement";
                else
                    f.CollectorMode = orientation == "inverted"
                        ? "downward_displacement"
                        : "upward_displacement";
            }

            // 加料是否成立：大试管可按教材预装（无漏斗）；其余必须有"接通 + 液封"的漏斗
            if (f.ReactorType == "tube" && !f.HasFeedFunnel) f.AdditionOk = true;
            else f.AdditionOk = f.HasFeedPath && f.LiquidSeal;

            // --- 检验栏 ---
            if (plan.Verification != null)
            {
                f.Fullness = plan.Verification.Fullness ?? "none";
                f.HasVerification = f.Fullness != "none";
                f.HasLimewater = plan.Verification.Identity == "limewater";
            }

            // --- 试剂 ---
            if (plan.Feeds != null)
            {
                f.ReagentSolid = ReagentDb.SolidTag(plan.Feeds.Solid);
                f.ReagentLiquid = ReagentDb.LiquidTag(plan.Feeds.Liquid);
                f.FeedRoute = plan.Feeds.LiquidRoute ?? "funnel";
            }

            return f;
        }

        private static bool IsReactor(Node n, string port)
        {
            if (n == null || port != "mouth") return false;
            return n.Type == "reactor.flask_conical" || n.Type == "reactor.test_tube";
        }
    }
}
