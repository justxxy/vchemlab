using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Vchem.Core.Contracts;

namespace Vchem.Showcase
{
    /// <summary>
    /// WP5 事件流播放器：纯 C#，不依赖 UnityEngine。
    /// Evaluate(t) 是纯函数——对同一 t 永远得到同一 ShowcaseState，
    /// 因此随机 Seek 与顺序播放结果一致（G3 确定性判据）。
    /// 事件流字段与 M4 真流一致：t / stage / type / payload。
    /// </summary>
    public sealed class TimelinePlayer
    {
        public sealed class Event
        {
            public float T;
            public string Stage;
            public string Type;
            public Dictionary<string, object> Payload;
        }

        public sealed class CurvePoint
        {
            public float T;
            public float Cumulative, Arrived, Held;
        }

        /// <summary>M6：一条液流窗口 [Start,End]，多条 feed.flow 取包络并集而非互相覆盖。</summary>
        private struct FeedWindow
        {
            public float Start, End;
        }

        /// <summary>M6：一个气泡强度锚点 (t,target)，相邻锚点间从上一目标插值，不归零重升。</summary>
        private struct BubbleKey
        {
            public float T, Target;
        }

        private readonly List<Event> _events = new List<Event>();
        private readonly List<CurvePoint> _curve = new List<CurvePoint>();
        private readonly List<FeedWindow> _feedWindows = new List<FeedWindow>();
        private readonly List<BubbleKey> _bubbleKeys = new List<BubbleKey>();
        /// <summary>M6：时间轴是否显式声明过 liquid.level；有则不再用累计产气兜底覆盖显式 0（空瓶）。</summary>
        private bool _hasLiquidEvent;
        /// <summary>§10.39：预处理的验满结果时刻。下探发生在 full 之前，不能等事件循环走到 flameAt 才算窗口。</summary>
        private float _verifyFullAt = -1f;
        public string ClassName { get; private set; }
        /// <summary>M1：时间轴声明其配套方案文件（如 R-OK-01.plan.json），用于换轴时防错配。</summary>
        public string PlanName { get; private set; }
        public string Schema { get; private set; }
        public float TotalSec { get; private set; }

        public IList<Event> Events { get { return _events; } }

        /// <summary>
        /// M7：时间轴里"阶段标记"类事件不直接驱动通道，但属于合法类型；
        /// 其余未知 type 登记在这里，不再静默丢弃，也不让整条时间轴崩掉。
        /// </summary>
        public readonly List<string> UnknownEvents = new List<string>();
        private static readonly HashSet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "feed.add", "feed.flow", "reaction.start", "bubbles.begin", "bubbles.rate",
            "liquid.level", "water.level", "turbidity.set", "test.turbid", "test.start",
            "mist.set", "verify.full.begin", "verify.full", "mouth.bubble",
            "camera.push", "transfer", "run.start", "run.end"
        };

