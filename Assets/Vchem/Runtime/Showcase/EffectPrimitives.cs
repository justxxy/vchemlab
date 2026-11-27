using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Vchem.Assembly;
using Vchem.Authoring.Geometry;

namespace Vchem.Showcase
{
    /// <summary>WP5 演出注入的资产（Editor 侧从工程加载，Runtime 不碰 AssetDatabase）。</summary>
    public sealed class ShowcaseAssets
    {
        public Material LiquidBody;
        public Material LiquidSurface;
        public Material Bubble;
        public Material GlassInner;
        public Material Opaque;
        public Mesh Cylinder;
        public Mesh Quad;
        public Mesh Sphere;
        public Mesh TestTube;
    }

    /// <summary>
    /// 8 个现象基元的 Unity 实体层。所有位置来自 WP4 装配节点的 anchor，
    /// 所有强度来自 ShowcaseState；同一个 state 应用两次得到完全相同的 Transform。
    /// 演出对象挂在独立根节点 "M3 Showcase"，不污染 "M3 Assembly" 的位姿哈希。
    /// </summary>
    public sealed class EffectPrimitives
    {
        public const string RootName = "M3 Showcase";
        private const int BubbleCount = 16;

        private GameObject _root;
        private readonly Dictionary<string, GameObject> _groups =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly List<Transform> _bubbles = new List<Transform>();
        private readonly List<Material> _ownedMaterials = new List<Material>(); // H7：实例材质统一释放
        private Material _liquidBody, _liquidSurface, _bubble, _mist, _flame;
        private Material _limewaterBody, _troughWaterBody; // M2：水槽水位用独立材质，避免污染反应器液体参数
        private Material _splintWood; // 验满木条：浅米木色，不共用灰色器材材质
        private Material _marble;      // §10.27 大理石块：浅灰白，不共用灰色器材材质
        private Material _dipGlass;    // §10.27 漏斗浸入管玻璃
        private Material _flowBody;    // §10.27 加液液流：略亮，独立于锥形瓶液体
        private Material _stopperMat;  // §10.33 锥形瓶橡胶塞：浅米黄哑光，不共用灰色器材材质
        private Mesh _stopperMesh;     // §10.33 程序化截锥，Clear 时销毁
        private Vector3 _funnelBottom; // §10.27 漏斗下端世界坐标（液封管对齐用）
        private ShowcaseAssets _assets;

        public GameObject Root { get { return _root; } }

