using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Vchem.Core.Judge;
using Vchem.Core.Model;
using Vchem.Handoff;
using Vchem.UI;

/// <summary>
/// M2 桥接驱动脚本：经 DSHBridge exec 调用，搭建场景 + 可编程验证 8 种错法定位。
/// 全部为静态方法（exec 只支持零参/单字符串参）。
/// </summary>
public static class VchemM2Builder
{
    private const string ScenePath = "Assets/Vchem/Scenes/M2_PlanPanel.unity";

    /// <summary>打开 AI 助教弹层（供桥接验证 UI）。arg 可选关键帧标签。</summary>
    public static string Pai(string arg)
    {
        Submission s;
        if (!SubmissionHandoff.TryLoad(out s)) return "no submission; submit first";
        AiAssistantPanel.Open(s, arg ?? "");
        return "opened keyframe=" + (arg ?? "");
    }

    /// <summary>打开 M3 播放器里的 AI 助教（供桥接验证）。</summary>
    public static string PaiM3(string arg)
    {
        GameObject go = GameObject.Find("Vchem.RuntimeShowcase");
        if (go == null) return "no runtime showcase; enter play in M3 first";
        Vchem.Showcase.VchemRuntimeShowcase rt = go.GetComponent<Vchem.Showcase.VchemRuntimeShowcase>();
        if (rt == null) return "no VchemRuntimeShowcase component";
        rt.OpenAssistantForTest();
        return "opened M3 assistant";
    }

    /// <summary>在已打开的 AI 弹层里触发一次“解释这次结果”（Play 模式下使用）。</summary>
    public static string PaiExplain(string arg)
    {
        AiAssistantPanel panel = UnityEngine.Object.FindObjectOfType<AiAssistantPanel>();
        if (panel == null) return "no panel; call Pai first";
        panel.ExplainForTest();
        return "asked";
    }

    /// <summary>按 mode:level:question 触发助教请求（本地 mock 服务，不配 Key）。</summary>
    public static string PaiAsk(string arg)
    {
        var panels = UnityEngine.Object.FindObjectsOfType<AiAssistantPanel>();
        if (panels == null || panels.Length == 0) return "no panel; call Pai first";
        panels[panels.Length - 1].AskForTest(arg ?? "explain:L1:");
        return "asked " + arg;
    }

    /// <summary>助教无 Key 诊断：读面板文本状态 + 验证请求体拼装（纯本地，不发起任何请求）。</summary>
    public static string PaiDiag(string arg)
    {
        var panels = UnityEngine.Object.FindObjectsOfType<AiAssistantPanel>();
        if (panels == null || panels.Length == 0) return "no panel; call Pai first";
        var panel = panels[panels.Length - 1];
        var texts = panel.GetComponentsInChildren<UnityEngine.UI.Text>(true);
        string title = texts.Length > 0 ? texts[0].text : "-";
        string resultLine = texts.Length > 1 ? texts[1].text : "-";
        string status = "", answer = "", raw = "";
        string dt = panel.DiagText();
        foreach (var line in dt.Split('\n'))
        {
            if (line.StartsWith("STATUS=")) status = line.Substring(7);
            else if (line.StartsWith("RAW=")) raw = line.Substring(4);
            else if (line.StartsWith("ANSWER=")) answer = line.Substring(7);
        }
        var btns = panel.GetComponentsInChildren<UnityEngine.UI.Button>(true);
        string btnList = "";
        foreach (var b in btns)
            btnList += (b.name.StartsWith("Btn_") ? b.name.Substring(4) : b.name) + "|";
        // 请求体拼装（仅字符串处理，不联网）
        Submission sub;
        string body = "";
        if (SubmissionHandoff.TryLoad(out sub))
            body = Vchem.UI.AiClient.BuildBody(sub, "explain", "", "L1", arg ?? "验满");
        return "panels=" + panels.Length +
               "\ntitle=" + title +
               "\nresult=" + resultLine +
               "\nbuttons=" + btnList +
               "\nstatus=" + status +
               "\nraw=" + raw +
               "\nanswer=" + (string.IsNullOrEmpty(answer) ? "<empty>" : answer) +
               "\nbody=" + (body.Length > 300 ? body.Substring(0, 300) : body);
    }

