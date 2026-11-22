using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Vchem.Authoring.Geometry;
using Vchem.Core.Model;

namespace Vchem.Assembly
{
    /// <summary>
    /// WP4 装配器：plan.json → 确定性位姿 → GameObject。
    /// 全部位置来自 profile anchor + 连接规则；没有针对单张拓扑的 if 分支。
    /// </summary>
    public static class PlanAssembler
    {
        /// <summary>M0/M3 场景里台面 Plane 的上表面 y = -1.0。</summary>
        public const float TableY = -1.0f;
        private const float GapMm = 42f;

        public static AssemblyResult Build(Plan plan, ProfileCatalog catalog, AssemblyAssets assets = null)
        {
            var result = new AssemblyResult();
            if (plan == null || plan.Topology == null) return result;
            if (catalog == null) catalog = ProfileCatalog.LoadDefault();
            if (assets == null) assets = new AssemblyAssets();

            var byId = new Dictionary<string, AssemblyNode>(StringComparer.Ordinal);
            foreach (Node n in plan.Topology.Nodes)
            {
                VesselProfile p = catalog.Get(n.Type);
                if (p == null)
                {
                    result.Errors.Add("no profile for " + n.Type + " (node " + n.Id + ")");
                    Debug.LogWarning("[Vchem.Assembly] no profile for " + n.Type);
                    continue;
                }
                if (byId.ContainsKey(n.Id))
                {
                    result.Errors.Add("duplicate node id " + n.Id);
                    continue;
                }
                var an = new AssemblyNode
                {
                    Id = n.Id,
                    Type = n.Type,
                    Model = n,
                    Profile = p,
                    Pose = new AssemblyPose
                    {
                        Rotation = Quaternion.identity,
                        Scale = catalog.WorldScale(p)
                    }
                };
                result.Nodes.Add(an);
                byId[an.Id] = an;
            }

            var waterJars = new HashSet<string>(StringComparer.Ordinal);
            var jarTrough = new Dictionary<string, string>(StringComparer.Ordinal);
            PreScanWater(plan, byId, waterJars, jarTrough);

            PlaceGrounded(plan, result, byId, catalog, waterJars, jarTrough);
            PlaceFunnels(plan, byId);
            PlaceConduits(plan, byId, catalog);
            ValidatePorts(plan, result, byId);

            BuildScene(result, assets, waterJars);
            MeasureEndpoints(plan, result);
            InterferenceChecker.Check(plan, result);
            result.Hash = Snapshot(result);
            return result;
        }

        /// <summary>
        /// M9：边的端点节点与 anchor 必须真实存在。SlotSolver 的零向量兜底只用于计算，
        /// 不能把"端口不存在"伪装成 0.0000mm 对位成功。
        /// </summary>
        private static void ValidatePorts(Plan plan, AssemblyResult result,
            Dictionary<string, AssemblyNode> byId)
        {
            foreach (Edge e in plan.Topology.Edges)
            {
                foreach (string endpoint in new[] { e.From, e.To })
                {
                    string id = Edge.NodeOf(endpoint);
                    string port = Edge.PortOf(endpoint);
                    AssemblyNode n;
                    if (!byId.TryGetValue(id, out n))
                    {
                        result.Errors.Add("missing node " + id + " on edge " + e.From + "->" + e.To);
                        continue;
                    }
                    if (n.Profile == null || !n.Profile.anchors.ContainsKey(port))
                        result.Errors.Add("missing anchor " + n.Type + "." + port);
                }
            }
        }

        private static void PreScanWater(Plan plan, Dictionary<string, AssemblyNode> byId,
            HashSet<string> waterJars, Dictionary<string, string> jarTrough)
        {
            foreach (Edge e in plan.Topology.Edges)
            {
                if (!string.Equals(e.Kind, "water", StringComparison.Ordinal)) continue;
                string a = Edge.NodeOf(e.From), b = Edge.NodeOf(e.To);
                AssemblyNode na, nb;
                bool ha = byId.TryGetValue(a, out na), hb = byId.TryGetValue(b, out nb);
                if (ha && na.Type == "collect.gas_jar") { waterJars.Add(a); jarTrough[a] = b; }
                else if (hb && nb.Type == "collect.gas_jar") { waterJars.Add(b); jarTrough[b] = a; }
            }
        }

