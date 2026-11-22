using System;
using System.Collections.Generic;
using UnityEngine;
using Vchem.Authoring.Geometry;
using Vchem.Core.Model;

namespace Vchem.Assembly
{
    /// <summary>
    /// 端口槽位规则。所有偏移都从 profile anchor 推出，禁止为某张拓扑写死坐标。
    /// 返回的是端口环平面内的 mm 偏移（X/Z），Y 恒为 0。
    /// </summary>
    public static class SlotSolver
    {
        public static Vector3 Offset(VesselProfile profile, string port, string connectionKind, int sameKindIndex)
        {
            if (profile == null || string.IsNullOrEmpty(port)) return Vector3.zero;
            ProfileAnchor a;
            if (!profile.anchors.TryGetValue(port, out a)) return Vector3.zero;

            // 加料、夹持、排水都走轴心；只有同一瓶口上的多根气路需要绕环错开。
            if (connectionKind == "feed" || connectionKind == "support"
                || connectionKind == "water") return Vector3.zero;

            float r = Mathf.Max(0f, a.r) * 0.55f;
            if (r < 0.001f) return Vector3.zero;

            // 气路按"从哪一端看"分别定角（§10.25）：
            // - 发生瓶口：angle=180 → 世界 -X（屏幕右），与漏斗细颈（世界 +X）分居两侧，
            //   上升段不再穿过漏斗颈（§10.21 的修复保留）。
            // - 集气瓶口：angle=0 → 世界 +X（屏幕左），导管落在瓶口左端；熄灭后玻璃片
            //   从右侧（世界 -X）盖上，两者分占两侧，不会被盖片压住。
            // 两者都仍按同类型边绕环均分，且与端点测量共用本函数，端口偏差保持 0。
            bool gasIntoJar = connectionKind == "gas"
                && profile.type == "collect.gas_jar";
            float baseAngle = gasIntoJar ? 0f : (connectionKind == "gas" ? 180f : 0f);
            float angle = (baseAngle + sameKindIndex * 24f) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
        }

        public static string OtherPort(Edge e, string endpoint)
        {
            return string.Equals(e.From, endpoint, StringComparison.Ordinal) ? e.To : e.From;
        }

        public static string OtherNode(Edge e, string endpoint)
        {
            return Edge.NodeOf(OtherPort(e, endpoint));
        }

        public static bool EndpointIs(Edge e, string endpoint, string nodeId, string port)
        {
            return string.Equals(endpoint, nodeId + "." + port, StringComparison.Ordinal);
        }

        /// <summary>按端口类型 + 边顺序统计某端点上同类型连接的序号。</summary>
        public static int SameKindIndex(IList<Edge> edges, Edge self, string endpoint)
        {
            int index = 0;
            foreach (Edge e in edges)
            {
                if (ReferenceEquals(e, self)) break;
                bool touches = string.Equals(e.From, endpoint, StringComparison.Ordinal)
                            || string.Equals(e.To, endpoint, StringComparison.Ordinal);
                if (touches && string.Equals(e.Kind, self.Kind, StringComparison.Ordinal)) index++;
            }
            return index;
        }

        public static bool IsGlass(string type)
        {
            return type == "reactor.flask_conical" || type == "reactor.test_tube"
                || type == "feed.thistle_funnel" || type == "collect.gas_jar"
                || type == "conduit.bent";
        }

        public static bool IsReactor(string type)
        {
            return type == "reactor.flask_conical" || type == "reactor.test_tube";
        }

        public static bool IsGrounded(string type)
        {
            return type == "reactor.flask_conical" || type == "reactor.test_tube"
                || type == "collect.gas_jar" || type == "collect.water_trough"
                || type == "support.stand";
        }
    }
}
