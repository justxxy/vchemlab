#if UNITY_EDITOR
// WP6 性能采样：Edit 模式固定机位离屏同步渲染基准。
// 对 1920x1080 RT 连续渲染，每帧 ReadPixels(1x1) 强制 GPU 同步，用 Stopwatch 测真实帧墙钟时间。
// 统计均值 / p50 / p99 帧时间，换算等效 fps 与 1% Low。口径为"渲染管线吞吐基准"，
// 非 Play 模式 vsync 帧率——但这正是方案 §6.3 想确认的"本机能跑"负载指标。
// 调用：VchemPerf.Sample(arg)  arg = high | compat | both
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class VchemPerf
{
    // QualitySettings.asset 顺序：0 Performant(Compat) / 1 Balanced / 2 HighFidelity(High)
    private const int LevelHigh = 2;
    private const int LevelCompat = 0;
    private const int WarmupFrames = 60;
    private const int SampleFrames = 600;
    private const int Width = 1920;
    private const int Height = 1080;

    public static string Sample(string arg)
    {
        string mode = string.IsNullOrEmpty(arg) ? "both" : arg.Trim().ToLowerInvariant();
        int saved = QualitySettings.GetQualityLevel();
        var sb = new StringBuilder();
        sb.Append("mode=").Append(mode).Append(" res=").Append(Width).Append('x').Append(Height)
          .Append(" warmup=").Append(WarmupFrames).Append(" sample=").Append(SampleFrames).AppendLine();

        try
        {
            if (mode == "high" || mode == "both") sb.Append(RunTier("High", LevelHigh, 4)).AppendLine();
            if (mode == "compat" || mode == "both") sb.Append(RunTier("Compat", LevelCompat, 2)).AppendLine();
        }
        finally
        {
            QualitySettings.SetQualityLevel(saved, true);
            sb.Append("restored_quality=").Append(saved).AppendLine();
        }

        string report = sb.ToString();
        WriteArtifact("perf_report.txt", report);
        return report;
    }

    private static string RunTier(string name, int level, int msaa)
    {
        QualitySettings.SetQualityLevel(level, true);
        var rp = GraphicsSettings.currentRenderPipeline;
        string pipeline = rp != null ? rp.name : "built-in";

        var cam = Camera.main;
        if (cam == null) return name + ": ERROR no Main Camera";

        int w = Width, h = Height;
        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, msaa);
        var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        var prevActive = RenderTexture.active;
        var prevTarget = cam.targetTexture;
        var times = new double[SampleFrames];
        var sw = new Stopwatch();

        try
        {
            cam.targetTexture = rt;
            for (int i = 0; i < WarmupFrames + SampleFrames; i++)
            {
                sw.Restart();
                cam.Render();
                RenderTexture.active = rt;
                probe.ReadPixels(new Rect(w * 0.5f, h * 0.5f, 1, 1), 0, 0, false);
                sw.Stop();
                if (i >= WarmupFrames) times[i - WarmupFrames] = sw.Elapsed.TotalMilliseconds;
            }
            RenderTexture.active = prevActive;
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            UnityEngine.Object.DestroyImmediate(probe);
            RenderTexture.ReleaseTemporary(rt);
        }

        Array.Sort(times);
        double mean = 0; for (int i = 0; i < times.Length; i++) mean += times[i];
        mean /= times.Length;
        double p50 = times[times.Length / 2];
        int p99Index = Math.Min(times.Length - 1, (int)Math.Floor(times.Length * 0.99));
        double p99 = times[p99Index];
        double avgFps = 1000.0 / mean;
        double lowFps = 1000.0 / p99;
        bool pass = avgFps >= 60.0 && lowFps >= 45.0;

        var csv = new StringBuilder();
        csv.Append("frame_ms\n");
        for (int i = 0; i < times.Length; i++)
            csv.Append(times[i].ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
        WriteArtifact("perf_" + name.ToLowerInvariant() + "_frames.csv", csv.ToString());

        return string.Format(CultureInfo.InvariantCulture,
            "{0}: pipeline={1} msaa={2} mean_ms={3:F3} p50_ms={4:F3} p99_ms={5:F3} avg_fps={6:F1} low1pct_fps={7:F1} -> {8}",
            name, pipeline, msaa, mean, p50, p99, avgFps, lowFps, pass ? "PASS" : "FAIL");
    }

    private static string ProjectRoot() { return Path.GetDirectoryName(Application.dataPath); }

    private static void WriteArtifact(string name, string text)
    {
        try
        {
            string workspace = Directory.GetParent(Directory.GetParent(ProjectRoot()).FullName).FullName;
            string runFile = Path.Combine(workspace, ".workbuddy", "artifacts", "m3", "current_run.txt");
            string run = File.ReadAllText(runFile).Trim();
            string dir = Path.Combine(workspace, ".workbuddy", "artifacts", "m3", run);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text, Encoding.UTF8);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[VchemPerf] artifact write failed: " + e.Message);
        }
    }
}
#endif