        private static void PlaceGrounded(Plan plan, AssemblyResult result,
            Dictionary<string, AssemblyNode> byId, ProfileCatalog catalog,
            HashSet<string> waterJars, Dictionary<string, string> jarTrough)
        {
            float total = 0f;
            foreach (AssemblyNode n in result.Nodes)
                if (SlotSolver.IsGrounded(n.Type))
                    total += ProfileCatalog.FootprintRadius(n.Profile) * 2f + GapMm;
            // 课本式布局：方案里先出现的发生装置（锥形瓶/试管）排在世界 +X。
            // 相机从 +X 侧看向原点，世界 +X ≈ 屏幕左，于是发生装置在左、收集装置（集气瓶）在右。
            float cursor = total * 0.5f;

            foreach (AssemblyNode n in result.Nodes)
            {
                if (!SlotSolver.IsGrounded(n.Type)) continue;
                n.Grounded = true;
                float radiusMm = ProfileCatalog.FootprintRadius(n.Profile);
                float xMm = cursor - radiusMm;
                cursor -= radiusMm * 2f + GapMm;
                float x = xMm * 0.001f;

                if (n.Type == "reactor.flask_conical" || n.Type == "reactor.test_tube")
                {
                    n.Pose.Position = new Vector3(x, TableY, 0f);
                }
                else if (n.Type == "collect.water_trough")
                {
                    n.Pose.Position = new Vector3(x, TableY, 0f);
                }
                else if (n.Type == "support.stand")
                {
                    n.Pose.Position = new Vector3(x, TableY, 0f);
                }
                else if (n.Type == "collect.gas_jar")
                {
                    bool water = waterJars.Contains(n.Id);
                    bool inverted = water || (n.Model != null &&
                        string.Equals(n.Model.Prop("orientation", "upright"), "inverted", StringComparison.Ordinal));
                    if (!inverted)
                    {
                        n.Pose.Position = new Vector3(x, TableY, 0f);
                    }
                    else
                    {
                        // 先按桌面定位；若带 water 边，等水槽落位后再把瓶口抬到 basin。
                        float heightM = Mathf.Max(1f, n.Profile.height) * 0.001f;
                        n.Pose.Rotation = Quaternion.Euler(180f, 0f, 0f);
                        n.Pose.Position = new Vector3(x, TableY + heightM, 0f);
                    }
                }
            }

            // 水槽必须与它服务的集气瓶同轴，否则 water 边对不上。
            foreach (var kv in jarTrough)
            {
                AssemblyNode jar, trough;
                if (!byId.TryGetValue(kv.Key, out jar) || !byId.TryGetValue(kv.Value, out trough)) continue;
                // 先让水槽与瓶同轴，再以 basin anchor 定瓶口高度。
                Vector3 mouth = jar.AnchorWorld("mouth", Vector3.zero);
                trough.Pose.Position = new Vector3(mouth.x, TableY, mouth.z);
                Vector3 basin = trough.AnchorWorld("basin", Vector3.zero);
                float heightM = Mathf.Max(1f, jar.Profile.height) * 0.001f;
                jar.Pose.Position = new Vector3(mouth.x, basin.y + heightM, mouth.z);
            }
            result.GroundedCount = 0;
            foreach (AssemblyNode n in result.Nodes) if (n.Grounded) result.GroundedCount++;
        }

