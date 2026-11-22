using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Vchem.Assembly;
using Vchem.Core.Contracts;
using Vchem.Core.Judge;
using Vchem.Core.Model;
using Vchem.Handoff;
using Vchem.UI;

namespace Vchem.Showcase
{
    /// <summary>
    /// 打包 Player 里的 M2→M3 编排器：接管"进入 3D"、场景切换、装配、逐帧 Seek 播放。
    /// 编辑器验收（VchemShowcase）走自己的桥接路径，本类只在 Player 运行时被激活，
    /// 两者共用同一套判定/合成/装配/演出核心与同一份 Resources 资源，语义一致。
    /// </summary>
    public sealed class VchemRuntimeShowcase : MonoBehaviour
    {
        public const string PreviewScene = "M3_LathePreview";
        public const string DesignScene = "M2_PlanPanel";
        private const string ResRoot = "VchemContent";

        private static VchemRuntimeShowcase _instance;
        // 跨场景传递的提交数据（场景切换不清静态字段；Player 里不像编辑器那样频繁 domain reload）。
        private static Submission _pending;
        private static bool _bootstrapped;
        // --auto3d 快捷入口固定播放内置标准方案，避免 Player 按 exe 位置推算出的交接目录
        // 恰好残留了别的测试方案时，把快捷演示带偏（例如干扰项 CaSO4）。
        private static bool _autoDemo;
        private static string _shotPath;
        private static float _shotAt = -1f;

        private TimelinePlayer _player;
        private ShowcaseDirector _director;
        private bool _playing;
        private float _playT;
        private float _total;
        private float _playRate = 1f; // §10.29 播放倍速：1 或 2，仅影响推进速率，不改时间轴/哈希

        // ---- 播放 UI ----
        private Text _captionText;
        private Button _btnPlay;
        private Image _rateBtnImg;
        private Text _rateBtnLabel;

