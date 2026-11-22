using System;
using UnityEngine;
using Vchem.Assembly;

namespace Vchem.Showcase
{
    /// <summary>
    /// WP5 演出导演：持有时间轴 + WP4 装配结果 + 8 基元，
    /// Seek(t) 是纯定位——同一 t 无论从何种播放历史到达，画面与状态哈希完全一致。
    /// 镜头基准取装配近景（§10.39 pos=(0.22,-0.78,0.55)/FOV31）；转接推镜 dz=0.12m、俯仰-2°，不环绕。
    /// </summary>
    public sealed class ShowcaseDirector
    {
        public TimelinePlayer Player { get; private set; }
        public EffectPrimitives Effects { get; private set; }
        public AssemblyResult Assembly { get; private set; }
        public ShowcaseState Current { get; private set; }

        private Camera _camera;
        private Vector3 _basePos;
        private Quaternion _baseRot;
        private float _baseFov;
        private bool _cameraCaptured;

        // 捕获时的原始基准（=标准机位），用于切回与 Dispose 还原。
        private Vector3 _capturedPos;
        private Quaternion _capturedRot;
        private float _capturedFov;

        // 视角 0 是运行时捕获的标准机位；1..N 是相对装置世界坐标的预设机位。
        // 视角是"用户选择状态"，不进入 ShowcaseState.Serialize，因此不改变时间轴哈希与 Seek 确定性：
        // 给定同一视角 + 同一 t，画面完全一致。
        private struct ViewPose
        {
            public string Name;
            public Vector3 Pos;
            public Vector3 Look;
            public float Fov;
        }
        private static readonly ViewPose[] Presets =
        {
            new ViewPose { Name = "全景",   Pos = new Vector3(0.30f, -0.62f, 0.88f), Look = new Vector3(0f, -0.90f, 0f), Fov = 38f },
            new ViewPose { Name = "俯视",   Pos = new Vector3(0.12f, -0.28f, 0.62f), Look = new Vector3(0f, -0.92f, 0f), Fov = 40f },
            new ViewPose { Name = "集气瓶", Pos = new Vector3(0.18f, -0.76f, 0.44f), Look = new Vector3(-0.045f, -0.90f, 0f), Fov = 33f },
        };
        private int _viewIndex;

        /// <summary>视角总数（含标准机位）。</summary>
        public int ViewCount { get { return 1 + Presets.Length; } }
        /// <summary>当前视角序号。</summary>
        public int ViewIndex { get { return _viewIndex; } }
        /// <summary>指定序号的中文名。</summary>
        public string ViewName(int index)
        {
            if (index <= 0) return "标准";
            if (index - 1 < Presets.Length) return Presets[index - 1].Name;
            return "标准";
        }
        public string CurrentViewName { get { return ViewName(_viewIndex); } }

        public ShowcaseDirector(TimelinePlayer player, AssemblyResult asm,
            ProfileCatalog catalog, ShowcaseAssets assets)
        {
            Player = player;
            Assembly = asm;
            Effects = new EffectPrimitives();
            Effects.Build(asm, catalog, assets);
        }

        /// <summary>捕获当前编辑器相机为演出基准位姿（装配近景 / 标准视角）。</summary>
        public void CaptureCamera(Camera cam)
        {
            _camera = cam;
            if (cam == null) return;
            _basePos = _capturedPos = cam.transform.position;
            _baseRot = _capturedRot = cam.transform.rotation;
            _baseFov = _capturedFov = cam.fieldOfView;
            _cameraCaptured = true;
            _viewIndex = 0;
        }

        /// <summary>切换到指定视角（0=标准机位）。切换后按当前 t 立即重算画面。</summary>
        public void SetView(int index)
        {
            if (!_cameraCaptured || _camera == null) return;
            int count = ViewCount;
            _viewIndex = ((index % count) + count) % count;
            if (_viewIndex == 0)
            {
                _basePos = _capturedPos;
                _baseRot = _capturedRot;
                _baseFov = _capturedFov;
            }
            else
            {
                ViewPose v = Presets[_viewIndex - 1];
                _basePos = v.Pos;
                _baseRot = Quaternion.LookRotation(v.Look - v.Pos, Vector3.up);
                _baseFov = v.Fov;
            }
            _camera.fieldOfView = _baseFov;
            if (Current != null) ApplyCamera(Current);
        }

        /// <summary>循环切到下一个视角。</summary>
        public void NextView() { SetView(_viewIndex + 1); }

        public ShowcaseState Seek(float t)
        {
            Current = Player.Evaluate(t);
            Effects.Apply(Current);
            ApplyCamera(Current);
            return Current;
        }

        private void ApplyCamera(ShowcaseState s)
        {
            if (!_cameraCaptured || _camera == null) return;
            float push = Mathf.Clamp01(s.CameraPush);
            // 方案 3.5.6：转接推镜沿视线 + 轻微俯仰，不做环绕。
            // M4：位移/俯仰由 camera.push 事件的 dz/dpitch 驱动，Director 不再写死数值。
            // 时间轴与合成器里的值已按示例近景收口为 0.12m / -2.0°，改动事件即可改画面。
            Vector3 forward = _baseRot * Vector3.forward;
            Vector3 pos = _basePos + forward * (s.CameraPushDz * push);
            Quaternion rot = _baseRot * Quaternion.Euler(s.CameraPushPitch * push, 0f, 0f);
            _camera.transform.SetPositionAndRotation(pos, rot);
            if (!Mathf.Approximately(_camera.fieldOfView, _baseFov)) _camera.fieldOfView = _baseFov;
        }

        public void Dispose()
        {
            if (Effects != null) Effects.Clear();
            // H7/M15：Seek 会推动相机，清理时必须还原到捕获时的基准位姿，
            // 否则切场景或重复 Prepare 会从推近位继续推，装置被裁且漂移累积。
            if (_camera != null && _cameraCaptured)
            {
                _camera.transform.SetPositionAndRotation(_capturedPos, _capturedRot);
                _camera.fieldOfView = _capturedFov;
            }
            _camera = null;
            _cameraCaptured = false;
        }
    }
}
