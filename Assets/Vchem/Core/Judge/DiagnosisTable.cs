using System;
using System.Collections.Generic;

namespace Vchem.Core.Judge
{
    /// <summary>
    /// 结论文案表（文档 7.6）。遵循"只给现象 + 检查点，不给因果"——
    /// 3D 字幕只出现象名，诊断卡把解释改写成检查点问句。
    /// </summary>
    public static class DiagnosisTable
    {
        private sealed class Entry
        {
            public string Summary;
            public string[] Checklist;
        }

        private static readonly Dictionary<string, Entry> Table =
            new Dictionary<string, Entry>(StringComparer.Ordinal)
            {
                { "D-OK-01", E("装置正确，已收满并检验。",
                    "长颈漏斗下端是否在液面下？", "导管是否伸到瓶底？") },
                { "D-OK-02", E("目标未完成：缺\"验满\"或\"检验\"。",
                    "是否放了燃着的木条（或排水法下观察瓶口气泡）？", "是否通入澄清石灰水？") },
                { "D-COL-01", E("气泡从长颈漏斗口逸出，集气瓶收不到气。",
                    "漏斗下端是否在液面下？") },
                { "D-COL-02", E("气泡正常但收集很慢、验不满。",
                    "导管是否伸到瓶底？") },
                { "D-COL-03", E("气体泄漏，收不到。",
                    "集气瓶口是否盖了玻璃片？") },
                { "D-COL-04", E("收不到气。",
                    "集气瓶是正放还是倒置？", "CO₂ 密度比空气大还是小？") },
                { "D-COL-05", E("水位下降很慢、瓶口迟迟不冒气泡。",
                    "CO₂ 能溶于水吗？", "该用哪种收集方法？") },
                { "D-RXN-04", E("瓶内立即剧烈起泡，气体来不及收集就从瓶口冲出。",
                    "大理石是否先放进了锥形瓶？", "稀盐酸是否改从长颈漏斗加入？") },
                { "D-RXN-01", E("气泡的产生逐渐停止。",
                    "换一种酸试试？") },
                { "D-RXN-05", E("锥形瓶内看不到气泡，收集不到气体。",
                    "这种固体含碳酸根吗？", "换成大理石试试？") },
                { "D-RXN-02", E("剧烈起泡、泡沫溢出，难以控制。",
                    "固体的颗粒大小是否合适？") },
                { "D-RXN-03", E("瓶口出现白雾，石灰水浑浊变浅。",
                    "盐酸的浓度是否合适？") },
                { "D-NONE-01", E("未观察到明显现象。",
                    "是否加入了酸？") },
            };

        private static Entry E(string summary, params string[] checklist)
        {
            return new Entry { Summary = summary, Checklist = checklist };
        }

        public static string Summary(string id)
        {
            Entry e;
            return Table.TryGetValue(id, out e) ? e.Summary : "";
        }

        public static List<string> Checklist(string id)
        {
            Entry e;
            List<string> list = new List<string>();
            if (Table.TryGetValue(id, out e) && e.Checklist != null)
                list.AddRange(e.Checklist);
            return list;
        }
    }
}
