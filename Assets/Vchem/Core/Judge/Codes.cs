using System;
using System.Collections.Generic;

namespace Vchem.Core.Judge
{
    /// <summary>错误码与结论档常量（文档 7.1 / 7.3）。</summary>
    public static class Codes
    {
        public const string E_STR_01 = "E-STR-01"; // 缺少出气口（装置封闭）
        public const string E_STR_02 = "E-STR-02"; // 没有收集装置
        public const string E_STR_03 = "E-STR-03"; // 端口类型不匹配
        public const string E_STR_04 = "E-STR-04"; // 气路未封闭（漏气）
        public const string E_STR_05 = "E-STR-05"; // 热源缺失或多余（预留，不触发）
        public const string E_STR_06 = "E-STR-06"; // 导管没有插入收集装置
        public const string E_STR_07 = "E-STR-07"; // 试剂槽未填
        public const string E_STR_08 = "E-STR-08"; // 节点或边超过上限
        public const string E_STR_09 = "E-STR-09"; // 元件类型不在白名单
        public const string E_STR_10 = "E-STR-10"; // 加料路径未接通
        public const string E_STR_11 = "E-STR-11"; // 关键元件重复（反应容器/漏斗/集气瓶/水槽）
        public const string E_RGT_01 = "E-RGT-01"; // 试剂不在白名单
        public const string E_SYS_01 = "E-SYS-01"; // 规则库无命中
        public const string E_SYS_02 = "E-SYS-02"; // 内容资源缺失（内容加载层，判定链不产出）

        public const string OutcomeSuccess = "success";
        public const string OutcomePartial = "partial";
        public const string OutcomeFail = "fail";
        public const string OutcomeNone = "none";
        public const string OutcomeError = "error";

        private static readonly Dictionary<string, string> Messages =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { E_STR_01, "缺少出气口（装置封闭）" },
                { E_STR_02, "没有收集装置" },
                { E_STR_03, "端口类型不匹配" },
                { E_STR_04, "气路未封闭（漏气）" },
                { E_STR_05, "热源缺失或多余" },
                { E_STR_06, "导管没有插入收集装置" },
                { E_STR_07, "试剂槽未填（固体或液体，见底部摘要）" },
                { E_STR_08, "节点或边超过上限" },
                { E_STR_09, "元件类型不在白名单" },
                { E_STR_10, "加料路径未接通（长颈漏斗没接到发生容器）" },
                { E_STR_11, "同类关键元件只能有一个（反应容器 / 长颈漏斗 / 集气瓶 / 水槽）" },
                { E_RGT_01, "试剂不在白名单" },
                { E_SYS_01, "该方案超出评测范围" },
            };

        public static string Message(string code)
        {
            string m;
            return Messages.TryGetValue(code, out m) ? m : code;
        }
    }
}
