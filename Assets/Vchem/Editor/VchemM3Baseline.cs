#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// WP0 baseline setup for the M3 demo stage.
/// Call through DSHBridge: exec?method=VchemM3Baseline.Setup / .Report
/// This file only touches the 3D probe scene and M3-owned assets; M2 is untouched.
/// </summary>
public static class VchemM3Baseline
{
    private const string ProfilePath = "Assets/Vchem/Content/Settings/VchemM3Profile.asset";
    // Linear camera clear value. After ACES + grading this renders close to #323940.
    private static readonly Color Background = new Color(0.342f, 0.370f, 0.395f, 1f);

    public static string Setup()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.path.EndsWith("M0_GlassProbe.unity", StringComparison.OrdinalIgnoreCase))
            return "ERROR: open Assets/Vchem/Scenes/M0_GlassProbe.unity first";

        var camera = Camera.main;
        if (camera == null) return "ERROR: no Main Camera";

        // Fixed camera: same pose/fov every run, so screenshots are comparable.
        camera.transform.position = new Vector3(0.8f, 0.5f, 4.4f);
        camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0f, 0f) - camera.transform.position);
        camera.fieldOfView = 35f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 200f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Background;
        camera.allowHDR = true;
        camera.allowMSAA = true;

        var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
        if (cameraData == null) cameraData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = true;

        var profile = LoadOrCreateProfile();
        ConfigureProfile(profile);

        var volumeGo = GameObject.Find("M3 Global Volume");
        if (volumeGo == null)
        {
            volumeGo = new GameObject("M3 Global Volume");
            Undo.RegisterCreatedObjectUndo(volumeGo, "M3 Baseline Volume");
        }
        var volume = volumeGo.GetComponent<Volume>();
        if (volume == null) volume = Undo.AddComponent<Volume>(volumeGo);
        volume.isGlobal = true;
        volume.priority = 0f;
        volume.sharedProfile = profile;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return "OK: " + Report();
    }

    public static string Report()
    {
        var camera = Camera.main;
        if (camera == null) return "ERROR: no Main Camera";
        var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
        var rp = GraphicsSettings.currentRenderPipeline;
        var sb = new StringBuilder();
        sb.Append("pipeline=").Append(rp != null ? rp.name : "built-in");
        sb.Append("; opaque=").Append(ReadAssetInt(rp, "m_RequireOpaqueTexture"));
        sb.Append("; depth=").Append(ReadAssetInt(rp, "m_RequireDepthTexture"));
        sb.Append("; hdr=").Append(ReadAssetInt(rp, "m_SupportsHDR"));
        sb.Append("; post=").Append(cameraData != null && cameraData.renderPostProcessing);
        sb.Append("; fov=").Append(camera.fieldOfView.ToString("0.##"));
        sb.Append("; clear=").Append(camera.backgroundColor.ToString());
        sb.Append("; profile=").Append(ProfilePath);

        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (profile != null)
            sb.Append("; components=").Append(string.Join(",", profile.components.Select(c => c != null ? c.GetType().Name : "null")));
        return sb.ToString();
    }

    private static VolumeProfile LoadOrCreateProfile()
    {
        // Rebuild from scratch: an earlier run left orphaned sub-assets with null refs.
        var existing = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (existing != null) AssetDatabase.DeleteAsset(ProfilePath);

        var diskDir = Path.GetDirectoryName(Path.Combine(Application.dataPath, "..", ProfilePath));
        if (!string.IsNullOrEmpty(diskDir) && !Directory.Exists(diskDir)) Directory.CreateDirectory(diskDir);

        var profile = UnityEditor.Rendering.VolumeProfileFactory.CreateVolumeProfileAtPath(ProfilePath);
        return profile;
    }

    private static void ConfigureProfile(VolumeProfile profile)
    {
        // A previous run may have left dangling refs; drop them before rebuilding.
        profile.components.RemoveAll(c => c == null);

        GetOrAdd<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);

        var bloom = GetOrAdd<Bloom>(profile);
        bloom.threshold.Override(1.05f);
        bloom.intensity.Override(0.30f);
        bloom.scatter.Override(0.72f);

        var dof = GetOrAdd<DepthOfField>(profile);
        dof.mode.Override(DepthOfFieldMode.Gaussian);
        dof.gaussianStart.Override(5.0f);
        dof.gaussianEnd.Override(9.0f);
        dof.gaussianMaxRadius.Override(0.4f);
        dof.highQualitySampling.Override(true);

        var color = GetOrAdd<ColorAdjustments>(profile);
        color.contrast.Override(12f);
        color.saturation.Override(2f);

        // G2 requires a pure background (std < 1.5); vignette would break it.
        GetOrAdd<Vignette>(profile).intensity.Override(0f);
        EditorUtility.SetDirty(profile);
    }

    private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        T component;
        if (!profile.TryGet(out component) || component == null)
            component = UnityEditor.Rendering.VolumeProfileFactory.CreateVolumeComponent<T>(profile, true, true);
        component.active = true;
        EditorUtility.SetDirty(component);
        return component;
    }

    private static int ReadAssetInt(UnityEngine.Object asset, string field)
    {
        if (asset == null) return -1;
        var so = new SerializedObject(asset);
        var prop = so.FindProperty(field);
        return prop == null ? -1 : prop.intValue;
    }
}
#endif
