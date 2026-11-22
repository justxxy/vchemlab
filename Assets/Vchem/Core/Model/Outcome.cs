using System;
using System.Collections.Generic;
using Vchem.Core.Contracts;

namespace Vchem.Core.Model
{
    /// <summary>
    /// 判定输出（result.json）。判定层只产出"结果类 + 参数 + 诊断 + 结构错误"；
    /// quantities 里的运行实测值在 M4 由 Sim 积分填入，M1 先用标定值。
    /// </summary>
    public sealed class Outcome
    {
        public string Schema = "vchem.result/1";
        /// <summary>命中的结果类，如 R-OK-01；合法性错误时为 null。</summary>
        public string Class;
        /// <summary>success / partial / fail / none / error（字段名避开与类名冲突，序列化时仍输出为 outcome）</summary>
        public string Verdict;
        public List<string> Errors = new List<string>();
        public Quantities Quantities;
        public Phenomena Phenomena;
        public Diagnosis Diagnosis;
        public List<Stage> Stages = new List<Stage>();
        /// <summary>命中的规则 id，便于审计。</summary>
        public string MatchedRule;

        public string ToJson()
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["schema"] = Schema;
            root["class"] = Class;
            root["outcome"] = Verdict;
            root["matchedRule"] = MatchedRule;

            if (Errors.Count > 0)
            {
                List<object> errs = new List<object>();
                foreach (string e in Errors) errs.Add(e);
                root["errors"] = errs;
            }

            if (Quantities != null)
            {
                Dictionary<string, object> q = new Dictionary<string, object>(StringComparer.Ordinal);
                q["collectRate"] = Quantities.CollectRate;
                q["peakRateMlPerSec"] = Quantities.PeakRateMlPerSec;
                q["totalGasMl"] = Quantities.TotalGasMl;
                q["fullAtSec"] = Quantities.FullAtSec;
                root["quantities"] = q;
            }

            if (Phenomena != null)
            {
                Dictionary<string, object> ph = new Dictionary<string, object>(StringComparer.Ordinal);
                ph["bubbles"] = Phenomena.Bubbles;
                ph["limewater"] = Phenomena.Limewater;
                ph["whiteMist"] = Phenomena.WhiteMist;
                ph["flame"] = Phenomena.Flame;
                ph["mouthBubble"] = Phenomena.MouthBubble;
                root["phenomena"] = ph;
            }

            if (Diagnosis != null)
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                d["diagnosisId"] = Diagnosis.Id;
                d["summary"] = Diagnosis.Summary;
                List<object> list = new List<object>();
                if (Diagnosis.Checklist != null)
                    foreach (string c in Diagnosis.Checklist) list.Add(c);
                d["checklist"] = list;
                root["diagnosis"] = d;
            }

            List<object> stages = new List<object>();
            foreach (Stage s in Stages)
            {
                Dictionary<string, object> so = new Dictionary<string, object>(StringComparer.Ordinal);
                so["index"] = s.Index;
                so["durationSec"] = s.DurationSec;
                stages.Add(so);
            }
            root["stages"] = stages;
            return Json.Stringify(root);
        }
    }

    public sealed class Quantities
    {
        public double CollectRate;
        public double PeakRateMlPerSec;
        public double TotalGasMl;
        public double FullAtSec;
    }

    /// <summary>现象基元参数（见文档 7.6 / 8.2）。</summary>
    public sealed class Phenomena
    {
        /// <summary>相对强度 0–1；字符串以便表达 "0.2→0"。</summary>
        public string Bubbles;
        /// <summary>none / slight / turbid</summary>
        public string Limewater;
        public bool WhiteMist;
        /// <summary>extinguish / keep / none</summary>
        public string Flame;
        /// <summary>none / slow / continuous</summary>
        public string MouthBubble;
    }

    public sealed class Diagnosis
    {
        public string Id;
        public string Summary;
        public List<string> Checklist = new List<string>();
    }

    public sealed class Stage
    {
        /// <summary>1 / "transition" / 2</summary>
        public object Index;
        public double DurationSec;
    }
}