        public void Build(AssemblyResult asm, ProfileCatalog cat, ShowcaseAssets assets)
        {
            Clear();
            _assets = assets;
            _root = new GameObject(RootName);
            _liquidBody = Instance(assets.LiquidBody);
            _limewaterBody = Instance(assets.LiquidBody);
            _troughWaterBody = Instance(assets.LiquidBody);
            _liquidSurface = Instance(assets.LiquidSurface);
            // §10.27：稀盐酸是无色液体，但原 _WaterColor.a=0.10 在纯色台面前几乎不可见，
            // 画面只剩气泡。这里只调锥形瓶液体/液面实例（石灰水、水槽是各自独立的实例，不受影响）：
            // 近无色偏淡青灰 + alpha 0.30/0.38，液柱与液面清晰可辨又不失"清液"观感。
            if (_liquidBody.HasProperty("_WaterColor"))
                _liquidBody.SetColor("_WaterColor", new Color(0.62f, 0.66f, 0.66f, 0.30f));
            if (_liquidBody.HasProperty("_DeepColor"))
                _liquidBody.SetColor("_DeepColor", new Color(0.50f, 0.55f, 0.56f, 0.38f));
            if (_liquidBody.HasProperty("_Opacity")) _liquidBody.SetFloat("_Opacity", 1f);
            if (_liquidSurface.HasProperty("_SurfaceColor"))
                _liquidSurface.SetColor("_SurfaceColor", new Color(0.80f, 0.86f, 0.88f, 0.34f));
            if (_liquidSurface.HasProperty("_BandStrength")) _liquidSurface.SetFloat("_BandStrength", 0.22f);
            _bubble = Instance(assets.Bubble);
            // §P2 G3-2：气泡原先只在瓶底成簇。靠径向分布与尺寸让气泡流沿整段液柱可见；
            // CoreAlpha 反降到 0.22——shader 的 fresnel*0.7 负责亮边，中心透空才像气泡，
            // 0.35/0.55 会把气泡变成实心白球，盖住瓶底大理石（实测已验证）。
            if (_bubble.HasProperty("_CoreAlpha")) _bubble.SetFloat("_CoreAlpha", 0.22f);
            _mist = Billboard(new Color(0.88f, 0.90f, 0.92f, 0.34f), 0f, 0.70f);
            _flame = Billboard(new Color(1.0f, 0.48f, 0.12f, 0.92f), 1f, 0.45f);
            // 木条杆身用独立浅米木色实例（参考实拍）：Linear 下 R≈G≈B*1.1 的暖米色。
            _splintWood = Instance(assets.Opaque);
            if (_splintWood != null && _splintWood.HasProperty("_BaseColor"))
                _splintWood.SetColor("_BaseColor", new Color(0.92f, 0.85f, 0.68f, 1f));
            // §10.33 橡胶塞：参考实拍的浅米黄、哑光不透明（Linear 下略提亮才不发灰）。
            _stopperMat = Instance(assets.Opaque);
            if (_stopperMat.HasProperty("_BaseColor"))
                _stopperMat.SetColor("_BaseColor", new Color(0.93f, 0.87f, 0.71f, 1f));
            if (_stopperMat.HasProperty("_Smoothness")) _stopperMat.SetFloat("_Smoothness", 0.16f);

            // 发生器（锥形瓶）: 液体实体 + 液面 + 气泡 + 液流 + 白雾。
            AssemblyNode reactor = Find(asm, "reactor.flask_conical") ?? Find(asm, "reactor.test_tube");
            if (reactor != null)
            {
                float scale = reactor.Pose.Scale;
                float innerR = InnerRadius(reactor.Profile, 24f) * 0.001f; // 瓶身下部内腔半径（米）
                Vector3 bottom = reactor.AnchorWorld("bottom", Vector3.zero);
                float height = MaxHeight(reactor.Profile) * 0.001f;

                Group("liquid").AddChild("ReactorLiquid", _assets.Cylinder, _liquidBody);
                Group("surface").AddChild("ReactorSurface", _assets.Cylinder, _liquidSurface);
                // 液流用独立略亮的材质并加粗，避免被玻璃内壁高光淹没（加液阶段要看得见"往下流"）。
                _flowBody = Instance(assets.LiquidBody);
                if (_flowBody.HasProperty("_WaterColor"))
                    _flowBody.SetColor("_WaterColor", new Color(0.72f, 0.80f, 0.80f, 0.52f));
                if (_flowBody.HasProperty("_DeepColor"))
                    _flowBody.SetColor("_DeepColor", new Color(0.66f, 0.74f, 0.74f, 0.55f));
                Group("flow").AddChild("AcidStream", _assets.Cylinder, _flowBody);
                Group("mist").AddChild("MouthMist", _assets.Quad, _mist);

                // §10.27 液封：漏斗细颈末端（bottom anchor）停在瓶口 y=-0.888，
                // 需一段浸没在液面下的细管把漏斗下端延到液面以下，才构成液封（长颈漏斗
                // 下端必须浸入液面以下防止气体逸出）。该管从瓶口竖直下探到液面下约 12mm。
                // 用独立玻璃材质（复用 GlassInner，半透明），随 LiquidLevel 一起显示。
                _dipGlass = assets.GlassInner != null ? assets.GlassInner : _liquidSurface;
                Group("diptube").AddChild("FunnelDip", _assets.Cylinder, _dipGlass);

                // §10.27 大理石：瓶底几块灰白色不规则块（CaCO3），参考图浅灰白早餐块状。
                // 用低多边形球体压扁+旋转拼出不规则感，材质从 Opaque 克隆改浅灰白。
                // 全程静止，反应中只冒泡不动它。
                _marble = Instance(assets.Opaque);
                if (_marble.HasProperty("_BaseColor"))
                    _marble.SetColor("_BaseColor", new Color(0.90f, 0.88f, 0.83f, 1f)); // 浅暖灰白（Linear）
                if (_marble.HasProperty("_Smoothness")) _marble.SetFloat("_Smoothness", 0.10f);
                var marGroup = Group("marble");
                float[] mx = { -0.45f, 0.38f, 0.05f, -0.12f, 0.62f, -0.60f };
                float[] mz = { 0.20f, -0.35f, 0.55f, -0.55f, 0.28f, -0.05f };
                float[] ms = { 1.00f, 0.85f, 0.72f, 0.90f, 0.66f, 0.78f };
                for (int i = 0; i < mx.Length; i++)
                {
                    var m = marGroup.AddChild("Marble_" + i, _assets.Sphere, _marble);
                    m.transform.position = bottom + new Vector3(
                        mx[i] * innerR * 0.62f, 0.006f * ms[i], mz[i] * innerR * 0.62f);
                    m.transform.rotation = Quaternion.Euler(17f + i * 51f, i * 73f, 9f + i * 29f);
                    m.transform.localScale = new Vector3(
                        innerR * 0.46f * ms[i], innerR * 0.16f * ms[i], innerR * 0.30f * ms[i]);
                }

                var bubGroup = Group("bubbles");
                for (int i = 0; i < BubbleCount; i++)
                    _bubbles.Add(bubGroup.AddChild("Bubble_" + i.ToString("00"), _assets.Sphere, _bubble).transform);

                _reactorBottom = bottom;
                _reactorHeight = height;
                _reactorInnerR = innerR;
                _reactorScale = scale;
                _reactorProfile = reactor.Profile;

                // 漏斗下端：优先按 feed 边实际落点（reactor mouth + slot）定位液封管；
                // 找不到漏斗时用瓶口中心兜底，保证画面始终有液封管。
                AssemblyNode funnel = Find(asm, "feed.thistle_funnel");
                _funnelBottom = funnel != null
                    ? funnel.AnchorWorld("bottom", Vector3.zero)
                    : reactor.AnchorWorld("mouth", Vector3.zero);

                // §10.33 橡胶塞：只在锥形瓶拓扑创建。底半径 16.5mm 塞进瓶口（口内约 19mm），
                // 顶半径 ×1.30≈21.5mm 略压在口沿上；总高 20mm，露出约 13mm、插入约 7mm。
                // 漏斗颈与导管从塞中穿过：不透明实心塞靠深度遮挡自然呈现「管子插在塞子里」。
                if (reactor.Type == "reactor.flask_conical")
                {
                    Vector3 mouth = reactor.AnchorWorld("mouth", Vector3.zero);
                    _stopperMesh = MakeStopperMesh();
                    var stopper = Group("stopper").AddChild("ReactorStopper", _stopperMesh, _stopperMat);
                    stopper.transform.position = mouth + Vector3.up * 0.003f;
                    stopper.transform.localScale = new Vector3(0.0165f, 0.020f, 0.0165f);
                }
            }

            // 集气瓶：验满火焰（木条 + 火焰 billboard）+ 瓶口气泡（排水法）。
            AssemblyNode jar = Find(asm, "collect.gas_jar");
            if (jar != null)
            {
                Vector3 mouth = jar.AnchorWorld("mouth", Vector3.zero);
                Group("flame").AddChild("Splint", _assets.Cylinder, _splintWood);
                Group("flame").AddChild("Flame", _assets.Quad, _flame);
                var mb = Group("mouthbubble");
                for (int i = 0; i < 6; i++)
                    mb.AddChild("MouthBubble_" + i, _assets.Sphere, _bubble);
                _jarMouth = mouth;
            }

            // M2：排水法水槽水位。water.level 事件此前只进状态不进画面，
            // 现在由 basin anchor 定位、ShowcaseState.WaterLevel 驱动水面高度与可见性。
            AssemblyNode trough = Find(asm, "collect.water_trough");
            if (trough != null)
            {
                _troughBasin = trough.AnchorWorld("basin", Vector3.zero);
                Group("troughwater").AddChild("TroughWater", _assets.Cylinder, _troughWaterBody);
            }

            // 幕二检验位：一支固定石灰水试管（检验栏固定项，不占 plan 试剂槽）。
            // 放在装置前方偏右（+Z 朝相机），避免被集气瓶/导管遮挡；转接推镜后入镜。
            // 相机从 (+X,+Z) 看向 (-X,-Z)，屏幕右 = 世界 -X/+Z 组合。
            // 试管放在装置中间偏前的空位，推镜后落在画面中下部且不被集气瓶遮挡。
            Vector3 testPos = new Vector3(-0.02f, PlanAssembler.TableY, 0.0f);
            _testTubePos = testPos;
            var lime = Group("limewater");
            // 玻璃试管壳（车削试管网格或圆柱兜底），内装石灰水。
            Mesh tubeMesh = _assets.TestTube;
            if (tubeMesh != null)
            {
                var shell = lime.AddChild("TestTubeGlass", tubeMesh,
                    _assets.GlassInner != null ? _assets.GlassInner : _liquidSurface);
                shell.transform.position = testPos;
                shell.transform.localScale = Vector3.one * 0.12f; // 车削网格 1 单位高 = 120mm
            }
            else
            {
                var shell = lime.AddChild("TestTubeGlass", _assets.Cylinder, _liquidSurface);
                shell.transform.position = testPos + Vector3.up * 0.06f;
                shell.transform.localScale = new Vector3(0.016f, 0.06f, 0.016f);
            }
            lime.AddChild("TestTubeLiquid", _assets.Cylinder, _limewaterBody);
            lime.AddChild("TestTubeSurface", _assets.Cylinder, _liquidSurface);
        }

