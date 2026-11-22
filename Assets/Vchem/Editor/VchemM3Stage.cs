#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// WP1 stage setup for M3: studio reflection probe, table, two lights, contact shadow.
/// Called via DSHBridge: exec?method=VchemM3Stage.Setup / .ProbeHdr / .Report
/// Scope: M0_GlassProbe scene + M3-owned assets only. M2 untouched.
/// </summary>
public static class VchemM3Stage
{
    private const string HdrPath = "Assets/Vchem/Content/Textures/VchemStudio.hdr";

    public static string ProbeHdr()
    {
        AssetDatabase.ImportAsset(HdrPath, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(HdrPath) as TextureImporter;
        if (importer == null) return "ERROR: no TextureImporter at " + HdrPath;

        importer.textureType = TextureImporterType.Default;
        importer.textureShape = TextureImporterShape.TextureCube;
        importer.sRGBTexture = false;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Trilinear;
        importer.SaveAndReimport();

        var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(HdrPath);
        var tex = AssetDatabase.LoadAssetAtPath<Texture>(HdrPath);
        return "OK: cubemap=" + (cube != null ? cube.width + "x" + cube.height : "null")
             + "; texture=" + (tex != null ? tex.GetType().Name + " " + tex.width + "x" + tex.height : "null")
             + "; importerShape=" + importer.textureShape;
    }

    public static string Report()
    {
        var probe = GameObject.Find("M3 Reflection Probe");
        var table = GameObject.Find("M3 Table");
        var shadow = GameObject.Find("M3 Contact Shadow");
        var key = GameObject.Find("M3 Key Light");
        var rim = GameObject.Find("M3 Rim Light");
        var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(HdrPath);
        var sb = new System.Text.StringBuilder();
        sb.Append("hdrCube=").Append(cube != null ? cube.width + "x" + cube.height : "null");
        sb.Append("; probe=").Append(probe != null);
        if (probe != null)
        {
            var p = probe.GetComponent<ReflectionProbe>();
            sb.Append(" mode=").Append(p.mode);
            sb.Append(" custom=").Append(p.customBakedTexture != null ? p.customBakedTexture.name : "null");
        }
        sb.Append("; table=").Append(table != null);
        sb.Append("; shadow=").Append(shadow != null);
        sb.Append("; key=").Append(key != null);
        sb.Append("; rim=").Append(rim != null);
        return sb.ToString();
    }

    public static string Diag()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var name in new[] { "M3 Table", "M3 Contact Shadow", "M3 Reflection Probe", "M3 Key Light", "M3 Rim Light" })
        {
            var go = GameObject.Find(name);
            sb.Append(name).Append(": active=").Append(go != null && go.activeInHierarchy);
            if (go == null) { sb.Append("; pos=MISSING\n"); continue; }
            sb.Append("; pos=").Append(go.transform.position.ToString("F3"));
            var mf = go.GetComponent<MeshFilter>();
            sb.Append("; mesh=").Append(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "null");
            var mr = go.GetComponent<MeshRenderer>();
            sb.Append("; mat=").Append(mr != null && mr.sharedMaterial != null
                ? mr.sharedMaterial.name + "/" + (mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : "null")
                : "null");
            sb.Append("; enabled=").Append(mr != null && mr.enabled);
            sb.Append('\n');
        }
        var shadowMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Vchem/Content/Materials/M_ContactShadow.mat");
        if (shadowMat != null)
            sb.Append("shadowMat tex=").Append(shadowMat.GetTexture("_ShadowTex") != null ? shadowMat.GetTexture("_ShadowTex").name : "null")
              .Append(" color=").Append(shadowMat.GetColor("_Color"))
              .Append(" bounds=").Append(GameObject.Find("M3 Contact Shadow").GetComponent<MeshRenderer>().bounds.ToString());
        var shader = Shader.Find("Vchem/Probe/TableFade");
        sb.Append("TableFade shader=").Append(shader != null ? shader.name + " supported=" + shader.isSupported : "null");
        return sb.ToString();
    }

    public static string TableProbe()
    {
        var table = GameObject.Find("M3 Table");
        var cam = Camera.main;
        if (table == null || cam == null) return "ERROR: missing table or camera";
        var mr = table.GetComponent<MeshRenderer>();
        var p = table.transform.position;
        var pts = new[] {
            cam.WorldToScreenPoint(new Vector3(-4f, p.y, -3f)),
            cam.WorldToScreenPoint(new Vector3(4f, p.y, -3f)),
            cam.WorldToScreenPoint(new Vector3(-4f, p.y, 3f)),
            cam.WorldToScreenPoint(new Vector3(4f, p.y, 3f)),
        };
        var sb = new System.Text.StringBuilder();
        sb.Append("bounds=").Append(mr.bounds.ToString());
        sb.Append("; camPos=").Append(cam.transform.position.ToString("F2"));
        sb.Append("; camFwd=").Append(cam.transform.forward.ToString("F2"));
        sb.Append("; mask=").Append(cam.cullingMask);
        sb.Append("; pts=");
        foreach (var q in pts) sb.Append('(').Append(q.x.ToString("F0")).Append(',').Append(q.y.ToString("F0")).Append(',').Append(q.z.ToString("F0")).Append(") ");
        return sb.ToString();
    }

    public static string Tune(string spec)
    {
        // spec=table:<r>,<g>,<b> | reflection:<strength> | rough:<v>
        var parts = spec.Split(':');
        if (parts.Length != 2) return "ERROR: use table:r,g,b | reflection:value | rough:value";
        if (parts[0] == "table")
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Vchem/Content/Materials/M_TableFade.mat");
            var v = parts[1].Split(',');
            mat.SetColor("_TableColor", new Color(float.Parse(v[0]), float.Parse(v[1]), float.Parse(v[2]), 1f));
            EditorUtility.SetDirty(mat);
            return "OK table=" + mat.GetColor("_TableColor");
        }
        if (parts[0] == "shadow")
        {
            var v = parts[1].Split(',');
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Vchem/Content/Materials/M_ContactShadow.mat");
            mat.SetColor("_Color", new Color(float.Parse(v[0]), float.Parse(v[1]), float.Parse(v[2]), float.Parse(v[3])));
            EditorUtility.SetDirty(mat);
            return "OK shadow=" + mat.GetColor("_Color");
        }
        if (parts[0] == "shadowy")
        {
            var go = GameObject.Find("M3 Contact Shadow");
            go.transform.position = new Vector3(0f, float.Parse(parts[1]), 0f);
            return "OK shadowY=" + go.transform.position;
        }
        if (parts[0] == "reflection" || parts[0] == "rough")
        {
            var prop = parts[0] == "reflection" ? "_ReflectionStrength" : "_ReflectionRoughness";
            foreach (var path in new[] { "Assets/Vchem/Content/Materials/M_GlassOuter.mat", "Assets/Vchem/Content/Materials/M_GlassInner.mat" })
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                mat.SetFloat(prop, float.Parse(parts[1]));
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            return "OK " + prop + "=" + parts[1];
        }
        return "ERROR: unknown target " + parts[0];
    }

    public static string Setup()
    {
        var info = ProbeHdr();
        if (!info.StartsWith("OK")) return info;
        var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(HdrPath);
        if (cube == null) return "ERROR: HDR did not import as cubemap: " + info;

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.path.EndsWith("M0_GlassProbe.unity", StringComparison.OrdinalIgnoreCase))
            return "ERROR: open M0_GlassProbe.unity first";

        // Reflection probe: custom HDRI cubemap, no scene geometry captured.
        var probeGo = GetOrCreate("M3 Reflection Probe");
        var probe = probeGo.GetComponent<ReflectionProbe>();
        if (probe == null) probe = Undo.AddComponent<ReflectionProbe>(probeGo);
        probe.mode = ReflectionProbeMode.Custom;
        probe.customBakedTexture = cube;
        probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
        probe.boxProjection = false;
        probe.size = new Vector3(20f, 20f, 20f);
        probe.center = new Vector3(0f, 0f, 0f);
        probe.intensity = 1f;
        probe.resolution = 256;
        probe.clearFlags = ReflectionProbeClearFlags.Skybox;
        probe.cullingMask = 0;

        // Table plane: y=-1 is the bottom of the M0 probe flask.
        var table = GetOrCreate("M3 Table");
        var mf = table.GetComponent<MeshFilter>();
        if (mf == null) mf = Undo.AddComponent<MeshFilter>(table);
        mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Plane.fbx");
        var mr = table.GetComponent<MeshRenderer>();
        if (mr == null) mr = Undo.AddComponent<MeshRenderer>(table);
        mr.sharedMaterial = LoadMaterial("Assets/Vchem/Content/Materials/M_TableFade.mat", "Vchem/Probe/TableFade");
        table.transform.position = new Vector3(0f, -1.005f, 0f);
        table.transform.rotation = Quaternion.identity;
        table.transform.localScale = new Vector3(8f, 1f, 6f); // built-in plane mesh is 1x1, so this is 8 x 6 m

        // Contact shadow: radial alpha quad, additive to table darkening.
        var shadow = GetOrCreate("M3 Contact Shadow");
        var smf = shadow.GetComponent<MeshFilter>();
        if (smf == null) smf = Undo.AddComponent<MeshFilter>(shadow);
        smf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        var smr = shadow.GetComponent<MeshRenderer>();
        if (smr == null) smr = Undo.AddComponent<MeshRenderer>(shadow);
        var smat = LoadMaterial("Assets/Vchem/Content/Materials/M_ContactShadow.mat", "Vchem/Probe/ContactShadow");
        smat.SetColor("_Color", new Color(0.02f, 0.025f, 0.03f, 0.78f));
        // PNG stores radial falloff in ALPHA with RGB=0. Force importer to read alpha,
        // otherwise Unity uses grayscale (RGB=0) and the decal renders fully transparent.
        const string shadowTexPath = "Assets/Vchem/Content/Textures/VchemContactShadow.png";
        var texImporter = AssetImporter.GetAtPath(shadowTexPath) as TextureImporter;
        if (texImporter != null &&
            (texImporter.alphaSource != TextureImporterAlphaSource.FromInput || !texImporter.alphaIsTransparency))
        {
            texImporter.alphaSource = TextureImporterAlphaSource.FromInput;
            texImporter.alphaIsTransparency = true;
            texImporter.textureCompression = TextureImporterCompression.Uncompressed;
            texImporter.mipmapEnabled = false;
            texImporter.SaveAndReimport();
        }
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(shadowTexPath);
        if (tex != null) { smat.SetTexture("_ShadowTex", tex); EditorUtility.SetDirty(smat); }
        smr.sharedMaterial = smat;
        shadow.transform.position = new Vector3(0f, -0.997f, 0f);
        shadow.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        shadow.transform.localScale = new Vector3(2.2f, 2.2f, 1f);

        // Key: existing direction light. Rim: new one, no shadows.
        var key = GetOrCreate("M3 Key Light");
        var keyLight = key.GetComponent<Light>();
        if (keyLight == null) keyLight = Undo.AddComponent<Light>(key);
        keyLight.type = LightType.Directional;
        keyLight.color = new Color(1.0f, 0.96f, 0.90f);
        keyLight.intensity = 1.0f;
        keyLight.shadows = LightShadows.Soft;
        key.transform.rotation = Quaternion.Euler(50f, -40f, 0f);

        var rim = GetOrCreate("M3 Rim Light");
        var rimLight = rim.GetComponent<Light>();
        if (rimLight == null) rimLight = Undo.AddComponent<Light>(rim);
        rimLight.type = LightType.Directional;
        rimLight.color = new Color(0.90f, 0.96f, 1.0f);
        rimLight.intensity = 0.8f;
        rimLight.shadows = LightShadows.None;
        rim.transform.rotation = Quaternion.Euler(35f, 150f, 0f);

        // Legacy M0 light must not double the key.
        var legacy = GameObject.Find("Directional Light");
        if (legacy != null && legacy != key) legacy.SetActive(false);

        // Hide probe-only clutter: the funnel stem is not part of "one cup" G0.
        var stem = GameObject.Find("FunnelStem");
        if (stem != null) stem.SetActive(false);

        EditorSceneManagerSave();
        AssetDatabase.SaveAssets();
        return "OK: " + Report();
    }

    private static GameObject GetOrCreate(string name)
    {
        var go = GameObject.Find(name);
        if (go != null) return go;
        go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "M3 Stage " + name);
        return go;
    }

    private static Material LoadMaterial(string path, string shaderName)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;
        var shader = Shader.Find(shaderName);
        if (shader == null) { Debug.LogError("M3 stage: shader not found: " + shaderName); return null; }
        mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    private static void EditorSceneManagerSave()
    {
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
    }
}
#endif
