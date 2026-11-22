using UnityEngine;

namespace Vchem.UI
{
    /// <summary>
    /// 课本/试卷式装置线稿的几何库：7 元件 + 塞子 + 试剂小图。
    /// 所有坐标在 120×140 器材盒内（左下角原点），与端口锚点共用同一份几何，
    /// 保证"画出来的瓶口"和"点得到的瓶口"永远是同一个点。
    /// </summary>
    public static class DeviceGlyph
    {
        public const float W = 120f, H = 140f;
        public const float FlaskLiquidY = 62f, TubeLiquidY = 60f;

        public struct State
        {
            public bool Solid, Liquid, Powder;
            public bool Inverted, Covered, PlateGap;
            public bool Assembled;     // 容器瓶口已插漏斗 / 漏斗已接入容器
            public bool HeldLiquid;    // 漏斗储液泡里还存着酸
            public bool FunnelHole;    // 塞子上有漏斗颈穿过
            public bool GasStub;       // 塞子带出气短管
        }

        public static void Draw(StrokeGraphic g, string type, State s)
        {
            Color ink = StrokeGraphic.Ink;
            switch (type)
            {
                case "reactor.flask_conical": DrawFlask(g, s, ink); break;
                case "reactor.test_tube": DrawTube(g, s, ink); break;
                case "feed.thistle_funnel": DrawFunnel(g, s, ink); break;
                case "collect.gas_jar": DrawJar(g, s, ink); break;
                case "collect.water_trough": DrawTrough(g, ink); break;
                case "support.stand": DrawStand(g, ink); break;
                case "stopper": DrawStopper(g, s, ink); break;
                case "reagent.solid": DrawSolidPile(g, ink); break;
                case "reagent.liquid": DrawReagentBottle(g, ink); break;
            }
        }

        // ---------------------------------------------------------------- 容器
        private static float FlaskHalf(float y)
        {
            return 12f + (88f - y) / 70f * 40f;
        }

        private static void DrawFlask(StrokeGraphic g, State s, Color ink)
        {
            g.Poly(new[]
            {
                new Vector2(46,113), new Vector2(48,111), new Vector2(48,88),
                new Vector2(16,18), new Vector2(18,14), new Vector2(102,14),
                new Vector2(104,18), new Vector2(72,88), new Vector2(72,111), new Vector2(74,113)
            }, 2f, ink, false);

            if (s.Liquid)
            {
                float hw = FlaskHalf(FlaskLiquidY);
                g.Line(new Vector2(60 - hw, FlaskLiquidY), new Vector2(60 + hw, FlaskLiquidY), 2f, ink);
                Hatch(g, ink, 60, hw, FlaskLiquidY);
                g.Poly(SolidLeader("reactor.flask_conical"), 1.5f, ink, false);
                g.Poly(LiquidLeader("reactor.flask_conical"), 1.5f, ink, false);
            }
            if (s.Solid) Pellets(g, ink, 60, 44, s.Powder);
        }

        private static void DrawTube(StrokeGraphic g, State s, Color ink)
        {
            g.Poly(new[]
            {
                new Vector2(44,112), new Vector2(47,110), new Vector2(47,26),
                new Vector2(48,18), new Vector2(54,14), new Vector2(66,14),
                new Vector2(72,18), new Vector2(73,26), new Vector2(73,110), new Vector2(76,112)
            }, 2f, ink, false);

            if (s.Liquid)
            {
                g.Line(new Vector2(47, TubeLiquidY), new Vector2(73, TubeLiquidY), 2f, ink);
                g.Line(new Vector2(51, 48), new Vector2(63, 48), 1.5f, ink);
                g.Line(new Vector2(55, 36), new Vector2(69, 36), 1.5f, ink);
                g.Poly(SolidLeader("reactor.test_tube"), 1.5f, ink, false);
                g.Poly(LiquidLeader("reactor.test_tube"), 1.5f, ink, false);
            }
            if (s.Solid) Pellets(g, ink, 60, 13, s.Powder);
        }

