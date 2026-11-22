using Vchem.Showcase;

namespace Vchem.Showcase
{
    /// <summary>
    /// 演示阶段字幕的唯一来源（§10.36）。Editor 桥接与 Player 播放都委托这里，
    /// 避免两份字幕文本再次漂移。字幕只依赖当前状态快照 + 时间轴 class，
    /// 不进 ShowcaseState.Serialize()，因此不改变 G3 演出哈希。
    /// </summary>
    public static class ShowcaseCaption
    {
        /// <summary>
        /// cls 来自时间轴的 class 字段（R-OK-01 / R-RXN-01 / R-RXN-05 …）。
        /// 干扰项（稀硫酸、硫酸钙）覆盖对应阶段的说明文字，标准方案保持原文案。
        /// </summary>
        public static string For(ShowcaseState s, string cls)
        {
            string c = cls ?? "";
            if (c == "R-RXN-05") return Caso4(s);
            if (c == "R-RXN-01") return H2so4(s);
            return Standard(s);
        }

        // ---- 标准方案：文案保持不变，保证既有验收与用户预期一致 ----
        private static string Standard(ShowcaseState s)
        {
            if (s.Turbidity > 0.1f) return "实验完成 · CO₂ 已成功制取并集满，验满成立";
            if (s.Flame != "none")
                return s.FlameKept
                    ? "检验 · 燃着的木条在瓶口持续燃烧，CO₂ 未收集满"
                    : "验满 · 燃着的木条伸到瓶口后熄灭，CO₂ 已集满";
            if (s.Stage == "2") return "幕二 · 收集完成，实验结束";
            if (s.Bubbles > 0.05f) return "反应中 · 大理石与稀盐酸反应放出 CO₂，气体经导管导入集气瓶";
            if (s.LiquidLevel > 0.01f || s.FeedFlow > 0.05f)
                return "加液 · 稀盐酸经长颈漏斗流入锥形瓶，浸没大理石";
            return "装置就绪 · 制取一瓶 CO₂ 并验满、检验";
        }

        // 干扰项的结果幕不能用 s.Stage=="2" 判断：错法时间轴的 stage=2 事件只在
        // run.end 那一刻出现，转场后 s.Stage 会停在 transition。改用推镜进度（转场触发）判定。
        private static bool InResultAct(ShowcaseState s)
        {
            return s.Stage == "2" || s.CameraPush > 0.01f;
        }

        // ---- 稀硫酸：气泡越来越少，直至停止 ----
        private static string H2so4(ShowcaseState s)
        {
            if (s.Flame != "none" && !InResultAct(s))
                return "检验 · 燃着的木条在瓶口持续燃烧，未收集到足量 CO₂";
            if (InResultAct(s)) return "结果 · 反应很快停止，生成的 CO₂ 不足以集满集气瓶";
            if (s.Bubbles > 0.05f)
                return "反应中 · 稀硫酸与大理石反应，生成的硫酸钙微溶覆盖表面，气泡逐渐减少";
            if (s.FeedFlow > 0.05f)
                return "加液 · 稀硫酸经长颈漏斗流入锥形瓶，浸没大理石";
            if (s.LiquidLevel > 0.01f)
                return "反应停止 · 硫酸钙覆盖大理石表面，气泡不再产生";
            return "装置就绪 · 观察稀硫酸与大理石的反应";
        }

        // ---- 硫酸钙：全程不产生气泡 ----
        private static string Caso4(ShowcaseState s)
        {
            if (s.Flame != "none" && !InResultAct(s))
                return "检验 · 燃着的木条在瓶口持续燃烧，没有 CO₂ 生成";
            if (InResultAct(s)) return "结果 · 硫酸钙不与稀盐酸反应生成 CO₂，集气瓶中未收集到气体";
            if (s.LiquidLevel > 0.01f || s.FeedFlow > 0.05f)
                return "反应中 · 硫酸钙与稀盐酸不反应，锥形瓶内没有气泡产生";
            return "装置就绪 · 固体换成硫酸钙，观察锥形瓶内是否有气泡";
        }
    }
}
