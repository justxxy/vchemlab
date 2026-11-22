using System;
using System.Collections.Generic;
using Vchem.Core.Model;
using Vchem.Core.Reagents;

namespace Vchem.Core.Judge
{
    /// <summary>
    /// 方案设计状态（M2 面板的数据模型）。学生操作（放元件/连线/选试剂/检验）都落到这个状态，
    /// 再编译成 Plan 交给 Resolver。连线合法性走 ConnectionMatrix，与判定链共用同一份矩阵。
    /// </summary>
    public sealed class DesignState
    {
        private readonly List<Node> _nodes = new List<Node>();
        private readonly List<Edge> _edges = new List<Edge>();
        private int _nextId = 1;

        public string Fullness = "none";   // burning_splint / bubble / none
        public string Identity = "none";   // limewater / none
        public string SolidReagent = "";
        public string LiquidReagent = "";
        /// <summary>液体入瓶路线：funnel＝经长颈漏斗加入（正确操作）；direct＝直接倒进容器。</summary>
        public string LiquidRoute = "funnel";
        /// <summary>收集方式：upright / inverted / water（联动检验栏动态槽 A）。</summary>
        public string CollectorMode = "upright";

        /// <summary>投料入口守卫返回值：固液同瓶直投，触发 R-RXN-04 判定。</summary>
        public const string AddRejectReaction = "R-RXN-04";
        /// <summary>投料入口守卫返回值：空瓶也不许直接倒酸，酸必须走长颈漏斗（引导，非判定）。</summary>
        public const string AddGuideFunnel = "GUIDE-FUNNEL";

        /// <summary>
        /// 投料入口守卫（H5）：正确操作＝大理石放入锥形瓶、稀盐酸从长颈漏斗加入。
        /// 瓶里已有大理石再直接倒酸（或已有酸再直接投固体）会立即触发反应，
        /// 当前阶段不可同时放入锥形瓶中 → 返回 R-RXN-04，由 UI 出判定并拒绝落料。
        /// 大试管按教材可预装，不受此守卫约束。
        /// </summary>
        public string CheckAddition(bool isSolid, bool toFlask)
        {
            if (!toFlask) return "";
            if (isSolid) return string.IsNullOrEmpty(LiquidReagent) ? "" : AddRejectReaction;
            if (!string.IsNullOrEmpty(SolidReagent)) return AddRejectReaction;
            return AddGuideFunnel;
        }

        public IList<Node> Nodes { get { return _nodes; } }
        public IList<Edge> Edges { get { return _edges; } }

        public Node AddNode(string type)
        {
            if (ElementCatalog.Get(type) == null) return null;
            // H4：上限在状态入口就拦住，不能等到 Validator 提交时才报 E-STR-08
            if (_nodes.Count >= Validator.MaxNodes) return null;
            Node n = new Node { Id = "n" + _nextId++, Type = type };
            _nodes.Add(n);
            return n;
        }

        /// <summary>
        /// 从已提交的方案恢复节点时保留原始 id（方案里的编号可能是 n2..n5，不是从 n1 开始）。
        /// 直接 AddNode 会重编号导致边的端点失效；这里按原 id 建节点并同步 _nextId。
        /// </summary>
        public Node AddNodeWithId(string id, string type)
        {
            if (ElementCatalog.Get(type) == null || string.IsNullOrEmpty(id)) return null;
            if (_nodes.Count >= Validator.MaxNodes) return null;
            if (FindNode(id) != null) return null;
            Node n = new Node { Id = id, Type = type };
            _nodes.Add(n);
            int numeric;
            if (id.StartsWith("n") && int.TryParse(id.Substring(1), out numeric) && numeric >= _nextId)
                _nextId = numeric + 1;
            return n;
        }

        /// <summary>从方案恢复边（含 depth 等属性），不重跑端口兼容校验：来源本身是已提交的合法方案。</summary>
        public void AddEdgeWithProps(string from, string to, string kind, Dictionary<string, object> props)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
            if (_edges.Count >= Validator.MaxEdges) return;
            Edge e = new Edge { From = from, To = to, Kind = kind };
            if (props != null)
                foreach (KeyValuePair<string, object> kv in props) e.Props[kv.Key] = kv.Value;
            _edges.Add(e);
        }

