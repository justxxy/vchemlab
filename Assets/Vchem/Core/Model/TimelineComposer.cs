using System;
using System.Collections.Generic;
using Vchem.Core.Contracts;

namespace Vchem.Core.Model
{
    /// <summary>
    /// Outcome → 事件流（timeline.json）。M4 真事件流未接入前，用判定结果里的
    /// 结果类、量值曲线、现象参数和三段时长确定性地合成演出流，字段与 timeline.demo.json 一致。
    /// 纯 C#，不引用 UnityEngine，可在 JudgeRunner 里单独验证。
    /// </summary>
    public static class TimelineComposer
    {
        public static string Compose(Plan plan, Outcome outcome)
        {
            if (plan == null || outcome == null)
                throw new ArgumentNullException(plan == null ? "plan" : "outcome");

            double s1 = 22.0, tr = 2.0, s2 = 8.0;
            if (outcome.Stages != null)
                foreach (Stage st in outcome.Stages)
                {
                    if (st == null) continue;
                    string idx = Convert.ToString(st.Index, System.Globalization.CultureInfo.InvariantCulture);
                    if (idx == "1") s1 = st.DurationSec;
                    else if (idx == "transition") tr = st.DurationSec;
                    else if (idx == "2") s2 = st.DurationSec;
                }
            if (s1 < 1.0) s1 = 1.0;
            if (tr < 0.0) tr = 0.0;
            if (s2 < 0.0) s2 = 0.0;
            double total = s1 + tr + s2;
            if (total < 1.0) total = 1.0;

            Quantities q = outcome.Quantities ?? new Quantities();
            Phenomena ph = outcome.Phenomena ?? new Phenomena();
            double gas = Math.Max(0.0, q.TotalGasMl);
            double rate = Clamp01(q.CollectRate);
            double fullAt = Math.Max(0.0, Math.Min(s1, q.FullAtSec));

            var root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["schema"] = "vchem.timeline/1";
            root["class"] = outcome.Class ?? "";
            root["note"] = "由当前提交的 Outcome 确定性合成（M4 真事件流接入前）";
            root["plan"] = "submitted plan";
            root["totalSec"] = total;
            root["outcome"] = outcome.Verdict ?? "";

            var stages = new List<object>();
            stages.Add(StageObj(1, s1));
            if (tr > 0.0) stages.Add(StageObj("transition", tr));
            if (s2 > 0.0) stages.Add(StageObj(2, s2));
            root["stages"] = stages;

            root["curve"] = BuildCurve(total, s1, gas, rate);

            var events = new List<object>();
            Add(events, 0.0, s1, tr, "run.start", "{}");
            Add(events, 0.6, s1, tr, "feed.add", Pair("slot", "liquid", "reagent", Feed(plan, "liquid")));
            Add(events, 0.6, s1, tr, "feed.add", Pair("slot", "solid", "reagent", Feed(plan, "solid")));

            Node reactor = FindType(plan, "reactor.flask_conical") ?? FindType(plan, "reactor.test_tube");
            Node funnel = FindType(plan, "feed.thistle_funnel");
            if (funnel != null && reactor != null && gas > 0.0)
                Add(events, 0.8, s1, tr, "feed.flow",
                    Pair("from", funnel.Id, "to", reactor.Id, "durationSec", 2.2));

            if (HasWater(plan))
                Add(events, 1.0, s1, tr, "water.level", Pair("fraction", 0.60));

            if (gas > 0.0)
            {
                // §10.27：加液阶段液位一次到位（0.42，足以淹没漏斗下端形成液封），
                // 反应中不再变化——液体/大理石静止，只冒泡。旧版 0.18→0.42 的二次升高已移除。
                Add(events, 1.1, s1, tr, "liquid.level", Pair("fraction", 0.42));
                Add(events, 2.1, s1, tr, "reaction.start", Pair("rule", outcome.MatchedRule ?? ""));

                // §10.36：bubbles 支持 "a→b" 递减曲线（稀硫酸 R-RXN-01 的 "0.2→0"）。
                // 旧实现只取箭头前的起始值且 s1=6 不触发第三段 → 恒定气泡，无法表达"越来越少"。
                // 播放器 SampleBubbleKeys 已支持多锚点平滑过渡，这里按幕一长度铺 4 个递减锚点。
                string bubSpec = ph.Bubbles ?? "";
                double bub = Level(bubSpec);
                if (bub > 0.0)
                {
                    Add(events, 2.4, s1, tr, "bubbles.begin", Pair("intensity", bub));
                    double endBub = LevelTail(bubSpec);
                    if (endBub < bub)
                    {
                        // 递减曲线：从幕一 30% 到 95% 均匀铺 4 档，末档到 endBub。
                        double tFrom = Math.Max(3.0, s1 * 0.30);
                        double tTo = s1 * 0.95;
                        for (int i = 1; i <= 4; i++)
                        {
                            double u = i / 4.0;
                            double t = tFrom + (tTo - tFrom) * u;
                            double v = bub + (endBub - bub) * u;
                            Add(events, t, s1, tr, "bubbles.rate", Pair("intensity", v));
                        }
                    }
                    else
                    {
                        Add(events, Math.Max(3.0, s1 * 0.30), s1, tr, "bubbles.rate", Pair("intensity", bub));
                        if (s1 > 12.0)
                            Add(events, s1 * 0.90, s1, tr, "bubbles.rate", Pair("intensity", bub * 0.72));
                    }
                }
                if (ph.WhiteMist)
                    Add(events, 0.9, s1, tr, "mist.set", Pair("value", 1.0));

                string mouth = (ph.MouthBubble ?? "none").ToLowerInvariant();
                if (mouth == "slow" || mouth == "continuous")
                    Add(events, s1 * 0.40, s1, tr, "mouth.bubble",
                        Pair("intensity", mouth == "slow" ? 0.40 : 0.80));
            }

            // §10.39：未成功制取 CO₂ 的规则 fullAt=0（不会集满），但仍要安排燃烧木条检验。
            // 用幕一 60% 处作为"检验时刻"；只有 flame=keep 才走这条兜底，不改变 Outcome.Quantities。
            double verifyAt = fullAt;
            if (verifyAt <= 0.0 && ph.Flame == "keep") verifyAt = Math.Max(2.4, s1 * 0.60);
            if (verifyAt > 0.0 && ph.Flame != "none" && !string.IsNullOrEmpty(ph.Flame))
            {
                // 提前 1.6s 开始检验，让木条有"上方下探→瓶口停留燃烧→熄灭"的完整过程。
                Add(events, Math.Max(1.0, verifyAt - 1.6), s1, tr, "verify.full.begin",
                    Pair("method", Burning(plan) ? "burning_splint" : "bubble"));
                Add(events, verifyAt, s1, tr, "verify.full",
                    Pair("method", Burning(plan) ? "burning_splint" : "bubble",
                         "result", ph.Flame == "keep" ? "keep" : "extinguished"));
            }

            if (tr > 0.0)
            {
                Add(events, s1 + 0.4, s1, tr, "transfer", Pair("to", "limewater"));
                // §10.39：默认机位改为示例近景后，推镜 0.30→0.12m，避免幕二贴脸出画（四处同步）。
                Add(events, s1 + 0.9, s1, tr, "camera.push", Pair("dz", 0.12, "dpitch", -2.0));
            }

            string lime = (ph.Limewater ?? "none").ToLowerInvariant();
            if (s2 > 0.0 && lime != "none" && lime != "")
            {
                double turb = lime == "turbid" ? 0.62 : lime == "slight" ? 0.25 : 0.0;
                double baseT = s1 + tr;
                Add(events, baseT + 1.0, s1, tr, "test.start", Pair("reagent", "limewater"));
                Add(events, baseT + 1.2, s1, tr, "turbidity.set", Pair("value", turb));
                Add(events, baseT + 2.0, s1, tr, "test.turbid", Pair("turbidity", turb));
                if (turb > 0.5)
                {
                    Add(events, baseT + 4.0, s1, tr, "turbidity.set", Pair("value", Math.Min(0.92, turb + 0.20)));
                    Add(events, baseT + 7.0, s1, tr, "turbidity.set", Pair("value", Math.Min(0.95, turb + 0.30)));
                }
            }

            Add(events, total, s1, tr, "run.end", Pair("outcome", outcome.Verdict ?? "success"));
            root["events"] = events;

            var keys = new List<object>();
            AddKey(keys, Math.Min(1.4, total * 0.1), "feed");
            AddKey(keys, Math.Min(8.0, s1 * 0.4), "reaction");
            if (verifyAt > 0.0) AddKey(keys, Math.Max(0.1, verifyAt - 0.2), "verify");
            if (tr > 0.0) AddKey(keys, s1 + tr * 0.7, "transfer");
            if (s2 > 0.0) AddKey(keys, total - 1.0, "result");
            root["keyframes"] = keys;
            return Json.Stringify(root);
        }

