using System;
using System.Collections.Generic;

namespace Vchem.Core.Judge
{
    /// <summary>
    /// 判定规则表（文档 7.2 / 7.6）。
    /// 每条规则 = when 谓词（在特征向量上求值） + then（结果类 + 参数 + 结论档 + 诊断 id）。
    /// 匹配策略：全部求值，多命中取"最具体"（Specificity 最高）；无命中报 E-SYS-01。
    /// </summary>
    public sealed class Rule
    {
        public string Id;            // 规则 id（如 R-CO2-OK-01 / R-CO2-OK-02b）
        public string Class;         // 结果类（如 R-OK-02），同一结果类可有多条规则
        public int Specificity;      // 谓词特异性权重，越大越优先
        public Predicate[] When;
        public string Outcome;
        public double CollectRate;
        public double PeakRate;
        public double TotalVolume;
        public string Bubbles;
        public string Limewater;
        public bool WhiteMist;
        public string Flame;
        public string MouthBubble;
        public string DiagnosisId;
        // 时间轴（模拟秒）：fullAt = 验满时刻（0 表示没有验满），s1/tr/s2 = 幕一/转接/幕二
        public double FullAtSec;
        public double Stage1Sec = 22;
        public double TransitionSec = 2;
        public double Stage2Sec = 8;

        public bool Matches(Dictionary<string, string> f)
        {
            foreach (Predicate p in When)
                if (!p.Test(f)) return false;
            return true;
        }
    }

    /// <summary>单个谓词：feature op value，op ∈ eq / ne / any。</summary>
    public sealed class Predicate
    {
        public string Feature;
        public string Op;
        public string Value;

        public static Predicate Eq(string feature, string value)
        {
            return new Predicate { Feature = feature, Op = "eq", Value = value };
        }
        public static Predicate Ne(string feature, string value)
        {
            return new Predicate { Feature = feature, Op = "ne", Value = value };
        }
        public static Predicate Any(string feature)
        {
            return new Predicate { Feature = feature, Op = "any", Value = "" };
        }

        public bool Test(Dictionary<string, string> f)
        {
            string actual;
            if (!f.TryGetValue(Feature, out actual)) actual = "";
            switch (Op)
            {
                case "eq": return string.Equals(actual, Value, StringComparison.Ordinal);
                case "ne": return !string.Equals(actual, Value, StringComparison.Ordinal);
                default: return true;
            }
        }

        public override string ToString()
        {
            return Feature + " " + Op + " " + Value;
        }
    }

