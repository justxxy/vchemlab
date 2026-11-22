#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 材质双份一致性守卫（§10.35）。
///
/// 背景：工程里有两份同名材质，职责不同——
///   Content/Materials            = G1 光学探针的调参板（VchemM3Optics.Setup 会写回并 SaveAssets）；
///   Resources/VchemContent/...   = 交付真相源（Editor 演出 VchemShowcase 与 Player VchemRuntimeShowcase 都从此加载）。
/// 两份 guid 不同，不能整目录互相覆盖；且探针写入的差异是合法的，盲目同步会把探针高光污染进交付画面（§10.31）。
///
/// 守卫策略：以 Resources 为交付基准，逐属性比对两份材质。
///   - 差异属性全部落在该材质的「探针可写白名单」内 → EXPECTED（探针调参，不参与交付，放行）；
///   - 出现白名单外的差异 → DRIFT（有人改了一处忘改另一处的非预期漂移），判定 FAIL 并在打包前拦截。
///
/// DSHBridge: exec?method=VchemMaterialGuard.Check
/// </summary>
public static class VchemMaterialGuard
{
    private const string SrcDir = "Assets/Vchem/Content/Materials";
    private const string DstDir = "Assets/Vchem/Resources/VchemContent/Materials";

    private static readonly string[] Materials =
    {
        "M_ApparatusOpaque", "M_Bubble", "M_ContactShadow", "M_GlassInner",
        "M_GlassOuter", "M_LiquidBody", "M_LiquidSurface", "M_TableFade"
    };

    /// <summary>探针 Setup()/Tune() 允许写回 Content 的属性；这些差异不算漂移。</summary>
    private static readonly Dictionary<string, HashSet<string>> ProbeWritable =
        new Dictionary<string, HashSet<string>>
        {
            { "M_GlassOuter", new HashSet<string> {
                "_GlassColor", "_RimColor", "_LineStrength", "_ReflectionStrength",
                "_TubeColor", "_TubeMode", "_TubeSplitY" } },
            { "M_GlassInner", new HashSet<string> {
                "_GlassColor", "_RimColor", "_LineStrength", "_ReflectionStrength",
                "_TubeColor", "_TubeMode", "_TubeSplitY" } },
            { "M_LiquidSurface", new HashSet<string> { "_SurfaceColor" } },
            { "M_LiquidBody", new HashSet<string> {
                "_WaterColor", "_DeepColor", "_TurbidColor", "_Tint", "_Turbidity",
                "_LiquidTopY", "_DepthSpan", "_FresnelStrength", "_DebugStripes" } },
            { "M_Bubble", new HashSet<string>() }
        };

    /// <summary>返回给 DSHBridge 的可读报告；报告首行含 DRIFT=&lt;n&gt; 供打包守卫解析。</summary>
    public static string Check()
    {
        var sb = new StringBuilder();
        int drift = 0, expected = 0, ok = 0, fail = 0;

        sb.AppendLine("# 材质双份一致性守卫（§10.35）");
        sb.AppendLine("交付基准: " + DstDir);
        sb.AppendLine("探针调参板: " + SrcDir);

        foreach (string name in Materials)
        {
            string dstPath = DstDir + "/" + name + ".mat";
            string srcPath = SrcDir + "/" + name + ".mat";
            var dst = AssetDatabase.LoadAssetAtPath<Material>(dstPath);
            var src = AssetDatabase.LoadAssetAtPath<Material>(srcPath);

            if (dst == null)
            {
                sb.AppendLine("FAIL 缺少交付材质 " + dstPath);
                fail++; continue;
            }
            if (src == null)
            {
                sb.AppendLine("WARN 缺少探针副本 " + srcPath + "（仅影响 G1 复测）");
                continue;
            }
            if (dst.shader == null || src.shader == null || dst.shader != src.shader)
            {
                sb.AppendLine("FAIL " + name + " shader 引用不一致或为空");
                fail++; continue;
            }

            var a = Props(src);
            var b = Props(dst);
            var keys = new HashSet<string>(a.Keys);
            keys.UnionWith(b.Keys);
            var diffs = new List<string>();
            foreach (string k in keys)
            {
                string va = a.ContainsKey(k) ? a[k] : "<missing>";
                string vb = b.ContainsKey(k) ? b[k] : "<missing>";
                if (!string.Equals(va, vb, StringComparison.Ordinal))
                    diffs.Add(k + " (" + va + " -> " + vb + ")");
            }

            if (diffs.Count == 0)
            {
                sb.AppendLine("OK   " + name + "  两份一致");
                ok++; continue;
            }

            HashSet<string> whitelist;
            bool probeWritable = ProbeWritable.TryGetValue(name, out whitelist);
            var driftProps = new List<string>();
            var expectedProps = new List<string>();
            foreach (string d in diffs)
            {
                string prop = d.Substring(0, d.IndexOf(' '));
                if (probeWritable && whitelist.Contains(prop)) expectedProps.Add(prop);
                else driftProps.Add(d);
            }

            if (driftProps.Count == 0)
            {
                sb.AppendLine("EXPECTED " + name + "  探针调参差异（不参与交付）: " +
                              string.Join(", ", expectedProps.ToArray()));
                expected++;
            }
            else
            {
                sb.AppendLine("DRIFT  " + name + "  非预期漂移: " + string.Join("; ", driftProps.ToArray()));
                drift++;
            }
        }

        string verdict = (drift == 0 && fail == 0) ? "PASS" : "FAIL";
        sb.AppendLine(string.Format("DRIFT={0} EXPECTED={1} OK={2} FAIL={3}  总判定: {4}",
            drift, expected, ok, fail, verdict));
        sb.AppendLine("规则: Resources 为交付基准；Content 仅 G1 探针可写白名单属性，其余差异视为漂移。");
        return sb.ToString();
    }

    /// <summary>读取材质的全部可序列化属性，按 shader 声明的类型取值为可比较字符串。</summary>
    private static Dictionary<string, string> Props(Material m)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var shader = m.shader;
        if (shader == null) return result;
        int n = ShaderUtil.GetPropertyCount(shader);
        for (int i = 0; i < n; i++)
        {
            string name = ShaderUtil.GetPropertyName(shader, i);
            var type = ShaderUtil.GetPropertyType(shader, i);
            string value;
            switch (type)
            {
                case ShaderUtil.ShaderPropertyType.Color:
                    value = "C" + Fmt(m.GetColor(name));
                    break;
                case ShaderUtil.ShaderPropertyType.Vector:
                    value = "V" + Fmt(m.GetVector(name));
                    break;
                case ShaderUtil.ShaderPropertyType.Float:
                case ShaderUtil.ShaderPropertyType.Range:
                    value = "F" + m.GetFloat(name).ToString("F5");
                    break;
                case ShaderUtil.ShaderPropertyType.TexEnv:
                    var tex = m.GetTexture(name);
                    value = "T" + (tex == null ? "null" : tex.name);
                    break;
                default:
                    value = "?";
                    break;
            }
            result[name] = value;
        }
        return result;
    }

    private static string Fmt(Color c)
    {
        return "(" + c.r.ToString("F4") + "," + c.g.ToString("F4") + "," +
               c.b.ToString("F4") + "," + c.a.ToString("F4") + ")";
    }

    private static string Fmt(Vector4 v)
    {
        return "(" + v.x.ToString("F4") + "," + v.y.ToString("F4") + "," +
               v.z.ToString("F4") + "," + v.w.ToString("F4") + ")";
    }
}
#endif
