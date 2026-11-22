using System;
using System.Collections.Generic;
using Vchem.Core.Model;

namespace Vchem.Core.Judge
{
    /// <summary>
    /// 端口连接矩阵（文档 6.2）：唯一一份，UI 实时校验与判定链共用。
    /// 完整矩阵：mouth↔gas、mouth↔feed、mouth↔water(basin)、gas↔gas、clamp↔body(support↔support)、heat↔heat。
    /// 连接是无序对：A→B 与 B→A 都允许（plan 里气体边可能反向书写）。
    /// </summary>
    public static class ConnectionMatrix
    {
        private struct Pair
        {
            public string A;
            public string B;
            public Pair(string a, string b)
            {
                // 归一顺序，使无序对可比较
                if (string.CompareOrdinal(a, b) <= 0) { A = a; B = b; }
                else { A = b; B = a; }
            }
        }

        private static readonly Pair[] Allowed =
        {
            new Pair("mouth", "gas"),       // 瓶口 ↔ 导管气路
            new Pair("mouth", "feed"),      // 瓶口 ↔ 加料口
            new Pair("mouth", "water"),     // 瓶口 ↔ 水槽（排水法）
            new Pair("gas", "gas"),         // 导管 ↔ 导管
            new Pair("support", "support"), // 铁架台 clamp ↔ 元件 body
            new Pair("heat", "heat"),       // 受热面（本期无热源，预留）
        };

        /// <summary>两种端口类型是否允许连接。</summary>
        public static bool Compatible(string portTypeA, string portTypeB)
        {
            if (string.IsNullOrEmpty(portTypeA) || string.IsNullOrEmpty(portTypeB)) return false;
            Pair p = new Pair(portTypeA, portTypeB);
            foreach (Pair allowed in Allowed)
                if (string.Equals(allowed.A, p.A, StringComparison.Ordinal) &&
                    string.Equals(allowed.B, p.B, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// 校验一条边的两个端点端口类型是否兼容。端点格式 "node.port"。
        /// 节点或端口在目录中不存在 → 返回 false（视为不匹配）。
        /// </summary>
        public static bool CanConnect(Plan plan, string fromEndpoint, string toEndpoint)
        {
            if (plan == null || plan.Topology == null) return false;
            string fn = Edge.NodeOf(fromEndpoint), fp = Edge.PortOf(fromEndpoint);
            string tn = Edge.NodeOf(toEndpoint), tp = Edge.PortOf(toEndpoint);
            Node from = plan.Topology.FindNode(fn);
            Node to = plan.Topology.FindNode(tn);
            if (from == null || to == null) return false;
            string ft = ElementCatalog.PortTypeOf(from.Type, fp);
            string tt = ElementCatalog.PortTypeOf(to.Type, tp);
            return Compatible(ft, tt);
        }

        /// <summary>UI 用：根据两个"节点类型.端口名"字符串判断能否连线（无需完整 plan）。</summary>
        public static bool CompatibleByType(string fromTypePort, string toTypePort)
        {
            string ft = TypePortPortType(fromTypePort);
            string tt = TypePortPortType(toTypePort);
            return Compatible(ft, tt);
        }

        private static string TypePortPortType(string typePort)
        {
            if (string.IsNullOrEmpty(typePort)) return "";
            int dot = typePort.IndexOf('.');
            if (dot < 0) return "";
            return ElementCatalog.PortTypeOf(typePort.Substring(0, dot), typePort.Substring(dot + 1));
        }
    }
}
