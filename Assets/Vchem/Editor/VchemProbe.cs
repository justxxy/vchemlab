#if UNITY_EDITOR
// Vchem M0 glass/liquid material probe builder.
// Driven by DSHBridge exec (zero-arg or single-string static methods).
// Creates materials + a conical-flask assembly, exposes parameter tweaks,
// and reports render stats. All assets go under Assets/Vchem (task assets).
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class VchemProbe
{
    const string ShaderBody = "Vchem/Probe/LiquidBody";
    const string ShaderSurface = "Vchem/Probe/LiquidSurface";
    const string ShaderGlass = "Vchem/Probe/GlassShell";
    const string MatDir = "Assets/Vchem/Content/Materials";

    static Material Mat(string name, string shaderName)
    {
        string path = MatDir + "/" + name + ".mat";
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            Shader sh = Shader.Find(shaderName);
            if (sh == null) return null;
            m = new Material(sh);
            AssetDatabase.CreateAsset(m, path);
        }
        return m;
    }

    static GameObject Part(string name, PrimitiveType prim, Vector3 pos, Vector3 scale,
                           Material mat, Transform parent)
    {
        GameObject go = GameObject.CreatePrimitive(prim);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    // Build the probe scene assembly in the currently active scene.
    public static string Build()
    {
        Material body = Mat("M_LiquidBody", ShaderBody);
        Material surf = Mat("M_LiquidSurface", ShaderSurface);
        Material glassOut = Mat("M_GlassOuter", ShaderGlass);
        Material glassIn = Mat("M_GlassInner", ShaderGlass);
        if (body == null || surf == null || glassOut == null || glassIn == null)
            return "FAIL: shader not found/compiled";

        // Rebuild root if present.
        GameObject old = GameObject.Find("Probe_Flask");
        if (old != null) Object.DestroyImmediate(old);

        GameObject root = new GameObject("Probe_Flask");

        // Glass: outer + inner cylinder shells (inner culls front faces).
        Part("GlassOuter", PrimitiveType.Cylinder, new Vector3(0, 0, 0),
             new Vector3(1.02f, 1.0f, 1.02f), glassOut, root.transform);
        glassOut.SetFloat("_Cull", (float)CullMode.Back);
        Part("GlassInner", PrimitiveType.Cylinder, new Vector3(0, 0, 0),
             new Vector3(0.98f, 0.985f, 0.98f), glassIn, root.transform);
        glassIn.SetFloat("_Cull", (float)CullMode.Front);

        // Liquid body: fills lower portion of flask (world Y from -1 to 0.2).
        Part("LiquidBody", PrimitiveType.Cylinder, new Vector3(0, -0.4f, 0),
             new Vector3(0.94f, 0.6f, 0.94f), body, root.transform);
        body.SetFloat("_LiquidTopY", 0.2f);
        body.SetFloat("_DepthSpan", 1.2f);

        // Liquid surface: thin disk at the top (local radius 0.5 -> scale 0.94).
        Part("LiquidSurface", PrimitiveType.Cylinder, new Vector3(0, 0.2f, 0),
             new Vector3(0.94f, 0.01f, 0.94f), surf, root.transform);
        surf.SetFloat("_Radius", 0.5f);
        surf.SetFloat("_MeniscusWidth", 0.08f);

        // Thistle funnel stem dipping below the surface (liquid seal visual).
        Material funnelMat = Mat("M_GlassInner", ShaderGlass);
        Part("FunnelStem", PrimitiveType.Cylinder, new Vector3(0.25f, -0.1f, 0),
             new Vector3(0.06f, 0.75f, 0.06f), funnelMat, root.transform);

        // Ensure a camera + light exist.
        if (Camera.main == null)
        {
            GameObject cam = new GameObject("Main Camera");
            cam.tag = "MainCamera";
            Camera c = cam.AddComponent<Camera>();
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = new Color(0.16f, 0.18f, 0.21f, 1f);
            cam.AddComponent<UniversalAdditionalCameraData>();
            cam.transform.position = new Vector3(2.2f, 1.3f, 3.2f);
            cam.transform.LookAt(new Vector3(0, 0, 0));
        }
        if (Object.FindFirstObjectByType<Light>() == null)
        {
            GameObject lg = new GameObject("Directional Light");
            Light l = lg.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.4f;
            lg.transform.rotation = Quaternion.Euler(50f, -30f, 0);
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        return "OK: probe assembly built (glass outer/inner + body + surface + funnel stem)";
    }

    // Tweak a body parameter by name. arg = "param=value" e.g. "DeepColor.a=0.9"
    public static string SetParam(string arg)
    {
        if (string.IsNullOrEmpty(arg) || !arg.Contains("=")) return "ERR: use param=value";
        string[] kv = arg.Split('=');
        string name = kv[0].Trim();
        string val = kv[1].Trim();
        Material body = Mat("M_LiquidBody", ShaderBody);
        if (body == null) return "ERR: material missing";
        float f;
        if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out f))
        {
            body.SetFloat(name, f);
        }
        else
        {
            Color c;
            string[] parts = val.Split(',');
            if (parts.Length == 4 &&
                float.TryParse(parts[0], out c.r) && float.TryParse(parts[1], out c.g) &&
                float.TryParse(parts[2], out c.b) && float.TryParse(parts[3], out c.a))
                body.SetColor(name, c);
            else return "ERR: unparsable value " + val;
        }
        EditorUtility.SetDirty(body);
        AssetDatabase.SaveAssets();
        return "OK: " + name + "=" + val;
    }

    // Report render stats of the probe assembly.
    public static string Stats()
    {
        StringBuilder sb = new StringBuilder();
        int renderers = 0, tris = 0, materials = 0;
        foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            renderers++;
            materials += r.sharedMaterials.Length;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;
        }
        sb.Append("renderers=").Append(renderers)
          .Append(" materialSlots=").Append(materials)
          .Append(" tris=").Append(tris).AppendLine();
        sb.Append("shader body=").Append(Shader.Find(ShaderBody) != null ? "ok" : "MISSING")
          .Append(" surface=").Append(Shader.Find(ShaderSurface) != null ? "ok" : "MISSING")
          .Append(" glass=").Append(Shader.Find(ShaderGlass) != null ? "ok" : "MISSING").AppendLine();
        sb.Append("camera clear=").Append(Camera.main != null ? Camera.main.clearFlags.ToString() : "none").AppendLine();
        return sb.ToString();
    }
}
#endif
