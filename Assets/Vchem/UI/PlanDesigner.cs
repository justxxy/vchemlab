using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Vchem.Core.Judge;
using Vchem.Core.Model;
using Vchem.Core.Reagents;
using Vchem.Handoff;

namespace Vchem.UI
{
    /// <summary>
    /// 方案设计（M2 阶段，契约 2.2 / 11.1）。
    /// 三栏布局：左「容器 / 试剂」选择区、中「装置区」可拖拽画布（横向可滚动）、右「诊断意见」。
    /// 学生把器材拖入画布、在画布内拖动改位置、点击语义连接点连线；
    /// 所有操作只翻译成 DesignState 的节点/边/属性，判定链（Vchem.Core）原样不动。
    /// </summary>
    public class PlanDesigner : MonoBehaviour
    {
        public DesignState State = new DesignState();

        private Canvas _canvas;
        private Font _font;

        // 文本引用
        private Text _checkAText, _checkBText;
        private Text _diagTitle, _diagBody, _diagLoc;
        private Text _collectorLabel;
        private Text _deviceCaption;
        // 需求4：固液同瓶直投被拒时的弹出提示（替代原常驻的"未选 ←..."引导文案）
        private GameObject _rejectToast;

        // 提交后的"结果是否过期"：任何改动都把右栏改成"请重新提交"
        private bool _hasResult;
        private string _submittedSig;
        // 需求2：底部单一动作按钮「检查 → 开始实验」两段式；已通过检查才允许进 3D。
        // "无反应"仍走 OnEnter3D 里的二次确认。
        private Button _btnAction;
        private Text _btnActionLabel;
        private bool _checkPassed;
        private Button _btnAsk;
        private bool _pendingNoneConfirm;
        // 危险/覆盖性操作的两段确认时间戳（4 秒内再点一次才执行）
        private float _confirmStandardAt = -99f;
        private float _confirmClearAt = -99f;

        // 左栏切换
        private GameObject _groupContainer, _groupReagent;
        private Image _tabContainerBg, _tabReagentBg;
        private bool _reagentTab = false;

        // 试剂卡高亮 / 收集方式按钮高亮
        private readonly Dictionary<string, Image> _reagentCardBg = new Dictionary<string, Image>();
        private string _selectedReagent;
        private Image _btnUpright, _btnInverted, _btnWater;
        private Image _btnSplint, _btnMouthBubble, _btnLimewater;

        // 装置区画布
        private RectTransform _content;
        private ScrollRect _deviceScroll;
        private const float ContentW = 1180f, ContentH = 414f;
        private const float ItemW = 120f, ItemH = 140f;
        private readonly Dictionary<string, DeviceItem> _items = new Dictionary<string, DeviceItem>();
        private readonly Dictionary<string, GameObject> _edgeLines = new Dictionary<string, GameObject>();
        // 端口视觉登记：一个端口一条记录（热区 + 空心环 + 悬停文字）。
        // 端口不再常驻画布：默认全部收起，指针停到器材上（或点一下器材 / 进入连接模式）才出现。
        private readonly Dictionary<string, PortVis> _ports = new Dictionary<string, PortVis>();
        private readonly Dictionary<string, List<PortVis>> _portsOfNode = new Dictionary<string, List<PortVis>>();
        private string _hoverPortKey;      // 指针正停在哪颗端口上（只给它出文字）
        private string _hoverDeviceId;     // 指针停在哪个器材上 → 显示它的端口
        private string _activeDeviceId;    // 点中的器材（触屏没有 hover，用点击来"点亮"端口）

        // ---------------- 长颈漏斗接入容器（固定装配态） ----------------
        // 接上以后漏斗不再自由摆放：位置锁定为相对容器的固定偏移，并叠加橡胶塞，
        // 视觉上就是课本里的"漏斗插入瓶口"的组装图。判定属性仍只写 submerged。
        private readonly Dictionary<string, string> _funnelOfFlask = new Dictionary<string, string>();  // 容器 -> 漏斗
        private readonly Dictionary<string, string> _flaskOfFunnel = new Dictionary<string, string>();  // 漏斗 -> 容器
        private readonly Dictionary<string, string> _asmSpot = new Dictionary<string, string>();         // 漏斗 -> below/above/inside
        private readonly Dictionary<string, GameObject> _stoppers = new Dictionary<string, GameObject>();// 容器 -> 橡胶塞
        private readonly Dictionary<string, Text> _funnelStatus = new Dictionary<string, Text>();        // 漏斗 -> "已接入"状态
        private readonly Dictionary<string, Text> _funnelNames = new Dictionary<string, Text>();         // 漏斗 -> 器材名（接入后收起，把右侧位置让给容器端口标签）
        private string _snapFlaskId, _snapKey;   // 拖动漏斗时的吸附目标（用于高亮与松手判定）
        private const float SnapRadius = 46f;
        // 器材线稿（课本式黑白线描）：按装填/朝向/液封等状态重画，不再切换 PNG
        private readonly Dictionary<string, StrokeGraphic> _deviceArt = new Dictionary<string, StrokeGraphic>();
        // 试卷式标注：装置字母编号 + 试剂名引线文字
        private readonly Dictionary<string, string> _letters = new Dictionary<string, string>();
        private readonly Dictionary<string, Text> _nameLabels = new Dictionary<string, Text>();
        private readonly Dictionary<string, Text> _liquidLabels = new Dictionary<string, Text>();
        private readonly Dictionary<string, Text> _solidLabels = new Dictionary<string, Text>();
        private readonly Dictionary<string, string> _stopperSig = new Dictionary<string, string>();
        // 长颈漏斗上"已加入试剂"的提示组（试剂名 + 向下箭头）
        private readonly Dictionary<string, GameObject> _funnelBadges = new Dictionary<string, GameObject>();
        // 学生拖到长颈漏斗里的试剂（仅用于显示，最终仍记到 State.LiquidReagent）
        private readonly Dictionary<string, string> _funnelReagent = new Dictionary<string, string>();
        private AnchorRef _pending;
        // 用户描述的快捷流程状态机：选导管→点容器→点集气瓶；选漏斗→点液面位置。
        // 用列表支持"先放漏斗再放导管"等任意放置顺序：每个器材独立记住自己的待连步骤。
        private enum LinkTask { ConduitIn, ConduitOut, FunnelBottom }
        private sealed class PendingTask
        {
            public LinkTask Kind;
            public string NodeId;
        }
        private readonly List<PendingTask> _tasks = new List<PendingTask>();

        // ---------------- 导管连接模式 ----------------
        // 按"导管连接"按钮进入：点一个端口、再点另一个端口，自动生成一根导管（conduit.bent 节点 +
        // 两条 gas 边），画布上画成课本里的双线折线管。判定链只看到导管节点与 gas 边，不感知画法。
        private bool _conduitMode;
        private Button _btnConduit;
        private Image _btnConduitBg;
        private Text _btnConduitLabel;
        private AnchorRef _conduitPending;                                  // 已选的第一个端口
        private readonly Dictionary<string, GameObject> _tubes = new Dictionary<string, GameObject>();
        private GameObject _tubeRoot;
        private static readonly Color C_ConduitPort = new Color(0.13f, 0.47f, 0.60f);   // 导管模式下可接端口着色
        private static readonly Color C_BtnOn = new Color(0.16f, 0.55f, 0.42f);

        // ---------------- 删除器材模式 ----------------
        // 按"删除器材"按钮进入：点一下画布上的器材＝删掉这件器材（容器连同里面的试剂一起清掉）。
        // 与导管连接模式互斥：两个模式都要"点画布"，同时开着会互相抢点击。
        private bool _deleteMode;
        private Button _btnDelete;
        private Image _btnDeleteBg;
        private Text _btnDeleteLabel;
        private static readonly Color C_BtnDel = new Color(0.74f, 0.26f, 0.20f);
        private static readonly Color C_DeviceDelTint = new Color(1f, 0.88f, 0.86f, 0.96f);  // 删除态下器材整体泛淡红

        private sealed class AnchorRef
        {
            public string NodeId, Port, Spot, Key;
            public string PortType;
            public Vector2 Local;       // 相对器材左下角
            public Image Go;            // 端口空心环（镜像热区为 null）
            public Color BaseColor;
        }

        /// <summary>一个端口的视觉与状态。环默认不可见（alpha=0），显隐与配色统一由 ApplyPortVisibility 决定。</summary>
        private sealed class PortVis
        {
            public string NodeId, Key, Base;   // Base = 去掉 _r 后的语义键
            public bool Mirrored;              // 右侧镜像：没插漏斗时只当热区，不画环
            public bool Connectable;           // 可接（瓶口/水槽/导管口/漏斗下端）；玻璃片、铁夹不可接
            public bool LogicHidden;           // 业务上暂时用不到（空瓶的液面点、已用掉的漏斗下端…）
            public GameObject Root;            // 热区根（透明，承接指针事件）
            public Image Ring;                 // 空心环
            public Text Label;                 // 端口名：只在这颗端口被指着时出现
            public GameObject Tag;             // 端口名标签（白底小条），随 Label 一起显隐
        }

        private static readonly Color C_TabOn = new Color(0.30f, 0.55f, 0.78f);
        private static readonly Color C_TabOff = new Color(0.84f, 0.88f, 0.93f);
        private static readonly Color C_CardOn = new Color(0.78f, 0.90f, 0.80f);
        private static readonly Color C_CardOff = new Color(0.94f, 0.95f, 0.97f);
        private static readonly Color C_ModeOn = new Color(0.78f, 0.90f, 0.80f);
        private static readonly Color C_ModeOff = new Color(0.88f, 0.91f, 0.95f);
        private static readonly Color C_Anchor = new Color(0.86f, 0.45f, 0.12f);
        // 端口配色：常态是中性的灰蓝细环，只有"指针指着/已选中/连接模式"才升到暖色。
        // 橙红系留给"错误/删除"，端口不再抢这个视觉层级。
        private static readonly Color C_PortIdle = new Color(0.44f, 0.55f, 0.67f);
        private static readonly Color C_PortHot = new Color(0.84f, 0.55f, 0.16f);
        private static readonly Color C_PortSel = new Color(0.86f, 0.40f, 0.10f);
        private static readonly Color C_PortOn = new Color(0.11f, 0.52f, 0.30f);    // 玻璃片已盖
        private static readonly Color C_PortLabel = new Color(0.18f, 0.24f, 0.32f); // 端口名文字（墨色，不再用橙色）
        private static readonly Color C_SlotFilled = new Color(0.12f, 0.32f, 0.55f);   // 试剂槽已填
        private static readonly Color C_SlotEmpty = new Color(0.62f, 0.24f, 0.07f);    // 试剂槽空缺（醒目但不刺眼）

        // 供交互组件读取
        public RectTransform CanvasRoot { get { return _canvas == null ? null : _canvas.transform as RectTransform; } }
        public float CanvasScaleFactor { get { return _canvas == null ? 1f : _canvas.scaleFactor; } }
        /// <summary>装置区 viewport（诊断用：算真实屏幕落点）。</summary>
        public RectTransform DeviceViewport { get { return _deviceScroll == null ? null : _deviceScroll.viewport; } }
        /// <summary>UI 相机：ScreenSpaceCamera 画布下，屏幕坐标换算必须用这台相机。</summary>
        public Camera UiCamera
        {
            get
            {
                if (_canvas == null) return Camera.main;
                if (_canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
                return _canvas.worldCamera;
            }
        }
        public Font UiFont { get { return _font; } }
        public string ReagentNameOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            ReagentDb.Reagent def = ReagentDb.Get(id);
            return def == null ? id : def.Name;
        }

        void Awake()
        {
            try
            {
                _font = LoadFont();
                BuildUI();
                // 需求1：3D 点「返回方案设计」后，恢复之前提交的方案（器材/连线/试剂/检验项）。
                string restore = SubmissionHandoff.TakeRestorePlan();
                if (!string.IsNullOrEmpty(restore)) RestoreFromPlanJson(restore);
                RefreshAll();
                Debug.Log("[PlanDesigner] ready, font=" + (_font == null ? "null" : _font.name));
            }
            catch (Exception e)
            {
                Debug.LogError("[PlanDesigner] Awake failed: " + e);
            }
        }

