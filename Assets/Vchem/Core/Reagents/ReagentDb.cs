using System;
using System.Collections.Generic;

namespace Vchem.Core.Reagents
{
    /// <summary>
    /// 试剂白名单（文档 3.5，含干扰项共 9 种）。
    /// 注意：澄清石灰水固定在检验栏，不占试剂槽，但仍列入白名单以便校验 verification.identity。
    /// </summary>
    public static class ReagentDb
    {
        public sealed class Reagent
        {
            public string Id;
            public string Name;
            public string Phase;   // 固 / 液
            public string Role;    // correct / wrong / test
            public string Reason;  // 错例原因（用于诊断，不含因果输出）
        }

        private static readonly Dictionary<string, Reagent> All =
            new Dictionary<string, Reagent>(StringComparer.Ordinal);

        static ReagentDb()
        {
            Add("reagent.caco3_lump", "大理石（块状）", "固", "correct", "");
            Add("reagent.limestone_lump", "石灰石（块状）", "固", "correct", "与大理石等价（别名解析）");
            Add("reagent.caco3_powder", "碳酸钙粉末", "固", "wrong", "反应过快、泡沫溢出");
            Add("reagent.caso4", "硫酸钙", "固", "wrong", "不与稀盐酸产生 CO₂，无气泡");
            Add("reagent.hcl_dilute", "稀盐酸", "液", "correct", "");
            Add("reagent.hcl_conc", "浓盐酸", "液", "wrong", "挥发，气体带 HCl，白雾");
            Add("reagent.h2so4_dilute", "稀硫酸", "液", "wrong", "CaSO₄ 微溶覆盖，反应很快停止");
            Add("reagent.water", "蒸馏水", "液", "wrong", "不反应");
            Add("reagent.limewater", "澄清石灰水", "液", "test", "检验（绑定检验栏槽 B）");
        }

        private static void Add(string id, string name, string phase, string role, string reason)
        {
            All[id] = new Reagent { Id = id, Name = name, Phase = phase, Role = role, Reason = reason };
        }

        public static bool Contains(string id)
        {
            return !string.IsNullOrEmpty(id) && All.ContainsKey(id);
        }

        public static Reagent Get(string id)
        {
            Reagent r;
            return id != null && All.TryGetValue(id, out r) ? r : null;
        }

        /// <summary>把固体试剂归一到"角色标签"，供规则匹配使用。</summary>
        public static string SolidTag(string id)
        {
            if (string.IsNullOrEmpty(id)) return "none";
            if (id == "reagent.caco3_lump" || id == "reagent.limestone_lump") return "caco3_lump";
            if (id == "reagent.caco3_powder") return "caco3_powder";
            if (id == "reagent.caso4") return "caso4";
            return "other";
        }

        /// <summary>把液体试剂归一到"角色标签"，供规则匹配使用。</summary>
        public static string LiquidTag(string id)
        {
            if (string.IsNullOrEmpty(id)) return "none";
            if (id == "reagent.hcl_dilute") return "hcl_dilute";
            if (id == "reagent.hcl_conc") return "hcl_conc";
            if (id == "reagent.h2so4_dilute") return "h2so4_dilute";
            if (id == "reagent.water") return "water";
            return "other";
        }
    }
}