        /// <summary>液面下的短横划线：试卷里表示"这是液体"，不用色块。</summary>
        private static void Hatch(StrokeGraphic g, Color ink, float cx, float hw, float surfaceY)
        {
            float l = cx - hw, r = cx + hw;
            g.Line(new Vector2(l + 8, surfaceY - 12), new Vector2(l + 26, surfaceY - 12), 1.5f, ink);
            g.Line(new Vector2(r - 30, surfaceY - 12), new Vector2(r - 10, surfaceY - 12), 1.5f, ink);
            g.Line(new Vector2(l + 14, surfaceY - 24), new Vector2(l + 34, surfaceY - 24), 1.5f, ink);
            g.Line(new Vector2(r - 26, surfaceY - 24), new Vector2(r - 8, surfaceY - 24), 1.5f, ink);
            g.Line(new Vector2(l + 10, surfaceY - 36), new Vector2(l + 24, surfaceY - 36), 1.5f, ink);
        }

        /// <summary>瓶底固体：块状＝一圈小圆球；粉末＝密排细点堆。</summary>
        private static void Pellets(StrokeGraphic g, Color ink, float cx, float spread, bool powder)
        {
            if (!powder)
            {
                Vector2[] c =
                {
                    new Vector2(cx-20,20), new Vector2(cx-8,17), new Vector2(cx+4,20),
                    new Vector2(cx+16,17), new Vector2(cx-14,26), new Vector2(cx-2,25), new Vector2(cx+10,26)
                };
                foreach (Vector2 p in c) g.Circle(p, 3.2f, 1.6f, ink, 12);
                return;
            }
            for (int row = 0; row < 3; row++)
            {
                float y = 16 + row * 4;
                float half = spread - row * 6;
                for (float x = cx - half; x <= cx + half; x += 5)
                    g.FillCircle(new Vector2(x + (row % 2) * 2.5f, y), 1.4f, ink, 8);
            }
        }