        /// <summary>系统动态字体（uGUI Text 原生支持，中文稳定）。按优先级尝试。</summary>
        private Font LoadFont()
        {
            // 优先用随包分发的 Noto Sans SC（OFL），避免目标机没有雅黑时掉字
            Font bundled = Resources.Load<Font>("Fonts/NotoSansSC-VF");
            if (bundled != null) { Debug.Log("[PlanDesigner] font: bundled NotoSansSC-VF"); return bundled; }

            string[] families = { "Microsoft YaHei", "SimHei", "Noto Sans SC", "Arial Unicode MS" };
            foreach (string f in families)
            {
                Font font = Font.CreateDynamicFontFromOSFont(f, 20);
                if (font != null) { Debug.Log("[PlanDesigner] font: " + f); return font; }
            }
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        // ---------------------------------------------------------------- UI 构建
        private void BuildUI()
        {
            GameObject root = new GameObject("M2_Panel");
            root.transform.SetParent(transform, false);
            _canvas = root.AddComponent<Canvas>();
            // 方案设计画布用 Overlay：ScreenSpaceCamera 在透视相机下会让 CanvasScaler 的
            // scaleFactor 与屏幕像素脱钩，窄视口下右栏会被裁掉。
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f; // 按高度适配，避免非 16:9 视口垂直溢出
            root.AddComponent<GraphicRaycaster>();

            Transform R = root.transform;

            // ---- 顶栏 ----
            MakeText(R, "任务卡：制取一瓶 CO₂，验满，并检验它   |   方案设计",
                new Vector2(16, 664), new Vector2(1248, 42), 20, FontStyle.Bold, new Color(0.1f, 0.16f, 0.24f));
            Panel(R, new Vector2(0, 658), new Vector2(1280, 3), new Color(0.30f, 0.55f, 0.78f));

            // ---- 左栏：容器 / 试剂 切换选择区 ----
            MakeText(R, "器材 / 试剂选择（点击或拖入装置区）", new Vector2(12, 618), new Vector2(298, 30), 15, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            Button tabC = MakeButton(R, "容器", new Vector2(12, 576), new Vector2(140, 38), OnTabContainer);
            Button tabR = MakeButton(R, "试剂", new Vector2(162, 576), new Vector2(140, 38), OnTabReagent);
            _tabContainerBg = tabC.GetComponent<Image>();
            _tabReagentBg = tabR.GetComponent<Image>();
            Panel(R, new Vector2(8, 96), new Vector2(298, 474), new Color(0.955f, 0.965f, 0.975f));

            _groupContainer = NewGroup(R, "Group_Container");
            _groupReagent = NewGroup(R, "Group_Reagent");
            BuildContainerCards(_groupContainer.transform);
            BuildReagentCards(_groupReagent.transform);

            // ---- 中栏：装置区（横向可拖拽画布） ----
            // 标题缩短，把右上角腾出来放两个按钮（导管连接 / 删除器材）
            MakeText(R, "装置区（拖入器材 · 拖动改位置）", new Vector2(316, 618), new Vector2(300, 30), 15, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            // 装置区右上角：导管连接（点两个端口即用导管把两件装置连起来）
            _btnConduit = MakeButton(R, "导管连接", new Vector2(620, 614), new Vector2(146, 34), OnToggleConduitMode);
            _btnConduitBg = _btnConduit.GetComponent<Image>();
            _btnConduitLabel = _btnConduit.GetComponentInChildren<Text>();
            // 装置区右上角：删除器材（点一下按钮进入删除态，再点画布上的器材即删除）
            _btnDelete = MakeButton(R, "删除器材", new Vector2(774, 614), new Vector2(146, 34), OnToggleDeleteMode);
            _btnDeleteBg = _btnDelete.GetComponent<Image>();
            _btnDeleteLabel = _btnDelete.GetComponentInChildren<Text>();
            BuildDeviceCanvas(R, new Vector2(312, 196), new Vector2(632, 414));
            // 状态提示条：压在画布顶部（滚动条在底部，若放底部会被遮住）；画布本身是白底，不再加底板
            _deviceCaption = MakeText(R, "", new Vector2(322, 576), new Vector2(612, 24), 13,
                FontStyle.Italic, new Color(0.25f, 0.30f, 0.37f));

            // 验满 / 检验融入装置面板底部：一条线与画布正文分开，两个独立模块（彼此无先后关系）
            Panel(R, new Vector2(312, 190), new Vector2(632, 2), new Color(0.30f, 0.55f, 0.78f));
            Panel(R, new Vector2(312, 96), new Vector2(196, 88), new Color(0.92f, 0.95f, 0.98f));
            MakeText(R, "验满", new Vector2(322, 156), new Vector2(120, 24), 14, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            _btnSplint = MakeButton(R, "燃着木条", new Vector2(322, 122), new Vector2(88, 28), OnToggleA).GetComponent<Image>();
            _btnMouthBubble = MakeButton(R, "瓶口气泡", new Vector2(416, 122), new Vector2(84, 28), OnMouthBubble).GetComponent<Image>();
            _checkAText = MakeText(R, "", new Vector2(322, 98), new Vector2(180, 22), 12, FontStyle.Normal, Color.black);

            Panel(R, new Vector2(516, 96), new Vector2(212, 88), new Color(0.92f, 0.95f, 0.98f));
            MakeText(R, "检验产物为 CO₂", new Vector2(526, 156), new Vector2(192, 24), 14, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            _btnLimewater = MakeButton(R, "澄清石灰水", new Vector2(526, 122), new Vector2(110, 28), OnToggleB).GetComponent<Image>();
            _checkBText = MakeText(R, "", new Vector2(526, 98), new Vector2(196, 22), 12, FontStyle.Normal, Color.black);

            // 收集装置：集气瓶朝向 / 水槽（与检验分开，避免误以为收集方式属于检验）
            Panel(R, new Vector2(736, 96), new Vector2(208, 88), new Color(0.90f, 0.94f, 0.97f));
            MakeText(R, "收集装置", new Vector2(746, 156), new Vector2(100, 24), 14, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            _btnUpright = MakeButton(R, "正放", new Vector2(746, 122), new Vector2(62, 28), () => SetCollector("upright")).GetComponent<Image>();
            _btnInverted = MakeButton(R, "倒置", new Vector2(812, 122), new Vector2(62, 28), () => SetCollector("inverted")).GetComponent<Image>();
            _btnWater = MakeButton(R, "排水", new Vector2(878, 122), new Vector2(62, 28), () => SetCollector("water")).GetComponent<Image>();
            _collectorLabel = MakeText(R, "", new Vector2(746, 98), new Vector2(192, 22), 12, FontStyle.Bold, new Color(0.48f, 0.19f, 0.07f));

            // ---- 右栏：诊断意见 ----（需求2：已删除原「进入 3D 演示」按钮，入口并入底部「检查」按钮）
            MakeText(R, "诊断意见", new Vector2(956, 618), new Vector2(200, 30), 16, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            Panel(R, new Vector2(952, 96), new Vector2(320, 514), new Color(0.99f, 0.965f, 0.90f));
            _diagTitle = MakeText(R, "尚未提交", new Vector2(964, 536), new Vector2(296, 60), 16, FontStyle.Bold, new Color(0.2f, 0.2f, 0.24f));
            _diagTitle.alignment = TextAnchor.UpperLeft;
            _diagBody = MakeText(R, "", new Vector2(964, 386), new Vector2(296, 144), 13, FontStyle.Normal, new Color(0.15f, 0.15f, 0.18f));
            _diagBody.alignment = TextAnchor.UpperLeft;
            _diagLoc = MakeText(R, "", new Vector2(964, 140), new Vector2(296, 230), 13, FontStyle.Normal, new Color(0.72f, 0.2f, 0.15f));
            _diagLoc.alignment = TextAnchor.UpperLeft;
            // 提交后可打开本机 AI 助教（解释 / 分层提示 / 提问 / 小结）。必须在 Panel 之后创建，否则被面板盖住。
            _btnAsk = MakeButton(R, "问助教", new Vector2(964, 100), new Vector2(200, 34), OnAskAssistant);
            _btnAsk.interactable = false;

            // ---- 底部操作 ----（需求3：示例按钮文案精简；需求2：提交按钮改为两段式「检查 / 开始实验」）
            MakeButton(R, "示例", new Vector2(16, 30), new Vector2(180, 48), OnLoadStandard);
            MakeButton(R, "清空", new Vector2(204, 30), new Vector2(96, 48), OnClear);
            _btnAction = MakeButton(R, "检查", new Vector2(952, 30), new Vector2(320, 48), OnAction, new Color(0.18f, 0.42f, 0.62f));
            _btnActionLabel = _btnAction.GetComponentInChildren<Text>();

            // 需求4：固液同瓶直投被拒时的弹出提示，默认隐藏；只在 RejectSimultaneousAddition 里显示一帧提示。
            _rejectToast = Panel(R, new Vector2(432, 320), new Vector2(416, 76), new Color(0.99f, 0.93f, 0.90f, 0.98f));
            Text toast = MakeText(_rejectToast.transform, "不可同时放入", new Vector2(8, 8), new Vector2(400, 60),
                22, FontStyle.Bold, new Color(0.65f, 0.15f, 0.12f));
            toast.alignment = TextAnchor.MiddleCenter;
            _rejectToast.SetActive(false);

            EnsureEventSystem();
        }

        /// <summary>装置区：ScrollRect（横向）+ content（宽于视口）+ 底部横向滚动条。</summary>
        private void BuildDeviceCanvas(Transform parent, Vector2 pos, Vector2 size)
        {
            GameObject scroll = new GameObject("DeviceScroll");
            scroll.transform.SetParent(parent, false);
            RectTransform srt = scroll.AddComponent<RectTransform>();
            srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.zero; srt.pivot = Vector2.zero;
            srt.anchoredPosition = pos; srt.sizeDelta = size;
            Image bg = scroll.AddComponent<Image>();
            bg.color = new Color(0.985f, 0.99f, 0.995f);
            scroll.AddComponent<RectMask2D>();
            _deviceScroll = scroll.AddComponent<ScrollRect>();
            _deviceScroll.horizontal = true; _deviceScroll.vertical = false;
            _deviceScroll.movementType = ScrollRect.MovementType.Clamped;
            _deviceScroll.viewport = srt;

            GameObject content = new GameObject("DeviceContent");
            content.transform.SetParent(scroll.transform, false);
            _content = content.AddComponent<RectTransform>();
            _content.anchorMin = Vector2.zero; _content.anchorMax = Vector2.zero; _content.pivot = Vector2.zero;
            _content.anchoredPosition = Vector2.zero; _content.sizeDelta = new Vector2(ContentW, ContentH);
            _deviceScroll.content = _content;

            // 地面线 + 画布提示（在 content 内，随滚动）
            RectTransform ground = Panel(_content, new Vector2(16, 54), new Vector2(ContentW - 32, 2),
                StrokeGraphic.Ink).GetComponent<RectTransform>();
            ground.gameObject.name = "GroundLine";
            // 画布内不再放说明文字：统一由装置区顶部的状态提示条显示，避免两行重叠

            // 横向滚动条
            GameObject sbGo = new GameObject("HScrollbar");
            sbGo.transform.SetParent(parent, false);
            RectTransform sbrt = sbGo.AddComponent<RectTransform>();
            sbrt.anchorMin = Vector2.zero; sbrt.anchorMax = Vector2.zero; sbrt.pivot = Vector2.zero;
            sbrt.anchoredPosition = pos + new Vector2(8, 6); sbrt.sizeDelta = new Vector2(size.x - 16, 10);
            Image sbBg = sbGo.AddComponent<Image>(); sbBg.color = new Color(0.86f, 0.89f, 0.93f);
            Scrollbar sb = sbGo.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.LeftToRight;
            GameObject sliding = new GameObject("Sliding Area");
            sliding.transform.SetParent(sbGo.transform, false);
            RectTransform slrt = sliding.AddComponent<RectTransform>();
            slrt.anchorMin = Vector2.zero; slrt.anchorMax = Vector2.one; slrt.offsetMin = new Vector2(4, 1); slrt.offsetMax = new Vector2(-4, -1);
            GameObject handle = new GameObject("Handle");
            handle.transform.SetParent(sliding.transform, false);
            RectTransform hrt = handle.AddComponent<RectTransform>();
            hrt.anchorMin = Vector2.zero; hrt.anchorMax = new Vector2(0.32f, 1); hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
            Image hi = handle.AddComponent<Image>(); hi.color = new Color(0.45f, 0.58f, 0.72f);
            sb.handleRect = hrt; sb.targetGraphic = hi;
            _deviceScroll.horizontalScrollbar = sb;
        }

        public void SetScrollEnabled(bool on) { if (_deviceScroll != null) _deviceScroll.enabled = on; }

        private GameObject NewGroup(Transform parent, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(1280, 720);
            return go;
        }

        // 容器卡：6 个器材，2 列 3 行，每项带示例图；点击=放置，拖拽=放入画布
        // 导管不再单独成卡：它在画布上只是"两端装置的连接"，统一走右上角〔导管连接〕按钮，
        // 否则学生会以为要把导管图标拖进去再手动拼。
        private void BuildContainerCards(Transform parent)
        {
            string[] types = { "reactor.flask_conical", "reactor.test_tube", "feed.thistle_funnel",
                               "collect.gas_jar", "collect.water_trough", "support.stand" };
            // 卡片与画布共用同一份线稿几何：缩小渲染，不再维护两套图
            string[] icons = { "reactor.flask_conical", "reactor.test_tube", "feed.thistle_funnel",
                               "collect.gas_jar", "collect.water_trough", "support.stand" };
            float[] xs = { 16, 158 };
            float[] ys = { 470, 378, 286 };
            for (int i = 0; i < types.Length; i++)
            {
                string t = types[i];
                float x = xs[i % 2], y = ys[i / 2];
                GameObject card = Card(parent, new Vector2(x, y), new Vector2(136, 84), () => OnAddNode(t));
                MakeGlyph(card.transform, icons[i], new Vector2(44, 24), 0.4f);
                MakeText(card.transform, ElementCatalog.NameOf(t), new Vector2(2, 3), new Vector2(132, 22),
                    15, FontStyle.Bold, new Color(0.12f, 0.16f, 0.22f)).alignment = TextAnchor.MiddleCenter;
                PaletteDragSource src = card.AddComponent<PaletteDragSource>();
                src.ElementType = t; src.Owner = this;
            }

            // 每行控制在 22 个汉字以内：uGUI 的 Wrap 对没有空格的中文长句会溢出（左右都被切）
            Text hint = MakeText(parent, "① 淡色圆点＝可接端口，指上去看名字（触屏点器材）\n② 长颈漏斗〔下端〕拖到锥形瓶端口上松手＝接入\n③ 点〔导管连接〕，依次点两个端口＝自动连导管\n④ 点〔删除器材〕，再点器材＝连液体一起删掉",
                new Vector2(16, 100), new Vector2(288, 88), 11, FontStyle.Italic, new Color(0.28f, 0.33f, 0.40f));
            hint.alignment = TextAnchor.UpperLeft;
            hint.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        // 试剂卡：固体 / 液体分开，每项带示例图；拖到器材上=加入，点击=选中（备用）
        private void BuildReagentCards(Transform parent)
        {
            MakeText(parent, "固体试剂（拖到锥形瓶上）", new Vector2(16, 548), new Vector2(286, 22), 14, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            string[] solids = { "reagent.caco3_lump", "reagent.limestone_lump", "reagent.caco3_powder", "reagent.caso4" };
            for (int i = 0; i < solids.Length; i++)
            {
                string id = solids[i];
                float x = 16 + (i % 2) * 142, y = 490 - (i / 2) * 66;
                GameObject card = Card(parent, new Vector2(x, y), new Vector2(136, 58), () => SelectSolid(id));
                _reagentCardBg[id] = card.GetComponent<Image>();
                MakeGlyph(card.transform, "reagent.solid", new Vector2(6, 6), 0.36f);
                MakeText(card.transform, ReagentNameOf(id), new Vector2(48, 8), new Vector2(86, 44),
                    12, FontStyle.Normal, new Color(0.12f, 0.16f, 0.22f));
                ReagentDragSource rg = card.AddComponent<ReagentDragSource>();
                rg.ReagentId = id; rg.IsSolid = true; rg.IconName = "reagent.solid"; rg.Owner = this;
            }
            MakeText(parent, "液体试剂（拖到长颈漏斗上）", new Vector2(16, 388), new Vector2(286, 22), 14, FontStyle.Bold, new Color(0.15f, 0.2f, 0.28f));
            string[] liquids = { "reagent.hcl_dilute", "reagent.hcl_conc", "reagent.h2so4_dilute", "reagent.water", "reagent.limewater" };
            for (int i = 0; i < liquids.Length; i++)
            {
                string id = liquids[i];
                float x = 16 + (i % 2) * 142, y = 326 - (i / 2) * 64;
                bool isTest = id == "reagent.limewater";
                GameObject card = Card(parent, new Vector2(x, y), new Vector2(136, 58),
                    () => { if (isTest) SelectIdentity(); else SelectLiquid(id); });
                if (!isTest) _reagentCardBg[id] = card.GetComponent<Image>();
                MakeGlyph(card.transform, "reagent.liquid", new Vector2(6, 6), 0.36f);
                MakeText(card.transform, ReagentNameOf(id), new Vector2(48, 8), new Vector2(86, 44),
                    12, FontStyle.Normal, new Color(0.12f, 0.16f, 0.22f));
                ReagentDragSource rg = card.AddComponent<ReagentDragSource>();
                rg.ReagentId = id; rg.IsSolid = false; rg.IconName = "reagent.liquid"; rg.Owner = this;
            }
            MakeText(parent, "做法：固体卡拖到锥形瓶上松手＝放入锥形瓶；\n液体卡拖到长颈漏斗顶部松手＝从漏斗加液（固液同瓶直投会触发判定）",
                new Vector2(16, 100), new Vector2(286, 60), 12, FontStyle.Italic, new Color(0.28f, 0.33f, 0.40f));
        }

        // ---------------------------------------------------------------- 交互回调
        private void OnTabContainer() { _reagentTab = false; RefreshTabs(); }
        private void OnTabReagent() { _reagentTab = true; RefreshTabs(); }

        private void OnAddNode(string type) { SpawnDevice(type, DefaultPos(type)); }

        // 点试剂卡：先选中并高亮，再提示"拖到哪里"；拖拽行为不变
        private void SelectSolid(string id)
        {
            _selectedReagent = id; TintReagentCards();
            SetCaption("已选中〔" + ReagentNameOf(id) + "〕：按住它拖到锥形瓶上松手，就加入固体");
        }
        private void SelectLiquid(string id)
        {
            _selectedReagent = id; TintReagentCards();
            SetCaption("已选中〔" + ReagentNameOf(id) + "〕：拖到长颈漏斗顶部＝从漏斗加液（不要直接倒进锥形瓶）");
        }

        private void TintReagentCards()
        {
            foreach (KeyValuePair<string, Image> kv in _reagentCardBg)
                if (kv.Value != null) kv.Value.color = kv.Key == _selectedReagent ? C_CardOn : C_CardOff;
        }
        private void SelectIdentity()
        {
            State.Identity = State.Identity == "limewater" ? "none" : "limewater";
            RefreshAll();
            SetCaption(State.Identity == "limewater"
                ? "澄清石灰水已放入〔检验产物为 CO₂〕：收满后通气检验；与验满谁先谁后都可以"
                : "已移出澄清石灰水：〔检验产物为 CO₂〕未检验");
        }

        /// <summary>试剂拖放：落到容器＝直接加入；落到长颈漏斗顶部＝从漏斗加入（只能液体）。
        /// 底层仍写 State.SolidReagent / State.LiquidReagent，判定链完全不感知拖拽。</summary>
        public void DropReagent(string reagentId, bool isSolid, Vector2 screenPos)
        {
            _dragReagent = null;
            _selectedReagent = null;
            TintReagentCards();
            ClearHover();
            if (string.IsNullOrEmpty(reagentId)) return;

            ReagentDb.Reagent def = ReagentDb.Get(reagentId);
            if (def != null && def.Role == "test")
            {
                SetCaption("〔" + def.Name + "〕是检验用试剂，请点它放入〔检验产物为 CO₂〕模块，不要加到反应容器里");
                return;
            }

            Node hit = DeviceAtScreenPoint(screenPos, 34f);
            // 漏斗接入后它的框整个盖在锥形瓶上：固体不能进漏斗，落点按容器算
            if (hit != null && hit.Type == "feed.thistle_funnel" && IsFunnelAssembled(hit.Id) && isSolid)
            {
                Node host = State.FindNode(_flaskOfFunnel[hit.Id]);
                if (host != null) hit = host;
            }
            if (hit == null)
            {
                if (IsInDeviceViewport(screenPos))
                    SetCaption(isSolid
                        ? "把固体拖到锥形瓶瓶身＝放入锥形瓶；稀盐酸等液体请拖到长颈漏斗顶部加入"
                        : "把液体拖到长颈漏斗顶部＝从漏斗加入锥形瓶（不要直接倒进锥形瓶）");
                else
                    SetCaption("试剂要拖到装置区里的锥形瓶 / 试管 / 长颈漏斗上，松手即可加入");
                return;
            }
            ApplyReagentToNode(hit, reagentId, isSolid);
        }

        // ---------------------------------------------------------------- 拖拽实时反馈
        private string _dragReagent;
        private string _hoverNodeId;
        // 每个器材的"可放置"选框（绿=能加，红=不能加），默认隐藏
        private readonly Dictionary<string, List<Image>> _dropHints = new Dictionary<string, List<Image>>();

        private static readonly Color C_HintOk = new Color(0.16f, 0.60f, 0.30f, 1f);
        private static readonly Color C_HintNo = new Color(0.72f, 0.24f, 0.19f, 1f);

        /// <summary>开始拖试剂：提示可放位置。</summary>
        public void BeginReagentDrag(string reagentId)
        {
            _dragReagent = reagentId;
            ReagentDb.Reagent def = ReagentDb.Get(reagentId);
            bool solid = def != null && def.Phase == "固";
            SetCaption("正在拖〔" + ReagentNameOf(reagentId) + "〕：" + (solid
                ? "松手到锥形瓶瓶身＝把固体放入锥形瓶（稀盐酸要从长颈漏斗加入）"
                : "松手到长颈漏斗顶部＝从漏斗加液（直接倒进已有大理石的锥形瓶会触发判定）"));
        }

        /// <summary>拖动中：指针经过的器材显示绿色/红色选框，让学生知道"这里能不能放"。</summary>
        public void HoverDropTarget(Vector2 screenPos)
        {
            Node n = DeviceAtScreenPoint(screenPos, 34f);
            ReagentDb.Reagent def = ReagentDb.Get(_dragReagent);
            bool solid = def != null && def.Phase == "固";
            // 与 DropReagent 同口径：漏斗接入后框盖在锥形瓶上，固体落点按容器算
            if (n != null && solid && n.Type == "feed.thistle_funnel" && IsFunnelAssembled(n.Id))
            {
                Node host = State.FindNode(_flaskOfFunnel[n.Id]);
                if (host != null) n = host;
            }
            string id = n == null ? null : n.Id;
            if (id == _hoverNodeId) return;
            ShowDropHint(_hoverNodeId, 0);
            _hoverNodeId = id;
            if (id == null) return;

            bool accepts;
            string why = null;
            if (n.Type == "reactor.flask_conical")
            {
                string gate = State.CheckAddition(solid, true);
                accepts = gate == "";
                if (gate == DesignState.AddRejectReaction)
                    why = "瓶内已有另一种试剂：固液同瓶直投会立即触发反应，当前阶段不可同时放入锥形瓶中";
                else if (gate == DesignState.AddGuideFunnel)
                    why = "稀盐酸要从长颈漏斗加入：先把大理石放入锥形瓶，再把酸倒进漏斗";
            }
            else if (n.Type == "reactor.test_tube") accepts = true;
            else if (n.Type == "feed.thistle_funnel")
            {
                accepts = !solid;
                if (solid) why = "长颈漏斗只能加液体：固体请直接放入锥形瓶";
            }
            else accepts = false;

            ShowDropHint(id, accepts ? 1 : 2);
            SetCaption(accepts
                ? "松手把〔" + ReagentNameOf(_dragReagent) + "〕加到" + ElementCatalog.NameOf(n.Type)
                : (why ?? ElementCatalog.NameOf(n.Type) + "上不能加试剂"));
        }

        private void ClearHover()
        {
            ShowDropHint(_hoverNodeId, 0);
            _hoverNodeId = null;
        }

        /// <summary>0=隐藏，1=可加（绿框），2=不可加（红框）。</summary>
        private void ShowDropHint(string nodeId, int state)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            List<Image> list;
            if (!_dropHints.TryGetValue(nodeId, out list)) return;
            bool on = state != 0;
            Color c = state == 2 ? C_HintNo : C_HintOk;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null) continue;
                list[i].gameObject.SetActive(on);
                if (!on) continue;
                Color cc = c;
                cc.a = i == 0 ? 0.10f : 1f;   // 第 0 个是淡淡的高亮底，其余是边框
                list[i].color = cc;
            }
        }

        /// <summary>器材的选框：一层淡底 + 四条边，随器材一起移动，不遮挡线稿。</summary>
        private void BuildDropHint(string nodeId, Transform parent)
        {
            GameObject root = new GameObject("DropHint");
            root.transform.SetParent(parent, false);
            RectTransform rrt = root.AddComponent<RectTransform>();
            rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.zero; rrt.pivot = Vector2.zero;
            rrt.anchoredPosition = Vector2.zero; rrt.sizeDelta = new Vector2(ItemW, ItemH);

            List<Image> list = new List<Image>();
            list.Add(HintBar(rrt, new Vector2(0, 0), new Vector2(ItemW, ItemH)));          // 淡底
            list.Add(HintBar(rrt, new Vector2(0, ItemH - 2), new Vector2(ItemW, 2)));       // 上
            list.Add(HintBar(rrt, new Vector2(0, 0), new Vector2(ItemW, 2)));               // 下
            list.Add(HintBar(rrt, new Vector2(0, 0), new Vector2(2, ItemH)));               // 左
            list.Add(HintBar(rrt, new Vector2(ItemW - 2, 0), new Vector2(2, ItemH)));       // 右
            foreach (Image im in list) im.gameObject.SetActive(false);
            _dropHints[nodeId] = list;
        }

        private static Image HintBar(RectTransform parent, Vector2 pos, Vector2 size)
        {
            GameObject go = new GameObject("HintBar");
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            Image img = go.AddComponent<Image>();
            img.raycastTarget = false;
            return img;
        }

        /// <summary>加料后的视觉反馈交给"器材外观变化 + 提示条"，这里不再闪框：
        /// 闪框要么依赖协程/计时器（编辑器暂停时会卡住），要么给 sprite 染色（会改变液体颜色）。</summary>

        /// <summary>把试剂应用到某个器材上（拖放命中和脚本化验证共用同一套规则）。</summary>
        private void ApplyReagentToNode(Node hit, string reagentId, bool isSolid)
        {
            HideRejectToast();   // 任何新的投放动作都先收起上一次「不可同时放入」提示
            if (hit == null) return;

            ReagentDb.Reagent def = ReagentDb.Get(reagentId);
            if (def == null)
            {
                SetCaption("未知试剂：" + reagentId + "（未写入试剂槽）");
                return;
            }
            if (def.Role == "test")
            {
                SetCaption("〔" + def.Name + "〕是检验用试剂，请放入〔检验产物为 CO₂〕模块，不要加到反应容器或漏斗里");
                return;
            }

            if (hit.Type == "reactor.flask_conical" || hit.Type == "reactor.test_tube")
            {
                // H5 投料守卫：大理石入锥形瓶、稀盐酸走长颈漏斗；固液同瓶直投触发 R-RXN-04 判定并拒收
                string gate = State.CheckAddition(isSolid, hit.Type == "reactor.flask_conical");
                if (gate == DesignState.AddRejectReaction) { RejectSimultaneousAddition(hit, isSolid); return; }
                if (gate == DesignState.AddGuideFunnel)
                {
                    SetCaption("稀盐酸不能直接倒进锥形瓶：先放〔大理石〕，再把〔稀盐酸〕倒进长颈漏斗，由漏斗加酸");
                    return;
                }
                if (isSolid) State.SolidReagent = reagentId;
                else { State.LiquidReagent = reagentId; State.LiquidRoute = "direct"; }   // 试管预装路线
                _funnelReagent.Clear();   // 试剂改为直接加在容器里，漏斗加料提示失效
                ClearFunnelReagentProps();
                // 瓶里一有液面，原本插在〔瓶内〕的漏斗下端就被淹没了 → 自动算液封
                bool sealedNow = !isSolid && SealFunnelOfFlask(hit.Id);
                RefreshAll();
                SetCaption("已把〔" + ReagentNameOf(reagentId) + "〕加入" + ElementCatalog.NameOf(hit.Type) +
                           (isSolid ? "：瓶底出现固体" : "：瓶内出现液体（液面上/液面下连接点已可用）") +
                           (sealedNow ? "；漏斗下端已被液面淹没，形成液封 ✓" : ""));
                return;
            }

            if (hit.Type == "feed.thistle_funnel")
            {
                if (isSolid)
                {
                    SetCaption("长颈漏斗只能加入液体试剂；固体请直接拖到锥形瓶里");
                    return;
                }

                // 关键：倒进漏斗 ≠ 倒进锥形瓶。酸先待在漏斗里（漏斗上方显示"稀盐酸 ↓"），
                // 锥形瓶保持空瓶形态；只有漏斗已经接入容器时，酸才顺管子流进瓶里。
                bool assembled = _flaskOfFunnel.ContainsKey(hit.Id);
                bool sealedNow = false;
                if (assembled)
                {
                    State.LiquidReagent = reagentId;
                    State.LiquidRoute = "funnel";   // 酸经漏斗入瓶＝正确加料路线
                    _funnelReagent.Remove(hit.Id);   // 已经流下去了，漏斗里不剩
                    State.SetNodeProp(hit.Id, "reagent", "");
                    sealedNow = SealFunnelOfFlask(_flaskOfFunnel[hit.Id]);
                }
                else
                {
                    _funnelReagent[hit.Id] = reagentId;
                    State.SetNodeProp(hit.Id, "reagent", reagentId);   // 让判定链也知道"酸在漏斗里"
                }
                RefreshAll();
                SetCaption(assembled
                    ? "已把〔" + ReagentNameOf(reagentId) + "〕倒进长颈漏斗：漏斗已接入锥形瓶，酸顺着管子流进瓶里（液面出现）" +
                      (sealedNow ? "，下端浸在液面下，形成液封 ✓" : "")
                    : "已把〔" + ReagentNameOf(reagentId) + "〕倒进长颈漏斗：酸现在只在漏斗里，锥形瓶还是空的；把漏斗下端拖到瓶口端口接入，酸才会流下去");
                return;
            }

            SetCaption("试剂要加到反应容器（锥形瓶）或长颈漏斗上，不能放到" + ElementCatalog.NameOf(hit.Type) + "上");
        }

        /// <summary>固液同瓶直投被判定拒绝：按 R-RXN-04 出诊断卡与端口定位，试剂不落槽。</summary>
        private void RejectSimultaneousAddition(Node reactor, bool addingSolid)
        {
            Outcome o = new Outcome { Class = "R-RXN-04", Verdict = Codes.OutcomeFail };
            string body = DiagnosisTable.Summary("D-RXN-04");
            foreach (string ck in DiagnosisTable.Checklist("D-RXN-04")) body += "\n· " + ck;
            body += addingSolid
                ? "\n· 瓶内已有稀盐酸：再直接投入大理石会立即触发反应，当前阶段不可同时放入锥形瓶中。"
                : "\n· 瓶内已有大理石：再直接倒入稀盐酸会立即触发反应，当前阶段不可同时放入锥形瓶中。";
            string loc = "";
            foreach (PortIssue pi in State.Locate(o)) loc += "· " + PortLabel(pi.NodeId, pi.Port) + "：" + pi.Hint + "\n";
            SetDiag("× 固液同瓶直投", body, loc, new Color(0.65f, 0.15f, 0.12f));
            SetCaption("判定拒绝本次投料：大理石放入锥形瓶，稀盐酸从长颈漏斗加入");
            ShowRejectToast();   // 需求4：仅此情形弹出「不可同时放入」（含硫酸钙+稀盐酸等干扰项）
        }

        /// <summary>命中测试：屏幕点落在哪个器材上（后添加的在上层，取最后命中者）。
        /// pad &gt; 0 时允许一定容错：矩形外 pad 像素内取最近的器材，避免"差一点没放到"。</summary>
        private Node DeviceAtScreenPoint(Vector2 screenPos, float pad = 0f)
        {
            Camera cam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
            Node found = null;
            foreach (KeyValuePair<string, DeviceItem> kv in _items)
            {
                if (kv.Value == null) continue;
                RectTransform rt = (RectTransform)kv.Value.transform;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, cam)) continue;
                Node n = State.FindNode(kv.Key);
                if (n != null) found = n;
            }
            if (found != null || pad <= 0f) return found;

            // 容错命中：算屏幕点到各器材矩形的最近距离，阈值内取最近
            float best = pad;
            foreach (KeyValuePair<string, DeviceItem> kv in _items)
            {
                if (kv.Value == null) continue;
                RectTransform rt = (RectTransform)kv.Value.transform;
                Vector2 c = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
                Vector2 half = new Vector2(rt.rect.width, rt.rect.height) * (rt.lossyScale.x * 0.5f);
                Vector2 d = screenPos - c;
                float dx = Mathf.Max(0f, Mathf.Abs(d.x) - half.x);
                float dy = Mathf.Max(0f, Mathf.Abs(d.y) - half.y);
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist > best) continue;
                Node n = State.FindNode(kv.Key);
                if (n == null) continue;
                best = dist; found = n;
            }
            return found;
        }
        private void SetCollector(string mode)
        {
            mode = mode == "inverted" || mode == "water" ? mode : "upright";
            string prevMode = State.CollectorMode;
            Node trough = State.FindNodeByType("collect.water_trough");
            Node jar = State.FindNodeByType("collect.gas_jar");

            // 离开排水法：水槽连同 mouth↔basin 的 water 边一起收掉（RemoveNode 会删关联边），
            // 避免"孤儿水槽"占掉 8 件名额。DeleteDevice 走真实点击删除路径，UI/数据同步。
            if (mode != "water" && trough != null)
            {
                DeleteDevice(trough.Id);
                trough = null;
            }

            // H3 + H4：选择排水法＝真的接通水槽。water 边在状态层建立，受 8/12 上限约束；
            // 任一步失败就回滚，绝不把"摆了水槽但没接通"留给提交判定去补。
            bool addedTrough = false;
            if (mode == "water")
            {
                if (trough == null)
                {
                    trough = State.AddNode("collect.water_trough");
                    if (trough == null)
                    {
                        State.CollectorMode = prevMode;
                        RefreshAll(); RefreshLines();
                        SetCaption("器材最多 " + Validator.MaxNodes + " 件，放不下水槽；先删掉一件再切排水法");
                        return;
                    }
                    addedTrough = true;
                    SpawnDeviceFor(trough, DefaultPos(trough.Type));
                }
                if (jar != null)
                {
                    string werr = State.ConnectWater();
                    if (!string.IsNullOrEmpty(werr))
                    {
                        if (addedTrough) DeleteDevice(trough.Id);
                        State.CollectorMode = prevMode;
                        RefreshAll(); RefreshLines();
                        SetCaption("无法切换排水法：" + Codes.Message(werr) + "（连线最多 " + Validator.MaxEdges + " 条）");
                        return;
                    }
                }
            }

            State.CollectorMode = mode;

            // 收集方式联动集气瓶朝向/落点：倒置＝瓶口朝下贴台面；排水＝倒置浸进水槽。
            // 判定链只认状态层真实存在的 water 边；朝向与落点是纯表现联动。
            foreach (Node n in State.Nodes)
            {
                if (n.Type != "collect.gas_jar") continue;
                State.SetNodeProp(n.Id, "orientation", mode == "upright" ? "upright" : "inverted");
                DeviceItem it;
                if (!_items.TryGetValue(n.Id, out it) || it == null) continue;
                RectTransform rt = (RectTransform)it.transform;
                Vector2 p = rt.anchoredPosition;
                if (mode == "water" && trough != null)
                {
                    DeviceItem ti;
                    if (_items.TryGetValue(trough.Id, out ti) && ti != null)
                    {
                        Vector2 tp = ((RectTransform)ti.transform).anchoredPosition;
                        p = new Vector2(tp.x, tp.y + 10f);
                    }
                }
                else p.y = RestY(n.Type);
                rt.anchoredPosition = ClampDeviceFor(n.Id, p);
            }
            RefreshAll();
            RefreshLines();
            if (mode == "water" && jar == null)
                SetCaption("已切到排水法：水槽已就位，放入集气瓶后瓶口会自动接通水槽");
        }

        private void OnToggleA()
        {
            if (State.CollectorMode == "water")
            {
                SetCaption("排水法验满自动看瓶口气泡；燃着的木条适用于排空气法");
                return;
            }
            State.Fullness = State.Fullness == "none" ? "burning_splint" : "none";
            RefreshAll();
        }
        private void OnMouthBubble()
        {
            SetCaption(State.CollectorMode == "water"
                ? "排水法验满：瓶口冒出大气泡即已收满（自动观察，无需摆放）"
                : "瓶口气泡验满只适用于排水法：先把收集装置切到〔排水〕");
        }
        private void OnToggleB() { State.Identity = State.Identity == "limewater" ? "none" : "limewater"; RefreshAll(); }

        private void OnClear()
        {
            if (Time.time - _confirmClearAt > 4f)
            {
                _confirmClearAt = Time.time;
                SetCaption("再点一次〔清空〕＝清空当前方案（不可撤销）");
                return;
            }
            _confirmClearAt = -99f;
            ClearNow();
        }

        private void ClearNow()
        {
            State.Clear();
            ClearBoard();
            ResetCheckState();
            HideRejectToast();
            SetDiag("尚未提交", "", "");
            RefreshAll();
        }

        /// <summary>方案被改动/清空/恢复时统一复位：底部按钮退回「检查」，助教禁用。</summary>
        private void ResetCheckState()
        {
            _hasResult = false; _pendingNoneConfirm = false; _checkPassed = false;
            if (_btnActionLabel != null) _btnActionLabel.text = "检查";
            if (_btnAction != null)
            {
                Image img = _btnAction.GetComponent<Image>();
                if (img != null) img.color = new Color(0.18f, 0.42f, 0.62f);
            }
            if (_btnAsk != null) _btnAsk.interactable = false;
        }

        /// <summary>
        /// 需求2：底部按钮两段式。首次点击＝「检查」（跑判定，合法则按钮变「开始实验」）；
        /// 再次点击＝进入 3D。合法判定指 verdict != error（含 success/partial/fail/none，
        /// 例如硫酸钙 fail 也能进 3D 演示），与原「进入 3D 演示」的放行条件一致。
        /// </summary>
        private void OnAction()
        {
            if (_checkPassed) { OnEnter3D(); return; }

            HideRejectToast();
            OnSubmit();
            Submission s;
            bool ok = SubmissionHandoff.TryLoad(out s) && s != null && s.Verdict != Codes.OutcomeError;
            _checkPassed = ok;
            if (_btnActionLabel != null) _btnActionLabel.text = ok ? "开始实验" : "检查";
            if (_btnAction != null)
            {
                Image img = _btnAction.GetComponent<Image>();
                if (img != null) img.color = ok ? new Color(0.16f, 0.55f, 0.32f) : new Color(0.18f, 0.42f, 0.62f);
            }
            SetCaption(ok
                ? "检查通过：可以进入 3D 演示；点「开始实验」查看"
                : "方案还不合法：按右侧诊断补齐后再点「检查」");
        }

        private void OnSubmit()
        {
            // 与 State.Submit() 等价（Resolver.Resolve(BuildPlan())），但保留 plan 本身用于交接。
            Plan submitted = State.BuildPlan();
            Outcome o = Resolver.Resolve(submitted);
            string title; Color c;
            switch (o.Verdict)
            {
                case "success": title = "√ 目标达成"; c = new Color(0.1f, 0.45f, 0.2f); break;
                case "partial": title = "◐ 部分达成"; c = new Color(0.6f, 0.42f, 0.05f); break;
                case "fail": title = "× 实验失败"; c = new Color(0.65f, 0.15f, 0.12f); break;
                case "none": title = "○ 无现象"; c = new Color(0.4f, 0.4f, 0.4f); break;
                default: title = "还差一步：结构还不完整"; c = new Color(0.65f, 0.15f, 0.12f); break;
            }
            string body = o.Diagnosis != null ? o.Diagnosis.Summary : "";
            if (o.Diagnosis != null && o.Diagnosis.Checklist != null)
                foreach (string ck in o.Diagnosis.Checklist) body += "\n· " + ck;
            string loc = "";
            foreach (PortIssue pi in State.Locate(o)) loc += "· " + PortLabel(pi.NodeId, pi.Port) + "：" + pi.Hint + "\n";
            SetDiag(title, body, loc, c);
            _hasResult = true;
            _submittedSig = StateSignature();
            _pendingNoneConfirm = false;

            // 打通 M2→M3：提交即发布交接（plan + result + 结果类），由 3D 阶段消费。
            SubmissionHandoff.Publish(new Submission
            {
                PlanJson = PlanWriter.ToJson(submitted),
                ResultJson = o.ToJson(),
                Class = o.Class ?? "",
                Verdict = o.Verdict ?? "",
                SubmittedAt = submitted.SubmittedAt,
                Source = "M2"
            });
            // 按钮放行由 OnAction 统一处理（OnSubmit 本身只做判定+交接，不再直接开 3D 入口）。
            if (_btnAsk != null)
                _btnAsk.interactable = true;
            Debug.Log("[M2] submit -> " + (o.Class ?? o.Verdict) + " verdict=" + o.Verdict);
        }

        /// <summary>打开本机 AI 助教弹层；只在提交后可用。</summary>
        private void OnAskAssistant()
        {
            if (!_hasResult || !SubmissionHandoff.HasCurrent)
            {
                SetCaption("先提交方案，助教才有本次实验的上下文");
                return;
            }
            AiAssistantPanel.Open(SubmissionHandoff.Current);
        }

        /// <summary>判定通过后进入 3D：结构非法直接拦下；无反应给一次确认；其余按提交方案装配演出。</summary>
        private void OnEnter3D()
        {
            if (!_hasResult || StateSignature() != _submittedSig) OnSubmit();
            Submission s;
            if (!SubmissionHandoff.TryLoad(out s))
            {
                SetCaption("还没有可提交的方案：先完成装置并点「开始实验」");
                return;
            }
            if (s.Verdict == Codes.OutcomeError)
            {
                SetCaption("结构还不合法，不能进 3D：按右侧红字补齐装置");
                return;
            }
            if (s.Verdict == Codes.OutcomeNone && !_pendingNoneConfirm)
            {
                _pendingNoneConfirm = true;
                SetCaption("这个方案没有明显现象；再点一次「开始实验」确认观看空镜");
                return;
            }
            _pendingNoneConfirm = false;
            SetCaption("已提交方案，正在进入 3D 演示…");
            SetCaption(SubmissionHandoff.RequestEnter3D());
        }

        /// <summary>状态指纹：节点/边/属性/试剂/检验项/收集方式，任一变化即视为"需要重新提交"。</summary>
        private string StateSignature()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Node n in State.Nodes)
            {
                sb.Append(n.Id).Append(':').Append(n.Type).Append('{');
                List<string> keys = new List<string>(n.Props.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys) sb.Append(k).Append('=').Append(n.Props[k]).Append(';');
                sb.Append("} ");
            }
            foreach (Edge e in State.Edges)
            {
                sb.Append(e.From).Append('>').Append(e.To).Append(':').Append(e.Kind).Append('{');
                List<string> keys = new List<string>(e.Props.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys) sb.Append(k).Append('=').Append(e.Props[k]).Append(';');
                sb.Append("} ");
            }
            sb.Append("F=").Append(State.Fullness)
              .Append(";I=").Append(State.Identity)
              .Append(";S=").Append(State.SolidReagent)
              .Append(";L=").Append(State.LiquidReagent)
              .Append(";M=").Append(State.CollectorMode);
            return sb.ToString();
        }

        /// <summary>提交后方案又变了：右栏从"结论"改成"请重新提交"，避免旧结论误导。</summary>
        private void CheckStale()
        {
            if (!_hasResult) return;
            if (StateSignature() == _submittedSig) return;
            ResetCheckState();
            SetDiag("方案已修改，请重新提交", "改动尚未判定。", "");
        }

        private void OnLoadStandard()
        {
            if (Time.time - _confirmStandardAt > 4f)
            {
                _confirmStandardAt = Time.time;
                SetCaption("再点一次〔示例〕＝载入示例方案（会覆盖当前方案）");
                return;
            }
            _confirmStandardAt = -99f;
            LoadStandardProgrammatic();
            ResetCheckState();
            SetDiag("尚未提交", "", "");
            RefreshAll();
            SetCaption("示例已载入：建议先自己搭一遍，再对照示例");
        }

        /// <summary>载入文档 3.5 标准方案（节点/边/属性一次到位），并在画布上重建可拖拽器材。</summary>
        public void LoadStandardProgrammatic()
        {
            State.Clear();
            ClearBoard();
            Node flask = State.AddNode("reactor.flask_conical");
            Node funnel = State.AddNode("feed.thistle_funnel");
            Node conduit = State.AddNode("conduit.bent");
            Node jar = State.AddNode("collect.gas_jar");
            State.SetNodeProp(funnel.Id, "submerged", true);
            State.SetNodeProp(jar.Id, "orientation", "upright");
            State.SetNodeProp(jar.Id, "covered", true);
            State.Connect(funnel.Id + ".bottom", flask.Id + ".mouth", "feed");
            State.Connect(conduit.Id + ".in", flask.Id + ".mouth", "gas");
            State.Connect(conduit.Id + ".out", jar.Id + ".mouth", "gas");
            State.SetEdgeProp(conduit.Id + ".out", jar.Id + ".mouth", "depth", "deep");
            State.Fullness = "burning_splint"; State.Identity = "limewater";
            State.SolidReagent = "reagent.caco3_lump"; State.LiquidReagent = "reagent.hcl_dilute";
            State.LiquidRoute = "funnel";
            State.CollectorMode = "upright";

            // 重建画布实例（默认排布保证 4 件都在初始视口 632 宽内可见）
            SpawnDeviceFor(flask, new Vector2(140, 40));
            SpawnDeviceFor(funnel, new Vector2(140, 200));
            SpawnDeviceFor(conduit, new Vector2(300, 300));
            SpawnDeviceFor(jar, new Vector2(470, 14));
            SetPending(null);
            _tasks.Clear();
            // 标准方案里漏斗是插在瓶口上的：直接进入固定装配态
            AssembleFunnel(funnel.Id, flask.Id, "below");
            ResetCheckState();
            SetDiag("尚未提交", "", "");
            RefreshLines();
        }

        /// <summary>
        /// 需求1：从提交的 plan.json 恢复画布（3D 返回方案设计时用）。
        /// 与 LoadStandardProgrammatic 平行：数据回 State，画布按节点类型重建，
        /// 漏斗走 AssembleFunnel 复用装配态，导管由 gas 边自动成管。
        /// </summary>
        public bool RestoreFromPlanJson(string planJson)
        {
            Plan plan;
            try { plan = Plan.FromJson(planJson); }
            catch (Exception) { plan = null; }
            if (plan == null || plan.Topology == null || plan.Topology.Nodes.Count == 0) return false;

            State.Clear();
            ClearBoard();
            // 节点：保留原 id，属性原样写回（submerged / orientation / covered / reagent ...）
            foreach (Node src in plan.Topology.Nodes)
            {
                Node n = State.AddNodeWithId(src.Id, src.Type);
                if (n == null) continue;
                foreach (KeyValuePair<string, object> kv in src.Props) n.Props[kv.Key] = kv.Value;
            }
            // 试剂与检验栏
            if (plan.Feeds != null)
            {
                State.SolidReagent = plan.Feeds.Solid ?? "";
                State.LiquidReagent = plan.Feeds.Liquid ?? "";
                State.LiquidRoute = string.IsNullOrEmpty(plan.Feeds.LiquidRoute) ? "funnel" : plan.Feeds.LiquidRoute;
            }
            if (plan.Verification != null)
            {
                State.Fullness = plan.Verification.Fullness ?? "none";
                State.Identity = plan.Verification.Identity ?? "none";
            }
            // 收集方式：从 plan 的 water 边/节点朝向反推（CollectorMode 未写进 plan.json）
            State.CollectorMode = "upright";
            foreach (Edge e in plan.Topology.Edges)
                if (e.Kind == "water") { State.CollectorMode = "water"; break; }
            if (State.CollectorMode != "water")
                foreach (Node n in plan.Topology.Nodes)
                    if (n.Type == "collect.gas_jar" && n.Props.ContainsKey("orientation") &&
                        string.Equals(System.Convert.ToString(n.Props["orientation"]), "inverted", StringComparison.Ordinal))
                    { State.CollectorMode = "inverted"; break; }

            // 画布：按器材类型铺默认位置，与原方案的相对次序一致
            foreach (Node n in State.Nodes) SpawnDeviceFor(n, DefaultPos(n.Type));

            // 边：feed 边用 AssembleFunnel 重建装配态；其余气体/水路边直接回 State（导管由 RefreshConduitVisuals 成管）
            foreach (Edge e in plan.Topology.Edges)
            {
                if (e.Kind == "feed")
                {
                    string fid = Edge.NodeOf(e.From), flaskId = Edge.NodeOf(e.To);
                    if (State.FindNode(fid) != null && State.FindNode(flaskId) != null)
                        AssembleFunnel(fid, flaskId, "below");
                    continue;
                }
                State.AddEdgeWithProps(e.From, e.To, e.Kind, e.Props);
            }

            SetPending(null);
            _tasks.Clear();
            ResetCheckState();
            if (_btnAsk != null) _btnAsk.interactable = false;
            SetDiag("方案已恢复，请重新检查", "", "");
            RefreshAll();
            RefreshLines();
            SetCaption("已恢复上次提交的方案（器材/连线/试剂/检验项齐全），可直接检查并进入 3D");
            return true;
        }

        // ---------------------------------------------------------------- 画布：放置 / 拖动 / 连接点
        private Vector2 DefaultPos(string type)
        {
            int n = CountType(type);
            float x;
            if (type.StartsWith("reactor.")) x = 140;   // 左侧留得下固体试剂的引线标签
            else if (type.StartsWith("feed.")) x = 110;
            else if (type.StartsWith("conduit.")) x = 300;
            else if (type.StartsWith("collect.")) x = 470;
            else x = 40;
            return new Vector2(Mathf.Clamp(x + n * 34f, 8f, ContentW - ItemW - 8f),
                               type.StartsWith("feed.") ? 200f : RestY(type));
        }

        /// <summary>落在台面线上的高度：各元件轮廓底边留白不同，落点补齐才贴线。</summary>
        private static float RestY(string type)
        {
            if (type == "collect.gas_jar") return 14f;
            if (type == "collect.water_trough") return 34f;
            if (type == "support.stand") return 42f;
            return 40f;
        }

        private int CountType(string type)
        {
            int n = 0;
            foreach (Node node in State.Nodes) if (node.Type == type) n++;
            return n;
        }

        public bool IsInDeviceViewport(Vector2 screenPos)
        {
            if (_deviceScroll == null) return false;
            // 画布是 ScreenSpaceOverlay：屏幕坐标换算必须传 null（UiCamera 在 Overlay 下即返回 null）。
            // 用 Camera.main 会把屏幕点错误地投影到透视相机，判定区整体偏移，器材拖不进装置区。
            Camera cam = UiCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(_deviceScroll.viewport, screenPos, cam);
        }

        /// <summary>左侧拖入：屏幕坐标转画布坐标后放置。</summary>
        public void DropElement(string type, Vector2 screenPos)
        {
            Vector2 lp;
            // 与 IsInDeviceViewport / DeviceAtScreenPoint / PaletteDragSource.Position 统一：
            // Overlay 画布传 null；传 Camera.main 会让局部坐标整体偏移，容器落到画布外看不见。
            Camera cam = UiCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_content, screenPos, cam, out lp))
                lp = DefaultPos(type);
            lp.x = Mathf.Clamp(lp.x - ItemW / 2f, 8f, ContentW - ItemW - 8f);
            lp.y = Mathf.Clamp(lp.y - ItemH / 2f, RestY(type), ContentH - ItemH - 44f);
            SpawnDevice(type, lp);
        }

