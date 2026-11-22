using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Vchem.Authoring.Geometry;

/// <summary>
/// WP3 几何烘焙器：读 Profiles/*.profile.json → 生成/更新 Mesh 资产 → 自检 → 出报告。
/// 可重复执行，不改 M2 与 G0/G1 已固化场景参数。
/// </summary>
public static class VchemGeometryBaker
{
        const string ProfileDir = "Assets/Vchem/Content/Profiles";
        const string MeshDir = "Assets/Vchem/Content/Meshes";
        const string RuntimeMeshDir = "Assets/Vchem/Resources/VchemContent/Meshes";
        const string ReportName = "geometry_report.csv";

        static readonly string[] Types = {
            "reactor.flask_conical",
            "reactor.test_tube",
            "feed.thistle_funnel",
            "collect.gas_jar",
            "conduit.bent",
            "collect.water_trough",
            "support.stand",
        };

        [MenuItem("Vchem/M3/Bake Geometry")]
        public static string Bake()
        {
            if (!AssetDatabase.IsValidFolder(MeshDir))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Vchem/Content"))
                    AssetDatabase.CreateFolder("Assets/Vchem", "Content");
                AssetDatabase.CreateFolder("Assets/Vchem/Content", "Meshes");
            }

            var sb = new StringBuilder();
            sb.AppendLine("type,name,kind,vertices,triangles,boundaryEdges,watertight," +
                          "wallMaxError_mm,volumeMesh_mm3,volumeTheory_mm3,volumeError,profileRms_mm");

            foreach (var type in Types)
            {
                string assetPath = ProfileDir + "/" + type + ".profile.json";
                if (!System.IO.File.Exists(assetPath))
                {
                    sb.AppendLine(type + ",MISSING_PROFILE,,,,,,,,,");
                    continue;
                }

                VesselProfile p;
                Mesh mesh;
                GeometryStats st;
                try
                {
                    p = VesselProfile.Load(assetPath);
                    switch (p.kind)
                    {
                        case "lathe": mesh = LatheMeshBuilder.Build(p, out st); break;
                        case "sweep": mesh = SweepBuilder.Build(p, out st); break;
                        case "box_shell":
                        case "extrusion": mesh = PrimitiveBuilder.Build(p, out st); break;
                        default: throw new Exception("unknown kind " + p.kind);
                    }
                }
                catch (Exception e)
                {
                    sb.AppendLine(type + ",ERROR," + e.Message.Replace(",", ";") + ",,,,,,,,");
                    Debug.LogError("[VchemGeometryBaker] " + type + " failed: " + e);
                    continue;
                }

                string meshPath = MeshDir + "/" + type + ".asset";
                var old = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (old != null)
                {
                    EditorUtility.CopySerialized(mesh, old);
                    EditorUtility.SetDirty(old);
                    UnityEngine.Object.DestroyImmediate(mesh);
                    mesh = old;
                }
                else AssetDatabase.CreateAsset(mesh, meshPath);

                // Player/Editor 统一从 Resources 取网格。必须在这里同步对象本身，
                // 不能靠工程外 cp 覆盖 .asset：Unity 的 Resources.Load 缓存会继续
                // 返回旧的 Mesh 对象，画面看起来完全没变。
                SyncRuntimeMesh(mesh, type);

                sb.AppendLine(string.Format(
                    "{0},{1},{2},{3},{4},{5},{6},{7:F4},{8:F1},{9:F1},{10:F4},{11:F4}",
                    p.type, p.name, p.kind, st.vertices, st.triangles, st.boundaryEdges,
                    st.watertight, st.wallMaxError, st.volumeMesh, st.volumeTheory,
                    st.volumeError, st.profileRms));
                Debug.Log("[VchemGeometryBaker] " + p.name + ": " + st.Report());
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 报告写到工程外：.workbuddy/artifacts/m3/<run>/（本目录写 csv 副本也方便查看）
            WriteReportOutside(sb.ToString());
            return sb.ToString();
        }