        private Vector3 _reactorBottom, _jarMouth, _testTubePos, _troughBasin;
        private float _reactorHeight, _reactorInnerR, _reactorScale;
        private VesselProfile _reactorProfile; // §10.28 液面半径须按液面高度处内轮廓插值，不能用瓶底最宽处
        private Transform _jarCover;
        private Vector3 _jarCoverHome;     // 盖片完全盖住时的 localPosition
        private Transform _conduit;        // 验满后要撤下的导管（左端不动、右端竖直上拔）
        private Vector3 _conduitHomePos;
        private Quaternion _conduitHomeRot;
        private Vector3 _conduitHomeScale;
        // §10.30 柔性撤管：克隆导管网格，按 submesh1（橡胶段）的 x 区间建立顶点权重。
        // 左玻璃权重 0（固定）、橡胶段平滑过渡、右玻璃权重 1（竖直上拔）。
        // 全程只改网格顶点，不改 Transform、不旋转，软管与两端玻璃管始终是一张连续网格。
        private Mesh _conduitWorkMesh;     // 克隆的可变形网格（不污染共享 asset）
        private Mesh _conduitSrcMesh;      // 原始共享网格（Clear 还原）
        private Vector3[] _conduitBaseVerts;
        private Vector3[] _conduitWorkVerts;
        private float[] _conduitLiftWeight;
        private float _conduitLiftLocal;   // 右端上拔量（网格局部单位）
        private float _conduitLastLift = -1f;
        private ShowcaseState _lastState; // 供 Diagnostics 读取当前收尾阶段