        /// <summary>创建一个器材实例并同步一个 DesignState 节点。</summary>
        private void SpawnDevice(string type, Vector2 pos)
        {
            // 导管不再是画布上的一个图标：点/拖"导管"卡＝进入导管连接模式，由两个端口自动成管
            if (type == "conduit.bent")
            {
                SetConduitMode(true);
                SetCaption("导管：点一个装置端口、再点另一个装置端口，就会用导管把两件装置连起来");
                return;
            }

            // 器材上限与判定层共用常量；关键元件各只能有一个（E-STR-11）
            if (State.Nodes.Count >= Validator.MaxNodes)
            {
                SetCaption("器材最多 " + Validator.MaxNodes + " 件：先删掉一件再放");
                return;
            }
            bool isReactorType = type == "reactor.flask_conical" || type == "reactor.test_tube";
            if (isReactorType &&
                CountType("reactor.flask_conical") + CountType("reactor.test_tube") > 0)
            {
                SetCaption("反应容器只能放一个（锥形瓶或试管二选一）");
                return;
            }
            if (type == "feed.thistle_funnel" && CountType(type) > 0)
            {
                SetCaption("长颈漏斗只能放一个");
                return;
            }
            if (type == "collect.gas_jar" && CountType(type) > 0)
            {
                SetCaption("集气瓶只能放一个");
                return;
            }
            if (type == "collect.water_trough" && CountType(type) > 0)
            {
                SetCaption("水槽只能放一个");
                return;
            }

            string spawnWarn = null;
            Node node = State.AddNode(type);
            if (node == null)
            {
                SetCaption(State.Nodes.Count >= Validator.MaxNodes
                    ? "器材最多 " + Validator.MaxNodes + " 件：先删掉一件再放"
                    : "未知器材：" + type);
                return;
            }
            // 玻璃片默认就在瓶口上（盖右留左缝），判定与画面同源
            if (type == "collect.gas_jar")
            {
                State.SetNodeProp(node.Id, "covered", true);
                // 排水法可能先于集气瓶选好：补建 mouth↔basin 边，保持"选择即接通"口径
                if (State.CollectorMode == "water")
                {
                    string werr = State.ConnectWater();
                    if (!string.IsNullOrEmpty(werr))
                        spawnWarn = "集气瓶已放好，但连线已满（" + Validator.MaxEdges + " 条），未能接入水槽";
                }
            }
            SpawnDeviceFor(node, pos);
            RefreshAll();
            if (spawnWarn != null) SetCaption(spawnWarn);
            // 用户描述的快捷流程：每个器材独立排队，支持任意放置顺序
            if (type == "feed.thistle_funnel")
            {
                _tasks.Add(new PendingTask { Kind = LinkTask.FunnelBottom, NodeId = node.Id });
                SetCaption("漏斗已放入：把它的〔下端〕橙点拖到锥形瓶的〔瓶内〕/〔液面上方〕/〔液面下方〕端口上松手，即可接入");
            }
        }

