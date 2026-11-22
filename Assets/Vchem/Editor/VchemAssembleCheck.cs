#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Vchem.Assembly;
using Vchem.Core.Model;

/// <summary>
/// WP4 装配桥接入口（全局命名空间，供 DSHBridge exec 调用）。
/// 方法：PrepareScene / Assemble(arg) / Check(arg) / ReportLast / Clear / Topologies。
/// 所有报告写在 chemical experiment 工程外，工程内只留运行必需资产。
/// </summary>
public static class VchemAssembleCheck
{
    private const string PreviewScene = "Assets/Vchem/Scenes/M3_LathePreview.unity";
    private const string GoldenDir = "Tests/Golden";
    private const string MatDir = "Assets/Vchem/Content/Materials";
    private const string MeshDir = "Assets/Vchem/Content/Meshes";

    private static readonly string[] DemoTopologies =
    {
        "R-OK-01",
        "R-OK-01.test-tube",
        "R-OK-01.trough-unconnected",
        "R-COL-04",
        "R-COL-03",
        "R-COL-05",
    };

    private static AssemblyResult _last;

    /// <summary>
    /// H6 诊断：输出各拓扑导管路径的 Z 跨度，并人为把一根导管末端沿 Z 推 20mm 后复测端点误差。
    /// 若扰动后误差仍是 0，说明判据看的还是 anchor 理论位置，而非真实生成路径。
    /// </summary>
    public static string PathDiag()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 导管路径 / 端点误差诊断（H6）");
        foreach (string name in DemoTopologies)
        {
            string err;
            Plan plan = ResolvePlan(name, out err);
            if (plan == null) { sb.AppendLine(name + ": ERROR " + err); continue; }
            AssemblyResult asm = PlanAssembler.Build(plan, ProfileCatalog.LoadDefault(), LoadAssets());
            float baseErr = asm.MaxEndpointErrorMm;
            float zSpan = 0f;
            int tubes = 0;
            foreach (AssemblyNode n in asm.Nodes)
            {
                if (n.PathWorld.Count == 0) continue;
                tubes++;
                foreach (Vector3 p in n.PathWorld) zSpan = Mathf.Max(zSpan, Mathf.Abs(p.z));
            }
            float probe = -1f;
            foreach (AssemblyNode n in asm.Nodes)
            {
                if (n.PathWorld.Count == 0) continue;
                int last = n.PathWorld.Count - 1;
                n.PathWorld[last] = n.PathWorld[last] + new Vector3(0f, 0f, 0.02f);
                PlanAssembler.MeasureEndpoints(plan, asm);
                probe = asm.MaxEndpointErrorMm;
                break;
            }
            sb.AppendFormat("{0}: tubes={1} maxAbsZ={2:F4}m baseErr={3:F4}mm perturbedErr={4:F2}mm\n",
                name, tubes, zSpan, baseErr, probe);
        }
        return sb.ToString();
    }

    public static string Topologies()
    {
        return string.Join(",", DemoTopologies);
    }

    public static string PrepareScene()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!string.Equals(scene.path, PreviewScene, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(ProjectRoot(), PreviewScene)))
                return "ERROR: missing preview scene " + PreviewScene;
            EditorSceneManager.OpenScene(PreviewScene, OpenSceneMode.Single);
        }
        SetCloseCamera();
        // 不落盘：此时场景里可能已有 VchemShowcase.Prepare 留下的内存装配，
        // SaveOpenScenes 会把它一起写进 .unity，domain reload 后旧装配盖住新装配。
        // 相机参数由 SetCloseCamera 每次重设，不依赖场景序列化。
        return "OK: " + EditorSceneManager.GetActiveScene().path;
    }

    public static string SetCamera()
    {
        SetCloseCamera();
        Camera c = Camera.main;
        return "OK camera=" + (c != null ? c.transform.position.ToString("F3") : "none");
    }

    public static string Assemble(string arg)
    {
        string err;
        Plan plan = ResolvePlan(arg, out err);
        if (plan == null) return "ERROR: " + err;
        PrepareScene();
        _last = PlanAssembler.Build(plan, ProfileCatalog.LoadDefault(), LoadAssets());
        return PlanAssembler.Report(_last);
    }

    public static string ReportLast()
    {
        if (_last == null) return "ERROR: no assembly yet";
        var sb = new StringBuilder();
        sb.Append(PlanAssembler.Report(_last)).AppendLine();
        foreach (EndpointError e in _last.EndpointErrors)
            sb.Append("EDGE ").Append(e.Edge).Append(" err=")
              .Append(e.DistanceMm.ToString("F4")).AppendLine();
        if (_last.Root != null)
            foreach (Transform t in _last.Root.transform)
                sb.Append("PART ").Append(t.name).Append(" p=")
                  .Append(t.position.ToString("F3")).Append(" s=")
                  .Append(t.localScale.x.ToString("F4")).AppendLine();
        return sb.ToString();
    }

    public static string Clear()
    {
        var go = GameObject.Find("M3 Assembly");
        if (go != null) UnityEngine.Object.DestroyImmediate(go);
        _last = null;
        return "OK: assembly cleared";
    }

    /// <summary>6 拓扑 × 2 次装配：位姿哈希一致、0 穿插、端口偏差 &lt; 0.5mm。</summary>
    public static string Check(string arg)
    {
        StringBuilder sb = new StringBuilder();
        var csv = new StringBuilder();
        csv.AppendLine("topology,nodes,hash,determinism,interference,maxEndpointErrorMm,verdict");

        sb.AppendLine("# WP4 装配验收（6 拓扑 × 2 次）");
        sb.AppendLine();
        sb.AppendLine("| 拓扑 | 节点 | 位姿哈希 | 确定性 | 穿插 | 端口最大偏差 | 结论 |");
        sb.AppendLine("|---|---:|---|---|---:|---:|---|");

        bool allPass = true;
        foreach (string name in DemoTopologies)
        {
            string err;
            Plan plan = ResolvePlan(name, out err);
            if (plan == null)
            {
                sb.AppendLine("| " + name + " | - | - | - | - | - | ERROR: " + err + " |");
                allPass = false;
                continue;
            }
            AssemblyResult a = PlanAssembler.Build(plan, ProfileCatalog.LoadDefault(), LoadAssets());
            AssemblyResult b = PlanAssembler.Build(plan, ProfileCatalog.LoadDefault(), LoadAssets());
            bool deterministic = string.Equals(a.Hash, b.Hash, StringComparison.Ordinal);
            bool pass = deterministic && a.Interference.Count == 0 && a.MaxEndpointErrorMm < 0.5f
                        && a.Errors.Count == 0 && b.Errors.Count == 0;
            allPass &= pass;
            string verdict = pass ? "PASS" : "FAIL";
            sb.AppendFormat("| {0} | {1} | {2} | {3} | {4} | {5:F4}mm | {6} |\n",
                name, a.Nodes.Count, a.Hash.Substring(0, 12), deterministic ? "一致" : "不一致",
                a.Interference.Count, a.MaxEndpointErrorMm, verdict);
            csv.AppendFormat("{0},{1},{2},{3},{4},{5:F4},{6}\n",
                name, a.Nodes.Count, a.Hash, deterministic, a.Interference.Count,
                a.MaxEndpointErrorMm, verdict);
            if (!pass)
            {
                sb.Append("  - ").Append(PlanAssembler.Report(a)).AppendLine();
                foreach (string perr in a.Errors) sb.Append("    - PARSE ERROR ").Append(perr).AppendLine();
                foreach (EndpointError e in a.EndpointErrors)
                    if (e.DistanceMm >= 0.5f)
                        sb.Append("    - EDGE ").Append(e.Edge).Append(" = ")
                          .Append(e.DistanceMm.ToString("F4")).Append("mm").AppendLine();
            }
        }

        // 只在内存里留第一张拓扑供截图，不落盘。
        // 历史坑：这里曾 SaveOpenScenes，把临时装配写进 M3_LathePreview.unity；
        // domain reload 后这份旧装配（旧网格）盖住新装配，导致"网格已更新、画面却不变"。
        // 演出装配由 VchemShowcase.Prepare 负责，同样是纯内存、不落盘。
        string firstErr;
        Plan first = ResolvePlan(DemoTopologies[0], out firstErr);
        if (first != null)
        {
            PrepareScene();
            _last = PlanAssembler.Build(first, ProfileCatalog.LoadDefault(), LoadAssets());
        }

        sb.AppendLine();
        sb.AppendLine("**总判定：" + (allPass ? "WP4 PASS" : "WP4 FAIL") + "**");
        WriteOutside("wp4_report.md", sb.ToString());
        WriteOutside("wp4_metrics.csv", csv.ToString());
        return sb.ToString();
    }

    private static Plan ResolvePlan(string arg, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(arg)) arg = DemoTopologies[0];
        string text = null;

        if (arg.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            text = arg;
        }
        else
        {
            string candidate = arg;
            if (!File.Exists(candidate))
            {
                string root = ProjectRoot();
                candidate = Path.Combine(root, GoldenDir, arg);
                if (!candidate.EndsWith(".plan.json", StringComparison.Ordinal)) candidate += ".plan.json";
            }
            if (File.Exists(candidate)) text = File.ReadAllText(candidate);
        }

        if (text == null)
        {
            error = "找不到 plan: " + arg;
            return null;
        }
        try
        {
            Plan p = Plan.FromJson(text);
            if (p.Topology == null || p.Topology.Nodes.Count == 0)
            {
                error = "plan 没有 topology.nodes";
                return null;
            }
            return p;
        }
        catch (Exception e)
        {
            error = "plan 解析失败: " + e.Message;
            return null;
        }
    }

    private static AssemblyAssets LoadAssets()
    {
        var a = new AssemblyAssets();
        foreach (string type in new[]
        {
            "reactor.flask_conical", "reactor.test_tube", "feed.thistle_funnel",
            "collect.gas_jar", "conduit.bent", "collect.water_trough", "support.stand"
        })
        {
            Mesh m = AssetDatabase.LoadAssetAtPath<Mesh>(MeshDir + "/" + type + ".asset");
            if (m != null) a.Meshes[type] = m;
        }
        a.GlassOuter = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_GlassOuter.mat");
        a.GlassInner = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_GlassInner.mat");
        a.Shadow = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ContactShadow.mat");
        a.Opaque = LoadOrCreateOpaque();
        a.Quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        a.Cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
        return a;
    }

    private static Material LoadOrCreateOpaque()
    {
        string path = MatDir + "/M_ApparatusOpaque.mat";
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) return null;
        m = new Material(sh) { name = "M_ApparatusOpaque" };
        m.SetColor("_BaseColor", new Color(0.62f, 0.65f, 0.68f, 1f));
        m.SetFloat("_Smoothness", 0.25f);
        AssetDatabase.CreateAsset(m, path);
        AssetDatabase.SaveAssets();
        return m;
    }

    private static void SetCloseCamera()
    {
        Camera c = Camera.main;
        if (c == null) return;
        // §10.39：默认机位统一为"示例"近景（与 VchemRuntimeShowcase.SetCamera 同步）。
        c.transform.position = new Vector3(0.22f, -0.78f, 0.55f);
        c.transform.rotation = Quaternion.LookRotation(
            new Vector3(0.00f, -0.90f, 0f) - c.transform.position);
        c.fieldOfView = 31f;
        c.nearClipPlane = 0.05f;
        c.farClipPlane = 200f;
    }

    private static string ProjectRoot()
    {
        return Path.GetDirectoryName(Application.dataPath);
    }

    private static void WriteOutside(string name, string text)
    {
        try
        {
            // ProjectRoot = .../vchem/vchem/chemical experiment；工作区根还要再上一层。
            string workspace = Directory.GetParent(Directory.GetParent(ProjectRoot()).FullName).FullName;
            string runFile = Path.Combine(workspace, ".workbuddy", "artifacts", "m3", "current_run.txt");
            if (!File.Exists(runFile)) return;
            string run = File.ReadAllText(runFile).Trim();
            string dir = Path.Combine(workspace, ".workbuddy", "artifacts", "m3", run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text, Encoding.UTF8);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VchemAssembleCheck] 报告外写失败: " + e.Message);
        }
    }
}
#endif
