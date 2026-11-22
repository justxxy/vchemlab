using System;
using System.Collections.Generic;

namespace Vchem.Core.Model
{
    /// <summary>
    /// 元件目录（文档 3.3）：7 个画布元件的 type / 中文名 / 端口→端口类型。
    /// 这是 UI 元件库与判定链共用的唯一一份定义，避免"界面允许、判定拒绝"。
    /// </summary>
    public sealed class ElementDef
    {
        public string Type;
        public string Name;
        /// <summary>端口名 → 端口类型（mouth / gas / feed / water / support）。</summary>
        public Dictionary<string, string> Ports = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool HasPort(string port) { return Ports.ContainsKey(port); }
        public string PortType(string port)
        {
            string t;
            return Ports.TryGetValue(port, out t) ? t : "";
        }
    }

    public static class ElementCatalog
    {
        private static readonly Dictionary<string, ElementDef> All =
            new Dictionary<string, ElementDef>(StringComparer.Ordinal);

        static ElementCatalog()
        {
            Add("reactor.flask_conical", "锥形瓶",
                "mouth:mouth", "body:support");
            Add("reactor.test_tube", "大试管",
                "mouth:mouth", "body:support");
            Add("feed.thistle_funnel", "长颈漏斗",
                "top:feed", "bottom:feed");
            Add("conduit.bent", "导管",
                "in:gas", "out:gas");
            Add("collect.gas_jar", "集气瓶",
                "mouth:mouth");
            Add("collect.water_trough", "水槽",
                "basin:water");
            Add("support.stand", "铁架台",
                "clamp:support");
        }

        private static void Add(string type, string name, params string[] ports)
        {
            ElementDef d = new ElementDef { Type = type, Name = name };
            foreach (string p in ports)
            {
                int colon = p.IndexOf(':');
                if (colon > 0) d.Ports[p.Substring(0, colon)] = p.Substring(colon + 1);
            }
            All[type] = d;
        }

        public static ElementDef Get(string type)
        {
            ElementDef d;
            return type != null && All.TryGetValue(type, out d) ? d : null;
        }

        public static string NameOf(string type)
        {
            ElementDef d = Get(type);
            return d == null ? type : d.Name;
        }

        /// <summary>查询"节点类型.端口名"的端口类型；未定义返回空串。</summary>
        public static string PortTypeOf(string nodeType, string portName)
        {
            ElementDef d = Get(nodeType);
            return d == null ? "" : d.PortType(portName);
        }

        public static IEnumerable<ElementDef> Elements { get { return All.Values; } }
    }
}