        private static Dictionary<string, object> StageObj(object index, double duration)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["index"] = index;
            o["durationSec"] = duration;
            return o;
        }

        private static List<object> BuildCurve(double total, double s1, double gas, double rate)
        {
            var list = new List<object>();
            double[] times = { 0.0, s1 * 0.45, s1, Math.Min(total, s1 + 2.0), total };
            double[] fractions = { 0.0, 0.35, 0.71, 0.84, 1.0 };
            for (int i = 0; i < times.Length; i++)
            {
                double cum = gas * fractions[i];
                double arr = cum * rate;
                double held = arr * (i == times.Length - 1 && gas > 0.0 ? 0.75 : 1.0);
                var p = new Dictionary<string, object>(StringComparer.Ordinal);
                p["t"] = times[i];
                p["cumulative"] = cum;
                p["arrived"] = arr;
                p["held"] = held;
                list.Add(p);
            }
            return list;
        }

        private static void Add(List<object> events, double t, double s1, double tr, string type, string payload)
        {
            var e = new Dictionary<string, object>(StringComparer.Ordinal);
            e["t"] = t;
            e["stage"] = t < s1 ? "1" : t < s1 + tr ? "transition" : "2";
            e["type"] = type;
            e["payload"] = Json.ParseObject(payload);
            events.Add(e);
        }