        public void Apply(ShowcaseState s)
        {
            _lastState = s;
            bool stage2 = s.Stage == "2";
            float liqH = Mathf.Max(0.001f, s.LiquidLevel * _reactorHeight);
            // §10.28 锥形瓶下宽上窄：液面盘/液柱半径必须取「液面高度处」的内半径。
            // 旧代码用瓶底最宽处（y=24mm 内取最大 r≈40→内腔≈38mm），但液面在 47mm 高处
            // 内腔仅≈27mm，液面盘半径 38×0.88≈33mm > 27mm → 两侧穿出瓶壁（用户反馈"液面穿模"）。
            // 这里按液面高度在 profile 内轮廓上线性插值，留 4% 余量避免贴壁 z-fighting。
            float liqR = Mathf.Max(0.002f, InnerRadiusAtY(_reactorProfile, liqH * 1000f) * 0.001f * 0.96f);

            // 内置 Cylinder 网格 bounds=(2,2,2)，即半径 1.0、高 2：
            // 故 x/z scale 直接等于「世界半径」，y scale = 世界高 × 0.5。
            // 旧代码误按半径 0.5 算，传 liqR*2，liqu液柱/液面实际半径翻一倍，
            // 液面盘比锥形瓶底还宽，透过玻璃在瓶底外形成一圈浅色圆盘（用户反馈的"一滩水"）。
            GameObject liquidGroup = GroupOrNull("liquid");
            if (liquidGroup != null)
                SetCylinder(liquidGroup, _reactorBottom + Vector3.up * liqH * 0.5f,
                    new Vector3(liqR, liqH * 0.5f, liqR), s.LiquidLevel > 0.01f && !stage2);
            GameObject surfaceGroup = GroupOrNull("surface");
            if (surfaceGroup != null)
                SetCylinder(surfaceGroup, _reactorBottom + Vector3.up * liqH,
                    new Vector3(liqR * 0.88f, 0.002f, liqR * 0.88f), s.LiquidLevel > 0.01f && !stage2);
            _liquidSurface.SetFloat("_Radius", 0.5f);

            // §10.27 液封浸入管：从漏斗下端（瓶口 y≈-0.888）竖直下探到液面以下 12mm。
            // 漏斗细颈末端本身在瓶口，这一段把它延伸到液面之下，形成长颈漏斗液封。
            // 液位从 t=1.1 起一次到位（0.42），反应中不再变化，故管长在幕一恒定。
            GameObject dipGroup = GroupOrNull("diptube");
            if (dipGroup != null)
            {
                float dipTop = _funnelBottom.y;
                float dipBot = _reactorBottom.y + liqH - 0.012f;
                float dipLen = Mathf.Max(0.002f, (dipTop - dipBot) * 0.5f);
                SetCylinder(dipGroup, new Vector3(_funnelBottom.x, (dipTop + dipBot) * 0.5f, _funnelBottom.z),
                    new Vector3(0.0032f, dipLen, 0.0032f), s.LiquidLevel > 0.01f && !stage2);
            }

            // 大理石全程静止（幕一常在）；反应中只冒泡，不移动石块与液面。
            GameObject marbleGroup = GroupOrNull("marble");
            if (marbleGroup != null) marbleGroup.SetActive(!stage2);

            // 液流：从漏斗口到液面的一条细柱（加料阶段）。
            Vector3 flowTop = _reactorBottom + Vector3.up * (_reactorHeight * 1.05f);
            Vector3 flowBot = _reactorBottom + Vector3.up * liqH;
            Vector3 mid = (flowTop + flowBot) * 0.5f;
            float flowLen = Mathf.Max(0.01f, (flowTop.y - flowBot.y) * 0.5f);
            GameObject flowGroup = GroupOrNull("flow");
            if (flowGroup != null)
                SetCylinder(flowGroup, mid, new Vector3(0.0042f, flowLen, 0.0042f), s.FeedFlow > 0.02f);

            // 气泡：固液界面附近，半径/相位由 t 与索引确定性推出。
            for (int i = 0; i < _bubbles.Count; i++)
            {
                float phase = Mathf.Repeat(s.T * (0.16f + 0.05f * (i % 3)) + i * 0.618f, 1f);
                float a = i * 2.39996f;
                float rad = liqR * 0.72f * (0.30f + 0.70f * ((i % 5) / 4f));
                float y = _reactorBottom.y + Mathf.Max(0.004f, liqH * 0.08f) + phase * liqH * 0.90f;
                Transform b = _bubbles[i];
                b.position = new Vector3(_reactorBottom.x + Mathf.Cos(a) * rad, y,
                    _reactorBottom.z + Mathf.Sin(a) * rad);
                float dia = (0.0030f + 0.0024f * ((i % 4) / 3f)) * (0.75f + s.Bubbles * 0.65f);
                b.localScale = Vector3.one * dia;
                b.gameObject.SetActive(s.Bubbles > 0.02f && phase < 0.96f);
            }

            // 白雾：瓶口 billboard，向下扩散（浓盐酸拓扑）。
            GameObject mist = GroupOrNull("mist");
            if (mist != null) mist.SetActive(s.WhiteMist > 0.02f);
            if (mist != null && mist.activeSelf)
            {
                Transform m = mist.transform.GetChild(0);
                m.position = _reactorBottom + Vector3.up * (_reactorHeight * 1.02f - 0.01f * s.WhiteMist);
                m.localScale = new Vector3(0.09f + 0.03f * s.WhiteMist, 0.05f, 1f);
                FaceCamera(m);
                _mist.color = new Color(0.88f, 0.90f, 0.92f, 0.18f + 0.22f * s.WhiteMist);
            }

            // 验满：火焰只在 burning/extinguishing 可见；木条熄灭后保留到竖直上撤完成。
            bool flameVisible = s.Flame == "burning" || s.Flame == "extinguishing";
            bool splintVisible = flameVisible || (s.Flame == "extinguished" && s.FlameWithdraw < 1f);
            GameObject flameGroup = GroupOrNull("flame");
            if (flameGroup == null) { flameVisible = false; splintVisible = false; }
            else flameGroup.SetActive(splintVisible);
            if (splintVisible)
            {
                // §10.29 木条竖直倒持：燃烧端（下端）贴近瓶口，主体向上延伸（参考实拍）。
                // 内置 Cylinder pivot 在中心、scale.y=半高，故木条中心 = 燃烧端 + 半长。
                const float halfLen = 0.045f;          // 木条半长（m），全长 90mm
                // §10.39：从瓶口上方 85mm 竖直下探到 11mm；熄灭后只沿 Y 轴上撤，X/Z 恒定。
                float tipY = _jarMouth.y + Mathf.Lerp(0.085f, 0.011f, s.FlameApproach)
                                         + 0.16f * s.FlameWithdraw;
                float sx = _jarMouth.x - 0.010f; // 横向位置恒定，全程不做水平抖动
                Transform splint = flameGroup.transform.Find("Splint");
                splint.position = new Vector3(sx, tipY + halfLen, _jarMouth.z);
                splint.rotation = Quaternion.identity; // 竖直，不倾斜
                splint.localScale = new Vector3(0.0035f, halfLen, 0.0035f);
                Transform fl = flameGroup.transform.Find("Flame");
                if (fl != null) fl.gameObject.SetActive(flameVisible);
                if (flameVisible && fl != null)
                {
                    fl.position = new Vector3(sx, tipY + 0.004f, _jarMouth.z);
                    // §10.40：用状态里的 FlameFade，不再硬编码旧检验时刻 21.9s。
                    float fsz = Mathf.Max(0.01f, 0.030f * s.FlameFade);
                    fl.localScale = new Vector3(fsz * 0.7f, fsz * 1.6f, 1f);
                    // H8：把模拟时间写进材质，shader 用 _ShowcaseT 取代 _Time.y，保证同 t 像素一致。
                    if (_flame != null && _flame.HasProperty("_ShowcaseT"))
                        _flame.SetFloat("_ShowcaseT", s.T);
                    FaceCamera(fl);
                }
            }

            // 瓶口气泡（排水法）。
            GameObject mbGroup = GroupOrNull("mouthbubble");
            if (mbGroup != null) mbGroup.SetActive(s.MouthBubble > 0.02f);
            if (mbGroup != null && mbGroup.activeSelf)
                for (int i = 0; i < mbGroup.transform.childCount; i++)
                {
                    Transform b = mbGroup.transform.GetChild(i);
                    float phase = Mathf.Repeat(s.T * 0.22f + i * 0.37f, 1f);
                    float a = i * 1.9f;
                    b.position = _jarMouth + new Vector3(Mathf.Cos(a) * 0.010f,
                        -phase * 0.020f, Mathf.Sin(a) * 0.010f);
                    b.localScale = Vector3.one * (0.002f + 0.0015f * (i % 3));
                }

            // M2：水槽水位。合成轴标称 0.60 对应 basin anchor 高度，
            // 事件改 fraction 时水面随之升降；无 water.level 事件时保持隐藏。
            GameObject troughGroup = GroupOrNull("troughwater");
            if (troughGroup != null)
            {
                float wy = _troughBasin.y + (s.WaterLevel - 0.60f) * 0.06f;
                SetCylinder(troughGroup,
                    new Vector3(_troughBasin.x, wy, _troughBasin.z),
                    new Vector3(0.096f, 0.0015f, 0.066f), s.WaterLevel > 0.01f);
            }

            // 熄灭后收尾三段（§10.25）：
            //  A 玻璃片从屏幕右端（世界 -X）滑入到贴住导管处；
            //  B 只把集气瓶端玻璃管竖直上拔，橡胶管柔性弯曲、左端（塞子侧）不动（§10.30）；
            //  C 玻璃片继续滑到瓶口中心，完全盖住。
            // 木条熄灭前瓶口敞开（向上排空气法），熄灭瞬间才开始 A。
            if (_root != null && (_jarCover == null || _conduit == null))
            {
                var asm = GameObject.Find("M3 Assembly");
                if (asm != null)
                {
                    // 不硬编码节点 id：Editor golden 是 n4、Player default.plan 是 n2，按类型名匹配。
                    foreach (Transform child in asm.transform)
                    {
                        if (child.name.Contains("collect_gas_jar") && _jarCover == null)
                        {
                            _jarCover = child.Find("Cover");
                            if (_jarCover != null) _jarCoverHome = _jarCover.localPosition;
                        }
                        else if (child.name.Contains("conduit_bent") && _conduit == null)
                        {
                            _conduit = child;
                            _conduitHomePos = child.position;
                            _conduitHomeRot = child.rotation;
                            _conduitHomeScale = child.localScale;
                            InitConduitDeform(child);
                        }
                    }
                }
            }

            // 盖片先"停在导管旁"再"封口"：用两级 localPosition 偏移，全程在瓶口平面滑动。
            // 盖片 home 是居中坐标；A 结束时应停在瓶口右侧一点（导管在左端已让出位置），
            // 故 A 从右外侧(-X)滑到略偏右；C 再滑回 home 完全居中。
            if (_jarCover != null)
            {
                bool show = s.CoverSlide > 0.001f;
                _jarCover.gameObject.SetActive(show);
                if (show)
                {
                    float slide = s.CoverSlide, seal = s.CoverSeal;
                    // 右侧起点偏移（米）→ local 需除以 jar 的 Pose.Scale(0.13)。
                    float outside = -0.075f; // A 起点：瓶口外侧 75mm
                    float park = -0.034f;    // A 终点：贴住导管，尚留左侧瓶口露出
                    float x = Mathf.Lerp(outside, park, slide);
                    x = Mathf.Lerp(x, 0f, seal);
                    Vector3 p = _jarCoverHome;
                    p.x += x / 0.13f;
                    _jarCover.localPosition = p;
                }
            }

            // §10.30 柔性撤管：B 段只把「集气瓶端」的玻璃管竖直上拔离开瓶口。
            // 左端（橡胶塞侧）玻璃管保持原位不动；中段软橡胶管随两端高度差自然弯曲，
            // 且与两侧玻璃管同属一张连续网格，始终相连、不会断开。全程不改 Transform、
            // 不旋转，因此不会出现整根导管飞出装置、穿过瓶壁或撤出后绕轴空转的旧问题。
            if (_conduit != null)
            {
                float w = s.ConduitWithdraw;
                float ease = w * w * (3f - 2f * w); // smoothstep：起停平缓
                ApplyConduitWithdraw(ease);
            }

            // 石灰水：2026-11-23 暂撤演示表现。试管常隐，浑浊度不再驱动渲染；
            // 时间轴的 turbidity 事件与 ShowcaseState.Turbidity 仍保留（G3 断言与恢复路径依赖）。
            // 恢复时把下面的 false 改回 stage2 即可。
            GameObject lime = _groups["limewater"];
            lime.SetActive(false);
            _limewaterBody.SetFloat("_Turbidity", 0f);
            if (false && stage2)
            {
                float lh = 0.052f;
                SetCylinder(lime.transform.Find("TestTubeLiquid"), _testTubePos + Vector3.up * lh * 0.5f,
                    new Vector3(0.014f, lh * 0.5f, 0.014f), true);
                SetCylinder(lime.transform.Find("TestTubeSurface"), _testTubePos + Vector3.up * lh,
                    new Vector3(0.014f, 0.002f, 0.014f), true);
                _limewaterBody.SetFloat("_Turbidity", s.Turbidity);
                _limewaterBody.SetFloat("_LiquidTopY", _testTubePos.y + lh);
                _limewaterBody.SetFloat("_DepthSpan", 0.05f);
                // 石灰水浑浊必须肉眼可辨：清→乳白，alpha 随 t 显著抬升。
                // 试管内是实心液体柱，用不透明度表达浑浊比依赖 opaque 背景模糊更稳。
                _limewaterBody.SetColor("_WaterColor", new Color(0.78f, 0.84f, 0.86f, 0.18f));
                _limewaterBody.SetColor("_TurbidColor", new Color(0.97f, 0.97f, 0.95f, 0.25f + 0.72f * s.Turbidity));
                _limewaterBody.SetFloat("_Opacity", 1f);
            }
        }