        private static void PlaceFunnels(Plan plan, Dictionary<string, AssemblyNode> byId)
        {
            foreach (Edge e in plan.Topology.Edges)
            {
                if (!string.Equals(e.Kind, "feed", StringComparison.Ordinal)) continue;
                AssemblyNode funnel = null, reactor = null;
                foreach (string endpoint in new[] { e.From, e.To })
                {
                    AssemblyNode n;
                    if (!byId.TryGetValue(Edge.NodeOf(endpoint), out n)) continue;
                    if (n.Type == "feed.thistle_funnel") funnel = n;
                    if (SlotSolver.IsReactor(n.Type)) reactor = n;
                }
                if (funnel == null || reactor == null) continue;

                string reactorEndpoint = string.Equals(Edge.NodeOf(e.From), reactor.Id, StringComparison.Ordinal)
                    ? e.From : e.To;
                string reactorPort = Edge.PortOf(reactorEndpoint);
                Vector3 slot = SlotSolver.Offset(reactor.Profile, reactorPort, e.Kind,
                    SlotSolver.SameKindIndex(plan.Topology.Edges, e, reactorEndpoint));
                Vector3 target = reactor.AnchorWorld(reactorPort, slot);
                Vector3 local = ProfileCatalog.AnchorLocalStatic(funnel.Profile, "bottom") * funnel.Pose.Scale;
                funnel.Pose.Position = target - funnel.Pose.Rotation * local;
            }
        }

        private static void PlaceConduits(Plan plan, Dictionary<string, AssemblyNode> byId,
            ProfileCatalog catalog)
        {
            foreach (AssemblyNode tube in byId.Values)
            {
                if (tube.Type != "conduit.bent") continue;
                Vector3 start = Vector3.zero, end = Vector3.zero;
                bool hasStart = false, hasEnd = false;

                foreach (Edge e in plan.Topology.Edges)
                {
                    bool from = string.Equals(Edge.NodeOf(e.From), tube.Id, StringComparison.Ordinal);
                    bool to = string.Equals(Edge.NodeOf(e.To), tube.Id, StringComparison.Ordinal);
                    if (!from && !to) continue;
                    string tubeEndpoint = from ? e.From : e.To;
                    string tubePort = Edge.PortOf(tubeEndpoint);
                    string otherEndpoint = from ? e.To : e.From;
                    AssemblyNode other;
                    if (!byId.TryGetValue(Edge.NodeOf(otherEndpoint), out other)) continue;
                    string otherPort = Edge.PortOf(otherEndpoint);
                    Vector3 slot = SlotSolver.Offset(other.Profile, otherPort, e.Kind,
                        SlotSolver.SameKindIndex(plan.Topology.Edges, e, otherEndpoint));
                    Vector3 target = other.AnchorWorld(otherPort, slot);
                    if (tubePort == "in") { start = target; hasStart = true; }
                    else if (tubePort == "out") { end = target; hasEnd = true; }
                }

                if (!hasStart && !hasEnd) continue;
                if (!hasStart) { start = end - new Vector3(0.08f, 0.02f, 0f); hasStart = true; }
                if (!hasEnd) end = start + new Vector3(0.08f, 0.02f, 0f);
                GenerateConduit(tube, start, end);
            }
        }

        private static void GenerateConduit(AssemblyNode tube, Vector3 start, Vector3 end)
        {
            VesselProfile template = tube.Profile;
            Vector3 dm = (end - start) * 1000f;
            float dx = dm.x, dy = dm.y, dz = dm.z; // H6：保留 Z，非共面拓扑不能压到 XY 平面

            // §10.38 演示阶段导管形态（用户 2026-11-22 反馈）：
            //  ① 发生瓶端"竖直上去"：一段真正的竖直立管，不再是从瓶口斜着爬升；
            //  ② 瓶口上方"直角拐成水平"：立管顶用一个小倒角转平，读起来是 90° 直角而不是斜坡；
            //  ③ 水平段走到集气瓶正上方，再"直角拐向下"；
            //  ④ 进瓶玻璃管加长到接近瓶底（向上排空气：CO₂ 密度大，须把空气从下方顶出）。
            // 关键约束：PathWorld 的末端仍停在瓶口平面（dy），否则端点误差会从 0 变成
            // 插入深度、WP4 判废。因此把加长的瓶内段只加进"网格路径 meshPath"，
            // PathWorld 用原来的 path。注意：位姿哈希覆盖 PathWorld，路径形状变化会
            // 使哈希从 04e01498... 变为 12a964a7...，这是 §10.38 的预期变化。
            // 为避免改变 SweepBuilder 的 extent（进而改变 uniform scale 与网格对位），
            // 瓶内段深度限制在原路径最大幅度的 0.85 倍以内。
            float rise = Mathf.Max(20f, Mathf.Abs(dy) * 0.35f + 12f);
            float apex = Mathf.Max(rise + 12f, dy + 30f);
            if (dy > apex - 8f) apex = dy + 14f;
            float elbow = 7f; // 拐角倒角(mm)：保持清晰直角观感，同时让扫掠帧不塌面

            var path = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),                          // 0 发生瓶口：竖直段起点
                new Vector3(0f, rise, 0f),                        // 1 竖直上升（立管）
                new Vector3(dx * 0.05f, apex, dz * 0.05f),        // 2 直角拐向水平（小倒角）
                new Vector3(dx * 0.50f, apex, dz * 0.50f),        // 3 水平段中点
                new Vector3(dx * 0.95f, apex, dz * 0.95f),        // 4 水平段末端
                new Vector3(dx, apex - elbow, dz),                // 5 集气瓶上方直角拐向下
                new Vector3(dx, dy, dz),                          // 6 瓶口平面：连接点（PathWorld 末端）
            };