        // ---------------------------------------------------------------- 长颈漏斗（圆球泡 + 开口短颈 + 尖口茎）
        private static void DrawFunnel(StrokeGraphic g, State s, Color ink)
        {
            // 球泡＝正圆（图2口径），上方留开口接短颈，下方收进茎
            const float cx = 60f, cy = 102f, r = 14f;
            float a0 = Mathf.Atan2(11.5f, -8f) * Mathf.Rad2Deg;
            float a1 = 360f + Mathf.Atan2(11.5f, 8f) * Mathf.Rad2Deg;
            const int n = 18;
            Vector2[] arc = new Vector2[n + 1];
            for (int i = 0; i <= n; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)n) * Mathf.Deg2Rad;
                arc[i] = new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r);
            }
            g.Poly(arc, 2f, ink, false);
            g.Line(new Vector2(52, 120), arc[0], 2f, ink);
            g.Line(new Vector2(68, 120), arc[n], 2f, ink);
            g.Line(new Vector2(50, 122), new Vector2(52, 120), 2f, ink);
            g.Line(new Vector2(70, 122), new Vector2(68, 120), 2f, ink);
            g.Line(new Vector2(57, 88.5f), new Vector2(57, 26), 2f, ink);
            g.Line(new Vector2(63, 88.5f), new Vector2(63, 14), 2f, ink);
            // 斜口尖：左壁短、右壁长收尖，切口一条斜线（课本图口径），最低点仍在 y14 保持锚点语义
            g.Line(new Vector2(57, 26), new Vector2(63, 14), 2f, ink);
            if (s.HeldLiquid)
            {
                g.Line(new Vector2(46, 104), new Vector2(74, 104), 2f, ink);
                g.Line(new Vector2(50, 98), new Vector2(62, 98), 1.5f, ink);
            }
        }

        // ---------------------------------------------------------------- 集气瓶 / 水槽 / 铁架台
        private static void DrawJar(StrokeGraphic g, State s, Color ink)
        {
            // 课本瓶形：撇口短颈 + 溜肩 + 圆角瓶底，不是长方框
            Vector2[] up =
            {
                new Vector2(40,104), new Vector2(44,102), new Vector2(44,94), new Vector2(32,86),
                new Vector2(32,44), new Vector2(36,40), new Vector2(84,40), new Vector2(88,44),
                new Vector2(88,86), new Vector2(76,94), new Vector2(76,102), new Vector2(80,104)
            };
            if (!s.Inverted) g.Poly(up, 2f, ink, false);
            else
            {
                Vector2[] inv = new Vector2[up.Length];
                for (int i = 0; i < up.Length; i++) inv[i] = new Vector2(up[i].x, 144f - up[i].y);
                g.Poly(inv, 2f, ink, false);
            }
            // 玻璃片＝薄板双线；插着导管时盖右侧、左缘留缝给 CO2（图2口径），收满移管后盖严
            if (s.Covered && !s.Inverted)
            {
                float x0 = s.PlateGap ? 61f : 38f;
                float x1 = s.PlateGap ? 98f : 82f;
                g.Poly(new[]
                {
                    new Vector2(x0,104), new Vector2(x1,104), new Vector2(x1,107), new Vector2(x0,107)
                }, 2f, ink, true);
            }
        }

        private static void DrawTrough(StrokeGraphic g, Color ink)
        {
            g.Poly(new[] { new Vector2(18,70), new Vector2(22,20), new Vector2(98,20), new Vector2(102,70) }, 2f, ink, false);
            g.Line(new Vector2(21, 56), new Vector2(99, 56), 2f, ink);
            g.Line(new Vector2(30, 44), new Vector2(48, 44), 1.5f, ink);
            g.Line(new Vector2(60, 44), new Vector2(80, 44), 1.5f, ink);
            g.Line(new Vector2(36, 32), new Vector2(56, 32), 1.5f, ink);
            g.Line(new Vector2(66, 32), new Vector2(88, 32), 1.5f, ink);
        }

        private static void DrawStand(StrokeGraphic g, Color ink)
        {
            g.Poly(new[] { new Vector2(20,12), new Vector2(100,12), new Vector2(100,18), new Vector2(20,18) }, 2f, ink, true);
            g.Line(new Vector2(30, 18), new Vector2(30, 122), 3f, ink);
            g.Line(new Vector2(30, 108), new Vector2(76, 108), 2f, ink);
            g.Line(new Vector2(30, 112), new Vector2(76, 112), 2f, ink);
            g.Circle(new Vector2(80, 110), 6f, 2f, ink, 14);
        }

        // ---------------------------------------------------------------- 橡皮塞（黑色实心 + 穿颈白通道 + 出气短管）
        private static void DrawStopper(StrokeGraphic g, State s, Color ink)
        {
            Vector2[] trap = { new Vector2(44,104), new Vector2(76,104), new Vector2(72,116), new Vector2(48,116) };
            // 试卷口径：橡胶塞＝上下两条黑、中间一圈空白；漏斗颈穿过处再留白通道描颈线
            g.FillPoly(trap, ink);
            g.Poly(trap, 2f, ink, true);
            g.FillPoly(new[]
            {
                new Vector2(43,108), new Vector2(77,108), new Vector2(77,112), new Vector2(43,112)
            }, StrokeGraphic.Paper);
            if (s.FunnelHole)
            {
                g.FillPoly(new[]
                {
                    new Vector2(55,104), new Vector2(65,104), new Vector2(65,116), new Vector2(55,116)
                }, StrokeGraphic.Paper);
                g.Line(new Vector2(57, 104), new Vector2(57, 116), 2f, ink);
                g.Line(new Vector2(63, 104), new Vector2(63, 116), 2f, ink);
            }
            if (s.GasStub)
            {
                // 与 BuildTube 的管径（外径 6）对齐：导管从 (93,107) 竖直上引时接缝重合
                g.Poly(new[] { new Vector2(76,115), new Vector2(96,115), new Vector2(96,109) }, 2f, ink, false);
                g.Line(new Vector2(76, 109), new Vector2(90, 109), 2f, ink);
            }
        }

        // ---------------------------------------------------------------- 试剂小图（左栏卡 / 拖拽幽灵）
        private static void DrawSolidPile(StrokeGraphic g, Color ink)
        {
            Vector2[] c =
            {
                new Vector2(44,40), new Vector2(60,36), new Vector2(76,40),
                new Vector2(52,50), new Vector2(68,50)
            };
            foreach (Vector2 p in c) g.Circle(p, 5f, 2f, ink, 14);
        }

        private static void DrawReagentBottle(StrokeGraphic g, Color ink)
        {
            g.Line(new Vector2(44, 110), new Vector2(76, 110), 2f, ink);
            g.Poly(new[]
            {
                new Vector2(48,110), new Vector2(48,96), new Vector2(40,88), new Vector2(40,30),
                new Vector2(80,30), new Vector2(80,88), new Vector2(72,96), new Vector2(72,110)
            }, 2f, ink, false);
            g.Line(new Vector2(40, 70), new Vector2(80, 70), 2f, ink);
            g.Line(new Vector2(46, 56), new Vector2(60, 56), 1.5f, ink);
            g.Line(new Vector2(64, 44), new Vector2(74, 44), 1.5f, ink);
        }

        // ---------------------------------------------------------------- 端口锚点（与线稿同一份几何）
        public static Vector2 Anchor(string type, string key, State s)
        {
            bool r = key.EndsWith("_r");
            string b = r ? key.Substring(0, key.Length - 2) : key;
            switch (type)
            {
                case "reactor.flask_conical":
                    if (b == "mouth") return s.Assembled ? new Vector2(93, 107) : new Vector2(60, 112);
                    if (b == "inside") return r ? new Vector2(80, 56) : new Vector2(52, 56);
                    if (b == "above") return r ? new Vector2(74, 80) : new Vector2(52, 80);
                    if (b == "below") return r ? new Vector2(86, 44) : new Vector2(52, 44);
                    break;
                case "reactor.test_tube":
                    if (b == "mouth") return new Vector2(60, 112);
                    if (b == "inside") return r ? new Vector2(66, 86) : new Vector2(54, 86);
                    if (b == "above") return r ? new Vector2(66, 72) : new Vector2(54, 72);
                    if (b == "below") return r ? new Vector2(66, 50) : new Vector2(54, 50);
                    break;
                case "feed.thistle_funnel":
                    return new Vector2(60, 14);
                case "collect.gas_jar":
                    if (b == "mouth") return s.Inverted ? new Vector2(56, 42) : new Vector2(56, 102);
                    // 深插＝伸到瓶底附近：正放瓶底在下、倒置瓶底在上
                    if (b == "bottom") return s.Inverted ? new Vector2(60, 100) : new Vector2(60, 44);
                    if (b == "cover") return s.Inverted ? new Vector2(74, 38) : new Vector2(74, 106);
                    break;
                case "collect.water_trough":
                    return b == "tube" ? new Vector2(32, 78) : new Vector2(60, 46);
                case "support.stand":
                    return new Vector2(80, 110);
            }
            return new Vector2(60, 70);
        }

        /// <summary>液体试剂引线：从瓶右壁液面处引到右侧文字。</summary>
        public static Vector2[] LiquidLeader(string type)
        {
            if (type == "reactor.test_tube")
                return new[] { new Vector2(75, 61), new Vector2(104, 68) };
            float hw = FlaskHalf(FlaskLiquidY);
            return new[] { new Vector2(60 + hw + 2, FlaskLiquidY + 1), new Vector2(104, 70) };
        }

        /// <summary>固体试剂引线：从瓶底颗粒引到左侧文字。</summary>
        public static Vector2[] SolidLeader(string type)
        {
            if (type == "reactor.test_tube")
                return new[] { new Vector2(45, 20), new Vector2(-4, 16) };
            return new[] { new Vector2(30, 20), new Vector2(-4, 16) };
        }
    }
}