        /// <summary>基元可见性 + 关键量的确定性快照，供 G3 断言。</summary>
        public string Diagnostics()
        {
            var sb = new StringBuilder();
            foreach (string key in new[] { "liquid", "surface", "flow", "diptube", "marble", "stopper", "bubbles", "mist", "flame", "mouthbubble", "troughwater", "limewater" })
            {
                GameObject g;
                if (!_groups.TryGetValue(key, out g)) { sb.Append(key).Append("=none;"); continue; }
                Transform t = g.transform.GetChild(0);
                sb.Append(key).Append('=')
                  .Append(g.activeSelf ? "on" : "off").Append('@')
                  .Append(t.position.x.ToString("F4")).Append(',')
                  .Append(t.position.y.ToString("F4")).Append(',')
                  .Append(t.position.z.ToString("F4")).Append(';');
            }
            sb.Append("turb=").Append(_limewaterBody != null ? _limewaterBody.GetFloat("_Turbidity").ToString("F3") : "-");
            // 盖片：none=无对象 / off=瓶口敞开 / park=滑到导管旁 / on=完全盖住。
            // 导管：in=在位 / out=已撤下。
            if (_jarCover != null)
            {
                string cs = !_jarCover.gameObject.activeSelf ? "off"
                          : (_lastState != null && _lastState.CoverSeal > 0.99f ? "on" : "park");
                sb.Append(";cover=").Append(cs);
            }
            else sb.Append(";cover=none");
            // §10.30：导管不再整体隐藏，tube=out 表示集气瓶端已竖直拔出。
            sb.Append(";tube=").Append(_conduit == null ? "none"
                : (_lastState != null && _lastState.ConduitWithdraw >= 0.995f ? "out" : "in"));
            return sb.ToString();
        }