            float origMax = 1f;
            foreach (Vector3 p in path)
                origMax = Mathf.Max(origMax, Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z));
            // 目标：插到瓶身下段（瓶口到瓶底 130mm，留约 40mm 不触底）。
            float dip = dy - 90f;
            if (Mathf.Abs(dip) > origMax * 0.85f) dip = -origMax * 0.85f;
            var meshPath = new List<Vector3>(path) { new Vector3(dx, dip, dz) }; // 7 瓶内长管末端

            var gen = new VesselProfile
            {
                type = template.type,
                name = template.name,
                kind = "sweep",
                unit = template.unit,
                outerRadius = template.outerRadius,
                wall = template.wall,
                radialSegments = template.radialSegments,
                segments = template.segments,
                path = meshPath,
                raw = template.raw
            };
            gen.anchors["in"] = new ProfileAnchor { pathIndex = 0, dir = "+y" };
            // out 仍指瓶口连接点（索引 6），不是瓶内管末梢（索引 7）。
            gen.anchors["out"] = new ProfileAnchor { pathIndex = path.Count - 1, dir = "-y" };

            // 软橡胶套管（§10.24）：只占水平段的中间约 68%，两端（含两处直角拐弯）
            // 保留玻璃管。水平段是 path[2..5]（都在 y=apex），按弧长算套管边界。
            float sleeveFrom = -1f, sleeveTo = -1f;
            if (meshPath.Count >= 7)
            {
                float[] cum = new float[meshPath.Count];
                for (int i = 1; i < meshPath.Count; i++)
                    cum[i] = cum[i - 1] + Vector3.Distance(meshPath[i - 1], meshPath[i]);
                float total = cum[meshPath.Count - 1];
                float run = cum[5] - cum[2];
                if (total > 1f && run > 1f)
                {
                    sleeveFrom = (cum[2] + 0.16f * run) / total;
                    sleeveTo = (cum[2] + 0.84f * run) / total;
                }
            }

            GeometryStats stats;
            Mesh mesh = SweepBuilder.Build(gen, out stats, sleeveFrom, sleeveTo, 1.22f);
            mesh.name = "GeneratedConduit_" + tube.Id;
            tube.Profile = gen;
            tube.GeneratedMesh = mesh;
            tube.Pose.Position = start;
            tube.Pose.Rotation = Quaternion.identity;
            tube.Pose.Scale = ProfileCatalog.WorldScaleStatic(gen);
            tube.PathWorld.Clear();
            foreach (Vector3 p in path) tube.PathWorld.Add(start + p * 0.001f);
        }

        private static void BuildScene(AssemblyResult result, AssemblyAssets assets,
            HashSet<string> waterJars)
        {
            var old = GameObject.Find("M3 Assembly");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var root = new GameObject("M3 Assembly");
            result.Root = root;

            // 隐藏 G0/G1 探针遗留物：它们不属于 plan 装配，会污染 G2 画面。
            foreach (string name in new[]
            {
                "Probe_Flask", "M3 Bubbles", "M3 Test Stripes", "M3 Contact Shadow"
            })
            {
                var go = GameObject.Find(name);
                if (go != null) go.SetActive(false);
            }
            // 清理场景里序列化的旧接触阴影（Shadow_*）：它们不是本次装配生成的，
            // 尺寸/材质可能与当前方案不符，叠加后会在瓶底形成多余的浅色圆盘。
            foreach (var rootGo in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                if (rootGo.name.StartsWith("Shadow_", StringComparison.Ordinal))
                    rootGo.SetActive(false);

            Material outer = assets.GlassOuter != null ? assets.GlassOuter : Transient("Vchem/Probe/GlassShell");
            Material inner = assets.GlassInner != null ? assets.GlassInner : outer;
            Material opaque = assets.Opaque != null ? assets.Opaque : Transient("Universal Render Pipeline/Lit");
            Material shadow = assets.Shadow != null ? assets.Shadow : Transient("Vchem/Probe/ContactShadow");
            Mesh quad = assets.Quad != null ? assets.Quad : Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            Mesh cylinder = assets.Cylinder != null ? assets.Cylinder : Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");

            foreach (AssemblyNode n in result.Nodes)
            {
                var go = new GameObject("ASM_" + n.Id + "_" + n.Type.Replace('.', '_'));
                go.transform.SetParent(root.transform, false);
                go.transform.position = n.Pose.Position;
                go.transform.rotation = n.Pose.Rotation;
                go.transform.localScale = Vector3.one * n.Pose.Scale;

                Mesh mesh = n.GeneratedMesh;
                if (mesh == null && !assets.Meshes.TryGetValue(n.Type, out mesh)) continue;
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                if (SlotSolver.IsGlass(n.Type))
                {
                    int slots = Mathf.Max(1, mesh.subMeshCount);
                    var mats = new Material[slots];
                    // 导管是单 submesh 空心管：内外壁+端面共用强反射的 GlassOuter，
                    // 细管上影棚反射/边缘线/Fresnel 叠加会读成一团斑驳乱线（尤其推镜特写）。
                    // 单独降参：保留双线轮廓，压掉强反射与过亮边缘。几何/路径不动。
                    Material face = n.Type == "conduit.bent" ? ConduitGlass(outer)
                                  : n.Type == "feed.thistle_funnel" ? FunnelGlass(outer)
                                  : outer;
                    for (int i = 0; i < slots; i++) mats[i] = i == 0 ? face : inner;
                    // 导管中段是软橡胶套管（SweepBuilder 的 submesh 1），两端仍是玻璃。
                    if (n.Type == "conduit.bent" && slots >= 2) mats[1] = RubberTube(opaque);
                    mr.sharedMaterials = mats;
                }
                else
                {
                    mr.sharedMaterial = opaque;
                }

                if (n.Type == "collect.gas_jar" && n.Model != null &&
                    n.Model.PropBool("covered", false))
                {
                    AddCover(go, n, cylinder, inner);
                }
                if (n.Grounded) AddShadow(root, n, quad, shadow);
            }
        }

        private static void AddCover(GameObject parent, AssemblyNode jar, Mesh cylinder, Material mat)
        {
            float unitMm = ProfileCatalog.LocalUnitMmStatic(jar.Profile);
            float mouthR = ProfileCatalog.AnchorRadius(jar.Profile, "mouth");
            // anchor.r 是瓶口内半径；盖片只需比瓶口略大。内置 Cylinder 的半径是 1，
            // localScale.x/z 必须再乘 0.5，否则实际直径翻倍成"蓝色大圆盘"盖住收颈。
            float coverDiamMm = Mathf.Max(12f, (mouthR + 3f) * 2f);
            float diam = coverDiamMm / unitMm;
            float thick = 0.4f / unitMm;
            var go = new GameObject("Cover");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = ProfileCatalog.AnchorLocalStatic(jar.Profile, "mouth")
                + Vector3.up * thick * 0.5f;
            go.transform.localScale = new Vector3(diam * 0.5f, thick * 0.5f, diam * 0.5f);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = cylinder;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }

        private static void AddShadow(GameObject root, AssemblyNode n, Mesh quad, Material mat)
        {
            var go = new GameObject("Shadow_" + n.Id);
            go.transform.SetParent(root.transform, false);
            go.transform.position = new Vector3(n.Pose.Position.x, TableY + 0.003f, n.Pose.Position.z);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            float r = ProfileCatalog.FootprintRadius(n.Profile) * 0.001f;
            // 接触阴影只比瓶底轮廓略大一圈（Quad 半宽≈1.25r）。此前用 r*4.4
            // （半宽 2.2r）使阴影直径达瓶底 2 倍多，看起来像瓶底洒了一滩水。
            float size = Mathf.Max(0.05f, r * 2.5f);
            go.transform.localScale = new Vector3(size, size, 1f);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }

        private static Material Transient(string shaderName)
        {
            Shader sh = Shader.Find(shaderName);
            return sh != null ? new Material(sh) : null;
        }

        // 细管专用玻璃：导管/漏斗细颈共用一套 shader 细管路径，各自缓存一份材质。
        // 细管在屏幕上只有几像素宽，器皿那套 dot(N,V)^k 亮边会碎成点串；抬高 alpha
        // 又变成实心蓝线。改用 _TubeMode：视空间法线横向分量做连续双线，与管长解耦。
        // _TubeColor 用中性玻璃灰（R≈G≈B），不随器皿偏蓝；_TubeSplitY 限定只作用于细颈段。
        private static Material _conduitGlass;
        private static Material _funnelGlass;

        private static Material MakeTubeGlass(Material outer, string name, float splitY)
        {
            Material src = outer != null ? outer : Transient("Vchem/Probe/GlassShell");
            if (src == null) return null;
            Material m = new Material(src);
            m.name = name;
            if (m.HasProperty("_TubeMode")) m.SetFloat("_TubeMode", 1f);
            if (m.HasProperty("_TubeSplitY")) m.SetFloat("_TubeSplitY", splitY);
            if (m.HasProperty("_TubeColor")) m.SetColor("_TubeColor", new Color(0.60f, 0.61f, 0.61f, 0.26f));
            // 下面反射/边缘线参数作为 TubeMode 未生效时的兜底，保持低碎光。
            if (m.HasProperty("_ReflectionStrength")) m.SetFloat("_ReflectionStrength", 0.16f);
            if (m.HasProperty("_ReflectionRoughness")) m.SetFloat("_ReflectionRoughness", 0.45f);
            if (m.HasProperty("_RefractionMix")) m.SetFloat("_RefractionMix", 0f);
            if (m.HasProperty("_LinePower")) m.SetFloat("_LinePower", 5f);
            if (m.HasProperty("_LineStrength")) m.SetFloat("_LineStrength", 0.60f);
            if (m.HasProperty("_FresnelStrength")) m.SetFloat("_FresnelStrength", 0.30f);
            return m;
        }

        private static Material ConduitGlass(Material outer)
        {
            if (_conduitGlass == null) _conduitGlass = MakeTubeGlass(outer, "Vchem_ConduitGlass", 2f);
            return _conduitGlass;
        }

        // 漏斗：只有细颈（归一化 y < 0.62，即 92mm/150mm）走细管路径，球碗保持器皿原玻璃。
        private static Material FunnelGlass(Material outer)
        {
            if (_funnelGlass == null) _funnelGlass = MakeTubeGlass(outer, "Vchem_FunnelGlass", 0.62f);
            return _funnelGlass;
        }

        // 水平段中部的软橡胶管：不透明米黄、低高光，视觉上像套在玻璃管外的胶皮。
        // 从 M_ApparatusOpaque 克隆以兼容 Editor/Player 两条资源加载链路。
        private static Material _rubberTube;
        private static Material RubberTube(Material opaque)
        {
            if (_rubberTube != null) return _rubberTube;
            Material src = opaque != null ? opaque : Transient("Universal Render Pipeline/Lit");
            if (src == null) return null;
            Material m = new Material(src);
            m.name = "Vchem_RubberTube";
            Color beige = new Color(1.05f, 0.92f, 0.82f, 1f);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", beige);
            if (m.HasProperty("_Color")) m.SetColor("_Color", beige);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.30f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            _rubberTube = m;
            return m;
        }

        /// <summary>
        /// H6：导管节点的真实端点是生成路径的首/末点，不是 profile anchor 的理论位置。
        /// 用 anchor 量误差会让"路径压错平面"这类错误永远报 0.0000mm。
        /// </summary>
        private static Vector3 ActualEndpoint(AssemblyNode n, string port, Vector3 slotMm)
        {
            if (n.Type == "conduit.bent" && n.PathWorld.Count >= 2)
                return port == "in" ? n.PathWorld[0] : n.PathWorld[n.PathWorld.Count - 1];
            return n.AnchorWorld(port, slotMm);
        }

        /// <summary>公开供编辑器诊断用：扰动导管路径后可复测端点误差，验证判据没有只看 anchor。</summary>
        public static void MeasureEndpoints(Plan plan, AssemblyResult result)
        {
            result.EndpointErrors.Clear();
            result.MaxEndpointErrorMm = 0f;
            var byId = new Dictionary<string, AssemblyNode>(StringComparer.Ordinal);
            foreach (AssemblyNode n in result.Nodes) byId[n.Id] = n;

            foreach (Edge e in plan.Topology.Edges)
            {
                AssemblyNode a, b;
                if (!byId.TryGetValue(Edge.NodeOf(e.From), out a)
                    || !byId.TryGetValue(Edge.NodeOf(e.To), out b)) continue;
                Vector3 slotA = SlotSolver.Offset(a.Profile, Edge.PortOf(e.From), e.Kind,
                    SlotSolver.SameKindIndex(plan.Topology.Edges, e, e.From));
                Vector3 slotB = SlotSolver.Offset(b.Profile, Edge.PortOf(e.To), e.Kind,
                    SlotSolver.SameKindIndex(plan.Topology.Edges, e, e.To));
                Vector3 pa = ActualEndpoint(a, Edge.PortOf(e.From), slotA);
                Vector3 pb = ActualEndpoint(b, Edge.PortOf(e.To), slotB);
                float mm = Vector3.Distance(pa, pb) * 1000f;
                result.MaxEndpointErrorMm = Mathf.Max(result.MaxEndpointErrorMm, mm);
                result.EndpointErrors.Add(new EndpointError
                {
                    Edge = e.From + " -> " + e.To, From = a.Id, To = b.Id, DistanceMm = mm
                });
            }
        }

        private static string Snapshot(AssemblyResult result)
        {
            var lines = new List<string>();
            foreach (AssemblyNode n in result.Nodes)
            {
                Vector3 p = n.Pose.Position, s = n.Pose.Rotation.eulerAngles;
                lines.Add(string.Format("{0}|{1}|{2:F4},{3:F4},{4:F4}|{5:F2},{6:F2},{7:F2}|{8:F5}",
                    n.Id, n.Type, p.x, p.y, p.z, s.x, s.y, s.z, n.Pose.Scale));
                // M8：位姿哈希必须覆盖导管路径形状，否则"两次一致"对弯错平面失明。
                if (n.PathWorld.Count > 0)
                {
                    var pts = new StringBuilder();
                    foreach (Vector3 q in n.PathWorld)
                        pts.Append(q.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                           .Append(q.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                           .Append(q.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                    lines.Add(n.Id + "|path|" + pts);
                }
            }
            lines.Sort(StringComparer.Ordinal);
            string text = string.Join("\n", lines.ToArray());
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder();
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Report(AssemblyResult r)
        {
            var sb = new StringBuilder();
            sb.Append("nodes=").Append(r.Nodes.Count)
              .Append(" grounded=").Append(r.GroundedCount)
              .Append(" hash=").Append(r.Hash)
              .Append(" maxEndpointErrorMm=").Append(r.MaxEndpointErrorMm.ToString("F4"))
              .Append(" interference=").Append(r.Interference.Count);
            foreach (InterferenceHit h in r.Interference)
                sb.Append("; HIT ").Append(h.A).Append('/').Append(h.PartA)
                  .Append('<').Append(h.B).Append('/').Append(h.PartB)
                  .Append('=').Append(h.OverlapMm.ToString("F1")).Append("mm");
            return sb.ToString();
        }
    }
}
