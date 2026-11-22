using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Vchem.Authoring.Geometry;
using Vchem.Core.Contracts;

namespace Vchem.Assembly
{
    /// <summary>
    /// 加载 Content/Profiles/*.profile.json，并把 profile 的 mm 坐标口径翻译成
    /// WP3 烘焙网格的 local 坐标口径。装配只依赖这里的换算，不手调 Transform。
    /// </summary>
    public sealed class ProfileCatalog
    {
        private readonly Dictionary<string, VesselProfile> _map =
            new Dictionary<string, VesselProfile>(StringComparer.Ordinal);
        /// <summary>M9：解析失败的 profile 不再静默吞掉，交给 Prepare/验收显式报错。</summary>
        public readonly List<string> LoadErrors = new List<string>();

        public static ProfileCatalog LoadDefault()
        {
            var cat = new ProfileCatalog();
            // 统一走 Resources：编辑器与打包 Player 用同一份数据、同一条加载路径。
            TextAsset[] assets = Resources.LoadAll<TextAsset>("VchemContent/Profiles");
            if (assets != null && assets.Length > 0)
            {
                foreach (var ta in assets)
                {
                    try
                    {
                        var p = VesselProfile.LoadText(ta.text);
                        if (!string.IsNullOrEmpty(p.type)) cat._map[p.type] = p;
                    }
                    catch (Exception e)
                    {
                        string msg = ta.name + ": " + e.Message;
                        cat.LoadErrors.Add(msg);
                        Debug.LogWarning("[Vchem.Assembly] profile load failed: " + msg);
                    }
                }
                return cat;
            }
            // 兼容：Resources 尚未生成时回退到编辑器 dataPath（仅编辑器可用）。
            string dir = Path.Combine(Application.dataPath, "Vchem", "Content", "Profiles");
            if (!Directory.Exists(dir)) return cat;
            foreach (string file in Directory.GetFiles(dir, "*.profile.json"))
            {
                try
                {
                    var p = VesselProfile.Load(file);
                    if (!string.IsNullOrEmpty(p.type)) cat._map[p.type] = p;
                }
                catch (Exception e)
                {
                    string msg = Path.GetFileName(file) + ": " + e.Message;
                    cat.LoadErrors.Add(msg);
                    Debug.LogWarning("[Vchem.Assembly] profile load failed: " + msg);
                }
            }
            return cat;
        }

        public VesselProfile Get(string type)
        {
            VesselProfile p;
            return type != null && _map.TryGetValue(type, out p) ? p : null;
        }

        /// <summary>烘焙网格 1 个 local 单位对应多少 mm（与三个 Builder 的归一化分母一致）。</summary>
        public float LocalUnitMm(VesselProfile p) { return LocalUnitMmStatic(p); }

        public static float LocalUnitMmStatic(VesselProfile p)
        {
            if (p == null) return 1f;
            switch (p.kind)
            {
                case "lathe":
                    return Mathf.Max(1f, p.height);
                case "sweep":
                    return SweepExtentMm(p);
                case "box_shell":
                    return Mathf.Max(1f, p.height, MaxSizeMm(p.raw, "size"));
                case "extrusion":
                    return Mathf.Max(1f, p.height, MaxPartExtentMm(p.raw));
                default:
                    return Mathf.Max(1f, p.height);
            }
        }

        /// <summary>local→world 的 uniform scale（米 / local 单位）。</summary>
        public float WorldScale(VesselProfile p) { return WorldScaleStatic(p); }

        public static float WorldScaleStatic(VesselProfile p)
        {
            return LocalUnitMmStatic(p) * 0.001f;
        }

        public Vector3 AnchorLocal(VesselProfile p, string anchorKey)
        {
            return AnchorLocalStatic(p, anchorKey);
        }