        public void Clear()
        {
            // 销毁演出时把盖片归位到"初始未盖上"状态（排空气收集阶段瓶口敞开），
            // 与 Apply 的无验满态一致，避免下一轮 Prepare 前残留已盖片。
            if (_jarCover != null) _jarCover.gameObject.SetActive(false);
            _jarCover = null;
            // 导管归还共享网格并复位到未变形状态，避免 Seek 回退/下一轮 Prepare 残留撤管位。
            if (_conduit != null)
            {
                _conduit.gameObject.SetActive(true);
                var mf = _conduit.GetComponent<MeshFilter>();
                if (mf != null && _conduitSrcMesh != null) mf.sharedMesh = _conduitSrcMesh;
                _conduit.position = _conduitHomePos;
                _conduit.rotation = _conduitHomeRot;
                _conduit.localScale = _conduitHomeScale;
            }
            _conduit = null;
            if (_conduitWorkMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(_conduitWorkMesh);
                _conduitWorkMesh = null;
            }
            if (_stopperMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(_stopperMesh);
                _stopperMesh = null;
            }
            _stopperMat = null;
            _conduitSrcMesh = null;
            _conduitBaseVerts = _conduitWorkVerts = null;
            _conduitLiftWeight = null;
            _conduitLastLift = -1f;
            _lastState = null;
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
            _groups.Clear();
            _bubbles.Clear();
            // H7：Build() 会先 Clear()，这里释放上一轮的实例材质，避免重复 Prepare 持续泄漏。
            foreach (Material m in _ownedMaterials)
                if (m != null) UnityEngine.Object.DestroyImmediate(m);
            _ownedMaterials.Clear();
            _liquidBody = _liquidSurface = _bubble = _mist = _flame = null;
            _limewaterBody = _troughWaterBody = null;
            _marble = _dipGlass = _flowBody = null;
        }