        private static void AddKey(List<object> keys, double t, string label)
        {
            var k = new Dictionary<string, object>(StringComparer.Ordinal);
            k["t"] = t; k["stage"] = "1"; k["label"] = label;
            keys.Add(k);
        }

        private static string Pair(string k1, object v1)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o[k1] = v1;
            return Json.Stringify(o);
        }

        private static string Pair(string k1, object v1, string k2, object v2)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o[k1] = v1; o[k2] = v2;
            return Json.Stringify(o);
        }

        private static string Pair(string k1, object v1, string k2, object v2, string k3, object v3)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o[k1] = v1; o[k2] = v2; o[k3] = v3;
            return Json.Stringify(o);
        }

        private static double Clamp01(double v) { return v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v; }

        private static string Feed(Plan plan, string slot)
        {
            if (plan.Feeds == null) return "";
            return slot == "solid" ? (plan.Feeds.Solid ?? "") : (plan.Feeds.Liquid ?? "");
        }

        private static Node FindType(Plan plan, string type)
        {
            if (plan.Topology == null) return null;
            foreach (Node n in plan.Topology.Nodes)
                if (n != null && n.Type == type) return n;
            return null;
        }

        private static bool HasWater(Plan plan)
        {
            if (plan.Topology == null) return false;
            foreach (Node n in plan.Topology.Nodes)
                if (n != null && n.Type == "collect.water_trough") return true;
            foreach (Edge e in plan.Topology.Edges)
                if (e != null && e.Kind == "water") return true;
            return false;
        }

        private static bool Burning(Plan plan)
        {
            if (plan.Verification == null) return true;
            return plan.Verification.Fullness != "bubble";
        }

        /// <summary>"0.2→0" 取起始强度；"1.0" 直接取；"0"/空 返回 0。</summary>
        private static double Level(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0.0;
            string s = text.Split('→')[0].Split('-')[0].Trim();
            double v;
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? Clamp01(v) : 0.0;
        }

        /// <summary>
        /// §10.36：取递减串 "0.2→0" 的末值（0）；无箭头/无法解析时返回 2.0（>起始值，
        /// 让调用方走恒定分支），避免误把恒定强度当成递减。
        /// </summary>
        private static double LevelTail(string text)
        {
            if (string.IsNullOrEmpty(text)) return 2.0;
            string[] parts = text.Split('→');
            if (parts.Length < 2) return 2.0;
            double v;
            return double.TryParse(parts[parts.Length - 1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? Clamp01(v) : 2.0;
        }
    }
}