        public static Vector3 AnchorLocalStatic(VesselProfile p, string anchorKey)
        {
            if (p == null || string.IsNullOrEmpty(anchorKey)) return Vector3.zero;
            ProfileAnchor a;
            if (!p.anchors.TryGetValue(anchorKey, out a)) return Vector3.zero;
            float unit = LocalUnitMmStatic(p);
            if (p.kind == "sweep")
            {
                int idx = a.pathIndex;
                if (idx < 0) idx += p.path.Count;
                if (idx >= 0 && idx < p.path.Count) return p.path[idx] / unit;
                return Vector3.zero;
            }
            // 车削/盒壳/型材的 anchor 只记 y；r 是端口环半径，不是轴向偏移。
            return new Vector3(0f, a.y / unit, 0f);
        }

        public Vector3 AnchorDir(VesselProfile p, string anchorKey)
        {
            if (p == null || string.IsNullOrEmpty(anchorKey)) return Vector3.up;
            ProfileAnchor a;
            if (!p.anchors.TryGetValue(anchorKey, out a) || string.IsNullOrEmpty(a.dir)) return Vector3.up;
            switch (a.dir)
            {
                case "+x": return Vector3.right;
                case "-x": return Vector3.left;
                case "+y": return Vector3.up;
                case "-y": return Vector3.down;
                case "+z": return Vector3.forward;
                case "-z": return Vector3.back;
                default: return Vector3.up;
            }
        }

        public float AnchorRadiusMm(VesselProfile p, string anchorKey) { return AnchorRadius(p, anchorKey); }

        public static float AnchorRadius(VesselProfile p, string anchorKey)
        {
            if (p == null || string.IsNullOrEmpty(anchorKey)) return 0f;
            ProfileAnchor a;
            if (!p.anchors.TryGetValue(anchorKey, out a)) return 0f;
            return Mathf.Max(0f, a.r);
        }

        /// <summary>器材落地足迹半径（mm），用于布局间距与接触阴影缩放。</summary>
        public float FootprintRadiusMm(VesselProfile p) { return FootprintRadius(p); }

        public static float FootprintRadius(VesselProfile p)
        {
            if (p == null) return 20f;
            if (p.kind == "lathe") return Mathf.Max(8f, p.MaxRadius);
            if (p.kind == "sweep") return Mathf.Max(8f, p.outerRadius * 2f);
            if (p.kind == "box_shell") return Mathf.Max(20f, MaxSizeMm(p.raw, "size") * 0.5f);
            if (p.kind == "extrusion") return Mathf.Max(40f, MaxPartExtentMm(p.raw) * 0.5f);
            return 20f;
        }

        private static float SweepExtentMm(VesselProfile p)
        {
            float m = 1f;
            if (p.path != null)
                foreach (Vector3 v in p.path)
                    m = Mathf.Max(m, Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
            return m * 2f; // 与 SweepBuilder 的 extent 定义一致
        }

        private static float MaxSizeMm(Dictionary<string, object> raw, string key)
        {
            if (raw == null) return 1f;
            var arr = Json.GetArray(raw, key);
            if (arr == null) return 1f;
            float m = 1f;
            foreach (var v in arr)
            {
                try { m = Mathf.Max(m, (float)Convert.ToDouble(v)); }
                catch { /* ignore malformed entry */ }
            }
            return m;
        }

        private static float MaxPartExtentMm(Dictionary<string, object> raw)
        {
            if (raw == null) return 1f;
            float m = Mathf.Max(1f, MaxSizeMm(raw, "size"));
            var parts = Json.GetArray(raw, "parts");
            if (parts == null) return m;
            foreach (var item in parts)
            {
                var part = item as Dictionary<string, object>;
                if (part == null) continue;
                var size = Json.GetArray(part, "size");
                var center = Json.GetArray(part, "center");
                for (int i = 0; i < 3 && size != null && center != null && i < size.Count && i < center.Count; i++)
                {
                    try
                    {
                        float half = (float)Convert.ToDouble(size[i]) * 0.5f;
                        float c = Mathf.Abs((float)Convert.ToDouble(center[i]));
                        m = Mathf.Max(m, c + half);
                    }
                    catch { /* ignore malformed entry */ }
                }
            }
            return m;
        }
    }
}
