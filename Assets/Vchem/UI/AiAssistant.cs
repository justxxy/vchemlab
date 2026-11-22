using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Vchem.Core.Contracts;
using Vchem.Handoff;

namespace Vchem.UI
{
    /// <summary>
    /// 本机 AI 助教服务的唯一客户端。API Key 只存在于 ai-service/.env，
    /// Unity 只访问 127.0.0.1，因此构建产物里没有任何密钥。
    /// </summary>
    public static class AiClient
    {
        public const string BaseUrl = "http://127.0.0.1:8000";

        public static string BuildBody(Submission s, string mode, string question = "",
            string level = "L1", string keyframe = "")
        {
            string plan = CleanJson(s == null ? "" : s.PlanJson);
            string result = CleanJson(s == null ? "" : s.ResultJson);
            string kf = string.IsNullOrEmpty(keyframe) ? "null" : Json.Stringify(keyframe);
            return "{" +
                   "\"mode\":" + Json.Stringify(mode) +
                   ",\"plan\":" + plan +
                   ",\"result\":" + result +
                   ",\"question\":" + Json.Stringify(question ?? "") +
                   ",\"level\":" + Json.Stringify(level ?? "L1") +
                   ",\"keyframe\":" + kf +
                   "}";
        }

        private static string CleanJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return "{}";
            text = text.Trim().TrimStart('\uFEFF');
            return text.StartsWith("{") ? text : "{}";
        }

