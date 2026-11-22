using System;
using System.Collections.Generic;
using UnityEngine;
using Vchem.Authoring.Geometry;
using Vchem.Core.Model;

namespace Vchem.Assembly
{
    public struct AssemblyPose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Scale;
    }

    public sealed class AssemblyNode
    {
        public string Id;
        public string Type;
        public Node Model;
        public VesselProfile Profile;      // 导管节点指向按端点生成的临时 profile
        public AssemblyPose Pose;
        public Mesh GeneratedMesh;         // 仅导管程序生成，其余来自 Mesh 资产
        public List<Vector3> PathWorld = new List<Vector3>(); // 导管中心线（世界坐标，米）
        public bool Grounded;

        public Vector3 AnchorWorld(string key, Vector3 slotOffsetMm)
        {
            Vector3 local = Profile != null ? ProfileCatalog.AnchorLocalStatic(Profile, key) : Vector3.zero;
            Vector3 offset = new Vector3(slotOffsetMm.x, 0f, slotOffsetMm.z) * 0.001f;
            return Pose.Position + Pose.Rotation * (local * Pose.Scale + offset);
        }
    }

    public sealed class EndpointError
    {
        public string Edge;
        public string From;
        public string To;
        public float DistanceMm;
    }

    public sealed class InterferenceHit
    {
        public string A;
        public string B;
        public string PartA;
        public string PartB;
        public float OverlapMm;
    }

    /// <summary>Editor 包装器注入的资产；Runtime 侧不直接碰 AssetDatabase。</summary>
    public sealed class AssemblyAssets
    {
        public readonly Dictionary<string, Mesh> Meshes =
            new Dictionary<string, Mesh>(StringComparer.Ordinal);
        public Material GlassOuter;
        public Material GlassInner;
        public Material Opaque;
        public Material Shadow;
        public Mesh Quad;
        public Mesh Cylinder;
    }

    public sealed class AssemblyResult
    {
        public GameObject Root;
        public readonly List<AssemblyNode> Nodes = new List<AssemblyNode>();
        public readonly List<EndpointError> EndpointErrors = new List<EndpointError>();
        public readonly List<InterferenceHit> Interference = new List<InterferenceHit>();
        /// <summary>M9：未知 profile、重复 id、缺失 anchor 等解析问题，Prepare/验收据此拒绝装配。</summary>
        public readonly List<string> Errors = new List<string>();
        public string Hash = "";
        public float MaxEndpointErrorMm;
        public int GroundedCount;
    }
}
