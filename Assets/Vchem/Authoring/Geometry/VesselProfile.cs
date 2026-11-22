using System;
using System.Collections.Generic;
using UnityEngine;
using Vchem.Core.Contracts;

namespace Vchem.Authoring.Geometry
{
    /// <summary>剖面控制点：y 轴向高度、r 半径，单位 mm。kind 仅作编辑期标注。</summary>
    [Serializable]
    public struct ProfilePoint
    {
        public float y;
        public float r;
        public ProfilePoint(float y, float r) { this.y = y; this.r = r; }
    }

    [Serializable]
    public struct ProfileAnchor
    {
        public float y;
        public float r;
        public string dir;
        public int pathIndex;
    }

    /// <summary>
    /// 一份器皿剖面真值。从 Content/Profiles/*.profile.json 反序列化。
    /// 只存参数，不存顶点——顶点由 LatheMeshBuilder / SweepBuilder 生成。
    /// </summary>
    [Serializable]
    public sealed class VesselProfile
    {
        public string type;
        public string name;
        public string kind;          // lathe | sweep | box_shell | extrusion
        public string source;
        public string unit;          // mm
        public float height;
        public float wall;
        public bool closedBottom;
        public int segments = 64;
        public float maxChord = 0.04f;

        public List<ProfilePoint> profile = new List<ProfilePoint>();

        // sweep 专用
        public float outerRadius;
        public int radialSegments = 20;
        public List<Vector3> path = new List<Vector3>();
        public List<float> pathY;    // JSON 里 path 是 [x,y,z]

        // box_shell / extrusion 专用，保留原始 JSON 供 builder 读
        public Dictionary<string, object> raw;

        public Dictionary<string, ProfileAnchor> anchors =
            new Dictionary<string, ProfileAnchor>(StringComparer.Ordinal);

        public static VesselProfile Load(string assetPath)
        {
            return LoadText(System.IO.File.ReadAllText(assetPath));
        }

        /// <summary>从 JSON 文本解析，供 Resources.Load&lt;TextAsset&gt; 的打包路径使用（Player 无文件系统路径可读）。</summary>
        public static VesselProfile LoadText(string text)
        {
            var map = Json.ParseObject(text);
            var p = new VesselProfile();
            p.raw = map;
            p.type = Json.GetString(map, "type", "");
            p.name = Json.GetString(map, "name", "");
            p.kind = Json.GetString(map, "kind", "lathe");
            p.source = Json.GetString(map, "source", "");
            p.unit = Json.GetString(map, "unit", "mm");
            p.height = (float)Json.GetNumber(map, "height", 0);
            p.wall = (float)Json.GetNumber(map, "wall", 1);
            p.closedBottom = Json.GetBool(map, "closed_bottom", true);
            p.segments = (int)Json.GetNumber(map, "segments", 64);
            p.maxChord = (float)Json.GetNumber(map, "maxChord", 0.04);
            p.outerRadius = (float)Json.GetNumber(map, "outer_radius", 4);
            p.radialSegments = (int)Json.GetNumber(map, "radial_segments", 20);

            var pts = Json.GetArray(map, "profile");
            if (pts != null)
            {
                foreach (var item in pts)
                {
                    var pair = item as List<object>;
                    if (pair == null || pair.Count < 2) continue;
                    p.profile.Add(new ProfilePoint(
                        (float)Convert.ToDouble(pair[0]),
                        (float)Convert.ToDouble(pair[1])));
                }
            }

            var pathArr = Json.GetArray(map, "path");
            if (pathArr != null)
            {
                foreach (var item in pathArr)
                {
                    var triple = item as List<object>;
                    if (triple == null || triple.Count < 3) continue;
                    p.path.Add(new Vector3(
                        (float)Convert.ToDouble(triple[0]),
                        (float)Convert.ToDouble(triple[1]),
                        (float)Convert.ToDouble(triple[2])));
                }
            }

            var amap = Json.GetObject(map, "anchors");
            if (amap != null)
            {
                foreach (var kv in amap)
                {
                    var a = kv.Value as Dictionary<string, object>;
                    if (a == null) continue;
                    var anchor = new ProfileAnchor();
                    anchor.y = (float)Json.GetNumber(a, "y", 0);
                    anchor.r = (float)Json.GetNumber(a, "r", 0);
                    anchor.dir = Json.GetString(a, "dir", "none");
                    anchor.pathIndex = (int)Json.GetNumber(a, "path_index", 0);
                    p.anchors[kv.Key] = anchor;
                }
            }
            return p;
        }

        public float MaxRadius
        {
            get
            {
                float m = 0f;
                for (int i = 0; i < profile.Count; i++) m = Mathf.Max(m, profile[i].r);
                return m;
            }
        }
    }
}