        public bool RemoveNode(string id)
        {
            Node n = FindNode(id);
            if (n == null) return false;
            _nodes.Remove(n);
            // 删除关联边
            for (int i = _edges.Count - 1; i >= 0; i--)
                if (Edge.NodeOf(_edges[i].From) == id || Edge.NodeOf(_edges[i].To) == id)
                    _edges.RemoveAt(i);
            return true;
        }

        public Node FindNode(string id)
        {
            foreach (Node n in _nodes) if (n.Id == id) return n;
            return null;
        }

        /// <summary>
        /// 尝试连线。from/to 形如 "n1.mouth"。返回空串表示成功，否则返回错误码。
        /// 端口类型不匹配 → E-STR-03（与判定链一致）。
        /// </summary>
        public string Connect(string from, string to, string kind)
        {
            if (_edges.Count >= Validator.MaxEdges) return Codes.E_STR_08;
            Node fn = FindNode(Edge.NodeOf(from));
            Node tn = FindNode(Edge.NodeOf(to));
            if (fn == null || tn == null) return Codes.E_STR_03;

            // 自环与重复连线都不接受（DAG 的最小防线；判定层另有 E-STR-11）
            if (fn.Id == tn.Id) return Codes.E_STR_03;
            foreach (Edge old in _edges)
                if ((old.From == from && old.To == to) || (old.From == to && old.To == from))
                    return Codes.E_STR_03;

            // 共用连接矩阵实时校验
            if (!ConnectionMatrix.Compatible(
                    ElementCatalog.PortTypeOf(fn.Type, Edge.PortOf(from)),
                    ElementCatalog.PortTypeOf(tn.Type, Edge.PortOf(to))))
                return Codes.E_STR_03;

            Edge e = new Edge { From = from, To = to, Kind = kind };
            _edges.Add(e);
            return "";
        }

        /// <summary>是否已有集气瓶瓶口 ↔ 水槽的 water 边。</summary>
        public bool HasWaterEdge()
        {
            foreach (Edge e in _edges)
                if (e.Kind == "water") return true;
            return false;
        }

        /// <summary>
        /// H3：排水法的"接通"是选择动作的一部分——切到 water 时由 SetCollector 在状态层真正建这条边。
        /// 判定链仍然只认 water 边，但不再由 BuildPlan 偷偷补；这样 UI、数据和判定口径一致。
        /// 返回空串表示已接通（包含原本就接好的情况）。
        /// </summary>
        public string ConnectWater()
        {
            Node jar = null, trough = null;
            foreach (Node n in _nodes)
            {
                if (n.Type == "collect.gas_jar") jar = n;
                else if (n.Type == "collect.water_trough") trough = n;
            }
            if (jar == null || trough == null) return Codes.E_STR_02;
            if (HasWaterEdge()) return "";
            return Connect(jar.Id + ".mouth", trough.Id + ".basin", "water");
        }

        /// <summary>给边设置属性（如导管插入深度 depth）。</summary>
        public void SetEdgeProp(string from, string to, string key, object value)
        {
            foreach (Edge e in _edges)
                if (e.From == from && e.To == to) { e.Props[key] = value; return; }
        }

        public void SetNodeProp(string id, string key, object value)
        {
            Node n = FindNode(id);
            if (n != null) n.Props[key] = value;
        }

        public void Clear()
        {
            _nodes.Clear();
            _edges.Clear();
            _nextId = 1;
            Fullness = "none"; Identity = "none";
            SolidReagent = ""; LiquidReagent = "";
            LiquidRoute = "funnel";
            CollectorMode = "upright";
        }

