using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// WP3 预览：在【另存的预览场景】里把探针圆柱换成车削锥形瓶双壳网格。
/// 不修改 M0_GlassProbe.unity（G0/G1 基线）。
/// 流程：open M0 → MakePreviewScene → SwapToLathe → 截图 → open_scene 回 M0。
/// 车削网格自带两个 submesh（外壳0/内壳1），单个 GlassOuter 物体渲染整网格，
/// 原 GlassInner 圆柱隐藏；液体圆柱暂隐藏（液位演出属 WP5）。
/// </summary>
public static class VchemGeometryPreview
{
    const string MeshPath = "Assets/Vchem/Content/Meshes/reactor.flask_conical.asset";
    const string PreviewScene = "Assets/Vchem/Scenes/M3_LathePreview.unity";

    [MenuItem("Vchem/M3/Make Preview Scene")]
    public static string MakePreviewScene()
    {
        if (EditorSceneManager.GetActiveScene().path != "Assets/Vchem/Scenes/M0_GlassProbe.unity")
            return "ERROR: 先打开 M0_GlassProbe.unity（当前="
                   + EditorSceneManager.GetActiveScene().path + "）";
        if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), PreviewScene))
            return "ERROR: 另存预览场景失败";
        return "saved " + PreviewScene;
    }

    [MenuItem("Vchem/M3/Swap To Lathe Flask")]
    public static string SwapToLathe()
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (mesh == null) return "ERROR: 先运行 VchemGeometryBaker.Bake";

        var outer = GameObject.Find("GlassOuter");
        var inner = GameObject.Find("GlassInner");
        if (outer == null || inner == null) return "ERROR: 场景里没有 GlassOuter/GlassInner";

        var omf = outer.GetComponent<MeshFilter>();
        var omr = outer.GetComponent<MeshRenderer>();
        if (omf == null || omr == null) return "ERROR: GlassOuter 缺 MeshFilter/Renderer";

        var outerMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Vchem/Content/Materials/M_GlassOuter.mat");
        var innerMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Vchem/Content/Materials/M_GlassInner.mat");

        // 单个物体渲染整网格：submesh0=外壁(outer材质)，submesh1=内壁(inner材质)
        omf.sharedMesh = mesh;
        omr.sharedMaterials = new Material[] { outerMat, innerMat };
        EditorUtility.SetDirty(omf);
        EditorUtility.SetDirty(omr);

        // 原内壳圆柱不再需要（车削网格已含内壁）
        inner.SetActive(false);

        // 液体圆柱与锥瓶内腔不匹配，预览阶段先隐藏，只看双壳几何与高光
        var body = GameObject.Find("LiquidBody");
        var surf = GameObject.Find("LiquidSurface");
        if (body != null) body.SetActive(false);
        if (surf != null) surf.SetActive(false);
        var bubbles = GameObject.Find("M3 Bubbles");
        if (bubbles != null) bubbles.SetActive(false);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        return string.Format("swapped: mesh={0} submeshes={1} verts={2} tris={3}; " +
            "hidden inner/liquid/bubbles",
            mesh.name, mesh.subMeshCount, mesh.vertexCount, mesh.triangles.Length / 3);
    }

    /// <summary>把锥瓶整体缩放到接近原圆柱的视觉宽度（真实比例 40/112 偏瘦）。</summary>
    public static string SetScale(string arg)
    {
        float s;
        if (!float.TryParse(arg, out s)) return "ERROR: scale 形如 1.3";
        var outer = GameObject.Find("GlassOuter");
        if (outer == null) return "ERROR: no GlassOuter";
        outer.transform.localScale = new Vector3(s, 1f, s);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        return "scale xz=" + s;
    }
    /// <summary>临时换成不透明 Lit 看纯几何（破面/锯齿）。再调一次恢复。</summary>
    public static string SolidView(string arg)
    {
        var go = GameObject.Find("GlassOuter");
        if (go == null) return "ERROR: no GlassOuter";
        var mr = go.GetComponent<MeshRenderer>();
        bool solid = arg == "1";
        if (solid)
        {
            var lit = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            lit.color = new Color(0.75f, 0.78f, 0.80f, 1f);
            mr.sharedMaterials = new Material[] { lit, lit };
        }
        else
        {
            var om = AssetDatabase.LoadAssetAtPath<Material>("Assets/Vchem/Content/Materials/M_GlassOuter.mat");
            var im = AssetDatabase.LoadAssetAtPath<Material>("Assets/Vchem/Content/Materials/M_GlassInner.mat");
            mr.sharedMaterials = new Material[] { om, im };
        }
        EditorUtility.SetDirty(mr);
        return solid ? "solid ON" : "glass restored";
    }

}