    /// <summary>新建一个干净场景并挂上 PlanDesigner。</summary>
    public static string Build()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        // 主相机（3D 背景与截图用；M2 面板本身是 Overlay Canvas）
        GameObject camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        Camera cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.93f, 0.95f, 0.97f);
        cam.orthographic = true;
        cam.orthographicSize = 5;
        camGo.transform.position = new Vector3(0, 0, -10);
        camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        GameObject go = new GameObject("PlanDesigner");
        go.AddComponent<Vchem.UI.PlanDesigner>();
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        AssetDatabase.SaveAssets();
        return "M2 scene built at " + ScenePath;
    }

    public static string Open()
    {
        EditorSceneManager.OpenScene(ScenePath);
        return "opened " + ScenePath;
    }

    /// <summary>把 Resources/VchemIcons 下的 PNG 统一导入为 Sprite，供 PlanDesigner 加载示例图。</summary>
    public static string ImportIcons()
    {
        string folder = "Assets/Vchem/Resources/VchemIcons";
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
        int n = 0;
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.filterMode = FilterMode.Bilinear;       // 缩小时平滑采样，避免细线锯齿/断点
            ti.maxTextureSize = 512;
            ti.spritePixelsPerUnit = 100f;
            ti.SaveAndReimport();
            n++;
        }
        AssetDatabase.Refresh();
        return "imported " + n + " icons as Sprite";
    }

    /// <summary>不进 Play，直接用 DesignState 构造 8 种错法 + 标准方案，验证判定与端口定位。</summary>
    public static string Verify()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (Case c in Cases())
        {
            DesignState st = new DesignState();
            c.Build(st);
            Outcome o = st.Submit();
            List<PortIssue> issues = st.Locate(o);
            string loc = "";
            foreach (PortIssue pi in issues) loc += pi.NodeId + "." + pi.Port + " ";
            bool ok = c.ExpectClass == "-" ? o.Class == null : o.Class == c.ExpectClass;
            sb.AppendLine((ok ? "PASS " : "FAIL ") + c.Name +
                " -> " + (o.Class ?? o.Verdict) + " [" + loc.Trim() + "]");
        }
        return sb.ToString();
    }

    private sealed class Case
    {
        public string Name;
        public string ExpectClass;
        public Action<DesignState> Build;
    }

    private static List<Case> Cases()
    {
        List<Case> list = new List<Case>();

        list.Add(C("标准方案", "R-OK-01", st =>
        {
            Node f = st.AddNode("reactor.flask_conical");
            Node fn = st.AddNode("feed.thistle_funnel");
            Node c = st.AddNode("conduit.bent");
            Node j = st.AddNode("collect.gas_jar");
            st.SetNodeProp(fn.Id, "submerged", true);
            st.SetNodeProp(j.Id, "orientation", "upright");
            st.SetNodeProp(j.Id, "covered", true);
            st.Connect(fn.Id + ".bottom", f.Id + ".mouth", "feed");
            st.Connect(c.Id + ".in", f.Id + ".mouth", "gas");
            st.Connect(c.Id + ".out", j.Id + ".mouth", "gas");
            st.SetEdgeProp(c.Id + ".out", j.Id + ".mouth", "depth", "deep");
            st.Fullness = "burning_splint"; st.Identity = "limewater";
            st.SolidReagent = "reagent.caco3_lump"; st.LiquidReagent = "reagent.hcl_dilute";
        }));

        // 错法1：漏斗未液封 -> R-COL-01，定位 n2.bottom
        list.Add(MutStd("未液封", "R-COL-01", st =>
        {
            Node fn = st.FindNodeByType("feed.thistle_funnel");
            st.SetNodeProp(fn.Id, "submerged", false);
        }));

        // 错法2：导管太短 -> R-COL-02，定位导管.out
        list.Add(MutStd("导管太短", "R-COL-02", st =>
        {
            Node c = st.FindNodeByType("conduit.bent");
            Node j = st.FindNodeByType("collect.gas_jar");
            st.SetEdgeProp(c.Id + ".out", j.Id + ".mouth", "depth", "shallow");
        }));

        // 错法3：集气瓶未盖 -> R-COL-03，定位集气瓶.mouth
        list.Add(MutStd("未盖玻璃片", "R-COL-03", st =>
        {
            Node j = st.FindNodeByType("collect.gas_jar");
            st.SetNodeProp(j.Id, "covered", false);
        }));

        // 错法4：集气瓶倒置 -> R-COL-04，定位集气瓶.mouth
        list.Add(MutStd("倒置收集", "R-COL-04", st =>
        {
            Node j = st.FindNodeByType("collect.gas_jar");
            st.SetNodeProp(j.Id, "orientation", "inverted");
        }));

        // 错法5：排水法 -> R-COL-05，定位水槽.basin
        list.Add(MutStd("排水法", "R-COL-05", st =>
        {
            st.AddNode("collect.water_trough");
            st.CollectorMode = "water";
        }));

        // 错法6：稀硫酸 -> R-RXN-01，定位液体位
        list.Add(MutStd("稀硫酸", "R-RXN-01", st =>
        {
            st.LiquidReagent = "reagent.h2so4_dilute";
        }));

        // 错法7：碳酸钙粉末 -> R-RXN-02，定位固体位
        list.Add(MutStd("碳酸钙粉末", "R-RXN-02", st =>
        {
            st.SolidReagent = "reagent.caco3_powder";
        }));

        // 错法10：硫酸钙固体 -> R-RXN-05，可进 3D 但演示阶段无气泡
        list.Add(MutStd("硫酸钙", "R-RXN-05", st =>
        {
            st.SolidReagent = "reagent.caso4";
        }));

        // 错法8：浓盐酸 -> R-RXN-03，定位液体位
        list.Add(MutStd("浓盐酸", "R-RXN-03", st =>
        {
            st.LiquidReagent = "reagent.hcl_conc";
        }));

        // 错法9：稀盐酸直接倒进已有大理石的锥形瓶 -> R-RXN-04
        list.Add(MutStd("固液同瓶直投", "R-RXN-04", st =>
        {
            st.LiquidRoute = "direct";
        }));

        return list;
    }

    private static Case C(string name, string expect, Action<DesignState> build)
    {
        return new Case { Name = name, ExpectClass = expect, Build = build };
    }

    /// <summary>基于标准方案做单点修改的错法用例。</summary>
    private static Case MutStd(string name, string expect, Action<DesignState> mutation)
    {
        return C(name, expect, st =>
        {
            Node f = st.AddNode("reactor.flask_conical");
            Node fn = st.AddNode("feed.thistle_funnel");
            Node c = st.AddNode("conduit.bent");
            Node j = st.AddNode("collect.gas_jar");
            st.SetNodeProp(fn.Id, "submerged", true);
            st.SetNodeProp(j.Id, "orientation", "upright");
            st.SetNodeProp(j.Id, "covered", true);
            st.Connect(fn.Id + ".bottom", f.Id + ".mouth", "feed");
            st.Connect(c.Id + ".in", f.Id + ".mouth", "gas");
            st.Connect(c.Id + ".out", j.Id + ".mouth", "gas");
            st.SetEdgeProp(c.Id + ".out", j.Id + ".mouth", "depth", "deep");
            st.Fullness = "burning_splint"; st.Identity = "limewater";
            st.SolidReagent = "reagent.caco3_lump"; st.LiquidReagent = "reagent.hcl_dilute";
            mutation(st);
        });
    }


    /// <summary>Play 中用：载入标准方案并刷新界面（桥接无法点击按钮，故走代码）。</summary>
    public static string PloadStandard()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner in play mode";
        pd.LoadStandardProgrammatic();
        pd.RefreshForBridge();
        return pd.DebugState();
    }

    /// <summary>Play 中用：提交判定并返回结果+定位。</summary>
    public static string Psubmit()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        pd.BridgeSubmit();  // 触发 OnSubmit，更新诊断卡 UI
        return pd.DebugSubmit();
    }

    /// <summary>Play 中用：与「进入 3D 演示」按钮同一条门控/交接路径。</summary>
    public static string Pplay3d()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeEnter3D();
    }

    /// <summary>需求2：点底部「检查 / 开始实验」，返回按钮状态与判定。</summary>
    public static string Paction()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        return pd == null ? "no PlanDesigner" : pd.BridgeAction();
    }

    /// <summary>需求1：从当前交接的 plan.json 恢复画布（与 3D 返回 M2 同一条路径）。</summary>
    public static string Prestore()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeRestore(pd.BridgeCurrentPlanJson());
    }

    public static string Pui()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        return pd == null ? "no PlanDesigner" : pd.DebugUI();
    }

    /// <summary>只读：诊断卡标题/正文/定位行文本，验证显示层无码泄漏。</summary>
    public static string Pdiag()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        return pd == null ? "no PlanDesigner" : pd.DebugDiagText();
    }

    /// <summary>Play 中用：切到「试剂」页签，验证左右可来回切换。</summary>
    public static string PtabReagent()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        pd.BridgeTabReagent();
        return pd.DebugUI();
    }

    /// <summary>Play 中用：切回「容器」页签。</summary>
    public static string PtabContainer()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        pd.BridgeTabContainer();
        return pd.DebugUI();
    }

    /// <summary>Play 中用：设置固/液体试剂并返回界面摘要。</summary>
    public static string Preagent(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        // arg = "solid|liquid"，空段表示不改
        string[] parts = (arg ?? "").Split('|');
        string solid = parts.Length > 0 ? parts[0] : "";
        string liquid = parts.Length > 1 ? parts[1] : "";
        pd.BridgeSetReagent(solid, liquid);
        return pd.DebugUI();
    }

    /// <summary>Play 中用：切换收集方式（验证检验栏动态槽 A）。</summary>
    public static string Pcollect()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        pd.BridgeCollect();
        return pd.DebugUI();
    }

    /// <summary>Play 中用：指定收集方式（upright/inverted/water），验证线稿朝向/玻璃片/水槽联动。</summary>
    public static string Pmode(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        return pd == null ? "no PlanDesigner" : pd.BridgeCollectMode(arg);
    }

    /// <summary>Play 中用：按 id 走点击删除路径（含已隐藏接通的导管，FindObjectsOfType 找不到Inactive）。</summary>
    public static string PdelAny(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        return pd == null ? "no PlanDesigner" : pd.BridgeDelete((arg ?? "").Trim());
    }

    /// <summary>Play 中用：脚本化放置一个器材（模拟点击/拖入）。arg = 器材 type。</summary>
    public static string Pdrop(string type)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeDrop(type);
    }

    /// <summary>
    /// 仅诊断：走器材卡 PaletteDragSource 的真实事件链（begin/drag/end），
    /// 屏幕坐标按真人输入口径换算（Overlay 画布传 null 相机），用于验证
    /// IsInDeviceViewport + DropElement 的坐标判定。arg = "器材type|x|yy"（像素坐标，省略则取装置区中心）。
    /// </summary>
    public static string PdragDevice(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string[] p = (arg ?? "").Split('|');
        string type = p.Length > 0 ? p[0] : "reactor.flask_conical";
        Vchem.UI.PaletteDragSource src = null;
        foreach (var s in UnityEngine.Object.FindObjectsOfType<Vchem.UI.PaletteDragSource>())
            if (s.ElementType == type) { src = s; break; }
        if (src == null) return "no palette card for " + type;

        // 与真人一致：Overlay 画布用 null 相机；世界->屏幕用 UiCamera（Overlay 下为 null）
        Camera cam = pd.UiCamera;
        var cardRt = (RectTransform)src.transform;
        Vector2 from = RectTransformUtility.WorldToScreenPoint(cam, cardRt.TransformPoint(cardRt.rect.center));
        Vector2 to;
        if (p.Length >= 3)
            to = new Vector2(float.Parse(p[1]), float.Parse(p[2]));
        else
        {
            var vp = pd.DeviceViewport;
            to = RectTransformUtility.WorldToScreenPoint(cam, vp.TransformPoint(vp.rect.center));
        }
        var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
        data.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
        var camProp = typeof(UnityEngine.EventSystems.PointerEventData).GetProperty("pressEventCamera");
        if (camProp != null && camProp.GetSetMethod(true) != null)
            camProp.GetSetMethod(true).Invoke(data, new object[] { cam });
        data.position = from;
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.beginDragHandler);
        data.dragging = true;
        data.position = Vector2.Lerp(from, to, 0.5f);
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.dragHandler);
        data.position = to;
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.dragHandler);
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.endDragHandler);
        return "from=" + from + " to=" + to + " inViewport=" + pd.IsInDeviceViewport(to) +
               " | " + pd.DebugState() + " | " + pd.DebugUI();
    }

    /// <summary>Play 中用：脚本化「把试剂拖到某个器材」。arg = "nodeId|reagentId"。</summary>
    public static string PdropReagent(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeDropReagent(arg);
    }

    /// <summary>Play 中用：打印 Canvas 的坐标尺度（排查拖拽幽灵坐标错位）。</summary>
    public static string Pcanvas()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        var rt = pd.CanvasRoot;
        var cv = pd.GetComponentInChildren<Canvas>();
        Vector2 lp;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, new Vector2(500, 300), Camera.main, out lp);
        Vector3 wp;
        RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, new Vector2(500, 300), Camera.main, out wp);
        return "rectSize=" + rt.rect.size + " pivot=" + rt.pivot + " localScale=" + rt.localScale +
               " lossy=" + rt.lossyScale + " scaleFactor=" + (cv == null ? -1 : cv.scaleFactor) +
               " lp=" + lp + " wp=" + wp + " camOrtho=" + Camera.main.orthographic +
               " orthoSize=" + Camera.main.orthographicSize + " rootCanvas=" + (cv == null ? "?" : cv.rootCanvas.name);
    }

    /// <summary>Play 中用：模拟一次**真实指针拖拽**（走 EventSystem 的 ExecuteEvents 事件链，与鼠标操作同一条代码路径）。
    /// arg = "reagentId|targetNodeId"，targetNodeId 为空时结束在画布空白处。
    /// 用于验证"试剂卡拖到器材上"这类必须经过输入层的行为，而不是绕过输入直接调业务方法。
    /// </summary>
    public static string Pdrag(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string err;
        Vchem.UI.ReagentDragSource src;
        UnityEngine.EventSystems.PointerEventData data;
        Camera cam;
        Vector2 from;
        if (!SimBeginDrag(arg, out src, out data, out cam, out from, out err)) return err;

        Vector2 to;
        if (!SimTargetPos(arg, cam, from, out to, out err)) return err;

        data.dragging = true;
        data.position = Vector2.Lerp(from, to, 0.5f);
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.dragHandler);
        data.position = to;
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.dragHandler);
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.endDragHandler);

        return "from=" + from + " to=" + to + " | " + pd.DebugState() + " | " + pd.DebugUI();
    }

    private static Vchem.UI.ReagentDragSource _simSrc;
    private static UnityEngine.EventSystems.PointerEventData _simData;

    /// <summary>开始拖拽但停在目标上方（用于截图检查幽灵位置/高亮）。arg 同 Pdrag。</summary>
    public static string PdragHold(string arg)
    {
        string err;
        Vchem.UI.ReagentDragSource src;
        UnityEngine.EventSystems.PointerEventData data;
        Camera cam;
        Vector2 from;
        if (!SimBeginDrag(arg, out src, out data, out cam, out from, out err)) return err;
        Vector2 to;
        if (!SimTargetPos(arg, cam, from, out to, out err)) return err;
        data.dragging = true;
        data.position = to;
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.dragHandler);
        _simSrc = src; _simData = data;

        string ghost = "";
        var g = GameObject.Find("DragGhost_" + arg.Split('|')[0]);
        if (g != null)
        {
            var grt = (RectTransform)g.transform;
            Vector2 gScreen = RectTransformUtility.WorldToScreenPoint(cam, grt.TransformPoint(grt.rect.center));
            ghost = " ghostScreen=" + gScreen + " aim=" + to + " delta=" + (gScreen - to) + " anchors=" + grt.anchorMin;
        }
        else ghost = " no ghost!";
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        return "from=" + from + " to=" + to + ghost + " | " + pd.DebugUI();
    }

    /// <summary>结束 PdragHold 留下的拖拽。</summary>
    public static string PdragRelease()
    {
        if (_simSrc == null || _simData == null) return "no active sim drag";
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        UnityEngine.EventSystems.ExecuteEvents.Execute(_simSrc.gameObject, _simData,
            UnityEngine.EventSystems.ExecuteEvents.endDragHandler);
        _simSrc = null; _simData = null;
        return pd == null ? "released" : pd.DebugState() + " | " + pd.DebugUI();
    }

    private static bool SimBeginDrag(string arg, out Vchem.UI.ReagentDragSource src,
        out UnityEngine.EventSystems.PointerEventData data, out Camera cam, out Vector2 from, out string err)
    {
        src = null; data = null; cam = null; from = Vector2.zero; err = null;
        string[] p = (arg ?? "").Split('|');
        string reagentId = p.Length > 0 ? p[0] : "";
        string found = "";
        foreach (var s in UnityEngine.Object.FindObjectsOfType<Vchem.UI.ReagentDragSource>())
        {
            found += s.ReagentId + " ";
            if (s.ReagentId == reagentId) src = s;
        }
        if (src == null) { err = "no reagent card [" + reagentId + "] visible cards: " + found; return false; }

        // Overlay 画布的 UI 事件相机是 null；用 Camera.main（3D 预览相机）换算会得到垃圾坐标
        var rootCv = src.GetComponentInParent<Canvas>();
        cam = rootCv != null && rootCv.renderMode != RenderMode.ScreenSpaceOverlay ? rootCv.worldCamera : null;

        var cardRt = (RectTransform)src.transform;
        from = RectTransformUtility.WorldToScreenPoint(cam, cardRt.TransformPoint(cardRt.rect.center));

        data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
        data.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
        // pressEventCamera 的 setter 是 internal：反射写入，保证与 StandaloneInputModule 的取值一致
        var camProp = typeof(UnityEngine.EventSystems.PointerEventData).GetProperty("pressEventCamera");
        if (camProp != null && camProp.GetSetMethod(true) != null)
            camProp.GetSetMethod(true).Invoke(data, new object[] { cam });
        data.position = from;
        UnityEngine.EventSystems.ExecuteEvents.Execute(src.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.beginDragHandler);
        return true;
    }

    private static bool SimTargetPos(string arg, Camera cam, Vector2 from, out Vector2 to, out string err)
    {
        to = from; err = null;
        string[] p = (arg ?? "").Split('|');
        string target = p.Length > 1 ? p[1] : "";
        if (string.IsNullOrEmpty(target)) return true;
        Vchem.UI.DeviceItem item = null;
        foreach (var it in UnityEngine.Object.FindObjectsOfType<Vchem.UI.DeviceItem>())
            if (it.NodeId == target) item = it;
        if (item == null) { err = "no device item [" + target + "]"; return false; }
        var irt = (RectTransform)item.transform;
        to = RectTransformUtility.WorldToScreenPoint(cam, irt.TransformPoint(irt.rect.center));
        return true;
    }

    /// <summary>Play 中用：清空画布（模拟「清空」按钮）。</summary>
    public static string Pclear()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        pd.BridgeClear();
        return pd.DebugState();
    }

    /// <summary>Play 中用：脚本化点击锚点。arg = "nodeId|key"。</summary>
    public static string Panchor(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeAnchor(arg);
    }

    /// <summary>Play 中用：读节点结构属性。arg = "nodeId|propKey"。</summary>
    public static string Pprop(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string[] p = (arg ?? "").Split('|');
        var n = pd.State.FindNode(p[0].Trim());
        if (n == null) return "no node " + p[0];
        return p[0] + "." + p[1] + "=" + n.Prop(p[1].Trim(), "<absent>");
    }

    /// <summary>Play 中用：列出当前画布上器材实例 id（供锚点脚本引用）。</summary>
    public static string Pids()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeIds();
    }

    /// <summary>Play 中用：开关"导管连接"模式。arg = "on" / "off" / ""（切换）。</summary>
    public static string PconduitMode(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeConduitMode(arg);
    }

    /// <summary>Play 中用：导管模式下点两个端口。arg = "nodeA|keyA|nodeB|keyB"。</summary>
    public static string Pconduit(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeConduit(arg);
    }

    /// <summary>Play 中用：查看所有导管的接线状态（是否画成折线管、图标是否收起）。</summary>
    public static string PconduitInfo()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.DebugConduit();
    }

    /// <summary>Play 中用：开关"删除器材"模式。arg = "on" / "off" / ""（切换）。</summary>
    public static string PdelMode(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeDeleteMode(arg);
    }

    /// <summary>Play 中用：走真实点击事件链删掉一件器材。arg = nodeId。</summary>
    public static string Pdel(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        Vchem.UI.DeviceItem target = null;
        foreach (var it in UnityEngine.Object.FindObjectsOfType<Vchem.UI.DeviceItem>())
            if (it.NodeId == (arg ?? "").Trim()) target = it;
        if (target == null) return "no device " + arg;

        var es = UnityEngine.EventSystems.EventSystem.current;
        var data = new UnityEngine.EventSystems.PointerEventData(es);
        data.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
        UnityEngine.EventSystems.ExecuteEvents.Execute(target.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        return pd.DebugDelete();
    }

    /// <summary>Play 中用：查看删除态与画布登记表（删完 items 应为 0）。</summary>
    public static string PdelInfo()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.DebugDelete();
    }

    /// <summary>Play 中用：列出某件器材登记了哪些端口。arg = nodeId。</summary>
    public static string Pports(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string id = (arg ?? "").Trim();
        string extra = "";
        foreach (var it in UnityEngine.Object.FindObjectsOfType<Vchem.UI.DeviceItem>())
        {
            if (it.NodeId != id) continue;
            var list = it.GetComponentsInChildren<Vchem.UI.AnchorPoint>(true);
            extra = " pos=" + ((UnityEngine.RectTransform)it.transform).anchoredPosition +
                    " anchors=" + list.Length + "[";
            foreach (var a in list) extra += a.Key + "@" + a.gameObject.activeSelf + " ";
            extra += "]";
        }
        return pd.DebugPortKeys(id) + extra + " | " + pd.DebugPorts();
    }

    /// <summary>Play 中用：走真实点击事件链点一下器材（触屏没有 hover，用点击点亮端口）。arg = nodeId。</summary>
    public static string Pclick(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string id = (arg ?? "").Trim();
        if (id.StartsWith("btn|"))
        {
            // 桥接 query 的中文会乱码：用 ASCII 别名映射到按钮标签
            string key = id.Substring(4);
            string label = key == "splint" ? "燃着木条"
                : key == "mouth" ? "瓶口气泡"
                : key == "lime" ? "澄清石灰水"
                : key == "upright" ? "正放"
                : key == "inverted" ? "倒置"
                : key == "water" ? "排水" : key;
            GameObject go = GameObject.Find("Btn_" + label);
            if (go == null) return "no button " + label;
            var bd = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
            { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
            UnityEngine.EventSystems.ExecuteEvents.Execute(go, bd,
                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            return "click " + id + " | " + pd.DebugUI();
        }
        string[] ck = id.Split('|');
        Vchem.UI.DeviceItem target = null;
        foreach (var it in UnityEngine.Object.FindObjectsOfType<Vchem.UI.DeviceItem>())
            if (it.NodeId == ck[0]) target = it;
        if (target == null) return "no device " + id;
        var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
        {
            button = UnityEngine.EventSystems.PointerEventData.InputButton.Left
        };
        if (ck.Length >= 2)
        {
            Vchem.UI.AnchorPoint ap = null;
            foreach (var a in target.GetComponentsInChildren<Vchem.UI.AnchorPoint>(true))
                if (a.Key == ck[1].Trim()) ap = a;
            if (ap == null) return "no port " + ck[1];
            UnityEngine.EventSystems.ExecuteEvents.Execute(ap.gameObject, data,
                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            return "click port " + id + " | " + pd.DebugPorts();
        }
        UnityEngine.EventSystems.ExecuteEvents.Execute(target.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        return "click " + id + " | " + pd.DebugPorts();
    }

    /// <summary>Play 中用：走真实事件链把指针"移入"某件器材或某个端口。arg = nodeId 或 "nodeId|portKey"。
    /// 用来验证"端口只在指针停上去时出现"这条链路是否真的通了。</summary>
    public static string Phover(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string[] p = (arg ?? "").Split('|');
        var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);

        Vchem.UI.DeviceItem di = null;
        foreach (var it in UnityEngine.Object.FindObjectsOfType<Vchem.UI.DeviceItem>())
            if (it.NodeId == p[0].Trim()) di = it;
        if (di == null) return "no device " + p[0];

        if (p.Length >= 2)
        {
            Vchem.UI.AnchorPoint ap = null;
            foreach (var a in di.GetComponentsInChildren<Vchem.UI.AnchorPoint>(true))
                if (a.Key == p[1].Trim()) ap = a;
            if (ap == null) return "no port " + p[1];
            UnityEngine.EventSystems.ExecuteEvents.Execute(ap.gameObject, data,
                UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
            return "hover port " + p[0] + "|" + p[1] + " | " + pd.DebugPorts();
        }

        UnityEngine.EventSystems.ExecuteEvents.Execute(di.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
        return "hover device " + p[0] + " | " + pd.DebugPorts();
    }

    /// <summary>Play 中用：把指针移开（离开器材与其端口）。arg = nodeId。</summary>
    public static string Punhover(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string id = (arg ?? "").Trim();
        var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
        foreach (var it in UnityEngine.Object.FindObjectsOfType<Vchem.UI.DeviceItem>())
        {
            if (it.NodeId != id) continue;
            foreach (var a in it.GetComponentsInChildren<Vchem.UI.AnchorPoint>(true))
                UnityEngine.EventSystems.ExecuteEvents.Execute(a.gameObject, data,
                    UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler);
            UnityEngine.EventSystems.ExecuteEvents.Execute(it.gameObject, data,
                UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler);
            return "unhover " + id + " | " + pd.DebugPorts();
        }
        return "no device " + id;
    }

    /// <summary>Play 中用：脚本化拖动器材改位置。arg = "nodeId|x|y"。</summary>
    public static string Pmove(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeMove(arg);
    }

    /// <summary>Play 中用：走真实拖拽事件链，把漏斗拖到容器端口上（验证输入层而非直接调业务方法）。
    /// arg = "funnelId|flaskId|key"。</summary>
    public static string Pbind(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        string[] p = (arg ?? "").Split('|');
        if (p.Length != 3) return "bad arg";
        Vchem.UI.DeviceItem fi = null, bi = null;
        foreach (var it in UnityEngine.Object.FindObjectsOfType<Vchem.UI.DeviceItem>())
        {
            if (it.NodeId == p[0]) fi = it;
            if (it.NodeId == p[1]) bi = it;
        }
        if (fi == null) return "no funnel " + p[0];
        if (bi == null) return "no flask " + p[1];

        var frt = (UnityEngine.RectTransform)fi.transform;
        var brt = (UnityEngine.RectTransform)bi.transform;
        Vchem.UI.AnchorPoint ap = null;
        foreach (var a in bi.GetComponentsInChildren<Vchem.UI.AnchorPoint>(true))
            if (a.NodeId == p[1] && a.Key == p[2]) ap = a;
        if (ap == null) return "no anchor " + p[2];

        UnityEngine.Vector2 portPos = brt.anchoredPosition +
            ((UnityEngine.RectTransform)ap.transform).anchoredPosition;
        UnityEngine.Vector2 target = portPos - new UnityEngine.Vector2(60f, 14f);
        UnityEngine.Vector2 canvasDelta = target - frt.anchoredPosition;

        var es = UnityEngine.EventSystems.EventSystem.current;
        var data = new UnityEngine.EventSystems.PointerEventData(es);
        data.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
        UnityEngine.Vector2 from = UnityEngine.RectTransformUtility.WorldToScreenPoint(
            Camera.main, frt.TransformPoint(frt.rect.center));
        data.position = from;

        UnityEngine.EventSystems.ExecuteEvents.Execute(fi.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.beginDragHandler);
        int steps = 8;
        for (int i = 0; i < steps; i++)
        {
            // DeviceItem.OnDrag 用 e.delta / scaleFactor，所以这里把画布位移换算回屏幕像素
            data.delta = canvasDelta * pd.CanvasScaleFactor / steps;
            UnityEngine.EventSystems.ExecuteEvents.Execute(fi.gameObject, data,
                UnityEngine.EventSystems.ExecuteEvents.dragHandler);
        }
        data.delta = UnityEngine.Vector2.zero;
        UnityEngine.EventSystems.ExecuteEvents.Execute(fi.gameObject, data,
            UnityEngine.EventSystems.ExecuteEvents.endDragHandler);
        return pd.DebugAsm() + " | funnelPos=" + frt.anchoredPosition;
    }

    /// <summary>Play 中用：模拟"把漏斗下端拖到容器端口上松手"。arg = "funnelId|flaskId|key"。</summary>
    public static string Pinsert(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeInsert(arg);
    }

    /// <summary>Play 中用：直接装配（不经过拖拽）。arg = "funnelId|flaskId|key"。</summary>
    public static string Pasm(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.BridgeAssemble(arg);
    }

    /// <summary>Play 中用：查看装配状态。</summary>
    public static string PasmInfo()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return pd.DebugAsm();
    }

    /// <summary>Play 中用：器材图标的着色与时间缩放（排查"颜色被闪光协程卡住"）。</summary>
    public static string Ptint()
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        return "timeScale=" + UnityEngine.Time.timeScale + " " + pd.DebugTints();
    }

    /// <summary>Play 中用：设置验满与检验（模拟切 A / 切 B）。arg = "full|lime" 中任一。</summary>
    public static string Pverify(string arg)
    {
        var pd = UnityEngine.Object.FindObjectOfType<Vchem.UI.PlanDesigner>();
        if (pd == null) return "no PlanDesigner";
        pd.BridgeVerify(arg ?? "");
        return pd.DebugUI();
    }

    /// <summary>连线矩阵自测：合法/非法连接各若干。</summary>
    public static string Matrix()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("mouth-gas: " + ConnectionMatrix.Compatible("mouth", "gas"));
        sb.AppendLine("gas-mouth: " + ConnectionMatrix.Compatible("gas", "mouth"));
        sb.AppendLine("mouth-feed: " + ConnectionMatrix.Compatible("mouth", "feed"));
        sb.AppendLine("mouth-water: " + ConnectionMatrix.Compatible("mouth", "water"));
        sb.AppendLine("gas-gas: " + ConnectionMatrix.Compatible("gas", "gas"));
        sb.AppendLine("mouth-gas(invalid feed): " + ConnectionMatrix.Compatible("mouth", "feed"));
        sb.AppendLine("gas-support(invalid): " + ConnectionMatrix.Compatible("gas", "support"));
        return sb.ToString();
    }
}