    public static class RuleSet
    {
        /// <summary>
        /// 规则 = 结构条件 ∧ 试剂条件的合取（文档 6.3 / 7.5）。
        /// 匹配：全部谓词成立才算命中；多命中取 Specificity 最高；无命中 E-SYS-01。
        /// 关键取舍：
        ///   - 没有"只要 sealed 就成功"的兜底：R-OK-01 必须同时满足加料、液封、插入深度、
        ///     收集方式、玻璃片、试剂、验满、检验全部条件；
        ///   - R-COL-01 只在"有漏斗且未液封"时命中，避免把"没有漏斗"误诊成未液封；
        ///   - 排水法只认 water 边（FeatureExtractor.HasWaterEdge）；该边由选择排水法时
        ///     在状态层真实建立（SetCollector → ConnectWater），不是"场景里摆了个水槽"。
        /// </summary>
        public static List<Rule> Build()
        {
            List<Rule> rules = new List<Rule>();

            // ---- 试剂类：产物/产气异常 ----
            // 固液同瓶直投：大理石已在瓶里又直接倒酸（或酸已在瓶里又直接投固体）＝反应提前触发、加料不可控。
            // 正确操作是固体入瓶、酸经长颈漏斗加入（feed_route=funnel）；试管预装不受此条约束。
            rules.Add(New("R-RXN-04", 105, Codes.OutcomeFail, P(
                    Predicate.Eq("reactor_type", "flask"),
                    Predicate.Eq("feed_route", "direct"),
                    Predicate.Ne("reagent_solid", "none"),
                    Predicate.Ne("reagent_liquid", "none")),
                0.30, 20.0, 420, "1.0", "turbid", false, "none", "none", "D-RXN-04"));

            rules.Add(New("R-RXN-01", 100, Codes.OutcomeFail, P(
                    Predicate.Eq("reagent_liquid", "h2so4_dilute")),
                0.05, 4.0, 30, "0.2→0", "none", false, "keep", "none", "D-RXN-01"));

            // 干扰项：硫酸钙（固体）——不与稀盐酸反应产生 CO₂。给液体量以保留加液/液面演出，
            // bubbles="0" 让合成器不生成任何气泡事件；§10.39 仍安排燃烧木条检验，
            // flame="keep" 让木条一直燃烧（不熄灭、不盖片撤管），直观体现未成功制取 CO₂。
            rules.Add(New("R-RXN-05", 97, Codes.OutcomeFail, P(
                    Predicate.Eq("reagent_solid", "caso4")),
                    0.05, 4.0, 30, "0", "none", false, "keep", "none", "D-RXN-05"));

            rules.Add(New("R-RXN-02", 95, Codes.OutcomePartial, P(
                    Predicate.Eq("reagent_solid", "caco3_powder")),
                    0.70, 40.0, 420, "1.0", "turbid", false, "extinguish", "none", "D-RXN-02"));

            rules.Add(New("R-RXN-03", 90, Codes.OutcomePartial, P(
                    Predicate.Eq("reagent_liquid", "hcl_conc")),
                0.80, 16.0, 420, "1.0", "slight", true, "extinguish", "none", "D-RXN-03"));

            // ---- 无反应：只加水（试剂问题优先于结构小毛病） ----
            rules.Add(New("R-NONE-01", 88, Codes.OutcomeNone, P(
                    Predicate.Eq("reagent_liquid", "water")),
                0, 0, 0, "0", "none", false, "none", "none", "D-NONE-01"));

            // ---- 收集类 ----
            rules.Add(New("R-COL-05", 85, Codes.OutcomeFail, P(
                    Predicate.Eq("collector_mode", "water_displacement")),
                0.15, 15.0, 420, "0.7", "none", false, "none", "slow", "D-COL-05"));

            rules.Add(New("R-COL-04", 82, Codes.OutcomeFail, P(
                    Predicate.Eq("collector_mode", "downward_displacement")),
                0.05, 16.0, 420, "1.0", "none", false, "keep", "none", "D-COL-04"));

            // 未液封：必须"有漏斗"；没有漏斗走 E-STR-10，不是这条
            rules.Add(New("R-COL-01", 78, Codes.OutcomeFail, P(
                    Predicate.Eq("has_funnel", "1"),
                    Predicate.Eq("liquid_seal", "0")),
                0.05, 16.0, 420, "0.9", "none", false, "keep", "none", "D-COL-01"));

            rules.Add(New("R-COL-03", 74, Codes.OutcomeFail, P(
                    Predicate.Eq("collector_cover", "0"),
                    Predicate.Eq("collector_mode", "upward_displacement")),
                0.05, 16.0, 420, "1.0", "none", false, "keep", "none", "D-COL-03"));

            rules.Add(New("R-COL-02", 70, Codes.OutcomeFail, P(
                    Predicate.Eq("tube_depth", "shallow")),
                0.45, 16.0, 420, "1.0", "turbid", false, "keep", "none", "D-COL-02"));

            // ---- 部分达成：缺验满 / 缺检验 ----
            rules.Add(New("R-OK-02", 40, Codes.OutcomePartial, P(
                    Predicate.Eq("has_verification", "0")),
                0.80, 16.0, 420, "1.0", "turbid", false, "none", "none", "D-OK-02", "R-CO2-OK-02a"));

            rules.Add(New("R-OK-02", 40, Codes.OutcomePartial, P(
                    Predicate.Eq("has_limewater", "0")),
                0.80, 16.0, 420, "1.0", "none", false, "extinguish", "none", "D-OK-02", "R-CO2-OK-02b"));

            // ---- 成功：全部条件同时成立，没有兜底 ----
            rules.Add(New("R-OK-01", 10, Codes.OutcomeSuccess, P(
                    Predicate.Eq("sealed", "1"),
                    Predicate.Eq("addition_ok", "1"),
                    Predicate.Eq("tube_depth", "deep"),
                    Predicate.Eq("collector_mode", "upward_displacement"),
                    Predicate.Eq("collector_cover", "1"),
                    Predicate.Eq("reagent_solid", "caco3_lump"),
                    Predicate.Eq("reagent_liquid", "hcl_dilute"),
                    Predicate.Eq("has_verification", "1"),
                    Predicate.Eq("has_limewater", "1")),
                0.80, 16.0, 420, "1.0", "turbid", false, "extinguish", "none", "D-OK-01"));

            foreach (Rule r in rules) ApplyTiming(r);
            return rules;
        }