        /// <summary>编译成 Plan。收集方式与检验栏动态槽 A 联动：排水法验满方式自动为 bubble。</summary>
        public Plan BuildPlan()
        {
            Plan p = new Plan
            {
                Schema = "vchem.plan/1",
                ExperimentId = "exp.co2.prepare",
                Topology = new Topology(),
                Verification = new Verification(),
                Feeds = new Feeds { Solid = SolidReagent, Liquid = LiquidReagent, LiquidRoute = LiquidRoute },
                StudentId = "S-UI",
                SubmittedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")
            };
            foreach (Node n in _nodes)
            {
                Node copy = new Node { Id = n.Id, Type = n.Type };
                foreach (KeyValuePair<string, object> kv in n.Props) copy.Props[kv.Key] = kv.Value;
                p.Topology.Nodes.Add(copy);
            }
            foreach (Edge e in _edges)
            {
                Edge copy = new Edge { From = e.From, To = e.To, Kind = e.Kind };
                foreach (KeyValuePair<string, object> kv in e.Props) copy.Props[kv.Key] = kv.Value;
                p.Topology.Edges.Add(copy);
            }
            // H3 兼容兜底：正常路径由 SetCollector 在状态层调用 ConnectWater() 建边（选择排水法＝接通水槽）。
            // 这里只兜住"外部直接构造 DesignState / 旧快照"的情况，且绝不突破 MaxEdges。
            // 判定链始终只认 water 边；不再出现"UI 说没连、判定却自动补边"的分叉。
            if (CollectorMode == "water" && p.Topology.Edges.Count < Validator.MaxEdges)
            {
                Node jar = null, trough = null;
                foreach (Node n in _nodes)
                {
                    if (n.Type == "collect.gas_jar") jar = n;
                    else if (n.Type == "collect.water_trough") trough = n;
                }
                if (jar != null && trough != null)
                {
                    string wf = jar.Id + ".mouth", wt = trough.Id + ".basin";
                    bool exists = false;
                    foreach (Edge e in p.Topology.Edges)
                        if (e.Kind == "water" &&
                            ((e.From == wf && e.To == wt) || (e.From == wt && e.To == wf)))
                        { exists = true; break; }
                    if (!exists)
                        p.Topology.Edges.Add(new Edge { From = wf, To = wt, Kind = "water" });
                }
            }
            // 动态槽 A：排水法时验满自动切 bubble
            p.Verification.Fullness = CollectorMode == "water" ? "bubble" : Fullness;
            p.Verification.Identity = Identity;
            return p;
        }

        public Outcome Submit()
        {
            return Resolver.Resolve(BuildPlan());
        }

