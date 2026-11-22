#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Vchem.Assembly;
using Vchem.Core.Judge;
using Vchem.Core.Model;
using Vchem.Handoff;
using Vchem.Showcase;

/// <summary>
/// WP5 演出桥接入口（全局命名空间，供 DSHBridge exec 调用）。
/// 方法：Prepare / Load / Seek(arg=t) / Keyframes / Hash / Check / Clear / Diagnostics / PrepareFromSubmission。
/// 报告写在 chemical experiment 工程外的工作区 artifacts，工程内只留运行必需资产。
/// M2 只通过 SubmissionHandoff 交接，不改本类的 golden 验收路径。
/// </summary>
[InitializeOnLoad]
public static class VchemShowcase
{
    static VchemShowcase()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;
        SubmissionHandoff.Enter3DHandler = RequestPlayFromSubmission;
    }

    private static bool _autoPlayPending;

    // ---- 学生播放态：Edit 模式下用 update 推进 t，Seek 纯函数不变 ----
    private static bool _playing;
    private static float _playT;
    private static float _playRate = 1f;
    private static double _playLastRealtime;

    private const string PreviewScene = "Assets/Vchem/Scenes/M3_LathePreview.unity";
    private const string TimelineAsset = "Assets/Vchem/Content/Timeline/timeline.demo.json";
    private const string MatDir = "Assets/Vchem/Content/Materials";
    private const string MeshDir = "Assets/Vchem/Content/Meshes";
    private const string GoldenDir = "Tests/Golden";

    private static TimelinePlayer _player;
    private static ShowcaseDirector _director;
    private static string _lastSeek;
    private static AssemblyResult _lastAssembly; // H7：持有上一轮装配，便于释放导管临时网格
    private static string _currentPlanClass;     // M1：当前装配的方案 class，用于换轴防错配
    private static string _timelineJson;         // M1：当前轴的源文本，Check 复验用

    /// <summary>原有 golden 验收入口：固定 R-OK-01 + timeline.demo.json，行为保持不变。</summary>
    public static string Prepare()
    {
        string err;
        Plan plan = ResolvePlan("R-OK-01", out err);
        if (plan == null) return "ERROR: " + err;
        string tlPath = Path.Combine(ProjectRoot(), TimelineAsset);
        if (!File.Exists(tlPath)) return "ERROR: missing timeline " + TimelineAsset;
        return Build(plan, File.ReadAllText(tlPath), true, "golden", "R-OK-01");
    }

    /// <summary>按 M2 提交方案装配并演出；时间轴由当前 Outcome 确定性合成。</summary>
    public static string PrepareFromSubmission()
    {
        Submission sub;
        if (!SubmissionHandoff.TryLoad(out sub)) return "ERROR: 没有提交记录，先在 M2 提交方案";
        if (sub.Verdict == Codes.OutcomeError) return "ERROR: 结构非法，按方案门控不得进入 3D";

        Plan plan;
        try { plan = Plan.FromJson(sub.PlanJson); }
        catch (Exception e) { return "ERROR: 提交方案解析失败: " + e.Message; }
        if (plan == null || plan.Topology == null || plan.Topology.Nodes.Count == 0)
            return "ERROR: 提交方案无节点";

        // 由方案现算 outcome，不信任交接里的缓存结果；两者不一致只提示，不阻断。
        Outcome outcome = Resolver.Resolve(plan);
        if (outcome.Verdict == Codes.OutcomeError)
            return "ERROR: 方案在当前判定链下结构非法: " + string.Join(",", outcome.Errors.ToArray());
        string timeline;
        try { timeline = TimelineComposer.Compose(plan, outcome); }
        catch (Exception e) { return "ERROR: 事件流合成失败: " + e.Message; }
        string result = Build(plan, timeline, false, "submitted:" + (outcome.Class ?? outcome.Verdict),
            outcome.Class ?? outcome.Verdict);
        // 学生流程：装配成功后直接定位到"首个有可观现象"的关键帧（加液完成、气泡已起），
        // 避免落在 t=0 的空装置上让人以为"没有试剂、没有现象"。golden Prepare() 不走这里，
        // 保持 Seek(0) 静止以满足确定性验收。用 Seek 定位（会置 _playing=false），动画需另调 Play。
        if (result.StartsWith("OK", StringComparison.Ordinal))
        {
            float t = FirstPhenomenonTime();
            if (t > 0f) Seek(t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result;
    }

    /// <summary>
    /// 仅诊断：把任意 plan JSON 直接送进 Build，绕过 Resolver 门控，用于验证 H5/M9 的装配期防御。
    /// 不写交接记录、不供 UI 或学生流程调用；调用后需 Prepare()/PrepareFromSubmission() 恢复。
    /// </summary>
    public static string DiagnoseRawPlan(string arg)
    {
        if (string.IsNullOrEmpty(arg) || !File.Exists(arg)) return "ERROR: plan file not found " + arg;
        Plan plan;
        try { plan = Plan.FromJson(File.ReadAllText(arg)); }
        catch (Exception e) { return "ERROR: plan 解析失败: " + e.Message; }
        string tlPath = Path.Combine(ProjectRoot(), TimelineAsset);
        if (!File.Exists(tlPath)) return "ERROR: missing timeline " + TimelineAsset;
        return Build(plan, File.ReadAllText(tlPath), false, "diagnose-raw", "DIAG");
    }

    /// <summary>
    /// 仅诊断：把任意 plan 文件按真实流程 Resolver→TimelineComposer→Build 装配演出，
    /// 用于核对干扰试剂（稀硫酸 R-RXN-01 / 硫酸钙 R-RXN-05）的 3D 画面。
    /// 与 PrepareFromSubmission 等价，但不依赖交接记录；调用后需 Prepare() 恢复 golden 态。
    /// </summary>
    public static string PrepareInterference(string arg)
    {
        if (string.IsNullOrEmpty(arg) || !File.Exists(arg)) return "ERROR: plan file not found " + arg;
        Plan plan;
        try { plan = Plan.FromJson(File.ReadAllText(arg)); }
        catch (Exception e) { return "ERROR: plan 解析失败: " + e.Message; }
        Outcome outcome = Resolver.Resolve(plan);
        if (outcome == null) return "ERROR: Resolve 返回空";
        if (outcome.Verdict == Codes.OutcomeError)
            return "ERROR: 方案结构非法，不得进入 3D";
        string timeline = TimelineComposer.Compose(plan, outcome);
        return Build(plan, timeline, false, "interference:" + (outcome.Class ?? ""),
            outcome.Class ?? outcome.Verdict);
    }

    /// <summary>
    /// 仅诊断：对任意 plan 文件走 Resolver→TimelineComposer 合成事件流并原样返回 JSON 文本。
    /// 用于离线核对试剂类干扰项（稀硫酸递减气泡 / 硫酸钙无气泡）的合成结果。
    /// 不写交接、不构造 Director、不供 UI。
    /// </summary>
    public static string ComposeForPlan(string arg)
    {
        if (string.IsNullOrEmpty(arg) || !File.Exists(arg)) return "ERROR: plan file not found " + arg;
        Plan plan;
        try { plan = Plan.FromJson(File.ReadAllText(arg)); }
        catch (Exception e) { return "ERROR: plan 解析失败: " + e.Message; }
        Outcome outcome = Resolver.Resolve(plan);
        if (outcome == null) return "ERROR: Resolve 返回空";
        return TimelineComposer.Compose(plan, outcome);
    }

    /// <summary>
    /// 仅诊断：加载任意 timeline JSON 并对一组探针时刻求 Evaluate 状态，
    /// 用于验证 M6 连续通道（多 feed 窗口并集 / bubble 段间过渡 / 显式 liquid=0 不被兜底覆盖）。
    /// 不写交接、不构造 Director、不供 UI，调用后不影响 golden 验收态。
    /// </summary>
    public static string DiagnoseTimeline(string arg)
    {
        if (string.IsNullOrEmpty(arg) || !File.Exists(arg)) return "ERROR: timeline file not found " + arg;
        TimelinePlayer p;
        try { p = TimelinePlayer.FromJson(File.ReadAllText(arg)); }
        catch (Exception e) { return "ERROR: timeline 解析失败: " + e.Message; }
        var sb = new StringBuilder();
        sb.AppendLine("class=" + p.ClassName + " events=" + p.Events.Count +
                      " total=" + p.TotalSec.ToString("F1"));
        var bubEvents = new List<string>();
        foreach (var e in p.Events)
            if (e.Type == "bubbles.begin" || e.Type == "bubbles.rate")
            {
                object iv;
                double intensity = e.Payload != null && e.Payload.TryGetValue("intensity", out iv)
                    ? Convert.ToDouble(iv, System.Globalization.CultureInfo.InvariantCulture) : 0.0;
                bubEvents.Add(e.T.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "@" +
                    intensity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
        sb.AppendLine("bubbleAnchors=" + (bubEvents.Count == 0 ? "NONE" : string.Join(" ", bubEvents.ToArray())));
        sb.AppendLine("t,feed,bub,liq,cum,caption");
        float[] probes = { 1.5f, 2.1f, 2.75f, 4.0f, 5.0f, 6.5f, 9.0f, 13.0f };
        foreach (float t in probes)
        {
            if (t > p.TotalSec) continue;
            ShowcaseState s = p.Evaluate(t);
            sb.AppendFormat("{0:F1},{1:F3},{2:F3},{3:F3},{4:F1},{5}\n",
                t, s.FeedFlow, s.Bubbles, s.LiquidLevel, s.CumulativeMl,
                ShowcaseCaption.For(s, p.ClassName));
        }
        return sb.ToString();
    }

    /// <summary>M2 按钮调用：Play 中先退出，再由编辑器钩子自动进入 3D；Edit 中直接进入。</summary>
    public static string RequestPlayFromSubmission()
    {
        if (EditorApplication.isPlaying)
        {
            SubmissionHandoff.SetAutoPlay(true);
            EditorApplication.isPlaying = false;
            return "OK: 已发布提交方案，退出 Play 后自动进入 3D 演示";
        }
        return PrepareFromSubmission();
    }

    public static string SubmissionStatus()
    {
        Submission s;
        if (!SubmissionHandoff.TryLoad(out s)) return "无提交记录";
        return "class=" + (s.Class ?? "") + " verdict=" + (s.Verdict ?? "") +
               " submittedAt=" + (s.SubmittedAt ?? "") + " source=" + (s.Source ?? "") +
               " autoPlay=" + s.AutoPlay;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        // 不能在 playModeStateChanged 回调里直接切场景；用 update 等到编辑器完全回到 Edit 态。
        if (SubmissionHandoff.ConsumeAutoPlay()) _autoPlayPending = true;
    }

    private static void OnEditorUpdate()
    {
        // 学生播放：按真实时间推进 t，Seek 是纯函数，画面/状态随时重算。
        // Edit 模式下 Game 视图不会重绘（现象看不到），所以进入 3D 时应处于 Play 模式，
        // Play 模式下 Game 视图每帧刷新，装置与 FX 场景对象保留，Seek 结果实时可见。
        if (_playing && _director != null
            && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _playLastRealtime);
            _playLastRealtime = now;
            if (dt < 0f) dt = 0f;
            if (dt > 0.25f) dt = 0.25f;   // 失焦/卡顿后钳制，避免一次跳过整幕
            _playT += dt * _playRate;
            float total = _player == null ? 0f : _player.TotalSec;
            if (_playT >= total) { _playT = total; _playing = false; }
            ShowcaseState s = _director.Seek(_playT);
            _lastSeek = s.Serialize();
            if (_playing) RepaintViews();
        }

        if (!_autoPlayPending) return;
        if (EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        _autoPlayPending = false;
        string r = PrepareFromSubmission();
        Debug.Log("[VchemShowcase] 从 M2 提交自动进入 3D: " + r);
    }

    private static void RepaintViews()
    {
        // 仅请求重绘。注意：Edit 模式 Game 视图不会因脚本改 Transform/Material 自动刷新，
        // 这里只保证 Scene 视图/Inspector 更新；真正看动画请用 offscreen 序列或运行时播放入口。
        EditorApplication.QueuePlayerLoopUpdate();
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
    }

    /// <summary>开始/继续播放。t&lt;0 表示从当前播放位置继续。</summary>
    public static string Play(string arg)
    {
        if (_director == null) return "ERROR: call Prepare/PrepareFromSubmission first";
        float start;
        if (string.IsNullOrEmpty(arg)) start = _playT > 0f && _playT < _player.TotalSec ? _playT : 0f;
        else if (!float.TryParse(arg, System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture, out start))
            return "ERROR: arg must be a number (t) or empty";
        StartPlay(Mathf.Clamp(start, 0f, _player.TotalSec));
        return PlayStatus();
    }

    private static void StartPlay(float t)
    {
        _playT = t;
        _playRate = 1f;
        _playLastRealtime = EditorApplication.timeSinceStartup;
        _playing = true;
        if (_director != null)
        {
            ShowcaseState s = _director.Seek(_playT);
            _lastSeek = s.Serialize();
        }
        RepaintViews();
    }

    /// <summary>暂停播放（保留当前 t，可用 Play("") 继续）。</summary>
    public static string Pause()
    {
        _playing = false;
        return PlayStatus();
    }

    /// <summary>从头重播。</summary>
    public static string Replay()
    {
        return Play("0");
    }

    /// <summary>切换视角：arg 为序号(0=标准/1=全景/2=俯视/3=集气瓶)，或 next / 空(=next)。</summary>
    public static string SetView(string arg)
    {
        if (_director == null) return "ERROR: call Prepare first";
        int idx;
        string a = (arg ?? "").Trim().ToLowerInvariant();
        if (a == "" || a == "next") _director.NextView();
        else if (int.TryParse(a, out idx)) _director.SetView(idx);
        else return "ERROR: arg must be 0/1/2/3 or next";
        return ViewStatus();
    }

    public static string ViewStatus()
    {
        if (_director == null) return "no director";
        return "view=" + _director.ViewIndex + "/" + (_director.ViewCount - 1) +
               " name=" + _director.CurrentViewName;
    }

    /// <summary>§10.29 播放倍速：arg 为 "1"/"2"，或空/"toggle" 在 1↔2 间切换。
    /// 仅改推进速率，不改时间轴状态/哈希。</summary>
    public static string SetRate(string arg)
    {
        string a = (arg ?? "").Trim().ToLowerInvariant();
        if (a == "1" || a == "1x") _playRate = 1f;
        else if (a == "2" || a == "2x") _playRate = 2f;
        else if (a == "" || a == "toggle") _playRate = _playRate >= 2f ? 1f : 2f;
        else return "ERROR: arg must be 1/2/toggle";
        return "rate=" + _playRate.ToString("F1");
    }

    public static string PlayStatus()
    {
        if (_player == null) return "no player";
        float total = _player.TotalSec;
        // 以 director 实际渲染的 t 为准（Seek/Replay 后即便未播放也要反映真实画面时刻）。
        float shown = _director != null && _director.Current != null ? _director.Current.T : _playT;
        string state = !_playing ? "paused" : (shown >= total ? "ended" : "playing");
        return "state=" + state + " t=" + shown.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
               "/" + total.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
               " rate=" + _playRate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
               "\n" + (_lastSeek ?? "");
    }

    // ---- 播放器窗口用的只读访问器（不改状态，供 EditorWindow 每帧读取）----
    public static bool IsReady { get { return _director != null && _player != null; } }
    public static bool IsPlaying { get { return _playing; } }
    public static float PlayTime
    {
        get { return _director != null && _director.Current != null ? _director.Current.T : _playT; }
    }
    public static float PlayTotal { get { return _player == null ? 0f : _player.TotalSec; } }

    /// <summary>
    /// 时间轴里"首个能看见现象"的时刻：第一个 bubbles.begin 之后 0.6s（液体已注入、气泡已起）；
    /// 没有气泡事件则取 liquid.level 之后 0.4s。找不到返回 0。
    /// </summary>
    private static float FirstPhenomenonTime()
    {
        if (_player == null) return 0f;
        float firstBub = -1f;
        foreach (var e in _player.Events)
            if (e.Type == "bubbles.begin" && (firstBub < 0f || e.T < firstBub)) firstBub = e.T;
        if (firstBub >= 0f) return Mathf.Min(_player.TotalSec, firstBub + 0.6f);
        float firstLiq = -1f;
        foreach (var e in _player.Events)
            if (e.Type == "liquid.level" && (firstLiq < 0f || e.T < firstLiq)) firstLiq = e.T;
        return firstLiq >= 0f ? Mathf.Min(_player.TotalSec, firstLiq + 0.4f) : 0f;
    }

    /// <summary>按当前状态生成一句中文阶段字幕，供播放器显示。</summary>
    public static string PlaybackCaption()
    {
        if (_director == null || _director.Current == null) return "未装配 · 请先提交方案进入 3D";
        // §10.36：字幕统一由 ShowcaseCaption 生成，按时间轴 class 区分标准/干扰试剂。
        string cls = _player == null ? "" : _player.ClassName;
        return ShowcaseCaption.For(_director.Current, cls);
    }

    /// <summary>Prepare / PrepareFromSubmission 共用的装配与导演构造。</summary>
    private static string Build(Plan plan, string timelineJson, bool saveScene, string label, string planClass)
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!string.Equals(scene.path, PreviewScene, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(ProjectRoot(), PreviewScene)))
                return "ERROR: missing preview scene " + PreviewScene;
            EditorSceneManager.OpenScene(PreviewScene, OpenSceneMode.Single);
        }
        VchemAssembleCheck.PrepareScene();
        // 先复位到装配近景机位再捕获基准：上一次 Seek 可能把相机推到了转接位，
        // 若直接捕获会得到漂移的基准，推镜量越积越大、装置被裁。
        VchemAssembleCheck.SetCamera();

        TimelinePlayer player;
        try { player = TimelinePlayer.FromJson(timelineJson); }
        catch (Exception e) { return "ERROR: timeline 解析失败: " + e.Message; }
        var catalog = ProfileCatalog.LoadDefault();
        if (catalog.LoadErrors.Count > 0)
            return "ERROR: profile 解析失败: " + string.Join("; ", catalog.LoadErrors.ToArray());
        AssemblyResult asm = PlanAssembler.Build(plan, catalog, LoadAssemblyAssets());
        // M9：解析期的未知 profile / 重复 id / 缺 anchor 必须在这里挡住，
        // 不能让 Director 对着半套装配继续演，也不能把跨 planning 的错报成成功。
        if (asm.Errors.Count > 0)
            return "ERROR: 装配解析失败: " + string.Join("; ", asm.Errors.ToArray());
        if (asm.Nodes.Count == 0)
            return "ERROR: 装配后没有有效节点";
        // H5：解析无错仍可能缺关键节点（例如 plan 只有导管/集气瓶）。
        // 演出基元以发生器为锚，缺反应器时宁可显式拒绝，也不要构造半套 Director。
        bool hasReactor = false;
        foreach (AssemblyNode n in asm.Nodes)
            if (n.Type == "reactor.flask_conical" || n.Type == "reactor.test_tube")
            { hasReactor = true; break; }
        if (!hasReactor)
            return "ERROR: 装配缺少反应容器（reactor.flask_conical / reactor.test_tube），无法演出";

        // Domain reload 会清空静态字段；按名字清掉场景残留演出根，避免两套重叠特效。
        PurgeShowcaseRoots();
        if (_director != null) { _director.Dispose(); _director = null; }
        // H7：导管网格是每次 Build 现生成的 UnityEngine.Object，重复 Prepare 必须显式释放旧的。
        ReleaseAssembly(_lastAssembly);
        _lastAssembly = asm;
        _player = player;
        _currentPlanClass = string.IsNullOrEmpty(planClass) ? player.ClassName : planClass;
        _timelineJson = timelineJson;
        _lastSeek = "";
        _director = new ShowcaseDirector(_player, asm, ProfileCatalog.LoadDefault(), LoadShowcaseAssets());
        _director.CaptureCamera(Camera.main);
        _director.Seek(0f);

        // H7：装配根、FX 与导管网格都是运行时临时对象，写进 .unity 会留下 fileID:0 空引用；
        // 每次 Build 都完整重建，因此不再 SaveOpenScenes。saveScene 参数仅为兼容调用方保留。
        return "OK: " + label + " " + asmReport(asm) + " events=" + _player.Events.Count +
               " hash=" + _player.Hash() + " transient=no-save" +
               " unknown=" + (_player.UnknownEvents.Count == 0 ? "0" : string.Join(",", _player.UnknownEvents.ToArray()));
    }

    public static string Load(string arg)
    {
        string path = !string.IsNullOrEmpty(arg) && File.Exists(arg) ? arg : Path.Combine(ProjectRoot(), TimelineAsset);
        if (!File.Exists(path)) return "ERROR: timeline not found " + path;
        string json = File.ReadAllText(path);
        TimelinePlayer parsed;
        try { parsed = TimelinePlayer.FromJson(json); }
        catch (Exception e) { return "ERROR: timeline 解析失败: " + e.Message; }
        // M1：时间轴声明了 plan 就必须和当前装配方案一致，否则是"错轴配错装置"。
        // 未声明 plan 的诊断用轴（负向样例、手工实验轴）不受约束。
        if (!string.IsNullOrEmpty(parsed.PlanName) && !string.IsNullOrEmpty(_currentPlanClass))
        {
            string axisClass = Path.GetFileNameWithoutExtension(parsed.PlanName);
            if (axisClass.EndsWith(".plan", StringComparison.OrdinalIgnoreCase))
                axisClass = axisClass.Substring(0, axisClass.Length - 5);
            if (!string.Equals(axisClass, _currentPlanClass, StringComparison.OrdinalIgnoreCase))
                return "ERROR: 时间轴配套方案 " + axisClass + " 与当前装配 " + _currentPlanClass +
                       " 不符；请用 Prepare() 或 PrepareFromSubmission() 重装配";
        }
        _playing = false;
        if (_director != null) _director.Dispose();
        _director = null;
        _player = parsed;
        _timelineJson = json;
        return "OK: class=" + _player.ClassName + " plan=" + (parsed.PlanName ?? "") +
               " events=" + _player.Events.Count +
               " unknown=" + (parsed.UnknownEvents.Count == 0 ? "0" : string.Join(",", parsed.UnknownEvents.ToArray()));
    }

    public static string Seek(string arg)
    {
        if (_director == null) return "ERROR: call Prepare first";
        float t;
        if (!float.TryParse(arg, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out t))
            return "ERROR: arg must be a number (t)";
        // 手动定位视为接管播放：停掉自动推进，但同步 _playT 以便 Play("") 从此处续播（golden 验收也走这里）。
        _playing = false;
        _playT = Mathf.Clamp(t, 0f, _player.TotalSec);
        ShowcaseState s = _director.Seek(_playT);
        _lastSeek = s.Serialize();
        return _lastSeek + "\nFX " + _director.Effects.Diagnostics();
    }

    public static string Diagnostics()
    {
        if (_director == null) return "ERROR: no director";
        return _lastSeek + "\nFX " + _director.Effects.Diagnostics();
    }

    public static string Hash()
    {
        if (_player == null) return "ERROR: call Prepare first";
        return _player.Hash();
    }

    /// <summary>诊断火焰/白雾 billboard 的实际 transform 与材质，用于排查盖屏问题。</summary>
    public static string FxDiag()
    {
        var sb = new StringBuilder();
        Camera cam = Camera.main;
        if (cam != null)
        {
            sb.Append("CAM pos=").Append(cam.transform.position.ToString("F3"))
              .Append(" fwd=").Append((cam.transform.forward).ToString("F3"))
              .Append(" fov=").Append(cam.fieldOfView.ToString("F0")).Append(';');
            var probe = GameObject.Find("TestTubeLiquid");
            if (probe != null)
            {
                Vector3 sp = cam.WorldToScreenPoint(probe.transform.position);
                sb.Append("TESTTUBE world=").Append(probe.transform.position.ToString("F3"))
                  .Append(" screen=").Append(sp.ToString("F0")).Append(';');
            }
        }
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name != EffectPrimitives.RootName) continue;
            foreach (string key in new[] { "FX_flame/Flame", "FX_flame/Splint", "FX_mist/MouthMist" })
            {
                Transform t = root.transform.Find(key);
                if (t == null) { sb.Append(key).Append("=MISSING;"); continue; }
                var mr = t.GetComponent<MeshRenderer>();
                string sh = mr != null && mr.sharedMaterial != null && mr.sharedMaterial.shader != null
                    ? mr.sharedMaterial.shader.name : "null";
                sb.Append(key).Append(" pos=").Append(t.position.ToString("F3"))
                  .Append(" scale=").Append(t.localScale.ToString("F3"))
                  .Append(" active=").Append(t.gameObject.activeInHierarchy)
                  .Append(" shader=").Append(sh).Append(';');
            }
        }
        return sb.ToString();
    }

    /// <summary>6 关键帧现象可辨断言（G3）。</summary>
    public static string Keyframes()
    {
        if (_player == null) return "ERROR: call Prepare first";
        // M1：六关键帧是 R-OK-01 专用断言；其它 class 的提交方案没有验满/浑浊幕，
        // 套用会以 FAIL 误报。这里交给 Check 的通用判据（hash/对齐/outcome）验收。
        if (_player.ClassName != "R-OK-01")
            return "# G3 关键帧断言\n\n当前 class=" + _player.ClassName +
                   " 无专用关键帧；按通用判据验收。\n\n**六关键帧：N/A**";
        var sb = new StringBuilder();
        string[] labels = { "feed", "reaction", "verify", "transfer", "turbid", "end" };
        float[] times = { 1.4f, 8.0f, 21.8f, 23.4f, 26.4f, 32.0f };
        sb.AppendLine("# G3 六关键帧现象断言");
        sb.AppendLine();
        sb.AppendLine("| 时刻 | 幕 | 关键帧 | 断言 | 观测 | 结论 |");
        sb.AppendLine("|---:|---|---|---|---|---|");
        bool allPass = true;
        for (int i = 0; i < times.Length; i++)
        {
            ShowcaseState s = _player.Evaluate(times[i]);
            string assertion, observed;
            bool pass;
            switch (labels[i])
            {
                case "feed":
                    pass = s.FeedFlow > 0.1f && s.LiquidLevel > 0f;
                    assertion = "液流可见 + 发生瓶液面出现";
                    observed = "feed=" + s.FeedFlow.ToString("F2") + " liq=" + s.LiquidLevel.ToString("F2");
                    break;
                case "reaction":
                    pass = s.Bubbles > 0.5f && s.CumulativeMl > 50f;
                    assertion = "气泡强度>0.5 + 累计产气>50mL";
                    observed = "bub=" + s.Bubbles.ToString("F2") + " cum=" + s.CumulativeMl.ToString("F0");
                    break;
                case "verify":
                    pass = s.Flame == "burning" && s.HeldMl > 220f;
                    assertion = "木条火焰燃烧 + 瓶内接近收满";
                    observed = "flame=" + s.Flame + " held=" + s.HeldMl.ToString("F0");
                    break;
                case "transfer":
                    pass = s.Stage == "transition" && s.CameraPush > 0f;
                    assertion = "转接幕 + 推镜进行中";
                    observed = "stage=" + s.Stage + " cam=" + s.CameraPush.ToString("F2");
                    break;
                case "turbid":
                    pass = s.Stage == "2" && s.Turbidity >= 0.60f;
                    assertion = "幕二石灰水浑浊≥0.60";
                    observed = "turb=" + s.Turbidity.ToString("F2");
                    break;
                default:
                    pass = s.Turbidity >= 0.90f && s.Bullet == "success";
                    assertion = "浑浊≥0.90 + 成功收尾";
                    observed = "turb=" + s.Turbidity.ToString("F2") + " end=" + s.Bullet;
                    break;
            }
            allPass &= pass;
            sb.AppendFormat("| {0:F1}s | {1} | {2} | {3} | {4} | {5} |\n",
                times[i], s.Stage, labels[i], assertion, observed, pass ? "PASS" : "FAIL");
        }
        sb.AppendLine();
        sb.AppendLine("**六关键帧：" + (allPass ? "PASS" : "FAIL") + "**");
        return sb.ToString();
    }

    /// <summary>G3 总验收：关键帧 + 事件对齐 + 两次随机 Seek 哈希一致，出报告。</summary>
    public static string Check()
    {
        if (_director == null) return "ERROR: call Prepare first";
        var sb = new StringBuilder();
        sb.AppendLine("# WP5 演出验收（G3）");
        sb.AppendLine();
        sb.Append("时间轴 class=").Append(_player.ClassName)
          .Append(" events=").Append(_player.Events.Count)
          .Append(" total=").Append(_player.TotalSec.ToString("F1")).AppendLine();
        sb.AppendLine();

        // 1) 时间轴哈希确定性。M1：复验当前轴的源，而非固定读 golden 文件——
        // 否则按提交方案演出后 Check 必然误报 FAIL。
        string source = !string.IsNullOrEmpty(_timelineJson)
            ? _timelineJson : File.ReadAllText(Path.Combine(ProjectRoot(), TimelineAsset));
        string h1 = _player.Hash(), h2 = TimelinePlayer.FromJson(source).Hash();
        bool hashOk = h1 == h2;
        sb.AppendLine("## 1. 事件流确定性");
        sb.AppendLine("- hash1=" + h1 + "  hash2=" + h2 + "  → " + (hashOk ? "PASS" : "FAIL"));
        sb.AppendLine();

        // 2) 随机 Seek 顺序无关：倒序 vs 正序同一 t 状态一致。
        float[] probes = { 3.7f, 12.3f, 21.9f, 24.6f, 29.4f };
        var forward = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (float t in probes) forward[t.ToString("F1")] = _player.Evaluate(t).Serialize();
        for (int i = probes.Length - 1; i >= 0; i--)
        {
            ShowcaseState s = _player.Evaluate(probes[i]);
            string key = probes[i].ToString("F1");
            if (forward[key] != s.Serialize()) hashOk = false;
        }
        sb.AppendLine("## 2. 随机 Seek（正序/倒序）状态一致");
        sb.AppendLine("- 5 个非关键帧时刻 → " + (hashOk ? "PASS" : "FAIL"));
        sb.AppendLine();

        // 3) 六关键帧。
        string kf = Keyframes();
        sb.Append(kf);
        bool kfPass = kf.Contains("六关键帧：PASS") || kf.Contains("六关键帧：N/A");

        // 4) 事件↔状态对齐：每条事件 t 与状态 stage 一致。
        sb.AppendLine();
        sb.AppendLine("## 3. 事件↔状态 stage 对齐");
        int alignOk = 0, alignBad = 0;
        foreach (TimelinePlayer.Event e in _player.Events)
        {
            ShowcaseState s = _player.Evaluate(e.T + 0.001f);
            string want = e.Stage;
            if (want.StartsWith("transition", StringComparison.Ordinal)) want = "transition";
            if (s.Stage == want) alignOk++; else alignBad++;
        }
        sb.AppendFormat("- 对齐 {0} / 失配 {1} → {2}\n", alignOk, alignBad, alignBad == 0 ? "PASS" : "FAIL");
        sb.AppendLine();

        // M1：非 golden class 没有专用关键帧，用收尾 outcome 做通用业务判据。
        string endBullet = _player.Evaluate(_player.TotalSec).Bullet;
        bool outcomeOk = endBullet == "success" || endBullet == "partial" ||
                         endBullet == "fail" || endBullet == "none";
        sb.AppendLine("## 4. 收尾 outcome 合法性（通用判据）");
        sb.AppendLine("- bullet=" + endBullet + " → " + (outcomeOk ? "PASS" : "FAIL"));
        sb.AppendLine();

        bool total = hashOk && kfPass && alignBad == 0 && outcomeOk;
        sb.AppendLine();
        sb.AppendLine("**总判定：" + (total ? "G3 PASS" : "G3 FAIL") + "**");

        WriteOutside("wp5_report.md", sb.ToString());
        var csv = new StringBuilder();
        csv.AppendLine("t,stage,feed,bubbles,liquid,turbidity,flame,mouthBubble,cameraPush,cumulativeMl,heldMl");
        for (int i = 0; i <= 64; i++)
        {
            float t = _player.TotalSec * i / 64f;
            ShowcaseState s = _player.Evaluate(t);
            csv.AppendFormat("{0:F2},{1},{2:F3},{3:F3},{4:F3},{5:F3},{6},{7:F3},{8:F3},{9:F1},{10:F1}\n",
                t, s.Stage, s.FeedFlow, s.Bubbles, s.LiquidLevel, s.Turbidity,
                s.Flame, s.MouthBubble, s.CameraPush, s.CumulativeMl, s.HeldMl);
        }
        WriteOutside("wp5_timeline.csv", csv.ToString());
        return sb.ToString();
    }

    /// <summary>释放上一轮装配里程序生成的导管网格（资产网格不归这里管）。</summary>
    private static void ReleaseAssembly(AssemblyResult asm)
    {
        if (asm == null) return;
        foreach (AssemblyNode n in asm.Nodes)
            if (n.GeneratedMesh != null) UnityEngine.Object.DestroyImmediate(n.GeneratedMesh);
    }

    public static string Clear()
    {
        _playing = false;
        if (_director != null) _director.Dispose();
        _director = null;
        ReleaseAssembly(_lastAssembly);
        _lastAssembly = null;
        _currentPlanClass = null;
        _timelineJson = null;
        PurgeShowcaseRoots();
        return "OK: showcase cleared";
    }

    private static void PurgeShowcaseRoots()
    {
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            if (root.name == EffectPrimitives.RootName)
                UnityEngine.Object.DestroyImmediate(root);
    }

    private static string asmReport(AssemblyResult r)
    {
        return "nodes=" + r.Nodes.Count + " hash=" + r.Hash + " interference=" + r.Interference.Count;
    }

    private static Plan ResolvePlan(string arg, out string error)
    {
        error = "";
        string candidate = Path.Combine(ProjectRoot(), GoldenDir, arg);
        if (!candidate.EndsWith(".plan.json", StringComparison.Ordinal)) candidate += ".plan.json";
        if (!File.Exists(candidate)) { error = "找不到 plan: " + candidate; return null; }
        try
        {
            Plan p = Plan.FromJson(File.ReadAllText(candidate));
            if (p.Topology == null || p.Topology.Nodes.Count == 0) { error = "plan 无节点"; return null; }
            return p;
        }
        catch (Exception e) { error = "plan 解析失败: " + e.Message; return null; }
    }

    private const string ResRoot = "VchemContent";
    private static Material ResMat(string name) { return Resources.Load<Material>(ResRoot + "/Materials/" + name); }
    private static Mesh ResMesh(string name) { return Resources.Load<Mesh>(ResRoot + "/Meshes/" + name); }

    private static AssemblyAssets LoadAssemblyAssets()
    {
        var a = new AssemblyAssets();
        foreach (string type in new[]
        {
            "reactor.flask_conical", "reactor.test_tube", "feed.thistle_funnel",
            "collect.gas_jar", "conduit.bent", "collect.water_trough", "support.stand"
        })
        {
            Mesh m = ResMesh(type);
            if (m != null) a.Meshes[type] = m;
        }
        a.GlassOuter = ResMat("M_GlassOuter");
        a.GlassInner = ResMat("M_GlassInner");
        a.Shadow = ResMat("M_ContactShadow");
        a.Opaque = ResMat("M_ApparatusOpaque");
        a.Quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        a.Cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
        return a;
    }

    private static ShowcaseAssets LoadShowcaseAssets()
    {
        return new ShowcaseAssets
        {
            LiquidBody = ResMat("M_LiquidBody"),
            LiquidSurface = ResMat("M_LiquidSurface"),
            Bubble = ResMat("M_Bubble"),
            GlassInner = ResMat("M_GlassInner"),
            Opaque = ResMat("M_ApparatusOpaque"),
            Cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx"),
            Quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx"),
            Sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx"),
            TestTube = ResMesh("reactor.test_tube")
        };
    }

    private static string ProjectRoot() { return Path.GetDirectoryName(Application.dataPath); }

    private static void WriteOutside(string name, string text)
    {
        try
        {
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
            Debug.LogWarning("[VchemShowcase] 报告外写失败: " + e.Message);
        }
    }
}
#endif
