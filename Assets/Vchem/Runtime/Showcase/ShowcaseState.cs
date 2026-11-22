using System;

namespace Vchem.Showcase
{
    /// <summary>
    /// 某一模拟时刻 t 的演出状态快照。8 个现象基元的强度全部是 [0,1] 数值或
    /// 离散状态字符串，完全由 TimelinePlayer.Evaluate(t) 决定，与播放历史无关。
    /// 这样 Seek(21.8) 与顺序播放到 21.8 得到逐字节相同的结果。
    /// </summary>
    [Serializable]
    public sealed class ShowcaseState
    {
        public float T;
        public string Stage = "1";

        // ---- 8 现象基元 ----------------------------------------------------
        /// <summary>1 液流：稀盐酸从长颈漏斗流下。</summary>
        public float FeedFlow;
        /// <summary>2 气泡：发生瓶内固液界面冒泡强度。</summary>
        public float Bubbles;
        /// <summary>3 液面：发生瓶内液体高度 0..1。</summary>
        public float LiquidLevel;
        /// <summary>4 水位：集气瓶/水槽水位 0..1（排水法拓扑）。</summary>
        public float WaterLevel;
        /// <summary>5 浑浊：石灰水浑浊度 0..1。</summary>
        public float Turbidity;
        /// <summary>6 白雾：瓶口白雾浓度 0..1（浓盐酸拓扑）。</summary>
        public float WhiteMist;
        /// <summary>7 火焰：none / burning / extinguishing / extinguished。</summary>
        public string Flame = "none";
        /// <summary>验满木条下探进度：0=瓶口上方（刚开始检验），1=已停在瓶口。</summary>
        public float FlameApproach;
        /// <summary>本次检验结果是否"木条继续燃烧"（未集满 / 未成功制取）。</summary>
        public bool FlameKept;
        /// <summary>火焰强度 1→0：burning=1，extinguishing 在 0.8s 内衰减，extinguished=0。</summary>
        public float FlameFade = 1f;
        /// <summary>8 瓶口气泡：排水法验满外溢 0..1。</summary>
        public float MouthBubble;

        // ---- 验满后收尾动作（§10.25，由 t 与熄灭火焰时刻确定性推出）----------
        /// <summary>熄灭后玻璃片从屏幕右侧滑入到"贴住导管"位置的进度 0..1。</summary>
        public float CoverSlide;
        /// <summary>玻璃片从贴住导管处滑到完全盖住瓶口的进度 0..1。</summary>
        public float CoverSeal;
        /// <summary>扭转橡胶管并撤下导管的进度 0..1（1=导管已移出画面）。</summary>
        public float ConduitWithdraw;

        // ---- 镜头 / 曲线量 -------------------------------------------------
        /// <summary>两幕转接推镜进度 0..1。</summary>
        public float CameraPush;
        /// <summary>M4：camera.push 事件参数——推镜位移（米）与俯仰（度），由 Director 真实消费。</summary>
        public float CameraPushDz = 0.12f;
        public float CameraPushPitch = -2.0f;
        /// <summary>累计产气 mL（标定曲线插值）。</summary>
        public float CumulativeMl;
        /// <summary>到达收集端 mL。</summary>
        public float ArrivedMl;
        /// <summary>瓶内保有量 mL（封顶 250）。</summary>
        public float HeldMl;

        public string Bullet = "";

        /// <summary>可哈希的规范化序列；任何可见量变化都必须反映在这里。</summary>
        public string Serialize()
        {
            return string.Format(
                "t={0:F3}|stage={1}|feed={2:F3}|bub={3:F3}|liq={4:F3}|water={5:F3}|" +
                "turb={6:F3}|mist={7:F3}|flame={8}|mouth={9:F3}|cam={10:F3}|" +
                "cum={11:F2}|arr={12:F2}|held={13:F2}|bullet={14}|dzd={15:F3}|pitch={16:F2}|" +
                "cvsl={17:F3}|cvsl2={18:F3}|wdraw={19:F3}|approach={20:F3}|kept={21}|fade={22:F3}",
                T, Stage, FeedFlow, Bubbles, LiquidLevel, WaterLevel,
                Turbidity, WhiteMist, Flame, MouthBubble, CameraPush,
                CumulativeMl, ArrivedMl, HeldMl, Bullet, CameraPushDz, CameraPushPitch,
                CoverSlide, CoverSeal, ConduitWithdraw, FlameApproach, FlameKept ? 1 : 0, FlameFade);
        }
    }
}
