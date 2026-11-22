#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 打包 Windows Player：把 M2（启动）与 M3 场景打成一个可运行的实验程序。
/// 输出路径任意：可传完整 .exe 路径或目录，未指定时落到工程外工作区
/// .workbuddy/artifacts/build/windows，不污染 Unity 工程。
/// 供 DSHBridge exec 调用：VchemBuild.Windows(arg) / VchemBuild.WindowsTo(arg)。
/// </summary>
public static class VchemBuild
{
    private const string M2 = "Assets/Vchem/Scenes/M2_PlanPanel.unity";
    private const string M3 = "Assets/Vchem/Scenes/M3_LathePreview.unity";
    private const string ExeName = "VchemLab.exe";

    /// <summary>兼容旧调用：arg 为空或普通目录名时走默认构建目录；为路径时等价 WindowsTo。</summary>
    public static string Windows(string name)
    {
        return WindowsTo(string.IsNullOrEmpty(name) ? "windows" : name);
    }

    /// <summary>
    /// 输出到任意路径。target 可为：
    ///  · 完整 exe 路径（&lt;outDir&gt;/VchemLab.exe）→ 直接用；
    ///  · 目录（&lt;outDir&gt;）→ 在其下生成 VchemLab.exe；
    ///  · 普通名字（windows）→ .workbuddy/artifacts/build/&lt;name&gt;/VchemLab.exe；
    ///  · 相对路径（build/demo.exe）→ 相对工程根目录解析。
    /// </summary>
    public static string WindowsTo(string target)
    {
        // §10.35：打包前拦截材质双份漂移，避免 Editor/Player 材质不一致的 exe 流出。
        string guard = VchemMaterialGuard.Check();
        if (guard.Contains("DRIFT=") && !guard.Contains("DRIFT=0 "))
            return "ERROR: 材质双份出现非预期漂移，已阻止打包\n" + guard;

        string exePath = ResolveExePath(target);
        string outDir = Path.GetDirectoryName(exePath);
        Directory.CreateDirectory(outDir);

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { M2, M3 },
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        try
        {
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;
            string errs = "";
            foreach (var step in report.steps)
                foreach (var msg in step.messages)
                    if (msg.type == LogType.Error) errs += msg.content + "; ";
            return "OK: result=" + sum.result + " size=" + (sum.totalSize / (1024 * 1024)) + "MB" +
                   " time=" + sum.totalTime.TotalSeconds.ToString("F1") + "s" +
                   " exe=" + exePath + (errs.Length > 0 ? " ERRORS=" + errs : "");
        }
        catch (Exception e)
        {
            return "ERROR: build failed: " + e.Message;
        }
    }

    /// <summary>把用户给的目标解析成完整 exe 路径，规则见 WindowsTo 注释。</summary>
    private static string ResolveExePath(string target)
    {
        string t = (target ?? "").Trim().Trim('"');
        if (t.Length == 0) t = "windows";

        bool rooted = Path.IsPathRooted(t);
        bool looksLikePath = rooted || t.Contains("/") || t.Contains("\\") ||
                             t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!looksLikePath)
        {
            // 普通名字：沿用默认构建根 .workbuddy/artifacts/build/<name>
            string project = Path.GetDirectoryName(Application.dataPath);
            string workspace = Directory.GetParent(Directory.GetParent(project).FullName).FullName;
            t = Path.Combine(workspace, ".workbuddy", "artifacts", "build", t);
        }
        else if (!rooted)
        {
            // 相对路径（含 / 或 \）：相对工程根目录解析
            string project = Path.GetDirectoryName(Application.dataPath);
            t = Path.Combine(project, t);
        }

        t = Path.GetFullPath(t);
        if (!t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            t = Path.Combine(t, ExeName);
        return t;
    }

    /// <summary>编辑器菜单：弹窗选任意目录导出，避免手填路径出错。</summary>
    [MenuItem("Vchem/Build Windows Player (choose output)...")]
    public static void WindowsMenu()
    {
        string dir = EditorUtility.OpenFolderPanel("选择 exe 输出目录", "", "");
        if (string.IsNullOrEmpty(dir)) return;   // 用户取消
        Debug.Log(WindowsTo(dir));
    }

    public static string Status()
    {
        string project = Path.GetDirectoryName(Application.dataPath);
        string workspace = Directory.GetParent(Directory.GetParent(project).FullName).FullName;
        string outDir = Path.Combine(workspace, ".workbuddy", "artifacts", "build");
        if (!Directory.Exists(outDir)) return "无打包产物";
        var dirs = Directory.GetDirectories(outDir);
        return dirs.Length == 0 ? "无打包产物" : string.Join("; ", Array.ConvertAll(dirs, Path.GetFileName));
    }
}
#endif
