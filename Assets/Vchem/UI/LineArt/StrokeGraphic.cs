using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Vchem.UI
{
    /// <summary>
    /// 课本线描基元：把折线/圆/凸多边形扩成三角带，统一墨色描边，白底无填充。
    /// 坐标一律用"器材盒左下角为原点"的局部坐标（创建时 pivot 必须为 0）。
    /// 判定链不感知本组件：它只负责把 DesignState 的结构属性画成试卷式线稿。
    /// </summary>
    public sealed class StrokeGraphic : MaskableGraphic
    {
        public sealed class Stroke
        {
            public Vector2[] Pts;
            public float Width;
            public Color Col;
            public bool Closed;
            public bool Fill;
        }

        public float Scale = 1f;
        /// <summary>
        /// 线宽补偿系数：只作用于描边，不作用于填充。
        /// 小尺寸实例（左栏卡 / 拖拽幽灵）按 Scale 缩放后主轮廓只剩 0.8px，需要额外补偿才清晰；
        /// 装置区大图只做轻微加粗。默认 1，因此导管 / 端口环 / 水面等直接 Line() 的元素不受影响。
        /// </summary>
        public float WidthBoost = 1f;
        private readonly List<Stroke> _strokes = new List<Stroke>();

        public static readonly Color Ink = new Color(0.10f, 0.11f, 0.13f, 1f);
        public static readonly Color Paper = new Color(0.985f, 0.99f, 0.995f, 1f);

        public void Clear()
        {
            _strokes.Clear();
            SetVerticesDirty();
        }

        public void Line(Vector2 a, Vector2 b, float w, Color c)
        {
            Poly(new[] { a, b }, w, c, false);
        }

        public void Poly(Vector2[] pts, float w, Color c, bool closed)
        {
            if (pts == null || pts.Length < 2) return;
            _strokes.Add(new Stroke { Pts = pts, Width = w, Col = c, Closed = closed });
            SetVerticesDirty();
        }

        public void FillPoly(Vector2[] pts, Color c)
        {
            if (pts == null || pts.Length < 3) return;
            _strokes.Add(new Stroke { Pts = pts, Col = c, Fill = true });
            SetVerticesDirty();
        }

        public void Circle(Vector2 center, float r, float w, Color c, int seg = 20)
        {
            Poly(Ring(center, r, seg), w, c, true);
        }

        public void FillCircle(Vector2 center, float r, Color c, int seg = 20)
        {
            FillPoly(Ring(center, r, seg), c);
        }

        public void Dash(Vector2 a, Vector2 b, float w, Color c, float dash = 6f, float gap = 4f)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;
            Vector2 dir = d / len;
            for (float t = 0f; t < len; t += dash + gap)
                Line(a + dir * t, a + dir * Mathf.Min(t + dash, len), w, c);
        }

        private static Vector2[] Ring(Vector2 c, float r, int seg)
        {
            Vector2[] pts = new Vector2[seg];
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            return pts;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Color tint = color;
            foreach (Stroke s in _strokes)
            {
                if (s.Fill) EmitFill(vh, s, tint);
                else EmitStroke(vh, s, tint);
            }
        }

        private void EmitFill(VertexHelper vh, Stroke s, Color tint)
        {
            Color c = s.Col * tint;
            int baseIdx = vh.currentVertCount;
            for (int i = 0; i < s.Pts.Length; i++)
                vh.AddVert(ToRect(s.Pts[i]), c, Vector2.zero);
            for (int i = 1; i < s.Pts.Length - 1; i++)
                vh.AddTriangle(baseIdx, baseIdx + i, baseIdx + i + 1);
        }

        private void EmitStroke(VertexHelper vh, Stroke s, Color tint)
        {
            Vector2[] p = s.Pts;
            int n = p.Length;
            if (n < 2) return;
            Color c = s.Col * tint;
            float half = s.Width * 0.5f * Scale * WidthBoost;
            int count = n;
            int baseIdx = vh.currentVertCount;

            for (int i = 0; i < count; i++)
            {
                Vector2 nrm;
                if (s.Closed)
                {
                    Vector2 dIn = Norm(p[i] - p[(i - 1 + n) % n]);
                    Vector2 dOut = Norm(p[(i + 1) % n] - p[i]);
                    nrm = Miter(dIn, dOut);
                }
                else if (i == 0) nrm = Perp(Norm(p[1] - p[0]));
                else if (i == n - 1) nrm = Perp(Norm(p[n - 1] - p[n - 2]));
                else
                {
                    Vector2 dIn = Norm(p[i] - p[i - 1]);
                    Vector2 dOut = Norm(p[i + 1] - p[i]);
                    nrm = Miter(dIn, dOut);
                }
                Vector2 pos = ToRect(p[i]);
                vh.AddVert(pos + nrm * half, c, Vector2.zero);
                vh.AddVert(pos - nrm * half, c, Vector2.zero);
            }

            int quads = s.Closed ? n : n - 1;
            for (int i = 0; i < quads; i++)
            {
                int a = baseIdx + i * 2;
                int b = baseIdx + ((i + 1) % n) * 2;
                vh.AddTriangle(a, a + 1, b + 1);
                vh.AddTriangle(a, b + 1, b);
            }
        }

        private Vector2 ToRect(Vector2 p)
        {
            return p * Scale + rectTransform.rect.min;
        }

        private static Vector2 Norm(Vector2 v)
        {
            float m = v.magnitude;
            return m < 1e-5f ? Vector2.right : v / m;
        }

        private static Vector2 Perp(Vector2 d)
        {
            return new Vector2(-d.y, d.x);
        }

        private static Vector2 Miter(Vector2 dIn, Vector2 dOut)
        {
            Vector2 nIn = Perp(dIn), nOut = Perp(dOut);
            Vector2 m = nIn + nOut;
            if (m.sqrMagnitude < 1e-6f) return nIn;
            m.Normalize();
            float k = Vector2.Dot(m, nIn);
            if (k < 0.5f) k = 0.5f;   // 限制斜接长度，尖角不炸出长刺
            return m / k;
        }
    }
}