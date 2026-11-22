using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vchem.Core.Contracts;

namespace Vchem.Handoff
{
    /// <summary>
    /// M2 提交 → M3 演出的单次交接。只持有数据（plan/result JSON + 结果类），
    /// 不持有任何 Unity 对象，因此 Play 模式退出、场景切换、domain reload 后仍可恢复。
    /// 磁盘镜像统一写在工程外的工作区 .workbuddy/artifacts/runs/current/，不污染 Unity 工程。
    /// </summary>
    public sealed class Submission
    {
        public string PlanJson = "";
        public string ResultJson = "";
        public string Class = "";
        public string Verdict = "";
        public string SubmittedAt = "";
        public string Source = "M2";
        /// <summary>M2 按钮请求“退出 Play 后自动进入 3D”，由编辑器钩子消费。</summary>
        public bool AutoPlay;
    }

    public static class SubmissionHandoff
    {
        private static Submission _current;
        /// <summary>
        /// 3D→M2「返回方案设计」时暂存提交的 plan.json：场景切换不清静态字段，
        /// M2 的 PlanDesigner.Awake 消费一次并清空；只在内存里，不落盘、不参与判定。
        /// </summary>
        private static string _restorePlanJson;

        public static Submission Current { get { return _current; } }
        public static bool HasCurrent { get { return _current != null; } }
        public static void StageRestorePlan(string planJson) { _restorePlanJson = planJson; }
        public static string TakeRestorePlan()
        {
            string s = _restorePlanJson;
            _restorePlanJson = null;
            return s;
        }

        /// <summary>由编辑器层注册的 3D 入口，避免 UI 程序集反向依赖编辑器程序集。</summary>
        public static Func<string> Enter3DHandler;

        public static string RequestEnter3D()
        {
            if (Enter3DHandler == null) return "3D 入口未注册（编辑器脚本尚未加载）";
            return Enter3DHandler();
        }

        /// <summary>.workbuddy/artifacts/runs/current/</summary>
        public static string DirectoryPath()
        {
            string project = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
            string workspace = System.IO.Directory.GetParent(
                System.IO.Directory.GetParent(project).FullName).FullName;
            return System.IO.Path.Combine(workspace, ".workbuddy", "artifacts", "runs", "current");
        }

        public static void Publish(Submission s)
        {
            if (s == null) return;
            _current = s;
            try
            {
                string dir = DirectoryPath();
                System.IO.Directory.CreateDirectory(dir);
                File.WriteAllText(System.IO.Path.Combine(dir, "plan.json"), s.PlanJson ?? "", Encoding.UTF8);
                File.WriteAllText(System.IO.Path.Combine(dir, "result.json"), s.ResultJson ?? "", Encoding.UTF8);
                File.WriteAllText(System.IO.Path.Combine(dir, "handoff.json"), Describe(s), Encoding.UTF8);
            }
            catch (Exception e)
            {
                // 无磁盘权限时内存交接仍然有效（例如打包运行环境）。
                UnityEngine.Debug.LogWarning("[Handoff] 落盘失败，仅保留内存交接: " + e.Message);
            }
        }

        public static void Clear()
        {
            _current = null;
            try
            {
                string dir = DirectoryPath();
                foreach (string name in new[] { "plan.json", "result.json", "handoff.json" })
                {
                    string f = System.IO.Path.Combine(dir, name);
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Handoff] 清理失败: " + e.Message);
            }
        }

        /// <summary>内存优先；domain reload 后从磁盘镜像恢复。</summary>
        public static bool TryLoad(out Submission s)
        {
            if (_current != null) { s = _current; return true; }
            s = null;
            try
            {
                string dir = DirectoryPath();
                string pf = System.IO.Path.Combine(dir, "plan.json");
                if (!File.Exists(pf)) return false;
                var sub = new Submission();
                sub.PlanJson = File.ReadAllText(pf, Encoding.UTF8);
                string rf = System.IO.Path.Combine(dir, "result.json");
                if (File.Exists(rf))
                {
                    sub.ResultJson = File.ReadAllText(rf, Encoding.UTF8);
                    try
                    {
                        var r = Json.ParseObject(sub.ResultJson);
                        sub.Class = Json.GetString(r, "class", "");
                        sub.Verdict = Json.GetString(r, "outcome", "");
                    }
                    catch (Exception) { /* 结果 JSON 坏掉时只靠 handoff.json */ }
                }
                string hf = System.IO.Path.Combine(dir, "handoff.json");
                if (File.Exists(hf))
                {
                    try
                    {
                        var h = Json.ParseObject(File.ReadAllText(hf, Encoding.UTF8));
                        sub.Class = Json.GetString(h, "class", sub.Class);
                        sub.Verdict = Json.GetString(h, "verdict", sub.Verdict);
                        sub.SubmittedAt = Json.GetString(h, "submittedAt", "");
                        sub.Source = Json.GetString(h, "source", sub.Source);
                        sub.AutoPlay = Json.GetBool(h, "autoPlay", false);
                    }
                    catch (Exception) { }
                }
                if (string.IsNullOrEmpty(sub.PlanJson)) return false;
                _current = sub;
                s = sub;
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Handoff] 读取失败: " + e.Message);
                return false;
            }
        }

        /// <summary>只改磁盘上的 autoPlay 标志；退出 Play 后 domain reload 仍可读到。</summary>
        public static void SetAutoPlay(bool value)
        {
            if (_current != null) _current.AutoPlay = value;
            try
            {
                string dir = DirectoryPath();
                System.IO.Directory.CreateDirectory(dir);
                string hf = System.IO.Path.Combine(dir, "handoff.json");
                Submission s;
                if (File.Exists(hf))
                {
                    try { s = FromMeta(Json.ParseObject(File.ReadAllText(hf, Encoding.UTF8))); }
                    catch (Exception) { s = _current ?? new Submission(); }
                }
                else s = _current ?? new Submission();
                s.AutoPlay = value;
                File.WriteAllText(hf, Describe(s), Encoding.UTF8);
                if (_current != null) _current.AutoPlay = value;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Handoff] autoPlay 落盘失败: " + e.Message);
            }
        }

        public static bool ConsumeAutoPlay()
        {
            bool value = false;
            try
            {
                string hf = System.IO.Path.Combine(DirectoryPath(), "handoff.json");
                if (File.Exists(hf))
                {
                    var h = Json.ParseObject(File.ReadAllText(hf, Encoding.UTF8));
                    value = Json.GetBool(h, "autoPlay", false);
                    if (value) { var s = FromMeta(h); s.AutoPlay = false; File.WriteAllText(hf, Describe(s), Encoding.UTF8); }
                }
            }
            catch (Exception) { value = false; }
            if (_current != null && value) _current.AutoPlay = false;
            return value;
        }

        public static string Describe()
        {
            Submission s;
            return TryLoad(out s) ? Describe(s) : "无提交记录";
        }

        private static string Describe(Submission s)
        {
            var root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["class"] = s.Class ?? "";
            root["verdict"] = s.Verdict ?? "";
            root["submittedAt"] = s.SubmittedAt ?? "";
            root["source"] = s.Source ?? "M2";
            root["autoPlay"] = s.AutoPlay;
            return Json.Stringify(root);
        }

        private static Submission FromMeta(Dictionary<string, object> h)
        {
            return new Submission
            {
                Class = Json.GetString(h, "class", ""),
                Verdict = Json.GetString(h, "verdict", ""),
                SubmittedAt = Json.GetString(h, "submittedAt", ""),
                Source = Json.GetString(h, "source", "M2"),
                AutoPlay = Json.GetBool(h, "autoPlay", false)
            };
        }
    }
}