        // ---- 自动引导：任何场景加载后确保实例存在并注册 3D 入口 ----
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_bootstrapped && _instance != null) return;
            var go = new GameObject("Vchem.RuntimeShowcase");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<VchemRuntimeShowcase>();
            _bootstrapped = true;
            // 接管 UI 的"进入 3D"按钮：编辑器里这段由 VchemShowcase 注册，Player 里由本类注册。
            SubmissionHandoff.Enter3DHandler = Enter3D;
            Debug.Log("[Vchem.Runtime] bootstrap ready, Enter3DHandler registered");
            // 成果快捷方式：命令行带 --auto3d 时，启动即按上次提交方案进入 3D 演出（双击可直接看演示）。
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--auto3d", StringComparison.OrdinalIgnoreCase))
                {
                    _autoDemo = true;
                    Debug.Log("[Vchem.Runtime] --auto3d 触发，自动进入 3D 演示（固定内置标准方案）");
                    SceneManager.sceneLoaded += OnSceneLoaded;
                    SceneManager.LoadScene(PreviewScene);
                    break;
                }
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "--shot", StringComparison.OrdinalIgnoreCase))
                    _shotPath = args[i + 1];
                if (string.Equals(args[i], "--shotdelay", StringComparison.OrdinalIgnoreCase))
                {
                    float d;
                    if (float.TryParse(args[i + 1], out d)) _shotAt = Time.realtimeSinceStartup + d;
                }
            }
            // 未显式给 --shotdelay 但给了 --shot：默认 2.5s 后抓。
            if (!string.IsNullOrEmpty(_shotPath) && _shotAt < 0f) _shotAt = Time.realtimeSinceStartup + 2.5f;
        }

        private void Awake()
        {
            if (_instance == null) { _instance = this; DontDestroyOnLoad(gameObject); }
            else if (_instance != this) { Destroy(gameObject); }
        }

        /// <summary>M2 按钮调用：读交接、缓存到静态字段、切到 M3 场景。</summary>
        private static string Enter3D()
        {
            Submission sub;
            if (!SubmissionHandoff.TryLoad(out sub)) return "还没有可提交的方案：先完成装置并点「开始实验」";
            if (sub.Verdict == Codes.OutcomeError) return "结构还不合法，不能进 3D";
            _pending = sub;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.LoadScene(PreviewScene);
            return "OK: 正在进入 3D 演示…";
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!string.Equals(scene.name, PreviewScene, StringComparison.Ordinal)) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_instance != null) _instance.StartShowcase();
        }

        /// <summary>在 M3 场景里装配并按时间轴演出。</summary>
        private void StartShowcase()
        {
            Submission sub = _pending;
            if (sub == null && !_autoDemo)
            {
                if (!SubmissionHandoff.TryLoad(out sub))
                {
                    // 打包 Player 无工作区磁盘：回退到内置演示方案（--auto3d 快捷入口用）。
                    TextAsset demo = Resources.Load<TextAsset>(ResRoot + "/Demo/default.plan");
                    if (demo == null) { Debug.LogWarning("[Vchem.Runtime] 无提交记录且无内置演示方案，停留在空场景"); return; }
                    sub = new Submission { PlanJson = demo.text, Verdict = "success", Class = "R-OK-01", Source = "demo" };
                }
            }
            if (sub == null)
            {
                // --auto3d：直接取内置标准方案，不读 Player 按 exe 位置推算出的交接目录（可能有测试残留）。
                TextAsset demo = Resources.Load<TextAsset>(ResRoot + "/Demo/default.plan");
                if (demo == null) { Debug.LogWarning("[Vchem.Runtime] 无内置演示方案，停留在空场景"); return; }
                sub = new Submission { PlanJson = demo.text, Verdict = "success", Class = "R-OK-01", Source = "demo" };
            }
            _pending = sub;
            Plan plan;
            try { plan = Plan.FromJson(sub.PlanJson); }
            catch (Exception e) { Debug.LogError("[Vchem.Runtime] 方案解析失败: " + e.Message); return; }
            if (plan == null || plan.Topology == null || plan.Topology.Nodes.Count == 0)
            { Debug.LogError("[Vchem.Runtime] 提交方案无节点"); return; }

            Outcome outcome = Resolver.Resolve(plan);
            if (outcome.Verdict == Codes.OutcomeError)
            { Debug.LogError("[Vchem.Runtime] 方案结构非法: " + string.Join(",", outcome.Errors.ToArray())); return; }

            string timeline;
            try { timeline = TimelineComposer.Compose(plan, outcome); }
            catch (Exception e) { Debug.LogError("[Vchem.Runtime] 事件流合成失败: " + e.Message); return; }

            string err = BuildAndPlay(plan, timeline);
            Debug.Log("[Vchem.Runtime] " + err);
        }

        private string BuildAndPlay(Plan plan, string timelineJson)
        {
            TimelinePlayer player;
            try { player = TimelinePlayer.FromJson(timelineJson); }
            catch (Exception e) { return "ERROR: timeline 解析失败: " + e.Message; }
            var catalog = ProfileCatalog.LoadDefault();
            if (catalog.LoadErrors.Count > 0)
                return "ERROR: profile 解析失败: " + string.Join("; ", catalog.LoadErrors.ToArray());

            AssemblyResult asm = PlanAssembler.Build(plan, catalog, LoadAssemblyAssets());
            if (asm.Errors.Count > 0)
                return "ERROR: 装配解析失败: " + string.Join("; ", asm.Errors.ToArray());
            if (asm.Nodes.Count == 0) return "ERROR: 装配后没有有效节点";

            bool hasReactor = false;
            foreach (AssemblyNode n in asm.Nodes)
                if (n.Type == "reactor.flask_conical" || n.Type == "reactor.test_tube") { hasReactor = true; break; }
            if (!hasReactor) return "ERROR: 装配缺少反应容器，无法演出";

            if (_director != null) { _director.Dispose(); _director = null; }
            _player = player;
            _total = _player.TotalSec;
            _director = new ShowcaseDirector(_player, asm, catalog, LoadShowcaseAssets());
            _director.CaptureCamera(Camera.main);
            SetCamera();
            _director.CaptureCamera(Camera.main); // 机位复位后重新捕获基准
            _playT = 0f;
            _director.Seek(0f);
            _playing = true;
            BuildPlayUI();
            return "OK: 演出装配完成 class=" + (outcomeClass(asm, _player)) +
                   " events=" + _player.Events.Count + " total=" + _total.ToString("F1");
        }

        private static string outcomeClass(AssemblyResult asm, TimelinePlayer p) { return p.ClassName ?? ""; }

        private void Update()
        {
            if (!_playing || _director == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.25f);
            _playT += dt * _playRate;
            if (_playT >= _total) { _playT = _total; _playing = false; }
            _director.Seek(_playT);
        }

        private void LateUpdate()
        {
            RefreshUI();
            if (!string.IsNullOrEmpty(_shotPath) && _shotAt > 0f && Time.realtimeSinceStartup >= _shotAt)
            {
                // 调试开关：仅当命令行显式传 --shot <path> 时，用引擎自身抓一张真实画面（含 overlay UI）。
                // 外部 PrintWindow/ImageGrab 在 Windows DPI 缩放下会错位，验证 UI 必须用这个。
                ScreenCapture.CaptureScreenshot(_shotPath);
                Debug.Log("[Vchem.Runtime] shot saved: " + _shotPath);
                _shotPath = null;
            }
        }

        // ---- 播放控制（供 UI 按钮调用）----
        public void Play() { if (_director == null) return; if (_playT >= _total) _playT = 0f; _playing = true; }
        public void Pause() { _playing = false; }
        public void Replay() { if (_director == null) return; _playT = 0f; _playing = true; _director.Seek(0f); }
        public void CycleRate() { _playRate = _playRate >= 2f ? 1f : 2f; RefreshUI(); }
        public float Rate { get { return _playRate; } }
        public bool IsPlaying { get { return _playing; } }
        public float PlayTime { get { return _playT; } }
        public float Total { get { return _total; } }
        public bool Ready { get { return _director != null; } }

        // ---- 视角切换（无 UI 按钮，供桥接与自动验证调用）----
        public void CycleView() { if (_director != null) _director.NextView(); RefreshUI(); }
        public void SetView(int index) { if (_director != null) _director.SetView(index); RefreshUI(); }
        public int ViewIndex { get { return _director == null ? 0 : _director.ViewIndex; } }
        public int ViewCount { get { return _director == null ? 1 : _director.ViewCount; } }
        public string ViewName { get { return _director == null ? "标准" : _director.CurrentViewName; } }

        public string Caption()
        {
            if (_director == null || _director.Current == null) return "正在装配装置…";
            // §10.36：字幕统一由 ShowcaseCaption 生成，按时间轴 class 区分标准/干扰试剂。
            string cls = _player == null ? "" : _player.ClassName;
            return ShowcaseCaption.For(_director.Current, cls);
        }

        private static void SetCamera()
        {
            Camera c = Camera.main;
            if (c == null) return;
            // §10.39：默认机位统一为"示例"近景（与 Editor VchemAssembleCheck.SetCloseCamera 同步）。
            c.transform.position = new Vector3(0.22f, -0.78f, 0.55f);
            c.transform.rotation = Quaternion.LookRotation(
                new Vector3(0.00f, -0.90f, 0f) - c.transform.position);
            c.fieldOfView = 31f;
            c.nearClipPlane = 0.05f;
            c.farClipPlane = 200f;
            // 演示交付必须全屏：场景里保存的相机 rect 可能是历史验收改过的非全屏值
            // （实测 rect=(0,0.18,0.88,1.09)），会导致 3D 只画屏幕一块、露出切场景前的残影。
            c.rect = new Rect(0f, 0f, 1f, 1f);
        }

        // ---- 播放 UI：代码生成，不依赖场景预置对象 ----
        private static Font UiFont()
        {
            Font f = Resources.Load<Font>("Fonts/NotoSansSC-VF");
            return f != null ? f : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private GameObject Panel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
            return go;
        }

        private void BuildPlayUI()
        {
            if (_captionText != null) return;
            // M3 场景本身没有 EventSystem（M2 的由 PlanDesigner 运行时创建、不跨场景）。
            // 没有它 UI Button 收不到点击 → 暂停/重播/加速按钮全部失效。这里补建，与 M2 同款。
            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            }
            var canvasGo = new GameObject("Vchem.PlayUI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            // 按宽度适配：窗口比 16:9 更高时（如 1706×1066），按高度会让字幕右溢、底部控制条出屏。
            scaler.matchWidthOrHeight = 0f;

            // 顶部字幕条
            var top = Panel("Caption", canvasGo.transform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(0, -64), new Vector2(0, 0));
            var topImg = top.AddComponent<Image>();
            topImg.color = new Color(0.04f, 0.05f, 0.07f, 0.78f);
            var capGo = Panel("Text", top.transform, Vector2.zero, Vector2.one,
                new Vector2(24, 6), new Vector2(-24, -6));
            _captionText = capGo.AddComponent<Text>();
            _captionText.font = UiFont();
            _captionText.fontSize = 22;
            _captionText.color = new Color(0.95f, 0.96f, 0.98f, 1f);
            _captionText.alignment = TextAnchor.MiddleCenter;
            _captionText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _captionText.verticalOverflow = VerticalWrapMode.Overflow;
            _captionText.text = Caption();

            // 底部控制条
            var bottom = Panel("Controls", canvasGo.transform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 0), new Vector2(0, 74));
            var bImg = bottom.AddComponent<Image>();
            bImg.color = new Color(0.04f, 0.05f, 0.07f, 0.8f);

            var playGo = Panel("PlayButton", bottom.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(20, -22), new Vector2(116, 22));
            _btnPlay = playGo.AddComponent<Button>();
            var playImg = playGo.AddComponent<Image>();
            playImg.color = new Color(0.15f, 0.45f, 0.75f, 1f);
            var playTxt = Panel("Label", playGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).AddComponent<Text>();
            playTxt.font = UiFont(); playTxt.fontSize = 18; playTxt.color = Color.white;
            playTxt.alignment = TextAnchor.MiddleCenter; playTxt.text = "暂停";
            _btnPlay.onClick.AddListener(() => { if (IsPlaying) Pause(); else Play(); });

            var replayGo = Panel("ReplayButton", bottom.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(126, -22), new Vector2(222, 22));
            var replayBtn = replayGo.AddComponent<Button>();
            var replayImg = replayGo.AddComponent<Image>();
            replayImg.color = new Color(0.3f, 0.34f, 0.4f, 1f);
            var replayTxt = Panel("Label", replayGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).AddComponent<Text>();
            replayTxt.font = UiFont(); replayTxt.fontSize = 18; replayTxt.color = Color.white;
            replayTxt.alignment = TextAnchor.MiddleCenter; replayTxt.text = "重播";
            replayBtn.onClick.AddListener(Replay);

            // 加速按钮：固定标签「加速」，1× 时暗、2× 时高亮。只改播放推进速率，不改时间轴/哈希。
            var rateGo = Panel("RateButton", bottom.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(230, -22), new Vector2(314, 22));
            var rateBtn = rateGo.AddComponent<Button>();
            _rateBtnImg = rateGo.AddComponent<Image>();
            _rateBtnLabel = Panel("Label", rateGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).AddComponent<Text>();
            _rateBtnLabel.font = UiFont(); _rateBtnLabel.fontSize = 17;
            _rateBtnLabel.alignment = TextAnchor.MiddleCenter;
            _rateBtnLabel.text = "加速";
            rateBtn.onClick.AddListener(CycleRate);
            RefreshRateButton();

            // 问助教：暂停在当前时刻，按最近关键帧打开 AI 弹层。
            var askGo = Panel("AskButton", bottom.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(322, -22), new Vector2(442, 22));
            var askBtn = askGo.AddComponent<Button>();
            var askImg = askGo.AddComponent<Image>();
            askImg.color = new Color(0.55f, 0.36f, 0.14f, 1f);
            var askTxt = Panel("Label", askGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).AddComponent<Text>();
            askTxt.font = UiFont(); askTxt.fontSize = 17; askTxt.color = Color.white;
            askTxt.alignment = TextAnchor.MiddleCenter; askTxt.text = "问助教";
            askBtn.onClick.AddListener(OpenAssistant);

            // 返回方案设计：停止演出、切回 M2 画布。placed 在问助教右侧，参考分辨率内不越界。
            var backGo = Panel("BackToDesignButton", bottom.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(452, -22), new Vector2(592, 22));
            var backBtn = backGo.AddComponent<Button>();
            var backImg = backGo.AddComponent<Image>();
            backImg.color = new Color(0.22f, 0.35f, 0.46f, 1f);
            var backTxt = Panel("Label", backGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).AddComponent<Text>();
            backTxt.font = UiFont(); backTxt.fontSize = 17; backTxt.color = Color.white;
            backTxt.alignment = TextAnchor.MiddleCenter; backTxt.text = "返回方案设计";
            backBtn.onClick.AddListener(BackToDesign);

            // 时间进度条与时钟均已按需求隐藏。
        }

        /// <summary>暂停在当前时刻，按最近关键帧打开 AI 助教弹层。</summary>
        private void OpenAssistant()
        {            Pause();
            Submission sub = _pending;
            if (sub == null) SubmissionHandoff.TryLoad(out sub);
            if (sub == null)
            {
                if (_captionText != null) _captionText.text = "没有可用的提交记录，无法打开助教";
                return;
            }
            AiAssistantPanel.Open(sub, NearestKeyframe(_playT));
        }

        /// <summary>底部「返回方案设计」：停止演出、释放演出资源，切回 M2 画布供重新编辑。</summary>
        private void BackToDesign()
        {
            _playing = false;
            if (_director != null) { _director.Dispose(); _director = null; }
            _player = null;
            // PlayUI/EventSystem 是 M3 场景内创建的对象，随场景卸载销毁；这里断开引用，
            // 避免下次进入 M3 时 BuildPlayUI 的 `_captionText != null` 守卫误判为已建。
            _captionText = null;
            _btnPlay = null;
            _rateBtnImg = null;
            _rateBtnLabel = null;
            // 需求1：把正在演出的方案暂存给 M2，场景重载后 PlanDesigner.Awake 恢复画布。
            if (_pending != null && !string.IsNullOrEmpty(_pending.PlanJson))
                SubmissionHandoff.StageRestorePlan(_pending.PlanJson);
            SceneManager.LoadScene(DesignScene);
        }