        static void SyncRuntimeMesh(Mesh baked, string type)
        {
            if (baked == null) return;
            string dir = RuntimeMeshDir;
            if (!AssetDatabase.IsValidFolder(dir))
            {
                string res = "Assets/Vchem/Resources";
                if (!AssetDatabase.IsValidFolder(res)) AssetDatabase.CreateFolder("Assets/Vchem", "Resources");
                string content = res + "/VchemContent";
                if (!AssetDatabase.IsValidFolder(content)) AssetDatabase.CreateFolder(res, "VchemContent");
                AssetDatabase.CreateFolder(content, "Meshes");
            }
            string path = dir + "/" + type + ".asset";
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (old != null)
            {
                if (old.vertexCount == baked.vertexCount && old.triangles.Length == baked.triangles.Length)
                {
                    // 顶点数相同也可能剖面不同，仍按位置数组做一次廉价比对。
                    var a = old.vertices;
                    var b = baked.vertices;
                    bool same = a.Length == b.Length;
                    for (int i = 0; same && i < a.Length; i++) same = a[i] == b[i];
                    if (same) return;
                }
                EditorUtility.CopySerialized(baked, old);
                EditorUtility.SetDirty(old);
                return;
            }
            AssetDatabase.CopyAsset(MeshDir + "/" + type + ".asset", path);
        }

        static void WriteReportOutside(string csv)
        {
            try
            {
                string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                string workspace = System.IO.Directory.GetParent(projectRoot).FullName; // vchem/
                string runFile = System.IO.Path.Combine(workspace, ".workbuddy", "artifacts", "m3", "current_run.txt");
                if (!System.IO.File.Exists(runFile)) return;
                string run = System.IO.File.ReadAllText(runFile).Trim();
                string outDir = System.IO.Path.Combine(workspace, ".workbuddy", "artifacts", "m3", run);
                System.IO.Directory.CreateDirectory(outDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, ReportName), csv, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VchemGeometryBaker] 报告外写失败（不影响烘焙）: " + e.Message);
            }
        }

        /// <summary>诊断 Resources.Load 实际拿到的集气瓶网格（排除工程外复制导致的缓存假更新）。</summary>
        public static string LoadedJarMesh()
        {
            Mesh res = Resources.Load<Mesh>("VchemContent/Meshes/collect.gas_jar");
            string a = DescribeJarMesh("resources", res);
            Mesh sceneMesh = null;
            GameObject exact = GameObject.Find("ASM_n4_collect_gas_jar");
            if (exact != null)
            {
                var mf = exact.GetComponent<MeshFilter>();
                if (mf != null) sceneMesh = mf.sharedMesh;
            }
            if (sceneMesh == null)
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || !go.name.Contains("collect_gas_jar")) continue;
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) { sceneMesh = mf.sharedMesh; break; }
                }
            }
            return a + " | " + DescribeJarMesh("scene", sceneMesh);
        }

        static string DescribeJarMesh(string label, Mesh m)
        {
            if (m == null) return label + "=missing";
            float body = 0f, neck = 0f;
            foreach (var p in m.vertices)
            {
                float r = Mathf.Sqrt(p.x * p.x + p.z * p.z) * 130f;
                float y = p.y * 130f;
                if (y > 40f && y < 90f) body = Mathf.Max(body, r);
                if (y > 115f && y < 122f) neck = Mathf.Max(neck, r);
            }
            return string.Format("{0}:name={1} verts={2} bodyR={3:F2}mm neckR={4:F2}mm",
                label, m.name, m.vertexCount, body, neck);
        }

        /// <summary>报告当前 7 件网格资产是否都已生成。</summary>
        public static string Status()
        {
            var sb = new StringBuilder();
            foreach (var type in Types)
            {
                string path = MeshDir + "/" + type + ".asset";
                var m = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                sb.Append(type).Append("=").Append(m != null
                    ? string.Format("v{0}/t{1}", m.vertexCount, m.triangles.Length / 3)
                    : "MISSING").Append("; ");
            }
            return sb.ToString();
        }
}