        /// <summary>按结果给时间轴（模拟秒）。fullAt=0 表示该结果不会验满。</summary>
        private static void ApplyTiming(Rule r)
        {
            switch (r.Id)
            {
                case "R-RXN-04":     r.FullAtSec = 0;  r.Stage1Sec = 6;  r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-RXN-01":     r.FullAtSec = 0;  r.Stage1Sec = 6;  r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-RXN-05":     r.FullAtSec = 0;  r.Stage1Sec = 8;  r.TransitionSec = 2; r.Stage2Sec = 6; break;
                case "R-RXN-02":     r.FullAtSec = 18; r.Stage1Sec = 18; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-RXN-03":     r.FullAtSec = 22; r.Stage1Sec = 22; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-NONE-01":    r.FullAtSec = 0;  r.Stage1Sec = 10; r.TransitionSec = 0; r.Stage2Sec = 0; break;
                case "R-COL-05":     r.FullAtSec = 0;  r.Stage1Sec = 22; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-COL-04":     r.FullAtSec = 0;  r.Stage1Sec = 12; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-COL-01":     r.FullAtSec = 0;  r.Stage1Sec = 12; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-COL-03":     r.FullAtSec = 0;  r.Stage1Sec = 12; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-COL-02":     r.FullAtSec = 0;  r.Stage1Sec = 22; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-CO2-OK-02a": r.FullAtSec = 0;  r.Stage1Sec = 22; r.TransitionSec = 2; r.Stage2Sec = 8; break;
                case "R-CO2-OK-02b": r.FullAtSec = 22; r.Stage1Sec = 22; r.TransitionSec = 0; r.Stage2Sec = 0; break;
                default:             r.FullAtSec = 22; r.Stage1Sec = 22; r.TransitionSec = 2; r.Stage2Sec = 8; break;
            }
        }

        private static Predicate[] P(params Predicate[] preds) { return preds; }

        /// <summary>给规则补时间轴：fullAt=0 表示该结果不会验满。</summary>
        private static Rule At(Rule r, double fullAt, double s1, double tr, double s2)
        {
            r.FullAtSec = fullAt;
            r.Stage1Sec = s1;
            r.TransitionSec = tr;
            r.Stage2Sec = s2;
            return r;
        }

        private static Rule New(string cls, int spec, string outcome, Predicate[] preds,
            double collectRate, double peakRate, double totalVolume,
            string bubbles, string limewater, bool whiteMist,
            string flame, string mouthBubble, string diagnosisId,
            string id = null)
        {
            return new Rule
            {
                Id = id ?? cls,
                Class = cls,
                Specificity = spec,
                Outcome = outcome,
                When = preds,
                CollectRate = collectRate,
                PeakRate = peakRate,
                TotalVolume = totalVolume,
                Bubbles = bubbles,
                Limewater = limewater,
                WhiteMist = whiteMist,
                Flame = flame,
                MouthBubble = mouthBubble,
                DiagnosisId = diagnosisId
            };
        }
    }
}