        private void SpawnDeviceFor(Node node, Vector2 pos)
        {
            GameObject go = new GameObject("Device_" + node.Id);
            go.transform.SetParent(_content, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(ItemW, ItemH);
            // 本体只承接射线（全透明），画面交给线描子物体：试卷式黑白线稿
            Image img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0f);
            StrokeGraphic art = MakeGlyph(go.transform, node.Type, Vector2.zero, 1f);
            // 漏斗接入后会整个盖在锥形瓶上：本体不参与射线检测，只留"储液泡"上一个抓取区。
            // 抓取区必须只覆盖储液泡上空着的部分（x 36..78 / y 88..118），不能用整块 60x56，
            // 否则会把锥形瓶的〔瓶口〕(84,113)、〔液面上方〕(60,78) 一起盖住——学生会觉得"端口被占用了"。
            if (node.Type == "feed.thistle_funnel")
            {
                img.raycastTarget = false;
                GameObject grip = new GameObject("Grip");
                grip.transform.SetParent(go.transform, false);
                RectTransform grt = grip.AddComponent<RectTransform>();
                grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.zero; grt.pivot = Vector2.zero;
                grt.anchoredPosition = new Vector2(36, 88); grt.sizeDelta = new Vector2(42, 30);
                Image gimg = grip.AddComponent<Image>();
                gimg.color = new Color(1f, 1f, 1f, 0f);
            }
            DeviceItem item = go.AddComponent<DeviceItem>();
            item.NodeId = node.Id; item.Owner = this;
            _items[node.Id] = item;
            _deviceArt[node.Id] = art;

            // 试卷式编号与试剂引线文字在 RefreshFill/RefreshLetters 里统一刷新
            Text name = MakeText(go.transform, "", new Vector2(-6, -24), new Vector2(ItemW + 12, 22),
                13, FontStyle.Bold, StrokeGraphic.Ink);
            name.alignment = TextAnchor.MiddleCenter;
            _nameLabels[node.Id] = name;
            if (node.Type == "feed.thistle_funnel") _funnelNames[node.Id] = name;

            // 试剂名引线文字（试卷口径：液体标在右、固体标在左），引线画在线稿里
            if (node.Type == "reactor.flask_conical" || node.Type == "reactor.test_tube")
            {
                Text ll = MakeText(go.transform, "", new Vector2(106, 60), new Vector2(120, 20),
                    13, FontStyle.Normal, StrokeGraphic.Ink);
                ll.alignment = TextAnchor.MiddleLeft;
                ll.gameObject.SetActive(false);
                _liquidLabels[node.Id] = ll;
                Text sl = MakeText(go.transform, "", new Vector2(-126, 6), new Vector2(120, 20),
                    13, FontStyle.Normal, StrokeGraphic.Ink);
                sl.alignment = TextAnchor.MiddleRight;
                sl.gameObject.SetActive(false);
                _solidLabels[node.Id] = sl;
            }

            // 长颈漏斗：顶部"已加入试剂"提示（试剂名 + 向下箭头），默认隐藏
            if (node.Type == "feed.thistle_funnel")
            {
                BuildFunnelBadge(node.Id, go.transform);
                // 接入状态：默认隐藏，接上容器后显示"✓ 已接入（液封）"
                // 接入状态：默认隐藏，接上容器后显示"✓ 已接入（液封）"。
                // 位置放在储液泡正上方——右上方那一片留给容器的〔瓶口〕〔液面上方〕标签。
                Text st = MakeText(go.transform, "", new Vector2(5, 132), new Vector2(110, 22),
                    12, FontStyle.Bold, new Color(0.10f, 0.48f, 0.26f));
                st.alignment = TextAnchor.MiddleCenter;
                st.gameObject.SetActive(false);
                _funnelStatus[node.Id] = st;
            }

            BuildDropHint(node.Id, go.transform);   // 选框在连接点之下，避免盖住橙色方块
            AddAnchors(node.Type, node.Id, go.transform);
        }

        /// <summary>长颈漏斗顶部的加料提示：试剂名 + 向下箭头，体现"实验开始从漏斗加入"。</summary>
        private void BuildFunnelBadge(string nodeId, Transform parent)
        {
            GameObject badge = new GameObject("FunnelFeedBadge");
            badge.transform.SetParent(parent, false);
            RectTransform rt = badge.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            // 放在漏斗正上方、横向居中：箭头正好指着储液泡的顶口，读起来就是"从这里倒进去"。
            // 图标 256x256 用 preserveAspect 塞进 120x140，实际绘制区域是 y 10..130，泡顶约 y=118。
            rt.anchoredPosition = new Vector2(ItemW * 0.5f - 55f, 118f);
            rt.sizeDelta = new Vector2(110, 36);

            Text name = MakeText(badge.transform, "", new Vector2(0, 20), new Vector2(110, 16),
                12, FontStyle.Bold, StrokeGraphic.Ink);
            name.alignment = TextAnchor.MiddleCenter;
            Text arrow = MakeText(badge.transform, "↓", new Vector2(0, 0), new Vector2(110, 20),
                18, FontStyle.Bold, StrokeGraphic.Ink);
            arrow.alignment = TextAnchor.MiddleCenter;

            badge.SetActive(false);
            _funnelBadges[nodeId] = badge;
        }

        /// <summary>线描视图工厂：画布器材 / 左栏卡 / 拖拽幽灵共用同一份几何，只换缩放。</summary>
        private StrokeGraphic MakeGlyph(Transform parent, string type, Vector2 pos, float scale)
        {
            GameObject go = new GameObject("Art_" + type);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(DeviceGlyph.W * scale, DeviceGlyph.H * scale);
            go.AddComponent<CanvasRenderer>();   // 运行时 AddComponent 不会自动补 RequireComponent 依赖
            StrokeGraphic g = go.AddComponent<StrokeGraphic>();
            g.Scale = scale;
            // 小图（左栏卡 scale=0.4 / 试剂 0.36 / 幽灵 0.5）缩后描线只剩 ~0.8px，需多补一档；
            // 装置区大图（scale=1）轻微加粗即可。填充不随之变化，塞子/颗粒不会糊。
            g.WidthBoost = scale <= 0.5f ? 1.85f : 1.32f;
            g.raycastTarget = false;
            DeviceGlyph.Draw(g, type, default(DeviceGlyph.State));
            return g;
        }

        /// <summary>为每个器材定义语义端口：port 走 ConnectionMatrix，spot 决定判定属性。
        /// labelDir 只用来决定端口名的挂靠方向（左/右），不再拉引线。</summary>
        private void AddAnchors(string type, string nodeId, Transform parent)
        {
            DeviceGlyph.State gs = default(DeviceGlyph.State);
            if (type == "reactor.flask_conical" || type == "reactor.test_tube")
            {
                // 瓶口：导管接这里。位置随装配态切换（见 RefreshPortAnchors）：
                // 空瓶时压在瓶口唇线上；插了漏斗以后瓶口被塞住，落到橡胶塞出气短管的口心上，避开漏斗抓取区。
                AddAnchor(type, nodeId, parent, "mouth", "mouth", "", "瓶口",
                    DeviceGlyph.Anchor(type, "mouth", gs), new Vector2(1, 0.35f));
                // 空容器：瓶内；已加液体：液面上方 / 液面下方（两组互斥显示）
                // 左右各一颗（key 带 _r 的是右侧镜像，语义与左侧完全相同）：
                // 漏斗从左边插进来后，右边那颗照样能接导管，不会和漏斗颈抢同一个点。
                AddAnchor(type, nodeId, parent, "inside", "mouth", "inside", "瓶内（不接触液体）",
                    DeviceGlyph.Anchor(type, "inside", gs), new Vector2(-1, 0.45f));
                AddAnchor(type, nodeId, parent, "above", "mouth", "above", "液面以上",
                    DeviceGlyph.Anchor(type, "above", gs), new Vector2(-1, 0.55f));
                AddAnchor(type, nodeId, parent, "below", "mouth", "below", "液面以下（液封）",
                    DeviceGlyph.Anchor(type, "below", gs), new Vector2(-1, -0.55f));

                AddAnchor(type, nodeId, parent, "inside_r", "mouth", "inside", "瓶内（不接触液体）",
                    DeviceGlyph.Anchor(type, "inside_r", gs), new Vector2(1, 0.45f));
                AddAnchor(type, nodeId, parent, "above_r", "mouth", "above", "液面以上",
                    DeviceGlyph.Anchor(type, "above_r", gs), new Vector2(1, 0.05f));
                AddAnchor(type, nodeId, parent, "below_r", "mouth", "below", "液面以下（液封）",
                    DeviceGlyph.Anchor(type, "below_r", gs), new Vector2(1, -0.55f));
            }
            else if (type == "feed.thistle_funnel")
            {
                // 只保留下端：接入方式是把下端拖到容器端口上（不再用"上端"连接点，减少干扰）
                AddAnchor(type, nodeId, parent, "bottom", "feed", "", "下端",
                    DeviceGlyph.Anchor(type, "bottom", gs), new Vector2(1, -0.55f));
            }
            else if (type == "collect.gas_jar")
            {
                // 连接点与线稿几何同源：正放/倒置时由 RefreshPortAnchors 重新对位
                AddAnchor(type, nodeId, parent, "mouth", "mouth", "", "瓶口",
                    DeviceGlyph.Anchor(type, "mouth", gs), new Vector2(-1, 0.3f));
                AddAnchor(type, nodeId, parent, "bottom", "mouth", "bottom", "瓶底（伸到底）",
                    DeviceGlyph.Anchor(type, "bottom", gs), new Vector2(-1, -0.25f));
                AddAnchor(type, nodeId, parent, "cover", "none", "", "玻璃片",
                    DeviceGlyph.Anchor(type, "cover", gs), new Vector2(1, 0.3f));
            }
            else if (type == "collect.water_trough")
            {
                AddAnchor(type, nodeId, parent, "basin", "water", "", "水槽",
                    DeviceGlyph.Anchor(type, "basin", gs), new Vector2(1, 0.2f));
            }
            else if (type == "support.stand")
            {
                AddAnchor(type, nodeId, parent, "clamp", "support", "", "夹",
                    DeviceGlyph.Anchor(type, "clamp", gs), new Vector2(1, 0.2f));
            }
        }

        /// <summary>端口 = 透明热区 + 空心细环 + 只在指着时出现的名字。
        /// 不画引线、不常显文字：默认整组收起，画布回到课本线稿的干净度。</summary>
        private void AddAnchor(string type, string nodeId, Transform parent, string key, string port,
            string spot, string label, Vector2 local, Vector2 labelDir)
        {
            GameObject go = new GameObject("Port_" + key);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = local; rt.sizeDelta = new Vector2(20, 20);   // 热区 20，视觉环 14

            // 热区：完全透明但参与射线检测，指针进出与点击都落在这里
            Image hit = go.AddComponent<Image>();
            hit.color = new Color(1f, 1f, 1f, 0f);

            GameObject ringGo = new GameObject("Ring");
            ringGo.transform.SetParent(go.transform, false);
            RectTransform rrt = ringGo.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0.5f, 0.5f); rrt.anchorMax = new Vector2(0.5f, 0.5f);
            rrt.pivot = new Vector2(0.5f, 0.5f); rrt.anchoredPosition = Vector2.zero;
            rrt.sizeDelta = new Vector2(16, 16);
            Image ring = ringGo.AddComponent<Image>();
            ring.sprite = RingSprite(); ring.color = C_PortIdle; ring.raycastTarget = false;

            AnchorPoint ap = go.AddComponent<AnchorPoint>();
            ap.NodeId = nodeId; ap.Key = key; ap.Owner = this;

