#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// WP2 optical setup for the M3 probe scene: glass four layers, near-colourless
/// liquid, flat surface band, turbidity, instanced sphere bubbles and a stripe
/// target used only for the turbidity contrast measurement.
/// DSHBridge: exec?method=VchemM3Optics.Setup / .Report / .Tune&arg=...
/// Scope: M3-owned assets and M0_GlassProbe only. M2 untouched.
/// </summary>
public static class VchemM3Optics
{
    private const string GlassOuter = "Assets/Vchem/Content/Materials/M_GlassOuter.mat";
    private const string GlassInner = "Assets/Vchem/Content/Materials/M_GlassInner.mat";
    private const string LiquidBody = "Assets/Vchem/Content/Materials/M_LiquidBody.mat";
    private const string LiquidSurface = "Assets/Vchem/Content/Materials/M_LiquidSurface.mat";
    private const string BubbleMat = "Assets/Vchem/Content/Materials/M_Bubble.mat";
    private const string StripeTex = "Assets/Vchem/Content/Textures/VchemTestStripes.png";

    public static string Setup()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.path.EndsWith("M0_GlassProbe.unity", StringComparison.OrdinalIgnoreCase))
            return "ERROR: open M0_GlassProbe.unity first";

        // ---- Glass: two shells, one shared absorption model -------------------
        var outer = Mat(GlassOuter, "Vchem/Probe/GlassShell");
        var inner = Mat(GlassInner, "Vchem/Probe/GlassShell");
        // §P2：探针高光只用于 G1 测量；M3 演出材质独立，不跟进 LineStrength，
        // 避免把 G2-2 的水平环面锯齿带进交付画面。
        SetGlass(outer, 1f, 2.4f);
        SetGlass(inner, 0f, 1.0f);
        outer.DisableKeyword("_REFRACTION_ON");
        inner.DisableKeyword("_REFRACTION_ON");
        EditorUtility.SetDirty(outer);
        EditorUtility.SetDirty(inner);

        // ---- Liquid: measured baseline is almost the same brightness as glass -
        var body = Mat(LiquidBody, "Vchem/Probe/LiquidBody");
        body.SetColor("_WaterColor", new Color(0.30f, 0.32f, 0.33f, 0.10f));
        body.SetColor("_DeepColor", new Color(0.28f, 0.30f, 0.31f, 0.16f));
        body.SetColor("_TurbidColor", new Color(0.93f, 0.94f, 0.92f, 1.0f));
        body.SetColor("_Tint", Color.white);
        body.SetFloat("_Turbidity", 0f);
        body.SetFloat("_LiquidTopY", 0.2f);
        body.SetFloat("_DepthSpan", 1.2f);
        body.SetFloat("_FresnelStrength", 0.12f);
        EditorUtility.SetDirty(body);

        var surface = Mat(LiquidSurface, "Vchem/Probe/LiquidSurface");
        // §P2 液面白圈：p99 来自盘本体而非亮带（band=0 实测 149.5≈149.7），
        // 把盘 alpha 0.18→0.10 提高透视，M3 演出液面由 EffectPrimitives 独立设色不受影响。
        surface.SetColor("_SurfaceColor", new Color(0.80f, 0.84f, 0.86f, 0.10f));
        surface.SetColor("_BandColor", new Color(0.94f, 0.97f, 0.98f, 1f));
        surface.SetFloat("_BandCenter", 0.82f);
        surface.SetFloat("_BandWidth", 0.10f);
        surface.SetFloat("_BandStrength", 0.08f);
        surface.SetFloat("_FresnelStrength", 0f);
        EditorUtility.SetDirty(surface);

        // ---- Bubbles: real spheres, shared instanced material -----------------
        var bubbleMat = Mat(BubbleMat, "Vchem/Probe/Bubble");
        bubbleMat.enableInstancing = true;
        EditorUtility.SetDirty(bubbleMat);
        var bubbles = GetOrCreate("M3 Bubbles");
        var sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        for (int i = bubbles.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(bubbles.transform.GetChild(i).gameObject);
        for (int i = 0; i < 12; i++)
        {
            var b = new GameObject("Bubble_" + i.ToString("00"));
            b.transform.SetParent(bubbles.transform, false);
            var mf = b.AddComponent<MeshFilter>();
            mf.sharedMesh = sphere;
            var mr = b.AddComponent<MeshRenderer>();
            mr.sharedMaterial = bubbleMat;
            float a = i * 2.39996f;                       // golden angle, deterministic
            float rad = 0.10f + 0.30f * ((i % 5) / 4f);
            float dia = 0.012f + 0.020f * ((i % 4) / 3f);
            b.transform.localPosition = new Vector3(
                Mathf.Cos(a) * rad, -0.94f + 0.10f * ((i % 3) / 2f), Mathf.Sin(a) * rad);
            b.transform.localScale = Vector3.one * dia;
        }

        // ---- Stripe target is sampled inside the liquid shader, not drawn as a quad.
        // The earlier quad + URP/Unlit material produced an offline asset-check false
        // positive (package shader guid cannot be resolved by the checker), so remove it.
        var oldStripes = FindAny("M3 Test Stripes");
        if (oldStripes != null) UnityEngine.Object.DestroyImmediate(oldStripes);

        var importer = AssetImporter.GetAtPath(StripeTex) as TextureImporter;
        if (importer != null)
        {
            bool dirty = false;
            if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (importer.filterMode != FilterMode.Trilinear) { importer.filterMode = FilterMode.Trilinear; dirty = true; }
            if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; dirty = true; }
            if (dirty) importer.SaveAndReimport();
        }
        var stripeTex = AssetDatabase.LoadAssetAtPath<Texture2D>(StripeTex);
        if (stripeTex != null)
        {
            body.SetTexture("_StripeTex", stripeTex);
            body.SetFloat("_DebugStripes", 0f);
            EditorUtility.SetDirty(body);
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        return "OK: " + Report();
    }

    public static string Report()
    {
        var body = AssetDatabase.LoadAssetAtPath<Material>(LiquidBody);
        var outer = AssetDatabase.LoadAssetAtPath<Material>(GlassOuter);
        var bubbles = FindAny("M3 Bubbles");
        return string.Format(
            "glassOuter={0}; refraction={1}; turbidity={2}; bubbles={3}; debugStripes={4}",
            outer != null ? outer.name : "null",
            outer != null && outer.IsKeywordEnabled("_REFRACTION_ON"),
            body != null ? body.GetFloat("_Turbidity").ToString("0.##") : "null",
            bubbles != null ? bubbles.transform.childCount.ToString() : "null",
            body != null ? body.GetFloat("_DebugStripes").ToString("0.##") : "null");
    }

    public static string Tune(string spec)
    {
        var parts = spec.Split(':');
        if (parts.Length != 2) return "ERROR: use turbidity:v | band:v | refraction:0|1 | stripes:0|1 | bubbles:0|1";
        float v;
        if (!float.TryParse(parts[1], out v)) return "ERROR: bad number " + parts[1];
        switch (parts[0])
        {
            case "turbidity":
            {
                var m = Mat(LiquidBody, "Vchem/Probe/LiquidBody");
                m.SetFloat("_Turbidity", v); EditorUtility.SetDirty(m);
                break;
            }
            case "band":
            {
                var m = Mat(LiquidSurface, "Vchem/Probe/LiquidSurface");
                m.SetFloat("_BandStrength", v); EditorUtility.SetDirty(m);
                break;
            }
            case "refraction":
            {
                var o = Mat(GlassOuter, "Vchem/Probe/GlassShell");
                var i = Mat(GlassInner, "Vchem/Probe/GlassShell");
                if (v > 0.5f) { o.EnableKeyword("_REFRACTION_ON"); i.EnableKeyword("_REFRACTION_ON"); }
                else { o.DisableKeyword("_REFRACTION_ON"); i.DisableKeyword("_REFRACTION_ON"); }
                o.SetFloat("_RefractionMix", 0.25f); i.SetFloat("_RefractionMix", 0.25f);
                EditorUtility.SetDirty(o); EditorUtility.SetDirty(i);
                break;
            }
            case "stripes":
            {
                // The shader-side debug target is what the turbidity metric samples.
                var m = Mat(LiquidBody, "Vchem/Probe/LiquidBody");
                m.SetFloat("_DebugStripes", v);
                EditorUtility.SetDirty(m);
                break;
            }
            case "bubbles":
            {
                var go = FindAny("M3 Bubbles");
                if (go != null) go.SetActive(v > 0.5f);
                break;
            }
            default:
                return "ERROR: unknown target " + parts[0];
        }
        AssetDatabase.SaveAssets();
        return "OK " + spec;
    }

    private static void SetGlass(Material m, float shellId, float lineStrength)
    {
        // §P2 A2 光学：原 (0.40,0.45,0.47) 本体中位 93 / p99 174 → 比值 1.867，
        // 低于 2.5~4.5。压暗本体、把高光推到 HDR 区间；_LineStrength 不动，
        // 避免水平环面 grazing 过强带来的锯齿（G2-2 已登记）。
        m.SetColor("_GlassColor", new Color(0.24f, 0.27f, 0.28f, 0.10f));
        m.SetColor("_RimColor", new Color(1.45f, 1.48f, 1.50f, 1f));
        m.SetFloat("_ShellId", shellId);
        m.SetFloat("_WallThicknessMM", 1.2f);
        m.SetFloat("_MaxPathMM", 82f);
        m.SetColor("_Sigma", new Color(0.030f, 0.018f, 0.012f, 0f));
        m.SetFloat("_LinePower", 14f);
        m.SetFloat("_LineStrength", lineStrength);
        m.SetFloat("_ReflectionStrength", 3.4f);
        m.SetFloat("_ReflectionRoughness", 0.02f);
        EditorUtility.SetDirty(m);
    }

    private static Material Mat(string path, string shaderName)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        var shader = Shader.Find(shaderName);
        if (shader == null) throw new Exception("shader not found: " + shaderName);
        m = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    private static GameObject FindAny(string name)
    {
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            if (root.name == name) return root;
        return null;
    }

    private static GameObject GetOrCreate(string name)
    {
        var go = FindAny(name);
        if (go != null) return go;
        go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "M3 Optics " + name);
        return go;
    }
}
#endif