        public static IEnumerator Post(string mode, string body,
            Action<Dictionary<string, object>> ok, Action<string> fail)
        {
            string url = BaseUrl + "/v1/" + mode;
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                byte[] payload = Encoding.UTF8.GetBytes(body ?? "{}");
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 30;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    fail?.Invoke("助教服务暂不可用，请先启动本机助教服务。");
                    yield break;
                }
                try
                {
                    Dictionary<string, object> obj = Json.ParseObject(req.downloadHandler.text);
                    if (obj == null) fail?.Invoke("助教返回内容格式不正确。");
                    else ok?.Invoke(obj);
                }
                catch (Exception)
                {
                    fail?.Invoke("助教返回内容无法解析。");
                }
            }
        }
    }

    /// <summary>M2 诊断卡里的“问助教”弹层：解释 / 分层提示 / 提问 / 学习小结。</summary>
    public sealed class AiAssistantPanel : MonoBehaviour
    {
        private Submission _sub;
        private string _keyframe = "";
        private Text _status, _answer;
        private InputField _question;
        private bool _busy;
        private Font _font;
#if UNITY_EDITOR
        // 原始 provider/safety 只进 Editor 诊断出口，不进 UI/Player（避免英文技术字样露给玩家）
        private string _rawProvider = "", _rawSafety = "";
#endif

        // 兜底：回答正文万一夹带 R-XXX-00 / [xxx.xxx] 码，统一抹掉（与 PlanDesigner 显示层同源）
        private static readonly System.Text.RegularExpressions.Regex DisplayCodeRx =
            new System.Text.RegularExpressions.Regex(
                @"(?:[RDE]-[A-Z0-9]{2,4}-\d{2})|(?:\[[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string Humanize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = DisplayCodeRx.Replace(s, "");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }

        /// <summary>
        /// 统一销毁：编辑态必须用 DestroyImmediate（Destroy 在 edit mode 会报错且延后到帧末，
        /// 暂停态留下残影）；运行态先隐藏再 Destroy，避免编辑器暂停时旧弹层仍可见。
        /// </summary>
        private static void SafeDestroy(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        public static void Open(Submission s, string keyframe = "")
        {
            if (s == null)
            {
                Debug.LogWarning("[AI] no submission");
                return;
            }
            GameObject old = GameObject.Find("AiAssistantPanel");
            if (old != null) SafeDestroy(old);

            GameObject root = new GameObject("AiAssistantPanel");
            AiAssistantPanel panel = root.AddComponent<AiAssistantPanel>();
            panel._sub = s;
            panel._keyframe = keyframe ?? "";
            panel.Build();
        }

        private void Build()
        {
            _font = Resources.Load<Font>("Fonts/NotoSansSC-VF");
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            Canvas canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 600;
            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 1f;
            gameObject.AddComponent<GraphicRaycaster>();

            GameObject bg = Panel(transform, "BG", new Vector2(0, 0), new Vector2(1280, 720), new Color(0f, 0f, 0f, 0.42f));
            bg.transform.SetAsFirstSibling();

            GameObject box = Panel(transform, "Box", new Vector2(230, 80), new Vector2(820, 560), new Color(0.99f, 0.975f, 0.93f));
            MakeText(box.transform, "智能助教（CO₂ 制取与检验）", new Vector2(24, 512), new Vector2(600, 30), 20, FontStyle.Bold, new Color(0.12f, 0.18f, 0.25f));
            string line = "结果：" + ResultCn(_sub.Verdict, _sub.Class) + " · 关键帧：" +
                          (string.IsNullOrEmpty(_keyframe) ? "无" : _keyframe);
            MakeText(box.transform, line, new Vector2(24, 486), new Vector2(700, 22), 13, FontStyle.Normal, new Color(0.35f, 0.4f, 0.46f));

            Button close = MakeButton(box.transform, "关闭", new Vector2(736, 512), new Vector2(60, 30), Close);
            close.GetComponent<Image>().color = new Color(0.9f, 0.92f, 0.95f);

            MakeButton(box.transform, "解释这次结果", new Vector2(24, 440), new Vector2(150, 34), () => Ask("explain", "L1"));
            MakeButton(box.transform, "L1 提示", new Vector2(182, 440), new Vector2(92, 34), () => Ask("hint", "L1"));
            MakeButton(box.transform, "L2 原理", new Vector2(282, 440), new Vector2(92, 34), () => Ask("hint", "L2"));
            MakeButton(box.transform, "L3 操作", new Vector2(382, 440), new Vector2(92, 34), () => Ask("hint", "L3"));
            MakeButton(box.transform, "学习小结", new Vector2(482, 440), new Vector2(110, 34), () => Ask("report", "L1"));

            _question = MakeInput(box.transform, new Vector2(24, 392), new Vector2(600, 34), "问一个与本实验有关的问题，例如“木条为什么会熄灭？”");
            MakeButton(box.transform, "提问", new Vector2(632, 392), new Vector2(80, 34), () => Ask("ask", "L1"));

            _status = MakeText(box.transform, "点上面的按钮开始提问，或点“解释这次结果”。", new Vector2(24, 364), new Vector2(760, 22), 12, FontStyle.Normal, new Color(0.4f, 0.45f, 0.5f));

            // 回答区（ScrollRect + Text）
            GameObject view = Panel(box.transform, "View", new Vector2(24, 24), new Vector2(760, 330), new Color(1f, 1f, 1f, 0.75f));
            ScrollRect scroll = view.AddComponent<ScrollRect>();
            GameObject content = new GameObject("Content");
            content.transform.SetParent(view.transform, false);
            RectTransform crt = content.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1);
            crt.anchorMax = new Vector2(1, 1);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = new Vector2(0, -8);
            crt.sizeDelta = new Vector2(-16, 0);
            VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            ContentSizeFitter cf = content.AddComponent<ContentSizeFitter>();
            cf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _answer = MakeText(content.transform, "", new Vector2(8, 0), new Vector2(744, 320), 14, FontStyle.Normal, new Color(0.12f, 0.14f, 0.18f));
            _answer.alignment = TextAnchor.UpperLeft;
            scroll.content = crt;
            scroll.viewport = view.GetComponent<RectTransform>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            view.AddComponent<RectMask2D>();
            view.GetComponent<Image>().raycastTarget = true;
        }

#if UNITY_EDITOR
        /// <summary>Editor 桥接诊断入口：触发一次“解释这次结果”，不进 Player。</summary>
        public void ExplainForTest() { Ask("explain", "L1"); }

        /// <summary>Editor 桥接诊断入口：按 mode/level/question 触发助教请求，不进 Player。</summary>
        public void AskForTest(string spec)
        {
            string[] p = (spec ?? "").Split(':');
            string mode = p.Length > 0 ? p[0] : "explain";
            string level = p.Length > 1 ? p[1] : "L1";
            string question = p.Length > 2 ? p[2] : "";
            // 问题含中文冒号/问号时 Split 会截断，用剩余部分拼回
            if (p.Length > 3) question = string.Join(":", p, 2, p.Length - 2);
            if (mode == "ask" && _question != null)
                _question.text = question;
            Ask(mode, level);
        }

        /// <summary>Editor 桥接诊断：返回 UI 中文文本 + 原始 provider/safety（仅供验证，不进界面/Player）。</summary>
        public string DiagText()
        {
            string s = _status == null ? "" : (_status.text ?? "");
            string a = _answer == null ? "" : (_answer.text ?? "");
            return "STATUS=" + s + "\nRAW=provider=" + _rawProvider + " · safety=" + _rawSafety +
                   "\nANSWER=" + (a.Length > 400 ? a.Substring(0, 400) : a);
        }
#endif

        private void Ask(string mode, string level)
        {
            if (_busy) return;            string q = mode == "ask" ? (_question == null ? "" : _question.text) : "";
            if (mode == "ask" && string.IsNullOrEmpty(q))
            {
                _status.text = "先输入一个问题，或直接点“解释这次结果”。";
                return;
            }
            _busy = true;
            _status.text = "助教思考中…";
            _answer.text = "";
            string body = AiClient.BuildBody(_sub, mode, q, level, _keyframe);
            StartCoroutine(AiClient.Post(mode, body, OnOk, OnErr));
        }

        private void OnOk(Dictionary<string, object> obj)
        {
            _busy = false;
            string answer = Json.GetString(obj, "answer", "");
            string next = Json.GetString(obj, "next_action", "");
            string safety = Json.GetString(obj, "safety", "ok");
            string provider = Json.GetString(obj, "provider", "");
            StringBuilder sb = new StringBuilder();
            sb.Append(answer);
            if (!string.IsNullOrEmpty(next)) sb.Append("\n\n下一步：").Append(next);
            List<object> cites = Json.GetArray(obj, "citations");
            if (cites != null && cites.Count > 0)
            {
                sb.Append("\n\n参考来源：");
                foreach (object c in cites)
                {
                    Dictionary<string, object> m = c as Dictionary<string, object>;
                    if (m == null) continue;
                    sb.Append("\n· ").Append(Json.GetString(m, "source", ""));
                }
            }
            _answer.text = Humanize(sb.ToString());
#if UNITY_EDITOR
            _rawProvider = provider;
            _rawSafety = safety;
#endif
            _status.text = safety == "refuse"
                ? "已按实验安全规范拒答，不提供危险操作。"
                : "已根据当前实验情况作答" + (provider == "cloud" ? "（在线模式）" : "（本地示例模式）") + "。";
            _status.color = safety == "refuse" ? new Color(0.72f, 0.2f, 0.15f) : new Color(0.32f, 0.42f, 0.5f);
        }

        private void OnErr(string message)
        {
            _busy = false;
            _answer.text = message;
            _status.text = "助教服务连接失败，请确认本机助教服务已启动。";
            _status.color = new Color(0.72f, 0.2f, 0.15f);
        }

        private void Close()
        {
            SafeDestroy(gameObject);
        }

        // ---------------------------------------------------------------- 显示层：结果中文化
        // 判定码（R-OK-01 / R-RXN-04 等）、verdict 英文只用于核心判定与交接，不进 UI。
        private static string ResultCn(string verdict, string cls)
        {
            switch (verdict)
            {
                case "success": return cls == "R-OK-02" ? "装置正确，尚缺检验或验满" : "实验成功";
                case "partial": return "部分达成";
                case "fail":
                    switch (cls)
                    {
                        case "R-RXN-04": return "固液同瓶直投，判定拒绝";
                        case "R-RXN-05": return "固体试剂选择错误";
                        case "R-COL-01": return "长颈漏斗未液封";
                        case "R-COL-02": return "导管未伸到集气瓶底";
                        case "R-COL-03": return "集气瓶未盖玻璃片";
                        case "R-COL-04": return "集气瓶倒置";
                        case "R-COL-05": return "收集方法不当";
                        default: return "实验失败";
                    }
                case "none": return "无现象";
                default: return "结构尚未完成";
            }
        }

        // ---------------------------------------------------------------- UI helpers

        private GameObject Panel(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            Image img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            return go;
        }

        private Text MakeText(Transform parent, string text, Vector2 pos, Vector2 size, int fontSize, FontStyle style, Color color)
        {
            GameObject go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            Text t = go.AddComponent<Text>();
            t.font = _font; t.text = text; t.fontSize = fontSize; t.fontStyle = style; t.color = color;
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        private Button MakeButton(Transform parent, string label, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction action)
        {
            GameObject go = Panel(parent, "Btn_" + label, pos, size, new Color(0.86f, 0.91f, 0.96f));
            Button b = go.AddComponent<Button>();
            b.targetGraphic = go.GetComponent<Image>();
            b.onClick.AddListener(action);
            Text t = MakeText(go.transform, label, new Vector2(4, 3), new Vector2(size.x - 8, size.y - 6), 13, FontStyle.Normal, new Color(0.12f, 0.18f, 0.26f));
            t.alignment = TextAnchor.MiddleCenter;
            return b;
        }

        private InputField MakeInput(Transform parent, Vector2 pos, Vector2 size, string placeholder)
        {
            GameObject go = Panel(parent, "Input", pos, size, new Color(1f, 1f, 1f, 0.95f));
            InputField input = go.AddComponent<InputField>();
            Text text = MakeText(go.transform, "", new Vector2(8, 4), new Vector2(size.x - 16, size.y - 8), 13, FontStyle.Normal, Color.black);
            text.alignment = TextAnchor.MiddleLeft;
            Text ph = MakeText(go.transform, placeholder, new Vector2(8, 4), new Vector2(size.x - 16, size.y - 8), 12, FontStyle.Italic, new Color(0.55f, 0.6f, 0.66f));
            ph.alignment = TextAnchor.MiddleLeft;
            input.textComponent = text;
            input.placeholder = ph;
            return input;
        }
    }
}