            // 名字：只在指着这颗端口时出现。白底小标签比纯文字好读——12px 细字压在墨线上会糊。
            bool left = labelDir.x < 0;
            float tw = Mathf.Max(50f, label.Length * 13f + 14f);
            GameObject tag = new GameObject("Tag");
            tag.transform.SetParent(go.transform, false);
            RectTransform grt = tag.AddComponent<RectTransform>();
            grt.anchorMin = new Vector2(0.5f, 0.5f); grt.anchorMax = new Vector2(0.5f, 0.5f);
            grt.pivot = left ? new Vector2(1f, 0.5f) : new Vector2(0f, 0.5f);
            grt.anchoredPosition = new Vector2(left ? -13f : 13f, 0f);
            grt.sizeDelta = new Vector2(tw, 20f);
            Image bg = tag.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.95f);
            bg.raycastTarget = false;

            Text t = MakeText(tag.transform, label, Vector2.zero, new Vector2(tw - 10f, 20f),
                13, FontStyle.Normal, C_PortLabel);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(5f, 0f); t.rectTransform.offsetMax = new Vector2(-5f, 0f);
            t.alignment = left ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            tag.SetActive(false);

            Node node = State.FindNode(nodeId);
            string portType = node == null ? "" : ElementCatalog.PortTypeOf(node.Type, PortNameFor(node.Type, key));
            PortVis pv = new PortVis
            {
                NodeId = nodeId, Key = key, Base = BaseKey(key),
                Mirrored = key.EndsWith("_r", StringComparison.Ordinal),
                Connectable = portType == "mouth" || portType == "water" || portType == "gas" ||
                              portType == "feed" || portType == "tube",
                Root = go, Ring = ring, Label = t, Tag = tag
            };
            _ports[nodeId + "|" + key] = pv;
            List<PortVis> lst;
            if (!_portsOfNode.TryGetValue(nodeId, out lst)) { lst = new List<PortVis>(); _portsOfNode[nodeId] = lst; }
            lst.Add(pv);
            go.SetActive(false);   // 默认收起：没有指针停在器材上时，一个端口都不占画面
        }

        // ---------------------------------------------------------------- 端口显隐与配色

        private static Sprite _ringSprite;

        /// <summary>空心细环（128²，带 mipmap）：白环 + 透明孔，Image.color 直接当环色。
        /// 用代码生成而不是加一张 PNG：不必重跑资源导入，也不会因资源缺失渲染成白方块。</summary>
        private static Sprite RingSprite()
        {
            if (_ringSprite != null) return _ringSprite;
            const int S = 128;
            Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            tex.name = "PortRing";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            float c = (S - 1) * 0.5f;
            float outer = S * 0.5f - 1.5f;        // 外半径 61
            float inner = outer - S * 0.115f;     // 环宽 ≈15px → 显示成 14px 时约 1.6px 的线
            Color32[] px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - c, dy = y - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - inner + 0.5f);
                    px[y * S + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            _ringSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
            _ringSprite.name = "PortRing";
            return _ringSprite;
        }

        /// <summary>端口总闸。只在四种时刻出现：指针停在器材上 / 点中的器材（触屏没有 hover）/
        /// 导管连接模式 / 已选了一个端点正等第二个。其余时候画布上一个端口都没有。</summary>
        private void ApplyPortVisibility()
        {
            bool connecting = !_deleteMode && (_conduitMode || _pending != null || _conduitPending != null);
            foreach (KeyValuePair<string, PortVis> kv in _ports)
            {
                PortVis p = kv.Value;
                if (p.Root == null) continue;

                bool nodeLit = p.NodeId == _hoverDeviceId || p.NodeId == _activeDeviceId;
                bool faint = !p.Mirrored && (p.Connectable || p.Base == "cover");
                bool show = !_deleteMode && !p.LogicHidden && (nodeLit || connecting || faint);
                p.Root.SetActive(show);
                if (!show) continue;

                // 右侧镜像平时不画环：它的存在意义只是"左列被漏斗颈挡住时还能接"，
                // 画出来就会变成同一只瓶上两个一模一样的〔瓶内〕。插了漏斗才轮到它露脸。
                // 锥形瓶〔瓶口〕环一律不画：塞子盖不住环边会露一圈，索性全藏；命中区与hover名字照旧。
                Node pn = State.FindNode(p.NodeId);
                bool stopperMouth = pn != null && pn.Type == "reactor.flask_conical" && p.Base == "mouth";
                bool drawRing = !(p.Mirrored && !_funnelOfFlask.ContainsKey(p.NodeId)) && !stopperMouth;
                Color c = PortColor(p);
                // 常态淡显（0.4），指针指着/连接模式才升到全亮；端口不再完全隐藏
                float alpha = (nodeLit || connecting) ? 1f : 0.4f;
                if (p.Ring != null) { c.a = drawRing ? alpha : 0f; p.Ring.color = c; }
                if (p.Tag != null) p.Tag.SetActive(_hoverPortKey == p.NodeId + "|" + p.Key);
            }
        }

        private Color PortColor(PortVis p)
        {
            string id = p.NodeId + "|" + p.Key;
            if (_pending != null && _pending.NodeId == p.NodeId && _pending.Key == p.Key) return C_PortSel;
            if (_conduitPending != null && _conduitPending.NodeId == p.NodeId && _conduitPending.Key == p.Key) return C_PortSel;
            if (_hoverPortKey == id) return C_PortHot;
            Node n = State.FindNode(p.NodeId);
            if (n != null && p.Key == "cover" && n.PropBool("covered", true)) return C_PortOn;
            if (_conduitMode && p.Connectable) return C_ConduitPort;
            return C_PortIdle;
        }

        /// <summary>指针进入/离开某件器材 → 显示/收起这件器材的端口。</summary>
        public void OnDeviceHover(string nodeId, bool on)
        {
            if (on)
            {
                if (_hoverDeviceId == nodeId) return;
                _hoverDeviceId = nodeId;
            }
            else
            {
                if (_hoverDeviceId != nodeId) return;
                _hoverDeviceId = null; _hoverPortKey = null;
            }
            ApplyPortVisibility();
        }

        /// <summary>指针进入/离开某颗端口 → 只让这一颗出名字。</summary>
        public void OnPortHover(string nodeId, string key, bool on)
        {
            string id = nodeId + "|" + key;
            if (on) { if (_hoverPortKey == id) return; _hoverPortKey = id; }
            else { if (_hoverPortKey != id) return; _hoverPortKey = null; }
            ApplyPortVisibility();
        }

        /// <summary>点中一件器材（非删除态）：触屏没有 hover，用"点亮"来出端口；同一时刻只点亮一件。</summary>
        public void SetActiveDevice(string nodeId)
        {
            string id = string.IsNullOrEmpty(nodeId) ? null : nodeId;
            if (_activeDeviceId == id && _hoverDeviceId == null) return;
            _activeDeviceId = id;
            ApplyPortVisibility();
        }

        public Vector2 ClampDevice(Vector2 p)
        {
            p.x = Mathf.Clamp(p.x, 4f, ContentW - ItemW - 4f);
            p.y = Mathf.Clamp(p.y, 40f, ContentH - ItemH - 44f);
            return p;
        }

        /// <summary>按器材类型 clamp：集气瓶允许落到台面线高度（正放/倒置都贴线）。</summary>
        public Vector2 ClampDeviceFor(string nodeId, Vector2 p)
        {
            Node n = State.FindNode(nodeId);
            p.x = Mathf.Clamp(p.x, 4f, ContentW - ItemW - 4f);
            p.y = Mathf.Clamp(p.y, n != null ? RestY(n.Type) : 40f, ContentH - ItemH - 44f);
            return p;
        }

        public void OnDeviceMoved(string nodeId) { RefreshLines(); }

        // ---------------------------------------------------------------- 漏斗接入容器（固定装配态）

        public bool IsFlaskAssembled(string flaskId) { return _funnelOfFlask.ContainsKey(flaskId); }
        public bool IsFunnelAssembled(string funnelId) { return _flaskOfFunnel.ContainsKey(funnelId); }

        /// <summary>每种端口对应的固定偏移：漏斗下端的落点正好是该端口的位置。
        /// below = 长颈伸到液面下（液封）；above = 漏斗被提起来，下端在液面上方（未液封）；
        /// inside = 瓶内没有液体时，下端停在瓶身里。</summary>
        private static Vector2 OffsetForSpot(string spot)
        {
            // 漏斗下端口在图标里的位置是 (60,14)，容器端口在容器坐标系里的位置见 AddAnchors
            if (spot == "above") return new Vector2(0f, 78f - 14f);
            if (spot == "inside") return new Vector2(0f, 55f - 14f);
            return new Vector2(0f, 44f - 14f);
        }

        private Vector2 OffsetOfFunnel(string funnelId)
        {
            string spot;
            return _asmSpot.TryGetValue(funnelId, out spot) ? OffsetForSpot(spot) : OffsetForSpot("below");
        }

        private AnchorPoint FindAnchorPoint(string nodeId, string key)
        {
            DeviceItem item;
            if (!_items.TryGetValue(nodeId, out item) || item == null) return null;
            AnchorPoint[] aps = item.GetComponentsInChildren<AnchorPoint>(true);
            foreach (AnchorPoint ap in aps)
                if (ap.NodeId == nodeId && ap.Key == key) return ap;
            return null;
        }

        /// <summary>漏斗下端的画布坐标。</summary>
        private Vector2 FunnelTipPos(string funnelId)
        {
            DeviceItem item;
            if (!_items.TryGetValue(funnelId, out item) || item == null) return Vector2.zero;
            return ((RectTransform)item.transform).anchoredPosition + new Vector2(60f, 14f);
        }

        /// <summary>拖动漏斗时找最近的容器端口（只考虑当前可见的端口）。</summary>
        private bool FindSnapTarget(string funnelId, out string flaskId, out string key)
        {
            flaskId = null; key = null;
            Vector2 tip = FunnelTipPos(funnelId);
            float best = SnapRadius;
            foreach (KeyValuePair<string, DeviceItem> kv in _items)
            {
                if (kv.Value == null) continue;
                Node n = State.FindNode(kv.Key);
                if (n == null) continue;
                if (n.Type != "reactor.flask_conical" && n.Type != "reactor.test_tube") continue;
                if (_funnelOfFlask.ContainsKey(kv.Key)) continue;      // 该容器已经有漏斗了
                Vector2 basePos = ((RectTransform)kv.Value.transform).anchoredPosition;
                string[] keys = { "below", "above", "inside", "below_r", "above_r", "inside_r" };
                foreach (string k in keys)
                {
                    // 端口默认是收起的（activeInHierarchy=false），不能再拿"有没有显示"当可用判据；
                    // 改用语义上的 LogicHidden（空瓶的液面点、已经被占掉的点）。
                    PortVis pv;
                    if (_ports.TryGetValue(kv.Key + "|" + k, out pv) && pv.LogicHidden) continue;
                    AnchorPoint ap = FindAnchorPoint(kv.Key, k);
                    if (ap == null) continue;
                    Vector2 p = basePos + ((RectTransform)ap.transform).anchoredPosition;
                    float d = Vector2.Distance(tip, p);
                    if (d < best) { best = d; flaskId = kv.Key; key = BaseKey(k); }
                }
            }
            return flaskId != null;
        }

        /// <summary>拖动漏斗中：靠近容器端口时把容器框亮绿、并把那个容器的端口点亮（告诉学生能吸到哪）。</summary>
        public void OnFunnelDragHover(string funnelId)
        {
            string fid, key;
            bool hit = FindSnapTarget(funnelId, out fid, out key);
            if (fid == _snapFlaskId && key == _snapKey) return;
            if (_snapFlaskId != null) ShowDropHint(_snapFlaskId, 0);
            _snapFlaskId = hit ? fid : null;
            _snapKey = hit ? key : null;
            _hoverDeviceId = hit ? fid : null;
            ApplyPortVisibility();
            if (!hit) return;
            ShowDropHint(fid, 1);
            SetCaption("松手把长颈漏斗接入" + ElementCatalog.NameOf(State.FindNode(fid).Type) + "：" + SpotHint(key));
        }

        private static string SpotHint(string key)
        {
            if (key == "below") return "下端在〔液面下方〕→ 液封 ✓";
            if (key == "above") return "下端在〔液面上方〕→ 未液封 ✗";
            return "下端在〔瓶内〕（瓶内还没有液体）";
        }

        /// <summary>把漏斗接进容器：写 feed 边 + submerged，锁定固定装配位置，叠上橡胶塞。</summary>
        public string AssembleFunnel(string funnelId, string flaskId, string key)
        {
            Node funnel = State.FindNode(funnelId);
            Node flask = State.FindNode(flaskId);
            if (funnel == null || funnel.Type != "feed.thistle_funnel") return "not funnel";
            if (flask == null) return "no flask";

            if (_flaskOfFunnel.ContainsKey(funnelId)) DetachFunnel(funnelId, false);
            RemoveFeedEdges(funnelId);

            string err = State.Connect(funnelId + ".bottom", flaskId + ".mouth", "feed");
            if (!string.IsNullOrEmpty(err)) return err;

            // 漏斗里先倒好的酸：一接上就顺着管子流进瓶里（漏斗倒空）
            string held;
            _funnelReagent.TryGetValue(funnelId, out held);

            // 瓶里（马上）会有液面时，插到"瓶内（不接触液体）"其实就是插到液面下 → 按液封处理，别记成"未液封"
            bool willHaveLiquid = !string.IsNullOrEmpty(State.LiquidReagent) || !string.IsNullOrEmpty(held);
            string spot = key == "inside" && willHaveLiquid ? "below" : key;

            State.SetNodeProp(funnelId, "submerged", spot == "below");
            _funnelOfFlask[flaskId] = funnelId;
            _flaskOfFunnel[funnelId] = flaskId;
            _asmSpot[funnelId] = spot;
            _tasks.RemoveAll(t => t.Kind == LinkTask.FunnelBottom && t.NodeId == funnelId);

            LockAssembly(funnelId, flaskId);

            if (!string.IsNullOrEmpty(held))
            {
                State.LiquidReagent = held;
                State.LiquidRoute = "funnel";
                _funnelReagent.Remove(funnelId);
                State.SetNodeProp(funnelId, "reagent", "");
            }

            RefreshAll();
            RefreshLines();
            SetCaption("长颈漏斗已接入" + ElementCatalog.NameOf(flask.Type) + "：" +
                (spot == "below" ? (key == "inside"
                        ? "瓶内有液体，下端浸在液面下，形成液封 ✓"
                        : "下端在液面下方，形成液封 ✓")
                 : spot == "above" ? "下端在液面上方，未液封 ✗（提交会提示这个问题）"
                 : "下端在瓶内（瓶内还没有液体，谈不上液封）"));
            return "";
        }

        /// <summary>瓶里刚出现液面：原本插在"瓶内（不接触液体）"的漏斗，下端此刻已被液面淹住 → 自动升级成"液面以下（液封）"（液封）。</summary>
        private bool SealFunnelOfFlask(string flaskId)
        {
            string funnelId, spot;
            if (!_funnelOfFlask.TryGetValue(flaskId, out funnelId)) return false;
            if (!_asmSpot.TryGetValue(funnelId, out spot) || spot != "inside") return false;

            State.SetNodeProp(funnelId, "submerged", true);
            _asmSpot[funnelId] = "below";
            LockAssembly(funnelId, flaskId);
            return true;
        }

        /// <summary>锁定装配位置：漏斗移到容器 + 固定偏移，并生成橡胶塞。</summary>
        private void LockAssembly(string funnelId, string flaskId)
        {
            DeviceItem fi, bi;
            if (!_items.TryGetValue(funnelId, out fi) || !_items.TryGetValue(flaskId, out bi)) return;
            if (fi == null || bi == null) return;
            RectTransform frt = (RectTransform)fi.transform;
            RectTransform brt = (RectTransform)bi.transform;
            frt.anchoredPosition = brt.anchoredPosition + OffsetOfFunnel(funnelId);
            frt.SetAsLastSibling();     // 漏斗茎在瓶身之前，遮住瓶口处的线条

            // 橡胶塞由 RefreshStoppers 统一画（黑色实心 + 穿颈通道 + 出气短管）

            // 状态文字：让学生一眼看到"接上了"
            Text stt;
            if (_funnelStatus.TryGetValue(funnelId, out stt) && stt != null)
            {
                stt.gameObject.SetActive(true);
                stt.text = _asmSpot[funnelId] == "below" ? "✓ 已接入·液封"
                    : _asmSpot[funnelId] == "above" ? "✓ 已接入·未液封" : "✓ 已接入·瓶内";
                stt.color = _asmSpot[funnelId] == "below"
                    ? new Color(0.10f, 0.48f, 0.26f) : new Color(0.78f, 0.32f, 0.12f);
            }

            // 下端口已经用掉了：显隐由 RefreshPortAnchors 按 _flaskOfFunnel 统一算，这里不再单独收起
        }

        /// <summary>拆除：删掉 feed 边、清 submerged、隐藏橡胶塞。</summary>
        public void DetachFunnel(string funnelId, bool notify = true)
        {
            string flaskId;
            if (!_flaskOfFunnel.TryGetValue(funnelId, out flaskId)) return;

            RemoveFeedEdges(funnelId);
            State.SetNodeProp(funnelId, "submerged", false);
            _flaskOfFunnel.Remove(funnelId);
            _funnelOfFlask.Remove(flaskId);
            _asmSpot.Remove(funnelId);

            Text stt;
            if (_funnelStatus.TryGetValue(funnelId, out stt) && stt != null) stt.gameObject.SetActive(false);

            RefreshPortAnchors();
            RefreshLines();
            if (notify)
                SetCaption("已把长颈漏斗从容器上拔下：把下端橙点拖到〔瓶内〕/〔液面上方〕/〔液面下方〕上松手即可重新接入");
        }

        private void RemoveFeedEdges(string funnelId)
        {
            for (int i = State.Edges.Count - 1; i >= 0; i--)
            {
                Edge e = State.Edges[i];
                if (Edge.NodeOf(e.From) == funnelId && Edge.PortOf(e.From) == "bottom")
                    State.Edges.RemoveAt(i);
            }
        }

        /// <summary>容器被拖动时，接入的漏斗与橡胶塞跟着走（整机一起移动）。</summary>
        private void FollowAssembly(string flaskId)
        {
            string funnelId;
            if (!_funnelOfFlask.TryGetValue(flaskId, out funnelId)) return;
            DeviceItem fi, bi;
            if (!_items.TryGetValue(funnelId, out fi) || !_items.TryGetValue(flaskId, out bi)) return;
            if (fi == null || bi == null) return;
            RectTransform frt = (RectTransform)fi.transform;
            RectTransform brt = (RectTransform)bi.transform;

            Vector2 fp = brt.anchoredPosition + OffsetOfFunnel(funnelId);
            float over = fp.y - (ContentH - ItemH - 4f);
            if (over > 0f)
            {
                fp.y -= over;
                brt.anchoredPosition = new Vector2(brt.anchoredPosition.x, brt.anchoredPosition.y - over);
            }
            frt.anchoredPosition = fp;
            frt.SetAsLastSibling();
        }

        /// <summary>器材开始拖动（漏斗一动＝从容器上拔下来）。拖动中不显示端口，免得一圈环跟着飘。</summary>
        public void OnDeviceDragBegin(string nodeId)
        {
            if (IsFunnelAssembled(nodeId)) DetachFunnel(nodeId);
            _hoverDeviceId = null; _hoverPortKey = null; _activeDeviceId = null;
            ApplyPortVisibility();
        }

        /// <summary>器材拖动中：整机跟随 + 漏斗吸附预览。</summary>
        public void OnDeviceDragged(string nodeId)
        {
            Node n = State.FindNode(nodeId);
            if (n == null) return;
            if (n.Type == "feed.thistle_funnel") OnFunnelDragHover(nodeId);
            else if (IsFlaskAssembled(nodeId)) FollowAssembly(nodeId);
            RefreshLines();
        }

        /// <summary>器材松手：漏斗落在端口上就接入。</summary>
        public void OnDeviceDragEnd(string nodeId)
        {
            if (_snapFlaskId != null) ShowDropHint(_snapFlaskId, 0);
            Node n = State.FindNode(nodeId);
            if (n != null && n.Type == "feed.thistle_funnel")
            {
                string flaskId, key;
                if (FindSnapTarget(nodeId, out flaskId, out key))
                    AssembleFunnel(nodeId, flaskId, key);
                else
                    SetCaption("长颈漏斗还没接入：把它的〔下端〕橙点拖到锥形瓶的〔瓶内〕/〔液面上方〕/〔液面下方〕端口上松手");
            }
            _snapFlaskId = null; _snapKey = null;
            // 松手后把指针当成还停在这件器材上：端口立刻回来，不用再挪出去重进
            _hoverDeviceId = null;
            if (!_deleteMode) _activeDeviceId = nodeId;
            RefreshPortAnchors();
            RefreshLines();
        }

        private AnchorRef FindAnchor(string nodeId, string key)
        {
            DeviceItem item;
            if (!_items.TryGetValue(nodeId, out item)) return null;
            AnchorPoint[] aps = item.GetComponentsInChildren<AnchorPoint>(true);
            foreach (AnchorPoint ap in aps)
                if (ap.NodeId == nodeId && ap.Key == key)
                {
                    Node node = State.FindNode(nodeId);
                    string portType = node == null ? "" : ElementCatalog.PortTypeOf(node.Type,
                        PortNameFor(node.Type, key));
                    RectTransform rt = ap.GetComponent<RectTransform>();
                    PortVis pv;
                    _ports.TryGetValue(nodeId + "|" + key, out pv);
                    return new AnchorRef
                    {
                        NodeId = nodeId, Key = key, Port = PortNameFor(node.Type, key),
                        Spot = SpotOf(key), PortType = portType, Local = rt.anchoredPosition,
                        Go = pv == null ? null : pv.Ring, BaseColor = C_PortIdle
                    };
                }
            return null;
        }

        /// <summary>左右成对端口的 key 归一：above_r / below_r / inside_r 都是右侧那一颗，
        /// 语义（spot / 端口名 / 标签）与左侧的 above / below / inside 完全一致，只是位置镜像。</summary>
        private static string BaseKey(string key)
        {
            if (key != null && key.EndsWith("_r", StringComparison.Ordinal))
                return key.Substring(0, key.Length - 2);
            return key;
        }

        private static string PortNameFor(string type, string key)
        {
            // 瓶内 / 液面各点只是"瓶口"的不同高度，端口名仍是 mouth；漏斗下端是 feed 端口
            string b = BaseKey(key);
            if (b == "above" || b == "below" || b == "inside") return "mouth";
            if (type == "feed.thistle_funnel") return key == "top" ? "top" : "bottom";
            if (type == "collect.gas_jar" && key == "bottom") return "mouth";
            return key;
        }

        private static string SpotOf(string key)
        {
            string b = BaseKey(key);
            if (b == "above" || b == "below" || b == "inside") return b;
            if (b == "bottom") return "bottom";
            return "";
        }

        private void ArmAnchor(string nodeId, string key)
        {
            AnchorRef a = FindAnchor(nodeId, key);
            SetPending(a);
            if (a != null) SetCaption("已选起点连接点〔" + LabelOf(key) + "〕，请点击目标器材上的连接点完成连接");
        }

        private static string LabelOf(string key)
        {
            switch (BaseKey(key))
            {
                case "in": return "导管入口";
                case "out": return "导管出口";
                case "bottom": return "下端/瓶底";
                case "tube": return "导管口";
                case "above": return "液面以上";
                case "below": return "液面以下（液封）";
                case "inside": return "瓶内（不接触液体）";
                default: return "瓶口";
            }
        }

        private void SetPending(AnchorRef a)
        {
            _pending = a;
            ApplyPortVisibility();   // 选中色统一由 PortColor 决定，不再手动改色
        }

        /// <summary>点击连接点：导管模式 → 导管两点连接；否则优先走快捷任务（导管/漏斗），最后走通用两点连线。</summary>
        public void OnAnchorClicked(string nodeId, string key)
        {
            AnchorRef cur = FindAnchor(nodeId, key);
            if (cur == null) return;

            // 删除器材模式：点器材上的任何东西（含端口橙点）都算"点这件器材"
            if (_deleteMode) { DeleteDevice(nodeId); return; }

            // 玻璃片开关：独立于任何模式/连线任务，点击即切换 covered
            if (key == "cover")
            {
                Node jar = State.FindNode(nodeId);
                if (jar != null && jar.Type == "collect.gas_jar")
                {
                    bool now = !jar.PropBool("covered", true);
                    State.SetNodeProp(nodeId, "covered", now);
                    RefreshAll();   // 玻璃片是线稿的一部分：改属性必须重绘，否则画面停在旧态
                    ApplyPortVisibility();   // 已盖上＝绿环（PortColor 读 covered）
                    SetCaption(now ? "集气瓶已盖玻璃片 ✓" : "集气瓶未盖玻璃片 ✗（提交会提示这个问题）");
                }
                return;
            }

            // 导管连接模式：两个端口 → 一根导管（容器上的液面/瓶内端口照样可以点，它们不被漏斗"占用"）
            if (_conduitMode) { OnConduitPortClicked(cur); return; }

            // 已接入的漏斗：点容器的〔瓶内〕/〔液面上方〕/〔液面下方〕＝改插入深度（重新判定液封）
            if ((BaseKey(key) == "above" || BaseKey(key) == "below" || BaseKey(key) == "inside") && IsFlaskAssembled(nodeId))
            {
                AssembleFunnel(_funnelOfFlask[nodeId], nodeId, BaseKey(key));
                return;
            }

            if (_tasks.Count > 0)
            {
                HandleTask(cur);
                return;
            }

            if (_pending == null || _pending.NodeId == nodeId && _pending.Key == key)
            {
                SetPending(cur);
                SetCaption("已选〔" + LabelOf(key) + "〕，再点另一个连接点连线");
                return;
            }
            TryConnect(_pending, cur);
        }

        /// <summary>快捷任务：按点击的连接点语义，匹配并消费对应的待连任务。</summary>
        private void HandleTask(AnchorRef cur)
        {
            Node clicked = State.FindNode(cur.NodeId);
            if (clicked == null) return;
            bool reactor = clicked.Type == "reactor.flask_conical" || clicked.Type == "reactor.test_tube";
            bool jar = clicked.Type == "collect.gas_jar" || clicked.Type == "collect.water_trough";

            // 集气瓶玻璃片连接点：任何任务状态下都允许切换
            if (cur.Key == "cover") { OnAnchorClicked(cur.NodeId, cur.Key); return; }

            // 漏斗：点在容器「瓶内 / 液面上方 / 液面下方」连接点 → 接入（feed 边 + submerged + 固定装配）
            if (cur.Spot == "below" || cur.Spot == "above" || cur.Spot == "inside")
            {
                PendingTask ft = _tasks.Find(t => t.Kind == LinkTask.FunnelBottom);
                if (ft != null && reactor)
                {
                    string err = AssembleFunnel(ft.NodeId, cur.NodeId, cur.Spot);
                    if (!string.IsNullOrEmpty(err)) { SetCaption("连接失败：" + Codes.Message(err)); return; }
                    return;
                }
                // 导管入口也可点在容器瓶身（按瓶口端口处理）
                PendingTask ci = _tasks.Find(t => t.Kind == LinkTask.ConduitIn);
                if (ci != null && reactor)
                {
                    ConnectConduitIn(ci, cur);
                    return;
                }
            }

            // 导管入口：点容器瓶口
            if (reactor)
            {
                PendingTask ci = _tasks.Find(t => t.Kind == LinkTask.ConduitIn);
                if (ci != null) { ConnectConduitIn(ci, cur); return; }
            }

            // 导管出口：点集气瓶/水槽
            if (jar)
            {
                PendingTask co = _tasks.Find(t => t.Kind == LinkTask.ConduitOut);
                if (co != null)
                {
                    string toPort = clicked.Type == "collect.water_trough" ? "basin" : "mouth";
                    string from = co.NodeId + ".out", to = cur.NodeId + "." + toPort;
                    string err = State.Connect(from, to, "gas");
                    if (!string.IsNullOrEmpty(err)) { SetCaption("连接失败：" + Codes.Message(err)); return; }
                    if (clicked.Type == "collect.gas_jar")
                        State.SetEdgeProp(from, to, "depth", cur.Spot == "bottom" ? "deep" : "shallow");
                    _tasks.Remove(co);
                    RefreshLines();
                    SetCaption(cur.Spot == "bottom"
                        ? "导管伸到集气瓶底 ✓ 深度足够"
                        : "导管只到瓶口（浅）— 伸到底部应选〔瓶底(深)〕连接点");
                    return;
                }
            }

            SetCaption("当前操作与待连步骤不匹配：请看画布提示选择正确连接点");
        }

        private void ConnectConduitIn(PendingTask ci, AnchorRef cur)
        {
            string err = State.Connect(ci.NodeId + ".in", cur.NodeId + ".mouth", "gas");
            if (!string.IsNullOrEmpty(err)) { SetCaption("连接失败：" + Codes.Message(err)); return; }
            _tasks.Remove(ci);
            _tasks.Add(new PendingTask { Kind = LinkTask.ConduitOut, NodeId = ci.NodeId });
            RefreshLines();
            SetCaption("导管入口已接好 ✓ 现在点击集气瓶的〔瓶口〕或〔瓶底(深)〕连接点接出口");
        }

        private void TryConnect(AnchorRef a, AnchorRef b)
        {
            SetPending(null);
            if (!ConnectionMatrix.Compatible(a.PortType, b.PortType))
            {
                SetCaption("端口不匹配：" + a.PortType + " ↔ " + b.PortType + "（连接矩阵拒绝）");
                return;
            }

            // 统一方向：气体/加料/液体边以功能端口为 from，容器瓶口为 to
            AnchorRef func = a, mouth = b;
            if (a.PortType != "mouth" && b.PortType == "mouth") { func = a; mouth = b; }
            else if (b.PortType != "mouth" && a.PortType == "mouth") { func = b; mouth = a; }
            else if (a.PortType == "gas" || a.PortType == "feed" || a.PortType == "water") { func = a; mouth = b; }
            else { func = b; mouth = a; }

            string kind = func.PortType == "feed" ? "feed" : func.PortType == "water" ? "water" : "gas";
            string from = func.NodeId + "." + func.Port;
            string to = mouth.NodeId + "." + mouth.Port;
            string err = State.Connect(from, to, kind);
            if (!string.IsNullOrEmpty(err))
            {
                SetCaption("连线失败：" + Codes.Message(err));
                return;
            }

            // 连接点位置 → 判定属性
            Node funcNode = State.FindNode(func.NodeId);
            if (funcNode != null && funcNode.Type == "feed.thistle_funnel")
                State.SetNodeProp(funcNode.Id, "submerged", mouth.Spot == "below");
            if (funcNode != null && funcNode.Type == "conduit.bent" && kind == "gas")
            {
                Node target = State.FindNode(mouth.NodeId);
                if (target != null && target.Type == "collect.gas_jar")
                    State.SetEdgeProp(from, to, "depth", mouth.Spot == "bottom" ? "deep" : "shallow");
            }

            RefreshLines();
            SetCaption("连线成功：" + ElementCatalog.NameOf(funcNode == null ? "" : funcNode.Type) +
                       " → " + ElementCatalog.NameOf(State.FindNode(mouth.NodeId).Type) +
                       (kind == "gas" ? "（气路）" : kind == "feed" ? "（加料）" : "（水）"));

            // 快捷流程：导管入口接好后自动引导接出口
            if (funcNode != null && funcNode.Type == "conduit.bent" && func.Key == "in")
                ArmAnchor(funcNode.Id, "out");
        }

        // ---------------------------------------------------------------- 导管连接模式

        private void OnToggleConduitMode() { SetConduitMode(!_conduitMode); }

        public void SetConduitMode(bool on)
        {
            bool changed = _conduitMode != on;
            _conduitMode = on;
            if (!on) _conduitPending = null;
            // 与删除器材互斥
            if (on && _deleteMode) SetDeleteMode(false);
            if (_btnConduitBg != null) _btnConduitBg.color = on ? C_BtnOn : new Color(0.86f, 0.9f, 0.94f);
            if (_btnConduitLabel != null)
            {
                _btnConduitLabel.text = on ? "导管连接中…" : "导管连接";
                _btnConduitLabel.color = on ? Color.white : new Color(0.1f, 0.12f, 0.16f);
            }
            RefreshConduitPortTint();
            if (!changed) return;
            SetCaption(on
                ? "导管连接：先点一个端口（锥形瓶〔瓶口〕/〔液面上方〕/〔液面下方〕），再点另一个装置的端口（集气瓶〔瓶底(深)〕），自动用导管连起来"
                : "已退出导管连接：点橙点恢复为普通连线/液封操作");
        }

        /// <summary>导管模式下端口统一染成蓝绿并全部露出（只露"能接的"），退出即回到常态。</summary>
        private void RefreshConduitPortTint()
        {
            ApplyPortVisibility();
        }

        /// <summary>导管模式下的端口点击：第 1 下选起点，第 2 下成管；点已接导管的端口＝拆除。</summary>
        private void OnConduitPortClicked(AnchorRef cur)
        {
            if (cur.PortType != "mouth" && cur.PortType != "water" && cur.PortType != "gas")
            {
                SetCaption("导管要接在〔瓶口〕〔导管口〕这类端口上；〔" + LabelOf(cur.Key) + "〕不是可接端口");
                return;
            }

            if (_conduitPending == null)
            {
                string exist = ConduitAtPort(cur.NodeId, cur.Port);
                if (!string.IsNullOrEmpty(exist)) { RemoveConduit(exist); return; }
                _conduitPending = cur;
                ApplyPortVisibility();
                Node n = State.FindNode(cur.NodeId);
                SetCaption("导管第 1 端已选：〔" + ElementCatalog.NameOf(n == null ? "" : n.Type) + "·" +
                           LabelOf(cur.Key) + "〕，请再点另一个装置的端口");
                return;
            }

            if (_conduitPending.NodeId == cur.NodeId && _conduitPending.Key == cur.Key)
            {
                _conduitPending = null;
                RefreshConduitPortTint();
                SetCaption("已取消，重新点第 1 个端口");
                return;
            }
            if (_conduitPending.NodeId == cur.NodeId)
            {
                SetCaption("一根导管要连接两个不同的装置，请点另一个装置上的端口");
                return;
            }
            ConnectConduit(_conduitPending, cur);
            _conduitPending = null;
            RefreshConduitPortTint();
        }

        private bool IsCollectorNode(string nodeId)
        {
            Node n = State.FindNode(nodeId);
            return n != null && (n.Type == "collect.gas_jar" || n.Type == "collect.water_trough");
        }

        private string ConduitAtPort(string nodeId, string port)
        {
            foreach (Node n in State.Nodes)
            {
                if (n.Type != "conduit.bent") continue;
                Edge eIn = FindConduitEdge(n.Id, "in");
                Edge eOut = FindConduitEdge(n.Id, "out");
                if (Matches(eIn, nodeId, port) || Matches(eOut, nodeId, port)) return n.Id;
            }
            return null;
        }

        private static bool Matches(Edge e, string nodeId, string port)
        {
            return e != null && Edge.NodeOf(e.To) == nodeId && Edge.PortOf(e.To) == port;
        }

        /// <summary>把两个端口用一根导管连起来：自动建 conduit.bent 节点 + 两条 gas 边，
        /// 画布上由 RefreshConduitVisuals 画成折线管。判定链看到的仍是标准拓扑。</summary>
        private void ConnectConduit(AnchorRef a, AnchorRef b)
        {
            // 排水法：集气瓶瓶口 ↔ 水槽是水下连接，不需要导管。
            // H3：切换收集方式时已在状态层建好 water 边；这里只做"确认/缺件时补连"，不重复建边。
            if (a.PortType == "water" || b.PortType == "water")
            {
                AnchorRef w = a.PortType == "water" ? a : b;
                AnchorRef m = a.PortType == "water" ? b : a;
                if (m.PortType != "mouth") { SetCaption("水槽只能接在集气瓶的〔瓶口〕上"); return; }
                if (!State.HasWaterEdge())
                {
                    string werr = State.ConnectWater();
                    if (!string.IsNullOrEmpty(werr)) { SetCaption("连接失败：" + Codes.Message(werr)); return; }
                }
                RefreshAll(); RefreshLines();
                SetCaption("排水法已接通：集气瓶瓶口 ↔ 水槽（无需导管）");
                SetConduitMode(false);
                return;
            }

            // 方向：发生装置 → 收集装置（导管.in 在发生装置侧，.out 在收集装置侧）
            AnchorRef src = a, dst = b;
            if (IsCollectorNode(a.NodeId) && !IsCollectorNode(b.NodeId)) { src = b; dst = a; }

            Node conduit = FindFreeConduit();
            bool created = conduit == null;
            if (created) conduit = State.AddNode("conduit.bent");
            if (conduit == null) { SetCaption("无法创建导管节点"); return; }

            string inEnd = src.NodeId + "." + src.Port;
            string outEnd = dst.NodeId + "." + dst.Port;
            string err = State.Connect(conduit.Id + ".in", inEnd, "gas");
            if (string.IsNullOrEmpty(err)) err = State.Connect(conduit.Id + ".out", outEnd, "gas");
            if (!string.IsNullOrEmpty(err))
            {
                RemoveConduitEdges(conduit.Id);
                if (created) State.RemoveNode(conduit.Id);
                SetCaption("连接失败：" + Codes.Message(err));
                return;
            }

            // 插到集气瓶的深度由点的端口决定：瓶底(深)=deep，瓶口=shallow
            Node dstNode = State.FindNode(dst.NodeId);
            bool deep = dstNode != null && dstNode.Type == "collect.gas_jar" && dst.Spot == "bottom";
            if (dstNode != null && dstNode.Type == "collect.gas_jar")
                State.SetEdgeProp(conduit.Id + ".out", outEnd, "depth", deep ? "deep" : "shallow");

            // 这个导管不再需要"先点入口再点出口"的排队任务
            _tasks.RemoveAll(t => t.NodeId == conduit.Id);

            RefreshAll();
            RefreshLines();
            SetCaption("导管已连接：" + ElementCatalog.NameOf(State.FindNode(src.NodeId).Type) + " → " +
                       ElementCatalog.NameOf(dstNode == null ? "" : dstNode.Type) +
                       (dstNode != null && dstNode.Type == "collect.gas_jar"
                           ? (deep ? "（导管伸到瓶底 ✓）" : "（导管只到瓶口，浅）") : ""));
            SetConduitMode(false);
        }

        private Node FindFreeConduit()
        {
            foreach (Node n in State.Nodes)
            {
                if (n.Type != "conduit.bent") continue;
                bool used = false;
                foreach (Edge e in State.Edges)
                    if (Edge.NodeOf(e.From) == n.Id || Edge.NodeOf(e.To) == n.Id) { used = true; break; }
                if (!used) return n;
            }
            return null;
        }

        private Edge FindConduitEdge(string conduitId, string port)
        {
            foreach (Edge e in State.Edges)
                if (Edge.NodeOf(e.From) == conduitId && Edge.PortOf(e.From) == port) return e;
            return null;
        }

        private bool IsWiredConduit(string nodeId)
        {
            Node n = State.FindNode(nodeId);
            if (n == null || n.Type != "conduit.bent") return false;
            return FindConduitEdge(nodeId, "in") != null && FindConduitEdge(nodeId, "out") != null;
        }

        private void RemoveConduitEdges(string conduitId)
        {
            for (int i = State.Edges.Count - 1; i >= 0; i--)
                if (Edge.NodeOf(State.Edges[i].From) == conduitId)
                    State.Edges.RemoveAt(i);
        }

        public void RemoveConduit(string conduitId)
        {
            DestroyConduitNode(conduitId);
            RefreshAll();
            RefreshLines();
            SetCaption("已拆除这根导管；重新进入〔导管连接〕对着端口点两下可以换接法或改插入深度");
        }

        /// <summary>只拆数据与实例，不刷新（供批量拆除时统一刷新）。</summary>
        private void DestroyConduitNode(string conduitId)
        {
            DeviceItem item;
            if (_items.TryGetValue(conduitId, out item) && item != null)
            {
                // 先隐藏再销毁：Destroy 是延后到帧末执行的，暂停/不推进帧时旧物件会留在画布上
                item.gameObject.SetActive(false);
                Destroy(item.gameObject);
            }
            _items.Remove(conduitId);
            _deviceArt.Remove(conduitId);
            _nameLabels.Remove(conduitId);
            _dropHints.Remove(conduitId);
            _tasks.RemoveAll(t => t.NodeId == conduitId);
            State.RemoveNode(conduitId);   // 关联的两条 gas 边由 RemoveNode 一并清掉
        }

        /// <summary>这根导管是否有一端接在这个器材上。</summary>
        private bool ConduitTouches(string conduitId, string nodeId)
        {
            Edge eIn = FindConduitEdge(conduitId, "in");
            Edge eOut = FindConduitEdge(conduitId, "out");
            return (eIn != null && Edge.NodeOf(eIn.To) == nodeId) ||
                   (eOut != null && Edge.NodeOf(eOut.To) == nodeId);
        }

        // ---------------------------------------------------------------- 删除器材模式

        private void OnToggleDeleteMode() { SetDeleteMode(!_deleteMode); }

        public void SetDeleteMode(bool on)
        {
            bool changed = _deleteMode != on;
            _deleteMode = on;
            // 两个模式互斥：进入删除态先退出导管连接（否则点端口会被导管吃掉）
            if (on && _conduitMode) SetConduitMode(false);
            if (_btnDeleteBg != null) _btnDeleteBg.color = on ? C_BtnDel : new Color(0.86f, 0.9f, 0.94f);
            if (_btnDeleteLabel != null)
            {
                _btnDeleteLabel.text = on ? "删除中…点器材" : "删除器材";
                _btnDeleteLabel.color = on ? Color.white : new Color(0.1f, 0.12f, 0.16f);
            }
            ApplyDeviceTint();
            ApplyPortVisibility();   // 删除态下端口一律收起，别和红框抢眼
            if (!changed) return;
            SetCaption(on
                ? "删除器材：点一下要删的器材·瓶里的试剂一起清掉·连带导管一并拆除"
                : "已退出删除器材：器材恢复为可拖动");
        }

        /// <summary>删除态下器材泛淡红 + 套一圈红框；退出时恢复原色、收掉红框（不依赖计时器，不会卡住）。</summary>
        private void ApplyDeviceTint()
        {
            Color c = _deleteMode ? C_DeviceDelTint : Color.white;
            foreach (KeyValuePair<string, StrokeGraphic> kv in _deviceArt)
                if (kv.Value != null) kv.Value.color = c;
            ApplyDeleteHints();
        }

        /// <summary>删除态：每件器材套一圈红框，让学生一眼看出"这些都能点掉"。</summary>
        private void ApplyDeleteHints()
        {
            foreach (KeyValuePair<string, List<Image>> kv in _dropHints)
            {
                List<Image> list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null) continue;
                    list[i].gameObject.SetActive(_deleteMode);
                    if (!_deleteMode) continue;
                    Color cc = C_HintNo;
                    cc.a = i == 0 ? 0.06f : 1f;   // 第 0 个是淡底，其余是边框
                    list[i].color = cc;
                }
            }
        }

        /// <summary>点中器材（真实点击或桥接脚本都走这里）：删除态下＝删除该器材；否则＝点亮它的端口
        /// （触屏没有 hover，用点击来让端口出现）。</summary>
        public void OnDeviceClicked(string nodeId)
        {
            if (_deleteMode) { DeleteDevice(nodeId); return; }
            SetActiveDevice(nodeId);
        }

        /// <summary>删除一件器材：容器里的试剂、接入的漏斗/橡胶塞、两端导管一起清掉。</summary>
        public bool DeleteDevice(string nodeId)
        {
            Node node = State.FindNode(nodeId);
            if (node == null) return false;

            if (node.Type == "conduit.bent") { RemoveConduit(nodeId); return true; }

            string name = ElementCatalog.NameOf(node.Type);
            bool isReactor = node.Type == "reactor.flask_conical" || node.Type == "reactor.test_tube";
            bool hadSolid = isReactor && !string.IsNullOrEmpty(State.SolidReagent);
            bool hadLiquid = isReactor && !string.IsNullOrEmpty(State.LiquidReagent);

            DeviceItem item;
            _items.TryGetValue(nodeId, out item);

            // 1) 装配关系先解开：容器 → 拔掉漏斗（漏斗留在画布上，挪到旁边）；漏斗 → 直接解除
            string funnelId;
            if (_funnelOfFlask.TryGetValue(nodeId, out funnelId))
            {
                DetachFunnel(funnelId, false);
                DeviceItem fi;
                if (item != null && _items.TryGetValue(funnelId, out fi) && fi != null)
                {
                    Vector2 fp = ((RectTransform)item.transform).anchoredPosition + new Vector2(-ItemW - 30f, 0f);
                    if (fp.x < 4f) fp = ((RectTransform)item.transform).anchoredPosition + new Vector2(ItemW + 30f, 0f);
                    ((RectTransform)fi.transform).anchoredPosition = ClampDevice(fp);
                }
            }
            if (node.Type == "feed.thistle_funnel" && _flaskOfFunnel.ContainsKey(nodeId))
            {
                DetachFunnel(nodeId, false);
                // 漏斗里剩下没倒下去的液体跟着漏斗一起删掉；瓶里已有的液体不受影响
                _funnelReagent.Remove(nodeId);
            }

            // 2) 两端接在本器材上的导管一起拆掉（否则画布上会剩半根断管）
            List<string> conduits = new List<string>();
            foreach (Node n in State.Nodes)
                if (n.Type == "conduit.bent" && ConduitTouches(n.Id, nodeId)) conduits.Add(n.Id);
            foreach (string cid in conduits) DestroyConduitNode(cid);

            // 3) 器材本体与所有登记表
            if (item != null)
            {
                item.gameObject.SetActive(false);   // 立即从画面消失（Destroy 帧末才真正生效）
                Destroy(item.gameObject);
            }
            _items.Remove(nodeId);
            _deviceArt.Remove(nodeId);
            _nameLabels.Remove(nodeId);
            _liquidLabels.Remove(nodeId);
            _solidLabels.Remove(nodeId);
            _letters.Remove(nodeId);
            _dropHints.Remove(nodeId);
            _funnelStatus.Remove(nodeId);
            _funnelBadges.Remove(nodeId);
            _funnelReagent.Remove(nodeId);
            _tasks.RemoveAll(t => t.NodeId == nodeId);
            ForgetAnchors(nodeId);

            // 橡胶塞随瓶口连线数由 RefreshStoppers 统一增删，这里不用单独处理
            _snapFlaskId = null; _snapKey = null;
            if (_hoverNodeId == nodeId) _hoverNodeId = null;
            if (_pending != null && _pending.NodeId == nodeId) SetPending(null);
            if (_conduitPending != null && _conduitPending.NodeId == nodeId)
            {
                _conduitPending = null;
                RefreshConduitPortTint();
            }

            State.RemoveNode(nodeId);

            // 4) 容器里的试剂跟着容器一起没了
            if (isReactor)
            {
                State.SolidReagent = "";
                State.LiquidReagent = "";
                State.LiquidRoute = "funnel";
            }
            // 水槽被删：排水法没有承接装置了，收集方式退回正放，集气瓶也转回正放
            if (node.Type == "collect.water_trough" && State.CollectorMode == "water")
            {
                State.CollectorMode = "upright";
                foreach (Node n in State.Nodes)
                    if (n.Type == "collect.gas_jar") State.SetNodeProp(n.Id, "orientation", "upright");
            }

            RefreshAll();
            RefreshLines();
            // 结构变了，上一次的判定结论不再成立：清掉右栏，避免删完器材仍挂着"√ 目标达成"
            ResetCheckState();
            SetDiag("尚未提交", "", "");

            string msg = "已删除〔" + name + "〕";
            if (hadSolid || hadLiquid)
                msg += "：瓶里的" + (hadSolid && hadLiquid ? "固体和液体" : hadSolid ? "固体" : "液体") + "一起清掉了";
            if (conduits.Count > 0) msg += "·连带导管已拆";
            SetCaption(msg);
            return true;
        }

        /// <summary>清掉某个器材在端口表里的登记（节点销毁后这些表会留空引用）。</summary>
        private void ForgetAnchors(string nodeId)
        {
            List<PortVis> list;
            if (_portsOfNode.TryGetValue(nodeId, out list))
            {
                foreach (PortVis p in list) _ports.Remove(p.NodeId + "|" + p.Key);
                _portsOfNode.Remove(nodeId);
            }
            if (_activeDeviceId == nodeId) _activeDeviceId = null;
            if (_hoverDeviceId == nodeId) _hoverDeviceId = null;
            if (_hoverPortKey != null && _hoverPortKey.StartsWith(nodeId + "|", StringComparison.Ordinal))
                _hoverPortKey = null;
        }

        /// <summary>端口在画布坐标里的位置（器材左下角 + 连接点局部坐标）。</summary>
        private bool AnchorPos(string nodeId, string key, out Vector2 pos)
        {
            pos = Vector2.zero;
            DeviceItem item;
            if (!_items.TryGetValue(nodeId, out item) || item == null) return false;
            AnchorPoint ap = FindAnchorPoint(nodeId, key);
            if (ap == null) return false;
            pos = ((RectTransform)item.transform).anchoredPosition + ((RectTransform)ap.transform).anchoredPosition;
            return true;
        }

        /// <summary>导管该从哪儿起笔：锥形瓶接上漏斗后从橡胶塞的出气短管出来（不穿过漏斗颈），
        /// 没接漏斗就从瓶颈口出来；其余器材直接用端口连接点。</summary>
        private bool ConduitEndPoint(string nodeId, string key, out Vector2 pos)
        {
            if (!AnchorPos(nodeId, key, out pos)) return false;
            Node n = State.FindNode(nodeId);
            // 水槽〔导管口〕：端口在槽沿上，管子本身要伸进水里（画到槽内水面以下）
            if (n != null && n.Type == "collect.water_trough" && key == "tube")
            {
                DeviceItem ti;
                if (_items.TryGetValue(nodeId, out ti) && ti != null)
                    pos = ((RectTransform)ti.transform).anchoredPosition + new Vector2(36f, 50f);
                return true;
            }
            if (n == null || key != "mouth") return true;
            if (n.Type != "reactor.flask_conical" && n.Type != "reactor.test_tube") return true;
            DeviceItem item;
            if (!_items.TryGetValue(nodeId, out item) || item == null) return true;
            // 瓶口堵了橡胶塞：导管从塞子出气短管口（93,107）起笔；空瓶才从瓶口唇线出来
            string sig;
            bool stub = _stopperSig.TryGetValue(nodeId, out sig) && sig.IndexOf('G') >= 0;
            pos = ((RectTransform)item.transform).anchoredPosition +
                  (stub ? new Vector2(93f, 107f) : DeviceGlyph.Anchor(n.Type, "mouth", GlyphStateOf(n)));
            return true;
        }

        private Transform TubeRoot()
        {
            if (_tubeRoot == null)
            {
                _tubeRoot = new GameObject("ConduitRoot");
                _tubeRoot.transform.SetParent(_content, false);
                RectTransform rt = _tubeRoot.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
                rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(ContentW, ContentH);
            }
            _tubeRoot.transform.SetAsLastSibling();   // 压在器材之上：管子伸进集气瓶里也看得见
            return _tubeRoot.transform;
        }

        /// <summary>按"两端都接满"重画所有导管：接满的画折线管并收起器材图标，没接满的恢复成可拖器材。</summary>
        private void RefreshConduitVisuals()
        {
            RefreshStoppers();   // 先定塞子（导管起笔要对准出气短管口），再重建管体
            foreach (KeyValuePair<string, GameObject> kv in _tubes)
            {
                if (kv.Value == null) continue;
                kv.Value.SetActive(false);
                Destroy(kv.Value);
            }
            _tubes.Clear();

            foreach (Node n in State.Nodes)
            {
                if (n.Type != "conduit.bent") continue;
                Edge eIn = FindConduitEdge(n.Id, "in");
                Edge eOut = FindConduitEdge(n.Id, "out");
                bool wired = eIn != null && eOut != null;

                DeviceItem item;
                if (_items.TryGetValue(n.Id, out item) && item != null && item.gameObject.activeSelf == wired)
                    item.gameObject.SetActive(!wired);
                if (!wired) continue;

                string aNode = Edge.NodeOf(eIn.To), aPort = Edge.PortOf(eIn.To);
                string bNode = Edge.NodeOf(eOut.To), bPort = Edge.PortOf(eOut.To);
                Vector2 p1, p2;
                if (!ConduitEndPoint(aNode, aPort, out p1)) continue;

                // 集气瓶这一端：depth=deep 时画到〔瓶底(深)〕，否则停在瓶口
                Node bn = State.FindNode(bNode);
                string bKey = bPort;
                if (bn != null && bn.Type == "collect.gas_jar" && eOut.Prop("depth", "shallow") == "deep")
                    bKey = "bottom";
                if (!ConduitEndPoint(bNode, bKey, out p2) && !ConduitEndPoint(bNode, bPort, out p2)) continue;

                _tubes[n.Id] = BuildTube(n.Id, p1, p2);
            }
        }

        /// <summary>橡胶塞统一按瓶口连线画：锥形瓶默认带塞（试卷口径的发生装置），插漏斗＝留穿颈通道，
        /// 有气路（或已插漏斗）＝带出气短管；试管只在瓶口有连线时才画塞。位置跟随容器，签名不变不重画。</summary>
        private void RefreshStoppers()
        {
            foreach (Node n in State.Nodes)
            {
                if (n.Type != "reactor.flask_conical" && n.Type != "reactor.test_tube") continue;
                bool funnelHole = false, gas = false;
                foreach (Edge e in State.Edges)
                {
                    if (e.To != n.Id + ".mouth") continue;
                    if (e.Kind == "feed") funnelHole = true;
                    else if (e.Kind == "gas") gas = true;
                }
                bool stub = funnelHole || gas;   // 双孔塞：插了漏斗就预留出气短管等导管来接

                GameObject st;
                if (!funnelHole && !gas && n.Type != "reactor.flask_conical")
                {
                    if (_stoppers.TryGetValue(n.Id, out st) && st != null) { st.SetActive(false); Destroy(st); }
                    _stoppers.Remove(n.Id);
                    _stopperSig.Remove(n.Id);
                    continue;
                }
                DeviceItem item;
                if (!_items.TryGetValue(n.Id, out item) || item == null) continue;
                if (!_stoppers.TryGetValue(n.Id, out st) || st == null)
                {
                    st = new GameObject("Stopper_" + n.Id);
                    st.transform.SetParent(_content, false);
                    RectTransform srt = st.AddComponent<RectTransform>();
                    srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.zero; srt.pivot = Vector2.zero;
                    srt.sizeDelta = new Vector2(DeviceGlyph.W, DeviceGlyph.H);
                    st.AddComponent<CanvasRenderer>();
                    StrokeGraphic sg0 = st.AddComponent<StrokeGraphic>();
                    sg0.WidthBoost = 1.32f;   // 橡皮塞与装置区器材同档加粗
                    sg0.raycastTarget = false;
                    _stoppers[n.Id] = st;
                    _stopperSig[n.Id] = null;   // 哨兵：保证首帧必画（空签名的实心塞也要画）
                }
                ((RectTransform)st.transform).anchoredPosition = ((RectTransform)item.transform).anchoredPosition;
                st.transform.SetAsLastSibling();   // 盖住穿塞的漏斗颈与瓶口唇线
                string sig = (funnelHole ? "F" : "") + (stub ? "G" : "");
                if (_stopperSig[n.Id] != sig)
                {
                    _stopperSig[n.Id] = sig;
                    StrokeGraphic sg = st.GetComponent<StrokeGraphic>();
                    sg.Clear();
                    DeviceGlyph.State gs = default(DeviceGlyph.State);
                    gs.FunnelHole = funnelHole;
                    gs.GasStub = stub;
                    DeviceGlyph.Draw(sg, "stopper", gs);
                }
            }
            // 容器被删后塞子跟着消失（DeleteDevice 不再单独管塞子）
            List<string> stale = null;
            foreach (KeyValuePair<string, GameObject> kv in _stoppers)
                if (State.FindNode(kv.Key) == null) (stale ?? (stale = new List<string>())).Add(kv.Key);
            if (stale != null)
                foreach (string id in stale)
                {
                    if (_stoppers[id] != null) { _stoppers[id].SetActive(false); Destroy(_stoppers[id]); }
                    _stoppers.Remove(id);
                    _stopperSig.Remove(id);
                }
        }

        /// <summary>课本式导管：从起点竖直上引 → 水平横跨 → 竖直下到终点；双线空心管 + 中段橡胶套管。</summary>
        private GameObject BuildTube(string conduitId, Vector2 p1, Vector2 p2)
        {
            GameObject root = new GameObject("Tube_" + conduitId);
            root.transform.SetParent(TubeRoot(), false);
            RectTransform rt = root.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(ContentW, ContentH);
            root.AddComponent<CanvasRenderer>();
            StrokeGraphic g = root.AddComponent<StrokeGraphic>();
            g.raycastTarget = false;

            const float h = 3f;        // 半管径：管外径 6px，与漏斗茎玻璃管同细

            // 两端就落在端口心上（端口点在管子空心里还看得见，管子看起来是"插在上面"而不是悬空）
            Vector2 a = p1;
            Vector2 b = p2;

            float top = Mathf.Max(a.y, b.y) + 42f;
            top = Mathf.Min(top, ContentH - 8f);
            if (top < Mathf.Max(a.y, b.y) + 18f) top = Mathf.Max(a.y, b.y) + 18f;
            float s = b.x >= a.x ? 1f : -1f;

            // 两条轮廓线（外/内），各 3 段：竖 → 横 → 竖。拐角处 miter 相接，管腔保持空心。
            g.Poly(new[]
            {
                new Vector2(a.x - s * h, a.y), new Vector2(a.x - s * h, top + h),
                new Vector2(b.x + s * h, top + h), new Vector2(b.x + s * h, b.y)
            }, 2f, StrokeGraphic.Ink, false);
            g.Poly(new[]
            {
                new Vector2(a.x + s * h, a.y), new Vector2(a.x + s * h, top - h),
                new Vector2(b.x - s * h, top - h), new Vector2(b.x - s * h, b.y)
            }, 2f, StrokeGraphic.Ink, false);

            float midX = (a.x + b.x) * 0.5f;
            Sleeve(g, new Vector2(midX - 7f, top));
            Sleeve(g, new Vector2(midX + 7f, top));
            return root;
        }

        /// <summary>橡胶套管：试卷图里导管中段那两节黑色实心接头。</summary>
        private static void Sleeve(StrokeGraphic g, Vector2 c)
        {
            g.FillPoly(new[]
            {
                new Vector2(c.x - 6, c.y - 7), new Vector2(c.x + 6, c.y - 7),
                new Vector2(c.x + 6, c.y + 7), new Vector2(c.x - 6, c.y + 7)
            }, StrokeGraphic.Ink);
        }

        // ---------------------------------------------------------------- 连线可视化
        private Vector2 AnchorWorld(AnchorRef a)
        {
            DeviceItem item;
            if (!_items.TryGetValue(a.NodeId, out item)) return a.Local;
            Vector2 p = ((RectTransform)item.transform).anchoredPosition + a.Local;
            p.x += 7f; p.y += 7f;
            return p;
        }

        private void RefreshLines()
        {
            foreach (KeyValuePair<string, GameObject> kv in _edgeLines)
                if (kv.Value != null) { kv.Value.SetActive(false); Destroy(kv.Value); }
            _edgeLines.Clear();

            foreach (Edge e in State.Edges)
            {
                // 已经完全接通的导管：由折线管本体表示，不再画直线
                if (IsWiredConduit(Edge.NodeOf(e.From)) || IsWiredConduit(Edge.NodeOf(e.To))) continue;
                // 已经接入的漏斗不再画 feed 线：橡胶塞 + 穿塞的漏斗颈本身就是"接上了"
                if (_flaskOfFunnel.ContainsKey(Edge.NodeOf(e.From)) && Edge.PortOf(e.From) == "bottom")
                    continue;
                AnchorRef a = FindAnchor(Edge.NodeOf(e.From), Edge.PortOf(e.From));
                AnchorRef b = FindAnchor(Edge.NodeOf(e.To), Edge.PortOf(e.To));
                if (a == null || b == null) continue;
                Vector2 p1 = AnchorWorld(a), p2 = AnchorWorld(b);

                GameObject go = new GameObject("Wire_" + e.From + "_" + e.To);
                go.transform.SetParent(_content, false);
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
                rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(ContentW, ContentH);
                go.AddComponent<CanvasRenderer>();
                StrokeGraphic g = go.AddComponent<StrokeGraphic>();
                g.raycastTarget = false;
                // 试卷口径一律墨线；排水法的水路用虚线区别于气路
                if (e.Kind == "water") g.Dash(p1, p2, 2f, StrokeGraphic.Ink);
                else g.Line(p1, p2, 2f, StrokeGraphic.Ink);
                _edgeLines[e.From + "|" + e.To] = go;
            }

            RefreshConduitVisuals();
            CheckStale();
        }

        private void ClearBoard()
        {
            SetPending(null);
            _tasks.Clear();
            foreach (KeyValuePair<string, DeviceItem> kv in _items)
                if (kv.Value != null) { kv.Value.gameObject.SetActive(false); Destroy(kv.Value.gameObject); }
            _items.Clear();
            foreach (KeyValuePair<string, GameObject> kv in _edgeLines)
                if (kv.Value != null) { kv.Value.SetActive(false); Destroy(kv.Value); }
            _edgeLines.Clear();
            _ports.Clear();
            _portsOfNode.Clear();
            _hoverPortKey = null;
            _hoverDeviceId = null;
            _activeDeviceId = null;
            _deviceArt.Clear();
            _letters.Clear();
            _nameLabels.Clear();
            _liquidLabels.Clear();
            _solidLabels.Clear();
            _dropHints.Clear();
            _hoverNodeId = null;
            _funnelBadges.Clear();
            _funnelReagent.Clear();
            _funnelOfFlask.Clear();
            _flaskOfFunnel.Clear();
            _asmSpot.Clear();
            _funnelStatus.Clear();
            _funnelNames.Clear();
            foreach (KeyValuePair<string, GameObject> kv in _stoppers)
                if (kv.Value != null) { kv.Value.SetActive(false); Destroy(kv.Value); }
            _stoppers.Clear();
            _stopperSig.Clear();
            _snapFlaskId = null; _snapKey = null;
            foreach (KeyValuePair<string, GameObject> kv in _tubes)
                if (kv.Value != null) { kv.Value.SetActive(false); Destroy(kv.Value); }
            _tubes.Clear();
            if (_tubeRoot != null) { Destroy(_tubeRoot); _tubeRoot = null; }
            SetConduitMode(false);
            SetDeleteMode(false);
            if (_deviceScroll != null) _deviceScroll.horizontalNormalizedPosition = 0f;
        }

        // ---------------------------------------------------------------- 刷新
        private void RefreshAll()
        {
            RefreshTabs();
            RefreshApparatus();
            RefreshSlots();
            RefreshPortAnchors();
            RefreshFill();
            RefreshFunnelBadges();
            RefreshFunnelLabels();
            RefreshConduitVisuals();
            ApplyDeviceTint();   // 删除态下器材泛淡红；平时恢复底色
            if (_conduitMode) RefreshConduitPortTint();
            CheckStale();
        }

        /// <summary>反应容器的装填形态：空瓶 / 固体 / 液体 / 固体+液体，按状态重画线稿
        /// （液面+短划 / 颗粒堆），并同步试剂名引线文字与装置字母编号。</summary>
        private void RefreshFill()
        {
            foreach (KeyValuePair<string, StrokeGraphic> kv in _deviceArt)
            {
                StrokeGraphic art = kv.Value;
                if (art == null) continue;
                Node n = State.FindNode(kv.Key);
                if (n == null) continue;
                art.Clear();
                DeviceGlyph.Draw(art, n.Type, GlyphStateOf(n));
            }
            foreach (KeyValuePair<string, Text> kv in _liquidLabels)
            {
                if (kv.Value == null) continue;
                bool has = !string.IsNullOrEmpty(State.LiquidReagent);
                kv.Value.gameObject.SetActive(has);
                if (!has) continue;
                kv.Value.text = ReagentNameOf(State.LiquidReagent);
                // 靠近画布右缘时把标签收回来，别让引线文字被裁掉
                DeviceItem it;
                if (_items.TryGetValue(kv.Key, out it) && it != null)
                {
                    float x = ((RectTransform)it.transform).anchoredPosition.x;
                    ((RectTransform)kv.Value.transform).anchoredPosition =
                        new Vector2(Mathf.Min(106f, ContentW - 124f - x), 60f);
                }
            }
            foreach (KeyValuePair<string, Text> kv in _solidLabels)
            {
                if (kv.Value == null) continue;
                bool has = !string.IsNullOrEmpty(State.SolidReagent);
                kv.Value.gameObject.SetActive(has);
                if (!has) continue;
                kv.Value.text = ReagentNameOf(State.SolidReagent);
                DeviceItem it;
                if (_items.TryGetValue(kv.Key, out it) && it != null)
                {
                    float x = ((RectTransform)it.transform).anchoredPosition.x;
                    ((RectTransform)kv.Value.transform).anchoredPosition =
                        new Vector2(Mathf.Max(-126f, 4f - x), 6f);
                }
            }
            RefreshLetters();
        }

        /// <summary>把 DesignState 翻译成线稿状态：装填 / 朝向 / 玻璃片 / 装配 / 漏斗存液。</summary>
        private DeviceGlyph.State GlyphStateOf(Node n)
        {
            DeviceGlyph.State gs = default(DeviceGlyph.State);
            if (n.Type == "reactor.flask_conical" || n.Type == "reactor.test_tube")
            {
                gs.Solid = !string.IsNullOrEmpty(State.SolidReagent);
                gs.Liquid = !string.IsNullOrEmpty(State.LiquidReagent);
                gs.Powder = State.SolidReagent == "reagent.caco3_powder";
                gs.Assembled = _funnelOfFlask.ContainsKey(n.Id);
            }
            else if (n.Type == "feed.thistle_funnel")
            {
                string rid;
                gs.HeldLiquid = _funnelReagent.TryGetValue(n.Id, out rid) && !string.IsNullOrEmpty(rid);
                gs.Assembled = _flaskOfFunnel.ContainsKey(n.Id);
            }
            else if (n.Type == "collect.gas_jar")
            {
                gs.Inverted = n.Prop("orientation", "upright") == "inverted";
                // 玻璃片默认盖着：插着导管＝盖右留左缝（PlateGap），移管后盖严；倒置排水不画片
                gs.Covered = n.PropBool("covered", true);
                gs.PlateGap = MouthHasGas(n.Id);
            }
            return gs;
        }

        private bool MouthHasGas(string nodeId)
        {
            foreach (Edge e in State.Edges)
                if (e.To == nodeId + ".mouth" && e.Kind == "gas") return true;
            return false;
        }

        /// <summary>试卷式字母编号：按放置顺序给"看得见的装置"顺排 A/B/C…（接入后的漏斗让位给容器）。</summary>
        private void RefreshLetters()
        {
            _letters.Clear();
            foreach (Node n in State.Nodes)
            {
                if (n.Type == "conduit.bent") continue;
                if (n.Type == "feed.thistle_funnel" && _flaskOfFunnel.ContainsKey(n.Id)) continue;
                _letters[n.Id] = ((char)('A' + _letters.Count)).ToString();
            }
            foreach (KeyValuePair<string, Text> kv in _nameLabels)
            {
                if (kv.Value == null) continue;
                Node n = State.FindNode(kv.Key);
                if (n == null) continue;
                string letter;
                kv.Value.text = _letters.TryGetValue(kv.Key, out letter)
                    ? letter + "  " + ElementCatalog.NameOf(n.Type)
                    : ElementCatalog.NameOf(n.Type);
                // 名字压在台面线下方一条；排水法时集气瓶浸在水槽里，名字让到左侧避免和水槽名字叠在一起
                RectTransform lrt = (RectTransform)kv.Value.transform;
                if (n.Type == "collect.gas_jar" && State.CollectorMode == "water")
                {
                    lrt.anchoredPosition = new Vector2(-126f, 70f);
                    kv.Value.alignment = TextAnchor.MiddleRight;
                }
                else
                {
                    lrt.anchoredPosition = new Vector2(-6f, 16f - RestY(n.Type));
                    kv.Value.alignment = TextAnchor.MiddleCenter;
                }
            }
        }

        /// <summary>长颈漏斗顶部加料提示：有试剂才显示"试剂名 + ↓"。</summary>
        private void RefreshFunnelBadges()
        {
            foreach (KeyValuePair<string, GameObject> kv in _funnelBadges)
            {
                if (kv.Value == null) continue;
                string rid;
                bool has = _funnelReagent.TryGetValue(kv.Key, out rid) && !string.IsNullOrEmpty(rid);
                kv.Value.SetActive(has);
                if (!has) continue;
                Text name = kv.Value.transform.childCount > 0 ? kv.Value.transform.GetChild(0).GetComponent<Text>() : null;
                if (name != null) name.text = ReagentNameOf(rid);
            }
        }

        /// <summary>哪些端口此刻"业务上用不到"：空瓶只留〔瓶内〕，有液体换成〔液面上方〕/〔液面下方〕；
        /// 漏斗插进左列后左列收起（右列镜像接棒）；漏斗下端插进容器后自己也收起。
        /// 这里只标 LogicHidden，真正的显隐/配色统一交给 ApplyPortVisibility。</summary>
        private void RefreshPortAnchors()
        {
            bool hasLiquid = !string.IsNullOrEmpty(State.LiquidReagent);
            foreach (KeyValuePair<string, PortVis> kv in _ports)
            {
                PortVis p = kv.Value;
                bool hidden = false;
                if (p.Base == "inside") hidden = hasLiquid;
                else if (p.Base == "above" || p.Base == "below") hidden = !hasLiquid;

                // 长颈漏斗插在左列时，左列那颗端口正压在下端玻璃管上（看起来像"端口长在漏斗上"）：
                // 收掉左列，右列镜像那颗照样能点、能接导管，也照样能改插入深度。
                // 注意 _funnelOfFlask 是"容器 → 漏斗"，key 才是容器 id。
                if (!p.Mirrored && (p.Base == "above" || p.Base == "below" || p.Base == "inside") &&
                    _funnelOfFlask.ContainsKey(p.NodeId)) hidden = true;

                // 漏斗下端已经插进容器：这颗点用掉了，拔下来会自动回来
                if (p.Base == "bottom" && _flaskOfFunnel.ContainsKey(p.NodeId)) hidden = true;

                // 端口与线稿几何同源（DeviceGlyph.Anchor）：锥形瓶〔瓶口〕随装配态走——
                // 空瓶压在瓶口唇线上，插了漏斗落到塞子出气短管口心（93,107），导管从那里起笔；
                // 集气瓶〔瓶口〕〔瓶底〕〔玻璃片〕随正放/倒置重新对位
                if (p.Root != null)
                {
                    Node n = State.FindNode(p.NodeId);
                    if (n != null && (n.Type == "reactor.flask_conical" || n.Type == "collect.gas_jar"))
                        ((RectTransform)p.Root.transform).anchoredPosition =
                            DeviceGlyph.Anchor(n.Type, p.Key, GlyphStateOf(n));
                }

                p.LogicHidden = hidden;
            }
            ApplyPortVisibility();
        }

        /// <summary>漏斗接入后收起"长颈漏斗"这个名字标签：右上方要留给容器的端口标签，两条并排会糊成一片。</summary>
        private void RefreshFunnelLabels()
        {
            foreach (KeyValuePair<string, Text> kv in _funnelNames)
            {
                if (kv.Value == null) continue;
                kv.Value.gameObject.SetActive(!_flaskOfFunnel.ContainsKey(kv.Key));
            }
        }

        private void RefreshTabs()
        {
            if (_groupContainer == null) return;
            _groupContainer.SetActive(!_reagentTab);
            _groupReagent.SetActive(_reagentTab);
            if (_tabContainerBg != null) _tabContainerBg.color = _reagentTab ? C_TabOff : C_TabOn;
            if (_tabReagentBg != null) _tabReagentBg.color = _reagentTab ? C_TabOn : C_TabOff;
        }

        private void RefreshApparatus()
        {
            if (_deviceCaption == null) return;
            if (State.Nodes.Count == 0)
                SetCaption("（空）从左侧点击或拖入器材开始搭建");
            else
                SetCaption("器材 " + State.Nodes.Count + " 件 · 连线 " + State.Edges.Count +
                           " 条 · 拖动改位置，点橙色连接点连线（结果仍由判定链给出）");
        }

        private void SetCaption(string text) { if (_deviceCaption != null) _deviceCaption.text = Humanize(text); }

        private void RefreshSlots()
        {
            if (_checkAText == null) return;
            bool water = State.CollectorMode == "water";
            bool splint = !water && State.Fullness == "burning_splint";
            bool lime = State.Identity == "limewater";
            _checkAText.text = water ? "排水法·自动看瓶口气泡"
                : splint ? "燃着木条置于瓶口" : "未验满";
            _checkAText.color = water || splint ? Color.black : C_SlotEmpty;
            _checkBText.text = lime ? "气体通入澄清石灰水" : "未检验";
            _checkBText.color = lime ? Color.black : C_SlotEmpty;
            if (_btnSplint != null) _btnSplint.color = splint ? C_ModeOn : C_ModeOff;
            if (_btnMouthBubble != null) _btnMouthBubble.color = water ? C_ModeOn : C_ModeOff;
            if (_btnLimewater != null) _btnLimewater.color = lime ? C_ModeOn : C_ModeOff;
            foreach (KeyValuePair<string, Image> kv in _reagentCardBg)
                kv.Value.color = kv.Key == State.SolidReagent || kv.Key == State.LiquidReagent ? C_CardOn : C_CardOff;
            if (_btnUpright != null) _btnUpright.color = State.CollectorMode == "upright" ? C_ModeOn : C_ModeOff;
            if (_btnInverted != null) _btnInverted.color = State.CollectorMode == "inverted" ? C_ModeOn : C_ModeOff;
            if (_btnWater != null) _btnWater.color = State.CollectorMode == "water" ? C_ModeOn : C_ModeOff;
            _collectorLabel.text = State.CollectorMode == "upright" ? "正放·向上排空气"
                : State.CollectorMode == "inverted" ? "倒置·向下排空气" : "水槽·排水法";
            // 需求4：常驻的"固体：未选 ←...；液体：未选 ←..."引导文案已删除。
            // 试剂选择靠试剂卡高亮（上方循环里已做）；固液同瓶直投只在 RejectSimultaneousAddition 弹提示。
        }

        /// <summary>需求4：固液同瓶直投被拒时显示「不可同时放入」弹层；下一次任何投放动作自动收起。</summary>
        private void ShowRejectToast()
        {
            if (_rejectToast != null) _rejectToast.SetActive(true);
        }

        private void HideRejectToast()
        {
            if (_rejectToast != null) _rejectToast.SetActive(false);
        }

        /// <summary>漏斗里存着的液体（还没流进锥形瓶的那份）。</summary>
        private string FunnelHeldReagent()
        {
            foreach (KeyValuePair<string, string> kv in _funnelReagent)
                if (!string.IsNullOrEmpty(kv.Value)) return kv.Value;
            return "";
        }

        /// <summary>清掉所有漏斗上"存着试剂"的标记（试剂改为直接加进容器时用）。</summary>
        private void ClearFunnelReagentProps()
        {
            foreach (Node n in State.Nodes)
                if (n.Type == "feed.thistle_funnel") n.Props.Remove("reagent");
        }

        // ---- 显示层"人文化"：内部规则码/定位码只在判定与字幕分支里用，绝不显示给玩家 ----
        // 定位码 [n1.inside] / [SLOT-LIQUID.liquid] → 中文位置名（port 是稳定语义，nodeId 是动态的）
        private static string PortLabel(string nodeId, string port)
        {
            switch (port)
            {
                case "inside": return "锥形瓶内";
                case "bottom": return "长颈漏斗下端";
                case "out": return "导管出口";
                case "in": return "导管入口";
                case "mouth": return "集气瓶口";
                case "basin": return "水槽";
                case "liquid": return "液体槽（长颈漏斗）";
                case "solid": return "固体槽（锥形瓶）";
                case "fullness": return "验满项";
                case "identity": return "检验项";
                case "collector": return "收集装置";
                case "conduit": return "导管";
                case "reactor": return "发生装置";
                case "count": return "器材数量";
                case "unique": return "装置唯一性";
                case "feed": return "加料装置";
                default: return "相关位置";
            }
        }

        // 兜底：万一某处仍拼进了 R-RXN-04 / D-RXN-04 / [n1.inside] 这类码，统一抹掉
        private static readonly System.Text.RegularExpressions.Regex DisplayCodeRx =
            new System.Text.RegularExpressions.Regex(
                @"(?:[RDE]-[A-Z0-9]{2,4}-\d{2})|(?:\[[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string Humanize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = DisplayCodeRx.Replace(s, "");
            s = s.Replace("（）", "").Replace("()", "");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.Replace(" ·\n", "\n").Replace("· \n", "\n");
            return s.Trim();
        }

        private void SetDiag(string title, string body, string loc, Color? color = null)
        {
            if (_diagTitle == null) return;
            _diagTitle.text = Humanize(title);
            _diagTitle.color = color ?? new Color(0.2f, 0.2f, 0.24f);
            _diagBody.text = Humanize(body);
            _diagLoc.text = Humanize(loc);
        }

        // ---------------------------------------------------------------- 控件工厂（uGUI Text）
        private GameObject Panel(Transform parent, Vector2 pos, Vector2 size, Color color)
        {
            GameObject go = new GameObject("Panel");
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            Image img = go.AddComponent<Image>();
            img.color = color; img.raycastTarget = false;
            return go;
        }

        private Text MakeText(Transform parent, string text, Vector2 pos, Vector2 size,
            int fontSize, FontStyle style, Color color)
        {
            GameObject go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            Text t = go.AddComponent<Text>();
            if (_font != null) t.font = _font;
            t.text = text; t.fontSize = fontSize; t.fontStyle = style; t.color = color;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            return t;
        }

        private GameObject Card(Transform parent, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = new GameObject("Card");
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            Image img = go.AddComponent<Image>();
            img.color = C_CardOff;
            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            return go;
        }

        private Button MakeButton(Transform parent, string label, Vector2 pos, Vector2 size,
            UnityEngine.Events.UnityAction onClick, Color? bg = null)
        {
            GameObject go = new GameObject("Btn_" + label);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            Image img = go.AddComponent<Image>();
            img.color = bg ?? new Color(0.86f, 0.9f, 0.94f);
            Button btn = go.AddComponent<Button>();
            btn.onClick.AddListener(onClick);
            Text t = MakeText(go.transform, label, Vector2.zero, size, 15, FontStyle.Normal, new Color(0.1f, 0.12f, 0.16f));
            t.alignment = TextAnchor.MiddleCenter;
            return btn;
        }

        private void EnsureEventSystem()
        {
            if (UnityEngine.EventSystems.EventSystem.current != null) return;
            GameObject es = new GameObject("EventSystem");
            es.transform.SetParent(transform, false);
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // ---------------------------------------------------------------- 桥接可编程接口
        public void RefreshForBridge() { RefreshAll(); }
        public void BridgeClear() { ClearNow(); }
        public void BridgeSubmit() { OnSubmit(); }
        /// <summary>桥接：点击底部「检查 / 开始实验」按钮的状态机。</summary>
        public string BridgeAction() { OnAction(); return DebugUI() + " | " + DebugState(); }
        /// <summary>桥接：从 plan.json 恢复画布（3D 返回方案设计的同一条路径）。</summary>
        public string BridgeRestore(string planJson)
        {
            bool ok = RestoreFromPlanJson(planJson);
            return "restore=" + ok + " | " + DebugState() + " | " + DebugUI();
        }
        /// <summary>桥接：查询当前交接里的 plan.json（供恢复链路测试取数）。</summary>
        public string BridgeCurrentPlanJson()
        {
            Submission s;
            return SubmissionHandoff.TryLoad(out s) && s != null ? (s.PlanJson ?? "") : "";
        }
        /// <summary>桥接：走与按钮完全相同的门控和交接逻辑。</summary>
        public string BridgeEnter3D()
        {
            OnEnter3D();
            return _deviceCaption == null ? SubmissionHandoff.Describe() : _deviceCaption.text;
        }
        /// <summary>H2：与真实按钮走同一条 SetCollector，不再只改字段留下"数据切了、画布没切"的分叉。</summary>
        public void BridgeCollect()
        {
            SetCollector(State.CollectorMode == "water" ? "upright" : "water");
        }

        /// <summary>桥接：指定收集方式（upright/inverted/water），验证线稿朝向与水槽联动。</summary>
        public string BridgeCollectMode(string arg)
        {
            SetCollector(arg == "inverted" ? "inverted" : arg == "water" ? "water" : "upright");
            return DebugState();
        }
        public void BridgeTabReagent() { _reagentTab = true; RefreshAll(); }
        public void BridgeTabContainer() { _reagentTab = false; RefreshAll(); }
        public void BridgeSetReagent(string solid, string liquid)
        {
            // H1：未知 id 在写入前拦下，否则 RefreshSlots 会空引用，友好诊断来不及出现。
            // "-" = 显式清空该槽（脚本化复现"漏加某种试剂"），空段 = 不改
            if (!string.IsNullOrEmpty(solid) && solid != "-" && !ReagentDb.Contains(solid))
            {
                RefreshAll();
                SetCaption("未知的固体试剂：" + solid + "（已忽略，试剂未写入）");
                return;
            }
            if (!string.IsNullOrEmpty(liquid) && liquid != "-" && !ReagentDb.Contains(liquid))
            {
                RefreshAll();
                SetCaption("未知的液体试剂：" + liquid + "（已忽略，试剂未写入）");
                return;
            }
            if (solid == "-") State.SolidReagent = "";
            else if (!string.IsNullOrEmpty(solid)) State.SolidReagent = solid;
            if (liquid == "-") { State.LiquidReagent = ""; State.LiquidRoute = "funnel"; }
            else if (!string.IsNullOrEmpty(liquid)) { State.LiquidReagent = liquid; State.LiquidRoute = "direct"; }
            RefreshAll();
        }

        /// <summary>桥接：脚本化"把试剂拖到某个器材"。arg = "nodeId|reagentId"。</summary>
        public string BridgeDropReagent(string arg)
        {
            string[] p = (arg ?? "").Split('|');
            if (p.Length != 2) return "bad arg";
            Node n = State.FindNode(p[0]);
            if (n == null) return "no node " + p[0];
            ReagentDb.Reagent def = ReagentDb.Get(p[1]);
            if (def == null) return "unknown reagent " + p[1] + "（未写入，试剂槽保持不变）";
            bool isSolid = def.Phase == "固";
            ApplyReagentToNode(n, p[1], isSolid);
            return DebugState() + " | caption=" + (_deviceCaption == null ? "" : _deviceCaption.text);
        }

        // 供桥接脚本化验证交互：arg = "type" 放置；"nodeId|key" 点连接点
        public string BridgeDrop(string type) { SpawnDevice(type, DefaultPos(type)); return DebugState(); }
        public string BridgeAnchor(string arg)
        {
            string[] p = (arg ?? "").Split('|');
            if (p.Length == 2) OnAnchorClicked(p[0], p[1]);
            return DebugState();
        }

        /// <summary>桥接：开关导管连接模式。arg = "on" / "off" / ""（切换）。</summary>
        public string BridgeConduitMode(string arg)
        {
            if (arg == "on") SetConduitMode(true);
            else if (arg == "off") SetConduitMode(false);
            else SetConduitMode(!_conduitMode);
            return "mode=" + _conduitMode + " | " + DebugConduit();
        }

        /// <summary>桥接：在导管模式下依次点两个端口。arg = "nodeA|keyA|nodeB|keyB"。</summary>
        public string BridgeConduit(string arg)
        {
            string[] p = (arg ?? "").Split('|');
            if (p.Length != 4) return "bad arg";
            if (!_conduitMode) SetConduitMode(true);
            OnAnchorClicked(p[0], p[1]);
            OnAnchorClicked(p[2], p[3]);
            return "mode=" + _conduitMode + " | " + DebugConduit() + " | " + DebugState();
        }

        /// <summary>桥接：开关删除器材模式。arg = "on" / "off" / ""（切换）。</summary>
        public string BridgeDeleteMode(string arg)
        {
            if (arg == "on") SetDeleteMode(true);
            else if (arg == "off") SetDeleteMode(false);
            else SetDeleteMode(!_deleteMode);
            return "deleteMode=" + _deleteMode + " | " + DebugUI();
        }

        /// <summary>桥接：走与真实点击同一条路径删除某件器材。arg = nodeId。</summary>
        public string BridgeDelete(string nodeId)
        {
            if (!_deleteMode) SetDeleteMode(true);
            OnDeviceClicked(nodeId);
            return "deleteMode=" + _deleteMode + " | " + DebugState() + " | items=" + _items.Count +
                   " | caption=" + (_deviceCaption == null ? "" : _deviceCaption.text);
        }

        /// <summary>桥接：端口显隐摘要（排查"该出现的端口没出现"）。</summary>
        public string DebugPorts()
        {
            int total = _ports.Count, shown = 0, rings = 0, labels = 0;
            foreach (KeyValuePair<string, PortVis> kv in _ports)
            {
                PortVis p = kv.Value;
                if (p.Root == null || !p.Root.activeSelf) continue;
                shown++;
                if (p.Ring != null && p.Ring.color.a > 0.01f) rings++;
                if (p.Tag != null && p.Tag.activeSelf) labels++;
            }
            return "ports=" + total + " shown=" + shown + " rings=" + rings + " labels=" + labels +
                   " hover=" + (_hoverDeviceId ?? "-") + " active=" + (_activeDeviceId ?? "-") +
                   " hoverPort=" + (_hoverPortKey ?? "-") +
                   " mode=" + (_deleteMode ? "delete" : _conduitMode ? "conduit" : "idle");
        }

        /// <summary>桥接：列出某个器材登记了哪些端口（排查"点不到某个端口"）。</summary>
        public string DebugPortKeys(string nodeId)
        {
            List<PortVis> list;
            if (!_portsOfNode.TryGetValue(nodeId, out list)) return "no node " + nodeId;
            string s = nodeId + ":";
            foreach (PortVis p in list)
            {
                RectTransform rt = p.Root == null ? null : p.Root.transform as RectTransform;
                s += " " + p.Key + (rt == null ? "?" : "(" + rt.anchoredPosition.x.ToString("0") + "," +
                                     rt.anchoredPosition.y.ToString("0") + ")") + (p.LogicHidden ? "!" : "");
            }
            return s;
        }

        /// <summary>桥接：删除态下各器材的登记状态（排查残留引用：删完后 items 应为 0）。</summary>
        public string DebugDelete()
        {
            string s = "deleteMode=" + _deleteMode + " items=" + _items.Count +
                   " art=" + _deviceArt.Count + " stoppers=" + _stoppers.Count +
                   " asm=" + _funnelOfFlask.Count + " hints=" + _dropHints.Count +
                   " nodes=" + State.Nodes.Count + " edges=" + State.Edges.Count +
                   " tubes=" + _tubes.Count + " solid=" + State.SolidReagent + " liquid=" + State.LiquidReagent;
            // 画布上实际存在的器材实例（含已隐藏但还没被 Destroy 回收的），用来确认没有残留
            foreach (DeviceItem it in Resources.FindObjectsOfTypeAll<DeviceItem>())
            {
                if (it == null || it.gameObject.scene.IsValid() == false) continue;
                s += " <" + it.NodeId + " active=" + it.gameObject.activeSelf +
                     " dict=" + _items.ContainsKey(it.NodeId) + ">";
            }
            return s;
        }

        public string DebugConduit()
        {
            string s = "";
            foreach (Node n in State.Nodes)
            {
                if (n.Type != "conduit.bent") continue;
                Edge eIn = FindConduitEdge(n.Id, "in"), eOut = FindConduitEdge(n.Id, "out");
                DeviceItem it;
                bool itemVisible = _items.TryGetValue(n.Id, out it) && it != null && it.gameObject.activeSelf;
                s += n.Id + "{in=" + (eIn == null ? "-" : eIn.To) +
                     " out=" + (eOut == null ? "-" : eOut.To) +
                     " depth=" + (eOut == null ? "-" : eOut.Prop("depth", "shallow")) +
                     " tube=" + _tubes.ContainsKey(n.Id) + " icon=" + itemVisible + "};";
            }
            return s == "" ? "(no conduit)" : s;
        }
        public string BridgeMove(string arg)
        {
            string[] p = (arg ?? "").Split('|');
            DeviceItem item;
            if (p.Length == 3 && float.TryParse(p[1], out float x) && float.TryParse(p[2], out float y) &&
                _items.TryGetValue(p[0], out item))
            {
                ((RectTransform)item.transform).anchoredPosition = ClampDeviceFor(p[0], new Vector2(x, y));
                OnDeviceDragged(p[0]);   // 与真实拖动一致：整机跟随 / 吸附预览都会刷新
                RefreshLines();
            }
            return "moved " + arg;
        }
        /// <summary>桥接：模拟"把漏斗下端拖到容器端口上松手"。arg = "funnelId|flaskId|key"。</summary>
        public string BridgeInsert(string arg)
        {
            string[] p = (arg ?? "").Split('|');
            if (p.Length != 3) return "bad arg";
            string funnelId = p[0], flaskId = p[1], key = p[2];
            DeviceItem fi, bi;
            if (!_items.TryGetValue(funnelId, out fi) || !_items.TryGetValue(flaskId, out bi))
                return "no item";
            AnchorPoint ap = FindAnchorPoint(flaskId, key);
            if (ap == null) return "no anchor " + key;

            RectTransform frt = (RectTransform)fi.transform;
            RectTransform brt = (RectTransform)bi.transform;
            Vector2 portPos = brt.anchoredPosition + ((RectTransform)ap.transform).anchoredPosition;
            // 与真实拖拽一致：先"抓起"（已装配则先拆开），再把下端放到端口上松手
            OnDeviceDragBegin(funnelId);
            frt.anchoredPosition = ClampDevice(portPos - new Vector2(60f, 14f));
            OnDeviceDragged(funnelId);
            OnDeviceDragEnd(funnelId);
            return DebugAsm();
        }

        public string DebugTints()
        {
            string s = "";
            foreach (KeyValuePair<string, StrokeGraphic> kv in _deviceArt)
                if (kv.Value != null) s += kv.Key + "=" + kv.Value.color + ";";
            return s;
        }

        public string DebugAsm()
        {
            string s = "";
            foreach (KeyValuePair<string, string> kv in _funnelOfFlask)
            {
                Node n = State.FindNode(kv.Value);
                s += kv.Key + "<-" + kv.Value + " spot=" + _asmSpot[kv.Value] +
                     " submerged=" + (n != null && n.PropBool("submerged", false)) +
                     " pos=" + ((RectTransform)_items[kv.Value].transform).anchoredPosition + ";";
            }
            return s == "" ? "(no assembly)" : s;
        }

        /// <summary>桥接：直接装配（测试判定不受拖拽影响时用）。arg = "funnelId|flaskId|key"。</summary>
        public string BridgeAssemble(string arg)
        {
            string[] p = (arg ?? "").Split('|');
            if (p.Length != 3) return "bad arg";
            string err = AssembleFunnel(p[0], p[1], p[2]);
            return (string.IsNullOrEmpty(err) ? "ok" : err) + " | " + DebugAsm();
        }

        public string BridgeIds()
        {
            string s = "";
            foreach (Node node in State.Nodes) s += node.Id + "=" + node.Type + ";";
            return s;
        }

        /// <summary>桥接设置验满/检验，arg 可含 full / lime，用 | 分隔。</summary>
        public void BridgeVerify(string arg)
        {
            string[] p = (arg ?? "").Split('|');
            foreach (string k in p)
            {
                if (k == "full") State.Fullness = "burning_splint";
                else if (k == "full0") State.Fullness = "none";
                else if (k == "lime") State.Identity = "limewater";
                else if (k == "lime0") State.Identity = "none";
            }
            RefreshAll();
        }

        public string DebugState()
        {
            Plan p = State.BuildPlan();
            return "nodes=" + State.Nodes.Count + " edges=" + State.Edges.Count +
                   " solid=" + State.SolidReagent + " liquid=" + State.LiquidReagent +
                   " collector=" + State.CollectorMode + " fullness=" + p.Verification.Fullness;
        }

        public string DebugSubmit()
        {
            Outcome o = State.Submit();
            string loc = "";
            foreach (PortIssue pi in State.Locate(o)) loc += " [" + pi + "]";
            return "class=" + (o.Class ?? "-") + " verdict=" + o.Verdict + " issues=" + loc;
        }

        public string DebugUI()
        {
            return "diagTitle=[" + (_diagTitle == null ? "null" : _diagTitle.text) + "]" +
                   " slotA=[" + (_checkAText == null ? "null" : _checkAText.text) + "]" +
                   " collector=[" + (_collectorLabel == null ? "null" : _collectorLabel.text) + "]" +
                   " tab=" + (_reagentTab ? "reagent" : "container") +
                   " action=[" + (_btnActionLabel == null ? "null" : _btnActionLabel.text) + "]" +
                   " checked=" + _checkPassed +
                   " toast=" + (_rejectToast == null ? "null" : (_rejectToast.activeSelf ? "on" : "off")) +
                   " device=[" + (_deviceCaption == null ? "null" : _deviceCaption.text) + "]";
        }

        /// <summary>只读诊断：返回诊断卡三行文本，验证显示层无内部码泄漏（Editor 桥接用）。</summary>
        public string DebugDiagText()
        {
            return "title=[" + (_diagTitle == null ? "null" : _diagTitle.text) + "]" +
                   " body=[" + (_diagBody == null ? "null" : _diagBody.text) + "]" +
                   " loc=[" + (_diagLoc == null ? "null" : _diagLoc.text) + "]";
        }
    }
}