        public static TimelinePlayer FromJson(string text)
        {
            Dictionary<string, object> root;
            try { root = Json.ParseObject(text); }
            catch (Exception e) { throw new FormatException("timeline JSON 解析失败: " + e.Message); }
            var p = new TimelinePlayer();
            p.Schema = Json.GetString(root, "schema", "");
            p.ClassName = Json.GetString(root, "class", "");
            p.PlanName = Json.GetString(root, "plan", "");
            p.TotalSec = (float)Json.GetNumber(root, "totalSec", 32.0);

            List<object> curve = Json.GetArray(root, "curve");
            if (curve != null)
                foreach (object o in curve)
                {
                    Dictionary<string, object> m = Json.AsObject(o);
                    p._curve.Add(new CurvePoint
                    {
                        T = (float)Json.GetNumber(m, "t", 0),
                        Cumulative = (float)Json.GetNumber(m, "cumulative", 0),
                        Arrived = (float)Json.GetNumber(m, "arrived", 0),
                        Held = (float)Json.GetNumber(m, "held", 0)
                    });
                }
            p._curve.Sort((a, b) => a.T.CompareTo(b.T));

            List<object> events = Json.GetArray(root, "events");
            if (events != null)
                foreach (object o in events)
                {
                    Dictionary<string, object> m = Json.AsObject(o);
                    string type = Json.GetString(m, "type", "");
                    if (!string.IsNullOrEmpty(type) && !KnownTypes.Contains(type))
                        p.UnknownEvents.Add(type + "@" +
                            Json.GetNumber(m, "t", 0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                    p._events.Add(new Event
                    {
                        T = (float)Json.GetNumber(m, "t", 0),
                        Stage = Json.GetString(m, "stage", "1"),
                        Type = type,
                        Payload = Json.GetObject(m, "payload") ?? new Dictionary<string, object>(StringComparer.Ordinal)
                    });
                }
            // M6：连续通道预处理成段集合。这样 Evaluate 只依赖 t，仍是纯函数。
            foreach (Event e in p._events)
            {
                switch (e.Type)
                {
                    case "feed.flow":
                    {
                        float dur = MathfMax(0.01f, (float)Json.GetNumber(e.Payload, "durationSec", 2.0));
                        p._feedWindows.Add(new FeedWindow { Start = e.T, End = e.T + dur });
                        break;
                    }
                    case "bubbles.begin":
                    case "bubbles.rate":
                    {
                        float target = MathfClamp01((float)Json.GetNumber(e.Payload, "intensity", 0.7));
                        p._bubbleKeys.Add(new BubbleKey { T = e.T, Target = target });
                        break;
                    }
                    case "liquid.level":
                        p._hasLiquidEvent = true;
                        break;
                    case "verify.full":
                        if (p._verifyFullAt < 0f) p._verifyFullAt = e.T;
                        break;
                }
            }
            p._bubbleKeys.Sort((a, b) => a.T.CompareTo(b.T));

            // 稳定排序：同 t 按事件出现顺序（List.Sort 不稳定，用 t + 序号）。
            var indexed = new List<KeyValuePair<int, Event>>();
            for (int i = 0; i < p._events.Count; i++) indexed.Add(new KeyValuePair<int, Event>(i, p._events[i]));
            indexed.Sort((x, y) =>
            {
                int c = x.Value.T.CompareTo(y.Value.T);
                return c != 0 ? c : x.Key.CompareTo(y.Key);
            });
            p._events.Clear();
            foreach (var kv in indexed) p._events.Add(kv.Value);
            return p;
        }

        /// <summary>给定模拟时间求状态。纯函数，可任意 Seek。</summary>
        public ShowcaseState Evaluate(float t)
        {
            // M7：t<0 / t>duration / NaN 都夹到合法区间；Evaluate 是公开 API，不能靠调用方自觉。
            float span = TotalSec > 0f ? TotalSec : 32f;
            if (float.IsNaN(t)) t = 0f;
            if (t < 0f) t = 0f;
            else if (t > span) t = span;
            var s = new ShowcaseState { T = t, Stage = "1" };
            // 曲线量（连续插值，按 t 取段）。
            SampleCurve(t, out s.CumulativeMl, out s.ArrivedMl, out s.HeldMl);

            float camEnd = -1f, mistEnd = -1f;
            float camFrom = 0f, mistFrom = 0f;
            float flameAt = -1f, flameBeginAt = -1f;
            bool flameExtinguishes = false;
            int stageRank = 0; // M5：stage 只允许前进，缺字段事件不能把幕重置回 1

            // 重放所有 t'<=t 的事件：每条事件把对应通道设成目标值 + 过渡时长。
            foreach (Event e in _events)
            {
                if (e.T > t) break;
                string ns = NormalizeStage(e.Stage);
                // 1 → transition → 2：transition 是过渡幕，必须排在正幕 2 之前，
                // 否则幕二的 stage=2 事件会被误判为"回退"而拒绝。
                int rank = ns == "1" ? 1 : ns == "transition" ? 2 : 3;
                if (rank >= stageRank) { s.Stage = ns; stageRank = rank; }
                Dictionary<string, object> pl = e.Payload;
                switch (e.Type)
                {
                    case "feed.flow":
                    case "bubbles.begin":
                    case "bubbles.rate":
                        // M6：这三类连续通道已在 FromJson 预处理为段集合，在下方统一按 t 求值；
                        // 不再在这里用单变量覆盖——否则后一条事件会截断前一条的窗口/把强度打到 0。
                        break;
                    case "liquid.level":
                    {
                        s.LiquidLevel = MathfClamp01((float)Json.GetNumber(pl, "fraction", 0f));
                        break;
                    }
                    case "water.level":
                    {
                        s.WaterLevel = MathfClamp01((float)Json.GetNumber(pl, "fraction", 0f));
                        break;
                    }
                    case "turbidity.set":
                    case "test.turbid":
                    {
                        float target = MathfClamp01((float)Json.GetNumber(pl, "turbidity",
                            Json.GetNumber(pl, "value", 0f)));
                        s.Turbidity = target;
                        break;
                    }
                    case "mist.set":
                    {
                        mistFrom = MathfClamp01((float)Json.GetNumber(pl, "value", 0f));
                        mistEnd = e.T + 0.6f;
                        break;
                    }
                    case "verify.full.begin":
                    {
                        // §10.39：木条从瓶口上方开始下探，不是凭空出现在瓶口。
                        flameBeginAt = e.T;
                        s.FlameApproach = 0f;
                        s.Flame = "burning";
                        break;
                    }
                    case "verify.full":
                    {
                        string result = Json.GetString(pl, "result", "extinguished");
                        flameAt = e.T;
                        flameExtinguishes = result != "keep";
                        s.FlameKept = !flameExtinguishes;
                        s.Flame = flameExtinguishes ? "extinguishing" : "burning";
                        s.FlameApproach = 1f; // 抵达瓶口后保持，熄灭/持续燃烧都发生在瓶口
                        break;
                    }
                    case "mouth.bubble":
                    {
                        s.MouthBubble = MathfClamp01((float)Json.GetNumber(pl, "intensity", 0.6));
                        break;
                    }
                    case "camera.push":
                    {
                        // M4：事件参数进入状态；默认值即装配近景下的安全收口值。
                        // Director 只消费这两个字段，不再写死数值；兜底值随 §10.39 同步到 0.12。
                        s.CameraPushDz = (float)Json.GetNumber(pl, "dz", 0.12);
                        s.CameraPushPitch = (float)Json.GetNumber(pl, "dpitch", -2.0);
                        camFrom = 1f; camEnd = e.T + 1.2f;
                        break;
                    }
                    case "run.end":
                    {
                        // 结果由事件 payload 决定：失败方案不能演成成功。
                        string outcome = Json.GetString(pl, "outcome", "success");
                        s.Bullet = outcome == "success" || outcome == "partial" ||
                                   outcome == "fail" || outcome == "none" ? outcome : "success";
                        break;
                    }
                }
            }

            // 连续通道求值（M6）：全部只依赖 t 与静态段集合，无跨帧累计状态，仍是纯函数。
            // 1) 液流：多个窗口取包络并集——任一条窗口在 t 处"开"，FeedFlow 就亮；
            //    窗口尾部 0.5s 线性收尾。新事件不再截断旧窗口。
            s.FeedFlow = SampleFeedWindows(t);
            // 2) 气泡：相邻锚点间从上一段目标过渡到本段目标（0.35s），不再每次从 0 重升造成突变。
            s.Bubbles = SampleBubbleKeys(t);
            if (camEnd >= 0f) s.CameraPush = MathfLerp(0f, camFrom, MathfClamp01((t - (camEnd - 1.2f)) / 1.2f));
            if (mistEnd >= 0f) s.WhiteMist = MathfLerp(0f, mistFrom, MathfClamp01((t - (mistEnd - 0.6f)) / 0.6f));

            // §10.39：木条下探——先从上方向瓶口移动，抵达后至少停留 0.5s 再触发熄灭。
            // 纯函数：只依赖 t 与两个事件时刻，Seek 可回退。
            if (s.Flame != "none" && flameBeginAt >= 0f)
            {
                // 结果时刻在 full 事件里，下探阶段它还没进入事件循环；
                // 用预处理得到的 _verifyFullAt，否则 flameAt=-1 会把窗口算成 0.1s，木条一出现就贴在瓶口。
                float resultAt = _verifyFullAt >= 0f ? _verifyFullAt : flameAt;
                float spanToResult = MathfMax(0.1f, resultAt - flameBeginAt);
                float approachSec = MathfMax(0.25f, spanToResult - 0.5f);
                if (approachSec > spanToResult) approachSec = spanToResult;
                s.FlameApproach = MathfClamp01((t - flameBeginAt) / approachSec);
            }
            else s.FlameApproach = 1f;

            // 火焰在 0.8s 内衰减到熄灭；不再给木条加水平抖动。
            // §10.40：衰减量由 flameAt 纯函数推出，不再让演出层硬编码检验时刻。
            if (flameAt >= 0f && s.Flame == "extinguishing")
            {
                s.FlameFade = 1f - MathfClamp01((t - flameAt) / 0.8f);
                if (t >= flameAt + 0.8f) { s.Flame = "extinguished"; s.FlameFade = 0f; }
            }
            else if (s.Flame == "burning") s.FlameFade = 1f;
            else if (s.Flame == "extinguished") s.FlameFade = 0f;

            // §10.39：熄灭后木条竖直上撤 0.6s，全程无水平位移；上撤完成才开始盖片。
            // withdrawAt 只由 flameAt 与结果类型推出，仍是纯函数，Seek 可回退。
            float withdrawAt = (flameAt >= 0f && flameExtinguishes) ? flameAt + 0.8f : -1f;
            s.FlameWithdraw = withdrawAt < 0f ? 0f : MathfClamp01((t - withdrawAt) / 0.6f);

            // 熄灭后收尾三段（§10.25），全部由 t 与 flameAt 确定性推出，可 Seek：
            // A 玻璃片从右侧滑到贴住导管（0.6s）→ B 扭转并撤下导管（0.9s）→
            // C 玻璃片滑到完全盖住瓶口（0.7s）。以木条撤离瓶口的时刻为基准，避免盖片与木条交叉。
            // §10.39：只有"木条熄灭"才收口；木条持续燃烧（CO₂ 未集满/未制取）不得盖片撤管。
            float outAt = withdrawAt < 0f ? -1f : withdrawAt + 0.6f;
            s.CoverSlide = outAt < 0f ? 0f : MathfClamp01((t - outAt) / 0.6f);
            s.ConduitWithdraw = outAt < 0f ? 0f : MathfClamp01((t - outAt - 0.6f) / 0.9f);
            s.CoverSeal = outAt < 0f ? 0f : MathfClamp01((t - outAt - 1.5f) / 0.7f);

            // 液面兜底（M6）：仅当整条时间轴从未显式声明 liquid.level 时，才按累计产气量近似淡液柱。
            // 若轴上有显式 liquid.level（哪怕 fraction=0 表达"空瓶"），就尊重显式值，不再被兜底覆盖。
            if (!_hasLiquidEvent && s.LiquidLevel <= 0f && s.CumulativeMl > 0f)
                s.LiquidLevel = MathfClamp01(s.CumulativeMl / 420f) * 0.5f;
            return s;
        }

        /// <summary>演出内容哈希：对 101 个采样点的状态序列取 SHA1。</summary>
        public string Hash()
        {
            var sb = new StringBuilder();
            for (int i = 0; i <= 100; i++)
            {
                float t = TotalSec * i / 100f;
                sb.Append(Evaluate(t).Serialize()).Append('\n');
            }
            using (var sha = SHA1.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder();
                foreach (byte b in bytes) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        private void SampleCurve(float t, out float cum, out float arr, out float held)
        {
            cum = arr = held = 0f;
            if (_curve.Count == 0) return;
            if (t <= _curve[0].T)
            {
                cum = _curve[0].Cumulative; arr = _curve[0].Arrived; held = _curve[0].Held; return;
            }
            for (int i = 1; i < _curve.Count; i++)
            {
                CurvePoint b = _curve[i];
                if (t > b.T && i < _curve.Count - 1) continue;
                CurvePoint a = _curve[i - 1];
                float span = MathfMax(0.0001f, b.T - a.T);
                float u = MathfClamp01((t - a.T) / span);
                cum = MathfLerp(a.Cumulative, b.Cumulative, u);
                arr = MathfLerp(a.Arrived, b.Arrived, u);
                held = MathfLerp(a.Held, b.Held, u);
                return;
            }
        }

        /// <summary>M6：液流多窗口包络。任一窗口在 [Start, End) 内为 1，末 0.5s 线性收尾；取最大值。</summary>
        private float SampleFeedWindows(float t)
        {
            float best = 0f;
            for (int i = 0; i < _feedWindows.Count; i++)
            {
                FeedWindow w = _feedWindows[i];
                if (t < w.Start || t >= w.End) continue;
                float level = MathfClamp01((w.End - t) / 0.5f) * StepOn(t, w.Start);
                if (level > best) best = level;
            }
            return best;
        }

        /// <summary>
        /// M6：气泡强度分段求值。锚点已按 t 升序；在 t 处的目标取自"最后一个 t'<=t 的锚点"，
        /// 并在其 0.35s 内从上一个锚点的目标线性过渡（第一个锚点从 0 起）。这样调整强度是平滑的，
        /// 不会像旧实现那样每条事件都先归零再升起。离散跳变（如幕二重新起泡）由 event.t 决定。
        /// </summary>
        private float SampleBubbleKeys(float t)
        {
            if (_bubbleKeys.Count == 0) return 0f;
            int idx = -1;
            for (int i = 0; i < _bubbleKeys.Count; i++)
                if (_bubbleKeys[i].T <= t) idx = i; else break;
            if (idx < 0) return 0f;
            float from = idx == 0 ? 0f : _bubbleKeys[idx - 1].Target;
            float to = _bubbleKeys[idx].Target;
            float u = MathfClamp01((t - _bubbleKeys[idx].T) / 0.35f);
            return MathfLerp(from, to, u);
        }

        private static string NormalizeStage(string stage)
        {
            if (string.IsNullOrEmpty(stage)) return "1";
            if (stage == "1" || stage == "2") return stage;
            if (stage.StartsWith("transition", StringComparison.Ordinal)) return "transition";
            // 数字 1 / 2（Json.GetString 对 double 会转成 "1"/"2"）。
            double d;
            if (double.TryParse(stage, out d)) return d < 1.5 ? "1" : d < 2.5 ? "2" : "transition";
            return stage;
        }

        private static float StepOn(float t, float edge) { return t >= edge ? 1f : 0f; }
        private static float MathfClamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }
        private static float MathfMax(float a, float b) { return a > b ? a : b; }
        private static float MathfLerp(float a, float b, float u) { return a + (b - a) * MathfClamp01(u); }
    }
}
