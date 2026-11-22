using System;
using System.Collections.Generic;
using Vchem.Core.Model;

namespace Vchem.Core.Judge
{
    /// <summary>
    /// 判定链主入口：三层判定（合法性 → 目标完整性 → 结果类）。
    /// 纯 C#，不引用 UnityEngine，可在 dotnet 控制台直接跑。
    /// </summary>
    public static class Resolver
    {
        public static Outcome Resolve(Plan plan)
        {
            // 第一层：合法性
            List<string> errors = Validator.Validate(plan);
            if (errors.Count > 0)
            {
                Outcome bad = new Outcome();
                bad.Verdict = Codes.OutcomeError;
                bad.Errors = errors;
                // 合法性错误不进 3D，无结果类/参数
                FillDiagnosisForError(bad, errors);
                return bad;
            }

            // 第二层 + 第三层：特征向量 → 规则匹配
            Features f = FeatureExtractor.Extract(plan);
            Dictionary<string, string> fmap = f.ToMap();
            List<Rule> rules = RuleSet.Build();

            Rule best = null;
            foreach (Rule r in rules)
            {
                if (!r.Matches(fmap)) continue;
                if (best == null || r.Specificity > best.Specificity)
                    best = r;
            }

            // 无命中
            if (best == null)
            {
                Outcome unknown = new Outcome();
                unknown.Verdict = Codes.OutcomeError;
                unknown.Errors.Add(Codes.E_SYS_01);
                return unknown;
            }

            return BuildOutcome(best);
        }

        private static Outcome BuildOutcome(Rule r)
        {
            Outcome o = new Outcome();
            o.Class = r.Class;
            o.Verdict = r.Outcome;
            o.MatchedRule = r.Id;

            // M1 阶段 quantities 用标定值；M4 由 Sim 积分替换为实测值。
            // fullAtSec 按结果类给（0 = 该结果不会验满）。
            o.Quantities = new Quantities
            {
                CollectRate = r.CollectRate,
                PeakRateMlPerSec = r.PeakRate,
                TotalGasMl = r.TotalVolume,
                FullAtSec = r.FullAtSec
            };

            o.Phenomena = new Phenomena
            {
                Bubbles = r.Bubbles,
                Limewater = r.Limewater,
                WhiteMist = r.WhiteMist,
                Flame = r.Flame,
                MouthBubble = r.MouthBubble
            };

            o.Diagnosis = new Diagnosis
            {
                Id = r.DiagnosisId,
                Summary = DiagnosisTable.Summary(r.DiagnosisId),
                Checklist = DiagnosisTable.Checklist(r.DiagnosisId)
            };

            // 三段时长：幕一 / 转接 / 幕二；转接或幕二为 0 时不写该段
            o.Stages.Add(new Stage { Index = 1, DurationSec = r.Stage1Sec });
            if (r.TransitionSec > 0)
                o.Stages.Add(new Stage { Index = "transition", DurationSec = r.TransitionSec });
            if (r.Stage2Sec > 0)
                o.Stages.Add(new Stage { Index = 2, DurationSec = r.Stage2Sec });
            return o;
        }

        private static void FillDiagnosisForError(Outcome o, List<string> errors)
        {
            o.Diagnosis = new Diagnosis
            {
                Id = "D-SYS-STRUCT",
                Summary = "结构还不完整：按右侧提示补齐缺少的器材或试剂。",
                Checklist = new List<string>()
            };
            foreach (string e in errors)
            {
                o.Diagnosis.Checklist.Add(e + " " + Codes.Message(e));
            }
        }
    }
}