#if UNITY_EDITOR
        /// <summary>Editor 桥接诊断入口：等价于点底部「返回方案设计」，不进 Player。</summary>
        public void BackToDesignForTest() { BackToDesign(); }

        /// <summary>Editor 桥接诊断入口：等价于点底部「问助教」，不进 Player。</summary>
        public void OpenAssistantForTest() { OpenAssistant(); }
#endif

        private static string NearestKeyframe(float t)
        {
            string best = "";
            float bestD = float.MaxValue;
            foreach (var kv in new[]
            {
                new KeyValuePair<string, float>("feed", 1.4f),
                new KeyValuePair<string, float>("reaction", 8.0f),
                new KeyValuePair<string, float>("verify", 21.8f),
                new KeyValuePair<string, float>("transfer", 23.4f),
                new KeyValuePair<string, float>("turbid", 26.4f),
                new KeyValuePair<string, float>("end", 32.0f),
            })
            {
                float d = Mathf.Abs(t - kv.Value);
                if (d < bestD) { bestD = d; best = kv.Key; }
            }
            return best;
        }

        private void RefreshUI()
        {
            if (_captionText == null) return;
            _captionText.text = Caption();
            var label = _btnPlay.GetComponentInChildren<Text>();
            if (label != null) label.text = IsPlaying ? "暂停" : "播放";
            RefreshRateButton();
        }

        /// <summary>加速按钮视觉：1× 暗（未启用），2× 亮（已启用）。标签固定为「加速」。</summary>
        private void RefreshRateButton()
        {
            if (_rateBtnImg == null) return;
            bool fast = Rate >= 2f;
            _rateBtnImg.color = fast ? new Color(0.16f, 0.70f, 0.45f, 1f) : new Color(0.18f, 0.21f, 0.25f, 1f);
            if (_rateBtnLabel != null)
                _rateBtnLabel.color = fast ? Color.white : new Color(0.55f, 0.58f, 0.62f, 1f);
        }

        // ---- 资源加载：与编辑器 VchemShowcase 完全相同的 Resources 路径 ----
        private static Material ResMat(string n) { return Resources.Load<Material>(ResRoot + "/Materials/" + n); }
        private static Mesh ResMesh(string n) { return Resources.Load<Mesh>(ResRoot + "/Meshes/" + n); }

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
    }
}