        /// <summary>
        /// 端口/节点定位：把结果类映射到画布上具体出问题的节点与端口（DoD：8 种错法定位到端口）。
        /// 返回 (nodeId, portLabel, hint) 列表；无定位返回空。
        /// </summary>
        public List<PortIssue> Locate(Outcome outcome)
        {
            List<PortIssue> issues = new List<PortIssue>();
            if (outcome == null) return issues;
            string cls = outcome.Class ?? "";

            Node funnel = FindByType("feed.thistle_funnel");
            Node conduit = FindByType("conduit.bent");
            Node jar = FindByType("collect.gas_jar");
            Node trough = FindByType("collect.water_trough");

            switch (cls)
            {
                case "R-COL-01": // 漏斗未液封
                    if (funnel != null) issues.Add(new PortIssue(funnel.Id, "bottom", "漏斗下端需在液面下（液封）"));
                    break;
                case "R-COL-02": // 导管太短
                    if (conduit != null) issues.Add(new PortIssue(conduit.Id, "out", "导管需伸到集气瓶底部"));
                    break;
                case "R-COL-03": // 集气瓶未盖玻璃片
                    if (jar != null) issues.Add(new PortIssue(jar.Id, "mouth", "集气瓶口没盖玻璃片：点瓶口右边〔玻璃片〕橙点"));
                    break;
                case "R-COL-04": // 集气瓶倒置
                    if (jar != null) issues.Add(new PortIssue(jar.Id, "mouth", "集气瓶应正放（CO₂ 比空气重）"));
                    break;
                case "R-COL-05": // 排水法
                    if (trough != null) issues.Add(new PortIssue(trough.Id, "basin", "排水法收集率低，改用向上排空气"));
                    break;
                case "R-RXN-01":
                case "R-RXN-02":
                case "R-RXN-03": // 试剂问题 → 试剂槽
                    issues.Add(new PortIssue("SLOT-LIQUID", "liquid", "检查液体位试剂"));
                    if (cls == "R-RXN-02") issues.Add(new PortIssue("SLOT-SOLID", "solid", "检查固体颗粒大小"));
                    break;
                case "R-RXN-05": // 硫酸钙固体不与酸反应 → 固体槽
                    issues.Add(new PortIssue("SLOT-SOLID", "solid", "固体应选含碳酸根的大理石/石灰石"));
                    break;
                case "R-RXN-04": // 固液同瓶直投 → 容器 + 液体槽（酸应走漏斗）
                    Node rxn = FindByType("reactor.flask_conical");
                    if (rxn != null) issues.Add(new PortIssue(rxn.Id, "inside", "固液不可同时直接放入锥形瓶：反应会立即触发"));
                    issues.Add(new PortIssue("SLOT-LIQUID", "liquid", "稀盐酸应从长颈漏斗加入，而不是直接倒进锥形瓶"));
                    break;
                case "R-OK-02": // 缺检验/验满
                    if (Identity != "limewater") issues.Add(new PortIssue("SLOT-B", "identity", "〔检验产物为 CO₂〕需选澄清石灰水"));
                    if (Fullness == "none" && CollectorMode != "water")
                        issues.Add(new PortIssue("SLOT-A", "fullness", "〔验满〕需选燃着的木条（排水法自动看瓶口气泡）"));
                    break;
                case "R-NONE-01":
                    issues.Add(new PortIssue("SLOT-LIQUID", "liquid", "是否加入了酸？"));
                    break;
            }

            // 结构错误 → 定位到相关端口
            foreach (string err in outcome.Errors)
            {
                if (err == Codes.E_STR_01)
                {
                    Node reactor = FindByType("reactor.flask_conical") ?? FindByType("reactor.test_tube");
                    if (reactor == null)
                        issues.Add(new PortIssue("CANVAS", "reactor", "先放一个发生容器（锥形瓶或大试管）"));
                    else if (conduit == null)
                        issues.Add(new PortIssue("CANVAS", "conduit", "还没有导气管：用〔导管连接〕把发生装置接到集气瓶"));
                    else
                        issues.Add(new PortIssue(conduit.Id, "in", "发生装置缺少出气口"));
                }
                else if (err == Codes.E_STR_02)
                    issues.Add(new PortIssue("CANVAS", "collector", "缺少收集装置（集气瓶）"));
                else if (err == Codes.E_STR_08)
                    issues.Add(new PortIssue("CANVAS", "count", "器材/连线超过上限：器材 ≤ 8、连线 ≤ 12"));
                else if (err == Codes.E_STR_11)
                    issues.Add(new PortIssue("CANVAS", "unique", "反应容器 / 长颈漏斗 / 集气瓶 / 水槽各只能放一个"));
                else if (err == Codes.E_STR_10)
                {
                    if (funnel != null)
                        issues.Add(new PortIssue(funnel.Id, "bottom", "长颈漏斗还没接到发生容器"));
                    else
                        issues.Add(new PortIssue("CANVAS", "feed", "缺少加料路径：锥形瓶需要长颈漏斗"));
                }
                else if (err == Codes.E_STR_06 && jar != null)
                    issues.Add(new PortIssue(jar.Id, "mouth", "导管未插进集气瓶（或没声明插入深度）"));
                else if (err == Codes.E_STR_07)
                {
                    // 固体/液体任一为空都会报 E-STR-07：必须分开说清楚是哪个槽，
                    // 否则学生看到"试剂槽未填"不知道缺的是大理石还是稀盐酸。
                    if (string.IsNullOrEmpty(SolidReagent))
                        issues.Add(new PortIssue("SLOT-SOLID", "solid", "固体槽没填：把〔大理石（块状）〕拖到锥形瓶里"));
                    if (string.IsNullOrEmpty(LiquidReagent))
                    {
                        // 酸可能还兜在长颈漏斗里：这时该做的是"把漏斗接进锥形瓶"，不是再倒一次
                        string held = funnel == null ? "" : funnel.Prop("reagent", "");
                        if (!string.IsNullOrEmpty(held))
                            issues.Add(new PortIssue(funnel.Id, "bottom",
                                "长颈漏斗里已经倒了〔稀盐酸〕：把它下端接到锥形瓶端口上，酸才会流进瓶里"));
                        else
                            issues.Add(new PortIssue("SLOT-LIQUID", "liquid", "液体槽没填：把〔稀盐酸〕倒进长颈漏斗（漏斗接进锥形瓶后酸才会流下去）"));
                    }
                }
            }
            return issues;
        }

        private Node FindByType(string type)
        {
            foreach (Node n in _nodes) if (n.Type == type) return n;
            return null;
        }

        /// <summary>按器材类型查找节点（UI 与诊断共用；原 ForTest 命名已废弃）。</summary>
        public Node FindNodeByType(string type) { return FindByType(type); }
    }

    public sealed class PortIssue
    {
        public string NodeId;
        public string Port;
        public string Hint;
        public PortIssue(string nodeId, string port, string hint)
        {
            NodeId = nodeId; Port = port; Hint = hint;
        }
        public override string ToString() { return NodeId + "." + Port + " : " + Hint; }
    }
}