        // ---- helpers ------------------------------------------------------
        /// <summary>
        /// §10.33：单位截锥（底 r=1、顶 r=1.30、高 1，中心在原点，y∈[-0.5,0.5]）。
        /// scale=(底半径, 总高, 底半径) 后即橡胶塞；侧面与上下盖用独立顶点，法线互不拉偏。
        /// </summary>
        private static Mesh MakeStopperMesh()
        {
            const int seg = 32;
            const float topR = 1.30f;
            var v = new List<Vector3>();
            var tri = new List<int>();
            // 侧面两圈：底 r=1 / 顶 r=1.30，绕序使法线朝外。
            for (int i = 0; i <= seg; i++)
            {
                float a = 2f * Mathf.PI * i / seg;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                v.Add(new Vector3(c, -0.5f, s));
                v.Add(new Vector3(c * topR, 0.5f, s * topR));
            }
            for (int i = 0; i < seg; i++)
            {
                int b0 = i * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
                tri.Add(b0); tri.Add(t0); tri.Add(b1);
                tri.Add(t0); tri.Add(t1); tri.Add(b1);
            }
            // 顶盖（法线 +Y）：反向绕序。
            int tc = v.Count; v.Add(new Vector3(0f, 0.5f, 0f));
            int tr = v.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = 2f * Mathf.PI * i / seg;
                v.Add(new Vector3(Mathf.Cos(a) * topR, 0.5f, Mathf.Sin(a) * topR));
            }
            for (int i = 0; i < seg; i++) { tri.Add(tc); tri.Add(tr + i + 1); tri.Add(tr + i); }
            // 底盖（法线 -Y）：正向绕序。
            int bc = v.Count; v.Add(new Vector3(0f, -0.5f, 0f));
            int br = v.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = 2f * Mathf.PI * i / seg;
                v.Add(new Vector3(Mathf.Cos(a), -0.5f, Mathf.Sin(a)));
            }
            for (int i = 0; i < seg; i++) { tri.Add(bc); tri.Add(br + i); tri.Add(br + i + 1); }
            var mesh = new Mesh { name = "FX_StopperFrustum" };
            mesh.SetVertices(v);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// §10.30：克隆导管网格，按 submesh1（中段软橡胶套管）的 x 区间建立顶点上拔权重。
        /// x≤rubberMin 的左玻璃权重 0（固定）；x≥rubberMax 的右玻璃+集气瓶插管权重 1；
        /// 橡胶段用 smoothstep 渐变——软管左端留在塞子上、右端随玻璃管抬起，
        /// 一张连续网格天然保持两端连接，不会断开也不会穿模。
        /// </summary>
        private void InitConduitDeform(Transform t)
        {
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            Mesh src = mf.sharedMesh;
            Mesh work = UnityEngine.Object.Instantiate(src);
            work.name = src.name + "_WithdrawWork";
            _conduitSrcMesh = src;
            _conduitWorkMesh = work;
            mf.sharedMesh = work;

            Vector3[] verts = work.vertices;
            _conduitBaseVerts = verts;
            _conduitWorkVerts = (Vector3[])verts.Clone();
            _conduitLiftWeight = new float[verts.Length];

            float rmin = float.MaxValue, rmax = float.MinValue;
            if (src.subMeshCount > 1)
            {
                int[] rubberTris = src.GetTriangles(1);
                if (rubberTris != null && rubberTris.Length > 0)
                {
                    var used = new bool[verts.Length];
                    for (int i = 0; i < rubberTris.Length; i++)
                        if (rubberTris[i] >= 0 && rubberTris[i] < used.Length) used[rubberTris[i]] = true;
                    for (int i = 0; i < verts.Length; i++)
                        if (used[i])
                        {
                            if (verts[i].x < rmin) rmin = verts[i].x;
                            if (verts[i].x > rmax) rmax = verts[i].x;
                        }
                }
            }
            if (rmin > rmax) // 兜底：没有橡胶 submesh 时按 x 归一化取中段
            {
                float xmin = float.MaxValue, xmax = float.MinValue;
                foreach (Vector3 v in verts) { if (v.x < xmin) xmin = v.x; if (v.x > xmax) xmax = v.x; }
                rmin = Mathf.Lerp(xmin, xmax, 0.30f);
                rmax = Mathf.Lerp(xmin, xmax, 0.80f);
            }
            float span = Mathf.Max(1e-5f, rmax - rmin);
            // 相机从 (+X,+Z) 看向 (-X,-Z)，世界 -X ≈ 屏幕右：集气瓶在导管起点的 -X 侧，
            // 而网格局部 x 沿路径从发生瓶端(0)走向集气瓶端(dx<0)是递减的。按集气瓶所在侧
            // 翻转权重，保证「靠集气瓶的玻璃管权重 1、靠发生瓶的权重 0」，不能假设 x 必递增。
            bool jarAtMinusX = _jarMouth != Vector3.zero && _jarMouth.x < t.position.x;
            for (int i = 0; i < verts.Length; i++)
            {
                float u = Mathf.Clamp01((verts[i].x - rmin) / span);
                if (jarAtMinusX) u = 1f - u;
                _conduitLiftWeight[i] = u * u * (3f - 2f * u);
            }

            // 上拔量（§10.38）：瓶内玻璃管已加长到接近瓶底，必须把"最深的那一段"
            // 整体提到瓶口以上，而不是只提瓶口附近那 12mm——否则撤管后长管仍插在瓶里。
            // 取集气瓶侧（权重>0.9）顶点中世界 y 最低者作为管口末梢。
            float outWorldY = t.position.y;
            if (_jarMouth != Vector3.zero)
            {
                float lowest = float.MaxValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    if (_conduitLiftWeight[i] < 0.9f) continue;
                    float y = t.TransformPoint(verts[i]).y;
                    if (y < lowest) lowest = y;
                }
                if (lowest < float.MaxValue) outWorldY = lowest;
            }
            float scaleY = Mathf.Max(1e-5f, t.lossyScale.y);
            // 瓶内长管：提到管口高过瓶口 ~12mm；下限 14mm 兜底短管/无瓶口情形。
            float liftWorld = _jarMouth == Vector3.zero ? 0.022f
                : Mathf.Max(0.014f, _jarMouth.y + 0.012f - outWorldY);
            _conduitLiftLocal = liftWorld / scaleY;
        }

        /// <summary>按进度把导管右端沿网格 +Y 上拔；同一进度得到同一组顶点，Seek 可回退。</summary>
        private void ApplyConduitWithdraw(float progress)
        {
            if (_conduitWorkMesh == null || _conduitBaseVerts == null) return;
            float lift = _conduitLiftLocal * progress;
            if (Mathf.Abs(lift - _conduitLastLift) < 1e-6f) return;
            _conduitLastLift = lift;
            for (int i = 0; i < _conduitWorkVerts.Length; i++)
                _conduitWorkVerts[i] = _conduitBaseVerts[i] + Vector3.up * (lift * _conduitLiftWeight[i]);
            _conduitWorkMesh.vertices = _conduitWorkVerts;
            _conduitWorkMesh.RecalculateNormals();
            _conduitWorkMesh.RecalculateBounds();
        }

        /// <summary>组可能因为提交方案缺件而不存在；调用方必须按 null 跳过。</summary>
        private GameObject GroupOrNull(string key)
        {
            GameObject g;
            return _groups.TryGetValue(key, out g) ? g : null;
        }

        private GroupHandle Group(string key)
        {
            GameObject g;
            if (!_groups.TryGetValue(key, out g))
            {
                g = new GameObject("FX_" + key);
                g.transform.SetParent(_root.transform, false);
                _groups[key] = g;
            }
            return new GroupHandle(g, _assets);
        }

        private struct GroupHandle
        {
            private readonly GameObject _go;
            private readonly ShowcaseAssets _a;
            public GroupHandle(GameObject go, ShowcaseAssets a) { _go = go; _a = a; }
            public GameObject gameObject { get { return _go; } }
            public GameObject AddChild(string name, Mesh mesh, Material mat)
            {
                var go = new GameObject(name);
                go.transform.SetParent(_go.transform, false);
                if (mesh != null) go.AddComponent<MeshFilter>().sharedMesh = mesh;
                if (mat != null) go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                return go;
            }
        }

        private static void SetCylinder(Transform t, Vector3 pos, Vector3 scale, bool active)
        {
            if (t == null) return;
            t.position = pos; t.localScale = scale; t.gameObject.SetActive(active);
        }
        private static void SetCylinder(GameObject g, Vector3 pos, Vector3 scale, bool active)
        {
            if (g != null) SetCylinder(g.transform, pos, scale, active);
        }
        private static void FaceCamera(Transform t)
        {
            if (Camera.main == null) return;
            Vector3 dir = (t.position - Camera.main.transform.position).normalized;
            t.rotation = Quaternion.LookRotation(dir);
        }
        private static AssemblyNode Find(AssemblyResult asm, string type)
        {
            if (asm == null) return null;
            foreach (AssemblyNode n in asm.Nodes) if (n.Type == type) return n;
            return null;
        }
        private static float InnerRadius(VesselProfile p, float yMm)
        {
            if (p == null || p.profile.Count == 0) return 30f;
            float best = 0f;
            for (int i = 0; i < p.profile.Count; i++)
                if (p.profile[i].y <= yMm) best = Mathf.Max(best, p.profile[i].r);
            return Mathf.Max(4f, best - (p != null ? p.wall : 0f));
        }
        /// <summary>§10.28：按液面高度在内轮廓上线性插值外半径再减壁厚。
        /// 与 InnerRadius（取 y≤yMm 的最大半径，适用于下宽上窄的"最宽包络"）不同，
        /// 本函数返回「该高度真实半径」，用于液体/液面盘避免穿出斜壁。</summary>
        private static float InnerRadiusAtY(VesselProfile p, float yMm)
        {
            if (p == null || p.profile.Count == 0) return 30f;
            var pts = p.profile;
            // 找包含 yMm 的区间（profile 点按 y 升序），线性插值出「该高度」真实半径。
            // 注意：不能像 InnerRadius 那样对 y≤yMm 取 max——那会恒定返回瓶底最宽处，
            // 对下宽上窄的锥形瓶液面必然穿壁。水平底盘段（y0==y1）取该段最大 r。
            for (int i = 0; i < pts.Count - 1; i++)
            {
                float y0 = pts[i].y, r0 = pts[i].r, y1 = pts[i + 1].y, r1 = pts[i + 1].r;
                if (Mathf.Abs(y1 - y0) < 0.001f)
                {
                    if (yMm >= y0 - 0.001f && yMm <= y0 + 0.001f) return Mathf.Max(r0, r1);
                    continue;
                }
                float lo = Mathf.Min(y0, y1), hi = Mathf.Max(y0, y1);
                if (yMm >= lo && yMm <= hi)
                {
                    float t = (yMm - lo) / (hi - lo);
                    float ra = y0 <= y1 ? r0 : r1;
                    float rb = y0 <= y1 ? r1 : r0;
                    return Mathf.Lerp(ra, rb, t);
                }
            }
            // 越界兜底：高于全部分辨率取末端 r，低于取首个非零 r。
            float fallback = pts[pts.Count - 1].r;
            foreach (var pt in pts) if (pt.r > 0f) { fallback = pt.r; break; }
            return fallback;
        }
        private static float MaxHeight(VesselProfile p)
        {
            float h = 1f;
            if (p != null) foreach (var pt in p.profile) h = Mathf.Max(h, pt.y);
            return h;
        }
        private Material Instance(Material src)
        {
            Material m = src != null ? new Material(src) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            _ownedMaterials.Add(m);
            return m;
        }
        private Material Billboard(Color c, float mode, float softness)
        {
            Shader sh = Shader.Find("Vchem/Fx/Billboard");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            var m = new Material(sh);
            _ownedMaterials.Add(m);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Mode")) m.SetFloat("_Mode", mode);
            if (m.HasProperty("_Softness")) m.SetFloat("_Softness", softness);
            if (m.HasProperty("_Flicker")) m.SetFloat("_Flicker", mode);
            if (m.HasProperty("_ShowcaseT")) m.SetFloat("_ShowcaseT", 0f);
            return m;
        }
    }
}
